# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Daily Report Service - Generates and sends daily issue status reports
"""
import logging
from datetime import datetime, timezone
from typing import List, Dict, Any
from dataclasses import dataclass, field

from .github_service import GitHubService
from .escalation_service import EscalationService
from .teams_service import TeamsService  # Requires TEAMS_WEBHOOK_URL env var
from .config_parser import ConfigParser
# TODO: For email support, add SendGrid or Microsoft Graph API integration

logger = logging.getLogger(__name__)


@dataclass
class IssueReportItem:
    """Single issue in the report."""
    number: int
    title: str
    priority: str
    labels: List[str]
    assignee: str
    hours_open: float
    sla_hours: int
    sla_status: str  # "within", "warning", "breached"
    url: str


@dataclass
class DailyReport:
    """Daily issue status report."""
    repository: str
    generated_at: str
    total_open: int
    by_priority: Dict[str, int]
    sla_compliance_pct: float
    breached_count: int
    warning_count: int  # Within 80% of SLA
    issues: List[IssueReportItem] = field(default_factory=list)


class DailyReportService:
    """Service for generating and sending daily issue reports."""

    def __init__(self):
        self.github_service = GitHubService()
        self.escalation_service = EscalationService()
        self._teams_service = None  # Lazily instantiated
        self.team_config = ConfigParser.get_default_config()

    def get_sla_hours(self, priority: str) -> int:
        """Get SLA hours for a priority level. Delegates to EscalationService."""
        return self.escalation_service.get_sla_hours(priority)

    def calculate_sla_status(self, hours_open: float, sla_hours: int) -> str:
        """Calculate SLA status: within, warning (>80%), or breached."""
        if hours_open > sla_hours:
            return "breached"
        elif hours_open > (sla_hours * 0.8):
            return "warning"
        return "within"

    def generate_report(self, owner: str, repo: str) -> DailyReport:
        """Generate daily report for a repository."""
        logger.info(f"Generating daily report for {owner}/{repo}")
        
        # Get all open issues
        open_issues = self.github_service.get_open_issues(owner, repo)
        
        issues_list = []
        by_priority = {"P0": 0, "P1": 0, "P2": 0, "P3": 0, "P4": 0, "None": 0}
        breached_count = 0
        warning_count = 0
        
        for issue in open_issues:
            # Extract priority
            priority = None
            labels = []
            for label in issue.labels:
                labels.append(label.name)
                if label.name.upper() in ['P0', 'P1', 'P2', 'P3', 'P4']:
                    priority = label.name.upper()
            
            # Count by priority
            if priority:
                by_priority[priority] = by_priority.get(priority, 0) + 1
            else:
                by_priority["None"] = by_priority.get("None", 0) + 1
            
            # Calculate hours since last update (consistent with EscalationService)
            # Use updated_at for SLA calculation - this resets when someone responds
            reference_time = issue.updated_at or issue.created_at
            if reference_time.tzinfo is None:
                reference_time = reference_time.replace(tzinfo=timezone.utc)
            hours_open = (datetime.now(timezone.utc) - reference_time).total_seconds() / 3600
            
            # Get SLA info
            sla_hours = self.get_sla_hours(priority) if priority else 120
            sla_status = self.calculate_sla_status(hours_open, sla_hours) if priority else "within"
            
            if sla_status == "breached":
                breached_count += 1
            elif sla_status == "warning":
                warning_count += 1
            
            # Get assignee
            assignee = issue.assignees[0].login if issue.assignees else "Unassigned"
            
            issues_list.append(IssueReportItem(
                number=issue.number,
                title=issue.title[:60] + "..." if len(issue.title) > 60 else issue.title,
                priority=priority or "None",
                labels=labels[:5],  # Limit labels
                assignee=assignee,
                hours_open=round(hours_open, 1),
                sla_hours=sla_hours,
                sla_status=sla_status,
                url=issue.html_url
            ))
        
        # Sort: breached first, then by priority, then by hours open
        priority_order = {"P0": 0, "P1": 1, "P2": 2, "P3": 3, "P4": 4, "None": 5}
        issues_list.sort(key=lambda x: (
            0 if x.sla_status == "breached" else (1 if x.sla_status == "warning" else 2),
            priority_order.get(x.priority, 5),
            -x.hours_open
        ))
        
        # Calculate SLA compliance
        total_with_priority = sum(v for k, v in by_priority.items() if k != "None")
        sla_compliance = ((total_with_priority - breached_count) / total_with_priority * 100) if total_with_priority > 0 else 100
        
        return DailyReport(
            repository=f"{owner}/{repo}",
            generated_at=datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC"),
            total_open=len(open_issues),
            by_priority=by_priority,
            sla_compliance_pct=round(sla_compliance, 1),
            breached_count=breached_count,
            warning_count=warning_count,
            issues=issues_list
        )

    def create_teams_card(self, report: DailyReport) -> dict:
        """Create Adaptive Card for Teams."""
        
        # Status indicator based on SLA compliance
        if report.sla_compliance_pct >= 90:
            status_indicator = "[OK]"
            status_color = "good"
        elif report.sla_compliance_pct >= 70:
            status_indicator = "[WARNING]"
            status_color = "warning"
        else:
            status_indicator = "[CRITICAL]"
            status_color = "attention"
        
        # Build issue rows (top 15)
        issue_rows = []
        for issue in report.issues[:15]:
            sla_icon = "[CRITICAL]" if issue.sla_status == "breached" else ("[WARNING]" if issue.sla_status == "warning" else "[OK]")
            issue_rows.append({
                "type": "TableRow",
                "cells": [
                    {"type": "TableCell", "items": [{"type": "TextBlock", "text": f"[#{issue.number}]({issue.url})", "size": "small"}]},
                    {"type": "TableCell", "items": [{"type": "TextBlock", "text": issue.priority, "size": "small", "weight": "bolder"}]},
                    {"type": "TableCell", "items": [{"type": "TextBlock", "text": f"@{issue.assignee}", "size": "small"}]},
                    {"type": "TableCell", "items": [{"type": "TextBlock", "text": f"{issue.hours_open}h", "size": "small"}]},
                    {"type": "TableCell", "items": [{"type": "TextBlock", "text": sla_icon, "size": "small"}]},
                ]
            })
        
        card = {
            "type": "message",
            "attachments": [{
                "contentType": "application/vnd.microsoft.card.adaptive",
                "content": {
                    "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                    "type": "AdaptiveCard",
                    "version": "1.4",
                    "body": [
                        {
                            "type": "TextBlock",
                            "text": f"Daily Issue Report - {report.repository}",
                            "weight": "bolder",
                            "size": "large"
                        },
                        {
                            "type": "TextBlock",
                            "text": report.generated_at,
                            "size": "small",
                            "isSubtle": True
                        },
                        {
                            "type": "ColumnSet",
                            "columns": [
                                {
                                    "type": "Column",
                                    "width": "auto",
                                    "items": [
                                        {"type": "TextBlock", "text": "Total Open", "size": "small", "isSubtle": True},
                                        {"type": "TextBlock", "text": str(report.total_open), "size": "extraLarge", "weight": "bolder"}
                                    ]
                                },
                                {
                                    "type": "Column",
                                    "width": "auto",
                                    "items": [
                                        {"type": "TextBlock", "text": "SLA Compliance", "size": "small", "isSubtle": True},
                                        {"type": "TextBlock", "text": f"{status_indicator} {report.sla_compliance_pct}%", "size": "extraLarge", "weight": "bolder", "color": status_color}
                                    ]
                                },
                                {
                                    "type": "Column",
                                    "width": "auto",
                                    "items": [
                                        {"type": "TextBlock", "text": "Breached", "size": "small", "isSubtle": True},
                                        {"type": "TextBlock", "text": str(report.breached_count), "size": "extraLarge", "weight": "bolder", "color": "attention" if report.breached_count > 0 else "default"}
                                    ]
                                }
                            ]
                        },
                        {
                            "type": "FactSet",
                            "facts": [
                                {"title": "P0 (Critical)", "value": str(report.by_priority.get("P0", 0))},
                                {"title": "P1 (High)", "value": str(report.by_priority.get("P1", 0))},
                                {"title": "P2 (Medium)", "value": str(report.by_priority.get("P2", 0))},
                                {"title": "P3 (Low)", "value": str(report.by_priority.get("P3", 0))},
                                {"title": "P4 (Trivial)", "value": str(report.by_priority.get("P4", 0))},
                                {"title": "No Priority", "value": str(report.by_priority.get("None", 0))}
                            ]
                        },
                        {
                            "type": "TextBlock",
                            "text": "Top Issues (sorted by SLA urgency)",
                            "weight": "bolder",
                            "size": "medium",
                            "spacing": "large"
                        },
                        {
                            "type": "Table",
                            "columns": [
                                {"width": 1},
                                {"width": 1},
                                {"width": 2},
                                {"width": 1},
                                {"width": 1}
                            ],
                            "rows": [
                                {
                                    "type": "TableRow",
                                    "cells": [
                                        {"type": "TableCell", "items": [{"type": "TextBlock", "text": "Issue", "weight": "bolder", "size": "small"}]},
                                        {"type": "TableCell", "items": [{"type": "TextBlock", "text": "Pri", "weight": "bolder", "size": "small"}]},
                                        {"type": "TableCell", "items": [{"type": "TextBlock", "text": "Assignee", "weight": "bolder", "size": "small"}]},
                                        {"type": "TableCell", "items": [{"type": "TextBlock", "text": "Open", "weight": "bolder", "size": "small"}]},
                                        {"type": "TableCell", "items": [{"type": "TextBlock", "text": "SLA", "weight": "bolder", "size": "small"}]}
                                    ]
                                }
                            ] + issue_rows
                        }
                    ],
                    "actions": [
                        {
                            "type": "Action.OpenUrl",
                            "title": "View All Issues",
                            "url": f"https://github.com/{report.repository}/issues?q=is%3Aissue+is%3Aopen"
                        }
                    ]
                }
            }]
        }
        
        return card

    def send_to_teams(self, report: DailyReport) -> bool:
        """Send report to MS Teams."""
        # Lazy instantiation - only create TeamsService when actually needed
        if self._teams_service is None:
            self._teams_service = TeamsService()
        card = self.create_teams_card(report)
        return self._teams_service.post_adaptive_card(card)

    def run(self, owner: str, repo: str, send_teams: bool = True) -> DailyReport:
        """Generate report and optionally send to Teams."""
        report = self.generate_report(owner, repo)
        
        if send_teams:
            success = self.send_to_teams(report)
            if success:
                logger.info("Daily report sent to Teams successfully")
            else:
                logger.warning("Failed to send daily report to Teams")
        
        return report
