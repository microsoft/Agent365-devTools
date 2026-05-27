# Add Agent 365 Observability

> **SCOPE — THIS FILE ONLY:** This file covers adding the Agent 365 observability SDK and tracing instrumentation to agent code. It does **NOT** cover provisioning, deployment, or CLI setup.
>
> This file is used in two ways: (1) automatically, as the final step of `a365-setup-instructions.md` when the selected capabilities include Observability; (2) directly, when the user explicitly asks to add observability, telemetry, logging, or tracing. If the user asked to start from scratch (e.g. "make my agent A365 compliant"), open `a365-setup-instructions.md` first.

Add Agent 365 observability to your agent at any point after `a365 setup all` has completed.

> **Implementation reference:** [Agent observability — Microsoft Learn](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/observability)
>
> **Prerequisite:** `a365 setup all` must have completed successfully so `AgentId` and `TenantId` values are present in `appsettings.json` (written by setup) or `a365.generated.config.json`. If neither source has these values, run `a365 setup all` first.

---

## Overview

This skill instruments Microsoft Agent 365 observability into an existing agent codebase
without disrupting the agent's core logic. It:

1. **Detects** the agent type (.NET AgentFramework, Node.js, or Python)
2. **Installs** the correct A365 observability packages (core + hosting + optional extensions)
3. **Wires** observability in the entry point
4. **Adds** BaggageBuilder context or BaggageMiddleware to message handlers
5. **Implements** the agentic token resolver with caching
6. **Adds** manual instrumentation scopes (InvokeAgentScope, InferenceScope, ExecuteToolScope — **required for store publishing**)
7. **Updates** configuration files with observability settings
8. **Validates** the build passes

> **Store publishing requirement:** The Agent 365 store validation requires `InvokeAgentScope`,
> `InferenceScope`, and `ExecuteToolScope` to be implemented. This skill wires them.

All changes are **additive** and **idempotent** — rerunning the skill is safe.

---

## Phase 0: Load Detection Cache and Validate

**TaskCreate** — "Load detection cache and validate with user"

**Read** `.a365-workspace-detection.json`.

If the file is missing or `detectedAt` is older than 60 minutes:
> "`a365-setup` must be run before this skill — it registers your agent with Agent 365 and writes
> the project detection cache this skill depends on. Run `a365-setup` now, then return here."

Stop until the user confirms `a365-setup` has been run.

Load from cache: `agentStack`, `programmingLanguage`, `usesTeamsOrCopilot`, `agentType`, `authMode` (if previously stored).

Present the loaded values in one message and wait for confirmation:

```
Here's what we detected about your agent:
  • Stack:    {agentStack}
  • Language: {programmingLanguage}

Reply **yes** to confirm, or describe any corrections.
```

**TaskUpdate** — Mark complete: "Load detection cache and validate with user"

---

## Phase 0.5: Agent Kind and Authentication Mode

**TaskCreate** — "Determine agent kind and authentication mode"

Follow the agent kind and auth mode detection rules to determine the correct values.

If `agentType` and `authMode` are already present in the detection cache (from a prior skill run in this session), confirm the values with the user and skip the questions.

