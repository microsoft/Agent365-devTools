---
name: review-pr
description: Generate structured PR review comments using Claude Code agents and post them to GitHub. No API key required - uses Claude Code's existing authentication.
allowed-tools: Bash(gh:*), Task, Read, Write
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
2. **Performs architectural review** (NEW!): Questions design decisions, checks for scope creep, validates use cases
3. **Analyzes changes** for security, testing, design patterns, and code quality issues
4. **Differentiates contexts**: CLI code vs GitHub Actions code (different standards)
5. **Creates actionable feedback**: Specific refactoring suggestions based on file names and patterns
6. **Generates structured review comments** in an editable YAML file
7. **Shows preview** of all generated comments

**Step 2: Post** (`/review-pr <number> --post`)
1. **Reads the YAML file** you reviewed/edited
2. **Posts to GitHub**: Submits all enabled comments to the PR
3. **Automatic fallback**: If GitHub API posting fails (e.g., Enterprise Managed User restrictions), automatically generates a markdown file with formatted comments for manual copy/paste

## Engineering Review Principles

This skill enforces the following principles:

### Architectural Review (NEW!)
- **Design Decision Validation**: Questions "why" before reviewing "how"
- **Scope Creep Detection**: Flags expansions beyond Agent365 deployment/management
- **Use Case Validation**: Requires concrete scenarios for new features
- **Overlap Detection**: Identifies duplication with existing tools (Azure CLI, Portal)
- **YAGNI Enforcement**: Questions features without documented need

### Architecture & Patterns
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

The skill uses **Claude Code directly** for semantic code analysis (inspired by Agent365-dotnet). No separate API key required!

**Generate mode** (default):
1. Claude Code reads `.claude/agents/pr-code-reviewer.md` for review process guidelines
2. Claude Code reads `.github/copilot-instructions.md` for coding standards
3. Claude Code fetches PR details: `gh pr view <number> --json ...`
4. Claude Code analyzes actual code changes: `gh pr diff <number>`
5. Claude Code performs semantic analysis using its own capabilities
6. Claude Code identifies specific issues with line numbers and code references
7. Claude Code writes YAML file to `C:\Users\<username>\AppData\Local\Temp\pr-reviews\pr-<number>-review.yaml`

**Post mode** (with --post flag):
1. Python script reads the YAML file
2. Python script posts comments to GitHub using `gh pr comment`
3. If posting fails (API permissions), automatically generates markdown file for manual copy/paste

**Key Advantages**:
- ✅ No `ANTHROPIC_API_KEY` required - uses Claude Code's existing authentication
- ✅ Better semantic analysis - Claude Code has full context and conversation history
- ✅ Simpler Python script - only handles posting logic (~240 lines vs ~1500 lines)
- ✅ Easier to maintain and debug

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
   - If API posting fails, automatically generates a markdown file for manual copy/paste

## Requirements

- GitHub CLI (`gh`) installed and authenticated
- Python 3.x (only for --post mode)
- PyYAML library: `pip install pyyaml` (only for --post mode)
- Repository must be a GitHub repository
- GitHub API permissions to post reviews (Enterprise Managed Users may have restrictions)

## See Also

- [README.md](README.md) - Detailed documentation
- [review-pr.py](review-pr.py) - Implementation script
