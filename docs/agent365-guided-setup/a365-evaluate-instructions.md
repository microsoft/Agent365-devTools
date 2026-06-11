# Agent 365 MCP Server Evaluation Pipeline — Setup Instructions for AI Agents

> **SCOPE — THIS FILE ONLY:** This file covers setting up the workstation and running the Agent 365 evaluation pipeline against an MCP server (`a365 develop-mcp evaluate`). It does **NOT** cover provisioning an agent (`a365 setup`), deploying code (`a365 deploy`), or adding observability. If the user asked to set up their agent for Agent 365, close this file and open `a365-setup-instructions.md` instead.

---

> **YOUR FIRST AND ONLY ACTION RIGHT NOW:** Ask the user the three path-determination questions below. Do NOT create todos, run commands, or read further until the user has answered all three questions. After all answers are received, create all todos for the determined path and mark Todo 1 in-progress.

**RULE 1 — ASK THREE QUESTIONS FIRST, THEN CREATE ALL TODOS.**

Before creating any todos or running any commands, ask the user these three questions (one at a time, wait for each response):

**Question 1: What is the URL of the MCP server you want to evaluate?**

Wait for the answer. Store as `serverUrl`. Must be the **Streamable HTTP endpoint URL** of the MCP server (typically ends in `/mcp` or a similar path). Examples:
- `https://my-mcp-server.contoso.com/mcp`
- `http://localhost:5000/mcp` (for a locally-running server)

If the user replies with just a hostname or a non-HTTP URL, ask them to provide the full HTTPS or HTTP URL including the endpoint path.

**Question 2: Does the MCP server require a bearer token for authentication?**

- Yes
- No

Wait for the answer. Store as `needsAuth`:
- If **Yes**: ask the user to provide the bearer token (or to paste the output of `a365 develop get-token …` if the server is in the Agent 365 tenant). Store as `authToken`. Do NOT echo the token back to the user or log it.
- If **No**: set `authToken = null`.

**Question 3: How should the semantic checks be scored?**

1. Auto — try GitHub Copilot first, fall back to Claude Code
2. GitHub Copilot only
3. Claude Code only
4. None — generate the checklist and let me (or my own LLM) score it manually (bring-your-own-LLM)

Wait for the answer. Store as `evalEngine`:
- If **1 (Auto)**: `evalEngine = "auto"`
- If **2 (GitHub Copilot)**: `evalEngine = "github-copilot"`
- If **3 (Claude Code)**: `evalEngine = "claude-code"`
- If **4 (None)**: `evalEngine = "none"`

> **Note:** Auto and the named engines require the corresponding CLI to be installed on the workstation (`copilot` or `claude`). If neither is installed and `evalEngine` is `auto` or a named engine, the pipeline will stop after writing the checklist and print BYO-LLM instructions — that is expected, not an error.

After all three questions are answered, create all todos for the path and mark Todo 1 in-progress:

**Evaluation pipeline path** (5 todos total):
- Todo 1: `Step 1: Verify and Install/Update the Agent 365 CLI`
- Todo 2: `Step 2: (Optional) Verify a local scoring agent is available`
- Todo 3: `Step 3: Validate the MCP server URL is reachable`
- Todo 4: `Step 4: Run a365 develop-mcp evaluate`
- Todo 5: `Step 5: Review the HTML report and action items`

**RULE 2 — ALWAYS BEGIN FROM STEP 1.** Even if the CLI appears installed, you MUST run the validation commands in Step 1.

**RULE 3 — SUB-SECTIONS ARE NOT SEPARATE TODOS.** Each `## Step` has internal sub-sections — these are tasks WITHIN that step, NOT separate todos.

**RULE 4 — ONE STEP AT A TIME.** Complete each step fully. Mark its todo in-progress when starting, complete when done.

**RULE 5 — TELEMETRY DISCLOSURE.** When the user runs Step 4, the CLI fires a single fire-and-forget telemetry marker (`POST /agents/externalMcpServers/logEvaluate`) at the start of the evaluation. This call carries **no customer content** — no server URL, no engine selection, no output paths. Identity is taken from the bearer token. The marker is non-blocking; the evaluation runs even if telemetry fails. If the user explicitly asks about telemetry, tell them this — do not surface it unprompted.

**RULE 6 — SILENT EXECUTION.** Work silently. Do NOT narrate what you are about to do, announce step transitions, print todo state, emoji checklists, or step completion summaries. Only speak to the user when you need input, have an error to report, or need confirmation before a destructive action.

**RULE 7 — DO NOT SHIP THE TOKEN.** If the user provided an auth token in Question 2, pass it only via the `--auth-token` flag or the `A365_MCP_AUTH_TOKEN` environment variable at the moment you invoke the CLI (the env var is preferred — it keeps the token out of process listings and shell history). Do NOT write it to a file, paste it back into chat, or commit it to any config.

---

## Context

