---
name: review-pr
description: Generate structured PR review comments with AI analysis and post them to GitHub
disable-model-invocation: true
allowed-tools: Bash(python:*), Bash(gh:*)
---

# PR Review Skill

Generate and post AI-powered PR review comments to GitHub following engineering best practices.

## Usage

```bash
/review-pr <pr-number>         # Generate review (step 1)
/review-pr <pr-number> --post  # Post review to GitHub (step 2)
```

Examples:
- `/review-pr 180` - Generate review and save to YAML file
- `/review-pr 180 --post` - Post the reviewed YAML to GitHub

## What this skill does

**Step 1: Generate** (`/review-pr <number>`)
1. **Fetches PR details** from GitHub using the gh CLI
2. **Analyzes changes** for security, testing, design patterns, and code quality issues
3. **Differentiates contexts**: CLI code vs GitHub Actions code (different standards)
4. **Creates actionable feedback**: Specific refactoring suggestions based on file names and patterns
5. **Generates structured review comments** in an editable YAML file
6. **Shows preview** of all generated comments

**Step 2: Post** (`/review-pr <number> --post`)
1. **Reads the YAML file** you reviewed/edited
2. **Posts to GitHub**: Submits all enabled comments to the PR

## Engineering Review Principles

This skill enforces the following principles:

### Architecture
- **.NET architect patterns**: Reviews follow .NET best practices
- **Azure CLI alignment**: Ensures consistency with az cli patterns and conventions
- **Cross-platform compatibility**: Validates Windows, Linux, and macOS compatibility (for CLI code)

### Design Patterns
- **KISS (Keep It Simple, Stupid)**: Prefers simple, straightforward solutions
- **DRY (Don't Repeat Yourself)**: Identifies code duplication
- **SOLID principles**: Especially Single Responsibility Principle
- **YAGNI (You Aren't Gonna Need It)**: Avoids over-engineering
- **One class per file**: Enforces clean code organization

### Code Quality
- **No large files**: Flags files over 500 additions
- **Function reuse**: Encourages reusing functions across commands
- **No special characters**: Avoids emojis in logs/output (Windows compatibility)
- **Self-documenting code**: Prefers clear code over excessive comments
- **Minimal changes**: Makes only necessary changes to solve the problem

### Testing Standards
- **Framework**: xUnit, FluentAssertions, NSubstitute for .NET; pytest/unittest for Python
- **Quality over quantity**: Focus on critical paths and edge cases
- **CLI reliability**: CLI code without tests is BLOCKING
- **GitHub Actions tests**: Strongly recommended (HIGH severity) but not blocking
- **Mock external dependencies**: Proper mocking patterns

### Security
- **No hardcoded secrets**: Use environment variables or Azure Key Vault
- **Credential management**: Follow az cli patterns for CLI code; use GitHub Secrets for Actions

### Context Awareness
The skill differentiates between:
- **CLI code** (strict requirements): Cross-platform, reliable, must have tests
- **GitHub Actions code** (GitHub-specific): Linux-only is acceptable, tests strongly recommended

## Review Comments Output

Generated comments are saved to:
```
C:\Users\<username>\AppData\Local\Temp\pr-reviews\pr-<number>-review.yaml
```

You can edit this file to:
- Disable comments by setting `enabled: false`
- Modify comment text
- Adjust severity levels (blocking, high, medium, low, info)
- Add or remove comments

## Implementation

The skill runs a Python script with two modes:

**Generate mode** (default):
```bash
python .claude/skills/review-pr/review-pr.py <pr-number>
```
1. Uses `gh pr view` to fetch PR details
2. Analyzes files and generates structured comments
3. Creates editable YAML review file
4. Previews comments for your review
5. Stops and waits for you to review/edit

**Post mode** (with --post flag):
```bash
python .claude/skills/review-pr/review-pr.py <pr-number> --post
```
1. Reads the existing YAML file
2. Previews what will be posted
3. Posts all enabled comments to GitHub

## Workflow

1. **Generate review**: `/review-pr 180`
   - Fetches PR details from GitHub
   - Analyzes code and generates review comments
   - Saves to YAML file (shows path in output)

2. **Review and edit**: Open the YAML file
   - Review all generated comments
   - Edit comment text if needed
   - Disable comments by setting `enabled: false`
   - Add your own comments if desired

3. **Post to GitHub**: `/review-pr 180 --post`
   - Reads the YAML file
   - Posts all enabled comments to the PR

## Requirements

- GitHub CLI (`gh`) installed and authenticated
- Python 3.x
- PyYAML library: `pip install pyyaml`
- Repository must be a GitHub repository
- GitHub API permissions to post reviews (Enterprise Managed Users may have restrictions)

## See Also

- [README.md](README.md) - Detailed documentation
- [review-pr.py](review-pr.py) - Implementation script
