"""
Intake Service - Core business logic for issue triage.
Used by both FastAPI (local dev) and Azure Functions (production).

Implements FR10-FR24 from requirements (Issue Intake Agent).
"""
import json
import logging
import os
import re
from datetime import datetime, timedelta, timezone
from typing import Dict, List, Any, Optional, Tuple

from services.github_service import GitHubService
from services.llm_service import LlmService
from services.config_parser import ConfigParser
from services.teams_service import TeamsService
from models.issue_classification import IssueClassification, TriageRationale


def _parse_issue_url(issue_url: str) -> Tuple[str, str, int] | None:
    """
    Parse a GitHub issue URL to extract owner, repo, and issue number.

    Args:
        issue_url: URL like https://github.com/owner/repo/issues/123

    Returns:
        Tuple of (owner, repo, issue_number) or None if parsing fails
    """
    pattern = r'https?://github\.com/([^/]+)/([^/]+)/issues/(\d+)'
    match = re.match(pattern, issue_url)
    if match:
        return match.group(1), match.group(2), int(match.group(3))
    return None


def _map_to_repository_labels(
    github_service: GitHubService,
    owner: str,
    repo: str,
    issue_type: str,
    priority: str
) -> List[str]:
    """Map LLM classification results to actual repository labels."""
    repo_labels = github_service.get_repository_labels(owner, repo)
    if not repo_labels:
        # Fallback to generic labels if repository labels unavailable
        return [f"type:{issue_type}", f"priority:{priority}"]

    mapped_labels = []

    # Map issue type to repository labels
    type_search_terms = []
    if issue_type.lower() == "feature":
        type_search_terms = ["enhancement", "feature", f"type:{issue_type.lower()}"]
    elif issue_type.lower() == "bug":
        type_search_terms = ["bug", f"type:{issue_type.lower()}"]
    elif issue_type.lower() == "documentation":
        type_search_terms = ["documentation", "docs", f"type:{issue_type.lower()}"]
    elif issue_type.lower() == "question":
        type_search_terms = ["question", "help wanted", f"type:{issue_type.lower()}"]
    else:
        type_search_terms = [issue_type.lower(), f"type:{issue_type.lower()}"]

    type_mapping = _find_matching_labels(repo_labels, type_search_terms)
    if type_mapping:
        mapped_labels.extend(type_mapping[:1])  # Take the best match

    # Map priority to repository labels (most repos don't have priority labels)
    priority_search_terms = [
        priority.lower(),
        f"priority:{priority.lower()}",
        f"p{priority[1:].lower()}",  # P1 -> p1
        f"prio:{priority.lower()}"
    ]

    # Add severity mappings for priority
    if priority == "P1":
        priority_search_terms.extend(["critical", "urgent", "high", "severe"])
    elif priority == "P2":
        priority_search_terms.extend(["high", "important"])
    elif priority == "P3":
        priority_search_terms.extend(["medium", "normal"])
    elif priority == "P4":
        priority_search_terms.extend(["low", "minor"])

    priority_mapping = _find_matching_labels(repo_labels, priority_search_terms)
    if priority_mapping:
        mapped_labels.extend(priority_mapping[:1])  # Take the best match

    return mapped_labels


def _find_matching_labels(repo_labels: dict, search_terms: List[str]) -> List[str]:
    """Find repository labels that match any of the search terms."""
    matches = []
    repo_label_names = list(repo_labels.keys())

    for search_term in search_terms:
        # Exact match first
        for label_name in repo_label_names:
            if label_name.lower() == search_term:
                matches.append(label_name)

        # Partial match (contains)
        if not matches:
            for label_name in repo_label_names:
                if search_term in label_name.lower():
                    matches.append(label_name)

        if matches:
            break  # Found matches for this search term

    return matches


