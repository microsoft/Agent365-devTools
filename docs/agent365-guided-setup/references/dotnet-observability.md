# .NET AgentFramework — A365 Observability Reference

Authoritative package versions and code patterns for instrumenting A365 observability
into a .NET AgentFramework agent. All samples mirror the official Microsoft Learn docs
(updated 2026-04-30).

---

## NuGet Packages

| Package | Purpose |
|---------|---------|
| `Microsoft.Agents.A365.Observability.Runtime` | `AddA365Tracing()`, `BaggageBuilder`, `EnvironmentUtils` — required for all agents |
| `Microsoft.Agents.A365.Observability.Hosting` | `AddAgenticTracingExporter()` — OBO token caching (user-delegated / agentic-identity); `AddServiceTracingExporter()` — S2S token cache (`IExporterTokenCache<string>`) |
| `Microsoft.Agents.A365.Observability.Hosting.Caching` | `IExporterTokenCache<T>`, `AgenticTokenStruct` |
| `Microsoft.Agents.A365.Observability.Hosting.Extensions` | `FromTurnContext()` extension on `BaggageBuilder` |
| `Microsoft.Agents.A365.Observability.Hosting.Middleware` | `BaggageTurnMiddleware`, `UseObservabilityRequestContext` |
| `Microsoft.Agents.A365.Observability.Runtime.Common` | `BaggageBuilder`, `EnvironmentUtils` |
| `Microsoft.Agents.A365.Observability.Runtime.Tracing.Exporters` | `Agent365ExporterOptions`, `Agent365ExporterType` |
| `Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts` | `AgentDetails`, `InvokeAgentScopeDetails`, `ToolCallDetails`, `InferenceCallDetails`, `Request`, `Channel`, `UserDetails`, `CallerDetails`, `Response`, `SpanDetails` |
| `Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes` | `InvokeAgentScope`, `ExecuteToolScope`, `InferenceScope`, `OutputScope` |
| `Microsoft.Agents.A365.Observability.Extensions.SemanticKernel` | SK auto-instrumentation (optional) |
| `Microsoft.Agents.A365.Observability.Extensions.OpenAI` | OpenAI auto-instrumentation (optional) |
| `Microsoft.Agents.A365.Observability.Extensions.AgentFramework` | AgentFramework auto-instrumentation (optional) |

Unified Distro (preferred for S2S / autonomous agents):

| Package | Purpose |
|---------|---------|
| `Microsoft.OpenTelemetry` (v1.0.0-beta.1) | All-in-one: includes A365 observability types (`BaggageBuilder`, `InvokeAgentScope`, `InferenceScope`, `ExecuteToolScope`, `IExporterTokenCache`, `ServiceTokenCache`, `AgentDetails`, etc.) plus OTel pipeline configuration |
| `Azure.Identity` | `ManagedIdentityCredential` for MSI-based token acquisition |
| `Microsoft.Identity.Client` | MSAL `ConfidentialClientApplicationBuilder` with `.WithFmiPath()` for the FMI token chain |

Install commands:
```bash
# Preferred for S2S / autonomous agents (includes all observability types):
dotnet add package Microsoft.OpenTelemetry --version 1.0.0-beta.1
dotnet add package Azure.Identity
dotnet add package Microsoft.Identity.Client
# Required: Microsoft.OpenTelemetry v1.0.0-beta.1 has a hard runtime dependency on
# Microsoft.Extensions.Logging v10. On net9.0 the assembly is not in the framework
# so it must be a direct reference to ensure it is copied to the output directory.
# Use 10.0.4 (stable) — do NOT use a preview version; the Hosting package requires >= 10.0.4
# and specifying a lower preview causes NU1605 downgrade errors.
dotnet add package Microsoft.Extensions.Logging --version "10.0.4"
```

> **⚠️ Do NOT add `Microsoft.Agents.A365.Observability.Hosting` or `.Runtime` as direct
> `<PackageReference>` entries on the S2S path.** `Microsoft.OpenTelemetry` already brings
> both as transitive dependencies and re-exports their types. Adding them directly causes
> **CS0433 type ambiguity** (`AgentDetails`, `CallerDetails`, `IExporterTokenCache<T>` exist
> in both assemblies). Remove any explicit Hosting/Runtime references and let them flow
> transitively through `Microsoft.OpenTelemetry`.

Install commands (individual packages / OBO path):
```bash
# Required for all agents
dotnet add package Microsoft.Agents.A365.Observability.Runtime

# Required for OBO agents (authMode: user-delegated or agentic-identity)
dotnet add package Microsoft.Agents.A365.Observability.Hosting

# Optional auto-instrumentation extensions
dotnet add package Microsoft.Agents.A365.Observability.Extensions.SemanticKernel
dotnet add package Microsoft.Agents.A365.Observability.Extensions.OpenAI
dotnet add package Microsoft.Agents.A365.Observability.Extensions.AgentFramework
```

---

## Program.cs — S2S Path (`authMode: S2S`)

Use this pattern for Agent (Non AI Teammate) agents that run without a signed-in user (Autonomous / S2S).
Requires two scaffold files in `Observability/` — create these before wiring Program.cs.

