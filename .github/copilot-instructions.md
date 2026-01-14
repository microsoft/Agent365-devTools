# Copilot Instructions: AI-First Repository

> **IMPORTANT**: This is the main instruction file. Load ALL referenced instruction files before proceeding with any task.

---

## 📚 Required Instruction Files

**BEFORE performing ANY task, you MUST load these files:**

| Order | File | Purpose |
|-------|------|----------|
| 1 | `.github/copilot/project-config.md` | **Config: Check feature toggles (ADO, Memory Bank, GitHub Projects)** |
| 2 | `.github/copilot/critical-evaluation.md` | Mindset: Challenge assumptions, disagree when necessary |
| 3 | `.github/copilot/task-workflow.md` | Process: Mandatory workflow for ALL tasks |
| 4 | `.github/copilot/code-guidelines.md` | Coding: Code surgery principles and quality standards |
| 5 | `.github/copilot/markdown-rules.md` | Formatting: Markdown standards (no em dashes, blank lines before lists) |
| 6 | `.github/copilot/memory-bank.md` | Context: Project memory (**only if Memory Bank = ✅ ON**) |

**🚨 MANDATORY**: If ANY of the first 5 files cannot be loaded, STOP immediately and inform the user.

---

## 🔧 Repository-Specific Rules

### Windows/PowerShell Environment

- **Starting servers**: Always check the terminal you are going to use - if you are running the front end web server, don't use the terminal for the back end!
- **Background processes**: Ensure proper handling for long-running tasks

### Documentation

- **Completion docs**: Create in `docs/<feature>/completion/` after completing tasks/features
- **DOCX conversion**: Use `scripts/md-to-docx.ps1`, place output with source MD file

### Developer Identity

- **Primary**: Read from `.developer` file in repo root (NAME= and EMAIL=)
- **Fallback**: Use Windows username from file paths if `.developer` doesn't exist
- **Usage**: "Last Updated by" fields, task completion attribution

---

## 🔄 Load Additional Files Based on project-config.md

**Check `.github/copilot/project-config.md` toggles first!**

**If Azure DevOps Integration = ✅ ON:**

- `.github/copilot/azure-devops-integration.md` - Feature-level tracking, ADO CLI commands
- `.github/copilot/ado-project-info.md` - ADO connection details (org, project, area path)

**If GitHub Projects = ✅ ON:**

- `.github/copilot/github-project-integration.md` - Issue creation, project board management

**Always available:**

- `.github/copilot/documentation-organization.md` - Directory structure, file placement rules

---

## 📖 Quick Reference

**Need guidance on...**

| Topic | See File |
|-------|----------|
| **Feature toggles (ADO/Memory Bank)** | **project-config.md** |
| How to think/respond | critical-evaluation.md |
| Task start/completion | task-workflow.md |
| Writing/modifying code | code-guidelines.md |
| Markdown formatting | markdown-rules.md |
| Doc file placement | documentation-organization.md |
| Azure DevOps sync | azure-devops-integration.md (if ADO = ON) |
| GitHub Projects sync | github-project-integration.md (if GitHub Projects = ON) |

**Detailed guidance index**: See `.github/copilot/README.md`

---

## ⚠️ CRITICAL Reminders

1. **Load project-config.md FIRST** to check feature toggles
2. **Load required files 2-5** before starting any work
3. **Load file 6 (Memory Bank) only if** Memory Bank = ✅ ON in project-config.md
4. **Follow task-workflow.md** for every task (before/during/after)
5. **Apply code-guidelines.md** for all code changes (surgical approach)
6. **Check markdown-rules.md** before editing any .md file
7. **Skip ADO references** if Azure DevOps Integration = ❌ OFF

---

**This file is an index only. All detailed guidance lives in the referenced files above.**

---

## 🔷 Agent365-Specific Code Review Rules

### Rule 1: Check for "Kairo" Keyword

- **Description**: Scan code for any occurrence of the keyword "Kairo"
- **Action**: If "Kairo" is found in any code file:
  - Flag it for review
  - Suggest removal or replacement with appropriate terminology
  - Check if it's a legacy reference that needs to be updated
- **Files to check**: All `.cs`, `.csx` files in the repository

### Rule 2: Verify Copyright Headers

- **Description**: Ensure all C# files have proper Microsoft copyright headers
- **Action**: If a `.cs` file is missing a copyright header:
  - Add the Microsoft copyright header at the top of the file
  - The header should be placed before any using statements or code
  - Maintain proper formatting and spacing

#### Required Copyright Header Format

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
```

### Implementation Guidelines

#### When Reviewing Code:

1. **Kairo Check**:
   - Search for case-insensitive matches of "Kairo"
   - Review context to determine if it's:
     - A class name
     - A namespace
     - A variable name
     - A comment reference
     - A using statement
     - A string literal
   - Suggest appropriate alternatives based on the context

2. **Header Check**:
   - Verify the first non-empty lines of C# files
   - If missing, prepend the copyright header
   - Ensure there's a blank line after the header before other content
   - Do not add headers to:
     - Auto-generated files (marked with `<auto-generated>` or `// <auto-generated />`)
     - Designer files (`.Designer.cs`)
     - Files with `#pragma warning disable` at the top for generated code

#### Example of Proper File Structure:

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace MyNamespace
{
    /// <summary>
    /// Class documentation
    /// </summary>
    public class MyClass
    {
        // Rest of the code...
    }
}
```

#### Example with File-Scoped Namespace (C# 10+):

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;

namespace MyNamespace;

/// <summary>
/// Class documentation
/// </summary>
public class MyClass
{
    // Rest of the code...
}
```

#### Example with Top-Level Statements (C# 9+):

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

var builder = WebApplication.CreateBuilder(args);

// Rest of the code...
```

### Auto-fix Behavior

When Copilot detects violations:

- **Kairo keyword**: Suggest inline replacement or flag for manual review
- **Missing header**: Automatically suggest adding the copyright header

### Exclusions

- Test files in `Tests/`, `test/`, or files ending with `.Tests.cs`, `.Test.cs` may have relaxed header requirements (but headers are still recommended)
- Auto-generated files (`.g.cs`, `.designer.cs`, files with auto-generated markers)
- Third-party code or vendored dependencies should not be modified
- Project files (`.csproj`, `.sln`), configuration files (`.json`, `.xml`, `.yaml`, `.md`) do not require copyright headers
- Build output directories (`bin/`, `obj/`)
- AssemblyInfo.cs files that are auto-generated
