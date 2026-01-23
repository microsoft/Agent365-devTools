"""
Weekly Planning Models
"""
from dataclasses import dataclass, field
from datetime import date
from typing import Any, Optional


@dataclass
class EngineerMetrics:
    """Metrics for an individual engineer."""
    github_handle: str
    issues_closed: int = 0
    prs_merged: int = 0
    lines_added: int = 0
    lines_removed: int = 0
    ado_items_closed: int = 0
    ado_items_open: int = 0
    ado_story_points: float = 0.0


@dataclass
class FileHeatmap:
    """File activity heatmap entry."""
    file_path: str
    changes_count: int = 0
    contributors: list[str] = field(default_factory=list)


@dataclass
class WeeklyPlanResult:
    """Result of weekly planning analysis."""
    week_of: str
    meeting_needed: bool
    meeting_reason: str
    suggested_duration_minutes: int = 0
    issues_closed_count: int = 0
    prs_merged_count: int = 0
    slipped_issues_count: int = 0
    decisions_required: list[str] = field(default_factory=list)
    closed_issues: list[dict] = field(default_factory=list)
    merged_prs: list[dict] = field(default_factory=list)
    slipped_issues: list[dict] = field(default_factory=list)
    next_week_focus: list[str] = field(default_factory=list)
    engineer_summaries: list[EngineerMetrics] = field(default_factory=list)
    file_heatmap: list[FileHeatmap] = field(default_factory=list)
    markdown_content: str = ""
    markdown_file_path: Optional[str] = None
    teams_message_sent: bool = False
    ai_analysis: Optional[dict] = None  # LLM-generated insights
    # ADO Integration fields
    ado_work_items_closed: list[dict] = field(default_factory=list)
    ado_work_items_open: list[dict] = field(default_factory=list)
    ado_stats: dict = field(default_factory=dict)  # closed_count, open_count, story_points

    def to_dict(self) -> dict:
        """Convert to dictionary for JSON serialization."""
        return {
            "week_of": self.week_of,
            "meeting_recommendation": {
                "needed": self.meeting_needed,
                "reason": self.meeting_reason,
                "suggested_duration_minutes": self.suggested_duration_minutes,
                "decision_count": len(self.decisions_required)
            },
            "summary": {
                "issues_closed_count": self.issues_closed_count,
                "prs_merged_count": self.prs_merged_count,
                "slipped_issues_count": self.slipped_issues_count,
                "ado_closed_count": self.ado_stats.get("closed_count", 0),
                "ado_open_count": self.ado_stats.get("open_count", 0),
                "ado_story_points": self.ado_stats.get("total_story_points", 0)
            },
            "details": {
                "closed_issues": self.closed_issues,
                "merged_prs": self.merged_prs,
                "slipped_issues": self.slipped_issues,
                "ado_work_items_closed": self.ado_work_items_closed,
                "ado_work_items_open": self.ado_work_items_open
            },
            "engineer_summaries": [
                {
                    "engineer": e.github_handle,
                    "issues_closed": e.issues_closed,
                    "prs_merged": e.prs_merged,
                    "slipped_issues": 0,
                    "ado_items_closed": e.ado_items_closed,
                    "ado_items_open": e.ado_items_open,
                    "ado_story_points": e.ado_story_points
                }
                for e in self.engineer_summaries
            ],
            "decisions_required": self.decisions_required,
            "next_week_focus": self.next_week_focus,
            "ai_analysis": self.ai_analysis,
            "markdown_content": self.markdown_content,
            "markdown_file_path": self.markdown_file_path,
            "teams_message_sent": self.teams_message_sent
        }

    @staticmethod
    def empty(reason: str = "No data available") -> "WeeklyPlanResult":
        """Create an empty weekly plan result."""
        return WeeklyPlanResult(
            week_of=date.today().isoformat(),
            meeting_needed=False,
            meeting_reason=reason,
            suggested_duration_minutes=0
        )
