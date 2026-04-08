---
agent: agent
description: Add Application Insights observability to an agent project
tools:
  - runCommands
  - terminalLastCommand
  - editFiles
  - codebase
---

# Add Observability Skill

Adds Agent 365 observability (S2S token exporter + OpenTelemetry tracing) to a non-DW autonomous agent project.

> **Note — .NET is different from Python/Node.js:**
> The Agent 365 observability SDK packages for .NET are not yet published to NuGet.
> Until then, .NET projects use two local staging files (`Observability/ObservabilityServiceExtensions.cs`
> and `Observability/ObservabilityTokenService.cs`) plus direct project references to the SDK source.
> This is a temporary workaround — it will be replaced by a single NuGet package reference.

## Usage

```bash
/add-observability              # Auto-detect project type in current directory
/add-observability --status     # Check current observability setup without making changes
```

## What this skill does

1. **Detects project type** from files in the current directory (`requirements.txt`, `package.json`, `*.csproj`)
2. **Checks current state** — reports if observability is already configured, partially configured, or missing
3. **Applies the appropriate changes** for the detected language (see per-language steps below)
4. **Updates `appsettings.json`** (.NET) or **`.env`** (Python/Node.js) with required config keys
5. **Shows verification steps** so you can confirm traces are flowing

## Implementation

When this skill is invoked, follow these steps exactly:

### Step 1 — Detect project type

Search the current directory for:
- `requirements.txt` or `pyproject.toml` → **Python**
- `package.json` → **Node.js**
- Any `*.csproj` file → **.NET**

If multiple are found, ask the user which one to use.
If none are found, report: "No supported project file found. Are you in the right directory?"

### Step 2 — Check current state (also used for --status)

**.NET:** Check for `Observability/ObservabilityServiceExtensions.cs` and `AddAgent365Observability()` in `Program.cs`
**Python:** Check for `from azure.monitor.opentelemetry import configure_azure_monitor` or `ENABLE_OBSERVABILITY_SDK` in `.env`
**Node.js:** Check for `@azure/monitor-opentelemetry` in `package.json` dependencies or `useAzureMonitor` in source files

Report the current state before making changes:
- Already fully configured → say so and stop (unless --force)
- Partially configured → describe what's missing
- Not configured → proceed

---

## .NET Steps (temporary staging approach — NuGet package not yet published)

### Step 3a — Ask for SDK source path

The observability packages are not yet on NuGet. Ask the user:
**"Where is your local clone of the Agent365-dotnet repo? (e.g. C:\repos\Agent365-dotnet)"**

This path is needed for the `<ProjectReference>` entries.

### Step 4a — Create Observability/ folder and copy staging files

Create an `Observability/` folder in the project directory and write these two files:

**`Observability/ObservabilityServiceExtensions.cs`:**
```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// NOTE: This file is a temporary staging helper.
// The plan is to move these types into Microsoft.Agents.A365.Observability.Hosting
// so that agent apps can add full observability with two lines and zero copied files.
// Track: https://github.com/microsoft/agent365

using System;
using Microsoft.Agents.A365.Observability.Hosting;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.A365.Observability.Extensions;

/// <summary>
/// Wraps <see cref="AgentDetails"/> as a single injectable for agents that operate in a single tenant.
/// </summary>
public sealed class Agent365ObservabilityContext
{
    /// <summary>Agent identity and metadata for span attributes (includes TenantId).</summary>
    public AgentDetails AgentDetails { get; }

    internal Agent365ObservabilityContext(AgentDetails agentDetails)
    {
        AgentDetails = agentDetails;
    }
}

/// <summary>
/// Extension methods for registering Agent 365 observability services.
/// These methods will be shipped as part of Microsoft.Agents.A365.Observability.Hosting in a future release.
/// </summary>
public static class ObservabilityServiceExtensions
{
    /// <summary>
    /// Adds all Agent 365 observability services required for span export.
    /// Registers the S2S token cache, exporter, ObservabilityTokenService background service,
    /// and Agent365ObservabilityContext singleton.
    /// Configuration section is populated automatically by a365 setup all.
    /// </summary>
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

**`Observability/ObservabilityTokenService.cs`:**
```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.A365.Observability.Hosting.Caching;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.A365.Observability.Extensions;

