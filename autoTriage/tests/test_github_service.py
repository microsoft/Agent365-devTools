# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Tests for github_service.py

Tests the GitHubService including:
- File path extraction from issue text
- Contributor tracking
- Repository context fetching
- Caching behavior
"""
import pytest
import sys
from pathlib import Path
from unittest.mock import Mock, MagicMock, patch
from datetime import datetime, timezone

sys.path.insert(0, str(Path(__file__).parent.parent))

from services.github_service import GitHubService


class TestGitHubService:
    """Test suite for GitHubService class."""

    def test_extract_file_paths_from_text_with_valid_paths(self):
        """Test extracting file paths from issue text."""
        service = GitHubService.__new__(GitHubService)  # Create instance without __init__

        text = """
        I found a bug in src/services/github_service.py
        Also affects autoTriage/config/prompts.yaml
        """

        paths = service.extract_file_paths_from_text(text)

        assert len(paths) > 0
        assert "src/services/github_service.py" in paths
        assert "autoTriage/config/prompts.yaml" in paths

    def test_extract_file_paths_from_text_filters_urls(self):
        """Test that URLs are filtered out from file path extraction."""
        service = GitHubService.__new__(GitHubService)

        text = """
        Check https://github.com/owner/repo/issues/123
        Also see http://example.com/path/to/file.txt
        But this is a real file: src/main.py
        """

        paths = service.extract_file_paths_from_text(text)

        # Should not include URLs
        assert not any("http://" in path for path in paths)
        assert not any("https://" in path for path in paths)
        # Should include actual file paths
        assert "src/main.py" in paths

    def test_extract_file_paths_from_text_with_no_paths(self):
        """Test extracting file paths when none exist."""
        service = GitHubService.__new__(GitHubService)

        text = "This is just a regular issue with no file paths mentioned"

        paths = service.extract_file_paths_from_text(text)

        assert len(paths) == 0

    def test_extract_file_paths_deduplicates_paths(self):
        """Test that duplicate file paths are removed."""
        service = GitHubService.__new__(GitHubService)

        text = """
        Error in src/main.py
        Also check src/main.py again
        And once more src/main.py
        """

        paths = service.extract_file_paths_from_text(text)

        # Should only have one instance
        assert paths.count("src/main.py") == 1

    def test_extract_file_paths_limits_results(self):
        """Test that file path extraction is limited to MAX_FILE_PATHS_TO_EXTRACT."""
        service = GitHubService.__new__(GitHubService)

        # Create text with many file paths
        file_paths = [f"src/file{i}.py" for i in range(20)]
        text = " ".join(file_paths)

        paths = service.extract_file_paths_from_text(text)

        # Should be limited to 5 (MAX_FILE_PATHS_TO_EXTRACT)
        assert len(paths) <= 5

    @patch('services.github_service.Github')
    def test_github_service_initialization_with_token(self, mock_github, mock_github_token):
        """Test GitHubService initializes with token."""
        mock_client = MagicMock()
        mock_github.return_value = mock_client

        service = GitHubService()

        assert service.client is not None
        mock_github.assert_called_once()

    def test_get_contributors_for_issue_with_no_paths(self):
        """Test get_contributors_for_issue with no file paths in issue."""
        with patch('services.github_service.Github'):
            service = GitHubService()
            service.extract_file_paths_from_text = Mock(return_value=[])

            result = service.get_contributors_for_issue(
                owner="test-owner",
                repo="test-repo",
                issue_title="Bug report",
                issue_body="No file paths here"
            )

            assert result == {}

    def test_get_contributors_for_issue_with_paths(self):
        """Test get_contributors_for_issue with file paths."""
        with patch('services.github_service.Github'):
            service = GitHubService()
            service.extract_file_paths_from_text = Mock(return_value=["src/main.py"])
            service.get_file_contributors = Mock(return_value={"user1": 10, "user2": 5})

            result = service.get_contributors_for_issue(
                owner="test-owner",
                repo="test-repo",
                issue_title="Bug in src/main.py",
                issue_body="File has issues"
            )

            assert "src/main.py" in result
            assert result["src/main.py"]["user1"] == 10
            assert result["src/main.py"]["user2"] == 5


class TestGitHubServiceCaching:
    """Test suite for GitHubService caching behavior."""

    def test_cache_stores_and_retrieves_values(self):
        """Test that cache correctly stores and retrieves values."""
        from services.github_service import _set_cached, _get_cached

        # Set a cached value
        _set_cached("test_key", "test_value", ttl=3600)

        # Retrieve it
        cached_value = _get_cached("test_key")

        assert cached_value == "test_value"

    def test_cache_returns_none_for_missing_keys(self):
        """Test that cache returns None for keys that don't exist."""
        from services.github_service import _get_cached

        cached_value = _get_cached("nonexistent_key_12345")

        assert cached_value is None

    def test_cache_expires_after_ttl(self):
        """Test that cached values expire after TTL."""
        from services.github_service import _set_cached, _get_cached

        # Set a cached value with 0 second TTL (immediate expiration)
        _set_cached("test_key_expiry", "test_value", ttl=0)

        # Should return None because it expired
        cached_value = _get_cached("test_key_expiry")

        assert cached_value is None


class TestGitHubServiceFilePathExtraction:
    """Test suite for file path extraction edge cases."""

    def test_extract_file_paths_with_windows_paths(self):
        """Test that Windows-style paths are not extracted (GitHub uses Unix paths)."""
        service = GitHubService.__new__(GitHubService)

        text = r"Error in C:\Users\test\file.py"

        paths = service.extract_file_paths_from_text(text)

        # Should not extract Windows paths
        assert len(paths) == 0

    def test_extract_file_paths_with_relative_paths(self):
        """Test extraction of relative file paths."""
        service = GitHubService.__new__(GitHubService)

        text = """
        Bug in ./src/main.py
        Also affects ../config/settings.json
        """

        paths = service.extract_file_paths_from_text(text)

        # Should extract relative paths
        assert any("src/main.py" in path for path in paths) or any("config/settings.json" in path for path in paths)

    def test_extract_file_paths_with_various_extensions(self):
        """Test extraction of files with different extensions."""
        service = GitHubService.__new__(GitHubService)

        text = """
        Files affected:
        - src/main.py (Python)
        - config/app.json (JSON)
        - docs/README.md (Markdown)
        - tests/test.ts (TypeScript)
        - styles/app.css (CSS)
        """

        paths = service.extract_file_paths_from_text(text)

        # Should extract files with various extensions
        assert len(paths) > 0
        assert any(".py" in path for path in paths)
        assert any(".json" in path for path in paths)
        assert any(".md" in path for path in paths)
