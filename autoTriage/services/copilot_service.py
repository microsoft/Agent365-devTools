# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Copilot Service - Assigns issues to GitHub Copilot coding agent for auto-fix.
"""
import logging
import os
import subprocess
import json
from typing import Optional, Dict, Any

logger = logging.getLogger(__name__)


class CopilotService:
    """Service for invoking GitHub Copilot coding agent to fix issues."""

    def __init__(self):
        self.github_token = os.getenv('GITHUB_TOKEN')

    def is_copilot_enabled(self, owner: str, repo: str) -> bool:
        """Check if Copilot coding agent is enabled for the repository.
        
        Returns True if copilot-swe-agent is in the suggested actors list.
        """
        try:
            query = '''
            query {
              repository(owner: "%s", name: "%s") {
                suggestedActors(capabilities: [CAN_BE_ASSIGNED], first: 100) {
                  nodes {
                    login
                  }
                }
              }
            }
            ''' % (owner, repo)
            
            result = subprocess.run(
                ['gh', 'api', 'graphql', '-f', f'query={query}'],
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
                if actor.get('login') == 'copilot-swe-agent':
                    return True
            
            return False
            
        except Exception as e:
            logger.error(f"Error checking Copilot status: {e}")
            return False

    def assign_to_copilot(
        self,
        owner: str,
        repo: str,
        issue_number: int,
        custom_instructions: str = "",
        base_branch: str = "main"
    ) -> Dict[str, Any]:
        """Assign an issue to GitHub Copilot coding agent.
        
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
            # Build the API request payload
            payload = {
                "assignees": ["copilot-swe-agent[bot]"],
                "agent_assignment": {
                    "target_repo": f"{owner}/{repo}",
                    "base_branch": base_branch,
                    "custom_instructions": custom_instructions,
                    "custom_agent": "",
                    "model": ""
                }
            }
            
            payload_json = json.dumps(payload)
            
            # Use gh CLI to make the API call
            result = subprocess.run(
                [
                    'gh', 'api',
                    '--method', 'POST',
                    '-H', 'Accept: application/vnd.github+json',
                    '-H', 'X-GitHub-Api-Version: 2022-11-28',
                    f'/repos/{owner}/{repo}/issues/{issue_number}/assignees',
                    '--input', '-'
                ],
                input=payload_json,
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
            
            logger.info(f"Successfully assigned issue #{issue_number} to Copilot coding agent")
            return {
                "success": True,
                "issue_number": issue_number,
                "assigned_to": "copilot-swe-agent[bot]",
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