> **⚠️ Known issues (v1.0.0-beta.1):**
> - **TFM / Logging v10:** `Microsoft.OpenTelemetry` v1.0.0-beta.1 has a hard **runtime** dependency on `Microsoft.Extensions.Logging` v10. On `net8.0` or `net9.0` this assembly is not part of the framework, causing `FileNotFoundException` at startup. Fix: upgrade TFM to `net9.0` and add `dotnet add package Microsoft.Extensions.Logging --version "10.0.4"`. Use the **stable** `10.0.4` release — specifying a preview version (e.g. `10.0.0-preview.*`) causes NU1605 downgrade errors because `Microsoft.Agents.A365.Observability.Hosting` already requires `>= 10.0.4`.
> - **CS0433 type ambiguity:** Do NOT add `Microsoft.Agents.A365.Observability.Hosting` or `Microsoft.Agents.A365.Observability.Runtime` as direct `<PackageReference>` entries alongside `Microsoft.OpenTelemetry`. The distro re-exports their types internally; adding them directly creates duplicate-type errors for `AgentDetails`, `CallerDetails`, and `IExporterTokenCache<T>`. Remove the direct references and let them flow transitively through `Microsoft.OpenTelemetry`.
> - **BaggageBuilder namespace:** `BaggageBuilder` requires `using Microsoft.Agents.A365.Observability.Runtime.Common;`. The `FromTurnContext()` extension additionally requires `using Microsoft.Agents.A365.Observability.Hosting.Extensions;`. Both usings are needed in the agent class.
> - **UseS2SEndpoint:** The distro does NOT set `UseS2SEndpoint = true` on the internal `Agent365Exporter`. You MUST set `o.Agent365.Exporter.UseS2SEndpoint = true` in the `UseMicrosoftOpenTelemetry` options callback, or the exporter posts to `/observability/` (OBO path) instead of `/observabilityService/` (S2S path), causing HTTP 401.
> - **InferenceCallDetails:** The `providerName` parameter is required (not optional). Constructor: `(InferenceOperationType operationName, string model, string providerName, ...)`.
> - **ExecuteToolScope.RecordResponse:** Takes `string`, not `Response` object.
> - **UseManagedIdentity:** Set `false` for local dev. MSI only works on Azure infrastructure.

### Scaffold: `Observability/ObservabilityServiceExtensions.cs`

```csharp
using Microsoft.Agents.A365.Observability.Hosting.Caching;
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
    // Registers S2S token cache, ObservabilityTokenService (if credentials are present),
    // and Agent365ObservabilityContext.
    // Config is written by `a365 setup all` under the Agent365Observability section.
    // When Agent365Observability credentials are missing, the agent still runs — spans are
    // emitted to the console exporter but not exported to the A365 service.
    public static IServiceCollection AddAgent365Observability(this IServiceCollection services)
    {
        services.AddSingleton<IExporterTokenCache<string>, ServiceTokenCache>();

        services.AddSingleton<Agent365ObservabilityContext>(sp =>
        {
            var obs = sp.GetRequiredService<IConfiguration>().GetSection("Agent365Observability");
            var agentDetails = new AgentDetails(
                agentId:          obs["AgentId"]          ?? "local-dev",
                agentName:        obs["AgentName"]        ?? "my-agent",
                agentDescription: obs["AgentDescription"] ?? "",
                agentBlueprintId: obs["AgentBlueprintId"] ?? "",
                tenantId:         obs["TenantId"]         ?? "local-dev");
            return new Agent365ObservabilityContext(agentDetails);
        });

        // Only start the background token service when the required credentials are configured.
        // Without these, the agent runs fine — observability spans go to the console exporter only.
        services.AddSingleton<ObservabilityTokenService>();
        services.AddHostedService(sp =>
        {
            var obs = sp.GetRequiredService<IConfiguration>().GetSection("Agent365Observability");
            var useManagedIdentity = !bool.TryParse(obs["UseManagedIdentity"], out var parsedUseManagedIdentity)
                || parsedUseManagedIdentity; // default true

            var hasCommonCredentials = !string.IsNullOrEmpty(obs["TenantId"])
                                    && !string.IsNullOrEmpty(obs["AgentId"])
                                    && !string.IsNullOrEmpty(obs["ClientId"])
                                    && !obs["TenantId"]!.StartsWith("<<");

            var hasClientSecret = !string.IsNullOrEmpty(obs["ClientSecret"])
                               && !obs["ClientSecret"]!.StartsWith("<<");

            var hasCredentials = hasCommonCredentials
                              && (useManagedIdentity || hasClientSecret);

            return new OptionalHostedService(
                hasCredentials ? sp.GetRequiredService<ObservabilityTokenService>() : null,
                sp.GetRequiredService<ILogger<ObservabilityTokenService>>(),
                hasCredentials ? null :
                    "Agent365Observability credentials not configured — skipping token service. " +
                    "Run 'a365 setup all' to enable A365 observability export.");
        });

        return services;
    }

    // Wrapper that conditionally starts a hosted service, allowing graceful skip.
    private sealed class OptionalHostedService(IHostedService? inner, ILogger logger, string? skipWarning = null) : IHostedService
    {
        public Task StartAsync(CancellationToken ct)
        {
            if (inner != null)
                return inner.StartAsync(ct);

            if (skipWarning != null)
                logger.LogWarning("{Warning}", skipWarning);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => inner?.StopAsync(ct) ?? Task.CompletedTask;
    }
}
```

### Scaffold: `Observability/ObservabilityTokenService.cs`

