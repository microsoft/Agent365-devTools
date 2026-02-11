# AutoTriage Tests

This directory contains tests for the autoTriage services.

## Running Tests

### Install dependencies

```bash
cd autoTriage
pip install -r requirements.txt
```

### Run all tests

```bash
cd autoTriage
pytest
```

### Run specific test file

```bash
pytest tests/test_config_parser.py
```

### Run tests with coverage

```bash
pytest --cov=services --cov=models --cov-report=html
```

### Run tests with verbose output

```bash
pytest -v
```

## Test Structure

- `conftest.py` - Shared pytest fixtures and configuration
- `test_config_parser.py` - Tests for configuration parsing (includes bug fix verification)
- `test_github_service.py` - Tests for GitHub API service
- `test_llm_service.py` - Tests for LLM/AI service

## Test Coverage

Current test coverage includes:

### ConfigParser (`test_config_parser.py`)
- ✅ Default config loading (bug fix verification)
- ✅ Priority rules parsing
- ✅ Triage metadata parsing
- ✅ Copilot fixable configuration
- ✅ ADO config parsing
- ✅ YAML parsing

### GitHubService (`test_github_service.py`)
- ✅ File path extraction from issue text
- ✅ URL filtering
- ✅ Contributor tracking
- ✅ Caching behavior
- ✅ Edge cases (Windows paths, relative paths, various extensions)

### LLMService (`test_llm_service.py`)
- ✅ Initialization with GitHub Models and Azure OpenAI
- ✅ Issue classification
- ✅ Assignee selection with contributor context
- ✅ Copilot fixable assessment
- ✅ Fallback behavior when LLM is unavailable
- ✅ Prompt building and formatting

## Writing New Tests

When adding new tests:

1. Create a new test file with prefix `test_`
2. Use fixtures from `conftest.py` for common setup
3. Mock external dependencies (GitHub API, Azure OpenAI)
4. Follow the existing test structure and naming conventions
5. Add docstrings explaining what each test verifies

### Example Test

```python
def test_my_feature(sample_team_members):
    """Test description of what this verifies."""
    # Arrange
    service = MyService()

    # Act
    result = service.do_something(sample_team_members)

    # Assert
    assert result is not None
    assert result == expected_value
```

## Continuous Integration

These tests run automatically in GitHub Actions on:
- Pull requests
- Pushes to main branch
- Manual workflow dispatch

See `.github/workflows/test.yml` for CI configuration.

## Test Markers

Tests can be marked with custom markers:

```python
@pytest.mark.unit
def test_unit_function():
    pass

@pytest.mark.integration
def test_integration_flow():
    pass

@pytest.mark.slow
def test_slow_operation():
    pass

@pytest.mark.requires_api
def test_with_real_api():
    pass
```

Run specific markers:

```bash
pytest -m unit           # Run only unit tests
pytest -m "not slow"     # Skip slow tests
pytest -m requires_api   # Run only API tests
```

## Mocking External Dependencies

Always mock external dependencies in tests:

```python
from unittest.mock import Mock, patch

@patch('services.github_service.Github')
def test_with_mock_github(mock_github):
    mock_github.return_value = MagicMock()
    # Test code here
```

## Test Data

Use fixtures for test data:

```python
@pytest.fixture
def sample_issue():
    return {
        "number": 123,
        "title": "Test issue",
        "body": "Issue description"
    }
```

## Debugging Tests

Run tests with debugging output:

```bash
pytest -vv --tb=long     # Very verbose with full tracebacks
pytest -s                # Show print statements
pytest --pdb             # Drop into debugger on failure
```

## Future Test Coverage Improvements

Areas that could benefit from additional tests:

- Integration tests for full triage workflow
- Error handling and edge cases
- Rate limiting behavior
- Concurrent request handling
- Teams notification service
- ADO integration (when implemented)
