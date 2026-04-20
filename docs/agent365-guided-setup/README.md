# Agent 365 Guided Setup — Index

> **If you are a coding agent (Claude Code, GitHub Copilot, Devin, or similar):**
> Read this file first, identify the task below, then **immediately open and follow ONLY the linked instruction file**. Open **one file only** — do NOT read both instruction files. Do NOT explore the codebase, fetch web pages, or run commands until you have read the instruction file. The instruction file is your complete guide.

---

## Which file do I need?

### "Make my agent A365 compliant" / "Set up my agent for Agent 365" / "Run agent setup"

**→ Open and follow: [a365-setup-instructions.md](a365-setup-instructions.md)**

Use this file when the user asks to:
- Make an agent A365 compliant
- Set up, provision, or configure an agent for Agent 365
- Run `a365 setup` or provision Azure / Entra resources
- Deploy an agent application

### "Add observability to my agent" / "Set up logging / telemetry / tracing"

**→ Open and follow: [a365-observability-instructions.md](a365-observability-instructions.md)**

Use this file when the user asks to:
- Add observability, telemetry, logging, or tracing to their agent
- Integrate OpenTelemetry or the Agent 365 observability stack
- Monitor agent activity or usage

---

## If the intent is unclear

If the user's request matches more than one category, or you are unsure, ask:

> "Are you looking to (1) set up your agent for Agent 365 compliance, or (2) add observability/telemetry to an existing agent?"

Wait for the answer, then open the appropriate file above.

---

## What NOT to do

- Do NOT open both instruction files. Open the one file that matches the task and follow it exclusively.
- Do NOT treat "make my agent A365 compliant" as including observability. Setup and observability are separate tasks with separate instruction files.
- Do NOT glob, search, or read files in the broader repository before reading the instruction file.
- Do NOT fetch external URLs or documentation before reading the instruction file.
- Do NOT ask clarifying questions beyond the one above if the intent is clear.
- Do NOT start writing code or running commands until the instruction file tells you to.
