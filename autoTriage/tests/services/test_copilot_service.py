# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Tests for CopilotService - Copilot auto-fix integration.

Uses parameterized tests for comprehensive edge case coverage.
"""
import json
import subprocess
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent.parent))


# =============================================================================
# Test Data Fixtures
# =============================================================================

def make_graphql_response(actors: list) -> dict:
    """Helper to create a valid GraphQL response structure."""
    return {
        "data": {
            "repository": {
                "suggestedActors": {
                    "nodes": actors
                }
            }
        }
    }


def make_subprocess_result(returncode: int, stdout: str = "", stderr: str = "") -> MagicMock:
    """Helper to create mock subprocess result."""
    mock = MagicMock()
    mock.returncode = returncode
    mock.stdout = stdout
    mock.stderr = stderr
    return mock


# =============================================================================
# TestCopilotServiceInit
# =============================================================================

class TestCopilotServiceInit:
    """Test CopilotService initialization."""

    def test_init_creates_service(self):
        """Test that CopilotService initializes successfully."""
        from services.copilot_service import CopilotService
        service = CopilotService()
        # Service should initialize without error
        # Note: gh CLI authentication is handled externally via GH_TOKEN or gh auth
        assert service is not None


# =============================================================================
# TestIsCopilotEnabled - Core Functionality
# =============================================================================

class TestIsCopilotEnabled:
    """Test is_copilot_enabled method with parameterized scenarios."""

    # Test: Copilot presence in actors list
    @pytest.mark.parametrize("actors,expected,description", [
        # Should return True - copilot-swe-agent present (both variants accepted)
        ([{"login": "copilot-swe-agent"}], True, "only copilot"),
        ([{"login": "copilot-swe-agent"}, {"login": "user1"}], True, "copilot first"),
        ([{"login": "user1"}, {"login": "copilot-swe-agent"}], True, "copilot last"),
        ([{"login": "user1"}, {"login": "copilot-swe-agent"}, {"login": "user2"}], True, "copilot middle"),
        ([{"login": "copilot-swe-agent[bot]"}], True, "bot suffix variant"),
        
        # Should return False - copilot-swe-agent NOT present
        ([], False, "empty list"),
        ([{"login": "user1"}], False, "single user"),
        ([{"login": "user1"}, {"login": "user2"}, {"login": "user3"}], False, "multiple users"),
        
        # Edge cases: similar but not exact match
        ([{"login": "copilot"}], False, "just copilot"),
        ([{"login": "swe-agent"}], False, "partial name"),
        ([{"login": "Copilot-swe-agent"}], False, "wrong case"),
        ([{"login": "COPILOT-SWE-AGENT"}], False, "uppercase"),
        ([{"login": " copilot-swe-agent"}], False, "leading space"),
        ([{"login": "copilot-swe-agent "}], False, "trailing space"),
    ], ids=lambda x: x if isinstance(x, str) else None)
    def test_copilot_detection_in_actors_list(self, actors, expected, description):
        """Test correct detection of copilot-swe-agent in various actor configurations."""
        from services.copilot_service import CopilotService
        
        mock_result = make_subprocess_result(0, json.dumps(make_graphql_response(actors)))
        
        with patch('subprocess.run', return_value=mock_result):
            service = CopilotService()
            result = service.is_copilot_enabled("owner", "repo")
            assert result is expected, f"Failed for: {description}"

    # Test: Malformed API responses
    @pytest.mark.parametrize("response,description", [
        ({}, "empty object"),
        ({"data": {}}, "missing repository"),
        ({"data": {"repository": {}}}, "missing suggestedActors"),
        ({"data": {"repository": {"suggestedActors": {}}}}, "missing nodes"),
        ({"data": {"repository": {"suggestedActors": {"nodes": None}}}}, "null nodes"),
        ({"data": None}, "null data"),
        ({"data": {"repository": None}}, "null repository"),
        ({"errors": [{"message": "Not Found"}]}, "graphql error"),
        ({"data": {"repository": {"suggestedActors": {"nodes": "not-a-list"}}}}, "nodes not a list"),
    ], ids=lambda x: x if isinstance(x, str) else None)
    def test_returns_false_on_malformed_responses(self, response, description):
        """Test graceful handling of malformed API responses."""
        from services.copilot_service import CopilotService
        
        mock_result = make_subprocess_result(0, json.dumps(response))
        
        with patch('subprocess.run', return_value=mock_result):
            service = CopilotService()
            result = service.is_copilot_enabled("owner", "repo")
            assert result is False, f"Should return False for: {description}"

    # Test: API/subprocess errors
    @pytest.mark.parametrize("side_effect,return_code,stderr,description", [
        (None, 1, "API error", "generic API failure"),
        (None, 1, "401 Unauthorized", "auth failure"),
        (None, 1, "404 Not Found", "repo not found"),
        (None, 1, "rate limit exceeded", "rate limited"),
        (subprocess.TimeoutExpired(cmd='gh', timeout=30), 0, "", "timeout"),
        (Exception("Network error"), 0, "", "network exception"),
        (OSError("Command not found"), 0, "", "gh not installed"),
    ], ids=lambda x: x if isinstance(x, str) else None)
    def test_returns_false_on_errors(self, side_effect, return_code, stderr, description):
        """Test returns False for various error scenarios."""
        from services.copilot_service import CopilotService
        
        if side_effect:
            with patch('subprocess.run', side_effect=side_effect):
                service = CopilotService()
                result = service.is_copilot_enabled("owner", "repo")
                assert result is False, f"Should handle: {description}"
        else:
            mock_result = make_subprocess_result(return_code, "", stderr)
            with patch('subprocess.run', return_value=mock_result):
                service = CopilotService()
                result = service.is_copilot_enabled("owner", "repo")
                assert result is False, f"Should handle: {description}"

    # Test: Invalid JSON response
    @pytest.mark.parametrize("invalid_stdout", [
        "",
        "not json",
        "{malformed",
        "null",
        "[]",
    ], ids=["empty", "plain_text", "malformed_json", "null_string", "array"])
    def test_returns_false_on_invalid_json(self, invalid_stdout):
        """Test returns False when response is not valid JSON object."""
        from services.copilot_service import CopilotService
        
        mock_result = make_subprocess_result(0, invalid_stdout)
        
        with patch('subprocess.run', return_value=mock_result):
            service = CopilotService()
            result = service.is_copilot_enabled("owner", "repo")
            assert result is False


# =============================================================================
# TestAssignToCopilot
# =============================================================================

class TestAssignToCopilot:
    """Test assign_to_copilot method."""

    # Test: Successful assignments with different parameters
    @pytest.mark.parametrize("issue_num,base_branch,instructions", [
        (1, "main", ""),
        (42, "main", ""),
        (999, "develop", "Fix the bug"),
        (123, "feature/test", "Please follow the style guide"),
        (1, "main", "Multi\nline\ninstructions"),
    ], ids=["minimal", "standard", "with_branch", "with_instructions", "multiline_instructions"])
    def test_successful_assignment(self, issue_num, base_branch, instructions):
        """Test successful issue assignment to Copilot."""
        from services.copilot_service import CopilotService
        
        mock_response = {"id": 123, "number": issue_num}
        mock_result = make_subprocess_result(0, json.dumps(mock_response))
        
        with patch('subprocess.run', return_value=mock_result):
            service = CopilotService()
            result = service.assign_to_copilot(
                "owner", "repo", issue_num,
                custom_instructions=instructions,
                base_branch=base_branch
            )
            
            assert result["success"] is True
            assert result["issue_number"] == issue_num
            assert result["assigned_to"] == "copilot-swe-agent[bot]"  # Uses COPILOT_ASSIGNEE constant
            assert result["base_branch"] == base_branch

    # Test: Payload verification
    def test_payload_structure(self):
        """Verify the payload sent to GitHub API is correct."""
        from services.copilot_service import CopilotService, COPILOT_ASSIGNEE
        
        mock_result = make_subprocess_result(0, "{}")
        
        with patch('subprocess.run', return_value=mock_result) as mock_run:
            service = CopilotService()
            service.assign_to_copilot(
                "microsoft", "Agent365-devTools", 42,
                custom_instructions="Fix it",
                base_branch="develop"
            )
            
            # Extract the input payload
            call_kwargs = mock_run.call_args.kwargs
            payload = json.loads(call_kwargs['input'])
            
            assert payload["assignees"] == [COPILOT_ASSIGNEE]
            assert payload["agent_assignment"]["target_repo"] == "microsoft/Agent365-devTools"
            assert payload["agent_assignment"]["base_branch"] == "develop"
            assert payload["agent_assignment"]["custom_instructions"] == "Fix it"

    # Test: Failure scenarios
    @pytest.mark.parametrize("side_effect,return_code,stderr,expected_error", [
        (None, 1, "Not authorized", "Not authorized"),
        (None, 1, "Issue not found", "Issue not found"),
        (None, 1, "Rate limit", "Rate limit"),
        (subprocess.TimeoutExpired(cmd='gh', timeout=60), 0, "", "Timeout"),
        (Exception("Connection failed"), 0, "", "Connection failed"),
    ], ids=["unauthorized", "not_found", "rate_limit", "timeout", "exception"])
    def test_assignment_failures(self, side_effect, return_code, stderr, expected_error):
        """Test handling of various assignment failures."""
        from services.copilot_service import CopilotService
        
        if side_effect:
            with patch('subprocess.run', side_effect=side_effect):
                service = CopilotService()
                result = service.assign_to_copilot("owner", "repo", 42)
                
                assert result["success"] is False
                assert expected_error in result["error"]
                assert result["issue_number"] == 42
        else:
            mock_result = make_subprocess_result(return_code, "", stderr)
            with patch('subprocess.run', return_value=mock_result):
                service = CopilotService()
                result = service.assign_to_copilot("owner", "repo", 42)
                
                assert result["success"] is False
                assert result["error"] == stderr
                assert result["issue_number"] == 42


# =============================================================================
# TestGetFixInstructions
# =============================================================================

class TestGetFixInstructions:
    """Test get_fix_instructions method."""

    # Test: Various suggestion combinations
    @pytest.mark.parametrize("suggestions,should_have_approach", [
        (["Fix A", "Fix B", "Fix C"], True),
        (["Single fix"], True),
        ([], False),
        (None, False),
    ], ids=["multiple", "single", "empty_list", "none"])
    def test_suggestions_section_presence(self, suggestions, should_have_approach):
        """Test that 'Suggested approach' section appears only when suggestions exist."""
        from services.copilot_service import CopilotService
        
        service = CopilotService()
        instructions = service.get_fix_instructions(
            issue_title="Test",
            issue_body="Body",
            fix_suggestions=suggestions
        )
        
        if should_have_approach:
            assert "Suggested approach:" in instructions
        else:
            assert "Suggested approach:" not in instructions

    # Test: Required elements always present
    @pytest.mark.parametrize("suggestions", [
        ["Fix A"],
        [],
        None,
    ])
    def test_required_elements_always_present(self, suggestions):
        """Test that core requirements are always in the instructions."""
        from services.copilot_service import CopilotService
        
        service = CopilotService()
        instructions = service.get_fix_instructions(
            issue_title="Test Issue",
            issue_body="Issue body",
            fix_suggestions=suggestions
        )
        
        assert "Please fix the issue" in instructions
        assert "Requirements:" in instructions
        assert "Follow existing code style" in instructions
        assert "Keep the PR small" in instructions

    # Test: Issue title is included
    def test_issue_title_included(self):
        """Test that issue title is included in the instructions."""
        from services.copilot_service import CopilotService
        
        service = CopilotService()
        instructions = service.get_fix_instructions(
            issue_title="Button color is wrong on mobile",
            issue_body="The button should be blue",
            fix_suggestions=[]
        )
        
        assert "Issue: Button color is wrong on mobile" in instructions

    # Test: Issue body is included
    def test_issue_body_included(self):
        """Test that issue body is included in the instructions."""
        from services.copilot_service import CopilotService
        
        service = CopilotService()
        instructions = service.get_fix_instructions(
            issue_title="Test",
            issue_body="The submit button on the checkout page shows as gray instead of blue.",
            fix_suggestions=[]
        )
        
        assert "Description:" in instructions
        assert "submit button" in instructions
        assert "gray instead of blue" in instructions

    # Test: Long issue body is truncated
    def test_issue_body_truncated(self):
        """Test that very long issue bodies are truncated."""
        from services.copilot_service import CopilotService
        
        service = CopilotService()
        long_body = "A" * 2000  # Longer than 1500 char limit
        instructions = service.get_fix_instructions(
            issue_title="Test",
            issue_body=long_body,
            fix_suggestions=[]
        )
        
        assert "..." in instructions
        # Should have truncated content
        assert "A" * 1500 in instructions

    # Test: Suggestion numbering
    def test_suggestions_are_numbered(self):
        """Test that multiple suggestions are properly numbered."""
        from services.copilot_service import CopilotService
        
        service = CopilotService()
        suggestions = ["First step", "Second step", "Third step"]
        instructions = service.get_fix_instructions(
            issue_title="Test",
            issue_body="Body",
            fix_suggestions=suggestions
        )
        
        assert "1. First step" in instructions
        assert "2. Second step" in instructions
        assert "3. Third step" in instructions

    # Test: Special characters in suggestions
    @pytest.mark.parametrize("suggestion", [
        "Fix `code` block",
        "Fix <html> tags",
        'Fix "quoted" text',
        "Fix 'single' quotes",
        "Fix path/to/file.py",
        "Fix with unicode: \u2713 \u274c",
    ], ids=["backticks", "html", "double_quotes", "single_quotes", "path", "unicode"])
    def test_special_characters_in_suggestions(self, suggestion):
        """Test that special characters in suggestions are preserved."""
        from services.copilot_service import CopilotService
        
        service = CopilotService()
        instructions = service.get_fix_instructions(
            issue_title="Test",
            issue_body="Body",
            fix_suggestions=[suggestion]
        )
        
        assert suggestion in instructions
