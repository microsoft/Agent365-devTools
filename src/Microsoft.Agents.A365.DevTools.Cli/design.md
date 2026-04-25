# Microsoft.Agents.A365.DevTools.Cli - Architecture

This document describes the architecture of the main CLI application. For development how-to guides, see [DEVELOPER.md](../DEVELOPER.md).

> **Parent:** [Repository Design](../../docs/design.md)

---

## Project Structure

```
Microsoft.Agents.A365.DevTools.Cli/
├── Program.cs                    # CLI entry point, DI registration, command registration
├── Commands/                     # Command implementations
│   ├── ConfigCommand.cs          # a365 config (init, display)
│   ├── SetupCommand.cs           # a365 setup (blueprint + messaging endpoint)
│   ├── CreateInstanceCommand.cs  # a365 create-instance (identity, licenses, notifications)
│   ├── DeployCommand.cs          # a365 deploy
│   ├── CleanupCommand.cs         # a365 cleanup (delete resources)
│   ├── QueryEntraCommand.cs      # a365 query-entra (blueprint-scopes, instance-scopes)
│   ├── DevelopCommand.cs         # a365 develop (development utilities)
│   ├── DevelopMcpCommand.cs      # a365 develop-mcp (MCP server management)
│   ├── PublishCommand.cs         # a365 publish (manifest packaging for upload)
│   └── SetupSubcommands/         # Setup workflow components
├── Services/                     # Business logic services
│   ├── ConfigService.cs          # Configuration management
│   ├── DeploymentService.cs      # Multiplatform Azure deployment
│   ├── PlatformDetector.cs       # Automatic platform detection
│   ├── IPlatformBuilder.cs       # Platform builder interface
│   ├── DotNetBuilder.cs          # .NET project builder
│   ├── NodeBuilder.cs            # Node.js project builder
│   ├── PythonBuilder.cs          # Python project builder
│   ├── TeamsGraphBackendConfigurator.cs  # Messaging endpoint (Teams Graph backend config)
│   ├── GraphApiService.cs        # Graph API interactions
│   ├── AuthenticationService.cs  # MSAL.NET authentication
│   ├── AzureAuthValidator.cs     # Azure CLI auth + App Service token validation
│   ├── Helpers/                  # Service helper utilities
│   └── Requirements/             # Prerequisite validation system
│       ├── IRequirementCheck.cs  # Check interface
│       ├── RequirementCheck.cs   # Abstract base class with logging wrapper
│       ├── RequirementCheckResult.cs  # Success/Warning/Failure result
│       └── RequirementChecks/    # Concrete check implementations
├── Models/                       # Data models
│   ├── Agent365Config.cs         # Unified configuration model
│   ├── ProjectPlatform.cs        # Platform enumeration
│   └── OryxManifest.cs           # Azure Oryx manifest model
├── Constants/                    # Centralized constants
│   ├── ErrorCodes.cs             # Error code definitions
│   ├── ErrorMessages.cs          # Error message templates
│   └── AuthenticationConstants.cs # Auth-related constants
├── Exceptions/                   # Custom exception types
├── Helpers/                      # Utility helpers
└── Templates/                    # Embedded resources (manifest.json, icons)
```

---

## Folder Documentation

