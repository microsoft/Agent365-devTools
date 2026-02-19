# Review Staged Files Skill

**Status**: ✅ Active
**Version**: 1.0
**Author**: Microsoft Agent365 DevTools Team

## Overview

The `review-staged` skill provides AI-powered code review for staged files before you commit. It uses the same rigorous engineering standards as the PR review process, helping you catch issues early in the development cycle.

## Why Use This Skill?

### Traditional Workflow Problems
- 😞 Issues discovered during PR review require rework
- 😞 Back-and-forth on coding standards wastes time
- 😞 Missed test coverage only found after commit
- 😞 Security issues caught late in the process

### With review-staged
- ✅ Catch issues **before committing**
- ✅ Get immediate feedback on code quality
- ✅ Learn coding standards through practice
- ✅ Reduce PR review cycles
- ✅ Ship better code faster

## Quick Start

### 1. Stage your changes
```bash
git add src/MyFile.cs
git add tests/MyFileTests.cs
```

### 2. Review staged files
```bash
/review-staged
```

### 3. Read the review
Claude Code will generate a markdown file in `.codereviews/` with detailed feedback organized by severity.

### 4. Fix issues
Address any blocking or high-priority issues before committing.

### 5. Commit with confidence
```bash
git commit -m "feat: add new feature with tests"
```

## What Gets Reviewed

The skill analyzes:
- **Architecture**: Design decisions, scope, use cases
- **Code Quality**: Patterns, maintainability, simplicity
- **Testing**: Coverage, quality, edge cases
- **Security**: Secrets, input validation, error handling
- **Standards**: Coding conventions, file organization
- **Context**: CLI vs GitHub Actions (different standards apply)

## Review Severity Levels

### 🔴 BLOCKING
**Must fix before committing**
- Security vulnerabilities
- Missing tests for CLI code
- Cross-platform compatibility issues
- Hardcoded secrets

### 🟠 HIGH
**Should fix before committing**
- Architectural concerns
- Missing edge case tests
- Performance issues
- Significant code quality problems

### 🟡 MEDIUM
**Consider fixing**
- Code duplication
- Naming conventions
- Missing documentation
- Minor design improvements

### 🟢 LOW
**Nice to have**
- Code style suggestions
- Optimization opportunities
- Refactoring ideas

### ℹ️ INFO
**For your awareness**
- Best practices
- Learning opportunities
- Alternative approaches

## Example Review Session

```bash
$ git add src/Commands/NewCommand.cs tests/NewCommandTests.cs

$ /review-staged

🔍 Analyzing staged files...
   - src/Commands/NewCommand.cs (+150 lines)
   - tests/NewCommandTests.cs (+75 lines)

📝 Generating review...

✅ Review complete!

Summary:
  🔴 BLOCKING: 0
  🟠 HIGH: 2
  🟡 MEDIUM: 3
  🟢 LOW: 1
  ℹ️ INFO: 2

Review saved to: .codereviews/claude-staged-20260218_143022.md

High Priority Issues:
  1. Missing null check in NewCommand.cs:45
  2. No test coverage for error handling path

Open the review file to see detailed feedback and suggestions.
```

## Review Output Format

The generated markdown file includes:

```markdown
# Code Review: Staged Files

**Reviewed**: 2026-02-18 14:30:22
**Files**: 2 changed
**Lines**: +225

## Summary
[Overview of changes and key concerns]

## Issues by Severity

### 🔴 BLOCKING (0)
[Critical issues that must be fixed]

### 🟠 HIGH (2)
**1. Missing null check**
- **File**: src/Commands/NewCommand.cs:45
- **Issue**: Parameter `config` is not null-checked
- **Fix**: Add null check before accessing properties
```csharp
if (config is null)
{
    throw new ArgumentNullException(nameof(config));
}
```

### 🟡 MEDIUM (3)
[...]

### 🟢 LOW (1)
[...]

### ℹ️ INFO (2)
[...]

## Recommendations
[Overall suggestions and next steps]
```

## Advanced Usage

### Review with verbose output
```bash
/review-staged --verbose
```
Shows detailed analysis and reasoning for each issue.

### Review specific file patterns
```bash
# Stage only C# files
git add "*.cs"
/review-staged
```

