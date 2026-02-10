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
# Helper functions for GraphQL API mocking
# =============================================================================

def make_prerequisites_response(
    bot_id: str = "BOT_123",
    repo_id: str = "REPO_123",
    issue_id: str = "ISSUE_456"
) -> dict:
    """Helper to create a valid combined prerequisites lookup response.
    
    This response combines bot ID, repo ID, and issue ID lookups into a single
    GraphQL query response.
    """
    return {
        "data": {
            "repository": {
                "id": repo_id,
                "issue": {
                    "id": issue_id
                },
                "suggestedActors": {
                    "nodes": [
                        {"login": "copilot-swe-agent", "id": bot_id, "__typename": "Bot"}
                    ]
                }
            }
        }
    }


def make_assignment_response() -> dict:
    """Helper to create a valid assignment mutation response."""
    return {
        "data": {
            "addAssigneesToAssignable": {
                "assignable": {
                    "id": "ISSUE_456",
                    "title": "Test Issue",
                    "assignees": {
                        "nodes": [{"login": "copilot-swe-agent"}]
                    }
                }
            }
        }
    }


# =============================================================================
# TestAssignToCopilot
# =============================================================================

class TestAssignToCopilot:
    """Test assign_to_copilot method with GraphQL API (2-call flow)."""

    def _mock_subprocess_calls(self, prereq_response, assign_response):
        """Helper to mock the sequence of subprocess calls for assign_to_copilot.
        
        The optimized flow uses 2 GraphQL calls:
        1. Prerequisites lookup (bot ID + repo ID + issue ID)
        2. Assignment mutation
        """
        responses = [
            make_subprocess_result(0, json.dumps(prereq_response)),
            make_subprocess_result(0, json.dumps(assign_response)),
        ]
        return responses

    # Test: Successful assignments with different parameters
    @pytest.mark.parametrize("issue_num,base_branch,instructions", [
        (1, "main", ""),
        (42, "main", ""),
        (999, "develop", "Fix the bug"),
        (123, "feature/test", "Please follow the style guide"),
        (1, "main", "Multi\nline\ninstructions"),
    ], ids=["minimal", "standard", "with_branch", "with_instructions", "multiline_instructions"])
    def test_successful_assignment(self, issue_num, base_branch, instructions):
        """Test successful issue assignment to Copilot via GraphQL."""
        from services.copilot_service import CopilotService
        
        responses = self._mock_subprocess_calls(
            make_prerequisites_response(),
            make_assignment_response()
        )
        
        with patch('subprocess.run', side_effect=responses):
            service = CopilotService()
            result = service.assign_to_copilot(
                "owner", "repo", issue_num,
                custom_instructions=instructions,
                base_branch=base_branch
            )
            
            assert result["success"] is True
            assert result["issue_number"] == issue_num
            assert result["assigned_to"] == "copilot-swe-agent[bot]"
            assert result["base_branch"] == base_branch

    # Test: GraphQL mutation is called with correct structure
    def test_graphql_mutation_called(self):
        """Verify that GraphQL mutation is called with proper structure."""
        from services.copilot_service import CopilotService
        
        responses = self._mock_subprocess_calls(
            make_prerequisites_response("BOT_XYZ", "REPO_ABC", "ISSUE_DEF"),
            make_assignment_response()
        )
        
        with patch('subprocess.run', side_effect=responses) as mock_run:
            service = CopilotService()
            service.assign_to_copilot(
                "microsoft", "Agent365-devTools", 42,
                custom_instructions="Fix it",
                base_branch="develop"
            )
            
            # Second call should be the mutation (optimized 2-call flow)
            mutation_call = mock_run.call_args_list[1]
            call_args = mutation_call[0][0]
            
            # Verify GraphQL Features header is included
            assert '-H' in call_args
            header_index = call_args.index('-H')
            header_value = call_args[header_index + 1]
            assert 'issues_copilot_assignment_api_support' in header_value

    # Test: Copilot not available for repository
    def test_copilot_not_available(self):
        """Test handling when Copilot is not enabled for the repository."""
        from services.copilot_service import CopilotService
        
        # Return response with no copilot-swe-agent in actors (combined query)
        no_copilot_response = {
            "data": {
                "repository": {
                    "id": "REPO_123",
                    "issue": {"id": "ISSUE_456"},
                    "suggestedActors": {
                        "nodes": [{"login": "some-user", "id": "USER_1"}]
                    }
                }
            }
        }
        mock_result = make_subprocess_result(0, json.dumps(no_copilot_response))
        
        with patch('subprocess.run', return_value=mock_result):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False
            assert "not available" in result["error"]
            assert result["issue_number"] == 42

    # Test: Failed prerequisites lookup (API error)
    def test_prerequisites_lookup_failure(self):
        """Test handling when prerequisites lookup fails with API error."""
        from services.copilot_service import CopilotService
        
        mock_result = make_subprocess_result(1, "", "API error")
        
        with patch('subprocess.run', return_value=mock_result):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False
            assert result["issue_number"] == 42
            # Should reflect the actual API failure, not generic "Copilot not available"
            assert "Failed to query prerequisites" in result["error"] or "API error" in result["error"]

    # Test: Issue not found in prerequisites response
    def test_issue_not_found_in_prerequisites(self):
        """Test handling when issue is not found in combined prerequisites lookup."""
        from services.copilot_service import CopilotService
        
        # Issue is null but bot and repo are valid
        missing_issue_response = {
            "data": {
                "repository": {
                    "id": "REPO_123",
                    "issue": None,
                    "suggestedActors": {
                        "nodes": [{"login": "copilot-swe-agent", "id": "BOT_123", "__typename": "Bot"}]
                    }
                }
            }
        }
        mock_result = make_subprocess_result(0, json.dumps(missing_issue_response))
        
        with patch('subprocess.run', return_value=mock_result):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False
            assert result["issue_number"] == 42
            assert "issue ID" in result["error"]

    # Test: Repository not found in prerequisites response
    def test_repo_not_found_in_prerequisites(self):
        """Test handling when repository is not found - specific error message."""
        from services.copilot_service import CopilotService
        
        # Repository is null
        null_repo_response = {
            "data": {
                "repository": None
            }
        }
        mock_result = make_subprocess_result(0, json.dumps(null_repo_response))
        
        with patch('subprocess.run', return_value=mock_result):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False
            assert result["issue_number"] == 42
            # When repo is null, bot_id won't be found, so error should be about Copilot not available
            assert "error" in result
            assert "not available" in result["error"] or "repository" in result["error"].lower()

    # Test: GraphQL mutation failure
    def test_mutation_failure(self):
        """Test handling when GraphQL mutation fails."""
        from services.copilot_service import CopilotService
        
        responses = [
            make_subprocess_result(0, json.dumps(make_prerequisites_response())),
            make_subprocess_result(1, "", "Forbidden"),
        ]
        
        with patch('subprocess.run', side_effect=responses):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False
            assert "Forbidden" in result["error"]
            assert result["issue_number"] == 42

    # Test: GraphQL errors in response
    def test_graphql_errors_in_response(self):
        """Test handling of GraphQL errors in the response body."""
        from services.copilot_service import CopilotService
        
        error_response = {
            "errors": [{"message": "You don't have permission to assign issues"}]
        }
        
        responses = [
            make_subprocess_result(0, json.dumps(make_prerequisites_response())),
            make_subprocess_result(0, json.dumps(error_response)),
        ]
        
        with patch('subprocess.run', side_effect=responses):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False
            assert "permission" in result["error"]
            assert result["issue_number"] == 42

    # Test: Multiple GraphQL errors are aggregated
    def test_multiple_graphql_errors_aggregated(self):
        """Test that multiple GraphQL errors are aggregated into a single message."""
        from services.copilot_service import CopilotService
        
        multi_error_response = {
            "errors": [
                {"message": "First error", "path": ["addAssigneesToAssignable", "assignable"]},
                {"message": "Second error"},
                {"message": "Third error", "path": ["mutation", "field"]}
            ]
        }
        
        responses = [
            make_subprocess_result(0, json.dumps(make_prerequisites_response())),
            make_subprocess_result(0, json.dumps(multi_error_response)),
        ]
        
        with patch('subprocess.run', side_effect=responses):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False
            assert result["issue_number"] == 42
            # All errors should be in the message
            assert "First error" in result["error"]
            assert "Second error" in result["error"]
            assert "Third error" in result["error"]
            # Path should be included for errors that have it
            assert "path:" in result["error"]

    # Test: Timeout during assignment
    def test_timeout_handling(self):
        """Test handling of timeout during assignment."""
        from services.copilot_service import CopilotService
        
        with patch('subprocess.run', side_effect=subprocess.TimeoutExpired(cmd='gh', timeout=60)):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False
            assert result["issue_number"] == 42

    # Test: General exception handling  
    def test_exception_handling(self):
        """Test handling of unexpected exceptions."""
        from services.copilot_service import CopilotService
        
        with patch('subprocess.run', side_effect=Exception("Unexpected error")):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False
            assert result["issue_number"] == 42

    # =========================================================================
    # Regression tests for nullable GraphQL responses in prerequisites lookup
    # These test cases ensure _get_assignment_prerequisites handles nulls gracefully.
    # =========================================================================

    @pytest.mark.parametrize("response,description", [
        ({"data": {"repository": None}}, "null repository"),
        ({"data": {"repository": {"id": "R1", "issue": {"id": "I1"}, "suggestedActors": None}}}, "null suggestedActors"),
        ({"data": {"repository": {"id": "R1", "issue": {"id": "I1"}, "suggestedActors": {"nodes": None}}}}, "null nodes"),
        ({"data": {"repository": {"id": "R1", "issue": {"id": "I1"}, "suggestedActors": {"nodes": [None]}}}}, "null actor in nodes"),
        ({"data": None}, "null data"),
        ({}, "empty response"),
    ], ids=["null_repo", "null_actors", "null_nodes", "null_actor_item", "null_data", "empty"])
    def test_prerequisites_nullable_response_variations(self, response, description):
        """Parameterized test for nullable fields in combined prerequisites lookup.
        
        All these cases should fail gracefully without raising exceptions.
        """
        from services.copilot_service import CopilotService
        
        mock_result = make_subprocess_result(0, json.dumps(response))
        
        with patch('subprocess.run', return_value=mock_result):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False, f"Should fail gracefully for prerequisites lookup: {description}"
            assert result["issue_number"] == 42

    # =========================================================================
    # Regression tests for nullable issue/repository in prerequisites response
    # These test cases ensure graceful failure when API returns null for
    # repository or issue, without raising unexpected exceptions.
    # =========================================================================

    def test_null_repository_in_response(self):
        """Test graceful handling when repository is null in GraphQL response.
        
        Regression test: When the API returns {"data": {"repository": null}},
        the code should fail gracefully without raising AttributeError.
        """
        from services.copilot_service import CopilotService
        
        null_repo_response = {
            "data": {
                "repository": None  # Repository not found or no access
            }
        }
        mock_result = make_subprocess_result(0, json.dumps(null_repo_response))
        
        with patch('subprocess.run', return_value=mock_result):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False
            assert result["issue_number"] == 42
            # Should have a clear error, not an exception traceback
            assert "error" in result

    def test_null_issue_in_response(self):
        """Test graceful handling when issue is null in GraphQL response.
        
        Regression test: When the API returns {"data": {"repository": {"id": "...", "issue": null}}},
        the code should fail gracefully without raising AttributeError.
        """
        from services.copilot_service import CopilotService
        
        null_issue_response = {
            "data": {
                "repository": {
                    "id": "REPO_123",
                    "issue": None,  # Issue not found
                    "suggestedActors": {
                        "nodes": [{"login": "copilot-swe-agent", "id": "BOT_123", "__typename": "Bot"}]
                    }
                }
            }
        }
        mock_result = make_subprocess_result(0, json.dumps(null_issue_response))
        
        with patch('subprocess.run', return_value=mock_result):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False
            assert result["issue_number"] == 42
            assert "error" in result

    def test_null_data_in_response(self):
        """Test graceful handling when data is null in GraphQL response."""
        from services.copilot_service import CopilotService
        
        null_data_response = {
            "data": None
        }
        mock_result = make_subprocess_result(0, json.dumps(null_data_response))
        
        with patch('subprocess.run', return_value=mock_result):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False
            assert result["issue_number"] == 42

    def test_missing_issue_id_in_response(self):
        """Test graceful handling when issue object exists but has no id field."""
        from services.copilot_service import CopilotService
        
        missing_id_response = {
            "data": {
                "repository": {
                    "id": "REPO_123",
                    "issue": {},  # Issue object exists but no id
                    "suggestedActors": {
                        "nodes": [{"login": "copilot-swe-agent", "id": "BOT_123", "__typename": "Bot"}]
                    }
                }
            }
        }
        mock_result = make_subprocess_result(0, json.dumps(missing_id_response))
        
        with patch('subprocess.run', return_value=mock_result):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False
            assert result["issue_number"] == 42

    @pytest.mark.parametrize("response,description", [
        ({"data": {"repository": None}}, "null repository"),
        ({"data": {"repository": {"id": "R1", "issue": None, "suggestedActors": {"nodes": [{"login": "copilot-swe-agent", "id": "B1"}]}}}}, "null issue"),
        ({"data": None}, "null data"),
        ({}, "empty response"),
        ({"data": {}}, "empty data object"),
        ({"data": {"repository": {}}}, "repository without id"),
    ], ids=["null_repo", "null_issue", "null_data", "empty", "empty_data", "no_repo_id"])
    def test_nullable_response_variations(self, response, description):
        """Parameterized test for various nullable/missing field scenarios.
        
        All these cases should fail gracefully without raising exceptions.
        """
        from services.copilot_service import CopilotService
        
        mock_result = make_subprocess_result(0, json.dumps(response))
        
        with patch('subprocess.run', return_value=mock_result):
            service = CopilotService()
            result = service.assign_to_copilot("owner", "repo", 42)
            
            assert result["success"] is False, f"Should fail gracefully for: {description}"
            assert result["issue_number"] == 42

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