def _select_human_assignee(
    llm_service: LlmService,
    config,
    issue_title: str,
    issue_body: str,
    issue_type: str,
    priority: str
) -> Tuple[str, str]:
    """
    Select an appropriate human assignee using LLM-based expertise matching.

    Args:
        llm_service: LLM service instance for AI-based selection
        config: Team configuration with team_members (includes login and expertise)
        issue_title: Title of the issue
        issue_body: Body/description of the issue
        issue_type: Classification type (bug, feature, documentation, question)
        priority: Priority level (P1, P2, P3, P4)

    Returns:
        Tuple of (assignee_login, assignment_rationale)
    """
    team_members = config.team_members if hasattr(config, 'team_members') else []

    logging.info(f"_select_human_assignee: Found {len(team_members)} team members")
    if team_members:
        logging.info(f"Team members: {[m.get('name') + ' (' + m.get('login') + ')' for m in team_members]}")

    if not team_members:
        logging.warning("No team members configured")
        return None, "No team members configured"

    # Use LLM to select best engineer based on expertise
    result = llm_service.select_assignee(
        title=issue_title,
        body=issue_body or "",
        issue_type=issue_type,
        priority=priority,
        team_members=team_members
    )
    logging.info(f"LLM select_assignee result: {result}")
    if result.get("assignee"):
        logging.info(f"Returning assignee from LLM: {result['assignee']}")
        return result["assignee"], result.get("rationale", "Selected by AI based on expertise")

    # Fallback: use first team member for high priority, or first available
    first_login = team_members[0].get("login") if team_members else None
    logging.info(f"LLM didn't return assignee, using fallback. first_login={first_login}, priority={priority}")
    if priority in ["P0", "P1"] and first_login:
        logging.info(f"P0/P1 issue, assigning to first team member: {first_login}")
        return first_login, f"High priority ({priority}) issue assigned to primary engineer"

    logging.info(f"Default assignment to: {first_login}")
    return first_login, "Default assignment"


def _validate_classification(
    github_service: GitHubService,
    owner: str,
    repo: str,
    classification: IssueClassification
) -> dict:
    """Validate the LLM's classification choices against repository constraints."""
    validation_result = {
        "valid": True,
        "warnings": [],
        "errors": []
    }

    try:
        # Validate labels against existing repository labels
        label_validation = github_service.validate_labels(owner, repo, classification.suggested_labels)

        if label_validation["invalid"]:
            validation_result["warnings"].append(
                f"Invalid labels: {label_validation['invalid']}. Suggestions: {label_validation['suggestions']}"
            )
            # Mark as invalid since proposed labels don't exist in repository
            validation_result["valid"] = False
            # Only use valid labels for any actual operations
            classification.suggested_labels = label_validation["valid"]

        # Check confidence threshold
        if classification.confidence < 0.7:
            validation_result["warnings"].append(
                f"Low confidence ({classification.confidence:.2f}). Manual review recommended."
            )
            validation_result["valid"] = False

        # Validate assignee exists (simplified - in production, would check if user exists)
        if classification.suggested_assignee and classification.suggested_assignee.startswith("@"):
            # Remove @ symbol for API calls
            classification.suggested_assignee = classification.suggested_assignee[1:]

    except Exception as e:
        validation_result["errors"].append(f"Validation error: {str(e)}")
        validation_result["valid"] = False

    return validation_result


def _generate_tool_calls(owner: str, repo: str, classification: IssueClassification) -> List[dict]:
    """Generate JSON tool calls for GitHub API as specified in the design document."""
    tool_calls = []

    # Tool call for applying labels (only if we have valid labels)
    labels_to_apply = classification.suggested_labels.copy()
    if classification.is_copilot_fixable:
        # Note: We'll check if copilot-fixable exists in repository during actual application
        labels_to_apply.append("copilot-fixable")

    if labels_to_apply:
        tool_calls.append({
            "tool": "github_apply_labels",
            "parameters": {
                "owner": owner,
                "repo": repo,
                "issue_number": classification.issue_number,
                "labels": labels_to_apply
            },
            "rationale": f"Apply labels based on classification: {classification.reason}"
        })

    # Tool call for assignee (always present now due to 6.1 fix)
    if classification.suggested_assignee:
        assignee = classification.suggested_assignee
        if assignee.startswith("@"):
            assignee = assignee[1:]

        rationale = (
            "Assign to Copilot as issue is determined to be Copilot-fixable"
            if classification.is_copilot_fixable
            else "Assign to human engineer for review and resolution"
        )

        tool_calls.append({
            "tool": "github_assign_issue",
            "parameters": {
                "owner": owner,
                "repo": repo,
                "issue_number": classification.issue_number,
                "assignee": assignee
            },
            "rationale": rationale
        })

    # Tool call for adding triage comment (focused on AI reasoning)
    # Build structured comment from rationale fields
    rationale = classification.rationale
    reasoning_parts = []

    if rationale.type_rationale:
        reasoning_parts.append(f"**Type:** {rationale.type_rationale}")
    if rationale.priority_rationale:
        reasoning_parts.append(f"**Priority:** {rationale.priority_rationale}")
    if rationale.copilot_rationale:
        reasoning_parts.append(f"**Copilot Assessment:** {rationale.copilot_rationale}")
    if rationale.assignment_rationale:
        reasoning_parts.append(f"**Assignment:** {rationale.assignment_rationale}")

    reasoning_text = "\n".join(reasoning_parts) if reasoning_parts else classification.reason

    comment = f"""## 🤖 Team Assistant Triage

This issue has been automatically analyzed and triaged.

### AI Decision Reasoning

{reasoning_text}

*Labels and assignee have been applied based on this analysis.*
"""

    tool_calls.append({
        "tool": "github_add_comment",
        "parameters": {
            "owner": owner,
            "repo": repo,
            "issue_number": classification.issue_number,
            "comment": comment
        },
        "rationale": "Add triage comment explaining AI decision-making process and reasoning"
    })

    return tool_calls


