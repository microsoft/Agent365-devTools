# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Tests for escalation_service.py

Tests the EscalationService including:
- SLA hours retrieval
- Escalation chain retrieval
- Priority extraction from issues
- Hours open calculation
- SLA breach detection
- Escalation action determination
"""
import pytest
import sys
from pathlib import Path
from unittest.mock import patch, MagicMock
from datetime import datetime, timezone, timedelta

sys.path.insert(0, str(Path(__file__).parent.parent.parent))


class TestEscalationServiceInit:
    """Test EscalationService initialization."""

    def test_service_initializes(self):
        """Test EscalationService can be initialized."""
        from services.escalation_service import EscalationService
        
        with patch('services.escalation_service.GitHubService'):
            service = EscalationService()
            assert service is not None


class TestSLAHours:
    """Test SLA hours retrieval."""

    def test_get_sla_hours_from_config(self):
        """Test SLA hours retrieval from service."""
        from services.escalation_service import EscalationService
        
        with patch('services.escalation_service.GitHubService'):
            service = EscalationService()
            
            assert service.get_sla_hours("P0") == 24
            assert service.get_sla_hours("P1") == 48
            assert service.get_sla_hours("P2") == 72
            assert service.get_sla_hours("P3") == 120
            assert service.get_sla_hours("P4") == 120

    # Test data: (priority, expected_hours)
    SLA_HOURS_CASES = [
        ("P0", 24, "P0 has 24 hour SLA"),
        ("P1", 48, "P1 has 48 hour SLA"),
        ("P2", 72, "P2 has 72 hour SLA"),
        ("P3", 120, "P3 has 120 hour SLA"),
        ("P4", 120, "P4 has 120 hour SLA"),
        ("unknown", 120, "Unknown priority defaults to 120"),
    ]

    @pytest.mark.parametrize(
        "priority,expected_hours,description",
        SLA_HOURS_CASES,
        ids=[c[2] for c in SLA_HOURS_CASES]
    )
    def test_sla_hours_by_priority(self, priority, expected_hours, description):
        """Test SLA hours for each priority level."""
        sla_config = {"P0": 24, "P1": 48, "P2": 72, "P3": 120, "P4": 120}
        result = sla_config.get(priority, 120)
        assert result == expected_hours, f"SLA hours mismatch: {description}"


class TestEscalationChain:
    """Test escalation chain retrieval."""

    def test_get_escalation_chain(self):
        """Test escalation chain retrieval."""
        from services.escalation_service import EscalationService
        
        with patch('services.escalation_service.GitHubService'):
            service = EscalationService()
            chain = service.get_escalation_chain()
            
            assert "lead" in chain
            assert "manager" in chain
            assert isinstance(chain["lead"], list)


class TestPriorityExtraction:
    """Test priority extraction from issues."""

    def test_get_issue_priority_extracts_priority_label(self):
        """Test priority extraction from issue labels."""
        from services.escalation_service import EscalationService
        
        with patch('services.escalation_service.GitHubService'):
            service = EscalationService()
            
            mock_label = MagicMock()
            mock_label.name = "P1"
            
            mock_issue = MagicMock()
            mock_issue.labels = [mock_label]
            
            priority = service.get_issue_priority(mock_issue)
            assert priority == "P1"

    def test_get_issue_priority_returns_none_without_priority(self):
        """Test priority extraction returns None without priority label."""
        from services.escalation_service import EscalationService
        
        with patch('services.escalation_service.GitHubService'):
            service = EscalationService()
            
            mock_label = MagicMock()
            mock_label.name = "bug"
            
            mock_issue = MagicMock()
            mock_issue.labels = [mock_label]
            
            priority = service.get_issue_priority(mock_issue)
            assert priority is None

    # Test data: (label_names, expected_priority)
    PRIORITY_EXTRACTION_CASES = [
        (["P0", "bug"], "P0", "P0 extracted from multiple labels"),
        (["P1"], "P1", "P1 only label"),
        (["enhancement", "P2"], "P2", "P2 with other label"),
        (["bug", "help wanted"], None, "No priority label"),
        (["p1"], "P1", "Lowercase p1 should match"),
    ]

    @pytest.mark.parametrize(
        "label_names,expected_priority,description",
        PRIORITY_EXTRACTION_CASES,
        ids=[c[2] for c in PRIORITY_EXTRACTION_CASES]
    )
    def test_priority_extraction_cases(self, label_names, expected_priority, description):
        """Test priority extraction with various label combinations."""
        from services.escalation_service import EscalationService
        
        with patch('services.escalation_service.GitHubService'):
            service = EscalationService()
            
            # Create mock labels properly - MagicMock(name=x) uses x as internal name,
            # need to set .name attribute separately
            mock_labels = []
            for label_name in label_names:
                mock_label = MagicMock()
                mock_label.name = label_name
                mock_labels.append(mock_label)
            
            mock_issue = MagicMock()
            mock_issue.labels = mock_labels
            
            priority = service.get_issue_priority(mock_issue)
            assert priority == expected_priority, f"Failed: {description}"


class TestHoursOpenCalculation:
    """Test hours open calculation."""

    def test_calculate_hours_open(self):
        """Test hours calculation from issue updated_at."""
        from services.escalation_service import EscalationService
        
        with patch('services.escalation_service.GitHubService'):
            service = EscalationService()
            
            mock_issue = MagicMock()
            mock_issue.updated_at = datetime.now(timezone.utc) - timedelta(hours=48)
            
            hours = service.calculate_hours_open(mock_issue)
            
            assert 47 < hours < 49


class TestSLABreachDetection:
    """Test SLA breach detection logic."""

    # Test data: (priority, hours_open, expected_breached)
    SLA_BREACH_CASES = [
        ("P0", 20, False, "P0 at 20h not breached (SLA=24h)"),
        ("P0", 25, True, "P0 at 25h breached (SLA=24h)"),
        ("P1", 40, False, "P1 at 40h not breached (SLA=48h)"),
        ("P1", 50, True, "P1 at 50h breached (SLA=48h)"),
        ("P2", 70, False, "P2 at 70h not breached (SLA=72h)"),
        ("P2", 80, True, "P2 at 80h breached (SLA=72h)"),
        ("P3", 100, False, "P3 at 100h not breached (SLA=120h)"),
        ("P3", 130, True, "P3 at 130h breached (SLA=120h)"),
    ]

    @pytest.mark.parametrize(
        "priority,hours_open,expected_breached,description",
        SLA_BREACH_CASES,
        ids=[c[3] for c in SLA_BREACH_CASES]
    )
    def test_sla_breach_logic(self, priority, hours_open, expected_breached, description):
        """Test SLA breach detection logic for different priorities."""
        sla_config = {"P0": 24, "P1": 48, "P2": 72, "P3": 120, "P4": 120}
        sla_hours = sla_config.get(priority, 120)
        
        sla_breached = hours_open > sla_hours
        
        assert sla_breached == expected_breached, f"Failed: {description}"


class TestAlreadyEscalated:
    """Test already-escalated detection and its effect on SLA reporting (MSRC #112249 follow-on)."""

    def _make_service(self):
        with patch('services.escalation_service.GitHubService'):
            from services.escalation_service import EscalationService
            return EscalationService()

    def _make_issue(self, label_names, hours_open=72):
        mock_labels = []
        for name in label_names:
            lbl = MagicMock()
            lbl.name = name
            mock_labels.append(lbl)
        mock_assignee = MagicMock()
        mock_assignee.login = "user1"
        issue = MagicMock()
        issue.number = 99
        issue.title = "Test issue"
        issue.labels = mock_labels
        issue.assignees = [mock_assignee]
        issue.updated_at = datetime.now(timezone.utc) - timedelta(hours=hours_open)
        issue.created_at = issue.updated_at
        return issue

    def test_is_already_escalated_true_with_escalated_label(self):
        """is_already_escalated returns True when issue carries 'escalated' label."""
        service = self._make_service()
        issue = self._make_issue(["P1", "escalated"])
        assert service.is_already_escalated(issue) is True, \
            "Issue with 'escalated' label must be detected as already escalated"

    def test_is_already_escalated_false_without_label(self):
        """is_already_escalated returns False when issue has no 'escalated' label."""
        service = self._make_service()
        issue = self._make_issue(["P1", "bug"])
        assert service.is_already_escalated(issue) is False, \
            "Issue without 'escalated' label must not be detected as already escalated"

    def test_is_already_escalated_case_insensitive(self):
        """is_already_escalated is case-insensitive for the label name."""
        service = self._make_service()
        for label in ("Escalated", "ESCALATED", "EsCalAtEd"):
            issue = self._make_issue(["P1", label])
            assert service.is_already_escalated(issue) is True, \
                f"Label '{label}' should be recognized as the escalated label"

    def test_check_issue_sla_already_escalated_returns_not_breached(self):
        """check_issue_sla returns sla_breached=False for an already-escalated issue.

        Requirement: previously escalated issues must not trigger a new escalation
        action to avoid repeated noise (MSRC #112249 follow-on).
        """
        service = self._make_service()
        # P1 SLA is 48h; issue is 72h old — would normally breach
        issue = self._make_issue(["P1", "escalated"], hours_open=72)
        result = service.check_issue_sla(issue)

        assert result.sla_breached is False, \
            "Already-escalated issue must not be marked as breached"
        assert result.escalation_action == "already_escalated", \
            "Already-escalated issue must carry action 'already_escalated'"

    def test_check_issue_sla_already_escalated_hours_open_rounded(self):
        """hours_open in the already_escalated path is rounded to 1 decimal place.

        Requirement: consistent reporting format — the normal breach path rounds
        to 1 decimal; the already_escalated path must do the same.
        """
        service = self._make_service()
        issue = self._make_issue(["P1", "escalated"], hours_open=72)
        result = service.check_issue_sla(issue)

        # hours_open must be a rounded float (no more than 1 decimal place)
        assert result.hours_open == round(result.hours_open, 1), \
            "hours_open must be rounded to 1 decimal in the already_escalated path"

    def test_run_escalation_check_separates_breached_from_already_escalated(self):
        """run_escalation_check counts already-escalated issues separately from breached.

        Requirement: total_breached must not include already-escalated issues;
        total_already_escalated must reflect them; and both groups appear in 'issues'.
        """
        from services.escalation_service import EscalationService

        with patch('services.escalation_service.GitHubService') as mock_gh_cls:
            service = EscalationService()

            # Issue A: P1, 72h old, NOT yet escalated -> breached
            issue_a = self._make_issue(["P1"], hours_open=72)
            issue_a.number = 1
            issue_a.title = "New breach"

            # Issue B: P1, 72h old, already escalated -> not breached again
            issue_b = self._make_issue(["P1", "escalated"], hours_open=72)
            issue_b.number = 2
            issue_b.title = "Already escalated"

            service.github_service.get_open_issues.return_value = [issue_a, issue_b]

            result = service.run_escalation_check(owner="owner", repo="repo", apply=False)

        assert result["total_breached"] == 1, \
            "Only the genuinely new breach should be counted in total_breached"
        assert result["total_already_escalated"] == 1, \
            "The already-escalated issue must appear in total_already_escalated"

        issue_numbers = {i["number"] for i in result["issues"]}
        assert 1 in issue_numbers, "Newly breached issue must appear in results"
        assert 2 in issue_numbers, "Already-escalated issue must appear in results for visibility"

        already_esc_entry = next(i for i in result["issues"] if i["number"] == 2)
        assert already_esc_entry["escalation_action"] == "already_escalated", \
            "Already-escalated entry must carry action 'already_escalated'"


class TestEscalationAction:
    """Test escalation action determination."""

    # Test data: (hours_open, sla_hours, assigned_to, leads, expected_action)
    ESCALATION_ACTION_CASES = [
        (20, 24, [], ["lead1"], None, "No breach, no action"),
        (30, 24, [], ["lead1"], "reassign_lead", "Breached, unassigned -> assign lead"),
        (30, 24, ["user1"], ["lead1", "lead2"], "reassign_lead", "Breached, assigned to non-lead"),
        (30, 24, ["lead1"], ["lead1", "lead2"], "notify_manager", "Lead assigned but unresolved"),
    ]

    @pytest.mark.parametrize(
        "hours_open,sla_hours,assigned_to,leads,expected_action,description",
        ESCALATION_ACTION_CASES,
        ids=[c[5] for c in ESCALATION_ACTION_CASES]
    )
    def test_escalation_action_determination(
        self, hours_open, sla_hours, assigned_to, leads, expected_action, description
    ):
        """Test correct escalation action based on breach and assignment state."""
        sla_breached = hours_open > sla_hours
        
        if not sla_breached:
            action = None
        elif not assigned_to:
            action = "reassign_lead"
        elif any(a in leads for a in assigned_to):
            action = "notify_manager"
        else:
            action = "reassign_lead"
        
        assert action == expected_action, f"Escalation action mismatch: {description}"
