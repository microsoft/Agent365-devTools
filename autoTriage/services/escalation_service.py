# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Escalation Service - SLA breach detection and escalation logic
"""
import logging
from datetime import datetime, timezone
from typing import List, Dict, Any, Optional
from dataclasses import dataclass

from .github_service import GitHubService
from .config_parser import ConfigParser

logger = logging.getLogger(__name__)


@dataclass
class EscalationResult:
    """Result of an escalation check for a single issue."""
    issue_number: int
    issue_title: str
    priority: Optional[str]
    assigned_to: List[str]
    hours_open: float
    sla_hours: int
    sla_breached: bool
    escalation_action: Optional[str]  # None, "notify_lead", "reassign_lead", "notify_manager"
    escalated_to: List[str]


class EscalationService:
    """Service for detecting SLA breaches and escalating issues."""

    def __init__(self):
        self.github_service = GitHubService()
        self.team_config = ConfigParser.get_default_config()

    def get_issue_priority(self, issue) -> Optional[str]:
        """Extract priority label from issue (P0-P4)."""
        for label in issue.labels:
            label_name = label.name.upper()
            if label_name in ['P0', 'P1', 'P2', 'P3', 'P4']:
                return label_name
        return None

    def get_sla_hours(self, priority: str) -> int:
        """Get SLA hours for a priority level."""
        if not self.team_config or not self.team_config.sla_hours:
            # Default SLAs if not configured (must match team-members.json)
            defaults = {'P0': 24, 'P1': 48, 'P2': 72, 'P3': 120, 'P4': 120}
            return defaults.get(priority, 120)
        return self.team_config.sla_hours.get(priority, 120)

    def calculate_hours_open(self, issue) -> float:
        """Calculate hours since issue was last updated (or created if no updates)."""
        # Use updated_at for SLA calculation - this resets when someone responds
        reference_time = issue.updated_at or issue.created_at
        if reference_time.tzinfo is None:
            reference_time = reference_time.replace(tzinfo=timezone.utc)
        
        now = datetime.now(timezone.utc)
        delta = now - reference_time
        return delta.total_seconds() / 3600

    def get_escalation_chain(self) -> Dict[str, Any]:
        """Get escalation chain from config."""
        if not self.team_config or not self.team_config.escalation_chain:
            return {'lead': [], 'manager': None}
        return self.team_config.escalation_chain

    def check_issue_sla(self, issue) -> EscalationResult:
        """Check if a single issue has breached its SLA."""
        priority = self.get_issue_priority(issue)
        
        if not priority:
            # No priority label, can't check SLA
            return EscalationResult(
                issue_number=issue.number,
                issue_title=issue.title,
                priority=None,
                assigned_to=[a.login for a in issue.assignees],
                hours_open=self.calculate_hours_open(issue),
                sla_hours=0,
                sla_breached=False,
                escalation_action=None,
                escalated_to=[]
            )

        sla_hours = self.get_sla_hours(priority)
        hours_open = self.calculate_hours_open(issue)
        sla_breached = hours_open > sla_hours
        
        escalation_action = None
        escalated_to = []
        
        if sla_breached:
            escalation_chain = self.get_escalation_chain()
            current_assignees = [a.login for a in issue.assignees]
            leads = escalation_chain.get('lead', [])
            manager = escalation_chain.get('manager')
            
            # Determine escalation action based on current state
            lead_assigned = any(lead in current_assignees for lead in leads)
            
            if not lead_assigned and leads:
                # First escalation: notify and assign to lead
                escalation_action = "reassign_lead"
                escalated_to = leads[:1]  # Assign to first lead
            elif lead_assigned and manager:
                # Already assigned to lead, escalate to manager
                escalation_action = "notify_manager"
                escalated_to = [manager]
            else:
                # Just notify leads
                escalation_action = "notify_lead"
                escalated_to = leads

        return EscalationResult(
            issue_number=issue.number,
            issue_title=issue.title,
            priority=priority,
            assigned_to=[a.login for a in issue.assignees],
            hours_open=round(hours_open, 1),
            sla_hours=sla_hours,
            sla_breached=sla_breached,
            escalation_action=escalation_action,
            escalated_to=escalated_to
        )

    def check_all_open_issues(self, owner: str, repo: str) -> List[EscalationResult]:
        """Check all open issues for SLA breaches."""
        issues = self.github_service.get_open_issues(owner, repo)
        results = []
        
        for issue in issues:
            result = self.check_issue_sla(issue)
            if result.priority:  # Only include issues with priority labels
                results.append(result)
        
        return results

    def get_breached_issues(self, owner: str, repo: str) -> List[EscalationResult]:
        """Get only issues that have breached their SLA."""
        all_results = self.check_all_open_issues(owner, repo)
        return [r for r in all_results if r.sla_breached]

    def apply_escalation(self, owner: str, repo: str, result: EscalationResult) -> bool:
        """Apply escalation action to an issue.
        
        Returns:
            True only if all operations (assign, label, comment) succeed.
        """
        if not result.sla_breached or not result.escalation_action:
            return False

        issue_number = result.issue_number
        escalation_chain = self.get_escalation_chain()
        leads = escalation_chain.get('lead', [])
        manager = escalation_chain.get('manager')
        
        # Track success of all operations
        assign_success = True  # Default to True for actions that don't assign

        # Build escalation comment
        comment_lines = [
            "## SLA Escalation Alert",
            "",
            f"This **{result.priority}** issue has exceeded its SLA threshold.",
            "",
            f"| Metric | Value |",
            f"|--------|-------|",
            f"| Priority | {result.priority} |",
            f"| SLA Threshold | {result.sla_hours} hours |",
            f"| Time Since Update | {result.hours_open} hours |",
            f"| Current Assignees | {', '.join(['@' + a for a in result.assigned_to]) or 'None'} |",
            "",
        ]

        if result.escalation_action == "reassign_lead":
            lead_to_assign = result.escalated_to[0] if result.escalated_to else leads[0]
            comment_lines.extend([
                f"**Action:** Adding Tech Lead @{lead_to_assign} as assignee for immediate attention.",
                "",
                f"cc: @{manager}" if manager else "",
            ])
            # Add lead as assignee (note: this adds, not replaces existing assignees)
            assign_success = self.github_service.assign_issue(owner, repo, issue_number, lead_to_assign)
            
        elif result.escalation_action == "notify_manager":
            comment_lines.extend([
                f"**Action:** This issue is already assigned to a Tech Lead but remains unresolved.",
                "",
                f"Escalating to Engineering Manager @{manager} for visibility.",
                "",
                f"Tech Leads: {', '.join(['@' + l for l in leads])}",
            ])
            
        elif result.escalation_action == "notify_lead":
            comment_lines.extend([
                f"**Action:** Notifying Tech Leads for attention.",
                "",
                f"cc: {', '.join(['@' + l for l in leads])}",
            ])

        comment_lines.extend([
            "",
            "---",
            "*Generated by AutoTriage SLA Escalation*"
        ])

        comment = "\n".join(comment_lines)
        
        # Add escalation label if it doesn't exist
        label_success = self.github_service.apply_labels(owner, repo, issue_number, ["escalated"])
        
        # Post comment
        comment_success = self.github_service.add_comment(owner, repo, issue_number, comment)
        
        # Return True only if all operations succeeded
        return assign_success and label_success and comment_success

    def run_escalation_check(self, owner: str, repo: str, apply: bool = False) -> Dict[str, Any]:
        """Run full escalation check on repository.
        
        Args:
            owner: Repository owner
            repo: Repository name
            apply: If True, apply escalation actions (assign, comment)
            
        Returns:
            Dict with summary and results
        """
        logger.info(f"Running SLA escalation check for {owner}/{repo}")
        
        breached_issues = self.get_breached_issues(owner, repo)
        
        results = {
            "repository": f"{owner}/{repo}",
            "checked_at": datetime.now(timezone.utc).isoformat(),
            "total_breached": len(breached_issues),
            "apply_mode": apply,
            "issues": []
        }
        
        for result in breached_issues:
            issue_info = {
                "number": result.issue_number,
                "title": result.issue_title,
                "priority": result.priority,
                "hours_open": result.hours_open,
                "sla_hours": result.sla_hours,
                "assigned_to": result.assigned_to,
                "escalation_action": result.escalation_action,
                "escalated_to": result.escalated_to,
                "applied": False
            }
            
            if apply and result.escalation_action:
                success = self.apply_escalation(owner, repo, result)
                issue_info["applied"] = success
                if success:
                    logger.info(f"Applied escalation to issue #{result.issue_number}: {result.escalation_action}")
                else:
                    logger.warning(f"Failed to apply escalation to issue #{result.issue_number}")
            
            results["issues"].append(issue_info)
        
        return results
