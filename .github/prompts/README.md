# Prompts

> Reusable prompt templates for GitHub Copilot. Invoke with `#` in chat.

## Categories

| Directory | Purpose | Prompts |
|-----------|---------|---------|
| **[prd-workflow/](prd-workflow/)** | Product requirements → tasks → implementation | 3 |
| **[document-review/](document-review/)** | Analyze and evaluate documents | 9 |
| **[thinking/](thinking/)** | Critical thinking and analysis frameworks | 2 |
| **[utility/](utility/)** | General-purpose utilities | 2 |

---

## PRD Workflow

End-to-end feature development from requirements to implementation.

| Prompt | Purpose |
|--------|---------|
| `create-prd.prompt.md` | Generate a design document through guided questions |
| `generate-tasks.prompt.md` | Create implementation task list from design doc |
| `process-task-list.prompt.md` | Execute tasks one-by-one with approval gates |

**Flow**: Create PRD → Generate Tasks → Process Tasks

---

## Document Review

Analyze documents from multiple perspectives.

| Prompt | Purpose |
|--------|---------|
| `review-argument.prompt.md` | Evaluate argument structure and validity |
| `review-critical.prompt.md` | Comprehensive critical assessment framework |
| `review-critical-analysis.prompt.md` | Identify biases, assumptions, gaps |
| `review-decomposition.prompt.md` | Break complex questions into sub-questions |
| `review-fact-check.prompt.md` | Verify factual claims with sources |
| `review-persuasive-tech.prompt.md` | Analyze persuasive techniques used |
| `review-sentiment.prompt.md` | Analyze emotional tone and evidence |
| `review-structure-org.prompt.md` | Evaluate document organization |
| `review-summarization.prompt.md` | Create concise summaries |

---

## Thinking

Frameworks for critical analysis and problem decomposition.

| Prompt | Purpose |
|--------|---------|
| `critical-decomposition.prompt.md` | Break down complex problems systematically |
| `tone-and-style.prompt.md` | Analyze writing tone and style |

---

## Utility

General-purpose prompts.

| Prompt | Purpose |
|--------|---------|
| `create-air-doc.prompt.md` | Create AI-Ready documentation |
| `split-work.prompt.md` | Distribute tasks across developers |

---

## Naming Convention

All prompt files follow the pattern: `name-of-prompt.prompt.md` (kebab-case with `.prompt.md` suffix)

---

**Last Updated:** December 3, 2025 by Greg Ratajik