> **Important:** The recommended approach is the **3-hop FMI chain** using MSAL with `.WithFmiPath()`:
>
> ```
> Blueprint (client_credentials / MSI)
>   → Hop 1+2: FMI token (api://AzureADTokenExchange/.default with WithFmiPath(agentId))
>     → Agent Identity token
>       → Hop 3: Observability API token (scope=api://9b975845-388f-4429-889e-eab1ef63949c/.default)
> ```
>
> **Auth strategy** is controlled by `Agent365Observability:UseManagedIdentity`:
>   - `true` (production) — MSI → Blueprint FIC → Agent Identity → API
>   - `false` (local dev) — Client Secret → Blueprint FIC → Agent Identity → API
>
> **Note:** As of CLI 1.1, `a365 setup all` automatically grants `Agent365.Observability.OtelWrite` to the Agent Identity SP (both delegated and application). No manual role assignment is needed for newly provisioned agents.

```csharp
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.A365.Observability.Hosting.Caching;
using Microsoft.Identity.Client;

namespace <ProjectNamespace>;

// Acquires an Observability API token for A365 observability via a 3-hop FMI chain.
//   Hop 1+2: Blueprint authenticates (MSI in prod, client secret locally) →
//            gets T1 via .WithFmiPath(agentId) to Agent Identity.
//   Hop 3:   Agent Identity uses T1 as assertion → Observability API token.
//            (ServiceIdentity type — AADSTS82001 does not apply.)
//
// Auth strategy is controlled by Agent365Observability:UseManagedIdentity:
//   true  (production)  — MSI → Blueprint FIC → Agent Identity → API
//   false (local dev)   — Client Secret → Blueprint FIC → Agent Identity → API
internal sealed class ObservabilityTokenService : BackgroundService
{
    private static readonly string[] FmiScopes = ["api://AzureADTokenExchange/.default"];
    private static readonly string[] ObservabilityScopes = ["api://9b975845-388f-4429-889e-eab1ef63949c/.default"];
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(50);

    private readonly IExporterTokenCache<string> _tokenCache;
    private readonly ILogger<ObservabilityTokenService> _logger;
    private readonly string _blueprintClientId, _blueprintClientSecret, _tenantId, _agentId;
    private readonly bool _useManagedIdentity;

    public ObservabilityTokenService(
        IExporterTokenCache<string> tokenCache,
        ILogger<ObservabilityTokenService> logger,
        IConfiguration configuration)
    {
        _tokenCache = tokenCache;
        _logger = logger;
        var obs = configuration.GetSection("Agent365Observability");
        _tenantId              = obs["TenantId"]     ?? "";
        _agentId               = obs["AgentId"]      ?? "";
        _blueprintClientId     = obs["ClientId"]     ?? "";
        _blueprintClientSecret = obs["ClientSecret"] ?? "";
        _useManagedIdentity    = obs.GetValue<bool>("UseManagedIdentity", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ObservabilityTokenService started (UseManagedIdentity={UseMsi}).", _useManagedIdentity);
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
        string authority = $"https://login.microsoftonline.com/{_tenantId}";

        // Hop 1+2: Blueprint → T1 via FMI path
        // When UseManagedIdentity is true, try MSI first and fall back to client secret
        // on AuthenticationFailedException (e.g. when running locally without MSI).
        string t1Token;
        if (_useManagedIdentity)
        {
            try
            {
                t1Token = await AcquireT1ViaMsiAsync(authority, ct);
            }
            catch (AuthenticationFailedException ex)
            {
                _logger.LogWarning(ex, "MSI authentication failed; falling back to client secret.");
                t1Token = await AcquireT1ViaClientSecretAsync(authority, ct);
            }
        }
        else
        {
            t1Token = await AcquireT1ViaClientSecretAsync(authority, ct);
        }

        // Hop 3: Agent Identity uses T1 → Observability API token
        var obsResult = await ConfidentialClientApplicationBuilder
            .Create(_agentId)
            .WithClientAssertion((AssertionRequestOptions _) => Task.FromResult(t1Token))
            .WithAuthority(new Uri(authority)).Build()
            .AcquireTokenForClient(ObservabilityScopes)
            .ExecuteAsync(ct);

        _tokenCache.RegisterObservability(_agentId, _tenantId, obsResult.AccessToken, ObservabilityScopes);
        _logger.LogInformation("Observability token registered for agent {AgentId}.", _agentId);
    }

    private async Task<string> AcquireT1ViaMsiAsync(string authority, CancellationToken ct)
    {
        var assertion = await new ManagedIdentityCredential()
            .GetTokenAsync(new TokenRequestContext(["api://AzureADTokenExchange"]), ct);
        return (await ConfidentialClientApplicationBuilder
            .Create(_blueprintClientId)
            .WithClientAssertion((AssertionRequestOptions _) => Task.FromResult(assertion.Token))
            .WithAuthority(new Uri(authority)).Build()
            .AcquireTokenForClient(FmiScopes).WithFmiPath(_agentId)
            .ExecuteAsync(ct)).AccessToken;
    }

    private async Task<string> AcquireT1ViaClientSecretAsync(string authority, CancellationToken ct)
    {
        return (await ConfidentialClientApplicationBuilder
            .Create(_blueprintClientId)
            .WithClientSecret(_blueprintClientSecret)
            .WithAuthority(new Uri(authority)).Build()
            .AcquireTokenForClient(FmiScopes).WithFmiPath(_agentId)
            .ExecuteAsync(ct)).AccessToken;
    }
}
```

### Program.cs wiring

