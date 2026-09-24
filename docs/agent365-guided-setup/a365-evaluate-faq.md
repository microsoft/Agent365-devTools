# MCP Evaluation — AI transparency FAQ

This page answers the most common questions about how `a365 develop-mcp evaluate` uses AI and what it does (and does not) process.

---

## Q1. What data does this command send to the AI, and where does it go?

The command fetches your MCP server's **tool schemas** — tool names, descriptions, and parameter schemas — via a standard MCP `tools/list` call to the URL you supply. It then writes those schemas to a checklist JSON file in your output directory and hands that file to your locally installed coding agent CLI (GitHub Copilot CLI or Claude Code CLI).

**The coding agent CLI runs entirely on your machine**, under your own account and subscription. No tool-schema data is sent to Microsoft's servers by this feature. The data path is:

```
Your MCP server
    → a365 CLI (your machine, checklist written to your output dir)
        → locally installed coding agent CLI (your machine) - Github Copilot or Claude Code CLI
            → AI provider under your account (Anthropic / GitHub Copilot)
```

Microsoft's role is supplying the `a365` CLI that orchestrates steps 1–2. Steps 3–4 are governed by your own AI subscription's terms.

---

## Q2. What does this command NOT process or collect?

| Item | Processed? | Notes |
|---|---|---|
| Runtime data from your MCP server (live payloads, user requests) | **No** | Only the static schema from `tools/list` |
| Personal data of your MCP server's end users | **No** | Schemas describe tool APIs, not user data |
| Auth token supplied via `--auth-token` or `A365_MCP_AUTH_TOKEN` | **No** (AI never sees it) | Token is held in memory, sent only as the HTTP `Authorization` header to your server, and never logged or passed to the coding agent. Prefer the `A365_MCP_AUTH_TOKEN` environment variable over the flag — it keeps the token out of process listings (`ps` / Task Manager) and shell history; the CLI warns once if you use the flag. |
| Telemetry or usage data | **No** | This feature adds no telemetry to the base CLI |
| Output report or checklist JSON | **No** (not sent anywhere) | Files are written to the `--output-dir` you specify and stay on your machine |

---

## Q3. Which AI model performs the evaluation, and who controls it?

By default the evaluation uses **Claude Haiku 4.5**, invoked through whichever coding agent CLI you have installed (GitHub Copilot CLI or Claude Code CLI). You can override the engine with `--eval-engine github-copilot` or `--eval-engine claude-code`.

You can also score with **your own Azure OpenAI deployment** via `--eval-engine azure-openai`. Set `A365_EVAL_AZURE_OPENAI_ENDPOINT` and `A365_EVAL_AZURE_OPENAI_DEPLOYMENT`, and sign in to Entra ID (e.g. `az login`). This engine authenticates with **Microsoft Entra ID only** (`DefaultAzureCredential`) — **API-key authentication is not supported**. Unlike the coding-agent engines it needs no local CLI: the `a365` CLI calls your Azure OpenAI deployment directly, under your own Azure subscription, billing, and access policies.

To run a **different model** without waiting for a CLI update, set an environment variable before the run: `A365_EVAL_COPILOT_MODEL` for GitHub Copilot (needs an exact model ID, e.g. `claude-haiku-4.5`) or `A365_EVAL_CLAUDE_MODEL` for Claude Code (accepts an alias, e.g. `haiku`). For Azure OpenAI, the model is whatever you name in `A365_EVAL_AZURE_OPENAI_DEPLOYMENT` (`gpt-5.4` recommended — it is the model this engine was tested against).

The CLI that runs the model is **yours**, installed from npm by you, authenticated with your credentials. Microsoft does not host or resell the model API call — it is made directly by your CLI to the AI provider under your own terms of service and billing. The `a365` CLI specifies the model flag but does not mediate the API call.

The model used in every run is recorded in the HTML report header so you always have a record of what AI produced the scores.

---

## Q4. How can I tell which results are AI-generated vs. rule-based?

Every check in the output is tagged with a `type` field:

| Tag | What it means |
|---|---|
| `"Deterministic"` | Rule-based logic in the CLI itself — no AI involved. Pass/fail is exact (e.g., "tool name must not be empty"). |
| `"Semantic"` | Scored by the coding agent (AI). Includes a `reason` string written by the model explaining the judgment. |

In the HTML report, Semantic checks show the AI's `reason` text under each result. In the JSON report, the same `type` field and `reason` field appear on every check object so you can filter programmatically.

The deterministic checks run first and will pass or fail regardless of whether an AI agent is available. Semantic checks require the coding agent.

---

## Q5. What files remain on disk after the evaluation completes?

Three output files are written to the directory you specify with `--output-dir`:

| File | Contents | Kept after run? |
|---|---|---|
| `<server-name>_checklist.json` | Full checklist with scores and AI reasons | Yes — also used by re-runs to skip re-discovery |
| `<server-name>_eval_report.html` | Human-readable HTML report | Yes |
| `<server-name>_eval_report.json` | Machine-readable JSON report | Yes |

Temporary files created during the run (the prompt file passed to the coding agent CLI) are deleted automatically when the run finishes or is cancelled. The auth token supplied via `--auth-token` is never written to disk.

If you want to remove all evaluation output, delete the three files above from your output directory. No other local state is created by this command.
