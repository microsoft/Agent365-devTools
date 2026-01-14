---
tags: [prompt, tasks, workflow, implementation]
description: Guidelines for executing tasks from a PRD task list one sub-task at a time
---

# Task List Management

> **Usage**: Invoke when implementing a PRD. Works through sub-tasks one at a time, waiting for user approval between each.

## Prerequisites

**FIRST**: Check `.github/copilot/project-config.md` for feature toggles:

- **Azure DevOps Integration**: If ❌ OFF, skip ADO sync and work item references
- **Memory Bank**: If ❌ OFF, skip memory bank updates
- **GitHub Projects**: If ❌ OFF, skip GitHub issue updates

Guidelines for managing task lists in markdown files to track progress on completing a PRD. If the user doesn't pass the name of the PRD, ask first.

Make sure to follow the rules in `.github/copilot/task-workflow.md`.

## Before Starting (per task-workflow.md)

1. **Check project-config.md** - Determine which integrations are enabled
2. **Read TASKS.md** - Find the current task, review acceptance criteria and dependencies
3. **Read design.md** - Understand architecture and design decisions
4. **Discover related docs** - Use `file_search` with `docs/**/*.md` to find completion summaries
5. **If ADO = ✅ ON**: Check Phase header for ADO Feature link and phase status

## Task Implementation

- **One sub-task at a time:** Do **NOT** start the next sub-task until you ask the user for permission and they say "yes" or "y"
- **Task status markers** (per task-workflow.md):
  - `[ ]` - Not started
  - `[>]` - In progress (add Started timestamp when marking)
  - `[x]` - Complete (add Completed timestamp and Duration)
- **Timestamp format:** `YYYY-MM-DD HH:MM:SS UTC±X` (e.g., `2025-10-09 14:30:00 UTC-7`)
- **Completion protocol:**
  1. When you finish a **sub-task**, mark it `[x]` with Completed timestamp and Duration.
  2. If **all** subtasks underneath a parent task are now `[x]`, also mark the **parent task** as completed.
- Stop after each sub-task and wait for the user's go-ahead.

## Task List Maintenance

1. **Update TASKS.md after each sub-task:**
   - Mark task `[x]` with Started, Completed, Duration
   - Add implementation notes
   - Update phase Progress count

2. **Update design.md when architecture changes:**
   - Document design decisions made during implementation
   - Add "Last Updated" timestamp to modified sections

3. **Create Completion Summary** (for significant work):
   - Location: `docs/tasks/TASK-X.Y.Z-TASK-NAME-COMPLETION-SUMMARY.md`
   - Include: What was implemented, design decisions, implementation details

4. **Maintain "Relevant Documentation" section:**
   - Update documentation references as implementation progresses
   - Add any new files created with a one-line description

## Implementation File Location

**IMPORTANT**: Before creating any implementation files, ask the user where they want the files to be placed in their project structure. For example:

- "Where would you like me to create the React components? (e.g., `src/components/`, `app/components/`, etc.)"
- "What's your preferred directory structure for this project?"
- "Should I place the API files in a specific directory like `src/api/` or `lib/api/`?"

Do not assume the location - always confirm with the user first.

## AI Instructions

When working with task lists, the AI must follow task-workflow.md:

**BEFORE starting:**

1. Read TASKS.md, design.md, and related completion docs
2. Ask for implementation directory preferences before creating files
3. Check which sub-task is next

**DURING work:**

4. Mark task `[>]` with Started timestamp when beginning
5. Implement the sub-task

**AFTER completing:**

6. Mark task `[x]` with Completed timestamp and Duration
7. Update design.md if architecture changed
8. Create completion summary for significant work (in `docs/tasks/`)
9. Update phase Progress count in TASKS.md
10. Pause for user approval before next sub-task

## Workflow Context

This prompt is part of a 3-step workflow:

1. `create-prd.prompt.md` - Generate the design document (PRD)
2. `generate-tasks.prompt.md` - Create task list from PRD
3. `process-task-list.prompt.md` - Execute tasks (this file)

---

**Last Updated:** December 3, 2025 by gregrata
