# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Tests for llm_service.py

Tests the LLMService including:
- Initialization with different API configurations
- Issue classification
- Assignee selection
- Fallback behavior when LLM is unavailable
"""
import pytest
import sys
from pathlib import Path
from unittest.mock import Mock, MagicMock, patch
import os

sys.path.insert(0, str(Path(__file__).parent.parent))

from services.llm_service import LlmService
from models.team_config import PriorityRules, CopilotFixableConfig


class TestLLMServiceInitialization:
    """Test suite for LLMService initialization."""

    def test_llm_service_initializes_with_github_models(self):
        """Test LLM service initializes with GitHub Models configuration."""
        with patch.dict(os.environ, {
            'GITHUB_TOKEN': 'test_token',
            'GITHUB_MODELS_ENDPOINT': 'https://models.inference.ai.azure.com',
            'GITHUB_MODELS_MODEL': 'gpt-4o-mini'
        }, clear=True):
            service = LlmService()

            assert service._client is not None
            assert service.model == 'gpt-4o-mini'

    def test_llm_service_initializes_with_azure_openai(self):
        """Test LLM service initializes with Azure OpenAI configuration."""
        with patch.dict(os.environ, {
            'AZURE_OPENAI_ENDPOINT': 'https://test.openai.azure.com',
            'AZURE_OPENAI_API_KEY': 'test_key',
            'AZURE_OPENAI_DEPLOYMENT': 'gpt-4'
        }, clear=True):
            service = LlmService()

            assert service._client is not None
            assert service.model == 'gpt-4'

    def test_llm_service_handles_missing_api_keys(self):
        """Test LLM service handles missing API keys gracefully."""
        with patch.dict(os.environ, {}, clear=True):
            service = LlmService()

            # Should initialize but client might be None
            assert service is not None


class TestLLMServiceClassification:
    """Test suite for LLM issue classification."""

    @patch.dict(os.environ, {'GITHUB_TOKEN': 'test_token'}, clear=True)
    def test_classify_issue_with_mock_llm_response(self):
        """Test issue classification with mocked LLM response."""
        service = LlmService()

        # Mock the LLM call
        mock_response = """{
            "type": "bug",
            "priority": "P2",
            "type_rationale": "Issue describes an error",
            "priority_rationale": "Moderate impact",
            "confidence": 0.85
        }"""

        service._call_llm = Mock(return_value=mock_response)

        rules = PriorityRules(
            p0_keywords=["crash"],
            p1_keywords=["bug"],
            p2_keywords=["feature"],
            default_priority="P3"
        )

        result = service.classify_issue(
            title="Bug: Application crashes",
            body="The app crashes on startup",
            rules=rules
        )

        assert result["type"] == "bug"
        assert result["priority"] == "P2"
        assert result["confidence"] == 0.85

    @patch.dict(os.environ, {'GITHUB_TOKEN': 'test_token'}, clear=True)
    def test_classify_issue_fallback_on_llm_failure(self):
        """Test issue classification falls back to keyword matching on LLM failure."""
        service = LlmService()
        service._call_llm = Mock(return_value=None)  # Simulate LLM failure

        rules = PriorityRules(
            p0_keywords=["crash", "outage"],
            p1_keywords=["bug", "error"],
            p2_keywords=["enhancement"],
            default_priority="P3"
        )

        result = service.classify_issue(
            title="Bug in login feature",
            body="Users cannot log in",
            rules=rules
        )

        # Should fallback to keyword matching
        assert result["type"] in ["bug", "feature", "documentation", "question"]
        assert result["priority"] in ["P1", "P2", "P3", "P4"]

    @patch.dict(os.environ, {'GITHUB_TOKEN': 'test_token'}, clear=True)
    def test_classify_issue_handles_invalid_json_response(self):
        """Test classification handles invalid JSON from LLM."""
        service = LlmService()
        service._call_llm = Mock(return_value="Invalid JSON response")

        rules = PriorityRules(
            p0_keywords=["crash"],
            p1_keywords=["bug"],
            p2_keywords=["feature"],
            default_priority="P3"
        )

        result = service.classify_issue(
            title="Some issue",
            body="Issue description",
            rules=rules
        )

        # Should fallback to keyword matching
        assert "type" in result
        assert "priority" in result


class TestLLMServiceAssigneeSelection:
    """Test suite for LLM assignee selection."""

    @patch.dict(os.environ, {'GITHUB_TOKEN': 'test_token'}, clear=True)
    def test_select_assignee_with_mock_llm_response(self, sample_team_members):
        """Test assignee selection with mocked LLM response."""
        service = LlmService()

        mock_response = """{
            "assignee": "test_user1",
            "rationale": "User has backend expertise",
            "confidence": 0.9
        }"""

        service._call_llm = Mock(return_value=mock_response)

        result = service.select_assignee(
            title="Backend API bug",
            body="API endpoint returns 500 error",
            issue_type="bug",
            priority="P2",
            team_members=sample_team_members
        )

        assert result["assignee"] == "test_user1"
        assert result["confidence"] == 0.9

    @patch.dict(os.environ, {'GITHUB_TOKEN': 'test_token'}, clear=True)
    def test_select_assignee_with_contributor_context(self, sample_team_members):
        """Test assignee selection includes file contributor context."""
        service = LlmService()

        mock_response = """{
            "assignee": "test_user1",
            "rationale": "User recently committed to mentioned file",
            "confidence": 0.95
        }"""

        service._call_llm = Mock(return_value=mock_response)

        file_contributors = {
            "src/api/endpoint.py": {
                "test_user1": 15,
                "test_user2": 3
            }
        }

        result = service.select_assignee(
            title="Bug in src/api/endpoint.py",
            body="Endpoint returns wrong data",
            issue_type="bug",
            priority="P2",
            team_members=sample_team_members,
            file_contributors=file_contributors
        )

        assert result["assignee"] == "test_user1"

        # Verify the LLM was called with contributor context
        call_args = service._call_llm.call_args
        assert call_args is not None
        user_prompt = call_args[0][1]  # Second argument is user_prompt
        assert "src/api/endpoint.py" in user_prompt
        assert "test_user1" in user_prompt

    @patch.dict(os.environ, {'GITHUB_TOKEN': 'test_token'}, clear=True)
    def test_select_assignee_handles_llm_failure(self, sample_team_members):
        """Test assignee selection handles LLM failure gracefully."""
        service = LlmService()
        service._call_llm = Mock(return_value=None)

        result = service.select_assignee(
            title="Some bug",
            body="Bug description",
            issue_type="bug",
            priority="P2",
            team_members=sample_team_members
        )

        # Should return default assignee with fallback when LLM fails
        assert "assignee" in result
        assert result["assignee"] is not None


class TestLLMServiceCopilotFixable:
    """Test suite for Copilot fixable assessment."""

    @patch.dict(os.environ, {'GITHUB_TOKEN': 'test_token'}, clear=True)
    def test_is_copilot_fixable_with_fixable_issue(self):
        """Test Copilot fixable assessment for fixable issue."""
        service = LlmService()

        mock_response = """{
            "is_copilot_fixable": true,
            "reasoning": "Simple typo fix",
            "confidence": 0.9
        }"""

        service._call_llm = Mock(return_value=mock_response)

        config = CopilotFixableConfig(
            enabled=True,
            criteria=["typo", "documentation"],
            max_issues_per_day=5
        )

        result = service.is_copilot_fixable(
            title="Typo in documentation",
            body="Fix spelling error",
            config=config,
            issue_type="documentation",
            priority="P4"
        )

        assert result["is_copilot_fixable"] is True
        assert "typo" in result["reasoning"].lower()
        assert result["confidence"] == 0.9

    @patch.dict(os.environ, {'GITHUB_TOKEN': 'test_token'}, clear=True)
    def test_is_copilot_fixable_with_complex_issue(self):
        """Test Copilot fixable assessment for complex issue."""
        service = LlmService()

        mock_response = """{
            "is_copilot_fixable": false,
            "reasoning": "Requires architectural changes",
            "confidence": 0.85
        }"""

        service._call_llm = Mock(return_value=mock_response)

        config = CopilotFixableConfig(
            enabled=True,
            criteria=["typo", "simple fix"],
            max_issues_per_day=5
        )

        result = service.is_copilot_fixable(
            title="Refactor authentication system",
            body="Need to redesign auth flow",
            config=config,
            issue_type="feature",
            priority="P1"
        )

        assert result["is_copilot_fixable"] is False
        assert "architectural" in result["reasoning"].lower()


class TestLLMServicePromptBuilding:
    """Test suite for prompt building and formatting."""

    @patch.dict(os.environ, {'GITHUB_TOKEN': 'test_token'}, clear=True)
    def test_select_assignee_formats_team_members_correctly(self, sample_team_members):
        """Test that team members are formatted correctly in prompts."""
        service = LlmService()

        mock_response = '{"assignee": "test_user1", "rationale": "test", "confidence": 0.9}'
        service._call_llm = Mock(return_value=mock_response)

        service.select_assignee(
            title="Test issue",
            body="Test body",
            issue_type="bug",
            priority="P2",
            team_members=sample_team_members
        )

        # Verify the LLM was called
        assert service._call_llm.called

        # Check that team members were included in the prompt
        call_args = service._call_llm.call_args
        user_prompt = call_args[0][1]
        assert "test_user1" in user_prompt or "Test Engineer 1" in user_prompt

    @patch.dict(os.environ, {'GITHUB_TOKEN': 'test_token'}, clear=True)
    def test_call_llm_includes_json_format_when_requested(self):
        """Test that _call_llm includes response_format for JSON responses."""
        service = LlmService()

        if service._client is None:
            pytest.skip("No LLM client available")

        with patch.object(service._client.chat.completions, 'create') as mock_create:
            mock_response = Mock()
            mock_response.choices = [Mock()]
            mock_response.choices[0].message.content = '{"result": "test"}'
            mock_create.return_value = mock_response

            service._call_llm("system prompt", "user prompt", json_response=True)

            # Verify response_format was included
            call_kwargs = mock_create.call_args[1]
            assert "response_format" in call_kwargs
            assert call_kwargs["response_format"]["type"] == "json_object"

    @patch.dict(os.environ, {'GITHUB_TOKEN': 'test_token'}, clear=True)
    def test_call_llm_omits_response_format_for_text(self):
        """Test that _call_llm omits response_format for text responses."""
        service = LlmService()

        if service._client is None:
            pytest.skip("No LLM client available")

        with patch.object(service._client.chat.completions, 'create') as mock_create:
            mock_response = Mock()
            mock_response.choices = [Mock()]
            mock_response.choices[0].message.content = 'Text response'
            mock_create.return_value = mock_response

            service._call_llm("system prompt", "user prompt", json_response=False)

            # Verify response_format was NOT included
            call_kwargs = mock_create.call_args[1]
            assert "response_format" not in call_kwargs
