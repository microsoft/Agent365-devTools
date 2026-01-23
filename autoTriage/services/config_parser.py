"""
Config Parser Service - Parses team-assistant.yml configuration files
"""
import json
from pathlib import Path
import yaml
from typing import Optional
from models.team_config import TeamConfig, PriorityRules, TriageMeta, CopilotFixableConfig
from models.ado_models import AdoConfig


def _load_team_members() -> list[dict]:
    """Load full team member data from config/team-members.json."""
    config_path = Path(__file__).parent.parent / "config" / "team-members.json"
    if config_path.exists():
        try:
            with open(config_path, 'r', encoding='utf-8') as f:
                data = json.load(f)
                return data.get("team_members", [])
        except Exception as e:
            print(f"Warning: Could not load team-members.json: {e}")
    return []


class ConfigParser:
    """Parses and validates team-assistant.yml configuration files."""

    @staticmethod
    def parse(yaml_content: str) -> TeamConfig:
        """Parse YAML content into a TeamConfig object."""
        data = yaml.safe_load(yaml_content)

        if not data:
            return ConfigParser._default_config()

        # Always load team members from team-members.json (not from YAML)
        team_members = _load_team_members()

        return TeamConfig(
            repo=data.get("repo", ""),
            owner=data.get("owner", ""),
            team_name=data.get("team_name", ""),
            standup_time=data.get("standup_time", "09:00"),
            timezone=data.get("timezone", "America/Los_Angeles"),
            priority_rules=ConfigParser._parse_priority_rules(data.get("priority_rules", {})),
            copilot_fixable=ConfigParser._parse_copilot_fixable(data.get("copilot_fixable", {})),
            triage_meta=ConfigParser._parse_triage_meta(data.get("triage_meta", {})),
            labels=data.get("labels", {}),
            team_members=team_members,
            copilot_fixable_labels=data.get("copilot_fixable_labels", []),
            features_enabled=data.get("features_enabled", {}),
            ado_config=ConfigParser._parse_ado_config(data.get("azure_devops", {}))
        )

    @staticmethod
    def parse_file(file_path: str) -> Optional[TeamConfig]:
        """Parse a YAML file into a TeamConfig object."""
        try:
            with open(file_path, 'r', encoding='utf-8') as f:
                return ConfigParser.parse(f.read())
        except Exception as e:
            print(f"Error parsing config file: {e}")
            return None

    @staticmethod
    def _parse_priority_rules(data: dict) -> PriorityRules:
        """Parse priority rules from config."""
        return PriorityRules(
            p0_keywords=data.get("p0_keywords", ["crash", "outage", "security", "data loss"]),
            p1_keywords=data.get("p1_keywords", ["bug", "broken", "error"]),
            p2_keywords=data.get("p2_keywords", ["enhancement", "feature"]),
            default_priority=data.get("default_priority", "P3")
        )

    @staticmethod
    def _parse_triage_meta(data: dict) -> TriageMeta:
        """Parse triage metadata from config."""
        return TriageMeta(
            auto_assign=data.get("auto_assign", True),
            auto_label=data.get("auto_label", True),
            copilot_enabled=data.get("copilot_enabled", False),
            copilot_max_issues_per_day=data.get("copilot_max_issues_per_day", 5)
        )

    @staticmethod
    def _parse_copilot_fixable(data: dict) -> CopilotFixableConfig:
        """Parse Copilot-fixable configuration from config."""
        return CopilotFixableConfig(
            enabled=data.get("enabled", False),
            criteria=data.get("criteria", ["typo", "simple fix", "documentation", "good first issue"]),
            max_issues_per_day=data.get("max_issues_per_day", 5)
        )

    @staticmethod
    def _parse_ado_config(data: dict) -> Optional[AdoConfig]:
        """Parse Azure DevOps configuration from config."""
        if not data or not data.get("enabled", False):
            return None

        # Validate required fields
        org = data.get("organization")
        project = data.get("project")

        if not org or not project:
            print("Warning: ADO enabled but organization or project missing")
            return None

        return AdoConfig(
            organization=org,
            project=project,
            enabled=data.get("enabled", True),
            tracked_work_item_types=data.get("tracked_types", ["User Story", "Task", "Bug", "Feature"]),
            ado_token_env=data.get("ado_token_env", "ADO_PAT_TOKEN")
        )

    @staticmethod
    def _default_config() -> TeamConfig:
        """Return default configuration."""
        # Load team members from config/team-members.json
        team_members = _load_team_members()

        return TeamConfig(
            repo="",
            owner="",
            team_name="",
            standup_time="09:00",
            timezone="America/Los_Angeles",
            priority_rules=PriorityRules(
                p0_keywords=["crash", "outage", "security", "data loss"],
                p1_keywords=["bug", "broken", "error"],
                p2_keywords=["enhancement", "feature"],
                default_priority="P3"
            ),
            copilot_fixable=CopilotFixableConfig(
                enabled=False,
                criteria=["typo", "simple fix", "documentation", "good first issue"],
                max_issues_per_day=5
            ),
            triage_meta=TriageMeta(
                auto_assign=True,
                auto_label=True,
                copilot_enabled=False,
                copilot_max_issues_per_day=5
            ),
            labels={},
            team_members=team_members,
            copilot_fixable_labels=[],
            features_enabled={},
            ado_config=None
        )

    @staticmethod
    def get_default_config() -> TeamConfig:
        """Get default configuration using team-members.json for assignees."""
        # Use the default config which loads team members from team-members.json
        return ConfigParser.parse_file("../sample-config.yml")