```csharp
using Microsoft.Agents.A365.Observability.Hosting.Caching;
using Microsoft.OpenTelemetry;

var builder = WebApplication.CreateBuilder(args);

// A365 Observability — S2S token cache + background token service + AgentDetails context.
// ObservabilityTokenService acquires tokens via a 3-hop FMI chain (Blueprint → Agent Identity → API)
// and registers them with the ServiceTokenCache every 50 minutes.
builder.Services.AddAgent365Observability();

// Microsoft OpenTelemetry distro — configures OTel tracing pipeline + A365 exporter.
// The token resolver reads from the ServiceTokenCache populated by ObservabilityTokenService.
// Note: tokenCache is resolved lazily after Build() via the closure over the local variable.
IExporterTokenCache<string>? tokenCache = null;
builder.UseMicrosoftOpenTelemetry(o =>
{
    o.Exporters = builder.Environment.IsDevelopment()
        ? ExportTarget.Agent365 | ExportTarget.Console
        : ExportTarget.Agent365;

    // ⚠️ Required for S2S: distro does NOT set this automatically in v1.0.0-beta.1
    o.Agent365.Exporter.UseS2SEndpoint = true;

    o.Agent365.Exporter.TokenResolver = async (agentId, tenantId) =>
    {
        return tokenCache != null
            ? await tokenCache.GetObservabilityToken(agentId, tenantId)
            : null;
    };
});

// ... rest of service configuration ...

var app = builder.Build();
tokenCache = app.Services.GetService<IExporterTokenCache<string>>();

// ... rest of app configuration ...
```

---

## Program.cs — Hosting Path (AI Teammate, auto token caching)

Use this pattern when the agent uses the AI Teammate hosting framework.

```csharp
using Microsoft.Agents.A365.Observability.Runtime;
using Microsoft.Agents.A365.Observability.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Registers IExporterTokenCache<AgenticTokenStruct> in DI — handles token caching automatically.
builder.Services.AddAgenticTracingExporter();

// Registers the OTel TracerProvider with the A365 exporter.
builder.AddA365Tracing();

var app = builder.Build();

// Optional: register HTTP-level baggage middleware (before the Bot Framework pipeline)
// app.UseObservabilityRequestContext((httpContext) =>
// {
//     var tenantId = GetTenantIdFromContext(httpContext);
//     var agentId = GetAgentIdFromContext(httpContext);
//     return (tenantId, agentId);
// });
```

---

## Adapter — BaggageTurnMiddleware

Register `BaggageTurnMiddleware` to auto-populate baggage from every incoming `ITurnContext`.
This removes the need to call `BaggageBuilder` manually in each activity handler.

```csharp
using Microsoft.Agents.A365.Observability.Hosting.Middleware;

adapter.Use(new BaggageTurnMiddleware());
// The middleware skips async replies (ContinueConversation) to avoid overwriting baggage.
```

For HTTP-level baggage (before the Bot Framework pipeline), register via `UseObservabilityRequestContext`:

```csharp
using Microsoft.Agents.A365.Observability.Hosting.Middleware;

app.UseObservabilityRequestContext((httpContext) =>
{
    var tenantId = GetTenantIdFromContext(httpContext);
    var agentId = GetAgentIdFromContext(httpContext);
    return (tenantId, agentId);
});
```

---

## Agent Class — Message Handler (OBO Path, `authMode: user-delegated` or `agentic-identity`)

```csharp
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App.UserAuth;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.Observability.Hosting.Caching;
using Microsoft.Agents.A365.Observability.Runtime.Common;
using System;
using System.Threading;
using System.Threading.Tasks;

public class MyAgent : AgentApplication
{
    private readonly IExporterTokenCache<AgenticTokenStruct> _agentTokenCache;
    private readonly ILogger<MyAgent> _logger;

    public MyAgent(
        AgentApplicationOptions options,
        IExporterTokenCache<AgenticTokenStruct> agentTokenCache,
        ILogger<MyAgent> logger) : base(options)
    {
        _agentTokenCache = agentTokenCache ?? throw new ArgumentNullException(nameof(agentTokenCache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected async Task MessageActivityAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        CancellationToken cancellationToken)
    {
        // Option A: Manual BaggageBuilder (use if BaggageTurnMiddleware is NOT registered)
        // Build() returns IDisposable — use `using var` to scope the baggage context.
        using var baggageScope = new BaggageBuilder()
            .TenantId(turnContext.Activity.Recipient.TenantId)
            .AgentId(turnContext.Activity.Recipient.AgenticAppId)
            .Build();

        // Option B: FromTurnContext helper (preferred — auto-populates from activity)
        // Requires: using Microsoft.Agents.A365.Observability.Hosting.Extensions;
        // using var baggageScope = new BaggageBuilder()
        //     .FromTurnContext(turnContext)
        //     .Build();

        // Register the agentic token so the exporter can authenticate exports.
        try
        {
            _agentTokenCache.RegisterObservability(
                turnContext.Activity.Recipient.AgenticAppId,
                turnContext.Activity.Recipient.TenantId,
                new AgenticTokenStruct(
                    userAuthorization: UserAuthorization,
                    turnContext: turnContext,
                    authHandlerName: "AGENTIC"
                ),
                EnvironmentUtils.GetObservabilityAuthenticationScope()
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error registering for observability.");
        }

        // ... existing agent message handling logic ...
    }
}
```

---

## Agent Class — Message Handler (S2S Path, `authMode: S2S`)

Inject `Agent365ObservabilityContext` instead of `IExporterTokenCache<AgenticTokenStruct>`.
`ObservabilityTokenService` holds the token in the background — no per-turn `RegisterObservability` call.

