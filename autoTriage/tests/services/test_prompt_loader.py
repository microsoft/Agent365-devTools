# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Tests for prompt_loader.py

Tests the PromptLoader including:
- Loading prompts from YAML
- Prompt variable substitution
"""
import pytest
import sys
from pathlib import Path
from unittest.mock import patch, mock_open

sys.path.insert(0, str(Path(__file__).parent.parent.parent))


class TestPromptLoaderInitialization:
    """Test PromptLoader initialization."""

    def test_prompt_loader_initializes(self):
        """Test PromptLoader initializes successfully."""
        from services.prompt_loader import PromptLoader
        
        mock_yaml_content = """
        classification:
          system: "You are a classifier."
          user: "Classify this: {issue_title}"
        """
        
        with patch('builtins.open', mock_open(read_data=mock_yaml_content)):
            with patch.object(Path, 'exists', return_value=True):
                loader = PromptLoader()
                assert loader is not None


class TestPromptLoading:
    """Test loading prompts from YAML."""

    def test_load_classification_prompt(self):
        """Test loading classification prompt."""
        from services.prompt_loader import PromptLoader
        
        loader = PromptLoader()
        
        # PromptLoader uses .get() method
        prompt = loader.get('classification', '')
        # Will be empty string if not found, or the prompt text
        assert isinstance(prompt, str)

    def test_load_prompt_with_default(self):
        """Test loading prompt with default value."""
        from services.prompt_loader import PromptLoader
        
        loader = PromptLoader()
        
        default = "Default prompt text"
        prompt = loader.get('nonexistent_prompt', default)
        assert prompt == default


class TestVariableSubstitution:
    """Test prompt variable substitution."""

    def test_substitute_single_variable(self):
        """Test substituting a single variable in prompt."""
        template = "Classify this: {issue_title}"
        result = template.format(issue_title="Test Issue")
        assert result == "Classify this: Test Issue"

    def test_substitute_multiple_variables(self):
        """Test substituting multiple variables in prompt."""
        template = "Issue #{issue_number}: {issue_title} by {author}"
        result = template.format(
            issue_number=123,
            issue_title="Test Issue",
            author="testuser"
        )
        assert result == "Issue #123: Test Issue by testuser"

    def test_missing_variable_raises_error(self):
        """Test that missing variables raise KeyError."""
        template = "Issue: {issue_title} - {missing_var}"
        
        with pytest.raises(KeyError):
            template.format(issue_title="Test")


class TestPromptCaching:
    """Test prompt caching behavior."""

    def test_prompt_loader_is_singleton(self):
        """Test that PromptLoader is a singleton."""
        from services.prompt_loader import PromptLoader
        
        loader1 = PromptLoader()
        loader2 = PromptLoader()
        
        # Both should be the same instance
        assert loader1 is loader2

    def test_prompts_available_after_init(self):
        """Test that prompts are available after initialization."""
        from services.prompt_loader import PromptLoader
        
        loader = PromptLoader()
        
        # _prompts should be a dict (possibly empty if no prompts.yaml)
        assert hasattr(loader, '_prompts')
        assert isinstance(loader._prompts, dict)
