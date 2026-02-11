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
        self.pr_diff = None
        self.repo_guidelines = None

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
            error_msg = result.stderr if result.stderr else "Command failed"
            raise Exception(error_msg)
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

    def fetch_pr_diff(self) -> str:
        """Fetch the actual diff for the PR."""
        print(f"Fetching PR diff...")
        self.pr_diff = self.run_command(f'gh pr diff {self.pr_number}')
        return self.pr_diff

    def load_repo_guidelines(self):
        """Load repository-specific guidelines from .github/copilot-instructions.md."""
        guidelines_path = Path('.github/copilot-instructions.md')
        if guidelines_path.exists():
            print(f"Loading repository guidelines from {guidelines_path}...")
            with open(guidelines_path, 'r', encoding='utf-8') as f:
                self.repo_guidelines = f.read()
        else:
            print(f"No repository guidelines found at {guidelines_path}")
            self.repo_guidelines = None
        return self.repo_guidelines

    def parse_diff_by_file(self) -> Dict[str, Dict[str, Any]]:
        """Parse the diff and extract added/modified code by file."""
        if not self.pr_diff:
            return {}

        files_data = {}
        current_file = None
        current_additions = []

        for line in self.pr_diff.split('\n'):
            # New file marker
            if line.startswith('diff --git'):
                if current_file and current_additions:
                    files_data[current_file]['added_lines'] = '\n'.join(current_additions)

                # Extract filename from "diff --git a/path/file b/path/file"
                parts = line.split(' ')
                if len(parts) >= 3:
                    current_file = parts[2][2:]  # Remove "a/" prefix
                    files_data[current_file] = {
                        'added_lines': '',
                        'removed_lines': '',
                        'chunks': []
                    }
                    current_additions = []

            # Track added lines (start with +, but not +++)
            elif line.startswith('+') and not line.startswith('+++'):
                current_additions.append(line[1:])  # Remove + prefix

        # Don't forget the last file
        if current_file and current_additions:
            files_data[current_file]['added_lines'] = '\n'.join(current_additions)

        return files_data

    def check_kairo_keyword(self, files_data: Dict[str, Dict[str, Any]]) -> List[Dict[str, Any]]:
        """Check for 'Kairo' keyword in C# files (Rule 1 from copilot-instructions.md)."""
        comments = []

        for file_path, data in files_data.items():
            if not file_path.endswith('.cs'):
                continue

            added_code = data.get('added_lines', '')
            if 'kairo' in added_code.lower():
                # Find the specific occurrences
                lines_with_kairo = [
                    line for line in added_code.split('\n')
                    if 'kairo' in line.lower()
                ]

                comments.append({
                    'file': file_path,
                    'type': 'change_request',
                    'severity': 'high',
                    'enabled': True,
                    'body': f"""**Kairo Keyword Found** (Rule 1: copilot-instructions.md)

The keyword "Kairo" was found in this C# file. This appears to be a legacy reference that needs to be updated.

**Occurrences:**
```
{chr(10).join(lines_with_kairo[:5])}
```

**Required Action:**
- Remove or replace "Kairo" with appropriate terminology
- Check if this is a legacy reference that needs updating
- Update class names, namespaces, variables, or comments as needed"""
                })

        return comments

    def check_copyright_headers(self, files_data: Dict[str, Dict[str, Any]]) -> List[Dict[str, Any]]:
        """Check for copyright headers in C# files (Rule 2 from copilot-instructions.md)."""
        comments = []
        required_header = "// Copyright (c) Microsoft Corporation.\n// Licensed under the MIT License."

        for file_path, _ in files_data.items():
            # Only check C# files, skip auto-generated and test files (relaxed requirement)
            if not file_path.endswith('.cs'):
                continue
            if any(x in file_path.lower() for x in ['.designer.cs', '.g.cs', 'assemblyinfo.cs']):
                continue

            # For new files or significantly modified files, check for copyright header
            # We need to fetch the full file content to check the header
            try:
                file_content = self.run_command(f'gh pr view {self.pr_number} --json files --jq \'.files[] | select(.path=="{file_path}") | .additions\'', check=False)
                additions = int(file_content) if file_content and file_content.isdigit() else 0

                # Only check files with significant additions (likely new or heavily modified)
                if additions < 10:
                    continue

                # Try to read the file from the PR branch
                file_content_cmd = f'gh api repos/{{owner}}/{{repo}}/contents/{file_path}?ref=refs/pull/{self.pr_number}/head'
                result = self.run_command(file_content_cmd, check=False)

                if result:
                    import base64
                    content_data = json.loads(result)
                    if 'content' in content_data:
                        file_content = base64.b64decode(content_data['content']).decode('utf-8', errors='replace')

                        # Check if header is present
                        if required_header not in file_content[:500]:  # Check first 500 chars
                            comments.append({
                                'file': file_path,
                                'type': 'change_request',
                                'severity': 'medium',
                                'enabled': True,
                                'body': f"""**Missing Copyright Header** (Rule 2: copilot-instructions.md)

This C# file is missing the required Microsoft copyright header.

**Required header:**
```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
```

**Action Required:**
- Add the copyright header at the top of the file
- Place it before any using statements or code
- Ensure there's a blank line after the header

**Note:** Auto-generated files (.g.cs, .designer.cs) are excluded from this requirement."""
                            })
            except Exception:
                # If we can't fetch the file, skip the check
                pass

        return comments

    def detect_large_functions(self, files_data: Dict[str, Dict[str, Any]]) -> List[Dict[str, Any]]:
        """Detect large functions (>100 lines) in added code."""
        comments = []

        for file_path, data in files_data.items():
            added_code = data.get('added_lines', '')
            if not added_code:
                continue

            # Parse the actual diff to get full file context and count real function lines
            # Use gh pr diff to get the full diff with context
            try:
                # Get the full file content from the PR branch to count accurate function lines
                file_content_result = self.run_command(
                    f'gh api repos/{{owner}}/{{repo}}/contents/{file_path}?ref=refs/pull/{self.pr_number}/head',
                    check=False
                )

                if file_content_result:
                    import base64
                    content_data = json.loads(file_content_result)
                    if 'content' in content_data:
                        full_file_content = base64.b64decode(content_data['content']).decode('utf-8', errors='replace')

                        # Now count actual function lines in the full file
                        lines = full_file_content.split('\n')
                        current_function = None
                        function_start = 0
                        brace_count = 0
                        in_function = False

                        for i, line in enumerate(lines):
                            stripped = line.strip()

                            # Detect function/method definitions
                            is_function_def = (
                                (stripped.startswith('def ') and '(' in stripped and ':' in stripped) or  # Python
                                (any(stripped.startswith(mod) for mod in ['public ', 'private ', 'protected ', 'internal '])
                                 and '(' in stripped) or  # C# methods
                                ('function ' in stripped and '(' in stripped) or  # JavaScript
                                ('=>' in stripped and '{' in stripped)  # Arrow functions
                            )

                            if is_function_def and not in_function:
                                # Check if previous function was added in this PR
                                if current_function:
                                    function_lines = i - function_start
                                    # Check if this function was modified in the PR
                                    if any(current_function in added_code for _ in [1]) and function_lines > 100:
                                        comments.append({
                                            'file': file_path,
                                            'type': 'comment',
                                            'severity': 'medium',
                                            'enabled': True,
                                            'body': f"""**Large Function**: `{current_function}` ({function_lines} lines)

**Engineering Principle**: SOLID - Single Responsibility Principle (copilot-instructions.md)

**Recommendation**: Break this function into smaller, focused methods:
- Extract logical blocks into separate methods
- Each method should have one clear responsibility
- Aim for functions under 50 lines for better readability and testability

This will:
- Make the code easier to test and maintain
- Improve readability and debuggability
- Follow the single responsibility principle"""
                                        })

                                # Extract function name
                                if 'def ' in stripped:
                                    current_function = stripped.split('def ')[1].split('(')[0].strip()
                                else:
                                    current_function = stripped.split('(')[0].strip().split()[-1]

                                function_start = i
                                in_function = True
                                brace_count = stripped.count('{') - stripped.count('}')

                            elif in_function:
                                brace_count += stripped.count('{') - stripped.count('}')
                                # Python function ends when indentation returns to function level
                                if file_path.endswith('.py'):
                                    if stripped and not line.startswith(' ') and not line.startswith('\t') and i > function_start:
                                        in_function = False
                                # C#/JS functions end when braces balance
                                elif brace_count == 0 and i > function_start:
                                    in_function = False

                        # Check last function
                        if current_function and in_function:
                            function_lines = len(lines) - function_start
                            if any(current_function in added_code for _ in [1]) and function_lines > 100:
                                comments.append({
                                    'file': file_path,
                                    'type': 'comment',
                                    'severity': 'medium',
                                    'enabled': True,
                                    'body': f"""**Large Function**: `{current_function}` ({function_lines} lines)

**Engineering Principle**: SOLID - Single Responsibility Principle (copilot-instructions.md)

**Recommendation**: Break this function into smaller, focused methods."""
                                })
            except Exception:
                # Fallback to simple line counting from diff if file fetch fails
                lines = added_code.split('\n')
                current_function = None
                function_lines = 0

                for _, line in enumerate(lines):
                    stripped = line.strip()

                    is_function = (
                        (stripped.startswith('def ') and '(' in stripped) or
                        (any(stripped.startswith(mod) for mod in ['public ', 'private ', 'protected ']) and '(' in stripped) or
                        ('function ' in stripped and '(' in stripped)
                    )

                    if is_function:
                        if current_function and function_lines > 100:
                            comments.append({
                                'file': file_path,
                                'type': 'comment',
                                'severity': 'medium',
                                'enabled': True,
                                'body': f"""**Large Function**: `{current_function}` (~{function_lines} added lines)

**Engineering Principle**: SOLID - Single Responsibility Principle (copilot-instructions.md)

**Recommendation**: Break this function into smaller, focused methods."""
                            })
                        current_function = stripped.split('(')[0].strip().split()[-1]
                        function_lines = 1
                    elif current_function:
                        function_lines += 1

                if current_function and function_lines > 100:
                    comments.append({
                        'file': file_path,
                        'type': 'comment',
                        'severity': 'medium',
                        'enabled': True,
                        'body': f"""**Large Function**: `{current_function}` (~{function_lines} added lines)

**Engineering Principle**: SOLID - Single Responsibility Principle (copilot-instructions.md)

**Recommendation**: Break this function into smaller, focused methods."""
                    })

        return comments

    def detect_resource_leaks(self, files_data: Dict[str, Dict[str, Any]]) -> List[Dict[str, Any]]:
        """Detect IDisposable resource leaks (HttpResponseMessage without using)."""
        comments = []

        for file_path, data in files_data.items():
            # Only check C# files
            if not file_path.endswith('.cs'):
                continue

            added_code = data.get('added_lines', '')
            lines = added_code.split('\n')

            for i, line in enumerate(lines):
                # Check for HttpClient calls without using statement
                if 'HttpClient' in line and any(method in line for method in ['GetAsync', 'PostAsync', 'PutAsync', 'DeleteAsync', 'SendAsync']):
                    # Check if this line or previous lines have 'using var' or 'using ('
                    context_start = max(0, i - 3)
                    context = '\n'.join(lines[context_start:i+1])

                    if 'using var' not in context and 'using (' not in context and 'await using' not in context:
                        comments.append({
                            'file': file_path,
                            'type': 'change_request',
                            'severity': 'high',
                            'enabled': True,
                            'body': f"""**Resource Leak Detected** (copilot-instructions.md: Resource Management)

HttpResponseMessage must be disposed properly. The code appears to call HttpClient methods without a using statement.

**Problematic pattern:**
```csharp
{line.strip()}
```

**Required pattern:**
```csharp
using var response = await httpClient.GetAsync(url, cancellationToken);
if (!response.IsSuccessStatusCode) {{ return null; }}
var content = await response.Content.ReadAsStringAsync(cancellationToken);
```

**Why this matters:**
- HttpResponseMessage is IDisposable and must be disposed
- Resource leaks can cause memory issues and connection exhaustion
- Using statements ensure proper cleanup even if exceptions occur

**Action Required:** Wrap the HttpClient call with a using statement."""
                        })

        return comments

    def detect_code_duplication(self, files_data: Dict[str, Dict[str, Any]]) -> List[Dict[str, Any]]:
        """Detect potential code duplication by comparing against existing codebase."""
        comments = []

        # For each file being modified, check for similar files in the repo
        for file_path, data in files_data.items():
            added_code = data.get('added_lines', '')
            if not added_code or len(added_code) < 200:  # Skip small additions
                continue

            # Extract the directory and filename pattern
            path_parts = Path(file_path).parts
            if len(path_parts) < 2:
                continue

            directory = str(Path(file_path).parent)
            filename = Path(file_path).name

            # Look for similar files in the same directory
            try:
                similar_files_cmd = f'git ls-files "{directory}/*"'
                similar_files = self.run_command(similar_files_cmd, check=False)

                if similar_files:
                    file_list = [f.strip() for f in similar_files.split('\n') if f.strip() and f.strip() != file_path]

                    if len(file_list) > 0:
                        # Check for similar patterns (e.g., service files, controller files)
                        if 'service' in filename.lower():
                            similar = [f for f in file_list if 'service' in f.lower()]
                        elif 'controller' in filename.lower():
                            similar = [f for f in file_list if 'controller' in f.lower()]
                        elif 'handler' in filename.lower():
                            similar = [f for f in file_list if 'handler' in f.lower()]
                        else:
                            similar = file_list[:3]  # Just take first few files

                        if similar:
                            comments.append({
                                'file': file_path,
                                'type': 'comment',
                                'severity': 'low',
                                'enabled': True,
                                'body': f"""**Code Duplication Check** (copilot-instructions.md: DRY Principle)

Similar files exist in this directory. Please verify you're not duplicating existing functionality.

**Similar files to review:**
{chr(10).join([f'- `{f}`' for f in similar[:5]])}

**Action:**
- Review these files for reusable functions
- Extract common functionality into shared utilities
- Ensure you're following existing patterns and conventions
- Consider if this new code could extend an existing file instead of creating duplication

**DRY Principle**: Don't Repeat Yourself - reuse existing code where possible."""
                            })
            except Exception:
                # If git ls-files fails, skip duplication check
                pass

        return comments

    def detect_hardcoded_secrets(self, files_data: Dict[str, Dict[str, Any]]) -> List[Dict[str, Any]]:
        """Detect hardcoded secrets, API keys, and hardcoded paths in code."""
        comments = []
        import re

        # Patterns to detect
        secret_patterns = [
            ('password', r'password\s*=\s*["\'][^"\']{3,}["\']'),
            ('api_key', r'api[_-]?key\s*=\s*["\'][^"\']{10,}["\']'),
            ('token', r'token\s*=\s*["\'][^"\']{10,}["\']'),
            ('secret', r'secret\s*=\s*["\'][^"\']{10,}["\']'),
            ('connection_string', r'(Server=|Data Source=|mongodb://|postgresql://)'),
        ]

        path_patterns = [
            ('windows_path', r'[Cc]:\\\\[\\w\\\\]+'),
            ('unix_absolute_path', r'\/tmp\/|\/var\/|\/usr\/'),
        ]

        for file_path, data in files_data.items():
            added_code = data.get('added_lines', '')
            lines = added_code.split('\n')

            issues_found = []

            for i, line in enumerate(lines):
                # Skip comments and test files
                if file_path.lower().endswith(('test.cs', 'test.py', 'test.js', 'test.ts', '.spec.', '.test.')):
                    continue
                if line.strip().startswith(('/', '#', '*', '--')):
                    continue

                # Check for hardcoded secrets
                line_lower = line.lower()
                for pattern_name, pattern in secret_patterns:
                    if re.search(pattern, line_lower):
                        # Make sure it's not an environment variable reference
                        if 'Environment.GetEnvironmentVariable' not in line and 'os.getenv' not in line and 'process.env' not in line:
                            issues_found.append(f"Line {i+1}: Possible hardcoded {pattern_name}")

                # Check for hardcoded paths (only for CLI code, not GitHub Actions)
                if not file_path.startswith(('.github/', 'autoTriage/')):
                    for pattern_name, pattern in path_patterns:
                        if re.search(pattern, line):
                            # Skip if it's using Path.GetTempPath or similar
                            if 'GetTempPath' not in line and 'tempfile' not in line:
                                issues_found.append(f"Line {i+1}: Hardcoded {pattern_name}")

            if issues_found:
                is_cli = not file_path.startswith(('.github/', 'autoTriage/'))
                recommendations = """**For CLI code:**
- Use environment variables for secrets
- Use Path.GetTempPath() or tempfile module for temp paths
- Use Path.Combine() or os.path.join() for path construction
- Follow az cli patterns for credential management

**For GitHub Actions:**
- Use GitHub Secrets: `${{ secrets.SECRET_NAME }}`
- Access via environment variables in Python/scripts"""

                comments.append({
                    'file': file_path,
                    'type': 'change_request',
                    'severity': 'high',
                    'enabled': True,
                    'body': f"""**Security: Hardcoded Secrets/Paths Detected** (copilot-instructions.md)

Potential hardcoded sensitive values or platform-specific paths were found:

{chr(10).join(['- ' + issue for issue in issues_found[:10]])}

{recommendations if is_cli else '**Use GitHub Secrets for sensitive values**'}

**Why this matters:**
- Hardcoded secrets are security vulnerabilities
- Hardcoded paths break cross-platform compatibility
- Credentials should never be committed to the repository"""
                })

        return comments

    def analyze_workflow_permissions(self, files_data: Dict[str, Dict[str, Any]]) -> List[Dict[str, Any]]:
        """Analyze workflow permission changes for least privilege violations."""
        comments = []

        for file_path, data in files_data.items():
            if not file_path.endswith(('.yml', '.yaml')) or not file_path.startswith('.github/workflows/'):
                continue

            added_code = data.get('added_lines', '')
            lines = added_code.split('\n')

            permissions_found = []
            for i, line in enumerate(lines):
                stripped = line.strip()
                # Look for permissions being added
                if stripped.startswith(('pull-requests:', 'actions:', 'contents:', 'issues:', 'deployments:')):
                    perm_parts = stripped.split(':')
                    if len(perm_parts) >= 2:
                        perm_name = perm_parts[0].strip()
                        perm_value = perm_parts[1].strip()
                        permissions_found.append((perm_name, perm_value, i+1))

            if permissions_found:
                perm_list = '\n'.join([f"- `{p[0]}: {p[1]}` (line {p[2]})" for p in permissions_found])

                comments.append({
                    'file': file_path,
                    'type': 'comment',
                    'severity': 'medium',
                    'enabled': True,
                    'body': f"""**Workflow Permissions Review**: New permissions added

**Engineering Principle**: Principle of Least Privilege (Security best practice)

**Permissions added:**
{perm_list}

**Review Required:**
- Verify each permission is actually required for the workflow to function
- Check if write access can be downgraded to read access
- Ensure permissions are scoped to specific jobs if possible (use `permissions:` at job level, not workflow level)
- Document WHY each permission is needed (inline comments are excellent)

**Questions to answer:**
- Can any of these be removed or downgraded?
- Are they documented with inline comments explaining their necessity?
- Could job-level permissions be more restrictive than workflow-level?

**Documentation**: If permissions are required, add inline comments explaining why (as done in this PR)."""
                })

        return comments


    def analyze_test_quality(self, files_data: Dict[str, Dict[str, Any]]) -> List[Dict[str, Any]]:
        """Analyze test file quality and coverage patterns."""
        comments = []

        for file_path, data in files_data.items():
            if 'test' not in file_path.lower():
                continue

            added_code = data.get('added_lines', '')
            if not added_code:
                continue

            test_quality_indicators = []
            test_issues = []

            # Count test functions/methods
            test_count = added_code.count('def test_') + added_code.count('[Fact]') + added_code.count('[Theory]') + added_code.count('it(')

            # Check for parameterized tests
            if '@pytest.mark.parametrize' in added_code or '[Theory]' in added_code or 'test.each' in added_code:
                test_quality_indicators.append(f"Parameterized tests for testing multiple scenarios efficiently")

            # Check for mocking
            if any(pattern in added_code for pattern in ['mock', 'Mock', 'patch', 'NSubstitute', 'Substitute.For']):
                test_quality_indicators.append("Proper mocking of external dependencies")

            # Check for edge case testing
            edge_case_keywords = ['null', 'None', 'empty', 'invalid', 'error', 'exception', 'edge', 'boundary']
            edge_case_count = sum(keyword in added_code.lower() for keyword in edge_case_keywords)
            if edge_case_count >= 3:
                test_quality_indicators.append("Edge case and error scenario testing")

            # Check for helper/setup functions
            if any(pattern in added_code for pattern in ['setUp', 'setup', 'beforeEach', 'fixture', '@pytest.fixture', 'make_']):
                test_quality_indicators.append("Test helper functions and fixtures for reusable test setup")

            # Check for assertions
            assertion_keywords = ['assert', 'Assert.', 'expect', 'Should']
            assertion_count = sum(added_code.count(keyword) for keyword in assertion_keywords)

            # Check for regression tests
            if 'regression' in added_code.lower() or 'bug' in added_code.lower():
                test_quality_indicators.append("Regression tests for bug fixes")

            # Issues to flag
            if assertion_count < test_count:
                test_issues.append("Some test functions may be missing assertions")

            # Only comment if significant test additions
            if test_count >= 3:
                if len(test_quality_indicators) >= 3:
                    # Positive feedback
                    comments.append({
                        'file': file_path,
                        'type': 'comment',
                        'severity': 'info',
                        'enabled': True,
                        'body': f"""**Excellent Test Coverage**

**Test Quality**: {test_count} test functions/methods added with comprehensive coverage

**Strengths:**
{chr(10).join([f'- {indicator}' for indicator in test_quality_indicators])}

**Test Quality**: Follows testing best practices from copilot-instructions.md:
- Focus on critical paths and edge cases
- Proper mocking patterns
- Quality over quantity (though this has both!)

Great work on comprehensive test coverage!"""
                    })
                elif test_issues:
                    # Constructive feedback
                    comments.append({
                        'file': file_path,
                        'type': 'comment',
                        'severity': 'low',
                        'enabled': True,
                        'body': f"""**Test Quality Review**: {test_count} test functions added

**Suggestions for improvement:**
{chr(10).join([f'- {issue}' for issue in test_issues])}

**Consider adding:**
- Parameterized tests for multiple scenarios
- Edge case testing (null values, empty inputs, boundary conditions)
- Mock external dependencies properly"""
                    })

        return comments

    def analyze_pr_context(self, files_data: Dict[str, Dict[str, Any]]) -> Dict[str, Any]:
        """Analyze PR context (title, description) to understand intent and justify complexity."""
        pr_body = self.pr_data.get('body', '') or ''
        pr_title = self.pr_data.get('title', '') or ''
        pr_context = f"{pr_title}\n{pr_body}".lower()

        context_info = {
            'is_bug_fix': any(keyword in pr_context for keyword in ['fix', 'bug', 'error', '403', '404', '500', 'crash', 'issue']),
            'is_new_feature': any(keyword in pr_context for keyword in ['add', 'new feature', 'implement', 'support for']),
            'is_refactor': any(keyword in pr_context for keyword in ['refactor', 'reorganize', 'restructure', 'cleanup']),
            'is_security_fix': any(keyword in pr_context for keyword in ['security', 'vulnerability', 'cve', 'injection', 'xss']),
            'mentions_complexity': any(keyword in pr_context for keyword in ['complex', 'graphql', 'api change', 'migration']),
            'explains_why': any(keyword in pr_context for keyword in ['because', 'since', 'due to', 'in order to', 'fixes', 'resolves']),
        }

        return context_info

    def check_large_file_with_context(self, files_data: Dict[str, Dict[str, Any]], pr_context: Dict[str, Any]) -> List[Dict[str, Any]]:
        """Check for large file additions with context-aware analysis."""
        comments = []
        files = self.pr_data.get('files', [])

        for file in files:
            additions = file.get('additions', 0)
            if additions > 200:  # Significant file addition
                file_name = file['path'].split('/')[-1]

                # Build context-aware message
                severity = 'high'
                context_note = ""

                if pr_context['is_bug_fix'] and pr_context['explains_why']:
                    severity = 'low'
                    context_note = f"""
**Context**: This PR fixes a bug/error (per PR description). The added complexity appears justified by the fix.

**Recommendation**: Consider adding inline comments explaining WHY this approach is needed (e.g., "REST API returns 403; GraphQL with feature headers is required")."""

                elif pr_context['is_security_fix']:
                    severity = 'low'
                    context_note = f"""
**Context**: This PR addresses a security issue. Security fixes often require additional complexity for proper mitigation."""

                else:
                    context_note = f"""
**Question**: Is this the simplest solution? Consider if the complexity is justified."""

                # Provide file-specific suggestions based on file type
                if 'service' in file_name.lower():
                    suggestions = f"""Consider splitting {file_name} into smaller, focused services:
- Each handling a single domain or responsibility
- One class per file
- Clear, specific names reflecting their purpose"""
                else:
                    suggestions = f"""Consider refactoring {file_name}:
- Extract classes/functions into separate files by responsibility
- Each file should have one clear purpose"""

                comments.append({
                    'file': file['path'],
                    'type': 'comment',
                    'severity': severity,
                    'enabled': True,
                    'body': f"""**Large File Addition**: This file has {additions} additions.

**Engineering Principle**: KISS - Keep It Simple, Stupid (copilot-instructions.md)

{context_note}

{suggestions}"""
                })

        return comments

    def analyze_pr(self) -> List[Dict[str, Any]]:
        """Analyze PR and generate review comments based on engineering principles."""
        print("Analyzing PR...")

        comments = []
        files = self.pr_data.get('files', [])

        # Parse the diff to get actual code changes
        files_data = self.parse_diff_by_file()
        print(f"  Parsed diff for {len(files_data)} files")

        # Analyze PR context to understand intent
        pr_context = self.analyze_pr_context(files_data)
        print(f"  PR Context: Bug fix={pr_context['is_bug_fix']}, Feature={pr_context['is_new_feature']}, Security={pr_context['is_security_fix']}")

        # Run deep code analysis checks on the diff
        print("  Running code analysis checks...")
        comments.extend(self.check_kairo_keyword(files_data))
        comments.extend(self.check_copyright_headers(files_data))
        comments.extend(self.detect_large_functions(files_data))
        comments.extend(self.detect_resource_leaks(files_data))
        comments.extend(self.detect_hardcoded_secrets(files_data))
        comments.extend(self.detect_code_duplication(files_data))
        comments.extend(self.analyze_workflow_permissions(files_data))
        comments.extend(self.analyze_test_quality(files_data))
        comments.extend(self.check_large_file_with_context(files_data, pr_context))
        print(f"  Code analysis generated {len(comments)} comments")

        # Track which files we've reviewed
        reviewed_files = set()

        # Helper function to categorize files
        def get_file_category(file_path):
            # Check for test files FIRST (before code) to avoid miscategorization
            if 'test' in file_path.lower():
                return 'test'
            elif file_path.startswith('.github/workflows/') and file_path.endswith(('.yml', '.yaml')):
                return 'workflow'
            elif file_path.endswith(('.py', '.js', '.ts', '.cs', '.java', '.go', '.rb', '.php', '.cpp', '.c', '.h')):
                return 'code'
            elif file_path.endswith(('.yml', '.yaml', '.json', '.toml', '.ini', '.config')):
                return 'config'
            elif file_path.endswith(('.md', '.txt', '.rst')):
                return 'doc'
            elif file_path.endswith(('requirements.txt', 'package.json', 'package-lock.json', 'Gemfile', 'go.mod', '.csproj', 'poetry.lock', 'yarn.lock')):
                return 'dependency'
            elif file_path.endswith(('.gitignore', '.dockerignore', 'Dockerfile', '.env.example')):
                return 'infrastructure'
            else:
                return 'unknown'

        # Categorize ALL files
        code_files = [f for f in files if get_file_category(f['path']) == 'code']
        test_files = [f for f in files if get_file_category(f['path']) == 'test']
        workflow_files = [f for f in files if get_file_category(f['path']) == 'workflow']
        config_files = [f for f in files if get_file_category(f['path']) == 'config']
        doc_files = [f for f in files if get_file_category(f['path']) == 'doc']
        dependency_files = [f for f in files if get_file_category(f['path']) == 'dependency']
        infrastructure_files = [f for f in files if get_file_category(f['path']) == 'infrastructure']
        unknown_files = [f for f in files if get_file_category(f['path']) == 'unknown']

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

        print(f"  Files: {len(files)} total | {len(code_files)} code | {len(workflow_files)} workflow | {len(config_files)} config | {len(unknown_files)} unknown")

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

        # 2. Cross-platform checks moved to detect_hardcoded_secrets (checks for hardcoded paths in code)
        # Large file checks now handled by check_large_file_with_context (context-aware)

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

        # 5. Check workflow files (comment on new or significantly modified workflows)
        for workflow in workflow_files:
            additions = workflow.get('additions', 0)
            workflow_name = workflow['path'].split('/')[-1]

            # Only comment if workflow has significant changes (>20 lines) or is new
            if additions > 20:
                comments.append({
                    'file': workflow['path'],
                    'type': 'comment',
                    'severity': 'medium',
                    'enabled': True,
                    'body': f"""**Workflow File Review**: {workflow_name} ({additions} additions)

**Check for**:
- Secrets properly referenced using `${{{{ secrets.SECRET_NAME }}}}`
- Appropriate timeouts set for jobs and steps
- Minimal permissions granted (use `permissions:` block)
- Proper error handling and failure notifications
- Dependencies between jobs clearly defined
- Triggers appropriate for the workflow purpose
- Consider adding workflow concurrency controls if applicable"""
                })

        # 6. Check large config files
        for config in config_files:
            additions = config.get('additions', 0)
            if additions > 100:
                config_name = config['path'].split('/')[-1]

                comments.append({
                    'file': config['path'],
                    'type': 'comment',
                    'severity': 'low',
                    'enabled': True,
                    'body': f"""**Large Config File**: {config_name} ({additions} additions)

**Review**:
- Ensure no sensitive data (tokens, keys, passwords) in config
- Consider splitting large configs into environment-specific files
- Validate config structure and syntax
- Document any non-obvious configuration options"""
                })

        # 7. Check dependency files (skip lockfiles with no deletions - those are just updates)
        for dep_file in dependency_files:
            dep_name = dep_file['path'].split('/')[-1]
            additions = dep_file.get('additions', 0)
            deletions = dep_file.get('deletions', 0)

            # Skip lockfile-only updates (additions but no deletions usually means regeneration)
            # Comment on actual dependency changes
            is_lockfile = dep_name.endswith(('lock.json', '.lock', 'yarn.lock'))
            if not is_lockfile or deletions > 0 or additions > 100:
                comments.append({
                    'file': dep_file['path'],
                    'type': 'comment',
                    'severity': 'medium',
                    'enabled': True,
                    'body': f"""**Dependency File Modified**: {dep_name} (+{additions}, -{deletions})

**Review**:
- Check for security vulnerabilities in new dependencies
- Verify version pinning strategy (exact vs. range)
- Ensure dependencies are actively maintained
- Check license compatibility
- Consider impact on package size/installation time"""
                })

        # 8. Check documentation files (avoid unnecessary docs)
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

        # 9. List other files that don't have category-specific checks
        if unknown_files:
            file_list = '\n'.join([f"- `{f['path']}` ({f.get('additions', 0)} additions)" for f in unknown_files[:10]])
            if len(unknown_files) > 10:
                file_list += f"\n- ... and {len(unknown_files) - 10} more"

            comments.append({
                'type': 'comment',
                'severity': 'info',
                'enabled': True,
                'body': f"""**Additional Files Modified**: {len(unknown_files)} file(s) without category-specific checks.

**Files**:
{file_list}

**General review points**:
- Verify correctness and completeness
- Check for security implications
- Consider performance impact
- Ensure compatibility with existing code
- Validate any configuration or data changes"""
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

    def generate_manual_format(self, comments_file: Path):
        """Generate markdown file for manual copy/paste posting."""
        with open(comments_file, 'r') as f:
            data = yaml.safe_load(f)

        enabled_comments = [c for c in data['comments'] if c.get('enabled', True)]

        # Create markdown file
        md_file = comments_file.parent / f"pr-{self.pr_number}-review-manual.md"

        with open(md_file, 'w', encoding='utf-8') as f:
            f.write(f"# PR #{self.pr_number} Review Comments\n\n")
            f.write(f"**PR Title:** {data['pr_title']}\n\n")
            f.write(f"**PR URL:** {data['pr_url']}\n\n")
            f.write(f"---\n\n")
            f.write(f"## Instructions\n\n")
            f.write(f"Copy and paste each comment below to the GitHub PR.\n\n")
            f.write(f"For file-specific comments, click on the file in the PR and add the comment to the appropriate location.\n\n")
            f.write(f"---\n\n")

            # Overall review
            f.write(f"## Overall Review\n\n")
            f.write(f"{data['overall_body']}\n\n")
            f.write(f"---\n\n")

            # Individual comments
            f.write(f"## Comments ({len(enabled_comments)})\n\n")

            for i, comment in enumerate(enabled_comments, 1):
                f.write(f"### Comment {i} - [{comment['severity'].upper()}]\n\n")

                if 'file' in comment:
                    f.write(f"**File:** `{comment['file']}`\n\n")
                else:
                    f.write(f"**Location:** General comment\n\n")

                f.write(f"{comment['body']}\n\n")
                f.write(f"---\n\n")

        print(f"\n[OK] Manual format generated: {md_file}")
        print(f"\nOpen the file and copy/paste comments to GitHub:")
        print(f"  {md_file}")
        return md_file

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

        try:
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

        except Exception as e:
            error_msg = str(e)
            # Check if it's a GitHub API permission error
            if 'Unauthorized' in error_msg or 'Enterprise Managed User' in error_msg:
                print(f"\n[WARNING] GitHub API posting failed due to permissions.")
                print(f"Error: {error_msg}")
                print(f"\n[INFO] Generating manual copy/paste format instead...")
                self.generate_manual_format(comments_file)
            else:
                # Re-raise other errors
                raise


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

            # 2. Fetch PR diff for code analysis
            reviewer.fetch_pr_diff()

            # 3. Load repository guidelines
            reviewer.load_repo_guidelines()

            # 4. Analyze and generate comments
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
