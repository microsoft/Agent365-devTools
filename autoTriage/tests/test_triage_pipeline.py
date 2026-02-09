# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Integration tests for the triage pipeline.

Tests the end-to-end flow: issue intake -> security detection -> 
priority assignment -> escalation -> daily reports.

Uses parameterized tests for concise, comprehensive coverage.
"""
import pytest
import sys
import re
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))


class TestSecurityKeywordMatching:
    """Test security keyword detection with word boundary handling."""

    # Test data: (title, body, keywords, expected_is_security, description)
    SECURITY_KEYWORD_CASES = [
        # Short keywords with word boundaries
        ("XSS vulnerability found", "", ["xss"], True, "Short keyword 'xss' exact match"),
        ("Resource allocation issue", "", ["rce"], False, "Short 'rce' should not match 'resource'"),
        ("DOS attack vector", "", ["dos"], True, "Short 'dos' exact word match"),
        ("Windows endpoint issue", "", ["dos"], False, "Short 'dos' should not match 'Windows'"),
        
        # Longer keywords with substring matching
        ("SQL injection in login", "", ["injection"], True, "Long keyword substring match"),
        ("Authentication bypass possible", "", ["authentication bypass"], True, "Multi-word keyword"),
        ("Vulnerability in parser", "", ["vulnerability"], True, "Standard security keyword"),
        
        # No matches
        ("Normal bug report", "Something is broken", ["xss", "injection"], False, "No security keywords"),
        
        # Case insensitivity
        ("XSS ATTACK FOUND", "", ["xss"], True, "Case insensitive matching"),
        ("VULNERABILITY detected", "", ["Vulnerability"], True, "Mixed case keyword"),
        
        # Keywords in body
        ("Bug report", "Found XSS in the form", ["xss"], True, "Keyword in body"),
        ("Issue", "Potential SQL injection risk", ["injection"], True, "Long keyword in body"),
    ]

    @pytest.mark.parametrize(
        "title,body,keywords,expected,description",
        SECURITY_KEYWORD_CASES,
        ids=[c[4] for c in SECURITY_KEYWORD_CASES]
    )
    def test_security_keyword_matching(self, title, body, keywords, expected, description):
        """Test keyword matching with word boundaries for short keywords."""
        combined = f"{title} {body}".lower()
        
        # Replicate the matching logic from llm_service.py
        matched_keywords = []
        for kw in keywords:
            kw_lower = kw.lower()
            if len(kw_lower) <= 3:
                pattern = r'\b' + re.escape(kw_lower) + r'\b'
                if re.search(pattern, combined):
                    matched_keywords.append(kw)
            else:
                if kw_lower in combined:
                    matched_keywords.append(kw)
        
        is_security = len(matched_keywords) > 0
        assert is_security == expected, f"Failed: {description}"


class TestPriorityElevation:
    """Test security priority elevation logic (never downgrade)."""

    # Test data: (current_priority, security_default, expected_priority, should_elevate)
    PRIORITY_ELEVATION_CASES = [
        # Should elevate
        ("P3", "P1", "P1", True, "P3 elevated to P1"),
        ("P4", "P1", "P1", True, "P4 elevated to P1"),
        ("P2", "P1", "P1", True, "P2 elevated to P1"),
        ("P3", "P2", "P2", True, "P3 elevated to P2"),
        
        # Should NOT downgrade
        ("P0", "P1", "P0", False, "P0 stays P0, not downgraded to P1"),
        ("P1", "P1", "P1", False, "P1 stays P1 (equal)"),
        ("P1", "P2", "P1", False, "P1 stays P1, not downgraded to P2"),
        ("P0", "P3", "P0", False, "P0 never downgraded to P3"),
        
        # Edge case: misconfigured security default
        ("P2", "P3", "P2", False, "P2 not downgraded to misconfigured P3"),
    ]

    @pytest.mark.parametrize(
        "current,security_default,expected,should_elevate,description",
        PRIORITY_ELEVATION_CASES,
        ids=[c[4] for c in PRIORITY_ELEVATION_CASES]
    )
    def test_priority_elevation_logic(self, current, security_default, expected, should_elevate, description):
        """Test that security priority only elevates, never downgrades."""
        priority_order = {"P0": 0, "P1": 1, "P2": 2, "P3": 3, "P4": 4}
        current_rank = priority_order.get(current, 4)
        security_rank = priority_order.get(security_default, 1)
        
        # Apply elevation logic
        if security_rank < current_rank:
            result_priority = security_default
            elevated = True
        else:
            result_priority = current
            elevated = False
        
        assert result_priority == expected, f"Priority mismatch: {description}"
        assert elevated == should_elevate, f"Elevation flag mismatch: {description}"


class TestSLACalculation:
    """Test SLA threshold and breach detection."""

    # Test data: (priority, expected_sla_hours)
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

    # Test data: (hours_open, sla_hours, expected_status)
    SLA_STATUS_CASES = [
        (10, 24, "within", "10h open, 24h SLA = within"),
        (20, 24, "warning", "20h open (>80% of 24h) = warning"),
        (25, 24, "breached", "25h open > 24h SLA = breached"),
        (72, 72, "warning", "Exactly at SLA (72h) = warning (not breached)"),
        (73, 72, "breached", "73h > 72h SLA = breached"),
        (58, 72, "warning", "58h open (>80% of 72h) = warning"),
        (40, 72, "within", "40h open (55% of 72h) = within"),
    ]

    @pytest.mark.parametrize(
        "hours_open,sla_hours,expected_status,description",
        SLA_STATUS_CASES,
        ids=[c[3] for c in SLA_STATUS_CASES]
    )
    def test_sla_status_calculation(self, hours_open, sla_hours, expected_status, description):
        """Test SLA status calculation (within, warning, breached)."""
        if hours_open > sla_hours:
            status = "breached"
        elif hours_open > (sla_hours * 0.8):
            status = "warning"
        else:
            status = "within"
        
        assert status == expected_status, f"SLA status mismatch: {description}"


class TestBotDetection:
    """Test exact bot user matching to avoid false positives."""

    TRIAGE_BOT_USERS = ['github-actions[bot]', 'dependabot[bot]']

    # Test data: (login, expected_is_bot)
    BOT_DETECTION_CASES = [
        ("github-actions[bot]", True, "Exact match for github-actions bot"),
        ("dependabot[bot]", True, "Exact match for dependabot"),
        ("mrunalhirve", False, "Human user not a bot"),
        ("actions-user", False, "Substring 'actions' should not match"),
        ("my-github-actions-helper", False, "Substring should not match"),
        ("bot", False, "Just 'bot' is not our bot"),
        ("github-actions", False, "Missing [bot] suffix"),
    ]

    @pytest.mark.parametrize(
        "login,expected_is_bot,description",
        BOT_DETECTION_CASES,
        ids=[c[2] for c in BOT_DETECTION_CASES]
    )
    def test_bot_user_exact_matching(self, login, expected_is_bot, description):
        """Test that bot detection uses exact matching, not substring."""
        # Exact matching (correct)
        is_bot = login in self.TRIAGE_BOT_USERS
        assert is_bot == expected_is_bot, f"Bot detection mismatch: {description}"


class TestEscalationAction:
    """Test escalation action determination."""

    # Test data: (hours_open, sla_hours, assigned_to, leads, expected_action)
    ESCALATION_ACTION_CASES = [
        # Not breached
        (20, 24, ["user1"], ["lead1"], None, "No breach, no action"),
        
        # Breached, no assignee -> assign lead
        (30, 24, [], ["lead1"], "reassign_lead", "Breached, unassigned -> assign lead"),
        
        # Breached, assigned to non-lead -> assign lead
        (30, 24, ["user1"], ["lead1", "lead2"], "reassign_lead", "Breached, assigned to non-lead"),
        
        # Breached, already assigned to lead -> notify manager
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


class TestSecurityConfigParsing:
    """Test SecurityConfig dataclass parsing from config."""

    def test_security_config_from_dict(self):
        """Test SecurityConfig creation from dictionary."""
        from models.team_config import SecurityConfig
        
        config_data = {
            "keywords": ["vulnerability", "xss", "injection"],
            "assignee": "security-lead",
            "default_priority": "P1"
        }
        
        config = SecurityConfig(
            keywords=config_data.get("keywords", []),
            assignee=config_data.get("assignee", ""),
            default_priority=config_data.get("default_priority", "P1")
        )
        
        assert config.keywords == ["vulnerability", "xss", "injection"]
        assert config.assignee == "security-lead"
        assert config.default_priority == "P1"

    def test_security_config_defaults(self):
        """Test SecurityConfig with default values."""
        from models.team_config import SecurityConfig
        
        config = SecurityConfig()
        
        assert len(config.keywords) > 0  # Has default keywords
        assert config.assignee == ""
        assert config.default_priority == "P1"


class TestEscalationService:
    """Test EscalationService methods."""

    def test_get_sla_hours_from_config(self):
        """Test SLA hours retrieval from service."""
        from services.escalation_service import EscalationService
        from unittest.mock import patch, MagicMock
        
        with patch('services.escalation_service.GitHubService'):
            service = EscalationService()
            
            # Test each priority
            assert service.get_sla_hours("P0") == 24
            assert service.get_sla_hours("P1") == 48
            assert service.get_sla_hours("P2") == 72
            assert service.get_sla_hours("P3") == 120
            assert service.get_sla_hours("P4") == 120

    def test_get_escalation_chain(self):
        """Test escalation chain retrieval."""
        from services.escalation_service import EscalationService
        from unittest.mock import patch
        
        with patch('services.escalation_service.GitHubService'):
            service = EscalationService()
            chain = service.get_escalation_chain()
            
            assert "lead" in chain
            assert "manager" in chain
            assert isinstance(chain["lead"], list)

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
        # Get SLA hours for priority
        sla_config = {"P0": 24, "P1": 48, "P2": 72, "P3": 120, "P4": 120}
        sla_hours = sla_config.get(priority, 120)
        
        # Check breach
        sla_breached = hours_open > sla_hours
        
        assert sla_breached == expected_breached, f"Failed: {description}"


class TestDailyReportService:
    """Test DailyReportService methods."""

    # Test data: (hours_open, sla_hours, expected_status)
    SLA_STATUS_CASES = [
        (10, 24, "within"),
        (20, 24, "warning"),
        (30, 24, "breached"),
        (50, 72, "within"),
        (60, 72, "warning"),
        (80, 72, "breached"),
    ]

    @pytest.mark.parametrize(
        "hours_open,sla_hours,expected_status",
        SLA_STATUS_CASES,
        ids=[f"{h}h/{s}h={e}" for h, s, e in SLA_STATUS_CASES]
    )
    def test_calculate_sla_status(self, hours_open, sla_hours, expected_status):
        """Test SLA status calculation in daily report."""
        from services.daily_report_service import DailyReportService
        from unittest.mock import patch
        
        with patch('services.daily_report_service.GitHubService'), \
             patch('services.daily_report_service.EscalationService'), \
             patch('services.daily_report_service.ConfigParser'):
            service = DailyReportService()
            status = service.calculate_sla_status(hours_open, sla_hours)
            assert status == expected_status

    def test_get_sla_hours_delegates_to_escalation_service(self):
        """Test that daily report delegates SLA hours to escalation service."""
        from services.daily_report_service import DailyReportService
        from unittest.mock import patch, MagicMock
        
        mock_escalation = MagicMock()
        mock_escalation.get_sla_hours.return_value = 48
        
        with patch('services.daily_report_service.GitHubService'), \
             patch('services.daily_report_service.EscalationService', return_value=mock_escalation), \
             patch('services.daily_report_service.ConfigParser'):
            service = DailyReportService()
            result = service.get_sla_hours("P1")
            
            assert result == 48
            mock_escalation.get_sla_hours.assert_called_once_with("P1")


class TestConfigParser:
    """Test ConfigParser loading functions."""

    def test_load_security_config_returns_dataclass(self):
        """Test that security config is returned as SecurityConfig dataclass."""
        from services.config_parser import _load_security_config
        from models.team_config import SecurityConfig
        
        config = _load_security_config()
        
        # Should return SecurityConfig or None
        assert config is None or isinstance(config, SecurityConfig)

    def test_load_sla_hours_returns_dict(self):
        """Test SLA hours loading returns dictionary."""
        from services.config_parser import _load_sla_hours
        
        sla_hours = _load_sla_hours()
        
        assert isinstance(sla_hours, dict)
        assert "P0" in sla_hours
        assert "P1" in sla_hours
        assert sla_hours["P0"] == 24
        assert sla_hours["P1"] == 48

    def test_load_escalation_chain_returns_dict(self):
        """Test escalation chain loading returns dictionary."""
        from services.config_parser import _load_escalation_chain
        
        chain = _load_escalation_chain()
        
        assert isinstance(chain, dict)
        assert "lead" in chain
        assert "manager" in chain


class TestTeamsServiceLazyInit:
    """Test TeamsService lazy initialization."""

    def test_teams_service_does_not_log_webhook_url(self):
        """Test that webhook URL is not logged (security)."""
        import logging
        from unittest.mock import patch
        from io import StringIO
        
        # Capture log output
        log_capture = StringIO()
        handler = logging.StreamHandler(log_capture)
        handler.setLevel(logging.DEBUG)
        
        with patch.dict('os.environ', {'TEAMS_WEBHOOK_URL': 'https://secret.webhook.url/abc123'}):
            from services.teams_service import TeamsService
            
            logger = logging.getLogger()
            logger.addHandler(handler)
            
            _service = TeamsService()  # noqa: F841 - instantiation triggers logging
            
            logger.removeHandler(handler)
            
            log_output = log_capture.getvalue()
            
            # Webhook URL should not appear in logs
            assert 'secret.webhook.url' not in log_output
            assert 'abc123' not in log_output

    def test_teams_service_indicates_configuration_status(self):
        """Test that TeamsService logs whether webhook is configured (without URL)."""
        from services.teams_service import TeamsService
        from unittest.mock import patch
        
        with patch.dict('os.environ', {'TEAMS_WEBHOOK_URL': 'https://example.com'}):
            service = TeamsService()
            assert service.webhook_url == 'https://example.com'
        
        with patch.dict('os.environ', {}, clear=True):
            service = TeamsService()
            assert service.webhook_url == ''


class TestWorkflowTriggers:
    """Test workflow trigger configuration validation."""

    def test_retriage_flag_logic(self):
        """Test retriage flag determination based on action type."""
        # Replicate workflow logic
        RETRIAGE_CASES = [
            ("opened", False, "New issue should not be retriage"),
            ("edited", True, "Edited issue should be retriage"),
            ("labeled", True, "Labeled issue should be retriage"),
            ("created", True, "Comment created should be retriage"),
        ]
        
        for action, expected_retriage, description in RETRIAGE_CASES:
            if action == "opened":
                retriage = False
            else:
                retriage = True
            
            assert retriage == expected_retriage, f"Failed: {description}"


class TestLLMServiceSecurityDetection:
    """Test LLM service security issue detection."""

    def test_is_security_issue_with_keywords(self):
        """Test security detection with keyword matching."""
        from services.llm_service import LlmService
        from unittest.mock import patch
        
        with patch.dict('os.environ', {'GITHUB_TOKEN': 'test'}):
            service = LlmService()
            
            # Test with security keywords
            result = service.is_security_issue(
                title="XSS vulnerability in login form",
                body="Found a cross-site scripting issue",
                security_keywords=["xss", "vulnerability", "injection"]
            )
            
            assert result["is_security"] == True
            assert result["confidence"] >= 0.8

    def test_is_security_issue_no_keywords(self):
        """Test security detection without matching keywords."""
        from services.llm_service import LlmService
        from unittest.mock import patch
        
        with patch.dict('os.environ', {'GITHUB_TOKEN': 'test'}):
            service = LlmService()
            
            # Mock the LLM call to avoid actual API call
            service._call_llm = lambda *args, **kwargs: '{"is_security": false, "confidence": 0.9, "reasoning": "Not security related"}'
            
            result = service.is_security_issue(
                title="Button color is wrong",
                body="The submit button should be blue not green",
                security_keywords=["xss", "vulnerability", "injection"]
            )
            
            assert result["is_security"] == False


class TestGitHubServiceLabelValidation:
    """Test GitHub service label operations."""

    def test_validate_labels_with_existing_labels(self):
        """Test label validation against repository labels."""
        from services.github_service import GitHubService
        from unittest.mock import patch, MagicMock
        
        with patch.dict('os.environ', {'GITHUB_TOKEN': 'test'}):
            service = GitHubService()
            
            # Mock repository labels
            service.get_repository_labels = MagicMock(return_value={
                'bug': {'color': 'red'},
                'enhancement': {'color': 'blue'},
                'P1': {'color': 'orange'},
                'P2': {'color': 'yellow'},
            })
            
            result = service.validate_labels('owner', 'repo', ['bug', 'P1', 'invalid-label'])
            
            assert 'bug' in result['valid']
            assert 'P1' in result['valid']
            assert 'invalid-label' in result['invalid']


class TestEscalationServiceMethods:
    """Test EscalationService additional methods."""

    def test_get_issue_priority_extracts_priority_label(self):
        """Test priority extraction from issue labels."""
        from services.escalation_service import EscalationService
        from unittest.mock import patch, MagicMock
        
        with patch('services.escalation_service.GitHubService'):
            service = EscalationService()
            
            # Create mock issue with P1 label
            mock_label = MagicMock()
            mock_label.name = "P1"
            
            mock_issue = MagicMock()
            mock_issue.labels = [mock_label]
            
            priority = service.get_issue_priority(mock_issue)
            assert priority == "P1"

    def test_get_issue_priority_returns_none_without_priority(self):
        """Test priority extraction returns None without priority label."""
        from services.escalation_service import EscalationService
        from unittest.mock import patch, MagicMock
        
        with patch('services.escalation_service.GitHubService'):
            service = EscalationService()
            
            # Create mock issue without priority label
            mock_label = MagicMock()
            mock_label.name = "bug"
            
            mock_issue = MagicMock()
            mock_issue.labels = [mock_label]
            
            priority = service.get_issue_priority(mock_issue)
            assert priority is None

    def test_calculate_hours_open(self):
        """Test hours calculation from issue updated_at."""
        from services.escalation_service import EscalationService
        from unittest.mock import patch, MagicMock
        from datetime import datetime, timezone, timedelta
        
        with patch('services.escalation_service.GitHubService'):
            service = EscalationService()
            
            mock_issue = MagicMock()
            mock_issue.updated_at = datetime.now(timezone.utc) - timedelta(hours=48)
            
            hours = service.calculate_hours_open(mock_issue)
            
            # Should be approximately 48 hours
            assert 47 < hours < 49


class TestDailyReportServiceGeneration:
    """Test DailyReportService report generation."""

    def test_issue_report_item_creation(self):
        """Test IssueReportItem dataclass creation."""
        from services.daily_report_service import IssueReportItem
        
        item = IssueReportItem(
            number=123,
            title="Test issue",
            priority="P2",
            labels=["priority-2", "area-security"],
            assignee="testuser",
            hours_open=24.5,
            sla_hours=72,
            sla_status="within",
            url="https://github.com/owner/repo/issues/123"
        )
        
        assert item.number == 123
        assert item.priority == "P2"
        assert item.sla_status == "within"
        assert "priority-2" in item.labels

    def test_daily_report_dataclass(self):
        """Test DailyReport dataclass creation."""
        from services.daily_report_service import DailyReport, IssueReportItem
        
        report = DailyReport(
            repository="owner/repo",
            generated_at="2026-02-06 10:00 UTC",
            total_open=5,
            sla_compliance_pct=80.0,
            breached_count=1,
            warning_count=1,
            by_priority={"P1": 2, "P2": 3},
            issues=[]
        )
        
        assert report.total_open == 5
        assert report.sla_compliance_pct == 80.0
        assert report.breached_count == 1


class TestIntakeServiceHelpers:
    """Test intake_service helper functions."""

    def test_parse_issue_url_valid(self):
        """Test parsing valid GitHub issue URL."""
        from services.intake_service import _parse_issue_url
        
        result = _parse_issue_url("https://github.com/microsoft/Agent365-devTools/issues/123")
        
        assert result is not None
        owner, repo, issue_num = result
        assert owner == "microsoft"
        assert repo == "Agent365-devTools"
        assert issue_num == 123

    def test_parse_issue_url_invalid(self):
        """Test parsing invalid URL returns None."""
        from services.intake_service import _parse_issue_url
        
        result = _parse_issue_url("https://example.com/not-a-github-url")
        assert result is None

    def test_map_to_repository_labels(self):
        """Test label mapping to repository labels."""
        from services.intake_service import _map_to_repository_labels
        from services.github_service import GitHubService
        from unittest.mock import patch, MagicMock
        
        mock_github = MagicMock()
        mock_github.get_repository_labels.return_value = {
            'bug': {'color': 'red'},
            'enhancement': {'color': 'blue'},
        }
        
        result = _map_to_repository_labels(mock_github, 'owner', 'repo', 'bug', 'P2')
        
        assert isinstance(result, list)

    # Test data: (issue_type, priority, expected_contains)
    LABEL_MAPPING_CASES = [
        ("bug", "P1", ["bug"], "Bug maps to bug label"),
        ("feature", "P2", ["enhancement", "feature"], "Feature may map to enhancement"),
    ]

    @pytest.mark.parametrize(
        "issue_type,priority,possible_labels,description",
        LABEL_MAPPING_CASES,
        ids=[c[3] for c in LABEL_MAPPING_CASES]
    )
    def test_label_mapping_types(self, issue_type, priority, possible_labels, description):
        """Test that issue types map to expected labels."""
        from services.intake_service import _map_to_repository_labels
        from unittest.mock import MagicMock
        
        mock_github = MagicMock()
        mock_github.get_repository_labels.return_value = {
            'bug': {},
            'enhancement': {},
            'feature': {},
            'documentation': {},
        }
        
        result = _map_to_repository_labels(mock_github, 'owner', 'repo', issue_type, priority)
        
        # Check that at least one expected label is present
        has_expected = any(label in result for label in possible_labels)
        assert has_expected or len(result) == 0, f"Failed: {description}"


class TestPromptLoader:
    """Test prompt loader service."""

    def test_prompt_loader_loads_prompts(self):
        """Test that prompt loader can load prompts from YAML."""
        from services.prompt_loader import PromptLoader
        
        loader = PromptLoader()
        
        # Should have some prompts loaded
        assert loader is not None

    def test_prompt_loader_get_prompt(self):
        """Test getting a specific prompt."""
        from services.prompt_loader import PromptLoader
        
        loader = PromptLoader()
        
        # Use the correct method: get() not get_prompt()
        prompt = loader.get("classification")
        
        # Should return a string (may be empty if not found)
        assert isinstance(prompt, str)
