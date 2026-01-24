"""
Issue Classification Models
"""
from dataclasses import dataclass, field
from typing import Optional


@dataclass
class TriageRationale:
    """Detailed rationale for each triage decision."""
    type_rationale: str = ""  # Why this issue type was chosen
    priority_rationale: str = ""  # Why this priority was assigned
    copilot_rationale: str = ""  # Why this is/isn't suitable for Copilot
    assignment_rationale: str = ""  # Why this assignee was selected
    labels_rationale: str = ""  # Why these labels were applied

    def to_dict(self) -> dict:
        """Convert to dictionary for JSON serialization."""
        return {
            "type_rationale": self.type_rationale,
            "priority_rationale": self.priority_rationale,
            "copilot_rationale": self.copilot_rationale,
            "assignment_rationale": self.assignment_rationale,
            "labels_rationale": self.labels_rationale
        }

    def to_summary(self) -> str:
        """Combine all rationales into a human-readable summary."""
        parts = []
        if self.type_rationale:
            parts.append(f"**Type:** {self.type_rationale}")
        if self.priority_rationale:
            parts.append(f"**Priority:** {self.priority_rationale}")
        if self.copilot_rationale:
            parts.append(f"**Copilot:** {self.copilot_rationale}")
        if self.assignment_rationale:
            parts.append(f"**Assignment:** {self.assignment_rationale}")
        if self.labels_rationale:
            parts.append(f"**Labels:** {self.labels_rationale}")
        return "\n".join(parts) if parts else "No rationale provided"

    @staticmethod
    def from_dict(data: dict) -> "TriageRationale":
        """Create from dictionary."""
        return TriageRationale(
            type_rationale=data.get("type_rationale", ""),
            priority_rationale=data.get("priority_rationale", ""),
            copilot_rationale=data.get("copilot_rationale", ""),
            assignment_rationale=data.get("assignment_rationale", ""),
            labels_rationale=data.get("labels_rationale", "")
        )


@dataclass
class IssueClassification:
    """Result of LLM-based issue classification."""
    issue_url: str
    issue_number: int
    issue_type: str  # bug, feature, question, documentation, etc.
    priority: str  # P0, P1, P2, P3
    suggested_labels: list[str] = field(default_factory=list)
    suggested_assignee: Optional[str] = None
    is_copilot_fixable: bool = False
    reason: str = ""  # Legacy single reason field (for backwards compatibility)
    confidence: float = 0.0
    rationale: TriageRationale = field(default_factory=TriageRationale)
    fix_suggestions: list[str] = field(default_factory=list)  # AI-generated suggestions on how to fix

    def to_dict(self) -> dict:
        """Convert to dictionary for JSON serialization."""
        return {
            "issue_number": self.issue_number,
            "issue_url": self.issue_url,
            "issue_type": self.issue_type,
            "priority": self.priority,
            "suggested_labels": self.suggested_labels,
            "suggested_assignee": self.suggested_assignee,
            "is_copilot_fixable": self.is_copilot_fixable,
            "reason": self.reason,
            "confidence": self.confidence,
            "rationale": self.rationale.to_dict(),
            "fix_suggestions": self.fix_suggestions
        }

    @staticmethod
    def from_dict(data: dict, issue_number: int, issue_url: str = "") -> "IssueClassification":
        """Create from dictionary."""
        rationale_data = data.get("rationale", {})
        return IssueClassification(
            issue_url=issue_url or f"https://github.com/unknown/unknown/issues/{issue_number}",
            issue_number=issue_number,
            issue_type=data.get("issue_type", "unknown"),
            priority=data.get("priority", "P3"),
            suggested_labels=data.get("suggested_labels", []),
            suggested_assignee=data.get("assignee"),
            is_copilot_fixable=data.get("is_copilot_fixable", False),
            reason=data.get("reason", ""),
            confidence=data.get("confidence", 0.0),
            rationale=TriageRationale.from_dict(rationale_data) if rationale_data else TriageRationale(),
            fix_suggestions=data.get("fix_suggestions", [])
        )