/// <summary>
/// Background service that acquires a Power Platform token for the Agent 365 observability exporter
/// via a 3-hop FMI chain and pushes it into IExporterTokenCache.
///
/// Hop 1+2: Authenticate as Blueprint (MSI in prod, client secret locally) + get T1 via FMI path to Agent Identity.
/// Hop 3:   Agent Identity uses T1 as assertion to acquire Power Platform token.
/// The Agent Identity is ServiceIdentity type (not agentic), so AADSTS82001 does not apply.
/// </summary>
internal sealed class ObservabilityTokenService : BackgroundService
{
    private static readonly string[] FmiScopes = ["api://AzureADTokenExchange/.default"];
    private static readonly string[] PowerPlatformScopes = ["https://api.powerplatform.com/.default"];
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(50);

    private readonly IExporterTokenCache<string> _tokenCache;
    private readonly ILogger<ObservabilityTokenService> _logger;
    private readonly string _blueprintClientId;
    private readonly string _blueprintClientSecret;
    private readonly string _tenantId;
    private readonly string _agentId;

    public ObservabilityTokenService(
        IExporterTokenCache<string> tokenCache,
        ILogger<ObservabilityTokenService> logger,
        IConfiguration configuration)
    {
        _tokenCache = tokenCache;
        _logger = logger;

        var obs = configuration.GetSection("Agent365Observability");
        _tenantId = obs["TenantId"] ?? throw new InvalidOperationException("Agent365Observability:TenantId is required.");
        _agentId = obs["AgentId"] ?? throw new InvalidOperationException("Agent365Observability:AgentId is required.");
        _blueprintClientId = obs["ClientId"] ?? throw new InvalidOperationException("Agent365Observability:ClientId is required.");
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

    private async Task AcquireAndRegisterTokenAsync(CancellationToken cancellationToken)
    {
        string t1Token;
        string authority = $"https://login.microsoftonline.com/{_tenantId}";

        var msiCredential = new ManagedIdentityCredential();
        try
        {
            var assertion = await msiCredential.GetTokenAsync(
                new TokenRequestContext(["api://AzureADTokenExchange"]), cancellationToken);
            var blueprintApp = ConfidentialClientApplicationBuilder
                .Create(_blueprintClientId)
                .WithClientAssertion((AssertionRequestOptions _) => Task.FromResult(assertion.Token))
                .WithAuthority(new Uri(authority)).Build();
            t1Token = (await blueprintApp.AcquireTokenForClient(FmiScopes).WithFmiPath(_agentId).ExecuteAsync(cancellationToken)).AccessToken;
        }
        catch (AuthenticationFailedException)
        {
            // Local dev fallback — use client secret instead of MSI
            var blueprintApp = ConfidentialClientApplicationBuilder
                .Create(_blueprintClientId).WithClientSecret(_blueprintClientSecret)
                .WithAuthority(new Uri(authority)).Build();
            t1Token = (await blueprintApp.AcquireTokenForClient(FmiScopes).WithFmiPath(_agentId).ExecuteAsync(cancellationToken)).AccessToken;
        }

        var identityApp = ConfidentialClientApplicationBuilder
            .Create(_agentId)
            .WithClientAssertion((AssertionRequestOptions _) => Task.FromResult(t1Token))
            .WithAuthority(new Uri(authority)).Build();
        var ppResult = await identityApp.AcquireTokenForClient(PowerPlatformScopes).ExecuteAsync(cancellationToken);
        _tokenCache.RegisterObservability(_agentId, _tenantId, ppResult.AccessToken, PowerPlatformScopes);
        _logger.LogInformation("Observability token registered for agent {AgentId}.", _agentId);
    }
}
```

### Step 5a — Update the .csproj

Add these two `ItemGroup` blocks to the project's `.csproj` file (replace `<SDK_PATH>` with the user-provided path):

```xml
  <ItemGroup>
    <!-- Agent 365 Observability — temporary until NuGet package is published -->
    <PackageReference Include="Microsoft.Identity.Client" Version="4.*" />
    <!-- Pin to match the version required by the Observability project references -->
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="<SDK_PATH>\src\Observability\Runtime\Microsoft.Agents.A365.Observability.Runtime.csproj" />
    <ProjectReference Include="<SDK_PATH>\src\Observability\Hosting\Microsoft.Agents.A365.Observability.Hosting.csproj" />
  </ItemGroup>
```

### Step 6a — Update Program.cs

Add these using statements after existing usings:
```csharp
using Microsoft.Agents.A365.Observability.Extensions;
using Microsoft.Agents.A365.Observability.Hosting;
using Microsoft.Agents.A365.Observability.Runtime;
```

Add these two lines in `Program.cs` after all other `builder.Services.*` registrations, before `var app = builder.Build();`:
```csharp
// Agent 365 observability — S2S token exporter + background token service (3-hop FMI chain).
// Reads Agent365Observability section from configuration (populated by 'a365 setup all').
builder.Services.AddAgent365Observability();
builder.AddA365Tracing();
```

### Step 7a — Add Agent365Observability config section to appsettings.json

Add this section to `appsettings.json` (values are populated by `a365 setup all` / `a365.generated.config.json` — use placeholders for now):
```json
"EnableAgent365Exporter": "true",
"Agent365Observability": {
  "AgentId": "<agentId-from-a365.generated.config.json>",
  "AgentBlueprintId": "<clientId-from-a365.generated.config.json>",
  "TenantId": "<tenantId>",
  "ClientId": "<clientId-from-a365.generated.config.json>",
  "ClientSecret": "<clientSecret-from-a365.generated.config.json>",
  "AgentName": "<agent-display-name>",
  "AgentDescription": "<agent-description>"
}
```

Also add observability log levels to the `Logging.LogLevel` section:
```json
"Microsoft.Agents.A365.Observability": "Debug",
"OpenTelemetry": "Debug"
```

### Step 8a — Verify build

```bash
dotnet build
```

If there are errors referencing missing types from the Observability packages, confirm the SDK path is correct and the `.csproj` project references resolve.

---

## Python Steps

### Step 3b — Install SDK package

```bash
pip install azure-monitor-opentelemetry
```
Then add `azure-monitor-opentelemetry` to `requirements.txt` if not already there.

### Step 4b — Find main entry point

Look for `main.py`, `app.py`, `agent.py`, or the script referenced as entry in `pyproject.toml`.

### Step 5b — Inject init code

Inject after stdlib imports, before framework imports:
```python
# Observability — must be initialized before agent/LLM imports
import os
from azure.monitor.opentelemetry import configure_azure_monitor
if os.getenv("ENABLE_OBSERVABILITY_SDK", "").lower() == "true":
    configure_azure_monitor(
        connection_string=os.environ["APPLICATIONINSIGHTS_CONNECTION_STRING"]
    )
```

### Step 6b — Update .env

```
ENABLE_OBSERVABILITY_SDK=true
OBSERVABILITY_SERVICE_NAME=<agent-name>
APPLICATIONINSIGHTS_CONNECTION_STRING=<from Azure Portal > App Insights > Overview>
```

---

## Node.js Steps

### Step 3c — Install SDK package

```bash
npm install @azure/monitor-opentelemetry
```

### Step 4c — Find main entry point

Check `package.json` `"main"` field, then look for `index.js`, `app.js`, `server.js`.

### Step 5c — Inject init code at top of file, before other requires:

```javascript
// Observability — must be initialized before agent/LLM imports
const { useAzureMonitor } = require("@azure/monitor-opentelemetry");
if (process.env.ENABLE_OBSERVABILITY_SDK === "true") {
    useAzureMonitor();
}
```

### Step 6c — Update .env

```
ENABLE_OBSERVABILITY_SDK=true
OBSERVABILITY_SERVICE_NAME=<agent-name>
APPLICATIONINSIGHTS_CONNECTION_STRING=<from Azure Portal > App Insights > Overview>
```

---

## Final step (all languages) — Show verification steps

```
Observability setup complete.

To verify:
1. Run your agent locally
2. Open Azure Portal > Application Insights > Live Metrics
   You should see live requests within ~30 seconds

For .NET: values in Agent365Observability config section come from a365.generated.config.json
  after running 'a365 setup all'. Copy AgentId, ClientId, ClientSecret, TenantId into appsettings.json.
```

## Notes

- **.NET only:** The two `Observability/` files are temporary staging helpers. When `Microsoft.Agents.A365.Observability.Hosting` ships on NuGet, delete those files, remove the `<ProjectReference>` blocks, and replace with a single `<PackageReference>`.
- The `Agent365Observability` config section is written automatically by `a365 setup all` into `a365.generated.config.json`. Copy the values into `appsettings.json` or inject them as environment variables.
- Do not commit secrets (`ClientSecret`) to version control. Use environment variables or Azure Key Vault in production.

## Requirements

- For Python: Python 3.8+ and pip
- For Node.js: Node.js 16+ and npm
- For .NET: .NET 8.0+ SDK + local clone of Agent365-dotnet repo (temporary requirement)
- `a365 setup all` must have been run so `Agent365Observability` config values are available
