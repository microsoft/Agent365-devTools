# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Tests for config_parser.py

Tests the ConfigParser service including:
- Loading default configuration (fixes bug where get_default_config returned None)
- Parsing YAML configuration
- Loading team members from JSON
- Priority rules parsing
"""
import pytest
import sys
from pathlib import Path
from unittest.mock import patch, mock_open

sys.path.insert(0, str(Path(__file__).parent.parent))

from services.config_parser import ConfigParser
from models.team_config import TeamConfig, PriorityRules


class TestConfigParser:
    """Test suite for ConfigParser class."""

    def test_get_default_config_returns_valid_config(self):
        """
        Test that get_default_config() returns a valid TeamConfig object.

        This test addresses the bug where get_default_config() was trying to load
        '../sample-config.yml' which doesn't exist, causing it to return None
        and triggering AttributeError when accessing config.priority_rules.

        Bug fix commit: Fix get_default_config() to return valid default config
        """
        config = ConfigParser.get_default_config()

        # Config should not be None
        assert config is not None, "get_default_config() should not return None"

        # Config should be a TeamConfig instance
        assert isinstance(config, TeamConfig), "Config should be TeamConfig instance"

        # Config should have all required attributes
        assert hasattr(config, 'priority_rules'), "Config should have priority_rules"
        assert hasattr(config, 'copilot_fixable'), "Config should have copilot_fixable"
        assert hasattr(config, 'triage_meta'), "Config should have triage_meta"
        assert hasattr(config, 'team_members'), "Config should have team_members"

    def test_get_default_config_has_valid_priority_rules(self):
        """Test that default config has valid priority rules."""
        config = ConfigParser.get_default_config()

        assert config.priority_rules is not None
        assert isinstance(config.priority_rules, PriorityRules)
        assert config.priority_rules.default_priority == "P3"
        assert "crash" in config.priority_rules.p0_keywords
        assert "bug" in config.priority_rules.p1_keywords
        assert "enhancement" in config.priority_rules.p2_keywords

    def test_get_default_config_loads_team_members(self):
        """Test that default config loads team members from team-members.json."""
        config = ConfigParser.get_default_config()

        assert config.team_members is not None
        assert isinstance(config.team_members, list)
        # Should have team members if file exists
        # Note: This test will pass even if the file doesn't exist (returns empty list)
        assert isinstance(config.team_members, list)

    def test_get_default_config_has_triage_meta(self):
        """Test that default config has valid triage metadata."""
        config = ConfigParser.get_default_config()

        assert config.triage_meta is not None
        assert config.triage_meta.auto_assign is True
        assert config.triage_meta.auto_label is True

    def test_get_default_config_has_copilot_fixable(self):
        """Test that default config has copilot fixable configuration."""
        config = ConfigParser.get_default_config()

        assert config.copilot_fixable is not None
        assert isinstance(config.copilot_fixable.criteria, list)
        assert config.copilot_fixable.max_issues_per_day > 0

    def test_parse_priority_rules_with_custom_keywords(self):
        """Test parsing custom priority rules."""
        custom_data = {
            "p0_keywords": ["critical", "urgent"],
            "p1_keywords": ["important"],
            "default_priority": "P2"
        }

        rules = ConfigParser._parse_priority_rules(custom_data)

        assert "critical" in rules.p0_keywords
        assert "urgent" in rules.p0_keywords
        assert "important" in rules.p1_keywords
        assert rules.default_priority == "P2"

    def test_parse_priority_rules_with_empty_data(self):
        """Test parsing priority rules with empty data uses defaults."""
        rules = ConfigParser._parse_priority_rules({})

        assert rules.default_priority == "P3"
        assert "crash" in rules.p0_keywords
        assert "bug" in rules.p1_keywords

    def test_parse_triage_meta_with_custom_values(self):
        """Test parsing triage metadata with custom values."""
        custom_data = {
            "auto_assign": False,
            "auto_label": False,
            "copilot_enabled": True,
            "copilot_max_issues_per_day": 10
        }

        meta = ConfigParser._parse_triage_meta(custom_data)

        assert meta.auto_assign is False
        assert meta.auto_label is False
        assert meta.copilot_enabled is True
        assert meta.copilot_max_issues_per_day == 10

    def test_parse_copilot_fixable_with_custom_criteria(self):
        """Test parsing copilot fixable configuration."""
        custom_data = {
            "enabled": True,
            "criteria": ["typo", "formatting"],
            "max_issues_per_day": 3
        }

        copilot_config = ConfigParser._parse_copilot_fixable(custom_data)

        assert copilot_config.enabled is True
        assert "typo" in copilot_config.criteria
        assert "formatting" in copilot_config.criteria
        assert copilot_config.max_issues_per_day == 3

    def test_parse_valid_yaml_content(self):
        """Test parsing valid YAML configuration content."""
        yaml_content = """
repo: "test-repo"
owner: "test-owner"
team_name: "Test Team"
timezone: "UTC"
priority_rules:
  default_priority: "P2"
triage_meta:
  auto_assign: true
  auto_label: true
"""

        config = ConfigParser.parse(yaml_content)

        assert config is not None
        assert config.repo == "test-repo"
        assert config.owner == "test-owner"
        assert config.team_name == "Test Team"
        assert config.timezone == "UTC"
        assert config.priority_rules.default_priority == "P2"

    def test_parse_empty_yaml_returns_default_config(self):
        """Test that parsing empty YAML returns default configuration."""
        config = ConfigParser.parse("")

        assert config is not None
        assert isinstance(config, TeamConfig)
        assert config.priority_rules is not None
        assert config.triage_meta is not None

    def test_parse_file_with_nonexistent_file_returns_none(self):
        """Test that parse_file returns None for non-existent files."""
        config = ConfigParser.parse_file("/nonexistent/path/config.yml")

        assert config is None

    def test_default_config_method_returns_valid_config(self):
        """Test the internal _default_config method."""
        config = ConfigParser._default_config()

        assert config is not None
        assert isinstance(config, TeamConfig)
        assert config.priority_rules is not None
        assert config.priority_rules.default_priority == "P3"
        assert config.triage_meta is not None
        assert config.copilot_fixable is not None

    def test_parse_ado_config_when_disabled(self):
        """Test parsing ADO config when disabled."""
        ado_data = {"enabled": False}

        ado_config = ConfigParser._parse_ado_config(ado_data)

        assert ado_config is None

    def test_parse_ado_config_when_enabled_without_required_fields(self):
        """Test parsing ADO config with missing required fields."""
        ado_data = {"enabled": True}  # Missing organization and project

        ado_config = ConfigParser._parse_ado_config(ado_data)

        assert ado_config is None

    def test_parse_ado_config_with_valid_data(self):
        """Test parsing ADO config with valid data."""
        ado_data = {
            "enabled": True,
            "organization": "test-org",
            "project": "test-project",
            "tracked_types": ["Bug", "Task"]
        }

        ado_config = ConfigParser._parse_ado_config(ado_data)

        assert ado_config is not None
        assert ado_config.enabled is True
        assert ado_config.organization == "test-org"
        assert ado_config.project == "test-project"
        assert "Bug" in ado_config.tracked_work_item_types
        assert "Task" in ado_config.tracked_work_item_types