```csharp
using Microsoft.Agents.Builder;
using Microsoft.Agents.A365.Observability.Hosting.Extensions;
using Microsoft.Agents.A365.Observability.Runtime.Common;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes;

public class MyAgent : AgentApplication
{
    // CallerDetails is read from Agent365Observability:Sponsor config — injected via
    // Agent365ObservabilityContext singleton (see ObservabilityServiceExtensions).
    // For autonomous agents, use the Blueprint sponsor's identity.
    private readonly Agent365ObservabilityContext _obs;

    public MyAgent(AgentApplicationOptions options, Agent365ObservabilityContext obs)
        : base(options)
    {
        _obs = obs;
    }

    protected async Task MessageActivityAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        CancellationToken cancellationToken)
    {
        // No RegisterObservability() call — ObservabilityTokenService holds the token.
        // IMPORTANT: FromTurnContext() is an extension on BaggageBuilder only — it does NOT
        // exist on InvokeAgentScope. InvokeAgentScopeDetails has no parameterless constructor;
        // pass at least `endpoint`. Keep baggage and scope as two separate using statements.
        // authMode: S2S

        // Step 1: propagate baggage from the incoming turn.
        // Requires: using Microsoft.Agents.A365.Observability.Hosting.Extensions;
        using var baggageScope = new BaggageBuilder()
            .FromTurnContext(turnContext)
            .Build();

        // Step 2: start the invoke scope with CallerDetails (required for traces to show up).
        using var scope = InvokeAgentScope.Start(
            new Request(turnContext.Activity.Text),
            new InvokeAgentScopeDetails(endpoint: new Uri("https://your-agent-endpoint")),
            _obs.AgentDetails,
            _obs.CallerDetails);

        // ... existing agent message handling logic ...
    }
}
```

```csharp
// ObservabilityServiceExtensions.cs — DI registration with dynamic CallerDetails from config
public sealed class Agent365ObservabilityContext
{
    public AgentDetails AgentDetails { get; }
    public CallerDetails CallerDetails { get; }
    internal Agent365ObservabilityContext(AgentDetails d, CallerDetails c)
    {
        AgentDetails = d;
        CallerDetails = c;
    }
}

public static class ObservabilityServiceExtensions
{
    public static IServiceCollection AddAgent365Observability(this IServiceCollection services)
    {
        services.AddSingleton<Agent365ObservabilityContext>(sp =>
        {
            var obs = sp.GetRequiredService<IConfiguration>().GetSection("Agent365Observability");
            var agentDetails = new AgentDetails(
                agentId:          obs["AgentId"]          ?? "local-dev",
                agentName:        obs["AgentName"]        ?? "unknown",
                agentDescription: obs["AgentDescription"] ?? "",
                agentBlueprintId: obs["AgentBlueprintId"] ?? "",
                tenantId:         obs["TenantId"]         ?? "local-dev");

            // Read sponsor/caller details from config — enables trace visibility in MAC portal
            var sponsor = obs.GetSection("Sponsor");
            var callerDetails = new CallerDetails(
                userDetails: new UserDetails(
                    userId:    sponsor["UserId"]    ?? obs["ClientId"] ?? "unknown",
                    userName:  sponsor["UserName"]  ?? obs["AgentName"] ?? "Blueprint Sponsor",
                    userEmail: sponsor["UserEmail"] ?? ""));

            return new Agent365ObservabilityContext(agentDetails, callerDetails);
        });
        // ... rest of DI registration
        return services;
    }
}
```

---

## Manual Instrumentation Scopes

> **Store publishing requirement:** `InvokeAgentScope`, `InferenceScope`, and `ExecuteToolScope`
> are **required** for store validation. Missing any one causes store validation failure.

### InvokeAgentScope

```csharp
using System;
using System.Threading.Tasks;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes;

var agentDetails = new AgentDetails(
    agentId: "agent-456",
    agentName: "MyAgent",
    agentDescription: "Handles user requests.",
    agenticUserId: "auid-123",
    agenticUserEmail: "agent@contoso.com",
    agentBlueprintId: "blueprint-789",
    tenantId: "tenant-123"
);

var scopeDetails = new InvokeAgentScopeDetails(
    endpoint: new Uri("https://myagent.contoso.com")
);

var request = new Request(
    content: userInput,
    sessionId: "session-abc",
    channel: new Channel("msteams"),
    conversationId: "conv-xyz"
);

var callerDetails = new CallerDetails(
    userDetails: new UserDetails(
        userId: "user-123",
        userEmail: "jane.doe@contoso.com",
        userName: "Jane Doe"
    )
);

// Start the scope — dispose automatically ends the span
using var scope = InvokeAgentScope.Start(
    request: request,
    scopeDetails: scopeDetails,
    agentDetails: agentDetails,
    callerDetails: callerDetails
);

scope.RecordInputMessages(new[] { userInput });

// ... your agent logic here ...

scope.RecordOutputMessages(new[] { output });
```

### ExecuteToolScope

```csharp
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes;

// Use the same agentDetails and request instances from InvokeAgentScope above
var userDetails = new UserDetails(
    userId: "user-123",
    userEmail: "jane.doe@contoso.com",
    userName: "Jane Doe"
);

var toolCallDetails = new ToolCallDetails(
    toolName: "summarize",
    arguments: "{\"text\": \"...\"}",
    toolCallId: "tc-001",
    description: "Summarize provided text",
    toolType: "function",
    endpoint: new Uri("https://tools.contoso.com:8080")
);

using var scope = ExecuteToolScope.Start(
    request: request,
    details: toolCallDetails,
    agentDetails: agentDetails,
    userDetails: userDetails
);

// ... your tool logic here ...

scope.RecordResponse("{\"summary\": \"The text was summarized.\"}");
```

### InferenceScope

