# PRD Workflow for VS Code

> **Prerequisite**: Enable prompt files in VS Code settings: `"chat.promptFiles": true`

A structured workflow for feature development using GitHub Copilot - from requirements to implementation with built-in checkpoints.

## The Core Idea

Building complex features with AI can feel like a black box. This workflow brings structure and control:

1. **Define Scope**: Create a design document clarifying what you're building
2. **Detailed Planning**: Break down the design into actionable tasks
3. **Iterative Implementation**: Tackle one task at a time with approval gates

## Workflow: From Idea to Implemented Feature 💡➡️💻

Here's the step-by-step process using the `.prompt.md` files in this repository:

### 1️⃣ Create a Design Document

First, lay out the blueprint for your feature. The design doc clarifies what you're building, for whom, and why.

1. In VS Code chat, invoke the prompt:

    ```
    #create-prd
    Here's the feature I want to build: [Describe your feature]
    Reference these files: [Optional: @file1.py @file2.ts]
    ```

2. The AI will ask clarifying questions, then generate the document.

3. Output saved to: `/docs/features/[feature-name]/design.md`

### 2️⃣ Generate Your Task List

With your design document ready, generate a step-by-step implementation plan:

1. In VS Code chat:

    ```
    #generate-tasks
    Use @docs/features/[feature-name]/design.md
    ```

2. The AI generates high-level tasks first, then asks "Ready for sub-tasks? Reply 'Go'"

3. Output saved to: `/docs/features/[feature-name]/tasks.md`

### 3️⃣ Examine Your Task List

You'll have a well-structured task list with parent tasks and sub-tasks, ready for implementation.

### 4️⃣ Work Through Tasks One-by-One

Use `process-task-list.prompt.md` to work methodically through tasks with approval gates:

1. In VS Code chat:

    ```
    #process-task-list
    Start on task 1.1
    ```

2. The AI completes the sub-task and waits for your approval.

3. Reply "yes" or "y" to mark complete and continue to the next task.

4. Provide feedback if changes are needed before moving on.

### 5️⃣ Review, Approve, and Progress

As tasks complete, you'll see progress:

- `[ ]` - Not started
- `[>]` - In progress (with Started timestamp)
- `[x]` - Complete (with Completed timestamp and Duration)

## Output Organization

All outputs are organized in `/docs/features/` for tracking:

```
docs/features/
├── feature-name-1/
│   ├── design.md          # Design document (requirements + architecture)
│   └── tasks.md           # Generated task list
├── feature-name-2/
│   ├── design.md
│   └── tasks.md
```

**Implementation Files**: Created in appropriate project directories based on your project structure.

**Benefits:**

- **Version Controlled**: All outputs are committed and tracked
- **Organized**: Each feature has its own directory
- **Searchable**: Easy to find and review previous work

## Video Demo

See this workflow in action on [Claire Vo's "How I AI" podcast](https://www.youtube.com/watch?v=fD4ktSkNCw4).

## Files in This Directory

| File | Purpose |
|------|---------|
| `create-prd.prompt.md` | Generate a design document through guided questions |
| `generate-tasks.prompt.md` | Create implementation tasks from a design document |
| `process-task-list.prompt.md` | Execute tasks one-by-one with approval gates |

## Benefits

- **Structured**: Clear process from idea to code
- **Verified**: Review and approve each step
- **Manageable**: Complex features broken into small tasks
- **Trackable**: Visual progress with timestamps

## Tips

- **Be specific**: More context = better output
- **Tag files correctly**: Use `@docs/features/[name]/design.md` when generating tasks
- **Iterate**: Provide feedback to correct issues before moving on

---

**Last Updated:** December 3, 2025 by Greg Ratajik
