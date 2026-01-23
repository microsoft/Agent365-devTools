"""
Daily Digest Models
"""
from dataclasses import dataclass, field
from datetime import date
from typing import Any


@dataclass
class DailyDigestResult:
    """Result of daily digest generation."""
    date: str
    standup_needed: bool
    standup_reason: str
    new_issues_count: int = 0
    updated_issues_count: int = 0
    merged_prs_count: int = 0
    open_prs_count: int = 0
    stale_prs_count: int = 0
    ci_failures_count: int = 0
    copilot_fixes_count: int = 0
    decision_items: list[str] = field(default_factory=list)
    teams_message_sent: bool = False
    new_issues: list[dict] = field(default_factory=list)
    merged_prs: list[dict] = field(default_factory=list)
    open_prs: list[dict] = field(default_factory=list)
    stale_prs: list[dict] = field(default_factory=list)
    unassigned_issues: list[dict] = field(default_factory=list)
    highlights: list[str] = field(default_factory=list)
    attention_items: list[str] = field(default_factory=list)
    # Report time range
    report_start_time: str = ""
    report_end_time: str = ""
    # AI-generated content
    ai_summary: str = ""
    ai_tone: str = "normal"  # quiet, normal, busy, urgent
    ai_priorities: list[str] = field(default_factory=list)

    def to_dict(self) -> dict:
        """Convert to dictionary for JSON serialization."""
        return {
            "date": self.date,
            "report_period": {
                "start_time": self.report_start_time,
                "end_time": self.report_end_time
            },
            "standup_needed": self.standup_needed,
            "standup_reason": self.standup_reason,
            "teams_message_sent": self.teams_message_sent,
            "summary": {
                "new_issues_count": self.new_issues_count,
                "updated_issues_count": self.updated_issues_count,
                "merged_prs_count": self.merged_prs_count,
                "open_prs_count": self.open_prs_count,
                "stale_prs_count": self.stale_prs_count,
                "ci_failures_count": self.ci_failures_count,
                "copilot_fixes_count": self.copilot_fixes_count,
                "unassigned_issues_count": len(self.unassigned_issues)
            },
            "details": {
                "new_issues": self.new_issues,
                "merged_prs": self.merged_prs,
                "open_prs": self.open_prs,
                "stale_prs": self.stale_prs,
                "unassigned_issues": self.unassigned_issues
            },
            "decision_items": self.decision_items,
            "highlights": self.highlights,
            "attention_items": self.attention_items,
            "ai_analysis": {
                "summary": self.ai_summary,
                "tone": self.ai_tone,
                "top_priorities": self.ai_priorities
            }
        }

    @staticmethod
    def empty(reason: str = "No data available") -> "DailyDigestResult":
        """Create an empty digest result."""
        return DailyDigestResult(
            date=date.today().isoformat(),
            standup_needed=False,
            standup_reason=reason
        )