def _apply_triage_changes(
    github_service: GitHubService,
    owner: str,
    repo: str,
    classification: IssueClassification
) -> dict:
    """Apply triage changes to the GitHub issue."""
    try:
        # Start with the mapped repository labels (already validated)
        labels = classification.suggested_labels.copy()

        # Only add copilot-fixable if it exists in repository or if we allow creating it
        if classification.is_copilot_fixable:
            repo_labels = github_service.get_repository_labels(owner, repo)
            if "copilot-fixable" in repo_labels:
                labels.append("copilot-fixable")
            else:
                # Log that we're skipping the copilot-fixable label
                logging.warning(f"Skipping 'copilot-fixable' label - not found in repository {owner}/{repo}")

        # Use the comprehensive apply_triage_result method
        comment = f"""## 🤖 Team Assistant Triage

This issue has been automatically analyzed and triaged.

**AI Decision Reasoning:** {classification.reason}

*Labels and assignee have been applied based on this analysis.*
"""

        # Apply changes with proper error handling
        results = github_service.apply_triage_result(
            owner=owner,
            repo=repo,
            issue_number=classification.issue_number,
            labels=labels if labels else None,  # Only pass labels if we have any
            assignee=classification.suggested_assignee,
            comment=comment
        )

        logging.info(f"Applied triage to issue #{classification.issue_number}: {results}")
        return results

    except Exception as e:
        logging.error(f"Error applying triage to issue #{classification.issue_number}: {str(e)}")
        return {"labels": False, "assignee": False, "comment": False, "error": str(e)}


