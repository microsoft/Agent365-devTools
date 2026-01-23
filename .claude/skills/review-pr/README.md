# PR Review Skill

A Claude Code skill for generating structured, AI-powered PR review comments.

## Quick Start

### Option 1: Via Claude Code Skill (Recommended)
```bash
/review-pr 180
```

This works if the skill is properly integrated (see Setup below).

### Option 2: Run Script Directly
```bash
# Generate review (dry-run - no posting)
python .claude/skills/review-pr/review-pr.py 180 --dry-run

# Post review (after editing YAML)
python .claude/skills/review-pr/review-pr.py 180 --output <yaml-file-path>
```

## Setup for Skill Integration

For `/review-pr` to work in Claude Code:

1. **Ensure files are in correct location:**
   ```
   .claude/skills/review-pr/
   ├── skill.yaml
   ├── review-pr.py
   └── README.md
   ```

2. **Commit and push the skill files:**
   ```bash
   git add .claude/skills/review-pr/
   git commit -m "Add PR review skill"
   git push
   ```

3. **Reload Claude Code:**
   - Restart VSCode, OR
   - Reload Claude Code window

4. **Verify skill is loaded:**
   - Type `/` in Claude Code
   - Look for `review-pr` in the skills list

5. **Use the skill:**
   ```bash
   /review-pr <pr-number>
   ```

## Features

**Automated Analysis**
- Detects missing tests
- Identifies large files (KISS/SRP violations)
- Flags potential security issues
- Checks cross-platform compatibility
- Validates design patterns (SOLID, DRY, YAGNI)
- Reviews error handling patterns

**Structured Comments**
- Editable YAML format
- Severity levels (blocking, high, medium, low)
- Enable/disable individual comments
- File-specific and general comments

**GitHub Integration**
- Posts directly to PR via GitHub CLI
- Supports dry-run preview
- Rate limiting protection

## Engineering Principles

This skill enforces the following code review principles:

### Architecture & Patterns
- **.NET Architecture**: Reviews follow .NET architect best practices
- **Azure CLI Alignment**: Ensures consistency with az cli patterns and conventions
- **Cross-Platform**: Validates Windows, Linux, and macOS compatibility
- **Design Patterns**: KISS, DRY, SOLID, YAGNI

### Code Quality Standards
- **One Class Per File**: Enforces Single Responsibility Principle
- **Small Files**: Flags files over 500 additions as potential violations
- **Function Reuse**: Encourages shared utilities across commands
- **Minimal Changes**: Keep changes focused on the problem at hand
- **No Special Characters**: No emojis or special characters in logs/output

### Testing Requirements
- **Framework**: xUnit, FluentAssertions, NSubstitute for .NET
- **Quality over Quantity**: Focus on critical paths and edge cases
- **Mocking**: Proper mocking of external dependencies
- **Reliability**: CLI MUST be reliable - missing tests are blocking

### User-Facing Quality
- **Clear Error Messages**: Client-facing messages must be actionable
- **Help Text**: Follow az cli patterns for help and logs
- **Documentation**: Minimal, focused docs - prefer self-documenting code
- **Error Handling**: Graceful handling with user-friendly guidance

### Review Standards
- Catch issues proactively during review
- Verify suggestions (no hallucinations)
- Cautious about deletions or destructive changes
- Critical review of all changes against principles

## Installation

### Prerequisites

```bash
# Install GitHub CLI
winget install GitHub.cli

# Authenticate
gh auth login

# Install Python dependencies
pip install PyYAML
```

### Enable Skill

The skill is automatically available in Claude Code once it's in `.claude/skills/review-pr/`.

## Usage

### Basic Review

```bash
# Review PR #180
/review-pr 180
```

This will:
1. Fetch PR details from GitHub
2. Analyze files and generate comments
3. Create editable YAML file
4. Preview comments
5. Ask for confirmation
6. Post to GitHub

### Dry Run (Preview Only)

```bash
# Preview without posting
python .claude/skills/review-pr/review-pr.py 180 --dry-run
```

### Custom Output Location

```bash
# Save to specific file
python .claude/skills/review-pr/review-pr.py 180 --output my-review.yaml
```

## Workflow

