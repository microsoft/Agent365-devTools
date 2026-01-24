#!/usr/bin/env python3
"""
Claude Skill: PR Review Generator
Generates structured, editable PR review comments and posts them to GitHub.

Usage:
    python review-pr.py <pr-number> [--dry-run] [--output FILE]

Example:
    python review-pr.py 180
    python review-pr.py 180 --dry-run
"""
import argparse
import json
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Dict, List, Any

try:
    import yaml
except ImportError:
    print("Error: PyYAML not installed. Run: pip install PyYAML")
    sys.exit(1)


class PRReviewer:
    """Generate and post PR review comments."""

    def __init__(self, pr_number: int, dry_run: bool = False):
        self.pr_number = pr_number
        self.dry_run = dry_run
        self.pr_data = None

    def run_command(self, cmd: str, check: bool = True) -> str:
        """Execute shell command and return output."""
        result = subprocess.run(
            cmd,
            shell=True,
            capture_output=True,
            text=True,
            encoding='utf-8',
            errors='replace'
        )
        if check and result.returncode != 0:
            print(f"Error: {result.stderr}", file=sys.stderr)
            if check:
                sys.exit(1)
        if result.stdout is None:
            return ""
        return result.stdout.strip()

    def fetch_pr_details(self) -> Dict[str, Any]:
        """Fetch PR details from GitHub."""
        print(f"Fetching PR #{self.pr_number} details...")

        pr_json = self.run_command(
            f'gh pr view {self.pr_number} --json '
            'number,title,body,author,files,state,reviews,comments,url'
        )

        self.pr_data = json.loads(pr_json)
        return self.pr_data

    def analyze_pr(self) -> List[Dict[str, Any]]:
        """Analyze PR and generate review comments based on engineering principles."""
        print("Analyzing PR...")

        comments = []
        files = self.pr_data.get('files', [])

        # Identify file types
        code_files = [f for f in files if f['path'].endswith(('.py', '.js', '.ts', '.cs', '.java'))]
        test_files = [f for f in files if 'test' in f['path'].lower()]
        cs_files = [f for f in files if f['path'].endswith('.cs')]
        py_files = [f for f in files if f['path'].endswith('.py')]

        # Differentiate between CLI code and GitHub Actions code
        cli_code_files = [
            f for f in code_files
            if not f['path'].startswith(('.github/', 'autoTriage/'))
            and 'workflow' not in f['path'].lower()
        ]

        github_actions_files = [
            f for f in code_files
            if f['path'].startswith(('.github/', 'autoTriage/'))
            or 'workflow' in f['path'].lower()
        ]

        # Determine the primary context of this PR
        is_cli_pr = len(cli_code_files) > len(github_actions_files)
        is_github_actions_pr = len(github_actions_files) > 0 and not is_cli_pr

        # 1. Check for missing tests
        if cli_code_files and not test_files:
            # CLI code without tests - BLOCKING
            test_framework = 'xUnit, FluentAssertions, and NSubstitute' if cs_files else 'pytest or unittest'
            comments.append({
                'type': 'change_request',
                'severity': 'blocking',
                'enabled': True,
                'body': f"""**Missing Tests**: No test files found for CLI code changes. This violates the principle of reliable CLI development.

**Required**: Add quality test coverage using {test_framework}.
- Focus on quality over quantity - test critical paths and edge cases
- Mock external dependencies properly
- Ensure tests are reliable and maintainable

The CLI MUST be reliable."""
            })
        elif github_actions_files and not test_files:
            # GitHub Actions code without tests - HIGH (not blocking, but strongly recommended)
            test_framework = 'pytest or unittest' if py_files else 'appropriate testing frameworks'
            comments.append({
                'type': 'change_request',
                'severity': 'high',
                'enabled': True,
                'body': f"""**Missing Tests**: No test files found for GitHub Actions code. Tests improve reliability and debugging.

**Recommended**: Add test coverage using {test_framework}:
- Unit tests for service modules (github_service, llm_service, intake_service)
- Integration tests for the workflow orchestration
- Mock tests for external API calls (GitHub API, Azure OpenAI)
- Test error handling and edge cases

Testing GitHub Actions code makes it easier to maintain and debug issues."""
            })

        # 2. Check for large files with specific refactoring suggestions
        for file in files:
            additions = file.get('additions', 0)
            if additions > 500:
                # Generate specific refactoring suggestions based on filename
                file_name = file['path'].split('/')[-1]
                base_name = file_name.rsplit('.', 1)[0]

                # Provide file-specific suggestions
                if 'service' in file_name.lower():
                    # Service files - split by domain/responsibility
                    if 'github' in file_name.lower():
                        suggestions = f"""Consider splitting {file_name} into:
- github_issue_service.py - Reading/fetching issue details
- github_comment_service.py - Posting comments and suggestions
- github_label_service.py - Label management operations

Each service should focus on a specific GitHub API domain."""
                    elif 'llm' in file_name.lower() or 'ai' in file_name.lower():
                        suggestions = f"""Consider splitting {file_name} into:
- llm_client.py - Azure OpenAI client wrapper and connection management
- prompt_builder.py - Prompt construction and template handling
- response_parser.py - Parse and validate LLM responses

This separates concerns: API interaction, prompt logic, and response handling."""
                    elif 'intake' in file_name.lower():
                        suggestions = f"""Consider splitting {file_name} into:
- input_validator.py - Validate and sanitize inputs
- workflow_orchestrator.py - Coordinate service calls
- data_transformer.py - Transform data between services

This applies Single Responsibility Principle to each module."""
                    else:
                        suggestions = f"""Consider splitting {file_name} into smaller, focused services:
- Each handling a single domain or responsibility
- One class per file
- Clear, specific names reflecting their purpose"""
                elif 'controller' in file_name.lower() or 'handler' in file_name.lower():
                    suggestions = f"""Consider splitting {file_name} into:
- Separate handlers for each endpoint/action
- Move business logic to service classes
- Keep controllers thin - only routing and validation"""
                elif 'utils' in file_name.lower() or 'helpers' in file_name.lower():
                    suggestions = f"""Consider organizing {file_name} into:
- Separate utility modules by concern (string_utils, file_utils, etc.)
- Each utility file with related functions only
- Move complex logic to dedicated service classes"""
                else:
                    suggestions = f"""Consider refactoring {file_name}:
- Extract classes/functions into separate files by responsibility
- Each file should have one clear purpose
- Use descriptive names that reflect specific functionality"""

                comments.append({
                    'file': file['path'],
                    'type': 'change_request',
                    'severity': 'high',
                    'enabled': True,
                    'body': f"""**Large File**: This file has {additions} additions.

{suggestions}"""
                })

        # 3. Check for cross-platform issues (only for CLI code, not GitHub Actions)
        # Skip cross-platform checks for GitHub Actions and workflows
        cli_code_files = [
            f for f in files
            if f['path'].endswith(('.cs', '.py', '.js', '.ts'))
            and not f['path'].startswith(('.github/', 'autoTriage/'))
            and 'workflow' not in f['path'].lower()
        ]

        if cli_code_files:
            comments.append({
                'type': 'comment',
                'severity': 'medium',
                'enabled': True,
                'body': """**Review Required - CLI Code**: Check for cross-platform issues in CLI code:
- Hardcoded paths (/tmp/, C:\\, etc.) - use Path.GetTempPath() or tempfile module
- Path separators - use Path.Combine() or os.path.join()
- Line endings - ensure consistent handling
- Case-sensitive file operations

Note: GitHub Actions code (autoTriage/, .github/workflows/) runs on Linux runners and doesn't need cross-platform checks.
The CLI must work across Windows, Linux, and macOS."""
            })

        # 4. Check for potential secrets
        for file in files:
            path_lower = file['path'].lower()
            if any(keyword in path_lower for keyword in ['secret', 'key', 'token', 'password', '.env']):
                if not path_lower.endswith(('.example', '.sample', '.template')):
                    # Determine if this is CLI or GitHub Actions context
                    is_cli_file = not file['path'].startswith(('.github/', 'autoTriage/'))

                    if is_cli_file:
                        credential_guidance = "- Follow az cli patterns for credential management"
                    else:
                        credential_guidance = "- Use GitHub Secrets for sensitive values\n- Access via environment variables in workflow"

                    comments.append({
                        'file': file['path'],
                        'type': 'change_request',
                        'severity': 'blocking',
                        'enabled': True,
                        'body': f"""**Security**: This file may contain secrets or API keys.

**Required**:
- Ensure no sensitive data is committed
- Use environment variables for secrets
{credential_guidance}
- Validate credentials before use (check for null/empty)
- Handle credential errors gracefully with clear error messages"""
                    })

        # 5. Check documentation files (avoid unnecessary docs)
        doc_files = [f for f in files if f['path'].endswith(('.md', '.txt', '.doc'))]
        if len(doc_files) > 2:
            comments.append({
                'type': 'comment',
                'severity': 'low',
                'enabled': True,
                'body': f"""**Documentation**: {len(doc_files)} documentation files changed.

**Review**: Ensure you're not creating unnecessary documentation.
- Focus on code comments and inline help
- Keep docs minimal and maintainable
- Prefer self-documenting code over external docs"""
            })

        # Note: Removed generic code quality checklist comment
        # The principles guide the specific comments above, but we don't post
        # generic checklists as PR comments - only specific, actionable feedback

        return comments

    def generate_comments_file(self, comments: List[Dict], output_path: Path):
        """Generate YAML file with review comments."""
        review_data = {
            'pr_number': self.pr_number,
            'pr_title': self.pr_data.get('title', ''),
            'pr_url': self.pr_data.get('url', ''),
            'overall_decision': 'COMMENT',  # APPROVE | REQUEST_CHANGES | COMMENT
            'overall_body': f"""Thanks for the PR! I've reviewed the changes and left some comments below.

**Summary:**
- Files changed: {len(self.pr_data.get('files', []))}
- Generated comments: {len(comments)}

Please address the comments and let me know if you have any questions.""",
            'comments': comments
        }

        with open(output_path, 'w') as f:
            yaml.dump(review_data, f, default_flow_style=False, sort_keys=False)

        print(f"[OK] Review comments saved to: {output_path}")
        return output_path

    def preview_comments(self, comments_file: Path):
        """Display preview of comments."""
        with open(comments_file, 'r') as f:
            data = yaml.safe_load(f)

        print(f"\n{'='*60}")
        print(f"PR #{data['pr_number']}: {data['pr_title']}")
        print(f"{'='*60}")
        print(f"Decision: {data['overall_decision']}")
        print(f"Overall: {data['overall_body'][:100]}...")
        print(f"\nComments ({len(data['comments'])}):")

        for i, comment in enumerate(data['comments'], 1):
            if comment.get('enabled', True):
                print(f"\n{i}. [{comment['severity'].upper()}] {comment.get('file', 'General')}")
                print(f"   {comment['body'][:80]}...")

    def post_review(self, comments_file: Path):
        """Post review comments to GitHub."""
        with open(comments_file, 'r') as f:
            data = yaml.safe_load(f)

        enabled_comments = [c for c in data['comments'] if c.get('enabled', True)]

        if self.dry_run:
            print("\n[DRY RUN - No changes will be made]")
            self.preview_comments(comments_file)
            return

        print(f"\nPosting {len(enabled_comments)} comments to PR #{self.pr_number}...")

        # Post overall review
        decision = data['overall_decision'].lower()
        overall_body = data['overall_body'].replace('"', '\\"').replace('\n', '\\n')

        self.run_command(
            f'gh pr review {self.pr_number} --{decision} --body "{overall_body}"'
        )

        print("[OK] Overall review posted")

        # Post individual comments
        for i, comment in enumerate(enabled_comments, 1):
            body = comment['body'].replace('"', '\\"').replace('\n', '\\n')

            print(f"  [{i}/{len(enabled_comments)}] Posting comment...")

            self.run_command(
                f'gh pr comment {self.pr_number} --body "{body}"',
                check=False
            )

            time.sleep(0.5)  # Rate limiting

        print(f"\n[OK] Successfully posted review to PR #{self.pr_number}")
        print(f"  View at: {data['pr_url']}")