def _write_reasoning_log(
    file_path: str,
    owner: str,
    repo: str,
    results: List[dict],
    applied_changes: bool
):
    """Write human-readable decision reasoning to markdown file."""
    with open(file_path, 'w', encoding='utf-8') as f:
        f.write("# Triage Reasoning Log\n\n")
        f.write(f"**Repository:** {owner}/{repo}\n")
        f.write(f"**Timestamp:** {datetime.now(timezone.utc).isoformat()}\n")
        f.write(f"**Applied Changes:** {'Yes' if applied_changes else 'No (Dry Run)'}\n")
        f.write(f"**Issues Processed:** {len(results)}\n\n")

        f.write("---\n\n")

        for result in results:
            issue = result["issue"]
            validation = result["validation"]
            tool_calls = result["tool_calls"]

            f.write(f"## Issue #{issue['issue_number']}: {issue.get('issue_type', 'Unknown').title()}\n\n")
            f.write(f"**Issue URL:** [#{issue['issue_number']}](https://github.com/{owner}/{repo}/issues/{issue['issue_number']})\n")
            f.write(f"**Classification:** {issue.get('issue_type', 'Unknown')} | {issue.get('priority', 'Unknown')}\n")
            f.write(f"**Confidence:** {issue.get('confidence', 0):.2f}\n")
            f.write(f"**Copilot-fixable:** {'Yes ✅' if issue.get('is_copilot_fixable', False) else 'No'}\n\n")

            # Write structured rationale if available
            rationale = issue.get('rationale', {})
            if rationale and any(rationale.get(k) for k in ['type_rationale', 'priority_rationale', 'copilot_rationale', 'assignment_rationale']):
                f.write("### AI Reasoning\n\n")
                if rationale.get('type_rationale'):
                    f.write(f"**Type:** {rationale['type_rationale']}\n\n")
                if rationale.get('priority_rationale'):
                    f.write(f"**Priority:** {rationale['priority_rationale']}\n\n")
                if rationale.get('copilot_rationale'):
                    f.write(f"**Copilot Assessment:** {rationale['copilot_rationale']}\n\n")
                if rationale.get('assignment_rationale'):
                    f.write(f"**Assignment:** {rationale['assignment_rationale']}\n\n")
            else:
                f.write(f"**Reasoning:** {issue.get('reason', 'No reasoning provided')}\n\n")

            # Validation results
            if validation.get("warnings") or validation.get("errors"):
                f.write("### Validation Issues\n\n")
                for warning in validation.get("warnings", []):
                    f.write(f"⚠️ **Warning:** {warning}\n\n")
                for error in validation.get("errors", []):
                    f.write(f"❌ **Error:** {error}\n\n")

            # Proposed actions
            f.write("### Proposed Actions\n\n")
            if not tool_calls:
                f.write("No actions proposed.\n\n")
            else:
                for j, tool_call in enumerate(tool_calls, 1):
                    f.write(f"{j}. **{tool_call['tool'].replace('_', ' ').title()}**\n")
                    f.write(f"   - {tool_call['rationale']}\n")
                    if tool_call['tool'] == 'github_apply_labels':
                        f.write(f"   - Labels: {', '.join(tool_call['parameters']['labels'])}\n")
                    elif tool_call['tool'] == 'github_assign_issue':
                        f.write(f"   - Assignee: {tool_call['parameters']['assignee']}\n")
                    f.write("\n")

            # Application status
            if applied_changes:
                applied = result.get("applied", False)
                app_result = result.get("application_result")
                f.write("### Application Status\n\n")
                if applied and app_result:
                    f.write("✅ **Applied Successfully**\n")
                    if isinstance(app_result, dict):
                        for action, success in app_result.items():
                            if action != "error":
                                status = "✅" if success else "❌"
                                f.write(f"   - {action.title()}: {status}\n")
                        if app_result.get("error"):
                            f.write(f"   - Error: {app_result['error']}\n")
                elif not validation.get("valid", True):
                    f.write("⚠️ **Skipped due to validation issues**\n")
                else:
                    f.write("❌ **Failed to apply changes**\n")
            else:
                f.write("### Status\n\n")
                f.write("📝 **Dry run - no changes applied**\n")

            f.write("\n---\n\n")


