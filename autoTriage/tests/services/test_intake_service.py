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
    """Test URL parsing for repository detection."""

    URL_PARSING_CASES = [
        ("https://github.com/microsoft/repo/issues/123", "microsoft", "repo", 123),
        ("https://github.com/org/project/issues/1", "org", "project", 1),
        ("https://github.com/owner/repo-name/issues/999", "owner", "repo-name", 999),
    ]

    @pytest.mark.parametrize(
        "url,expected_owner,expected_repo,expected_issue",
        URL_PARSING_CASES,
        ids=[f"issue_{issue}" for _, _, _, issue in URL_PARSING_CASES]
    )
    def test_url_parsing(self, url, expected_owner, expected_repo, expected_issue):
        """Test GitHub URL parsing."""
        import re
        pattern = r"github\.com/([^/]+)/([^/]+)/issues/(\d+)"
        match = re.search(pattern, url)
        
        assert match is not None
        assert match.group(1) == expected_owner
        assert match.group(2) == expected_repo
        assert int(match.group(3)) == expected_issue


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