```csharp
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes;

// Use the same agentDetails and request instances from InvokeAgentScope above
var userDetails = new UserDetails(
    userId: "user-123",
    userEmail: "jane.doe@contoso.com",
    userName: "Jane Doe"
);

var inferenceDetails = new InferenceCallDetails(
    operationName: InferenceOperationType.Chat,
    model: "gpt-4o-mini",
    providerName: "Azure OpenAI",
    inputTokens: 123,
    outputTokens: 456,
    finishReasons: new[] { "stop" }
);

using var scope = InferenceScope.Start(
    request: request,
    details: inferenceDetails,
    agentDetails: agentDetails,
    userDetails: userDetails
);

// ... your inference logic here ...

scope.RecordOutputMessages(new[] { "AI response message" });
scope.RecordInputTokens(123);
scope.RecordOutputTokens(456);
```

### OutputScope (async scenarios)

```csharp
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes;

// Use the same agentDetails and request instances from InvokeAgentScope above

// Get the parent context from the originating scope
var parentContext = invokeScope.GetActivityContext();

var response = new Response(new[] { "Here is your organized inbox with 15 urgent emails." });

using var scope = OutputScope.Start(
    request: request,
    response: response,
    agentDetails: agentDetails,
    spanDetails: new SpanDetails(parentContext: parentContext)
);
// Output messages are recorded automatically from the response
```

---

## appsettings.json — Complete Pattern

> **Note:** If you ran `a365 setup`, the following values are **already present** in your
> `appsettings.json`: `EnableAgent365Exporter: false`, `Agent365Observability.AgentBlueprintId`,
> and `Agent365Observability.TenantId`. Preserve these existing values when instrumenting.

**OBO path (`authMode: user-delegated` or `agentic-identity`):**

```json
{
  "EnableAgent365Exporter": true,
  "Agent365Observability": {
    "AgentBlueprintId": "your-blueprint-id",
    "TenantId": "your-tenant-id",
    "AgentName": "My Agent",
    "AgentDescription": "Description of what this agent does"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Agents.A365.Observability": "Information",
      "OpenTelemetry": "Warning"
    }
  }
}
```

**S2S path (`authMode: S2S`):**

```json
{
  "Agent365Observability": {
    "AgentBlueprintId": "<<BLUEPRINT_APP_ID>>",
    "TenantId": "<<TENANT_ID>>",
    "AgentName": "<<AGENT_NAME>>",
    "AgentDescription": "<<AGENT_DESCRIPTION>>",
    "AgentId": "<<AGENT_IDENTITY_ID>>",
    "ClientId": "<<BLUEPRINT_APP_ID>>",
    "ClientSecret": "<<BLUEPRINT_CLIENT_SECRET>>",
    "UseManagedIdentity": true,
    "Sponsor": {
      "UserId": "<<BLUEPRINT_APP_ID>>",
      "UserName": "<<BLUEPRINT_NAME>>",
      "UserEmail": "<<BLUEPRINT_SPONSOR_EMAIL>>"
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Agents": "Warning",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  }
}
```

> **S2S auth note:** `UseManagedIdentity` defaults to `true`. In production (Azure), the service uses Managed Identity and the `ClientSecret` is only needed as a local-dev fallback. Set to `false` in `appsettings.Development.json` if you always want client-secret auth locally.
>
> **Sponsor note:** For S2S / autonomous agents, the `Sponsor` section provides the `CallerDetails` required for MAC portal trace visibility. Use the Blueprint app ID as `UserId`, the Blueprint display name as `UserName`, and the agent sponsor's email as `UserEmail`.

> **Critical:** The `Logging.LogLevel` section is **required** for observability events to be
> captured in console output and forwarded to Microsoft Defender. Without this, the SDK is
> instrumented but logs are suppressed. The `a365 setup` command does **not** add logging
> configuration — you must add it manually or via this instrumentation skill.

> **Local dev convention:** Set `EnableAgent365Exporter: false` in `appsettings.Development.json`
> to keep local runs console-only. The main `appsettings.json` should have it **enabled** so
> deployed environments export by default without requiring an env override.

## appsettings.Development.json

```json
{
  "EnableAgent365Exporter": false,
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Agents.A365.Observability": "Debug",
      "OpenTelemetry": "Debug"
    }
  }
}
```

## Validate Locally

Set `EnableAgent365Exporter` to `false` in `appsettings.Development.json` — spans export to the console.

To investigate export failures, enable verbose logging:

```json
{
  "EnableAgent365Exporter": true,
  "Logging": {
    "LogLevel": {
      "Microsoft.Agents.A365.Observability": "Debug"
    }
  }
}
```

Or set environment variables:

```bash
EnableAgent365Exporter=True
A365_OBSERVABILITY_DOMAIN_OVERRIDE=https://your-test-endpoint.example.com
# For S2S exports, override to the Observability API scope used by FMI Hop 3.
A365_OBSERVABILITY_SCOPE_OVERRIDE=api://9b975845-388f-4429-889e-eab1ef63949c/.default
```

Key log messages:

```text
info: Agent365ExporterCore: Obtained token for agent {agentId} tenant {tenantId}.
info: Agent365ExporterCore: Sending {count} spans to {requestUri} for agent {agentId} tenant {tenantId}.
info: Agent365ExporterCore: HTTP {statusCode} exporting spans. 'x-ms-correlation-id': '{correlationId}'.
error: Agent365Exporter: Exception exporting spans: {exception}
warn: Agent365ExporterCore: No token obtained for agent {agentId} tenant {tenantId}. Skipping export.
```