| Folder | Purpose | README |
|--------|---------|--------|
| **Commands/** | CLI command implementations | [README](Commands/README.md) |
| **Commands/SetupSubcommands/** | Setup workflow components | [README](Commands/SetupSubcommands/README.md) |
| **Services/** | Business logic services | [README](Services/README.md) |
| **Services/Helpers/** | Service helper utilities | [README](Services/Helpers/README.md) |
| **Models/** | Data models | [README](Models/README.md) |
| **Constants/** | Centralized constants | [README](Constants/README.md) |
| **Exceptions/** | Custom exception types | [README](Exceptions/README.md) |
| **Helpers/** | Utility helpers | [README](Helpers/README.md) |

---

## Configuration System Architecture

### Two-File Design

The CLI uses a unified configuration model with a clear separation between static (user-managed) and dynamic (CLI-managed) data.

```mermaid
flowchart LR
    subgraph User["User-Managed"]
        Static["a365.config.json<br/>Version controlled"]
    end

    subgraph CLI["CLI-Managed"]
        Dynamic["a365.generated.config.json<br/>Gitignored"]
    end

    subgraph Runtime["Runtime"]
        Config["Agent365Config<br/>Merged model"]
    end

    Static -->|Load| Config
    Dynamic -->|Merge| Config
    Config -->|Save state| Dynamic
```

| File | Content | Editing |
|------|---------|---------|
| `a365.config.json` | Tenant ID, subscription, resource names, project path | User edits |
| `a365.generated.config.json` | Agent blueprint ID, identity ID, consent status | CLI generates |

### Configuration File Storage and Portability

Both configuration files are stored in **two locations**:

1. **Project Directory** (optional, for local development)
2. **%LocalAppData%\Microsoft.Agents.A365.DevTools.Cli** (authoritative, for portability)

This dual-storage design enables **CLI portability** - users can run `a365` commands from any directory on their system, not just the project directory. The `deploymentProjectPath` property points to the actual project location.

**File Resolution Strategy:**
- **Load**: Current directory first, then %LocalAppData% (fallback)
- **Save**: Write to **both** locations to maintain consistency
- **Sync**: When static config is loaded from current directory, it's automatically synced to %LocalAppData%

### Agent365Config Model Design

The unified model uses C# property patterns to enforce immutability:

```csharp
public class Agent365Config
{
    // STATIC PROPERTIES (init-only) - from a365.config.json
    // Set once during configuration, never change at runtime
    public string TenantId { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public string ResourceGroup { get; init; } = string.Empty;
    public string WebAppName { get; init; } = string.Empty;
    public string DeploymentProjectPath { get; init; } = string.Empty;

    // DYNAMIC PROPERTIES (get/set) - from a365.generated.config.json
    // Modified at runtime by CLI operations
    public string? AgentBlueprintId { get; set; }
    public string? AgentIdentityId { get; set; }
    public string? AgentUserId { get; set; }
    public string? AgentUserPrincipalName { get; set; }
    public bool? Consent1Granted { get; set; }
    public bool? Consent2Granted { get; set; }
    public bool? Consent3Granted { get; set; }
}
```

**Design Principles:**
- `init` properties = Immutable after construction = Static config
- `get; set` properties = Mutable = Dynamic state
- `ConfigService` handles merge (load) and split (save) logic

### Environment Variable Overrides

For security and flexibility, the CLI supports environment variable overrides:

| Variable | Purpose |
|----------|---------|
| `A365_MCP_APP_ID` | Override Agent 365 Tools App ID for authentication |
| `A365_MCP_APP_ID_{ENV}` | Per-environment MCP Platform App ID |
| `A365_DISCOVER_ENDPOINT_{ENV}` | Per-environment discover endpoint URL |
| `POWERPLATFORM_API_URL` | Override Power Platform API URL |

**Design Decision:** All test/preprod App IDs and URLs have been removed from the codebase. The production App ID is the only hardcoded value. Internal Microsoft developers use environment variables for non-production testing.

### Sovereign / Government Cloud Configuration

By default the CLI targets the commercial Microsoft Graph endpoint. For sovereign or government cloud tenants, set `graphBaseUrl` in `a365.config.json`:

| Cloud | `graphBaseUrl` value |
|-------|----------------------|
| Commercial (default) | *(omit the field)* |
| GCC High / DoD | `https://graph.microsoft.us` |
| China (21Vianet) | `https://microsoftgraph.chinacloudapi.cn` |

This field is optional. When omitted, `https://graph.microsoft.com` is used.

The value is read from `Agent365Config.GraphBaseUrl` and forwarded to `GraphApiService` via its `GraphBaseUrl` property after config is loaded. This controls both the HTTP endpoint used for all Graph API calls and the token resource identifier passed to `AuthenticationService.GetAccessTokenAsync`.

---

## Authentication Architecture

All token acquisition goes through **MSAL.NET via `AuthenticationService`**. No az CLI subprocess is used for tokens.

### Token Acquisition Flow

```
All callers (GraphApiService, ArmApiService, TeamsGraphBackendConfigurator, ...)
        |
        v
AuthenticationService.GetAccessTokenAsync(resource, tenantId)
        |
        +-- Check persistent disk cache (%LocalAppData%\Agent365\token-cache.json)
        |       Cache key: {resource}[:tenant:{tenantId}][:user:{userId}]
        |
        +-- Cache hit + not expiring: return token immediately (0 prompts)
        |
        +-- Cache miss / expired: MsalBrowserCredential.GetTokenAsync(scopes)
                |
                +-- Windows:  WAM broker (no browser, SSO, CAP-compliant)
                +-- macOS:    System browser → device code fallback if restricted
                +-- Linux:    Device code flow
```

### Two Auth Paths

| Path | When used | Scopes | Client app |
|---|---|---|---|
| **Default (MSAL)** | ARM, Graph `.default` calls | `{resource}/.default` | PowerShell client ID |
| **Delegated scopes (MSAL)** | Graph calls needing specific permissions (e.g. `AgentInstance.ReadWrite.All`) | Explicit scope list | `clientAppId` from config |

### Login Hint Resolution

To prevent WAM from selecting a stale or wrong account, a login hint (UPN) is resolved before interactive auth:
1. `AzCliHelper.ResolveLoginHintAsync()` — reads `az account show` if az CLI is present
2. `AuthenticationService.ResolveLoginHintFromCacheAsync()` — decodes `upn`/`preferred_username` from a cached JWT if az CLI is unavailable

### IAuthenticationService Interface

`IAuthenticationService` is defined in the same file as `AuthenticationService` (`AuthenticationService.cs`). This is intentional — the interface is narrow (two methods), tightly coupled to its single implementation, and co-location follows the **related-interfaces convention** in the copilot instructions. It exists solely to enable test substitution in `ArmApiService` and `GraphApiService` without triggering real MSAL/WAM prompts.

Only `GetAccessTokenAsync` and `ResolveLoginHintFromCacheAsync` are on the interface. Other methods (`GetAccessTokenWithScopesAsync`, `GetAccessTokenForMcpAsync`, `ClearCache`) stay on the concrete class and are used by commands that take `AuthenticationService` directly.

### Token Caching

- **Persistent cache** (`AuthenticationService`): survives across CLI invocations, keyed by resource + tenant + user
- **Process-level login hint cache** (`AzCliHelper`): caches the result of `az account show` for the process lifetime — invalidated after `az login` operations

### Platform Notes

- **Windows**: WAM handles token acquisition at the OS level — no browser popup, no Python subprocess, corporate proxy not involved
- **macOS/Linux**: Browser redirect or device code — falls back to device code automatically if browser auth is blocked by tenant policy (e.g., corp-managed macOS)

---

## Command Pattern Implementation

Commands use `System.CommandLine` with a static factory method pattern. Each command exposes a `CreateCommand(...)` method that receives its dependencies as explicit parameters and returns a `Command` object wired with a `SetHandler` lambda.

```csharp
// Factory method receives dependencies explicitly — no reflection-based DI
internal static Command CreateCommand(
    ILogger logger,
    IConfigService configService,
    GraphApiService graphApiService,
    ...)
{
    var command = new Command("setup", "...");
    var configOption = new Option<FileInfo>(["--config", "-c"], ...);
    command.AddOption(configOption);

    command.SetHandler(async (InvocationContext context) =>
    {
        var config = context.ParseResult.GetValueForOption(configOption)!;
        var ct = context.GetCancellationToken();

        // Implementation
        context.ExitCode = 0;
    });

    return command;
}
```

**Guidelines:**
- Keep command handlers thin — delegate business logic to services or orchestrators
- Dependencies are passed as constructor-style parameters to `CreateCommand`
- Exit code: 0 = success, 1 = failure (set via `context.ExitCode` or `Environment.Exit`)
- Log progress with `ILogger<T>` and structured placeholders (`{Name}` syntax)
- Dry-run guard: check `dryRun` flag before any mutating work

---

## Prerequisite Validation Pattern (IRequirementCheck)

Commands validate prerequisites through a structured check system before performing any mutating work. This produces consistent `[PASS]`/`[FAIL]`/`[WARN]` output and ensures users see actionable errors early.

### Core Types

```csharp
// Each check returns a structured result
public class RequirementCheckResult
{
    public bool Passed { get; }                      // true = pass or warning, false = failure
    public bool IsWarning { get; }                   // true = warning (non-blocking)
    public string? ErrorMessage { get; }             // What went wrong
    public string? ResolutionGuidance { get; }       // How to fix it
    public string? Details { get; }                  // Additional context (e.g., URLs)
}

// Base class handles [PASS]/[FAIL]/[WARN] output and check execution
public abstract class RequirementCheck : IRequirementCheck
{
    public abstract string Name { get; }
    public abstract string Category { get; }
    public abstract Task<RequirementCheckResult> CheckAsync(Agent365Config, ILogger, CancellationToken);
}
```

### Check Composition

Each command declares its checks via a static `GetChecks()` method, making composition explicit and testable:

```csharp
// deploy: auth first, then App Service token
public static List<IRequirementCheck> GetChecks(AzureAuthValidator auth)
    => [new AzureAuthRequirementCheck(auth), new AppServiceAuthRequirementCheck(auth)];

// setup infrastructure: base checks + config validation
internal static List<IRequirementCheck> GetChecks(AzureAuthValidator auth)
{
    var checks = SetupCommand.GetBaseChecks(auth);  // Auth + FrontierPreview + PowerShell
    checks.Add(new InfrastructureRequirementCheck());
    return checks;
}
```

### Running Checks

`RequirementsSubcommand.RunChecksOrExitAsync` is the shared runner — prints `[PASS]/[FAIL]/[WARN]` per check and calls `ExceptionHandler.ExitWithCleanup(1)` on any failure:

```csharp
await RequirementsSubcommand.RunChecksOrExitAsync(
    GetChecks(authValidator), config, logger, cancellationToken);
```

### Dry-Run Rule

Commands supporting `--dry-run` skip checks entirely — the `RunChecksOrExitAsync` call is guarded by `if (!dryRun)` so dry runs are always fast and require no Azure credentials.

### Available Checks

| Check | Category | Used By |
|-------|----------|---------|
| `AzureAuthRequirementCheck` | Azure | setup all, setup infra, deploy, cleanup azure |
| `AppServiceAuthRequirementCheck` | Azure | deploy |
| `FrontierPreviewRequirementCheck` | Tenant Enrollment | setup all, setup infra |
| `PowerShellModulesRequirementCheck` | Tools | setup all, setup infra |
| `InfrastructureRequirementCheck` | Configuration | setup infra |
| `LocationRequirementCheck` | Configuration | setup endpoint |
| `ClientAppRequirementCheck` | Configuration | setup blueprint |

---

## Multiplatform Deployment Architecture

### Platform Detection

The `PlatformDetector` service auto-detects project type from files:

```csharp
public enum ProjectPlatform
{
    Unknown, DotNet, NodeJs, Python
}
```

| Platform | Detection Files |
|----------|-----------------|
| .NET | `*.csproj`, `*.fsproj`, `*.vbproj` |
| Node.js | `package.json` |
| Python | `requirements.txt`, `setup.py`, `pyproject.toml`, `*.py` |

Detection priority: .NET > Node.js > Python > Unknown

### Platform Builder Interface

```csharp
public interface IPlatformBuilder
{
    Task<bool> ValidateEnvironmentAsync();      // Check required tools installed
    Task CleanAsync(string projectDir);         // Clean build artifacts
    Task<string> BuildAsync(string projectDir, string outputPath, bool verbose);
    Task<OryxManifest> CreateManifestAsync(string projectDir, string publishPath);
}
```

### Deployment Pipeline

```mermaid
flowchart TD
    A[Platform Detection] --> B[Environment Validation]
    B --> C[Clean Build Artifacts]
    C --> D[Platform-Specific Build]
    D --> E[Create Oryx Manifest]
    E --> F[Package ZIP]
    F --> G[Deploy to Azure App Service]
```

1. **Platform Detection** - Auto-detect project type from files
2. **Environment Validation** - Check required tools (dotnet/node/python)
3. **Clean** - Remove previous build artifacts
4. **Build** - Platform-specific build process
5. **Manifest Creation** - Generate Azure Oryx manifest
6. **Package** - Create deployment ZIP
7. **Deploy** - Upload to Azure App Service

### Restart Mode (`--restart` flag)

For quick iteration after manual changes to the `publish/` folder:

```bash
a365 deploy           # Full pipeline: steps 1-7
a365 deploy --restart # Quick mode: steps 6-7 only (packaging + deploy)
```

---

## Permissions Architecture

The CLI configures two independent layers of permissions for agent blueprints:

1. **Inheritable Permissions** — Blueprint-level permissions that agent instances inherit automatically. Set via the Agent Blueprint API (`/beta/applications/microsoft.graph.agentIdentityBlueprint/{id}/inheritablePermissions`). Requires Agent ID Administrator or Global Administrator role. Read back after writing to verify presence.
2. **OAuth2 Grants** — Tenant-wide delegated consent via Graph API `/oauth2PermissionGrants` with `consentType=AllPrincipals`. Requires Global Administrator only.

> **Technical limitation:** `oauth2PermissionGrant` creation via the API requires `DelegatedPermissionGrant.ReadWrite.All`, which is an admin-only scope. Additionally, Global Administrator bypasses entitlement validation and can grant any scope; non-admin users receive HTTP 403 (insufficient privileges) or HTTP 400 (entitlement not found) for all resource SPs. There is no self-service path for non-admin users.

> **Note:** `requiredResourceAccess` (portal "API permissions") is **not** configured for Agent Blueprints — it is not supported by the Agent ID API.

```mermaid
flowchart TD
    Blueprint["Agent Blueprint<br/>(Application Registration)"]
    OAuth2["OAuth2 Permission Grants<br/>(AllPrincipals — Global Admin only)"]
    Inheritable["Inheritable Permissions<br/>(Agent ID Admin or Global Admin)"]
    Instance["Agent Instance<br/>(Inherits from Blueprint)"]

    Blueprint --> OAuth2
    Blueprint --> Inheritable
    Inheritable --> Instance
```

### Role-based setup workflow

Because the two permission layers require different roles, the CLI supports a two-person handoff:

| Step | Command | Who runs it | What it does |
|------|---------|-------------|--------------|
| 1 | `a365 setup all` | Agent ID Admin or Developer | All infra + blueprint + inheritable permissions. OAuth2 grants skipped (requires GA). Ends with instructions to hand off config folder to GA. |
| 2 | `a365 setup admin --config-dir "<path>"` | Global Administrator | Reads both config files, resolves SPs, creates AllPrincipals OAuth2 grants for all resources. |

**Batch flow (`BatchPermissionsOrchestrator`):**
- **Phase 1:** Token prewarm + SP resolution (blueprint + all resource SPs).
- **Phase 2a:** Inheritable permissions — set via Blueprint API, read back to verify. Agent ID Admin and GA.
- **Phase 2b:** OAuth2 grants — `AllPrincipals` via Graph API. GA only; skipped for non-admin with instruction to run `setup admin`.
- **Phase 3:** For GA: skipped (Phase 2b satisfies consent). For non-admin: shows `setup admin` command and a Graph Explorer query to verify inheritable permissions.

**Standalone callers:** `SetupHelpers.EnsureResourcePermissionsAsync` handles a single resource with retry logic and is used by `CopilotStudioSubcommand` and direct callers.

**Per-Resource Tracking:** `ResourceConsent` model tracks inheritance state per resource (Agent 365 Tools, Messaging Bot API, Observability API).

---

## Entry Point (Program.cs)

The entry point handles:

1. **Logging Configuration** - `Microsoft.Extensions.Logging` with clean console and file sinks (per-command log file under `%LocalAppData%`)
2. **Service Resolution** - Services are registered in a DI container (ServiceCollection/ServiceProvider) and passed to `CreateCommand` factory methods
3. **Command Registration** - `System.CommandLine` `RootCommand` with subcommands added via `AddCommand`
4. **Exception Handling** - `CommandLineBuilder` middleware + `ExceptionHandler` for user-friendly messages

```csharp
// Simplified structure
var loggerFactory = LoggerFactoryHelper.CreateCleanLoggerFactory(logLevel);
var configService = new ConfigService(loggerFactory.CreateLogger<ConfigService>());
var graphApiService = new GraphApiService(...);
// ... more services

var rootCommand = new RootCommand("a365 — Microsoft Agent 365 CLI");
rootCommand.AddCommand(SetupCommand.CreateCommand(logger, configService, ...));
rootCommand.AddCommand(DeployCommand.CreateCommand(logger, configService, ...));
// ... more commands

return await new CommandLineBuilder(rootCommand)
    .UseDefaults()
    .Build()
    .InvokeAsync(args);
```

---

## Setup Workflow Architecture

### Two Agent Flows

`a365 setup all` supports two distinct agent types, controlled by `--aiteammate` (CLI) or `aiTeammate` (config):

| Agent Type | Flag | What it creates |
|---|---|---|
| **Own-identity agent** (default) | `--aiteammate true` or omit | Azure infra + Agent Blueprint + batch permissions (5 resources) + messaging endpoint |
| **Custom Engine Agent / Blueprint** | `--aiteammate false` | Agent Blueprint + batch permissions (Graph + A365 Tools only) + Agent Instance (Graph API) |

Non-DW blueprint agents do not use Azure Bot Service, so there is no infrastructure step, no manifest zip, and no messaging endpoint registration. The final step is `POST /beta/agentRegistry/agentInstances` instead.

### SetupContext — Shared Step State

`SetupContext` is a bundle of mutable state and services passed to each extracted step method. It enables the non-DW orchestrator to reuse the same step implementations as the DW flow without duplicating code.

```
AllSubcommand.ExecuteAsync
  │
  ├── builds SetupContext (config, results, logger, services)
  │
  ├── DW path:
  │     ExecuteInfrastructureStepAsync(ctx)     ← DW only
  │     ExecuteBlueprintStepAsync(ctx)           ← shared
  │     ExecuteBatchPermissionsStepAsync(ctx, dwSpecs)  ← shared (5 resources)
  │     ExecuteMessagingEndpointStepAsync(ctx)   ← DW only
  │
  └── Non-DW path:
        NonDwBlueprintSetupOrchestrator.ExecuteAsync(ctx)
          ExecuteBlueprintStepAsync(ctx)         ← reuses DW step
          ExecuteBatchPermissionsStepAsync(ctx, nonDwSpecs)  ← reuses, 2 resources only
          RegisterAgentInstanceAsync(...)        ← non-DW final step
```

`SetupContext.Config` is intentionally mutable — the blueprint step reloads configuration from disk after writing `AgentBlueprintId`, and the updated instance must be visible to all subsequent steps.

### Batch Permissions — Resource Specs

The non-DW spec list is a strict subset of the DW list:

| Resource | DW | Non-DW Blueprint |
|---|---|---|
| Microsoft Graph (delegated) | ✓ | — |
| Agent 365 Tools (delegated) | ✓ | — |
| Messaging Bot API | ✓ | — |
| Observability API | ✓ | ✓ |
| Power Platform API | ✓ | ✓ |

---

## Cross-References

- **[Repository Design](../../docs/design.md)** - High-level architecture overview
- **[Developer Guide](../DEVELOPER.md)** - Build, test, add commands, contribute
- **[Commands README](Commands/README.md)** - Command implementations
- **[Services README](Services/README.md)** - Business logic services
