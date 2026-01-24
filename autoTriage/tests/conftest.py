# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Pytest configuration and shared fixtures for autoTriage tests
"""
import sys
import os
from pathlib import Path
import pytest
import json
from unittest.mock import Mock, MagicMock

# Add parent directory to path for imports
sys.path.insert(0, str(Path(__file__).parent.parent))


@pytest.fixture
def mock_github_token():
    """Mock GITHUB_TOKEN environment variable."""
    original = os.environ.get('GITHUB_TOKEN')
    os.environ['GITHUB_TOKEN'] = 'test_token_12345'
    yield 'test_token_12345'
    if original:
        os.environ['GITHUB_TOKEN'] = original
    else:
        os.environ.pop('GITHUB_TOKEN', None)


@pytest.fixture
def mock_config_dir(tmp_path):
    """Create a temporary config directory with test files."""
    config_dir = tmp_path / "config"
    config_dir.mkdir()

    # Create test team-members.json
    team_members = {
        "team_members": [
            {
                "name": "Test Engineer 1",
                "role": "Senior Engineer",
                "login": "test_user1",
                "contributions": 10,
                "expertise": ["backend", "api"]
            },
            {
                "name": "Test Engineer 2",
                "role": "Engineer",
                "login": "test_user2",
                "contributions": 5,
                "expertise": ["frontend"]
            }
        ]
    }

    team_file = config_dir / "team-members.json"
    with open(team_file, 'w') as f:
        json.dump(team_members, f, indent=2)

    return config_dir


@pytest.fixture
def sample_team_members():
    """Sample team members data for testing."""
    return [
        {
            "name": "Test Engineer 1",
            "role": "Senior Engineer",
            "login": "test_user1",
            "contributions": 10,
            "expertise": ["backend", "api"]
        },
        {
            "name": "Test Engineer 2",
            "role": "Engineer",
            "login": "test_user2",
            "contributions": 5,
            "expertise": ["frontend"]
        }
    ]


@pytest.fixture
def mock_github_client():
    """Mock GitHub client for testing."""
    mock_client = MagicMock()

    # Mock repository
    mock_repo = MagicMock()
    mock_repo.name = "test-repo"
    mock_repo.full_name = "test-owner/test-repo"
    mock_repo.default_branch = "main"

    # Mock issue
    mock_issue = MagicMock()
    mock_issue.number = 123
    mock_issue.title = "Test issue"
    mock_issue.body = "This is a test issue"
    mock_issue.state = "open"
    mock_issue.labels = []

    mock_client.get_repo.return_value = mock_repo
    mock_repo.get_issue.return_value = mock_issue

    return mock_client


@pytest.fixture
def mock_llm_response():
    """Mock LLM response for testing."""
    return {
        "type": "bug",
        "priority": "P2",
        "type_rationale": "Issue describes an error condition",
        "priority_rationale": "Impact is moderate",
        "confidence": 0.85
    }


@pytest.fixture
def sample_issue_data():
    """Sample issue data for testing."""
    return {
        "number": 123,
        "title": "Bug: Application crashes on startup",
        "body": "The application crashes when starting up with error code 500",
        "state": "open",
        "labels": [],
        "assignee": None
    }