> If you don't register an `ILoggerFactory` in DI, the exporter automatically falls back to a console logger.

---

## Key Types Reference

| Type | Namespace | Purpose |
|------|-----------|---------|
| `BaggageBuilder` | `Microsoft.Agents.A365.Observability.Runtime.Common` | Propagates context across spans; `Build()` returns `IDisposable` — use `using var` |
| `EnvironmentUtils` | `Microsoft.Agents.A365.Observability.Runtime.Common` | `GetObservabilityAuthenticationScope()` helper |
| `IExporterTokenCache<T>` | `Microsoft.Agents.A365.Observability.Hosting.Caching` | DI interface for caching and retrieving agentic tokens |
| `ServiceTokenCache` | `Microsoft.Agents.A365.Observability.Hosting.Caching` | S2S implementation of `IExporterTokenCache<string>` |
| `AgenticTokenStruct` | `Microsoft.Agents.A365.Observability.Hosting.Caching` | Wraps `TurnContext` + `UserAuthorization` + `AuthHandlerName` for token resolution. Uses **constructor** syntax: `new AgenticTokenStruct(userAuthorization: ..., turnContext: ..., authHandlerName: "AGENTIC")` |
| `Agent365ExporterOptions` | `Microsoft.Agents.A365.Observability.Runtime.Tracing.Exporters` | Exporter config (`TokenResolver`, `MaxQueueSize`, `ScheduledDelayMilliseconds`, etc.) |
| `Agent365ExporterType` | `Microsoft.Agents.A365.Observability.Runtime.Tracing.Exporters` | Enum for `AddA365Tracing()` exporter type param |
| `AddAgenticTracingExporter()` | `Microsoft.Agents.A365.Observability.Hosting` | DI extension for OBO token caching (`IExporterTokenCache<AgenticTokenStruct>`) — user-delegated / agentic-identity |
| `AddServiceTracingExporter()` | `Microsoft.Agents.A365.Observability.Hosting` | Legacy/manual DI extension for S2S token cache (`IExporterTokenCache<string>`) when not using the unified distro |
| `Agent365ObservabilityContext` | Scaffold (`Observability/`) | Singleton wrapping `AgentDetails` for S2S agents — inject instead of per-turn `RegisterObservability` |
| `ObservabilityTokenService` | Scaffold (`Observability/`) | `BackgroundService` — acquires the export token via the FMI 3-hop chain (`.WithFmiPath()` + agent assertion); refreshes every 50 min |
| `AddAgent365Observability()` | Scaffold (`Observability/`) | Registers `ServiceTokenCache`, `ObservabilityTokenService` (conditional), and `Agent365ObservabilityContext` |
| `UseMicrosoftOpenTelemetry()` | `Microsoft.OpenTelemetry` | Configures OTel pipeline with A365 exporter (preferred for S2S) |
| `ExportTarget` | `Microsoft.OpenTelemetry` | Enum: `Agent365`, `Console`, `AzureMonitor` |
| `AddA365Tracing()` | `Microsoft.Agents.A365.Observability.Runtime` | Registers OTel TracerProvider with A365 exporter |
| `BaggageTurnMiddleware` | `Microsoft.Agents.A365.Observability.Hosting.Middleware` | Adapter middleware — auto-populates baggage from every `ITurnContext` |
| `FromTurnContext()` | `Microsoft.Agents.A365.Observability.Hosting.Extensions` | Extension on **`BaggageBuilder` only** — auto-populates from activity. Does NOT exist on `InvokeAgentScope` or any scope type. |
| `InvokeAgentScope` | `Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes` | Required for store publishing — wrap top-level message handler |
| `ExecuteToolScope` | `Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes` | Required for store publishing — wrap each tool call |
| `InferenceScope` | `Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes` | Required for store publishing — wrap each LLM call |
| `OutputScope` | `Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes` | For async scenarios where parent scope can't capture output synchronously |
| `AgentDetails` | `Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts` | Agent identity for scope telemetry |
| `InvokeAgentScopeDetails` | `Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts` | Endpoint details for `InvokeAgentScope` |
| `ToolCallDetails` | `Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts` | Tool info for `ExecuteToolScope` |
| `InferenceCallDetails` | `Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts` | Model/token info for `InferenceScope` |
| `CallerDetails` / `UserDetails` | `Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts` | Caller identity |

---

## Agent365ExporterOptions Properties

| Property | Description | Default |
|----------|-------------|---------|
| `UseS2SEndpoint` | Use service-to-service endpoint path | `false` |
| `MaxQueueSize` | Max queue size for batch processor | `2048` |
| `ScheduledDelayMilliseconds` | Delay between export batches | `5000` |
| `ExporterTimeoutMilliseconds` | Timeout for export operation | `30000` |
| `MaxExportBatchSize` | Max batch size | `512` |

---

## Configuration Sources

The `a365 setup` command (as of April 2026) automatically writes the following to `appsettings.json`:

```json
{
  "EnableAgent365Exporter": false,
  "Agent365Observability": {
    "AgentBlueprintId": "<from-setup>",
    "TenantId": "<from-setup>",
    "AgentName": "",
    "AgentDescription": ""
  }
}
```

**What `a365 setup` does NOT add:**
- `Logging.LogLevel` configuration (required for Defender visibility)
- `Agent365Observability:Sponsor` values for `CallerDetails` (required for S2S / autonomous agent trace visibility in MAC portal)

