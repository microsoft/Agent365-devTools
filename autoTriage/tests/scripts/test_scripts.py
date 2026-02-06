# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Tests for autoTriage scripts.

Tests the scripts in scripts/:
- update_contributions.py
"""
import pytest
import sys
from pathlib import Path
from unittest.mock import patch, MagicMock

sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from scripts.update_contributions import calculate_contribution_score


class TestContributionScoreCalculation:
    """Test contribution score calculation."""

    # Test cases: (open_issues, open_prs, expected_score, description)
    SCORE_CASES = [
        (0, 0, 5, "No workload = minimum score of 5"),
        (1, 0, 7, "1 issue = 5 base + 2 = 7"),
        (5, 0, 15, "5 issues = 5 base + 10 = 15"),
        (0, 5, 10, "5 PRs = 5 base + 5 = 10"),
        (5, 5, 20, "5 issues + 5 PRs = 5 + 10 + 5 = 20"),
        (10, 10, 35, "10 issues + 10 PRs = 5 + 20 + 10 = 35"),
        (25, 0, 50, "25 issues = capped at 50"),
        (30, 30, 50, "Very high workload = capped at 50"),
        (20, 5, 50, "20 issues + 5 PRs = 5 + 40 + 5 = 50 (at cap)"),
    ]

    @pytest.mark.parametrize(
        "open_issues,open_prs,expected_score,description",
        SCORE_CASES,
        ids=[c[3] for c in SCORE_CASES]
    )
    def test_contribution_score_calculation(
        self, open_issues, open_prs, expected_score, description
    ):
        """Test contribution score calculation based on workload."""
        score = calculate_contribution_score(open_issues, open_prs)
        assert score == expected_score, f"Failed: {description}"

    def test_contribution_score_minimum(self):
        """Test that score never goes below 5."""
        score = calculate_contribution_score(0, 0)
        assert score >= 5

    def test_contribution_score_maximum(self):
        """Test that score is capped at 50."""
        score = calculate_contribution_score(100, 100)
        assert score == 50

    def test_contribution_score_issue_weight(self):
        """Test that issues have weight of 2."""
        score_0 = calculate_contribution_score(0, 0)
        score_1 = calculate_contribution_score(1, 0)
        assert score_1 - score_0 == 2

    def test_contribution_score_pr_weight(self):
        """Test that PRs have weight of 1."""
        score_0 = calculate_contribution_score(0, 0)
        score_1 = calculate_contribution_score(0, 1)
        assert score_1 - score_0 == 1


class TestWorkloadFetching:
    """Test workload fetching from GitHub."""

    def test_get_team_member_workload_success(self):
        """Test successful workload fetching."""
        from scripts.update_contributions import get_team_member_workload
        
        mock_github_service = MagicMock()
        
        # Mock search results
        mock_issues_result = MagicMock()
        mock_issues_result.totalCount = 5
        
        mock_prs_result = MagicMock()
        mock_prs_result.totalCount = 2
        
        mock_github_service.client.search_issues.side_effect = [
            mock_issues_result,
            mock_prs_result
        ]
        
        workload = get_team_member_workload(
            mock_github_service,
            "owner",
            "repo",
            "testuser"
        )
        
        assert workload["open_issues"] == 5
        assert workload["open_prs"] == 2

    def test_get_team_member_workload_error_handling(self):
        """Test workload fetching handles errors gracefully."""
        from scripts.update_contributions import get_team_member_workload
        
        mock_github_service = MagicMock()
        mock_github_service.client.search_issues.side_effect = Exception("API Error")
        
        workload = get_team_member_workload(
            mock_github_service,
            "owner",
            "repo",
            "testuser"
        )
        
        # Should return zeros on error, not raise
        assert workload["open_issues"] == 0
        assert workload["open_prs"] == 0