You are an AI coding agent with access to execute shell commands. Your task is to set up and run the Agent 365 MCP server evaluation pipeline. The pipeline discovers tools from a target MCP server, generates a checklist of deterministic + semantic checks, runs the semantic checks through a locally-installed coding agent (or hands them off for manual scoring), and produces an HTML report scoring the server's tool schema quality and an action-item list for improvement.

---

## Step 1: Verify and Install/Update the Agent 365 CLI

> **DO NOT SKIP THIS STEP.** Mark this todo in-progress now.

The Agent 365 CLI is a .NET global tool. Verify install + version:

```bash
a365 --version
```

- If the CLI is not installed: run `dotnet tool install --global Microsoft.Agents.A365.DevTools.Cli --prerelease`
- If installed but outdated: run `dotnet tool update --global Microsoft.Agents.A365.DevTools.Cli --prerelease`

### Verify .NET is available

```bash
dotnet --version
```

Confirm .NET 8.0 or later. If not installed, instruct the user to install .NET 8.0 from https://dotnet.microsoft.com/download.

### Step 1 completion

> **BEFORE MOVING ON:** Mark Todo 1 as **completed**. Mark Todo 2 as **in-progress**. Proceed to Step 2.

---

## Step 2: (Optional) Verify a local scoring agent is available

> **Skip this step entirely if `evalEngine = "none"`.** In that case, mark Todo 2 completed and jump to Step 3.

The evaluation pipeline scores semantic checks (tool-name clarity, description quality, parameter naming, return-shape sanity) using a locally-installed coding agent CLI. Verify the engine the user selected is present.

### If `evalEngine = "github-copilot"` or `"auto"`

Verify the GitHub Copilot CLI is installed:

```bash
copilot --version
```

- If installed: continue.
- If not installed: install with `npm install -g @github/copilot` (requires Node.js 18 or later). This is the standalone GitHub Copilot CLI (binary `copilot`) that the pipeline invokes — it is a different tool from the `gh copilot` GitHub CLI extension, which does not accept the flags the pipeline uses.

### If `evalEngine = "claude-code"` or `"auto"` (and Copilot was not found)

Verify Claude Code CLI is installed:

```bash
claude --version
```

