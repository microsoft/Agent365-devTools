# Add Agent 365 Observability

> **SCOPE — THIS FILE ONLY:** This file covers adding the Agent 365 observability SDK and tracing instrumentation to agent code. It does **NOT** cover provisioning, deployment, or CLI setup.
>
> This file is used in two ways: (1) automatically, as the final step of `a365-setup-instructions.md` when the selected capabilities include Observability; (2) directly, when the user explicitly asks to add observability, telemetry, logging, or tracing. If the user asked to start from scratch (e.g. "make my agent A365 compliant"), open `a365-setup-instructions.md` first.

Add Agent 365 observability to your agent at any point after `a365 setup all` has completed.

> **Implementation reference:** [Agent observability — Microsoft Learn](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/observability)
>
> **Prerequisite:** `a365 setup all` must have completed successfully so `AgentId` and `TenantId` values are present in `appsettings.json` (written by setup) or `a365.generated.config.json`. If neither source has these values, run `a365 setup all` first.

---

## Task A — Add the observability SDK to the project

Ask your coding agent (Claude Code, GitHub Copilot, or similar):

> "Using #file:a365-observability-instructions.md, add observability to this project"

> **Do NOT search NuGet package caches, inspect package XML files, browse MS Learn, or probe installed package versions. All required code is provided verbatim below — use it as-is.**

1. Install the observability SDK packages:
   - **.NET** — run these two commands in the project directory:
     ```
     dotnet add package Microsoft.Agents.A365.Observability.Runtime --prerelease
     dotnet add package Microsoft.Agents.A365.Observability.Hosting --prerelease
     ```
   - Python / Node.js: see the MS Learn reference for current package names

### .NET helper files — scaffold before step 2 (agentType = 3 only)

> **agentType = 3 only.** Skip this section for `agentType = 1` (Entra app ID agents) — those use the standard agentic token flow and do not need these files.
>
> These two files bridge gaps in the current SDK release and will be incorporated into `Microsoft.Agents.A365.Observability.Hosting` in a future version. Create them in an `Observability/` subfolder at the root of your project, replacing `<ProjectNamespace>` with the project's root namespace.

**`Observability/ObservabilityServiceExtensions.cs`** — registers the S2S token cache, background token service, and injectable `Agent365ObservabilityContext`:

```csharp
using Microsoft.Agents.A365.Observability.Hosting;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace <ProjectNamespace>;

// Injectable singleton wrapping AgentDetails for single-tenant agents.
// Pass ctx.AgentDetails to InvokeAgentScope.Start() for span attributes.
public sealed class Agent365ObservabilityContext
{
    public AgentDetails AgentDetails { get; }
    internal Agent365ObservabilityContext(AgentDetails d) => AgentDetails = d;
}

public static class ObservabilityServiceExtensions
{
    // Registers S2S token cache + exporter, ObservabilityTokenService, and Agent365ObservabilityContext.
    // Config is written by `a365 setup all` under the Agent365Observability section.
    public static IServiceCollection AddAgent365Observability(
        this IServiceCollection services,
        string? clusterCategory = "production")
    {
        services.AddServiceTracingExporter(clusterCategory);
        services.AddHostedService<ObservabilityTokenService>();
        services.AddSingleton<Agent365ObservabilityContext>(sp =>
        {
            var obs = sp.GetRequiredService<IConfiguration>().GetSection("Agent365Observability");
            var agentDetails = new AgentDetails(
                agentId:          obs["AgentId"],
                agentName:        obs["AgentName"],
                agentDescription: obs["AgentDescription"],
                agentBlueprintId: obs["AgentBlueprintId"],
                tenantId:         obs["TenantId"]
                    ?? throw new InvalidOperationException("Agent365Observability:TenantId is required."));
            return new Agent365ObservabilityContext(agentDetails);
        });
        return services;
    }
}
```

**`Observability/ObservabilityTokenService.cs`** — background service that acquires a Power Platform token via a 3-hop FMI chain and refreshes it every 50 minutes:

