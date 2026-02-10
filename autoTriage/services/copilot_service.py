# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Copilot Service - Assigns issues to GitHub Copilot coding agent for auto-fix.
"""
import logging
import os
import subprocess
import json
from typing import Dict, Any

logger = logging.getLogger(__name__)

# Copilot actor names - GitHub may return either variant
COPILOT_ACTOR_LOGIN = "copilot-swe-agent"
COPILOT_ACTOR_BOT = "copilot-swe-agent[bot]"
COPILOT_ASSIGNEE = COPILOT_ACTOR_BOT  # The assignee format for API calls


class CopilotService:
    """Service for invoking GitHub Copilot coding agent to fix issues.
    
    Note: This service uses the gh CLI which relies on its own authentication.
    Ensure gh is authenticated via `gh auth login` or GH_TOKEN environment variable.
    """

    def __init__(self):
        pass  # gh CLI handles authentication via GH_TOKEN or gh auth state

    def is_copilot_enabled(self, owner: str, repo: str) -> bool:
        """Check if Copilot coding agent is enabled for the repository.
        
        Returns True if copilot-swe-agent is in the suggested actors list.
        """
        try:
            query = '''
            query($owner: String!, $name: String!) {
              repository(owner: $owner, name: $name) {
                suggestedActors(capabilities: [CAN_BE_ASSIGNED], first: 100) {
                  nodes {
                    login
                  }
                }
              }
            }
            '''
            
            result = subprocess.run(
                [
                    'gh', 'api', 'graphql',
                    '-f', f'query={query}',
                    '-f', f'owner={owner}',
                    '-f', f'name={repo}'
                ],
                capture_output=True,
                text=True,
                timeout=30
            )
            
            if result.returncode != 0:
                logger.warning(f"Failed to check Copilot status: {result.stderr}")
                return False
            
            data = json.loads(result.stdout)
            actors = data.get('data', {}).get('repository', {}).get('suggestedActors', {}).get('nodes', [])
            
            for actor in actors:
                login = actor.get('login', '')
                # Check both variants: with and without [bot] suffix
                if login in (COPILOT_ACTOR_LOGIN, COPILOT_ACTOR_BOT):
                    return True
            
            return False
            
        except Exception as e:
            logger.error(f"Error checking Copilot status: {e}")
            return False

    def _get_assignment_prerequisites(
        self, owner: str, repo: str, issue_number: int
    ) -> tuple[str | None, str | None, str | None, str | None]:
        """Get all prerequisites for Copilot assignment in a single GraphQL call.
        
        Combines bot ID lookup and issue/repo ID lookup to reduce API calls
        and rate-limit pressure.
        
        Returns:
            Tuple of (bot_id, repo_id, issue_id, error). 
            If error is not None, it indicates the specific failure reason.
            Individual IDs may be None with error=None if that specific item wasn't found.
        """
        try:
            query = '''
            query($owner: String!, $name: String!, $issueNumber: Int!) {
              repository(owner: $owner, name: $name) {
                id
                issue(number: $issueNumber) {
                  id
                }
                suggestedActors(capabilities: [CAN_BE_ASSIGNED], first: 100) {
                  nodes {
                    login
                    __typename
                    ... on Bot {
                      id
                    }
                    ... on User {
                      id
                    }
                  }
                }
              }
            }
            '''
            
            result = subprocess.run(
                [
                    'gh', 'api', 'graphql',
                    '-f', f'query={query}',
                    '-f', f'owner={owner}',
                    '-f', f'name={repo}',
                    '-F', f'issueNumber={issue_number}'
                ],
                capture_output=True,
                text=True,
                timeout=30
            )
            
            if result.returncode != 0:
                error_msg = f"Failed to query prerequisites: {result.stderr.strip() or 'Unknown error'}"
                logger.warning(error_msg)
                return None, None, None, error_msg
            
            data = json.loads(result.stdout)
            
            # Normalize nullable 'data' field to avoid AttributeError on None.get()
            data_field = data.get('data')
            if not isinstance(data_field, dict):
                error_msg = f"GraphQL returned null data for {owner}/{repo}"
                logger.warning(error_msg)
                return None, None, None, error_msg
            
            # Validate repository data
            repo_data = data_field.get('repository')
            if not isinstance(repo_data, dict):
                logger.warning(f"Repository {owner}/{repo} not found or not accessible")
                return None, None, None, None  # Not an API error, just repo not found
            
            repo_id = repo_data.get('id')
            
            # Extract issue ID (may be null if issue doesn't exist)
            issue_data = repo_data.get('issue')
            issue_id = None
            if isinstance(issue_data, dict):
                issue_id = issue_data.get('id')
            else:
                logger.warning(f"Issue #{issue_number} not found in {owner}/{repo}")
            
            # Extract Copilot bot ID from suggested actors
            bot_id = None
            suggested_actors = repo_data.get('suggestedActors')
            if isinstance(suggested_actors, dict):
                nodes = suggested_actors.get('nodes')
                if isinstance(nodes, list):
                    for actor in nodes:
                        if not isinstance(actor, dict):
                            continue
                        login = actor.get('login', '')
                        if login in (COPILOT_ACTOR_LOGIN, COPILOT_ACTOR_BOT):
                            bot_id = actor.get('id')
                            break
            
            return bot_id, repo_id, issue_id, None
            
        except subprocess.TimeoutExpired:
            error_msg = f"Timeout querying prerequisites for {owner}/{repo}"
            logger.error(error_msg)
            return None, None, None, error_msg
        except json.JSONDecodeError as e:
            error_msg = f"Invalid JSON response: {e}"
            logger.error(error_msg)
            return None, None, None, error_msg
        except Exception as e:
            error_msg = f"Error getting assignment prerequisites: {e}"
            logger.error(error_msg)
            return None, None, None, error_msg

    def assign_to_copilot(
        self,
        owner: str,
        repo: str,
        issue_number: int,
        custom_instructions: str = "",
        base_branch: str = "main"
    ) -> Dict[str, Any]:
        """Assign an issue to GitHub Copilot coding agent using GraphQL API.
        
        Args:
            owner: Repository owner
            repo: Repository name
            issue_number: Issue number to assign
            custom_instructions: Additional instructions for Copilot
            base_branch: Branch to base the fix on (default: main)
            
        Returns:
            Dict with success status and details
        """
        try:
            # Step 1: Get all prerequisites in a single GraphQL call
            bot_id, repo_id, issue_id, prereq_error = self._get_assignment_prerequisites(
                owner, repo, issue_number
            )
            
            # If there was an API/query error, return it directly
            if prereq_error:
                return {
                    "success": False,
                    "error": prereq_error,
                    "issue_number": issue_number
                }
            
            if not bot_id:
                logger.error(f"Copilot coding agent not available for {owner}/{repo}")
                return {
                    "success": False,
                    "error": "Copilot coding agent not available for this repository",
                    "issue_number": issue_number
                }
            
            if not repo_id:
                logger.error(f"Failed to get repository ID for {owner}/{repo}")
                return {
                    "success": False,
                    "error": "Failed to get repository ID",
                    "issue_number": issue_number
                }
            
            if not issue_id:
                logger.error(f"Failed to get issue ID for {owner}/{repo}#{issue_number}")
                return {
                    "success": False,
                    "error": "Failed to get issue ID",
                    "issue_number": issue_number
                }
            
            # Step 2: Assign issue to Copilot using GraphQL mutation
            # Use json.dumps for proper escaping of all special characters
            escaped_issue_id = json.dumps(issue_id)
            escaped_bot_id = json.dumps(bot_id)
            escaped_repo_id = json.dumps(repo_id)
            escaped_base_branch = json.dumps(base_branch)
            escaped_instructions = json.dumps(custom_instructions)
            
            mutation = f'''
            mutation {{
              addAssigneesToAssignable(input: {{
                assignableId: {escaped_issue_id},
                assigneeIds: [{escaped_bot_id}],
                agentAssignment: {{
                  targetRepositoryId: {escaped_repo_id},
                  baseRef: {escaped_base_branch},
                  customInstructions: {escaped_instructions},
                  customAgent: "",
                  model: ""
                }}
              }}) {{
                assignable {{
                  ... on Issue {{
                    id
                    title
                    assignees(first: 10) {{
                      nodes {{
                        login
                      }}
                    }}
                  }}
                }}
              }}
            }}
            '''
            
            result = subprocess.run(
                [
                    'gh', 'api', 'graphql',
                    '-f', f'query={mutation}',
                    '-H', 'GraphQL-Features: issues_copilot_assignment_api_support,coding_agent_model_selection'
                ],
                capture_output=True,
                text=True,
                timeout=60
            )
            
            if result.returncode != 0:
                logger.error(f"Failed to assign issue #{issue_number} to Copilot: {result.stderr}")
                return {
                    "success": False,
                    "error": result.stderr,
                    "issue_number": issue_number
                }
            
            response = json.loads(result.stdout) if result.stdout else {}
            
            # Check for GraphQL errors - aggregate all messages for better diagnostics
            errors = response.get('errors')
            if errors:
                error_messages = []
                for err in errors:
                    msg = err.get('message', 'Unknown GraphQL error')
                    path = err.get('path')
                    if path:
                        path_str = '/'.join(str(p) for p in path)
                        msg = f"{msg} (path: {path_str})"
                    error_messages.append(msg)
                
                combined_error_msg = '; '.join(error_messages) if error_messages else 'Unknown GraphQL error'
                logger.error(f"GraphQL error assigning issue #{issue_number}: {combined_error_msg}")
                return {
                    "success": False,
                    "error": combined_error_msg,
                    "issue_number": issue_number
                }
            
            logger.info(f"Successfully assigned issue #{issue_number} to Copilot coding agent")
            return {
                "success": True,
                "issue_number": issue_number,
                "assigned_to": COPILOT_ASSIGNEE,
                "base_branch": base_branch,
                "response": response
            }
            
        except subprocess.TimeoutExpired:
            logger.error(f"Timeout assigning issue #{issue_number} to Copilot")
            return {
                "success": False,
                "error": "Timeout",
                "issue_number": issue_number
            }
        except Exception as e:
            logger.error(f"Error assigning issue #{issue_number} to Copilot: {e}")
            return {
                "success": False,
                "error": str(e),
                "issue_number": issue_number
            }

    def get_fix_instructions(
        self,
        issue_title: str,
        issue_body: str,
        fix_suggestions: list
    ) -> str:
        """Generate custom instructions for Copilot based on triage analysis.
        
        Args:
            issue_title: The issue title
            issue_body: The issue body/description
            fix_suggestions: List of fix suggestions from triage
            
        Returns:
            Custom instructions string for Copilot
        """
        instructions = []
        
        instructions.append("Please fix the issue described below.")
        instructions.append("")
        
        # Include issue title
        if issue_title:
            instructions.append(f"Issue: {issue_title}")
            instructions.append("")
        
        # Include issue body (truncated for reasonable instruction length)
        if issue_body:
            # Truncate body to 1500 chars to keep instructions manageable
            truncated_body = issue_body[:1500]
            if len(issue_body) > 1500:
                truncated_body += "..."
            instructions.append("Description:")
            instructions.append(truncated_body)
            instructions.append("")
        
        if fix_suggestions:
            instructions.append("Suggested approach:")
            for i, suggestion in enumerate(fix_suggestions, 1):
                instructions.append(f"{i}. {suggestion}")
            instructions.append("")
        
        instructions.append("Requirements:")
        instructions.append("- Create a focused fix addressing only the issue described")
        instructions.append("- Follow existing code style and conventions")
        instructions.append("- Add or update tests if applicable")
        instructions.append("- Keep the PR small and reviewable")
        
        return "\n".join(instructions)