def main():
    """Main entry point."""
    parser = argparse.ArgumentParser(
        description='Generate and post structured PR review comments'
    )
    parser.add_argument(
        'pr_number',
        type=int,
        help='Pull request number'
    )
    parser.add_argument(
        '--post',
        action='store_true',
        help='Post the review to GitHub (reads from existing YAML file)'
    )
    parser.add_argument(
        '--output',
        type=Path,
        default=None,
        help='Output file path for comments YAML'
    )

    args = parser.parse_args()

    # Create output path
    if args.output is None:
        output_dir = Path(tempfile.gettempdir()) / 'pr-reviews'
        output_dir.mkdir(exist_ok=True)
        args.output = output_dir / f'pr-{args.pr_number}-review.yaml'

    # Create reviewer
    reviewer = PRReviewer(args.pr_number, dry_run=False)

    # Execute workflow
    try:
        if args.post:
            # POST mode: Read existing YAML and post to GitHub
            if not args.output.exists():
                print(f"Error: Review file not found: {args.output}", file=sys.stderr)
                print(f"\nGenerate the review first by running:", file=sys.stderr)
                print(f"  /review-pr {args.pr_number}", file=sys.stderr)
                sys.exit(1)

            print(f"Reading review from: {args.output}")
            reviewer.preview_comments(args.output)

            print(f"\n" + "="*60)
            print(f"Ready to post review to PR #{args.pr_number}")
            print("="*60)

            reviewer.post_review(args.output)
        else:
            # GENERATE mode (default): Fetch, analyze, generate YAML, preview
            # 1. Fetch PR details
            reviewer.fetch_pr_details()

            # 2. Analyze and generate comments
            comments = reviewer.analyze_pr()

            # 3. Generate YAML file
            comments_file = reviewer.generate_comments_file(comments, args.output)

            # 4. Preview
            reviewer.preview_comments(comments_file)

            # 5. Instructions for next step
            print(f"\n" + "="*60)
            print(f"Review file generated: {comments_file}")
            print("="*60)
            print(f"\n[OK] Review the file and edit if needed.")
            print(f"When ready to post, run:")
            print(f"  /review-pr {args.pr_number} --post")

    except KeyboardInterrupt:
        print("\n\nCancelled by user.")
        sys.exit(1)
    except Exception as e:
        print(f"\nError: {e}", file=sys.stderr)
        import traceback
        traceback.print_exc()
        sys.exit(1)


if __name__ == '__main__':
    main()
