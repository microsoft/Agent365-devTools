# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Teams Service - Microsoft Teams webhook integration
"""
from __future__ import annotations

import os
import json
import logging
import requests
from typing import TYPE_CHECKING

# Conditional imports for type hints only (these models don't exist in autoTriage)
if TYPE_CHECKING:
    from models.daily_digest import DailyDigestResult
    from models.weekly_plan import WeeklyPlanResult


class TeamsService:
    """Service for posting to Microsoft Teams via Incoming Webhook."""

    def __init__(self):
        self.webhook_url = os.environ.get("TEAMS_WEBHOOK_URL", "")
        logging.info(f"TeamsService initialized with webhook URL: {self.webhook_url[:60]}..." if self.webhook_url else "TeamsService: No webhook URL configured")

    def post_adaptive_card(self, card: dict) -> bool:
        """Post an Adaptive Card to Teams."""
        if not self.webhook_url:
            logging.warning("Teams webhook URL not configured - TEAMS_WEBHOOK_URL env var is empty")
            return False

        try:
            logging.info(f"Posting to Teams webhook: {self.webhook_url[:60]}...")
            logging.debug(f"Card payload: {json.dumps(card, indent=2)[:500]}...")
            
            response = requests.post(
                self.webhook_url,
                json=card,
                headers={"Content-Type": "application/json"},
                timeout=30
            )
            
            logging.info(f"Teams webhook response - Status: {response.status_code}")
            logging.info(f"Teams webhook response - Body: {response.text[:200] if response.text else '(empty)'}")
            
            # Power Automate webhooks return 200 or 202
            success = response.status_code in [200, 202]
            if success:
                logging.info("Teams message posted successfully!")
            else:
                logging.error(f"Teams webhook failed with status {response.status_code}")
            
            return success
        except Exception as e:
            logging.error(f"Error posting to Teams: {e}")
            return False

    def post_daily_digest(self, digest: DailyDigestResult) -> bool:
        """Post daily digest to Teams."""
        logging.info("Creating daily digest card for Teams...")
        card = self._create_daily_digest_card(digest)
        return self.post_adaptive_card(card)

    def post_weekly_summary(self, plan: WeeklyPlanResult) -> bool:
        """Post weekly summary to Teams."""
        logging.info("Creating weekly summary card for Teams...")
        card = self._create_weekly_summary_card(plan)
        return self.post_adaptive_card(card)

    def _create_daily_digest_card(self, digest: DailyDigestResult) -> dict:
        """Create Adaptive Card for daily digest (FR35-FR37)."""
        status_text = "🔴 STANDUP NEEDED" if digest.standup_needed else "🟢 No standup needed today"
        status_color = "attention" if digest.standup_needed else "good"

        # Build body elements
        body = [
            {
                "type": "TextBlock",
                "text": f"📋 Daily Digest - {digest.date}",
                "weight": "bolder",
                "size": "large"
            },
            {
                "type": "TextBlock",
                "text": status_text,
                "color": status_color,
                "weight": "bolder"
            },
            {
                "type": "TextBlock",
                "text": digest.standup_reason,
                "wrap": True
            },
            {
                "type": "FactSet",
                "facts": [
                    {"title": "New Issues", "value": str(digest.new_issues_count)},
                    {"title": "Updated Issues", "value": str(digest.updated_issues_count)},
                    {"title": "PRs Merged", "value": str(digest.merged_prs_count)},
                    {"title": "Open PRs", "value": str(digest.open_prs_count)},
                    {"title": "Stale PRs", "value": str(digest.stale_prs_count)},
                    {"title": "CI Failures", "value": str(digest.ci_failures_count)},
                    {"title": "Copilot Fixes", "value": str(digest.copilot_fixes_count)}
                ]
            }
        ]

        # FR36: Add decision items as agenda if standup needed
        if digest.decision_items:
            body.append({
                "type": "TextBlock",
                "text": "📌 **Discussion Agenda:**",
                "weight": "bolder",
                "spacing": "medium"
            })
            for item in digest.decision_items[:10]:  # Limit to 10 items
                body.append({
                    "type": "TextBlock",
                    "text": f"• {item}",
                    "wrap": True,
                    "spacing": "small"
                })

        # Add highlights if any
        if digest.highlights:
            body.append({
                "type": "TextBlock",
                "text": "✨ **Highlights:**",
                "weight": "bolder",
                "spacing": "medium"
            })
            for highlight in digest.highlights:
                body.append({
                    "type": "TextBlock",
                    "text": f"• {highlight}",
                    "wrap": True,
                    "spacing": "small"
                })

        # Add attention items if any
        if digest.attention_items:
            body.append({
                "type": "TextBlock",
                "text": "👀 **Watch Items:**",
                "weight": "bolder",
                "spacing": "medium"
            })
            for item in digest.attention_items[:5]:  # Limit to 5
                body.append({
                    "type": "TextBlock",
                    "text": f"• {item}",
                    "wrap": True,
                    "spacing": "small"
                })

        return {
            "type": "message",
            "attachments": [
                {
                    "contentType": "application/vnd.microsoft.card.adaptive",
                    "content": {
                        "type": "AdaptiveCard",
                        "version": "1.4",
                        "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                        "body": body
                    }
                }
            ]
        }

    def _create_weekly_summary_card(self, plan: WeeklyPlanResult) -> dict:
        """Create Adaptive Card for weekly summary."""
        if plan.meeting_needed:
            status_text = f"🔴 PLANNING MEETING RECOMMENDED ({plan.suggested_duration_minutes} min)"
        else:
            status_text = "🟢 No meeting needed - review async"

        return {
            "type": "message",
            "attachments": [
                {
                    "contentType": "application/vnd.microsoft.card.adaptive",
                    "content": {
                        "type": "AdaptiveCard",
                        "version": "1.4",
                        "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                        "body": [
                            {
                                "type": "TextBlock",
                                "text": f"📅 Weekly Planning - Week of {plan.week_of}",
                                "weight": "bolder",
                                "size": "large"
                            },
                            {
                                "type": "TextBlock",
                                "text": status_text,
                                "weight": "bolder"
                            },
                            {
                                "type": "TextBlock",
                                "text": plan.meeting_reason,
                                "wrap": True
                            },
                            {
                                "type": "FactSet",
                                "facts": [
                                    {"title": "Issues Closed", "value": str(plan.issues_closed_count)},
                                    {"title": "PRs Merged", "value": str(plan.prs_merged_count)},
                                    {"title": "Slipped Issues", "value": str(plan.slipped_issues_count)},
                                    {"title": "Decisions Required", "value": str(len(plan.decisions_required))}
                                ]
                            }
                        ]
                    }
                }
            ]
        }

    def post_intake_results(self, owner: str, repo: str, results: list, applied_changes: bool) -> bool:
        """Post intake triage results to Teams."""
        logging.info(f"Creating intake results card for Teams ({len(results)} results)...")
        card = self._create_intake_card(owner, repo, results, applied_changes)
        return self.post_adaptive_card(card)

    def _create_intake_card(self, owner: str, repo: str, results: list, applied_changes: bool) -> dict:
        """Create Adaptive Card for intake triage results."""
        processed_count = len(results)
        
        if processed_count == 0:
            status_text = "✅ No untriaged issues found"
            status_color = "good"
        else:
            status_text = f"🔍 Triaged {processed_count} issue(s)"
            status_color = "accent"

        mode_text = "✅ Changes Applied" if applied_changes else "📝 Dry Run (Preview Only)"

        # Build body elements
        body = [
            {
                "type": "TextBlock",
                "text": "🤖 Issue Intake Triage Report",
                "weight": "bolder",
                "size": "large"
            },
            {
                "type": "TextBlock",
                "text": f"Repository: {owner}/{repo}",
                "spacing": "small"
            },
            {
                "type": "TextBlock",
                "text": status_text,
                "color": status_color,
                "weight": "bolder",
                "spacing": "medium"
            },
            {
                "type": "TextBlock",
                "text": mode_text,
                "spacing": "small"
            }
        ]

        # Add summary of triaged issues
        if results:
            body.append({
                "type": "TextBlock",
                "text": "📋 **Triage Summary:**",
                "weight": "bolder",
                "spacing": "medium"
            })

            # Group by priority
            p1_count = sum(1 for r in results if r.get("issue", {}).get("priority") == "P1")
            p2_count = sum(1 for r in results if r.get("issue", {}).get("priority") == "P2")
            p3_count = sum(1 for r in results if r.get("issue", {}).get("priority") == "P3")
            copilot_count = sum(1 for r in results if r.get("issue", {}).get("is_copilot_fixable"))

            body.append({
                "type": "FactSet",
                "facts": [
                    {"title": "Total Processed", "value": str(processed_count)},
                    {"title": "P1 (Critical)", "value": str(p1_count)},
                    {"title": "P2 (High)", "value": str(p2_count)},
                    {"title": "P3 (Normal)", "value": str(p3_count)},
                    {"title": "Copilot-Fixable", "value": str(copilot_count)}
                ]
            })

            # List individual issues (limit to 10)
            body.append({
                "type": "TextBlock",
                "text": "📌 **Issues Triaged:**",
                "weight": "bolder",
                "spacing": "medium"
            })

            for result in results[:10]:
                issue = result.get("issue", {})
                issue_num = issue.get("issue_number", "?")
                priority = issue.get("priority", "?")
                issue_type = issue.get("issue_type", "unknown")
                copilot = "🤖" if issue.get("is_copilot_fixable") else ""
                applied = "✅" if result.get("applied") else "⏸️"
                
                body.append({
                    "type": "TextBlock",
                    "text": f"{applied} #{issue_num} - {priority} {issue_type} {copilot}",
                    "wrap": True,
                    "spacing": "small"
                })

            if len(results) > 10:
                body.append({
                    "type": "TextBlock",
                    "text": f"*...and {len(results) - 10} more*",
                    "isSubtle": True,
                    "spacing": "small"
                })

        return {
            "type": "message",
            "attachments": [
                {
                    "contentType": "application/vnd.microsoft.card.adaptive",
                    "content": {
                        "type": "AdaptiveCard",
                        "version": "1.4",
                        "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                        "body": body
                    }
                }
            ]
        }
