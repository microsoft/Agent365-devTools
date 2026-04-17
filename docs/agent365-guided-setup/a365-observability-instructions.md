# Add Agent 365 Observability

> **SCOPE — THIS FILE ONLY:** This file covers adding the Agent 365 observability SDK and tracing instrumentation to agent code. It does **NOT** cover provisioning, deployment, or CLI setup. If the user asked to "make their agent A365 compliant" or run setup, close this file and open `a365-setup-instructions.md` instead.
>
> **DO NOT use this file unless the user explicitly asked to add observability, telemetry, logging, or tracing.**

Add Agent 365 observability to your agent at any point after `a365 setup all` has completed.

> **Implementation reference:** [Agent observability — Microsoft Learn](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/observability)
>
> **Prerequisite:** `a365 setup all` must have completed successfully so `AgentId` and `TenantId` values are present in `appsettings.json` (written by setup) or `a365.generated.config.json`. If neither source has these values, run `a365 setup all` first.

---

## Task A — Add the observability SDK to the project

Ask your coding agent (Claude Code, GitHub Copilot, or similar):

> "Using #file:a365-observability-instructions.md, add observability to this project"

The agent will follow the MS Learn reference above to:
1. Install the observability SDK packages for your project type (.NET, Python, or Node.js)
2. Register the exporter and tracing in startup code
3. Wire up the token resolver in the agent's turn handler
4. Add the exporter configuration setting (`EnableAgent365Exporter` / `ENABLE_A365_OBSERVABILITY_EXPORTER`) and leave it disabled by default

After completing the above steps, the agent **must** ask the user:

> "Setup is complete. Would you like me to scan your code and add instrumentation automatically? I'll find LLM calls, tool dispatches, agent-to-agent calls, and output operations and wrap each with the appropriate tracing scope."

- If **yes**: scan all agent source files, identify operations matching the scope types in Task B, present a summary of planned changes, confirm with the user, then apply — adding the correct scope wrapper and required usings to each.
- If **no**: skip — instrumentation can be added later via Task B.

After setup, set `EnableAgent365Exporter` to `true` in `appsettings.json` (or `ENABLE_A365_OBSERVABILITY_EXPORTER=true` in `.env`) to start exporting traces.

---

## Task B — Instrument individual code blocks

Select a code block in your editor (an LLM call, a tool dispatch, or an agent-to-agent call), then ask:

> "Using #file:a365-observability-instructions.md, add observability to the selected code"

The agent will:
1. Read the selected code and identify the operation type
2. Infer the relevant parameters (model name, provider, tool name, etc.) — it will not ask for values it can read from the code
3. Present its interpretation and ask for confirmation before making any changes
4. Wrap the code block with the correct Agent365 tracing scope

### Supported scope types (auto-detected from code)

| What the code does | Scope applied |
|-------------------|---------------|
| Calls an LLM/model API (`gpt-4o`, `claude-3`, etc.) | `InferenceScope` |
| Dispatches a tool or plugin function | `ExecuteToolScope` |
| Calls another agent (A2A) | `InvokeAgentScope` |
| Sends final response back to user | `OutputScope` |

For Python and Node.js, equivalent OpenTelemetry spans are used with the same Agent365 attribute names. See the [MS Learn reference](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/observability) for attribute names and patterns.