### Review after addressing PR comments
```bash
# Make fixes
git add <fixed-files>
/review-staged  # Verify fixes are correct
```

## Integration with Workflow

### Before Creating PR
```bash
# 1. Complete feature
git add <files>

# 2. Review staged
/review-staged

# 3. Fix issues
# ... make fixes ...

# 4. Review again
git add <fixed-files>
/review-staged

# 5. Commit and push
git commit -m "feat: new feature"
git push origin feature-branch

# 6. Create PR
gh pr create
```

### During PR Review Cycle
```bash
# 1. Address PR comments
# ... make fixes ...

# 2. Stage changes
git add <fixed-files>

# 3. Review staged (ensure fixes are correct)
/review-staged

# 4. Commit
git commit -m "fix: address PR feedback"

# 5. Push
git push
```

## Review Guidelines

The skill follows the same guidelines as PR review, documented in:
- `.claude/agents/pr-code-reviewer.md` - Review process
- `.github/copilot-instructions.md` - Coding standards

### Key Principles

1. **KISS (Keep It Simple, Stupid)**
   - Prefer straightforward solutions
   - Avoid over-engineering

2. **DRY (Don't Repeat Yourself)**
   - Extract common code
   - Reuse existing functions

3. **SOLID Principles**
   - Single Responsibility Principle
   - Dependency Injection
   - Interface Segregation

4. **YAGNI (You Aren't Gonna Need It)**
   - Implement only what's needed now
   - Avoid premature optimization

## Context-Aware Reviews

The skill adjusts standards based on code type:

### CLI Code (Strict)
- ✅ Must be cross-platform (Windows, Linux, macOS)
- ✅ Must have comprehensive tests
- ✅ Must handle errors gracefully
- ✅ Must follow az CLI patterns

### GitHub Actions Code (Relaxed)
- ✅ Linux-only is acceptable
- ✅ Tests strongly recommended (not required)
- ✅ Can use GitHub-specific features

## Troubleshooting

### No staged files
```bash
$ /review-staged

⚠️ No staged files found.

Stage files first:
  git add <files>
```

**Solution**: Stage files with `git add` before reviewing.

### Review file not found
```bash
$ /review-staged

❌ Error: Could not read review guidelines
```

**Solution**: Ensure you're in the Agent365-devTools repository with `.claude/agents/pr-code-reviewer.md` present.

### Permission denied
```bash
$ /review-staged

❌ Error: Cannot create .codereviews directory
```

**Solution**: Check directory permissions or run from repository root.

## Best Practices

### 1. Review Early, Review Often
- Review after each logical change
- Don't wait until you have many files staged

### 2. Fix Blocking Issues First
- Always address BLOCKING issues before committing
- HIGH issues should also be fixed when possible

### 3. Learn from Feedback
- Read INFO-level comments to improve coding skills
- Apply patterns to future work

### 4. Keep Changes Small
- Smaller changesets = better review quality
- Split large features into smaller commits

### 5. Write Tests First
- Stage test files along with implementation
- The review will verify test quality

## Comparison with PR Review

| Feature | review-staged | review-pr |
|---------|---------------|-----------|
| **Timing** | Before commit | After PR created |
| **Scope** | Staged files only | All PR changes |
| **Output** | Markdown file | GitHub comments + YAML |
| **Posting** | Local only | Posts to GitHub |
| **Speed** | Fast | Slower (GitHub API) |
| **Best for** | Pre-commit check | Team collaboration |

## Future Enhancements

Planned features:
- [ ] Auto-fix suggestions with code patches
- [ ] Integration with pre-commit hooks
- [ ] Customizable severity thresholds
- [ ] Export to other formats (JSON, HTML)
- [ ] Compare with previous reviews

## Support

For issues or questions:
- Check `.claude/agents/pr-code-reviewer.md` for review guidelines
- Review `.github/copilot-instructions.md` for coding standards
- Ask Claude Code for clarification on any review feedback

## See Also

- [SKILL.md](SKILL.md) - Skill definition and usage
- `/review-pr` - Review pull requests on GitHub
- `.claude/agents/pr-code-reviewer.md` - Review process guidelines
- `.github/copilot-instructions.md` - Coding standards
