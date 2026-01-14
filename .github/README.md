# .github - AI-First Development Configuration

> **One-liner**: Everything GitHub Copilot needs to understand your project and work like a senior teammate.

---

## Quick Start

1. **Copy `copilot/ai-seed.md`** to your repo's `.github/` directory
2. **Tell Copilot**: "run AI Seed"
3. **Create `.developer`** file in repo root with your `NAME=` and `EMAIL=`
   - Add `.developer` to your `.gitignore` first
   - Or just tell Copilot: "Create my .developer file. Use my git info to fill it out. Add a .gitignore for that file"

4. **Configure** `copilot/project-config.md` to enable/disable features

That's it. You're AI-First.

---

## What's Here

```
.github/
├── copilot-instructions.md     # Main entry point - Copilot reads this first
├── copilot/                    # Rules, guidelines, configuration
├── prompts/                    # Reusable prompt templates
├── agents/                     # Specialized AI agents
├── instructions/               # File-type specific instructions
└── [config dirs]               # acl/, compliance/, policies/, ISSUE_TEMPLATE/
```

---

## Directory Breakdown

### `copilot/` - The Brain

Configuration and rules that shape how Copilot behaves in your project.

| File | Purpose |
|------|---------|
| `project-config.md` | **Start here** - Feature toggles (ADO, Memory Bank, GitHub Projects) |
| `task-workflow.md` | Mandatory workflow for all tasks (before/during/after) |
| `code-guidelines.md` | Code surgery principles and quality standards |
| `markdown-rules.md` | Markdown formatting standards |
| `critical-evaluation.md` | Mindset rules - challenge assumptions, disagree when necessary |
| `ai-seed.md` | Bootstrap tool to copy these files to other repos |
| `memory-bank.md` | Context persistence across sessions (if enabled) |
| `ado-project-info.md` | Azure DevOps connection details (if ADO enabled) |
| `azure-devops-integration.md` | ADO Feature tracking rules (if ADO enabled) |
| `github-project-integration.md` | GitHub Projects sync rules (if enabled) |
| `documentation-organization.md` | Where docs go and how to name them |
| `absolute-mode.md` | Direct, no-frills communication mode |

### `prompts/` - Reusable Templates

Invoke with `/prompt-name` in Copilot chat.

| Directory | What's Inside |
|-----------|---------------|
| `prd-workflow/` | Design doc → Tasks → Implementation (3 prompts) |
| `document-review/` | Argument, sentiment, fact-check, structure analysis (9 prompts) |
| `thinking/` | Critical decomposition, tone analysis (2 prompts) |
| `utility/` | Split work, create AI-ready docs (2 prompts) |

### `agents/` - Specialized Personas

AI agents with specific expertise. 40+ agents across 7 categories:

| Category | Examples |
|----------|----------|
| `design/` | UI Designer, UX Architect, Visual Storyteller |
| `engineering/` | Backend Architect, Frontend Developer, DevOps Automator |
| `marketing/` | Growth Hacker, Social Media Strategist, Content Creator |
| `product/` | Feedback Synthesizer, Sprint Prioritizer, Trend Researcher |
| `project-management/` | Project Shepherd, Studio Producer, Experiment Tracker |
| `support/` | Analytics Reporter, Legal Compliance Checker, Finance Tracker |
| `testing/` | API Tester, Performance Benchmarker, Reality Checker |

### `instructions/` - File-Specific Rules

Applied automatically based on file type (uses `applyTo:` frontmatter).

| File | Applies To |
|------|------------|
| `md.instructions.md` | `**/*.md` - Microsoft voice and style for all markdown |

---

## How It All Works

```
┌─────────────────────────────────────────────────────────────┐
│                    Copilot Reads...                         │
├─────────────────────────────────────────────────────────────┤
│  1. copilot-instructions.md (main entry point)              │
│     ↓                                                       │
│  2. copilot/project-config.md (check feature toggles)       │
│     ↓                                                       │
│  3. Required files: task-workflow, code-guidelines,         │
│     markdown-rules, critical-evaluation                     │
│     ↓                                                       │
│  4. Optional files based on toggles:                        │
│     - ADO ON → azure-devops-integration, ado-project-info   │
│     - Memory Bank ON → memory-bank                          │
│     - GitHub Projects ON → github-project-integration       │
│     ↓                                                       │
│  5. instructions/*.md matched by file type                  │
│     ↓                                                       │
│  6. .developer file for attribution                         │
└─────────────────────────────────────────────────────────────┘
```

---

## Feature Toggles

Edit `copilot/project-config.md` to enable/disable:

| Feature | Default | What It Does |
|---------|---------|--------------|
| **Azure DevOps Integration** | ❌ OFF | Sync phases to ADO Features, work item links |
| **Memory Bank** | ❌ OFF | Persist context across sessions |
| **GitHub Projects** | ❌ OFF | Create issues, sync to project boards |

---

## Developer Identity

Create a `.developer` file in your repo root:

```
NAME=Your Full Name
EMAIL=your.email@example.com
```

**Important:** Add `.developer` to your `.gitignore` - this file is personal and shouldn't be committed.

Used for "Last Updated by" fields and task completion attribution.

---

## Naming Conventions

| Directory | Pattern | Example |
|-----------|---------|---------|
| `prompts/` | `name.prompt.md` | `review-sentiment.prompt.md` |
| `agents/` | `name.agent.md` | `backend-architect.agent.md` |
| `copilot/` | `kebab-case.md` | `task-workflow.md` |
| `instructions/` | `name.instructions.md` | `md.instructions.md` |

---

## Spreading the Love

Want to use this in another repo?

1. Copy `copilot/ai-seed.md` to the target repo's `.github/` directory
2. Open it in VS Code with Copilot
3. Say: "run AI Seed"
4. All files download automatically

---

## File Count

| Directory | Files |
|-----------|-------|
| `copilot/` | 13 |
| `prompts/` | 16 (across 4 subdirs) |
| `agents/` | 40+ (across 7 subdirs) |
| `instructions/` | 1 |
| **Total** | ~70 files |

---

**Last Updated:** December 3, 2025 by Greg Ratajik
