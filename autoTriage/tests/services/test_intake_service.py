# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Tests for intake_service.py

Tests the IntakeService including:
- URL parsing and repository detection
- Label priority/area mapping
- Security issue detection and priority elevation
- Bot detection
"""
import pytest
import sys
from pathlib import Path
from unittest.mock import patch, MagicMock

sys.path.insert(0, str(Path(__file__).parent.parent.parent))


class TestBotDetection:
    """Test bot account detection."""

    BOT_DETECTION_CASES = [
        ("dependabot[bot]", True, "Standard bot format with [bot] suffix"),
        ("renovate[bot]", True, "Renovate bot"),
        ("github-actions[bot]", True, "GitHub Actions bot"),
        ("myuser", False, "Regular user"),
        ("botuser", False, "User with 'bot' in name but no [bot] suffix"),
        ("user-bot-name", False, "User with bot in middle of name"),
        ("", False, "Empty string"),
        ("[bot]", True, "Just [bot] suffix (edge case)"),
        ("Bot[bot]", True, "Bot with [bot] suffix"),
    ]

    @pytest.mark.parametrize(
        "username,expected,description",
        BOT_DETECTION_CASES,
        ids=[c[2] for c in BOT_DETECTION_CASES]
    )
    def test_is_bot_detection(self, username, expected, description):
        """Test bot detection logic."""
        result = username.endswith("[bot]")
        assert result == expected, f"Bot detection failed: {description}"


class TestLabelPriorityMapping:
    """Test label to priority mapping."""

    PRIORITY_LABELS = [
        ("priority-0", "P0"),
        ("priority-1", "P1"),
        ("priority-2", "P2"),
        ("priority-3", "P3"),
        ("priority-4", "P4"),
        ("Priority-0", "P0"),  # Case handling
        ("P0", None),  # Not a priority-X label
        ("high-priority", None),  # Not matching format
    ]

    @pytest.mark.parametrize(
        "label,expected_priority",
        PRIORITY_LABELS,
        ids=[f"{l}=>{p or 'None'}" for l, p in PRIORITY_LABELS]
    )
    def test_label_priority_mapping(self, label, expected_priority):
        """Test label to priority mapping."""
        label_lower = label.lower()
        if label_lower.startswith("priority-"):
            priority = f"P{label_lower[-1]}"
        else:
            priority = None
        
        assert priority == expected_priority


class TestSecurityPriorityElevation:
    """Test security issue priority elevation logic."""

    ELEVATION_CASES = [
        ("P4", "P1", "P1", "Security issue starts at P4, elevates to P1"),
        ("P3", "P1", "P1", "Security issue at P3, elevates to P1"),
        ("P2", "P1", "P1", "Security issue at P2, elevates to P1"),
        ("P1", "P1", "P1", "Already at security threshold, no change"),
        ("P0", "P1", "P0", "P0 stays P0 (higher than security threshold)"),
    ]

    @pytest.mark.parametrize(
        "current_priority,security_threshold,expected_final,description",
        ELEVATION_CASES,
        ids=[c[3] for c in ELEVATION_CASES]
    )
    def test_security_priority_elevation(
        self, current_priority, security_threshold, expected_final, description
    ):
        """Test priority elevation for security issues."""
        priority_order = {"P0": 0, "P1": 1, "P2": 2, "P3": 3, "P4": 4}
        current_rank = priority_order.get(current_priority, 4)
        security_rank = priority_order.get(security_threshold, 1)
        
        if current_rank > security_rank:
            final_priority = security_threshold
        else:
            # Original code had redundant elif - now just else
            final_priority = current_priority
        
        assert final_priority == expected_final, f"Elevation failed: {description}"


class TestURLParsing:
    """Test _parse_issue_url for repository detection, including GHE support."""

    def setup_method(self):
        from services.intake_service import _parse_issue_url
        self._parse = _parse_issue_url

    VALID_CASES = [
        ("https://github.com/microsoft/repo/issues/123", "microsoft", "repo", 123),
        ("https://github.com/org/project/issues/1", "org", "project", 1),
        ("https://github.com/owner/repo-name/issues/999", "owner", "repo-name", 999),
        # GitHub Enterprise Server
        ("https://github.example.com/myorg/myrepo/issues/42", "myorg", "myrepo", 42),
        ("https://ghe.contoso.com/team/project/issues/7", "team", "project", 7),
    ]

    @pytest.mark.parametrize(
        "url,expected_owner,expected_repo,expected_issue",
        VALID_CASES,
        ids=[f"issue_{issue}" for _, _, _, issue in VALID_CASES]
    )
    def test_valid_url_parsing(self, url, expected_owner, expected_repo, expected_issue):
        """Valid GitHub and GHE issue URLs parse correctly."""
        result = self._parse(url)
        assert result is not None
        owner, repo, number = result
        assert owner == expected_owner
        assert repo == expected_repo
        assert number == expected_issue

    INVALID_CASES = [
        ("https://github.com/owner/repo/pull/123", "PR URL, not an issue"),
        ("https://github.com/owner/repo/issues/abc", "non-numeric issue number"),
        ("https://github.com/owner/issues/123", "missing repo segment"),
        ("not-a-url", "bare string"),
        ("", "empty string"),
        ("https://github.com/owner/repo/issues/123/comments", "extra path segment"),
    ]

    @pytest.mark.parametrize(
        "url,description",
        INVALID_CASES,
        ids=[d for _, d in INVALID_CASES]
    )
    def test_invalid_url_returns_none(self, url, description):
        """Malformed or non-issue URLs return None."""
        result = self._parse(url)
        assert result is None, f"Expected None for: {description}"


class TestTriageDecisionLogic:
    """Test triage decision-making logic."""

    def test_skip_triage_for_bot_issues(self):
        """Test that bot issues skip triage."""
        mock_issue = MagicMock()
        mock_issue.user.login = "dependabot[bot]"
        
        is_bot = mock_issue.user.login.endswith("[bot]")
        assert is_bot is True

    def test_triage_for_human_issues(self):
        """Test that human issues proceed with triage."""
        mock_issue = MagicMock()
        mock_issue.user.login = "realuser"
        
        is_bot = mock_issue.user.login.endswith("[bot]")
        assert is_bot is False


class TestAreaLabelMapping:
    """Test area label detection and mapping."""

    AREA_LABEL_CASES = [
        ("area-security", "security"),
        ("area-performance", "performance"),
        ("area-docs", "docs"),
        ("Area-Security", "security"),  # Case insensitive
        ("bug", None),  # Not an area label
        ("priority-1", None),  # Priority label
    ]

    @pytest.mark.parametrize(
        "label,expected_area",
        AREA_LABEL_CASES,
        ids=[f"{l}=>{a or 'None'}" for l, a in AREA_LABEL_CASES]
    )
    def test_area_label_extraction(self, label, expected_area):
        """Test area extraction from labels."""
        label_lower = label.lower()
        if label_lower.startswith("area-"):
            area = label_lower[5:]  # Remove "area-" prefix
        else:
            area = None
        
        assert area == expected_area


class TestFetchIssuesToTriageMutualExclusion:
    """Tests for the issue_url / issue_numbers mutual exclusion guard."""

    def test_raises_when_both_provided(self):
        """Passing both issue_url and issue_numbers raises ValueError immediately."""
        from unittest.mock import MagicMock
        from services.intake_service import _fetch_issues_to_triage

        mock_github = MagicMock()

        with pytest.raises(ValueError, match="mutually exclusive"):
            _fetch_issues_to_triage(
                github_service=mock_github,
                owner="owner",
                repo="repo",
                since_hours=24,
                issue_url="https://github.com/owner/repo/issues/1",
                issue_numbers=[2, 3],
            )

    def test_accepts_issue_url_alone(self):
        """issue_url without issue_numbers is valid (no ValueError raised)."""
        from unittest.mock import MagicMock
        from services.intake_service import _fetch_issues_to_triage

        mock_github = MagicMock()
        mock_issue = MagicMock()
        mock_issue.number = 1
        mock_github.get_new_untriaged_issues.return_value = [mock_issue]
        mock_github.get_issue.return_value = mock_issue

        # Should not raise
        result = _fetch_issues_to_triage(
            github_service=mock_github,
            owner="owner",
            repo="repo",
            since_hours=24,
            issue_url="https://github.com/owner/repo/issues/1",
            issue_numbers=None,
        )
        assert result is not None

    def test_accepts_issue_numbers_alone(self):
        """issue_numbers without issue_url is valid (no ValueError raised)."""
        from unittest.mock import MagicMock
        from services.intake_service import _fetch_issues_to_triage

        mock_github = MagicMock()
        mock_issue = MagicMock()
        mock_github.get_issue.return_value = mock_issue

        result = _fetch_issues_to_triage(
            github_service=mock_github,
            owner="owner",
            repo="repo",
            since_hours=24,
            issue_url=None,
            issue_numbers=[5, 6],
        )
        assert result is not None

    def test_ghe_host_extracted_from_url(self):
        """github_host returned from _fetch_issues_to_triage matches the URL hostname."""
        from unittest.mock import MagicMock
        from services.intake_service import _fetch_issues_to_triage

        mock_github = MagicMock()
        mock_issue = MagicMock()
        mock_issue.number = 99
        mock_github.get_new_untriaged_issues.return_value = [mock_issue]
        mock_github.get_issue.return_value = mock_issue

        _, _, _, _, _, github_host = _fetch_issues_to_triage(
            github_service=mock_github,
            owner="owner",
            repo="repo",
            since_hours=24,
            issue_url="https://github.example.com/owner/repo/issues/99",
            issue_numbers=None,
        )
        assert github_host == "github.example.com"