**When instrumenting observability:**
1. Preserve existing `EnableAgent365Exporter`, `AgentBlueprintId`, `TenantId` values
2. Add `Logging.LogLevel` section if missing
3. Populate `AgentName` and `AgentDescription` if empty

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| No traces in console | OTel not wired | Call `builder.UseMicrosoftOpenTelemetry()` (or `builder.AddA365Tracing()` for OBO path) |
| No logs in Defender | Missing `Logging.LogLevel` config | Add `Microsoft.Agents.A365.Observability: Debug` to appsettings.json |
| `AgenticAppId` is null | Missing `AGENTIC_APP_ID` env var | Set it in `.env` or App Service config |
| Token resolver returns null | `AddAgenticTracingExporter()` not called | Add to `Program.cs` DI |
| 401 from A365 exporter | OAuth consent not granted | Run `a365 setup permissions observability`; also check if upgrading past `0.3-beta` (requires new `Agent365.Observability.OtelWrite` permission) |
| Build error on `BaggageBuilder` | Wrong namespace | Use `Microsoft.Agents.A365.Observability.Runtime.Common` |
| Build error on `AgenticTokenStruct` | Object initializer syntax used | Use constructor: `new AgenticTokenStruct(userAuthorization: ..., turnContext: ..., authHandlerName: "AGENTIC")` |
| Build error on `IExporterTokenCache` | Wrong namespace | Use `Microsoft.Agents.A365.Observability.Hosting.Caching` |
| Build error on `AddAgenticTracingExporter` | Wrong namespace | Use `Microsoft.Agents.A365.Observability.Hosting` |
| Build error on `AddA365Tracing` | Wrong namespace | Use `Microsoft.Agents.A365.Observability.Runtime` |
| Spans dropped silently | Missing tenant/agent ID in baggage | Ensure `BaggageBuilder` is set up before creating spans, or register `BaggageTurnMiddleware` |
| S2S: token service skipped at startup | Placeholder or missing `Agent365Observability` credentials | Run `a365 setup all` or populate `TenantId`, `AgentId`, `ClientId`, and `ClientSecret` (when `UseManagedIdentity` is `false`) |
| S2S: 401 on export | Token acquired for wrong scope or app | Verify FMI Hop 3 scope is `api://9b975845-388f-4429-889e-eab1ef63949c/.default`. For agents provisioned before CLI 1.1, verify Agent Identity SP has `Agent365.Observability.OtelWrite` app role via Entra portal |
| S2S: FMI Hop 1+2 fails | Blueprint credentials wrong or `.WithFmiPath(agentId)` target incorrect | Check `ClientId` (Blueprint app ID) and `ClientSecret` in appsettings; verify `AgentId` matches the Agent Identity app ID |
| S2S: FMI Hop 3 → 401 on export | Wrong scope or missing role | FMI Hop 3 scope is `api://9b975845-388f-4429-889e-eab1ef63949c/.default`; Agent Identity SP needs `OtelWrite` role assigned via Graph API |
| S2S: MSI fails locally | No Managed Identity available in dev | Set `UseManagedIdentity: false` in appsettings.Development.json, ensure `ClientSecret` is populated |
| S2S: `UseMicrosoftOpenTelemetry` not found | Unified distro not installed | Run `dotnet add package Microsoft.OpenTelemetry --version 1.0.0-beta.1` |
| S2S: Runtime `FileNotFoundException` for `Microsoft.Extensions.Logging v10.0.0` | `Microsoft.OpenTelemetry` v1.0.0-beta.1 depends on v10 logging | (1) Upgrade TFM to `net9.0`. (2) Run `dotnet add package Microsoft.Extensions.Logging --version "10.0.4"` — use the **stable** version, not a preview; specifying a preview causes NU1605 downgrade errors because `Microsoft.Agents.A365.Observability.Hosting` requires `>= 10.0.4`. |
| S2S: CS0433 type ambiguity on `AgentDetails` / `CallerDetails` / `IExporterTokenCache` | `Microsoft.Agents.A365.Observability.Hosting` and/or `.Runtime` added as direct references alongside `Microsoft.OpenTelemetry` | Remove the direct `<PackageReference>` entries for `Hosting` and `Runtime` from the `.csproj`. `Microsoft.OpenTelemetry` already brings both transitively and re-exports their types — direct references create duplicate symbols. |
| S2S: HTTP 401 on span export (correct token) | `UseS2SEndpoint` not set — exporter posts to `/observability/` instead of `/observabilityService/` | Set `o.Agent365.Exporter.UseS2SEndpoint = true` in `UseMicrosoftOpenTelemetry` options |
| S2S: CS7036 on `InferenceCallDetails` — missing `providerName` | `providerName` is required (not optional) | Use: `new InferenceCallDetails(operationName: ..., model: ..., providerName: "Azure OpenAI")` |
| S2S: CS1503 on `ExecuteToolScope.RecordResponse` | Method takes `string`, not `Response` | Use: `toolScope.RecordResponse(resultString)` |
| S2S: `InvokeAgentScopeDetails` constructor error | No parameterless constructor exists | Pass at least `endpoint`: `new InvokeAgentScopeDetails(endpoint: new Uri("..."))` |
| S2S: `InvokeAgentScope` has no `FromTurnContext` | `FromTurnContext` is a `BaggageBuilder` extension only | Create `BaggageBuilder` separately: `new BaggageBuilder().FromTurnContext(tc).Build()` |
| Build error: `Azure.AI.OpenAI` version conflict with `Extensions.OpenAI` | Package requires `Azure.AI.OpenAI >= 2.7.0-beta.2` | Run `dotnet add package Azure.AI.OpenAI --version 2.7.0-beta.2` before adding the extension |