```
User: /review-pr 180
     ↓
1. Fetch PR from GitHub
     ↓
2. Analyze files (tests, size, security)
     ↓
3. Generate comments YAML
   /tmp/pr-reviews/pr-180-review.yaml
     ↓
4. Preview comments
     ↓
5. User confirms (y/N)
     ↓
6. Post to GitHub PR #180
     ↓
✓ Done!
```

## Generated YAML Structure

```yaml
pr_number: 180
pr_title: "Add auto-triage feature"
pr_url: "https://github.com/..."
overall_decision: COMMENT  # or APPROVE, REQUEST_CHANGES
overall_body: |
  Thanks for the PR! I've reviewed...

comments:
  - type: change_request      # or comment, praise
    severity: blocking        # or high, medium, low, info
    enabled: true            # set to false to skip
    file: path/to/file.py    # optional - for file-specific comments
    line: 42                 # optional - for inline comments
    body: |
      **Issue**: Description of issue
      **Fix**: Suggested solution
```

## Customization

### Edit Before Posting

1. Review generates the YAML file
2. Open in editor: `code /tmp/pr-reviews/pr-180-review.yaml`
3. Modify comments, severity, or disable entries
4. Run again: `python review-pr.py 180 --output /tmp/pr-reviews/pr-180-review.yaml`

### Add Custom Checks

Edit `review-pr.py` to add custom analysis:

```python
def analyze_pr(self) -> List[Dict[str, Any]]:
    comments = []

    # Add your custom check
    for file in self.pr_data.get('files', []):
        if file['path'].endswith('.sql'):
            comments.append({
                'file': file['path'],
                'type': 'comment',
                'severity': 'high',
                'enabled': True,
                'body': '**Database Change**: Ensure migration is tested and reversible.'
            })

    return comments
```

## Examples

### Example 1: Security Review

```bash
# Generate review
/review-pr 180

# Output: pr-180-review.yaml
pr_number: 180
comments:
  - type: change_request
    severity: blocking
    body: "**Security**: Potential API keys in config file. Use environment variables."
    file: "config/settings.py"
    enabled: true
```

### Example 2: Test Coverage

```bash
# Analyzes and finds missing tests
/review-pr 180

# Generates comment:
- type: change_request
  severity: blocking
  body: "**Missing Tests**: Code changes detected but no test files updated."
```

## Team Usage

### Share Across Team

```bash
# Team members clone repo
git clone https://github.com/microsoft/Agent365-devTools.git
cd Agent365-devTools

# Skill is available immediately
/review-pr <number>
```

### Customize Per Project

Edit `.claude/skills/review-pr/skill.yaml` to adjust:
- Default severity levels
- Auto-check rules
- Review focus areas

## Troubleshooting

### GitHub CLI Not Authenticated

```bash
gh auth status
# If not authenticated:
gh auth login
```

### PyYAML Not Found

```bash
pip install PyYAML
```

### Rate Limiting

The script includes a 0.5s delay between comments to avoid rate limits.

If you hit limits:
```bash
# Check rate limit status
gh api rate_limit

# Wait or use different token
export GITHUB_TOKEN="different-token"
```

### Comments Not Posting

1. Check PR permissions:
   ```bash
   gh pr view 180 --json viewerCanUpdate
   ```

2. Verify branch protection rules

3. Check if you're a collaborator:
   ```bash
   gh api repos/:owner/:repo/collaborators/:username
   ```

## Advanced

### Batch Review Multiple PRs

```bash
# Review all open PRs
for pr in $(gh pr list --json number -q '.[].number'); do
  python .claude/skills/review-pr/review-pr.py $pr --dry-run
done
```

### Integration with CI/CD

```yaml
# .github/workflows/auto-review.yml
name: Auto Review
on:
  pull_request:
    types: [opened, synchronize]

jobs:
  review:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Install dependencies
        run: pip install PyYAML
      - name: Generate review
        run: |
          python .claude/skills/review-pr/review-pr.py \
            ${{ github.event.pull_request.number }} \
            --dry-run
```

## Contributing

To improve the skill:

1. Edit `.claude/skills/review-pr/review-pr.py`
2. Add new checks to `analyze_pr()`
3. Update `skill.yaml` if adding new capabilities
4. Test with `--dry-run`
5. Submit PR with improvements

## License

MIT - Same as parent repository