- If installed: continue.
- If not installed: install via `npm install -g @anthropic-ai/claude-code` or follow [Claude Code install](https://docs.claude.com/claude-code).

### If `evalEngine = "auto"` and neither agent is installed

Tell the user one of the following is required, and that without one of them the pipeline will stop after writing the checklist (BYO-LLM mode). Offer them:
- Install GitHub Copilot CLI (see above), or
- Install Claude Code (see above), or
- Switch to `evalEngine = "none"` and score the checklist with their own LLM after the run.

### Step 2 completion

> Mark Todo 2 as **completed**. Mark Todo 3 as **in-progress**. Proceed to Step 3.

---

## Step 3: Validate the MCP server URL is reachable

> **DO NOT SKIP THIS STEP.** Catching a wrong URL here saves an aborted run later.

Verify the server responds to an initial request:

```bash
curl -i -X POST -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  ${authToken:+-H "Authorization: Bearer $authToken"} \
  --data '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"a365-precheck","version":"1.0"}}}' \
  "$serverUrl"
```

- **200 OK** with a JSON body containing `result` → MCP server is reachable. Continue.
- **401 Unauthorized** → the server needs a bearer token. Go back to Question 2 and obtain one. Re-run this check before proceeding.
- **404 Not Found** → the URL path is wrong. Ask the user to confirm the URL includes the full endpoint path (typically ends in `/mcp`).
- **Connection refused / DNS failure** → the server is not running or the host is unreachable. Tell the user, ask them to start the server or correct the URL, then re-validate.

> **If the user is evaluating a server behind a corporate firewall**, they may need to be on VPN or run the evaluation from a machine that can reach the server. The evaluation does NOT proxy through any Agent 365 service.

### Step 3 completion

> Mark Todo 3 as **completed**. Mark Todo 4 as **in-progress**. Proceed to Step 4.

---

## Step 4: Run `a365 develop-mcp evaluate`

> Pick an output directory (use the current directory unless the user specifies otherwise). The pipeline writes the checklist and HTML report there.

### Run the evaluation

```bash
a365 develop-mcp evaluate \
  --server-url "$serverUrl" \
  --output-dir "." \
  --eval-engine "$evalEngine" \
  ${authToken:+--auth-token "$authToken"}
```

### Optional: configure via environment variables

These settings can come from environment variables instead of (or alongside) the flags:

| Env var | Purpose |
|---|---|
| `A365_MCP_AUTH_TOKEN` | Bearer token for the MCP server, used when `--auth-token` is not passed. **Preferred over the flag** — it keeps the token out of process listings (`ps` / Task Manager) and shell history. If you pass `--auth-token`, the CLI prints a one-time warning recommending this variable. |
| `A365_EVAL_COPILOT_MODEL` | Override the GitHub Copilot model (exact model ID, e.g. `claude-haiku-4.5`). |
| `A365_EVAL_CLAUDE_MODEL` | Override the Claude Code model (alias, e.g. `haiku`). |

The model defaults to Claude Haiku 4.5; override only to move to a newer model without waiting for a CLI release.

### What you will see

The CLI logs progress in numbered steps `[1/5]` through `[5/5]`:

| Step | What it does |
|---|---|
| `[1/5] Discovering tools …` | Connects to the MCP server, runs `tools/list`, captures schemas. |
| `[2/5] Generated evaluation checklist …` | Writes `<server>_checklist.json` to the output dir. |
| `[3/5] Running semantic evaluation` | Hands the checklist to the selected coding agent for scoring (or stops here if `evalEngine = "none"` or no agent is available). |
| `[4/5] Analysis complete: score …` | Aggregates per-tool and overall maturity scores. |
| `[5/5] Writing reports` | Produces `<server>_eval_report.html` and `<server>_eval_report.json`. |

### Common in-run conditions

- **Coding agent missing.** The CLI prints a "pick one" guidance block and stops after step `[2/5]`. The checklist is now on disk — the user can score it with their own LLM and re-run the same command to resume from the checklist.
- **Mid-run abort.** Re-running the same command picks up from the existing checklist file. Delete `<server>_checklist.json` to force a fresh discovery.
- **WAM/auth prompt on Windows.** Same handling as in `a365-setup-instructions.md` — the prompt is for the CLI's own Microsoft Graph token, not for the MCP server's bearer.

### Step 4 completion

> Mark Todo 4 as **completed**. Mark Todo 5 as **in-progress**. Proceed to Step 5.

---

## Step 5: Review the HTML report and action items

> Open `<server>_eval_report.html` from the output directory in a browser, or display the report path and prompt the user to open it.

### Headline numbers

The report has:

- **Overall score** (0–100). Higher is better.
- **Maturity level** (0–4): Level 0 (Functional) → Level 4 (Exemplary).
- **Per-tool scores** with category breakdowns: schema quality, semantic clarity, parameter quality, return-shape quality.
- **Action item list**, ordered by impact.

### Triage with the user

1. **If overall score is below 60 or maturity is Level 0–1 (Functional/Described)**, walk through the top 3 action items with the user. These usually cluster around: tool names that don't describe the action, descriptions that are stubs, parameter names that are abbreviations.
2. **If overall score is 60–74 or maturity is Level 2 (Consistent)**, the schema is shippable but has room. Pick 1–2 high-impact action items to address.
3. **If overall score is 75 or above or maturity is Level 3–4 (Optimized for AI/Exemplary)**, summarize the strengths and surface any low-hanging fixes.

### Re-running after fixes

When the user updates their tool schemas, delete the checklist file and re-run Step 4:

```bash
rm "<server>_checklist.json"
a365 develop-mcp evaluate --server-url "$serverUrl" --output-dir "." --eval-engine "$evalEngine" ${authToken:+--auth-token "$authToken"}
```

This forces a fresh discovery so the new schemas are scored.

### Step 5 completion

> Mark Todo 5 as **completed**. Summarize the headline numbers and the top 3 action items to the user.
>
> If the user asked "what's next?", offer:
> - Re-evaluate after fixing the top items.
> - Add the evaluate command to their CI/PR pipeline so schema regressions are caught automatically. (Provide a sample command, not a full CI snippet, unless the user asks for one.)
>
> Do NOT proactively suggest adding observability or running `a365 setup` — those are separate workflows in this folder.

---

## Error Handling and Troubleshooting

If any step results in an error, stop and analyze the error message carefully.

### Common errors

| Error | Likely cause | Fix |
|---|---|---|
| `Unauthorized` from `tools/list` | Wrong or expired bearer token | Re-acquire the token (Question 2). |
| `Could not parse server URL` | URL doesn't include the protocol | Add `http://` or `https://` to the URL. |
| `No coding agent detected` after `[2/5]` | Neither `copilot` nor `claude` is on PATH | Install one (Step 2) or pass `--eval-engine none`. |
| `Failed to write report to <path>` | Output dir not writable | Choose a different `--output-dir` or fix permissions. |
| Telemetry warning at debug level | Non-blocking — the marker call failed | Ignore. The evaluation runs regardless. |

For broader help see:

- **[Agent 365 CLI Reference](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-cli)** — command-specific options.
- **[GitHub Issues](https://github.com/microsoft/Agent365-devTools/issues)** — search by error message.

### Quick tips

- The pipeline is idempotent — safe to re-run after fixing an issue.
- The checklist JSON is human-readable and editable — the user can hand-score categories the agent got wrong and re-run the report step.
- Log files: Windows `%APPDATA%\a365\logs\`, Linux/Mac `~/.config/a365/logs/`.
