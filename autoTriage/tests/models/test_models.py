# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Tests for autoTriage data models.

Tests the dataclasses in models/:
- team_config.py (PriorityRules, SecurityConfig, TeamConfig, etc.)
- issue_classification.py (IssueClassification, TriageRationale)
- ado_models.py (AdoWorkItem, AdoConfig)
"""
import pytest
import sys
from pathlib import Path
from datetime import datetime, timezone

sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from models.team_config import (
    PriorityRules,
    CopilotFixableConfig,
    TriageMeta,
    SecurityConfig,
    TeamConfig
)
from models.issue_classification import IssueClassification, TriageRationale
from models.ado_models import AdoWorkItem, AdoConfig


class TestPriorityRules:
    """Test PriorityRules dataclass."""

    def test_default_priority_rules(self):
        """Test PriorityRules with default values."""
        rules = PriorityRules()
        
        assert "crash" in rules.p0_keywords
        assert "security" in rules.p0_keywords
        assert "bug" in rules.p1_keywords
        assert "enhancement" in rules.p2_keywords
        assert rules.default_priority == "P3"

    def test_custom_priority_rules(self):
        """Test PriorityRules with custom values."""
        rules = PriorityRules(
            p0_keywords=["critical", "emergency"],
            p1_keywords=["high"],
            default_priority="P2"
        )
        
        assert "critical" in rules.p0_keywords
        assert "crash" not in rules.p0_keywords  # Custom replaces default
        assert rules.default_priority == "P2"


class TestSecurityConfig:
    """Test SecurityConfig dataclass."""

    def test_default_security_config(self):
        """Test SecurityConfig with default values."""
        config = SecurityConfig()
        
        assert "vulnerability" in config.keywords
        assert "CVE" in config.keywords
        assert "XSS" in config.keywords
        assert config.default_priority == "P1"
        assert config.assignee == ""

    def test_custom_security_config(self):
        """Test SecurityConfig with custom values."""
        config = SecurityConfig(
            keywords=["breach", "leak", "exploit"],
            assignee="security-team-lead",
            default_priority="P0"
        )
        
        assert "breach" in config.keywords
        assert config.assignee == "security-team-lead"
        assert config.default_priority == "P0"


class TestCopilotFixableConfig:
    """Test CopilotFixableConfig dataclass."""

    def test_default_copilot_config(self):
        """Test CopilotFixableConfig with default values."""
        config = CopilotFixableConfig()
        
        assert config.enabled is False
        assert "typo" in config.criteria
        assert "documentation" in config.criteria
        assert config.max_issues_per_day == 5

    def test_enabled_copilot_config(self):
        """Test CopilotFixableConfig when enabled."""
        config = CopilotFixableConfig(
            enabled=True,
            criteria=["simple bug", "one-liner"],
            max_issues_per_day=10
        )
        
        assert config.enabled is True
        assert config.max_issues_per_day == 10


class TestTriageMeta:
    """Test TriageMeta dataclass."""

    def test_default_triage_meta(self):
        """Test TriageMeta with default values."""
        meta = TriageMeta()
        
        assert meta.auto_assign is True
        assert meta.auto_label is True
        assert meta.copilot_enabled is False

    def test_custom_triage_meta(self):
        """Test TriageMeta with custom values."""
        meta = TriageMeta(
            auto_assign=False,
            copilot_enabled=True,
            copilot_max_issues_per_day=3
        )
        
        assert meta.auto_assign is False
        assert meta.copilot_enabled is True
        assert meta.copilot_max_issues_per_day == 3


class TestTeamConfig:
    """Test TeamConfig dataclass."""

    def test_minimal_team_config(self):
        """Test TeamConfig with minimal required fields."""
        config = TeamConfig(repo="test-repo", owner="test-owner")
        
        assert config.repo == "test-repo"
        assert config.owner == "test-owner"
        assert config.full_repo == "test-owner/test-repo"
        assert config.assignees == []

    def test_team_config_with_members(self):
        """Test TeamConfig assignees property."""
        config = TeamConfig(
            repo="repo",
            owner="owner",
            team_members=[
                {"login": "user1", "expertise": ["python"]},
                {"login": "user2", "expertise": ["javascript"]},
                {"name": "No Login User"}  # No login field
            ]
        )
        
        assert config.assignees == ["user1", "user2"]
        assert len(config.assignees) == 2

    def test_team_config_copilot_enabled(self):
        """Test is_copilot_enabled method."""
        config = TeamConfig(repo="repo", owner="owner")
        assert config.is_copilot_enabled() is False
        
        config.triage_meta.copilot_enabled = True
        assert config.is_copilot_enabled() is True

    def test_team_config_with_sla_and_escalation(self):
        """Test TeamConfig with SLA and escalation chain."""
        config = TeamConfig(
            repo="repo",
            owner="owner",
            sla_hours={"P0": 24, "P1": 48, "P2": 72},
            escalation_chain={"lead": ["lead1", "lead2"], "manager": "manager1"}
        )
        
        assert config.sla_hours["P0"] == 24
        assert config.escalation_chain["manager"] == "manager1"


class TestTriageRationale:
    """Test TriageRationale dataclass."""

    def test_empty_rationale(self):
        """Test TriageRationale with empty values."""
        rationale = TriageRationale()
        
        assert rationale.type_rationale == ""
        assert rationale.to_summary() == "No rationale provided"

    def test_rationale_to_dict(self):
        """Test TriageRationale to_dict method."""
        rationale = TriageRationale(
            type_rationale="This is a bug report",
            priority_rationale="Affects production"
        )
        
        d = rationale.to_dict()
        assert d["type_rationale"] == "This is a bug report"
        assert d["priority_rationale"] == "Affects production"

    def test_rationale_from_dict(self):
        """Test TriageRationale from_dict method."""
        data = {
            "type_rationale": "Feature request",
            "priority_rationale": "Nice to have"
        }
        
        rationale = TriageRationale.from_dict(data)
        assert rationale.type_rationale == "Feature request"

    def test_rationale_to_summary(self):
        """Test TriageRationale to_summary method."""
        rationale = TriageRationale(
            type_rationale="Bug",
            priority_rationale="High impact"
        )
        
        summary = rationale.to_summary()
        assert "**Type:** Bug" in summary
        assert "**Priority:** High impact" in summary


class TestIssueClassification:
    """Test IssueClassification dataclass."""

    def test_issue_classification_creation(self):
        """Test IssueClassification creation."""
        classification = IssueClassification(
            issue_url="https://github.com/owner/repo/issues/123",
            issue_number=123,
            issue_type="bug",
            priority="P1",
            suggested_labels=["bug", "priority-1"],
            suggested_assignee="developer1",
            confidence=0.85
        )
        
        assert classification.issue_number == 123
        assert classification.priority == "P1"
        assert classification.confidence == 0.85
        assert "bug" in classification.suggested_labels

    def test_issue_classification_to_dict(self):
        """Test IssueClassification to_dict method."""
        classification = IssueClassification(
            issue_url="https://github.com/owner/repo/issues/1",
            issue_number=1,
            issue_type="feature",
            priority="P2"
        )
        
        d = classification.to_dict()
        assert d["issue_number"] == 1
        assert d["issue_type"] == "feature"
        assert d["priority"] == "P2"

    def test_issue_classification_copilot_fixable(self):
        """Test IssueClassification with Copilot fixable flag."""
        classification = IssueClassification(
            issue_url="https://github.com/owner/repo/issues/1",
            issue_number=1,
            issue_type="documentation",
            priority="P4",
            is_copilot_fixable=True
        )
        
        assert classification.is_copilot_fixable is True


class TestAdoConfig:
    """Test AdoConfig dataclass."""

    def test_ado_config_creation(self):
        """Test AdoConfig creation."""
        config = AdoConfig(
            organization="my-org",
            project="my-project"
        )
        
        assert config.organization == "my-org"
        assert config.project == "my-project"
        assert config.enabled is True
        assert "Bug" in config.tracked_work_item_types

    def test_ado_config_validation_missing_org(self):
        """Test AdoConfig raises error without organization."""
        with pytest.raises(ValueError, match="organization is required"):
            AdoConfig(organization="", project="project")

    def test_ado_config_validation_missing_project(self):
        """Test AdoConfig raises error without project."""
        with pytest.raises(ValueError, match="project is required"):
            AdoConfig(organization="org", project="")


class TestAdoWorkItem:
    """Test AdoWorkItem dataclass."""

    def test_ado_work_item_creation(self):
        """Test AdoWorkItem creation."""
        now = datetime.now(timezone.utc)
        work_item = AdoWorkItem(
            id=12345,
            title="Fix login bug",
            work_item_type="Bug",
            state="Active",
            assigned_to="developer@example.com",
            created_date=now,
            changed_date=now,
            closed_date=None,
            url="https://dev.azure.com/org/project/_workitems/edit/12345",
            priority=2,
            tags=["security", "login"],
            area_path="Project\\Area",
            iteration_path="Project\\Sprint1",
            parent_id=None,
            story_points=3.0
        )
        
        assert work_item.id == 12345
        assert work_item.work_item_type == "Bug"
        assert work_item.source == "ado"  # Always set to "ado"
        assert "security" in work_item.tags

    def test_ado_work_item_source_always_ado(self):
        """Test AdoWorkItem source is always 'ado' even if set differently."""
        now = datetime.now(timezone.utc)
        work_item = AdoWorkItem(
            id=1,
            title="Test",
            work_item_type="Task",
            state="New",
            assigned_to=None,
            created_date=now,
            changed_date=now,
            closed_date=None,
            url="https://example.com",
            priority=None,
            tags=[],
            area_path="",
            iteration_path="",
            parent_id=None,
            story_points=None,
            source="github"  # Try to set different source
        )
        
        # __post_init__ should force it to "ado"
        assert work_item.source == "ado"