```csharp
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.A365.Observability.Hosting.Caching;
using Microsoft.Identity.Client;

namespace <ProjectNamespace>;

// Acquires a Power Platform token for A365 observability via a 3-hop FMI chain.
//   Hop 1+2: Blueprint authenticates (MSI in prod, client secret locally) →
//            gets T1 via .WithFmiPath(agentId) to Agent Identity.
//   Hop 3:   Agent Identity uses T1 as assertion → Power Platform token.
//            (ServiceIdentity type — AADSTS82001 does not apply.)
internal sealed class ObservabilityTokenService : BackgroundService
{
    private static readonly string[] FmiScopes = ["api://AzureADTokenExchange/.default"];
    private static readonly string[] PowerPlatformScopes = ["https://api.powerplatform.com/.default"];
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(50);

    private readonly IExporterTokenCache<string> _tokenCache;
    private readonly ILogger<ObservabilityTokenService> _logger;
    private readonly string _blueprintClientId, _blueprintClientSecret, _tenantId, _agentId;

    public ObservabilityTokenService(
        IExporterTokenCache<string> tokenCache,
        ILogger<ObservabilityTokenService> logger,
        IConfiguration configuration)
    {
        _tokenCache = tokenCache;
        _logger = logger;
        var obs = configuration.GetSection("Agent365Observability");
        _tenantId              = obs["TenantId"]     ?? throw new InvalidOperationException("Agent365Observability:TenantId is required.");
        _agentId               = obs["AgentId"]      ?? throw new InvalidOperationException("Agent365Observability:AgentId is required.");
        _blueprintClientId     = obs["ClientId"]     ?? throw new InvalidOperationException("Agent365Observability:ClientId is required.");
        // ClientSecret is required at construction time even in production:
        // MSI is tried first; the secret is only used as a local-dev fallback.
        // Ensure ClientSecret is present in all environments (can be a placeholder in prod if MSI is guaranteed).
        _blueprintClientSecret = obs["ClientSecret"] ?? throw new InvalidOperationException("Agent365Observability:ClientSecret is required.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ObservabilityTokenService started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await AcquireAndRegisterTokenAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            { _logger.LogWarning(ex, "Failed to acquire observability token; will retry in {Interval}.", RefreshInterval); }
            try { await Task.Delay(RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _logger.LogInformation("ObservabilityTokenService stopped.");
    }

    private async Task AcquireAndRegisterTokenAsync(CancellationToken ct)
    {
        string t1Token;
        string authority = $"https://login.microsoftonline.com/{_tenantId}";

        // Hop 1+2: Blueprint → T1 via FMI path (MSI in prod, client secret locally)
        try
        {
            // ManagedIdentityCredential.GetTokenAsync uses a resource URI (no /.default suffix).
            // FmiScopes uses /.default format — correct for MSAL AcquireTokenForClient.
            // These two forms are intentionally different; do not "fix" them to match.
            var assertion = await new ManagedIdentityCredential()
                .GetTokenAsync(new TokenRequestContext(["api://AzureADTokenExchange"]), ct);
            t1Token = (await ConfidentialClientApplicationBuilder
                .Create(_blueprintClientId)
                .WithClientAssertion((AssertionRequestOptions _) => Task.FromResult(assertion.Token))
                .WithAuthority(new Uri(authority)).Build()
                .AcquireTokenForClient(FmiScopes).WithFmiPath(_agentId)
                .ExecuteAsync(ct)).AccessToken;
        }
        catch (AuthenticationFailedException)
        {
            // MSI unavailable — fall back to client secret (local dev)
            t1Token = (await ConfidentialClientApplicationBuilder
                .Create(_blueprintClientId)
                .WithClientSecret(_blueprintClientSecret)
                .WithAuthority(new Uri(authority)).Build()
                .AcquireTokenForClient(FmiScopes).WithFmiPath(_agentId)
                .ExecuteAsync(ct)).AccessToken;
        }

        // Hop 3: Agent Identity uses T1 → Power Platform token
        var ppResult = await ConfidentialClientApplicationBuilder
            .Create(_agentId)
            .WithClientAssertion((AssertionRequestOptions _) => Task.FromResult(t1Token))
            .WithAuthority(new Uri(authority)).Build()
            .AcquireTokenForClient(PowerPlatformScopes)
            .ExecuteAsync(ct);

        _tokenCache.RegisterObservability(_agentId, _tenantId, ppResult.AccessToken, PowerPlatformScopes);
        _logger.LogInformation("Observability token registered for agent {AgentId}.", _agentId);
    }
}
```

2. **Register the exporter and tracing in startup code**
   - .NET (`agentType = 3`): call `builder.Services.AddAgent365Observability()` (using the extension from the helper files above), then `builder.AddA365Tracing()`
   - .NET (`agentType = 1`): call `builder.Services.AddAgenticTracingExporter()`, then `builder.AddA365Tracing()`
   - Python / Node.js: see the MS Learn reference
3. **Wire up the token resolver in your agent**
   - .NET (`agentType = 3`): inject `Agent365ObservabilityContext` into your agent class and any background services; pass `ctx.AgentDetails` directly to `InvokeAgentScope.Start()` — no `RegisterObservability` call needed
   - .NET (`agentType = 1`): call `_agentTokenCache.RegisterObservability(agentId, tenantId, new AgenticTokenStruct(...), EnvironmentUtils.GetObservabilityAuthenticationScope())` inside your turn handler
   - Python / Node.js: see the MS Learn reference
4. Add the exporter configuration setting (`EnableAgent365Exporter` / `ENABLE_A365_OBSERVABILITY_EXPORTER`) **enabled by default** in the main config, and **disabled in the development/local override** (e.g. `appsettings.Development.json` for .NET, `.env` for Node.js/Python) so that `dotnet run` / local dev stays console-only until the agent is reachable from the platform

   > **To verify exporter connectivity from Visual Studio:** temporarily set `"EnableAgent365Exporter": true` and add `"Microsoft.Agents.A365.Observability": "Debug"` and `"OpenTelemetry": "Debug"` to the `LogLevel` section of `appsettings.Development.json`, then revert when done.

> **REQUIRED — do not skip this step.**
> After completing steps 1–4 above, you **must** say to the user, verbatim:
>
> "---
> **Observability SDK is wired up.** Would you like me to scan your code and add instrumentation automatically? I'll find LLM calls, tool dispatches, agent-to-agent calls, and output operations and wrap each with the appropriate tracing scope.
>
> Reply **yes** to add instrumentation, or **no** to skip (you can add it later).
> ---"

- If **yes**: scan all agent source files, identify operations matching the scope types in Task B, present a summary of planned changes, confirm with the user, then apply — adding the correct scope wrapper and required usings to each. **Follow the hierarchy rule in Task B:** every instrumented block must have `InvokeAgentScope` as its outermost scope; `InferenceScope`, `ExecuteToolScope`, and `OutputScope` are child scopes that go inside it.
- If **no**: skip — instrumentation can be added later via Task B.

> **Note — recording response data:** Auto-instrumentation adds scope wrappers only. To attach the actual response text to a span, call the appropriate record method manually after you have the result:
> - `invokeAgentScope.RecordResponse(responseText)` — adds the agent's final reply to the `invoke_agent` span
> - `inferenceScope.RecordOutputMessages(...)` / `inferenceScope.RecordInputMessages(...)` — attaches LLM output/input messages to the `Chat` span
>
> These are one-liners and are best added by hand once you know which variable holds the response.

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