Store `agentType` (`ai-teammate` = AI Teammate, or `system-agent` = Agent (Non AI Teammate)) and `authMode`:
- **AI Teammate:** `user-delegated` (OBO as signed-in user) or `agentic-identity` (OBO as agent's own M365 identity)
- **Agent (Non AI Teammate):** `agentic-identity` (Assistive OBO) or `S2S` (Autonomous / Service Principal)

**Update `.a365-workspace-detection.json`** — merge `agentType` and `authMode` into the existing cache file, preserving all other fields (`agentStack`, `programmingLanguage`, `usesTeamsOrCopilot`, `detectedAt`). Use the **Write** tool to write the merged object back.

The `authMode` value drives Phases 3–5: OBO and S2S paths differ in entry point wiring (Phase 3), message handler pattern (Phase 4), and token resolver (Phase 5). **Phases 2, 6, 7, and 8 are identical regardless of `authMode`.**

**TaskUpdate** — Mark complete: "Determine agent type and authentication mode"

---

## Phase 1: Detect Agent Type

**TaskCreate** — "Detect agent type and load reference patterns"

1. **Run detection** using the following heuristics:
   - Check for `.NET AgentFramework` indicators (Microsoft.Agent.*, AgentFramework) → `.csproj`
   - Check for `Node.js` indicators (package.json, @langchain, openai, @microsoft/agents-*)
   - Check for `Python` indicators (requirements.txt, pyproject.toml, `.py` files, `microsoft-agents`)
   - Determine package file (*.csproj, package.json, pyproject.toml/requirements.txt)
   - Determine entry point (Program.cs, index.ts/js, app.py / host_agent_server.py)
   - Determine message handler location

2. **Load reference patterns:**
   - If .NET: **Read** `./references/dotnet-observability.md`
   - If Node.js: **Read** `./references/nodejs-observability.md`
   - If Python: **Read** `./references/python-observability.md`

3. **If agent type cannot be determined**, write marker `.a365setup-unknown-agent` and **exit early** with clear error message.

4. **TaskUpdate** — Mark complete and report detected agent type to user.

---

## Phase 2: Install A365 Observability Packages

**TaskCreate** — "Install A365 observability packages"

### For .NET AgentFramework

1. **Bash** — Run package installation (path-dependent):

   **OBO path** (`user-delegated` or `agentic-identity`):
   ```bash
   dotnet add package Microsoft.Agents.A365.Observability.Runtime
   dotnet add package Microsoft.Agents.A365.Observability.Hosting
   ```

   **S2S / autonomous agents — use the unified distro** (preferred; do NOT also add Runtime/Hosting):
   ```bash
   dotnet add package Microsoft.OpenTelemetry --version 1.0.0-beta.1
   dotnet add package Azure.Identity
   dotnet add package Microsoft.Identity.Client
   # Required: Microsoft.OpenTelemetry v1.0.0-beta.1 has a hard runtime dependency on
   # Microsoft.Extensions.Logging v10. Use the stable 10.0.4 release — do NOT use a
   # preview version; Microsoft.Agents.A365.Observability.Hosting requires >= 10.0.4
   # and a lower preview causes NU1605 downgrade errors at restore time.
   dotnet add package Microsoft.Extensions.Logging --version "10.0.4"
   ```
   > **⚠️ Do NOT add `Microsoft.Agents.A365.Observability.Hosting` or `.Runtime` as
   > direct `<PackageReference>` entries when using the unified distro.** `Microsoft.OpenTelemetry`
   > already brings both as transitive dependencies and re-exports their types. Adding them
   > directly causes **CS0433 type ambiguity** on `AgentDetails`, `CallerDetails`, and
   > `IExporterTokenCache<T>`. Remove any explicit Hosting/Runtime references from the `.csproj`.
   >
   > **⚠️ TFM requirement:** If the project targets `net8.0`, upgrade to `net9.0` or later.
   > `Microsoft.OpenTelemetry` v1.0.0-beta.1 has a hard runtime dependency on
   > `Microsoft.Extensions.Logging` v10 which is not part of the `net8.0` or `net9.0`
   > framework — it must be a direct reference so the assembly is copied to the output
   > directory. Without it you get `FileNotFoundException` at startup.

2. **Optional auto-instrumentation extensions** — ask the user which AI framework they use.

   **If the user selects `Extensions.OpenAI` — pre-flight check (do this first, as a named step):**
   ```bash
   dotnet list package | grep Azure.AI.OpenAI
   ```
   If the installed version is below `2.7.0-beta.2`, upgrade it **before** installing the extension:
   ```bash
   dotnet add package Azure.AI.OpenAI --version 2.7.0-beta.2
   ```
   Do this proactively — do not wait for a build failure to discover the version conflict.

   Then install the selected extension(s):
   ```bash
   # Semantic Kernel
   dotnet add package Microsoft.Agents.A365.Observability.Extensions.SemanticKernel
   # OpenAI (requires Azure.AI.OpenAI >= 2.7.0-beta.2 — checked above)
   dotnet add package Microsoft.Agents.A365.Observability.Extensions.OpenAI
   # Agent Framework
   dotnet add package Microsoft.Agents.A365.Observability.Extensions.AgentFramework
   ```

3. **Verify** the packages appear in the `.csproj` file.

### For Node.js

1. **Bash** — Run package installation (core + hosting + unified distro):
   ```bash
   npm install @microsoft/opentelemetry
   npm install @microsoft/agents-a365-observability
   npm install @microsoft/agents-a365-runtime
   npm install @microsoft/agents-a365-observability-hosting
   ```

2. **Optional auto-instrumentation extensions** — ask the user which AI framework they use.

   **If the user selects `extensions-openai` — pre-flight check (do this first):**
   The extension requires `@openai/agents ^0.7.0` as a peer dependency — this is the **OpenAI Agents SDK**, NOT the `openai` npm package and NOT `@azure/openai`. Check and install the peer dep first:
   ```bash
   npm list @openai/agents
   # If missing or below 0.7.0:
   npm install @openai/agents@^0.7.0
   ```

   Then install the selected extension(s):
   ```bash
   # OpenAI Agents SDK (requires @openai/agents ^0.7.0 — checked above)
   npm install @microsoft/agents-a365-observability-extensions-openai
   # LangChain
   npm install @microsoft/agents-a365-observability-extensions-langchain
   ```

3. **Verify** the packages appear in `package.json`.

### For Python

1. **Bash** — Run package installation (unified distro + S2S deps):
   ```bash
   pip3 install microsoft-opentelemetry 2>/dev/null || pip install microsoft-opentelemetry
   # S2S path also requires:
   pip3 install msal azure-identity httpx 2>/dev/null || pip install msal azure-identity httpx
   ```
   > **OBO path:** only `microsoft-opentelemetry` is required. The `msal`, `azure-identity`, and `httpx` packages are only needed for the S2S token service.

2. **Optional auto-instrumentation extensions** — ask the user which AI framework they use and install accordingly:
   ```bash
   # Semantic Kernel
   pip3 install microsoft-agents-a365-observability-extensions-semantic-kernel 2>/dev/null || pip install microsoft-agents-a365-observability-extensions-semantic-kernel
   # OpenAI Agents SDK
   pip3 install microsoft-agents-a365-observability-extensions-openai 2>/dev/null || pip install microsoft-agents-a365-observability-extensions-openai
   # Agent Framework
   pip3 install microsoft-agents-a365-observability-extensions-agent-framework 2>/dev/null || pip install microsoft-agents-a365-observability-extensions-agent-framework
   # LangChain
   pip3 install microsoft-agents-a365-observability-extensions-langchain 2>/dev/null || pip install microsoft-agents-a365-observability-extensions-langchain
   ```

3. **Update the dependency manifest** — `pip install` does not modify `requirements.txt` or `pyproject.toml` automatically. Explicitly add the installed packages:
   - `requirements.txt` project: append `microsoft-opentelemetry` (and S2S deps if applicable)
   - `pyproject.toml` project: add under `[project] dependencies` or run `uv add microsoft-opentelemetry` / `poetry add microsoft-opentelemetry`

4. **Verify** the packages appear in `requirements.txt` or `pyproject.toml`.

7. **TaskUpdate** — Mark complete.

---

## Phase 3: Wire Observability in Entry Point

**TaskCreate** — "Wire observability in entry point"

> **Pre-existing placeholders:** As of CLI 1.1, `a365 setup all` auto-writes `Agent365Observability` placeholder sections to `appsettings.json` (.NET) or `.env` (Node.js/Python). Before creating config from scratch, **check if placeholders already exist** and fill in values rather than duplicating the section.

### For .NET AgentFramework

1. **Read** the current entry point (`Program.cs` or detected file).

2. **Edit** — Add observability wiring following the reference pattern in `dotnet-observability.md`:
   - Add using directives for the observability namespaces
   - **OBO path** (`user-delegated` or `agentic-identity`): call `builder.Services.AddAgenticTracingExporter();` then `builder.AddA365Tracing();`
   - **S2S path**: First **Write** the two scaffold files from the reference doc — `Observability/ObservabilityServiceExtensions.cs` (DI extension with `AddAgent365Observability()` using `ServiceTokenCache` and conditional `ObservabilityTokenService`) and `Observability/ObservabilityTokenService.cs` (background service that acquires the Observability API token via the MSAL FMI 3-hop chain with `.WithFmiPath()` targeting scope `api://9b975845-388f-4429-889e-eab1ef63949c/.default`, supports MSI with client-secret fallback). Then call `builder.Services.AddAgent365Observability();` and `builder.UseMicrosoftOpenTelemetry(...)` with token resolver reading from the `ServiceTokenCache`. **Critical:** Set `o.Agent365.Exporter.UseS2SEndpoint = true` in the options callback — without this, the exporter posts to the wrong path (`/observability/` instead of `/observabilityService/`) and gets HTTP 401. See "Known Issues" section.
   - Optionally register `adapter.Use(new BaggageTurnMiddleware())` (OBO path only) to auto-populate baggage on every request
   - Mark all new lines with: `// A365 Observability — best-effort instrumentation (verify against official sample)`

3. **Preserve** all existing code — only add new lines, never remove.

### For Node.js

1. **Read** the current entry point (`index.ts`, `app.ts`, or detected file).

2. **Edit** — Add observability initialization following the reference pattern in `nodejs-observability.md`:
   - Add imports for `ObservabilityManager` from `@microsoft/agents-a365-observability`
   - **OBO path**: Call `useMicrosoftOpenTelemetry({ a365: { enabled: true, tokenResolver } })` from `@microsoft/opentelemetry` **before** any LLM/framework imports. The `tokenResolver` reads from `AgenticTokenCacheInstance`.
   - **S2S path**: First **Write** `observability/token-cache.ts` (in-memory token cache with `cacheToken`/`getCachedToken`/`tokenResolver`) and `observability/observability-token-service.ts` using the scaffold pattern from `nodejs-observability.md` (S2S section). This module acquires the Observability API token via MSAL FMI 3-hop chain (`@azure/msal-node` with `fmiPath` parameter, targeting scope `api://9b975845-388f-4429-889e-eab1ef63949c/.default`, supports MSI with client-secret fallback) and refreshes it every 50 min. Then call `useMicrosoftOpenTelemetry()` with the S2S workaround pattern from `nodejs-observability.md` (custom `Agent365Exporter` + `A365SpanProcessor` via `spanProcessors` when `AGENT365_USE_S2S_ENDPOINT=true`). Set `ENABLE_A365_OBSERVABILITY_EXPORTER=false` in `.env`. Also run `npm install @microsoft/opentelemetry @azure/msal-node @azure/identity @opentelemetry/sdk-trace-base`.
   - Optionally register `adapter.use(new BaggageMiddleware())` (OBO path) to auto-populate baggage on every request
   - Mark all new lines with: `// A365 Observability — best-effort instrumentation (verify against official sample)`

3. **Preserve** all existing code — only add new lines, never remove.

### For Python

1. **Read** the current entry point (`app.py`, `host_agent_server.py`, or detected file).

2. **Edit** — Add observability configuration following the reference pattern in `python-observability.md`:
   - Add `from microsoft.opentelemetry import use_microsoft_opentelemetry` and call `use_microsoft_opentelemetry(enable_a365=True, a365_token_resolver=...)` with `service_name` and `service_namespace`
   - **OBO path**: Wire `a365_token_resolver` to return the cached agentic token from `token_cache.py`.
   - **S2S path**: First **Write** `observability/token_cache.py` (in-memory token cache with `cache_token`/`get_cached_token`) and `observability/observability_token_service.py` using the scaffold pattern from `python-observability.md` (S2S section). This module acquires the Observability API token via MSAL FMI 3-hop chain (`msal.ConfidentialClientApplication` with `fmi_path` parameter, targeting scope `api://9b975845-388f-4429-889e-eab1ef63949c/.default`, supports MSI with client-secret fallback) and refreshes it every 50 min via an `asyncio` background task. Then call `use_microsoft_opentelemetry(enable_a365=True, a365_token_resolver=...)` from `microsoft.opentelemetry` and schedule `run_token_service()` as an asyncio task. Also install `msal` and `azure-identity` if not already present.
   - Optionally register `BaggageMiddleware` or use `ObservabilityHostingManager` on the adapter (OBO path) to auto-populate baggage on every request
   - Mark all new lines with: `# A365 Observability — best-effort instrumentation (verify against official sample)`

3. **Preserve** all existing code — only add new lines, never remove.

4. **TaskUpdate** — Mark complete.

---

## Phase 4: Add BaggageBuilder Context to Message Handler

**TaskCreate** — "Add BaggageBuilder context to message handler"

> **Skip this phase** if BaggageMiddleware was registered in Phase 3 — the middleware handles
> baggage propagation automatically for every request.

> **Auth mode note:** All three `authMode` values use `authHandlerName: "AGENTIC"` in the
> code — the token exchange call is identical. The identity in traces is determined by Azure AD
> provisioning and the incoming token. Add an inline comment indicating which mode was chosen.

### For .NET AgentFramework

1. **Read** the detected message handler file.

2. **Edit** — Follow the reference pattern in `dotnet-observability.md` based on `authMode`:

   **OBO path** (`user-delegated` or `agentic-identity`):
   - Inject `IExporterTokenCache<AgenticTokenStruct>` in the constructor
   - Use `new BaggageBuilder().FromTurnContext(turnContext).Build()` — requires `using Microsoft.Agents.A365.Observability.Hosting.Extensions;`; `Build()` returns `IDisposable`, use `using var`
   - Call `RegisterObservability` with all four arguments per turn (wrap in try/catch — non-fatal):
     ```csharp
     _agentTokenCache.RegisterObservability(
         turnContext.Activity.Recipient.AgenticAppId,
         turnContext.Activity.Recipient.TenantId,
         new AgenticTokenStruct(
             userAuthorization: UserAuthorization,
             turnContext: turnContext,
             authHandlerName: "AGENTIC"),
         EnvironmentUtils.GetObservabilityAuthenticationScope()
     );
     ```
     - `user-delegated`: token exchange resolves to the **signed-in user's** identity → traces attributed to the user
     - `agentic-identity`: token exchange resolves to the **agentic user** provisioned in Azure AD → traces attributed to the agent
   - Add inline comment: `// A365 auth mode: {authMode} — see: https://learn.microsoft.com/en-us/entra/agent-id/agent-on-behalf-of-oauth-flow`

   **S2S path**:
   - Inject `Agent365ObservabilityContext` (singleton registered by `AddAgent365Observability()`) in the constructor — **not** `IExporterTokenCache<AgenticTokenStruct>`
   - **Baggage:** Use `new BaggageBuilder().FromTurnContext(turnContext).Build()` as a separate `using var baggageScope` — `FromTurnContext()` is an extension on `BaggageBuilder` **only**; it does not exist on `InvokeAgentScope` or any scope type
   - **Scope:** Use `InvokeAgentScope.Start(new Request(...), new InvokeAgentScopeDetails(endpoint: new Uri("...")), _obs.AgentDetails, callerDetails)` as a separate `using var scope` — `InvokeAgentScopeDetails` has **no parameterless constructor**; always pass at least `endpoint`. `CallerDetails` with the blueprint sponsor's identity is **required** for S2S traces to appear in the portal
   - **No** per-turn `RegisterObservability()` call; **no** `.FromTurnContext()` chaining on the scope
   - Add inline comment: `// A365 auth mode: S2S — FMI 3-hop chain via ObservabilityTokenService (scope: api://9b975845-388f-4429-889e-eab1ef63949c/.default)`

   Mark all new lines with: `// A365 Observability — best-effort instrumentation (verify against official sample)`

3. **Preserve** all existing handler logic.

### For Node.js

1. **Read** the detected message handler file.

2. **Edit** — Add BaggageBuilder context following the reference pattern in `nodejs-observability.md`:
   - Import `BaggageBuilder` from `@microsoft/agents-a365-observability`
   - Import `AgenticTokenCacheInstance`, `BaggageBuilderUtils` from `@microsoft/agents-a365-observability-hosting`
   - Import `getObservabilityAuthenticationScope` from `@microsoft/agents-a365-runtime`
   - **OBO paths only** (`user-delegated` / `agentic-identity`): Call `AgenticTokenCacheInstance.RefreshObservabilityToken(agentId, tenantId, context, authorization, scopes)` at the start of each turn (non-fatal, wrap in try/catch):
     - `user-delegated`: `authorization` is the **user's** delegated token → traces attributed to the user
     - `agentic-identity`: `authorization` resolves to the **agentic user** provisioned in Azure AD → traces attributed to the agent
   - **S2S path**: Do **NOT** call `AgenticTokenCacheInstance.RefreshObservabilityToken` — there is no user authorization token. The `tokenResolver` passed to `useMicrosoftOpenTelemetry()` (set up in Phase 3) handles authentication via the FMI 3-hop chain token service.
   - Use `BaggageBuilderUtils.fromTurnContext(new BaggageBuilder(), context).build()` to build baggage automatically from TurnContext
   - Wrap the handler body in `await baggageScope.run(async () => { ... })`
   - Add inline comment: `// A365 auth mode: {authMode} — see: https://learn.microsoft.com/en-us/entra/agent-id/agent-on-behalf-of-oauth-flow`
   - Mark all new lines with: `// A365 Observability — best-effort instrumentation (verify against official sample)`

3. **Preserve** all existing handler logic.

### For Python

1. **Read** the detected message handler file.

2. **Edit** — Add BaggageBuilder context following the reference pattern in `python-observability.md`:
   - Import `BaggageBuilder` from `microsoft.opentelemetry.a365.core`
   - Import `populate` from `microsoft.opentelemetry.a365.hosting.scope_helpers.populate_baggage`
   - Import `AgenticTokenCache`, `AgenticTokenStruct` from `microsoft.opentelemetry.a365.hosting.token_cache_helpers`
   - Import `get_observability_authentication_scope` from `microsoft.opentelemetry.a365.runtime`
   - Call `token_cache.register_observability(agent_id=..., tenant_id=..., token_generator=AgenticTokenStruct(authorization=AGENT_APP.auth, turn_context=context), observability_scopes=get_observability_authentication_scope())`:
     - `user-delegated`: the OBO exchange resolves to the **signed-in user's** identity
     - `agentic-identity`: the OBO exchange resolves to the **agentic user** provisioned in Azure AD
     - `S2S`: agent authenticates as itself — no user context available
   - Use `populate(builder, turn_context)` to auto-populate baggage, then `with builder.build():`
   - Wrap existing agent logic inside the baggage scope
   - Add inline comment: `# A365 auth mode: {authMode} — see: https://learn.microsoft.com/en-us/entra/agent-id/agent-on-behalf-of-oauth-flow`
   - Mark all new lines with: `# A365 Observability — best-effort instrumentation (verify against official sample)`

3. **Preserve** all existing handler logic.

4. **TaskUpdate** — Mark complete.

---

## Phase 5: Implement Agentic Token Resolver

**TaskCreate** — "Implement agentic token resolver with caching"

For AI Teammate agents using the hosting packages, the built-in token cache (`AddAgenticTracingExporter` for .NET, `AgenticTokenCacheInstance` for Node.js, `AgenticTokenCache` for Python) handles caching automatically — no custom resolver needed. Skip to step 3 for these agents.

### For .NET AgentFramework (hosting path)

1. `AddAgenticTracingExporter()` (registered in Phase 3) provides the `IExporterTokenCache<AgenticTokenStruct>` DI instance — no additional token resolver class needed.

2. In the agent class, inject `IExporterTokenCache<AgenticTokenStruct>` in the constructor and call `RegisterObservability(...)` per turn (already done in Phase 4).

### For .NET AgentFramework (S2S path)

The `ObservabilityTokenService` background service (created in Phase 3 via the scaffold) acquires and refreshes the Observability API token automatically via the FMI 3-hop chain (Blueprint → Agent Identity → Power Platform PFAT token) — no manual `TokenResolver` delegate needed.

1. **Check** if `Observability/ObservabilityServiceExtensions.cs` and `Observability/ObservabilityTokenService.cs` exist. If yes, **skip** — they were already created in Phase 3.

2. **If absent** (Phase 3 was skipped or re-running the skill on a partial state), create them now following the S2S scaffold patterns in `dotnet-observability.md`. These files provide `AddAgent365Observability()` (DI extension registering `AddServiceTracingExporter`, `ObservabilityTokenService`, and `Agent365ObservabilityContext`) and `ObservabilityTokenService` (background service that acquires the Observability API token via the FMI 3-hop chain and refreshes it every 50 minutes).

### For Node.js (OBO path)

`AgenticTokenCacheInstance` from `@microsoft/agents-a365-observability-hosting` handles caching automatically. The `useMicrosoftOpenTelemetry()` call in Phase 3 wires it as the `tokenResolver`. No additional token resolver module is needed unless `Use_Custom_Resolver=true` is required (see reference doc for custom resolver pattern).

### For Node.js (S2S path)

**Check** if `observability/observability-token-service.ts` exists. If yes, **skip** — it was created in Phase 3.

**If absent** (Phase 3 was skipped or re-running), create `observability/token-cache.ts` and `observability/observability-token-service.ts` now using the scaffold from `nodejs-observability.md` (S2S section). The token service uses MSAL (`@azure/msal-node`) with `fmiPath` to acquire tokens via the FMI 3-hop chain targeting scope `api://9b975845-388f-4429-889e-eab1ef63949c/.default`. Call `startTokenService(config)` at app startup and pass `tokenResolver` from the cache module to `useMicrosoftOpenTelemetry()`.

### For Python (OBO path)

`AgenticTokenCache` from `microsoft.opentelemetry.a365.hosting.token_cache_helpers` handles caching automatically. It was wired as the `token_resolver` in the `configure()` call in Phase 3. No additional module is needed.

### For Python (S2S path)

**Check** if `observability/observability_token_service.py` exists. If yes, **skip** — it was created in Phase 3.

**If absent**, create `observability/token_cache.py` and `observability/observability_token_service.py` now using the scaffold from `python-observability.md` (S2S section). The token service uses MSAL (`msal.ConfidentialClientApplication`) with `fmi_path` to acquire tokens via the FMI 3-hop chain targeting scope `api://9b975845-388f-4429-889e-eab1ef63949c/.default`. Call `acquire_initial_token()` for pre-warm, schedule `run_token_service()` as `asyncio.create_task()`, and pass `token_cache.get_cached_token` as the `a365_token_resolver` in `use_microsoft_opentelemetry()`.

**TaskUpdate** — Mark complete.

---

## Phase 5.5: Scan ALL Agent Source Files and Add Instrumentation Scopes

**TaskCreate** — "Scan all source files and instrument InvokeAgentScope, InferenceScope, ExecuteToolScope"

> **Store publishing requirement:** The Agent 365 store validator requires `InvokeAgentScope`,
> `InferenceScope`, and `ExecuteToolScope` to be present and populating telemetry. Missing any one
> of these three scopes causes store validation failure.

> **This phase is mandatory. Do NOT skip it or proceed to Phase 6 until it is complete.**
> **Do NOT write any scope code until Step 3 (the summary table) has been confirmed by the user.**

**Step 1 — Glob all source files** (excluding generated/build output):
- .NET: `**/*.cs` excluding `obj/`
- Node.js: `**/*.ts` or `**/*.js` excluding `node_modules/`, `dist/`
- Python: `**/*.py`

**Step 2 — Read and scan every file** for instrumentation points:

| What to look for | Scope to apply | Role |
|-----------------|---------------|------|
| Message handlers, timer loops, `BackgroundService.ExecuteAsync`, autonomous cycles — any agent "turn" or operation | `InvokeAgentScope` | **Root** — required outermost scope |
| LLM/model API calls (`CompleteChatAsync`, `chat.completions.create`, `kernel.InvokeAsync`, `RunStreamingAsync`, etc.) | `InferenceScope` | Child — nest inside `InvokeAgentScope` |
| Tool/function dispatch calls, external API calls acting as tools | `ExecuteToolScope` | Child — nest inside `InvokeAgentScope` |
| Final response / streaming output operations | `OutputScope` | Child — nest inside `InvokeAgentScope` |

**Step 3 — Present a summary table** of ALL findings and wait for user confirmation before writing any code:

```
Files scanned: X
Instrumentation plan:
| File | Method / Location | Operation | Scope to add |
|------|------------------|-----------|-------------|
| Agent/MyAgent.cs | OnMessageAsync | User message handler | InvokeAgentScope (root) |
| Agent/MyAgent.cs | OnMessageAsync → RunStreamingAsync | LLM streaming call | InferenceScope |
| Agent/MyAgent.cs | BuildAgent → GetCurrentWeather | Tool dispatch | ExecuteToolScope |
| WeatherMonitorService.cs | ExecuteAsync (timer loop body) | Autonomous cycle | InvokeAgentScope (root) |
| WeatherMonitorService.cs | ExecuteAsync → CompleteChatAsync | LLM call | InferenceScope |
...

Confirm to apply, or describe corrections.
```

**Step 4 — Apply** the scopes per language-specific patterns after confirmation, following the scope hierarchy rule:
- `InvokeAgentScope` is always the outermost scope — one per agent turn or autonomous operation
- `InferenceScope`, `ExecuteToolScope`, `OutputScope` are children — always nested inside an open `InvokeAgentScope`
- Never open child scopes as standalone top-level scopes — they produce orphaned spans the exporter silently drops

### For .NET AgentFramework — scope patterns

- `CallerDetails` must be passed to `InvokeAgentScope.Start()` as the 4th parameter — required for traces to appear in the MAC portal
- For S2S autonomous agents: read sponsor details from config (`Agent365Observability:Sponsor`) and construct `CallerDetails` with `UserDetails(userId, userName, userEmail)`
- For autonomous background operations with no `ITurnContext` (e.g. `BackgroundService`): use `new BaggageBuilder().AgentId(...).TenantId(...).Build()` — `FromTurnContext()` is not available without a turn context
- Pass `UserDetails` directly (not wrapped in `CallerDetails`) to `InferenceScope.Start()` and `ExecuteToolScope.Start()` as the optional 4th parameter
- `InferenceCallDetails` requires `providerName` — it is **not optional** (CS7036 if omitted)
- `ExecuteToolScope.RecordResponse()` takes `string`, not a `Response` object (CS1503 if passed an object)
- The `Agent365ObservabilityContext` singleton should hold both `AgentDetails` and `CallerDetails`

### For Node.js — scope patterns

- Use `ScopeUtils.populateInvokeAgentScopeFromTurnContext` from `@microsoft/agents-a365-observability-hosting` to auto-populate from TurnContext
- `CallerDetails` must be passed to `InvokeAgentScope.start()` as the 4th parameter
- Pass `UserDetails` directly to `InferenceScope.start()` and `ExecuteToolScope.start()` as the optional 4th parameter
- Export `callerDetails` (for `InvokeAgentScope`) and `userDetails` (for `InferenceScope`/`ExecuteToolScope`) from the entry point module alongside `agentDetails`

### For Python — scope patterns

- `CallerDetails` / `UserDetails` must be supplied when creating the top-level `InvokeAgentScope` — required for MAC portal visibility
- For S2S autonomous agents, construct `CallerDetails(UserDetails(userId, userName, userEmail))` from config or environment
- Pass `UserDetails` directly to `InferenceScope`, `ExecuteToolScope`, and `OutputScope`
- Keep shared observability state with both `agent_details` and `caller_details` / `user_details` so nested scopes can reuse them consistently

All new lines marked with the language-appropriate comment:
- C# / JavaScript / TypeScript: `// A365 Observability — best-effort instrumentation (verify against official sample)`
- Python: `# A365 Observability — best-effort instrumentation (verify against official sample)`

**TaskUpdate** — Mark complete only after all planned scopes have been applied and confirmed by the user.

---

## Phase 6: Update Configuration Files

**TaskCreate** — "Update configuration files with observability settings"

### For .NET AgentFramework

1. **Read** `appsettings.json` fully — **before writing anything** — and identify:
   - Whether a `Logging` section already exists anywhere in the file
   - Whether `Logging.LogLevel` already exists
   - The existing `EnableAgent365Exporter`, `AgentBlueprintId`, and `TenantId` values

   > **Merge safety rule (enforce without exception):** A JSON file may only have one `Logging` section. If `Logging` or `Logging.LogLevel` already exists, **merge** the new log level keys into that block. Never append a second `Logging` section — this produces silently invalid config where only the last block wins.

2. **Check for existing `a365 setup` configuration:**
   - `EnableAgent365Exporter` — always set to `true` in `appsettings.json` (the Development override sets it to `false`; `a365 setup` may have written `false` here, which this skill corrects)
   - If `Agent365Observability` section exists → **preserve** all existing values (AgentBlueprintId, TenantId, AgentName, AgentDescription, Sponsor)
   - If missing → add with defaults

3. **Edit** — Add or update observability configuration following the reference pattern:

   **`appsettings.json`** (exporter enabled by default in all environments except Development):
   ```json
   {
     "EnableAgent365Exporter": true,   // ← enabled by default; Development override turns it off
     "Agent365Observability": {
       "AgentBlueprintId": "...",      // ← populated by a365 setup (or placeholder if not run)
       "TenantId": "...",
       "AgentName": "",
       "AgentDescription": "",
       "Sponsor": {
         "UserId": "<<Blueprint ID>>",
         "UserName": "<<Blueprint Name>>",
         "UserEmail": "<<Blueprint Sponsor Email>>"
       },
       // S2S path only — add:
       // "ClientId": "<agent-blueprint-client-id>",
       // "ClientSecret": "<agent-blueprint-client-secret>",  // MSI tried first in prod; secret is local-dev fallback
       // "UseManagedIdentity": false  // ← set false for local dev (MSI only works on Azure infra)
     },
     "Logging": {
       "LogLevel": {
         "Default": "Information",
         "Microsoft.Agents.A365.Observability": "Debug",
         "OpenTelemetry": "Debug"
       }
     }
   }
   ```

   > **S2S note:** `EnableAgent365Exporter` must be `true` for S2S span export to work. `a365 setup` may write `false` — this skill corrects it. Also set `UseManagedIdentity: false` for local dev since MSI is only available on Azure infrastructure (App Service, AKS, VM). On local machines, MSI fails with `CredentialUnavailableError: Network unreachable`.
   >
   > **Sponsor note:** For S2S / autonomous agents, the `Sponsor` section provides `CallerDetails` for MAC portal trace visibility. Use the Blueprint app ID as `UserId`, the Blueprint display name as `UserName`, and the agent sponsor's email as `UserEmail`.

   **`appsettings.Development.json`** (create if absent — disables exporter for local dev so traces go to console only):
   ```json
   {
     "EnableAgent365Exporter": false
   }
   ```

4. **Critical:** The `Logging.LogLevel` section is **required** for observability events to appear in console output and Microsoft Defender. Without this, the SDK is instrumented but logs are suppressed. The `a365 setup` command does **not** add logging configuration.

5. **If `appsettings.json` does not exist**, create it with the complete structure above.

6. **If `Logging` or `Logging.LogLevel` already exists**, merge the new entries into that existing block. Do **not** create a second `Logging` section — only one is allowed in a JSON config file.

7. **Inform user:**
   - "Observability exporter is enabled by default (`EnableAgent365Exporter: true` in `appsettings.json`). For local development, `appsettings.Development.json` overrides this to `false` so traces go to console only."
   - If `AgentBlueprintId` or `TenantId` are empty: "Run `a365 setup` to populate AgentBlueprintId and TenantId, or fill them manually from your Entra app registration."
   - If S2S path: "Add `ClientId` and `ClientSecret` under `Agent365Observability` in `appsettings.json` — `ObservabilityTokenService` requires both. In production, MSI is tried first and the secret is a local-dev fallback; `ClientSecret` must still be present in config."

### For Node.js

1. **Read** `.env` (or `.env.local`, `.env.development`).

2. **Check for existing `a365 setup` configuration:**
   - If `ENABLE_A365_OBSERVABILITY_EXPORTER` exists → **preserve** it (do not change)
   - If missing → add with default value `false`

3. **Edit** — Add or update observability environment variables following the reference pattern in `nodejs-observability.md`:
   ```dotenv
   ENABLE_A365_OBSERVABILITY_EXPORTER=false
   SERVICE_NAME=my-agent
   A365_OBSERVABILITY_LOG_LEVEL=info|warn|error
   Use_Custom_Resolver=false

   # Sponsor / CallerDetails for MAC portal trace visibility
   agent365Observability__sponsorUserId=<<Blueprint ID>>
   agent365Observability__sponsorUserName=<<Blueprint Name>>
   agent365Observability__sponsorUserEmail=<<Blueprint Sponsor Email>>
   ```
   - **S2S path only:** Also add `AGENT365_USE_S2S_ENDPOINT=true` — this tells the distro to use the `/observabilityService/...` endpoint path instead of `/observability/...`.

4. **If `.env` does not exist**, create it with the variables above.

5. **If the project uses `.env.example`**, also update it with placeholder values.

6. **Inform user:**
   - If `ENABLE_A365_OBSERVABILITY_EXPORTER` is `false`: "Observability is instrumented but disabled. Set ENABLE_A365_OBSERVABILITY_EXPORTER=true in .env to start exporting traces."

### For Python

1. **Read** `.env` (or `.env.local`).

2. **Edit** — Add or update observability environment variables:
   ```dotenv
   ENABLE_A365_OBSERVABILITY_EXPORTER=false
   ```
   - **S2S path only:** Also add `AGENT365_USE_S2S_ENDPOINT=true` — this tells the distro to use the `/observabilityService/...` endpoint path instead of `/observability/...`.

3. **If `.env` does not exist**, create it with the variable above.

4. **Inform user:**
   - If `ENABLE_A365_OBSERVABILITY_EXPORTER` is `false`: "Observability is instrumented but disabled. Set ENABLE_A365_OBSERVABILITY_EXPORTER=true in .env to start exporting traces."

7. **TaskUpdate** — Mark complete.

---

## Phase 7: Validate Build

**TaskCreate** — "Validate build passes"

### For .NET AgentFramework

1. **Bash** — Run:
   ```bash
   dotnet build
   ```

2. **If build fails**, collect error output and present to user with suggested fixes.

3. **If build succeeds**, confirm to user.

### For Node.js

1. **Bash** — Run:
   ```bash
   npm install   # Ensure new packages are installed
   npm run build || npm run compile || echo "No build script found — skipping compile check"
   ```

2. **If build fails**, collect error output and present to user with suggested fixes.

3. **If build succeeds** (or no build script exists), confirm to user.

### For Python

1. **Bash** — Run an import check to verify the packages load without errors:
   ```bash
   python3 -c "from microsoft.opentelemetry import use_microsoft_opentelemetry; print('A365 observability imports OK')" 2>/dev/null || python -c "from microsoft.opentelemetry import use_microsoft_opentelemetry; print('A365 observability imports OK')"
   ```

2. **If import fails**, collect error output and present to user with suggested fixes (usually a missing `pip install`).

3. **If import succeeds**, confirm to user.

4. **TaskUpdate** — Mark complete.

---

## Phase 8: Test Locally

**TaskCreate** — "Test locally"

Ask the user:

```
AskUserQuestion:
  question: "Build succeeded. Want to run a quick local test now?"
  options:
    - "Yes — run the test-local skill"
    - "No — I'll test later"
```

If yes, invoke the `test-local` skill.

**TaskUpdate** — Mark complete.

---

## Phase 9: Final Summary

### Task A completion — final summary

> **REQUIRED.** After Task A is complete (SDK wired up, instrumentation applied or skipped), output a single combined summary in this format and nothing else:
>
> **Agent 365 setup complete.**
>
> **Provisioned resources** _(from `a365.generated.config.json` or the setup CLI output shown earlier in this session):_
> - Blueprint: `<agentBlueprintId>` _(agentType=1: N/A — uses Entra app ID instead)_
> - Agent identity: `<agentIdentityDisplayName>`
> - Agent registration: `<agentRegistrationId>`
> - Config written to: `appsettings.json`
>
> **Observability** _(list each file and scope added, or "No instrumentation added" if skipped):_
> - `<File>` — `<ScopeType>` around `<method>`
> - ...
> - Tracing exports to the A365 service by default. To disable locally: set `"EnableAgent365Exporter": false` in `appsettings.Development.json` (or the equivalent local env override for your platform)

Do NOT add commentary, next-step suggestions, or further output after this summary.

---

## Task B — Instrument individual code blocks

Select a code block in your editor (an LLM call, a tool dispatch, or an agent-to-agent call), then ask:

> "Using #file:a365-observability-instructions.md, add observability to the selected code"

The agent will:
1. Read the selected code and identify the operation type
2. Infer the relevant parameters (model name, provider, tool name, etc.) — it will not ask for values it can read from the code
3. Present its interpretation and ask for confirmation before making any changes
4. Wrap the code block with the correct Agent365 tracing scope

### Scope hierarchy — read this before instrumenting

Scopes are **hierarchical, not peer**. `InvokeAgentScope` is the root; the others are children that go inside it. The `Agent365Exporter` only exports `InvokeAgentScope` spans — child scopes opened without a parent `InvokeAgentScope` are silently dropped and never reach the observability service.

```
InvokeAgentScope          ← root — always required; one per agent turn or autonomous operation
  ├── InferenceScope      ← child — wrap each LLM call inside the turn
  ├── ExecuteToolScope    ← child — wrap each tool dispatch inside the turn
  └── OutputScope         ← child — wrap the final reply inside the turn
```

For simple agents with no nested LLM calls or tool dispatches, `InvokeAgentScope` alone is sufficient — do not add child scopes just to have them.

### Supported scope types (auto-detected from code)

| What the code does | Scope | Role |
|-------------------|-------|------|
| Handles a user message, a background/autonomous task, or an A2A call — any agent "turn" | `InvokeAgentScope` | **Root** — required outermost scope for every instrumented block |
| Calls an LLM/model API (`gpt-4o`, `claude-3`, etc.) | `InferenceScope` | Child — nest inside an open `InvokeAgentScope` |
| Dispatches a tool or plugin function | `ExecuteToolScope` | Child — nest inside an open `InvokeAgentScope` |
| Sends final response back to user | `OutputScope` | Child — nest inside an open `InvokeAgentScope` |

> **CRITICAL:** Do NOT open `InferenceScope`, `ExecuteToolScope`, or `OutputScope` as standalone top-level scopes. They will compile and run without error but produce orphaned spans that the exporter never picks up.

**For .NET agent turn handlers**, chain `.FromTurnContext(tc)` on `InvokeAgentScope` to propagate conversation baggage (tenantId, conversationId, channelId) into the span:
```csharp
using var scope = InvokeAgentScope.Start(new Request(text), new InvokeAgentScopeDetails(), agentDetails)
    .FromTurnContext(tc);
```

For Python and Node.js, equivalent OpenTelemetry spans are used with the same Agent365 attribute names. See the [MS Learn reference](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/observability) for attribute names and patterns.

---

## Error Handling

### Unknown Agent Type
If the agent type cannot be determined:
- Write marker: `.a365setup-unknown-agent`
- Exit early with message: "Could not detect agent type. Please verify this is a .NET AgentFramework, Node.js, or Python agent project."

### Build Failures
If the build fails after instrumentation:
- Do NOT revert changes
- Present error output to user
- Suggest fixes based on error messages
- Offer to help debug

### Missing Files
If expected files are not found:
- Ask user to confirm the project structure
- Suggest running detection again
- Offer to create missing files if appropriate

---

## Idempotency

This skill is safe to rerun. On subsequent runs:
- Skip package installation if packages already present
- Skip code edits if observability is already wired (detect by marker comments)
- Update configuration only if values are missing
- Always revalidate the build

---

## S2S Known Issues and Workarounds

### OtelWrite App Role Assignment

`a365 setup all` **attempts** to grant `Agent365.Observability.OtelWrite` to the Agent Identity SP, but this requires **Global Administrator** privileges. If the logged-in user is not a Global Admin, the assignment silently fails with 403 and trace exports will return HTTP 403 from the observability service.

**The CLI prints a PowerShell admin consent script** in its output when the assignment fails. When running `a365 setup all`, **always scan the output for this script block** and display it to the user in a fenced code block so they can copy it and hand it to a Global Admin.

If the script was not captured, grant the permission manually via Entra portal (requires Global Admin):
1. [Entra portal](https://entra.microsoft.com) > App registrations > select Blueprint app > API permissions
2. Add a permission > APIs my organization uses > search `9b975845-388f-4429-889e-eab1ef63949c`
3. Add both **Delegated** and **Application** `Agent365.Observability.OtelWrite` > Grant admin consent

Alternatively, read the `agentIdentityClientId` from `a365.generated.config.json` and use the Graph API:

```bash
# Create a temp JSON body file (required on Windows due to az rest escaping)
echo '{"principalId":"<agentIdentitySPObjectId>","resourceId":"2a275186-1775-4439-8551-5438df22cdfc","appRoleId":"8f71190c-00c8-461d-a63b-f74abde9ba52"}' > body.json
az rest --method POST --url "https://graph.microsoft.com/v1.0/servicePrincipals/<agentIdentitySPObjectId>/appRoleAssignments" --body @body.json
rm body.json
```

- `resourceId` `2a275186-...` is the Observability API SP object ID
- `appRoleId` `8f71190c-...` is the OtelWrite role ID
- For agents provisioned before CLI 1.1, this manual step is still required

### Node.js and .NET SDK `/otlp/` URL Path Bug

The Node.js SDK (`@microsoft/agents-a365-observability@0.2.0-preview.5`) and .NET SDK (`0.3.4-beta`) include `/otlp/` in the S2S export URL path. The Power Platform PFAT gateway returns `401 MSAuth10AuthenticatorTypeUnknown` on this path. Python SDK `0.1.0` does NOT include `/otlp/` and works correctly.

**Status:** Awaiting SDK fix. No workaround should be applied in generated code — this is an SDK-level issue.

### S2S Endpoint Path — `useS2SEndpoint` Not Passed by Distro

The `@microsoft/opentelemetry` distro creates `Agent365Exporter` internally but does NOT pass `useS2SEndpoint: true`. For S2S agents, the exporter defaults to the OBO path (`/observability/tenants/{tenantId}/otlp/agents/{agentId}/traces`), but S2S requires `/observabilityService/...`.

**This bug affects BOTH Node.js and .NET SDKs:**

**Node.js (`@microsoft/opentelemetry` v0.1.0-beta.1):**

1. `A365Configuration` — add `useS2SEndpoint` property + `AGENT365_USE_S2S_ENDPOINT` env var support
2. `distro.js` — pass `a365Config.useS2SEndpoint` when constructing `Agent365Exporter`

**For generated agent code:** Set the env var in `.env`:
```
AGENT365_USE_S2S_ENDPOINT=true
```

This is a distro-level fix. The `useMicrosoftOpenTelemetry()` call does NOT need a custom `spanProcessors` array — the built-in exporter reads the env var via `A365Configuration` and passes it to `Agent365Exporter`.

**.NET (`Microsoft.OpenTelemetry` v1.0.0-beta.1):**

The `UseMicrosoftOpenTelemetry()` builder extension does NOT set `UseS2SEndpoint = true` on the `Agent365ExporterOptions` when using the unified distro. Without this, the exporter posts to `/observability/` (OBO path) instead of `/observabilityService/` (S2S path), causing HTTP 401.

**Fix:** Set `UseS2SEndpoint = true` explicitly in the `UseMicrosoftOpenTelemetry` options callback:
```csharp
builder.UseMicrosoftOpenTelemetry(o =>
{
    o.Exporters = ExportTarget.Agent365 | ExportTarget.Console;
    o.Agent365.Exporter.UseS2SEndpoint = true;  // ← Required for S2S agents
    o.Agent365.Exporter.TokenResolver = async (agentId, tenantId) =>
    {
        return tokenCache != null
            ? await tokenCache.GetObservabilityToken(agentId, tenantId)
            : null;
    };
});
```

**URL paths:**
- OBO: `observability/tenants/{tenantId}/otlp/agents/{agentId}/traces`
- S2S: `observabilityService/tenants/{tenantId}/otlp/agents/{agentId}/traces`

### Node.js MSAL `fmiPath` Not Supported (AADSTS82008)

No published version of `@azure/msal-node` (v3.x or v5.x) serializes the `fmiPath` parameter to the token endpoint request body. Passing `fmiPath` in `acquireTokenByClientCredential()` options (even with `as any`) is silently ignored, resulting in:

```
AADSTS82008: All agentic applications requesting a token exchange token must include the fmipath parameter on the token request.
```

**Workaround (implemented in `nodejs-observability.md`):** For the client-secret local-dev path (`acquireT1ViaClientSecret`), use a direct HTTP POST to `https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token` with `fmi_path={agentId}` as a URL-encoded form parameter. The MSI path still uses MSAL + `ManagedIdentityCredential` which handles FMI via a different mechanism.

**Status:** Awaiting `@azure/msal-node` to ship native `fmiPath` support. Remove the HTTP workaround once available.

### Node.js LangChain Instrumentor Initialization Order

`LangChainTraceInstrumentor.instrument(LangChainCallbacks)` requires `ObservabilityManager` to be fully initialized. Calling it as a standalone statement after `useMicrosoftOpenTelemetry()` throws `"ObservabilityManager is not configured yet"` when `a365.enabled: true`.

**Workaround:** Use `instrumentationOptions: { langchain: {} }` inside the `useMicrosoftOpenTelemetry()` options object. This ensures the distro initializes the manager and the LangChain instrumentor in the correct order.

### .NET `Microsoft.OpenTelemetry` v1.0.0-beta.1 Requires .NET 10 Logging

`Microsoft.OpenTelemetry` v1.0.0-beta.1 has a hard **runtime** dependency on `Microsoft.Extensions.Logging` v10. On projects targeting `net8.0` or `net9.0`, this causes a `FileNotFoundException` for `Microsoft.Extensions.Logging, Version=10.0.0.0` at startup.

**Workaround:** Add an explicit direct reference to the **stable** v10 release so the assembly is copied to the output directory:
```bash
dotnet add package Microsoft.Extensions.Logging --version "10.0.4"
```

> **⚠️ Do NOT use a preview version** (e.g. `10.0.0-*` or `10.0.0-preview.*`). `Microsoft.Agents.A365.Observability.Hosting` already requires `>= 10.0.4` — specifying a lower preview causes a **NU1605 downgrade error** at restore time.

If the project targets `net8.0`, upgrade the TFM to `net9.0`:
```xml
<TargetFramework>net9.0</TargetFramework>
```

**Status:** This is expected to be resolved when `Microsoft.OpenTelemetry` ships a stable release.

### .NET CS0433 Type Ambiguity — Do NOT Add Hosting/Runtime as Direct References

When `Microsoft.OpenTelemetry` is referenced, adding `Microsoft.Agents.A365.Observability.Hosting` or `Microsoft.Agents.A365.Observability.Runtime` as **direct** `<PackageReference>` entries causes **CS0433 build errors** — the types `AgentDetails`, `CallerDetails`, and `IExporterTokenCache<T>` exist in both assemblies simultaneously.

**Cause:** `Microsoft.OpenTelemetry` re-exports all A365 observability types internally. The Hosting and Runtime packages are already brought in transitively.

**Fix:** Remove the direct `<PackageReference>` entries for `Hosting` and `Runtime` from the `.csproj`. Keep only `Microsoft.OpenTelemetry`, `Azure.Identity`, `Microsoft.Identity.Client`, and `Microsoft.Extensions.Logging`:
```xml
<!-- ✅ Correct — S2S path -->
<PackageReference Include="Microsoft.OpenTelemetry" Version="1.0.0-beta.1" />
<PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.4" />
<PackageReference Include="Azure.Identity" Version="..." />
<PackageReference Include="Microsoft.Identity.Client" Version="..." />

<!-- ❌ Do NOT add these on the S2S path — causes CS0433 -->
<!-- <PackageReference Include="Microsoft.Agents.A365.Observability.Hosting" ... /> -->
<!-- <PackageReference Include="Microsoft.Agents.A365.Observability.Runtime" ... /> -->
```

**Status:** SDK packaging issue — types should not be re-exported by the distro. Awaiting fix in a future release.

### .NET `InferenceCallDetails` Constructor — `providerName` Is Required

The `InferenceCallDetails` constructor signature is `(InferenceOperationType operationName, string model, string providerName, int? inputTokens, int? outputTokens, string[]? finishReasons, string? conversationId)`. The `providerName` parameter is **required** (not optional). Omitting it causes CS7036.

**Correct usage:**
```csharp
new InferenceCallDetails(
    operationName: InferenceOperationType.Chat,
    model: "gpt-5.4",
    providerName: "Azure OpenAI")
```

### .NET `ExecuteToolScope.RecordResponse` Takes `string`, Not `Response`

`ExecuteToolScope.RecordResponse()` accepts a `string` parameter (the tool result), not a `Response` object. Passing `new Response(...)` causes CS1503.

**Correct usage:**
```csharp
toolScope.RecordResponse(resultString);
```

### .NET `appsettings.json` — S2S Configuration Notes

For S2S / autonomous agents:
- `EnableAgent365Exporter` must be `true` in `appsettings.json` (not `false` — `a365 setup` may write `false` by default)
- `UseManagedIdentity` must be `false` for local development (MSI is only available on Azure infrastructure)
- Both `ClientId` and `ClientSecret` are required under `Agent365Observability` for the FMI 3-hop chain

### CallerDetails Required for MAC Portal Trace Visibility

For S2S / autonomous agents, `CallerDetails` with `UserDetails` (`userId`, `userName`, `userEmail`) must be passed to `InvokeAgentScope.Start()` / `.start()`. Without `CallerDetails`, exported spans reach the observability API (HTTP 200) but do **not** appear in the Microsoft Admin Center (MAC) portal's Advanced Hunting view.

**Node.js API differences:**
- `InvokeAgentScope.start()` takes `CallerDetails` (wraps `userDetails`) as 4th parameter
- `InferenceScope.start()` and `ExecuteToolScope.start()` take `UserDetails` directly as 4th parameter
- `OutputScope.start()` takes `UserDetails` directly as 4th parameter

**.NET API:**
- `InvokeAgentScope.Start()` takes `CallerDetails` (wraps `UserDetails`) as 4th parameter
- Other scopes do not take `CallerDetails` directly

**Recommendation:** For autonomous agents without a real user, use the Blueprint sponsor's identity:
- `UserId` = Blueprint App (Client) ID
- `UserName` = Blueprint display name
- `UserEmail` = Agent sponsor's email address

---

## References

- **.NET Patterns:** [dotnet-observability.md](./references/dotnet-observability.md)
- **Node.js Patterns:** [nodejs-observability.md](./references/nodejs-observability.md)
- **Python Patterns:** [python-observability.md](./references/python-observability.md)
