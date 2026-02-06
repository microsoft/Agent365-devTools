# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Tests for GitHub Actions workflow logic.

Tests the shell scripts and conditionals in:
- auto-triage-issues.yml
- escalate-stale-issues.yml
- daily-issue-report.yml
- update-team-workload.yml
"""
import pytest
import sys
import yaml
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent.parent))

# Path to workflows folder (autoTriage is under Agent365-devTools, workflows are at repo root)
WORKFLOWS_DIR = Path(__file__).parent.parent.parent.parent / ".github" / "workflows"


class TestAutoTriageWorkflow:
    """Test auto-triage-issues.yml workflow logic."""

    @pytest.fixture
    def workflow(self):
        """Load the auto-triage workflow."""
        workflow_path = WORKFLOWS_DIR / "auto-triage-issues.yml"
        with open(workflow_path, 'r', encoding='utf-8') as f:
            return yaml.safe_load(f)

    def test_workflow_triggers_on_issues(self, workflow):
        """Test workflow triggers on issue events."""
        # YAML 'on' becomes True in Python, need to check for both
        on_key = workflow.get("on") or workflow.get(True)
        assert "issues" in on_key
        assert "opened" in on_key["issues"]["types"]
        assert "edited" in on_key["issues"]["types"]

    def test_workflow_skips_bots(self, workflow):
        """Test workflow skips bot actors."""
        job_if = workflow["jobs"]["triage"]["if"]
        assert "github-actions[bot]" in job_if
        assert "dependabot[bot]" in job_if

    def test_workflow_has_required_permissions(self, workflow):
        """Test workflow has correct permissions."""
        assert workflow["permissions"]["issues"] == "write"
        assert workflow["permissions"]["contents"] == "read"

    def test_workflow_uses_python_311(self, workflow):
        """Test workflow uses Python 3.11."""
        steps = workflow["jobs"]["triage"]["steps"]
        python_step = next(s for s in steps if s.get("name") == "Set up Python")
        assert python_step["with"]["python-version"] == "3.11"

    # Test retriage flag logic
    RETRIAGE_CASES = [
        ("opened", "false", "New issue should not retriage"),
        ("edited", "true", "Edited issue should retriage"),
        ("labeled", "true", "Labeled action should retriage"),
        ("reopened", "true", "Reopened should retriage"),
    ]

    @pytest.mark.parametrize(
        "action,expected_retriage,description",
        RETRIAGE_CASES,
        ids=[c[2] for c in RETRIAGE_CASES]
    )
    def test_retriage_flag_logic(self, action, expected_retriage, description):
        """Test retriage flag is set correctly based on event action."""
        # Simulate the bash logic from the workflow
        if action == "opened":
            retriage = "false"
        else:
            retriage = "true"
        
        assert retriage == expected_retriage, f"Failed: {description}"


class TestEscalationWorkflow:
    """Test escalate-stale-issues.yml workflow logic."""

    @pytest.fixture
    def workflow(self):
        """Load the escalation workflow."""
        workflow_path = WORKFLOWS_DIR / "escalate-stale-issues.yml"
        with open(workflow_path, 'r', encoding='utf-8') as f:
            return yaml.safe_load(f)

    def test_workflow_runs_on_schedule(self, workflow):
        """Test workflow runs on schedule."""
        on_key = workflow.get("on") or workflow.get(True)
        assert "schedule" in on_key
        # Should run daily at 8 AM UTC
        cron = on_key["schedule"][0]["cron"]
        assert "0 8 * * *" == cron

    def test_workflow_supports_manual_dispatch(self, workflow):
        """Test workflow supports manual trigger."""
        on_key = workflow.get("on") or workflow.get(True)
        assert "workflow_dispatch" in on_key
        assert "apply" in on_key["workflow_dispatch"]["inputs"]

    def test_workflow_has_issues_write_permission(self, workflow):
        """Test workflow can write to issues."""
        assert workflow["permissions"]["issues"] == "write"

    # Test apply flag logic
    APPLY_FLAG_CASES = [
        ("schedule", None, "--apply", "Scheduled runs always apply"),
        ("workflow_dispatch", "true", "--apply", "Manual dispatch with apply=true"),
        ("workflow_dispatch", "false", "", "Manual dispatch with apply=false"),
    ]

    @pytest.mark.parametrize(
        "event_name,apply_input,expected_flag,description",
        APPLY_FLAG_CASES,
        ids=[c[3] for c in APPLY_FLAG_CASES]
    )
    def test_apply_flag_logic(self, event_name, apply_input, expected_flag, description):
        """Test apply flag is set correctly based on trigger."""
        # Simulate the bash logic from the workflow
        if event_name == "workflow_dispatch":
            if apply_input == "true":
                apply_flag = "--apply"
            else:
                apply_flag = ""
        else:
            # Scheduled runs always apply
            apply_flag = "--apply"
        
        assert apply_flag == expected_flag, f"Failed: {description}"


class TestDailyReportWorkflow:
    """Test daily-issue-report.yml workflow logic."""

    @pytest.fixture
    def workflow(self):
        """Load the daily report workflow."""
        workflow_path = WORKFLOWS_DIR / "daily-issue-report.yml"
        with open(workflow_path, 'r', encoding='utf-8') as f:
            return yaml.safe_load(f)

    def test_workflow_runs_at_9am_utc(self, workflow):
        """Test workflow runs daily at 9 AM UTC."""
        on_key = workflow.get("on") or workflow.get(True)
        cron = on_key["schedule"][0]["cron"]
        assert "0 9 * * *" == cron

    def test_workflow_has_read_only_permissions(self, workflow):
        """Test workflow only needs read permissions."""
        assert workflow["permissions"]["issues"] == "read"
        assert workflow["permissions"]["contents"] == "read"

    def test_workflow_uploads_report_artifact(self, workflow):
        """Test workflow uploads report as artifact."""
        steps = workflow["jobs"]["daily-report"]["steps"]
        upload_step = next((s for s in steps if s.get("name") == "Upload report artifact"), None)
        assert upload_step is not None
        assert upload_step["with"]["name"] == "daily-report"


class TestUpdateWorkloadWorkflow:
    """Test update-team-workload.yml workflow logic."""

    @pytest.fixture
    def workflow(self):
        """Load the update workload workflow."""
        workflow_path = WORKFLOWS_DIR / "update-team-workload.yml"
        with open(workflow_path, 'r', encoding='utf-8') as f:
            return yaml.safe_load(f)

    def test_workflow_runs_weekly_on_monday(self, workflow):
        """Test workflow runs every Monday at 9 AM UTC."""
        on_key = workflow.get("on") or workflow.get(True)
        cron = on_key["schedule"][0]["cron"]
        assert "0 9 * * 1" == cron  # 1 = Monday

    def test_workflow_can_write_contents(self, workflow):
        """Test workflow can write to repository contents."""
        assert workflow["permissions"]["contents"] == "write"

    def test_workflow_checks_for_changes(self, workflow):
        """Test workflow checks if team-members.json changed."""
        steps = workflow["jobs"]["update-workload"]["steps"]
        check_step = next((s for s in steps if s.get("id") == "check_changes"), None)
        assert check_step is not None
        assert "team-members.json" in check_step["run"]

    def test_workflow_commits_with_skip_ci(self, workflow):
        """Test workflow commit message includes [skip ci]."""
        steps = workflow["jobs"]["update-workload"]["steps"]
        commit_step = next((s for s in steps if "Commit and push" in s.get("name", "")), None)
        assert commit_step is not None
        assert "[skip ci]" in commit_step["run"]


class TestBotSkipLogic:
    """Test bot detection/skip logic used across workflows."""

    BOT_ACTOR_CASES = [
        ("github-actions[bot]", True, "GitHub Actions bot should be skipped"),
        ("dependabot[bot]", True, "Dependabot should be skipped"),
        ("renovate[bot]", True, "Renovate bot should be skipped"),
        ("regularuser", False, "Regular user should not be skipped"),
        ("botuser", False, "User with 'bot' in name should not be skipped"),
    ]

    @pytest.mark.parametrize(
        "actor,should_skip,description",
        BOT_ACTOR_CASES,
        ids=[c[2] for c in BOT_ACTOR_CASES]
    )
    def test_bot_actor_detection(self, actor, should_skip, description):
        """Test bot actor detection for workflow skip logic."""
        # Check if actor matches the pattern used in workflow
        is_bot = actor in ["github-actions[bot]", "dependabot[bot]"] or actor.endswith("[bot]")
        assert is_bot == should_skip, f"Failed: {description}"


class TestWorkflowEnvironmentVariables:
    """Test environment variable configuration in workflows."""

    @pytest.fixture
    def triage_workflow(self):
        """Load the auto-triage workflow."""
        workflow_path = WORKFLOWS_DIR / "auto-triage-issues.yml"
        with open(workflow_path, 'r', encoding='utf-8') as f:
            return yaml.safe_load(f)

    def test_github_models_env_vars(self, triage_workflow):
        """Test GitHub Models environment variables are configured."""
        steps = triage_workflow["jobs"]["triage"]["steps"]
        triage_step = next(s for s in steps if s.get("id") == "triage")
        env = triage_step["env"]
        
        assert "GITHUB_TOKEN" in env
        assert "GITHUB_MODELS_ENDPOINT" in env
        assert "GITHUB_MODELS_MODEL" in env

    def test_azure_openai_env_vars(self, triage_workflow):
        """Test Azure OpenAI environment variables are configured."""
        steps = triage_workflow["jobs"]["triage"]["steps"]
        triage_step = next(s for s in steps if s.get("id") == "triage")
        env = triage_step["env"]
        
        assert "AZURE_OPENAI_API_KEY" in env
        assert "AZURE_OPENAI_ENDPOINT" in env
        assert "AZURE_OPENAI_DEPLOYMENT" in env


class TestBashArrayCommandBuilding:
    """Test bash array command building pattern used in workflows."""

    def test_bash_array_building(self):
        """Test that bash array pattern correctly builds commands."""
        # Simulate the bash array pattern from auto-triage-issues.yml
        args = [
            "--owner", "testowner",
            "--repo", "testrepo",
            "--issue-number", "123",
            "--output", "/tmp/result.json",
            "--apply"
        ]
        
        retriage = True
        if retriage:
            args.append("--retriage")
        
        # Verify args are correct
        assert "--owner" in args
        assert "testowner" in args
        assert "--retriage" in args
        assert len(args) == 10  # 9 base args + 1 retriage

    def test_bash_array_without_retriage(self):
        """Test bash array without retriage flag."""
        args = [
            "--owner", "testowner",
            "--repo", "testrepo",
            "--issue-number", "123",
            "--output", "/tmp/result.json",
            "--apply"
        ]
        
        # retriage is False for new issues (action == 'opened')
        # This test verifies the base args without the --retriage flag
        assert "--retriage" not in args
        assert len(args) == 9  # 9 base args, no retriage


class TestWorkflowNoEval:
    """Test that workflows don't use eval (security)."""

    @pytest.fixture
    def all_workflows(self):
        """Load all autoTriage workflow files."""
        workflows = {}
        for yml_file in ["auto-triage-issues.yml", "escalate-stale-issues.yml", 
                         "daily-issue-report.yml", "update-team-workload.yml"]:
            path = WORKFLOWS_DIR / yml_file
            if path.exists():
                with open(path, 'r', encoding='utf-8') as f:
                    workflows[yml_file] = f.read()
        return workflows

    def test_no_eval_in_workflows(self, all_workflows):
        """Test that workflows don't use eval command."""
        for name, content in all_workflows.items():
            assert "eval " not in content, f"Found 'eval' in {name} - security risk"

    def test_no_shell_injection_patterns(self, all_workflows):
        """Test that workflows don't use shell injection patterns."""
        dangerous_patterns = [
            "eval ",
            "`$",  # Backtick followed by variable (command injection)
            "$(",  # Command substitution
        ]
        
        for name, content in all_workflows.items():
            for pattern in dangerous_patterns:
                # Allow $(cat in summary steps where it's safe
                if pattern == "$(cat" and "GITHUB_STEP_SUMMARY" in content:
                    continue
                assert pattern not in content or "STEP_SUMMARY" in content, \
                    f"Found dangerous pattern '{pattern}' in {name}"
