"""
Team Configuration Models
"""
from dataclasses import dataclass, field
from typing import Optional
from models.ado_models import AdoConfig


@dataclass
class PriorityRules:
    """Rules for determining issue priority."""
    p0_keywords: list[str] = field(default_factory=lambda: ["crash", "outage", "security", "data loss"])
    p1_keywords: list[str] = field(default_factory=lambda: ["bug", "broken", "error"])
    p2_keywords: list[str] = field(default_factory=lambda: ["enhancement", "feature"])
    p3_keywords: list[str] = field(default_factory=lambda: ["minor", "low"])
    p4_keywords: list[str] = field(default_factory=lambda: ["trivial", "nice-to-have"])
    default_priority: str = "P3"


@dataclass
class CopilotFixableConfig:
    """Configuration for Copilot-fixable issue detection."""
    enabled: bool = False
    criteria: list[str] = field(default_factory=lambda: ["typo", "simple fix", "documentation", "good first issue"])
    max_issues_per_day: int = 5


@dataclass
class TriageMeta:
    """Triage behavior configuration."""
    auto_assign: bool = True
    auto_label: bool = True
    copilot_enabled: bool = False
    copilot_max_issues_per_day: int = 5


@dataclass
class TeamConfig:
    """Complete team configuration from team-assistant.yml."""
    repo: str
    owner: str
    team_name: str = ""
    standup_time: str = "09:00"
    timezone: str = "America/Los_Angeles"
    priority_rules: PriorityRules = field(default_factory=PriorityRules)
    copilot_fixable: CopilotFixableConfig = field(default_factory=CopilotFixableConfig)
    triage_meta: TriageMeta = field(default_factory=TriageMeta)
    labels: dict = field(default_factory=dict)
    team_members: list[dict] = field(default_factory=list)  # Full team member data with expertise
    copilot_fixable_labels: list[str] = field(default_factory=list)
    features_enabled: dict = field(default_factory=dict)
    ado_config: Optional[AdoConfig] = None  # Azure DevOps configuration

    @property
    def assignees(self) -> list[str]:
        """Get list of team member logins (derived from team_members)."""
        return [m.get("login") for m in self.team_members if m.get("login")]

    @property
    def full_repo(self) -> str:
        """Get full repository name (owner/repo)."""
        return f"{self.owner}/{self.repo}"

    def is_copilot_enabled(self) -> bool:
        """Check if Copilot coding agent is enabled."""
        return self.triage_meta.copilot_enabled