def triage_issues(
    owner: str,
    repo: str,
    since_hours: int = 24,
    apply_changes: bool = False,
    output_logs: bool = False,
    post_to_teams: bool = False,
    issue_url: Optional[str] = None,
    issue_numbers: Optional[List[int]] = None,
    github_service: Optional[GitHubService] = None,
    llm_service: Optional[LlmService] = None,
    config_parser: Optional[ConfigParser] = None,
    teams_service: Optional[TeamsService] = None,
) -> Dict[str, Any]:
    """
    Triage GitHub issues from a repository.

    Args:
        owner: GitHub repo owner
        repo: GitHub repo name
        since_hours: Hours to look back for issues (default 24)
        apply_changes: Whether to apply triage changes to issues
        output_logs: Whether to write output log files
        post_to_teams: Whether to post results to Teams
        issue_url: Optional single issue URL (overrides owner/repo)
        issue_numbers: Optional list of issue numbers to triage (overrides since_hours filtering)
        github_service: Optional injected GitHubService (for testing)
        llm_service: Optional injected LlmService (for testing)
        config_parser: Optional injected ConfigParser (for testing)
        teams_service: Optional injected TeamsService (for testing)

    Returns:
        Dict with triage results
    """
    # Initialize services if not injected
    if github_service is None:
        github_service = GitHubService()
    if llm_service is None:
        llm_service = LlmService()
    if config_parser is None:
        config_parser = ConfigParser()
    if teams_service is None:
        teams_service = TeamsService()

    # Check for single issue mode via issueUrl
    target_issue_number = None

    if issue_url:
        # Single issue mode - parse URL to get owner, repo, issue number
        parsed = _parse_issue_url(issue_url)
        if not parsed:
            raise ValueError("Invalid issue URL format. Expected: https://github.com/owner/repo/issues/123")
        owner, repo, target_issue_number = parsed
        since_hours = 8760  # Look back 1 year for single issue mode
        logging.info(f"Single issue mode: {owner}/{repo}#{target_issue_number}")

    # Load config
    config = config_parser.get_default_config()

    # Handle issue_numbers mode (triage specific selected issues)
    if issue_numbers and len(issue_numbers) > 0:
        logging.info(f"Selected issues mode: triaging {len(issue_numbers)} issues")
        untriaged_issues = []
        for issue_num in issue_numbers:
            single_issue = github_service.get_issue(owner, repo, issue_num)
            if single_issue:
                untriaged_issues.append(single_issue)
                logging.info(f"Fetched issue #{issue_num}")
            else:
                logging.warning(f"Issue #{issue_num} not found")
    else:
        # Get untriaged issues
        since_time = datetime.now(timezone.utc) - timedelta(hours=since_hours)
        untriaged_issues = github_service.get_new_untriaged_issues(owner, repo, since_time)

        # Filter to specific issue if in single issue mode
        if target_issue_number is not None:
            untriaged_issues = [issue for issue in untriaged_issues if issue.number == target_issue_number]
            if not untriaged_issues:
                # Issue not found in untriaged list - try to fetch it directly
                single_issue = github_service.get_issue(owner, repo, target_issue_number)
                if single_issue:
                    untriaged_issues = [single_issue]
                    logging.info(f"Fetched single issue #{target_issue_number} directly")

    # Handle no issues found
    if not untriaged_issues:
        teams_message_sent = False
        if post_to_teams:
            teams_message_sent = teams_service.post_intake_results(owner, repo, [], apply_changes)

        result = {
            "message": f"No untriaged issues found in {owner}/{repo} since {since_time.isoformat()}",
            "processed_count": 0,
            "applied_changes": apply_changes,
            "total_tool_calls": 0,
            "teams_message_sent": teams_message_sent,
            "results": []
        }

        if output_logs:
            timestamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
            output_dir = "output"
            os.makedirs(output_dir, exist_ok=True)

            decisions_file = os.path.join(output_dir, f"triage-decisions_{owner}_{repo}_{timestamp}.json")
            with open(decisions_file, 'w', encoding='utf-8') as f:
                json.dump({
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                    "repository": f"{owner}/{repo}",
                    "processed_count": 0,
                    "applied_changes": apply_changes,
                    "total_tool_calls": 0,
                    "tool_calls": [],
                    "results": []
                }, f, indent=2)

            reasoning_file = os.path.join(output_dir, f"reasoning-log_{owner}_{repo}_{timestamp}.md")
            with open(reasoning_file, 'w', encoding='utf-8') as f:
                f.write("# Triage Reasoning Log\n\n")
                f.write(f"**Repository:** {owner}/{repo}\n")
                f.write(f"**Timestamp:** {datetime.now(timezone.utc).isoformat()}\n")
                f.write(f"**Since:** {since_time.isoformat()}\n\n")
                f.write("## Result\n\nNo untriaged issues found.\n")

            result["output_files"] = {
                "triage_decisions": decisions_file,
                "reasoning_log": reasoning_file
            }

        return result

    logging.info(f"Found {len(untriaged_issues)} untriaged issues to process")

    # Get repository labels once for efficient mapping
    repo_labels = github_service.get_repository_labels(owner, repo)
    logging.info(f"Retrieved {len(repo_labels)} labels from repository {owner}/{repo}")

    # Process each issue
    results = []
    for issue in untriaged_issues:
        # Classify the issue
        classification = llm_service.classify_issue(
            title=issue.title,
            body=issue.body or "",
            rules=config.priority_rules
        )

        # Check if Copilot-fixable using LLM-based assessment
        copilot_result = llm_service.is_copilot_fixable(
            title=issue.title,
            body=issue.body or "",
            config=config.copilot_fixable,
            issue_type=classification["type"],
            priority=classification["priority"]
        )
        is_copilot_fixable = copilot_result["is_copilot_fixable"]
        copilot_reasoning = copilot_result.get("reasoning", "")

        # Generate fix suggestions
        fix_suggestions = llm_service.generate_fix_suggestions(
            title=issue.title,
            body=issue.body or "",
            issue_type=classification["type"],
            priority=classification["priority"]
        )

        # Determine assignee based on Copilot-fixable status
        assignment_rationale = ""
        if is_copilot_fixable:
            suggested_assignee = "copilot"
            assignment_rationale = f"Issue is suitable for Copilot automated fix. {copilot_reasoning}"
        else:
            # Use LLM to select best human engineer based on expertise
            logging.info(f"Calling _select_human_assignee for issue #{issue.number}, type={classification['type']}, priority={classification['priority']}")
            human_assignee, assignment_rationale = _select_human_assignee(
                llm_service=llm_service,
                config=config,
                issue_title=issue.title,
                issue_body=issue.body or "",
                issue_type=classification["type"],
                priority=classification["priority"]
            )
            logging.info(f"_select_human_assignee returned: assignee={human_assignee}, rationale={assignment_rationale[:100] if assignment_rationale else None}")
            suggested_assignee = human_assignee

        # Map classification results to actual repository labels
        suggested_labels = _map_to_repository_labels(
            github_service, owner, repo, classification["type"], classification["priority"]
        )

        # Build structured rationale for each decision
        triage_rationale = TriageRationale(
            type_rationale=classification.get("type_rationale", f"Classified as '{classification['type']}' based on issue content"),
            priority_rationale=classification.get("priority_rationale", f"Assigned {classification['priority']} based on keywords and impact"),
            copilot_rationale=copilot_reasoning or ("Suitable for Copilot fix" if is_copilot_fixable else "Requires human expertise"),
            assignment_rationale=assignment_rationale,
            labels_rationale=f"Applied labels {', '.join(suggested_labels)} based on issue type and priority"
        )

        # Build legacy combined reason for backwards compatibility
        combined_reason = triage_rationale.to_summary()

        issue_classification = IssueClassification(
            issue_url=f"https://github.com/{owner}/{repo}/issues/{issue.number}",
            issue_number=issue.number,
            issue_type=classification["type"],
            priority=classification["priority"],
            suggested_labels=suggested_labels,
            suggested_assignee=suggested_assignee,
            is_copilot_fixable=is_copilot_fixable,
            reason=combined_reason,
            confidence=classification.get("confidence", 0.8),
            rationale=triage_rationale,
            fix_suggestions=fix_suggestions
        )

        # Validate the LLM's choices
        validation_result = _validate_classification(github_service, owner, repo, issue_classification)

        # Generate JSON tool calls for proposed changes
        tool_calls = _generate_tool_calls(owner, repo, issue_classification)

        # Apply changes if requested and valid
        application_result = None
        if apply_changes and validation_result["valid"]:
            application_result = _apply_triage_changes(github_service, owner, repo, issue_classification)

        results.append({
            "issue": issue_classification.to_dict(),
            "validation": validation_result,
            "tool_calls": tool_calls,
            "applied": apply_changes and validation_result["valid"],
            "application_result": application_result
        })

    # Write results to files if enabled
    if output_logs:
        timestamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
        output_dir = "output"
        os.makedirs(output_dir, exist_ok=True)

        # Write triage-decisions.json
        decisions_file = os.path.join(output_dir, f"triage-decisions_{owner}_{repo}_{timestamp}.json")
        with open(decisions_file, 'w', encoding='utf-8') as f:
            json.dump({
                "timestamp": datetime.now(timezone.utc).isoformat(),
                "repository": f"{owner}/{repo}",
                "processed_count": len(results),
                "applied_changes": apply_changes,
                "results": results
            }, f, indent=2)

        # Write reasoning-log.md
        reasoning_file = os.path.join(output_dir, f"reasoning-log_{owner}_{repo}_{timestamp}.md")
        _write_reasoning_log(reasoning_file, owner, repo, results, apply_changes)

    # Post to Teams if requested
    teams_message_sent = False
    if post_to_teams:
        teams_message_sent = teams_service.post_intake_results(owner, repo, results, apply_changes)

    result = {
        "message": f"Processed {len(results)} issues from {owner}/{repo}",
        "processed_count": len(results),
        "applied_changes": apply_changes,
        "teams_message_sent": teams_message_sent,
        "results": results
    }

    if output_logs:
        result["output_files"] = {
            "triage_decisions": decisions_file,
            "reasoning_log": reasoning_file
        }

    return result
