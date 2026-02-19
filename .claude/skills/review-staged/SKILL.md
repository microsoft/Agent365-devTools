---
name: review-staged
description: Generate structured code review for staged files (git staged changes) using Claude Code agents. Provides feedback before committing to catch issues early.
allowed-tools: Bash(git:*), Read, Write
---

# Review Staged Files Skill

Generate AI-powered code review comments for your staged files (git staged changes) before committing. Catch issues early in the development process using the same rigorous review standards as PR reviews.

## Usage

```bash
/review-staged              # Review all staged files
/review-staged --verbose    # Show detailed analysis
```

Examples:
- `/review-staged` - Review all currently staged files
- `/review-staged --verbose` - Show detailed analysis with full context

## What this skill does

1. **Checks for staged files** using `git diff --staged --name-only`
2. **Fetches staged changes** using `git diff --staged`
3. **Performs architectural review**: Questions design decisions, checks for scope creep, validates use cases
4. **Analyzes changes** for security, testing, design patterns, and code quality issues
5. **Differentiates contexts**: CLI code vs GitHub Actions code (different standards)
6. **Creates actionable feedback**: Specific refactoring suggestions based on file names and patterns
7. **Generates structured review document** saved to a markdown file
8. **Shows summary** of all issues found organized by severity

## Engineering Review Principles

This skill enforces the same principles as the PR review skill:

### Architectural Review
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

## Review Output

Generated review is saved to:
```
.codereviews/claude-staged-<timestamp>.md
```

The review includes:
- **Summary**: Overview of changes and key concerns
- **Critical Issues**: Blocking issues that must be fixed
- **High Priority**: Important issues that should be addressed
- **Medium Priority**: Issues that improve code quality
- **Low Priority**: Suggestions for enhancement
- **Informational**: Best practices and recommendations

## Implementation

The skill uses **Claude Code directly** for semantic code analysis (same as review-pr):

1. Claude Code reads `.claude/agents/pr-code-reviewer.md` for review process guidelines
2. Claude Code reads `.github/copilot-instructions.md` for coding standards
3. Claude Code gets staged files: `git diff --staged --name-only`
4. Claude Code gets staged changes: `git diff --staged`
5. Claude Code performs semantic analysis using its own capabilities
6. Claude Code identifies specific issues with line numbers and code references
7. Claude Code writes markdown file to `.codereviews/claude-staged-<timestamp>.md`

**Key Advantages**:
- ✅ No API key required - uses Claude Code's existing authentication
- ✅ Better semantic analysis - Claude Code has full context
- ✅ Catch issues before committing
- ✅ Same rigorous review standards as PR reviews
- ✅ Works offline (no GitHub required)

## Workflow

1. **Stage your changes**: `git add <files>`

2. **Review staged files**: `/review-staged`
   - Analyzes all staged changes
   - Generates review document
   - Shows summary of issues

3. **Address issues**: Fix any blocking or high-priority issues

4. **Re-review if needed**: `/review-staged`

5. **Commit**: `git commit -m "your message"`

## When to Use

- **Before committing**: Catch issues early
- **Before creating a PR**: Ensure quality before sharing
- **After addressing PR comments**: Verify fixes are correct
- **During code cleanup**: Validate refactoring changes
- **When learning**: Get feedback on coding patterns

## Requirements

- Git repository with staged changes
- Repository must follow Agent365 DevTools coding standards
- `.claude/agents/pr-code-reviewer.md` must exist (for review guidelines)
- `.github/copilot-instructions.md` must exist (for coding standards)

## See Also

- [README.md](README.md) - Detailed documentation
- `/review-pr` - Review pull requests on GitHub
