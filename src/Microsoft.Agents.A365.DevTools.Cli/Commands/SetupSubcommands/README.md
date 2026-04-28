# SetupSubcommands

This folder contains the workflow components for the `a365 setup` command. The setup process is divided into discrete subcommands that can run independently or as part of the full workflow.

> **Parent:** [Commands](../README.md) | **CLI Design:** [design.md](../../design.md)

---

## Component Reference

| Component | File | Description |
|-----------|------|-------------|
| **AllSubcommand** | `AllSubcommand.cs` | Orchestrates the complete setup workflow (`a365 setup all`) |
| **BlueprintSubcommand** | `BlueprintSubcommand.cs` | Creates agent blueprint application registration |
| **BlueprintCreationOptions** | `BlueprintCreationOptions.cs` | Options record for blueprint creation (e.g. `DeferConsent`) |
| **InfrastructureSubcommand** | `InfrastructureSubcommand.cs` | Provisions Azure infrastructure (App Service, etc.) |
| **PermissionsSubcommand** | `PermissionsSubcommand.cs` | Configures Graph API permissions and admin consent |
| **BatchPermissionsOrchestrator** | `BatchPermissionsOrchestrator.cs` | Three-phase batch permissions flow used by `setup all` and standalone permission commands |
| **ResourcePermissionSpec** | `ResourcePermissionSpec.cs` | Spec record describing a single resource's required permissions |
| **RequirementsSubcommand** | `RequirementsSubcommand.cs` | Validates prerequisites (Azure CLI, permissions) |
| **SetupHelpers** | `SetupHelpers.cs` | Shared helper methods; `EnsureResourcePermissionsAsync` used by standalone callers and `CopilotStudioSubcommand` |
| **SetupResults** | `SetupResults.cs` | Result models for setup operations |
| **SetupContext** | `SetupContext.cs` | Context bundle threaded through orchestrator steps; exposes `AuthMode`, `IsOboMode`, `IsS2sMode`, `IsBothMode` |
| **NonDwBlueprintSetupOrchestrator** | `NonDwBlueprintSetupOrchestrator.cs` | Blueprint-based non-DW setup flow; skips Phase 2a/2b (inheritable permissions); gates agent identity grants by `authMode` |

---

## Setup Workflow

```mermaid
flowchart TD
    Start[a365 setup all] --> Requirements
    Requirements[Requirements Check] --> Blueprint
    Blueprint[Create Blueprint] --> Infrastructure
    Infrastructure[Provision Azure] --> Permissions
    Permissions[Configure Permissions] --> Endpoint
    Endpoint[Register Messaging Endpoint] --> Complete

    subgraph Individual["Individual Commands"]
        Req2[a365 setup requirements]
        BP2[a365 setup blueprint]
        Infra2[a365 setup infrastructure]
        Perm2[a365 setup permissions]
    end
```

### Workflow Steps

1. **Requirements** - Validate Azure CLI authentication, subscription access, required permissions
2. **Blueprint** - Create Entra ID application registration for the agent blueprint
3. **Infrastructure** - Provision Azure App Service, configure app settings
4. **Permissions** - Configure Microsoft Graph API permissions, grant admin consent
5. **Messaging Endpoint (M365 only, opt-in)** - For M365 agents, register the blueprint's backend messaging endpoint via the Teams Graph API (routed through MCP Platform). Requires `--m365`; other hosts configure the endpoint directly in the Teams Developer Portal.

---

## Usage

```bash
# Run complete setup
a365 setup all

# Run individual steps
a365 setup requirements    # Check prerequisites only
a365 setup blueprint       # Create blueprint only
a365 setup infrastructure  # Provision Azure only
a365 setup permissions     # Configure permissions only
```

### Authentication mode (`--authmode`)

The `--authmode` option controls how the agent identity service principal is granted permissions. It is available on `setup all`, `setup blueprint`, and `setup permissions mcp/bot/custom`.

| Value | Behaviour |
|-------|-----------|
| `obo` (default) | Principal-scoped delegated grants (`consentType: "Principal"`) on the agent identity SP — no Global Admin required |
| `s2s` | Application role assignments on the agent identity SP — attempted programmatically; PowerShell instructions printed as fallback if the caller lacks Global Admin |
| `both` | Both OBO delegated grants and S2S app role assignments |

`authMode` may also be persisted in `a365.config.json` so it takes effect on every run without the flag.

Phase 2a (inheritable permissions on the blueprint) and Phase 2b (AllPrincipals grants) are **always skipped** for non-DW agents regardless of `authMode`, to avoid requiring a Global Admin role.

```bash
# Use OBO grants (default)
a365 setup all

# Use S2S app-role assignments
a365 setup all --authmode s2s

# Use both
a365 setup all --authmode both
```

---

### Messaging endpoint (M365 agents)

The `--m365` flag opts into registering the messaging endpoint with Teams Graph via MCP Platform. It is **off by default** — without it, `--endpoint-only`, `--update-endpoint`, and the messaging-endpoint step in `setup all` skip the API call and point you at the Teams Developer Portal for manual configuration.

```bash
# Full setup including messaging endpoint registration
a365 setup all --m365

# Register the messaging endpoint only (existing blueprint)
a365 setup blueprint --endpoint-only --m365

# Update the endpoint URL (clears and re-registers)
a365 setup blueprint --update-endpoint https://your-host.example.com/api/messages --m365

# Non-M365 agent: CLI prints the Teams Developer Portal link and skips the API call
a365 setup blueprint --endpoint-only
```

When the server rejects the request with a recognized contract-mismatch signature (today: the pre-migration Azure Bot Service validator), the CLI logs at INFO:

```
Automated messaging endpoint registration is not available for this tenant yet. You'll need to configure it manually.
```

and the `a365 setup all` summary includes an "Action Required" entry with the Teams Developer Portal URL. The command does not fail — other setup steps still complete.

---

## BatchPermissionsOrchestrator

`BatchPermissionsOrchestrator.cs` implements a three-phase batch permissions flow used by `setup all` and the standalone `setup permissions` subcommands:

- **Phase 1 — Resolve service principals** (non-admin): Pre-warms the delegated token and resolves all SP IDs once. `requiredResourceAccess` is not updated here — it is not supported for Agent Blueprints.
- **Phase 2 — Configure inherited permissions** (Agent ID Administrator or Global Administrator): Creates OAuth2 grants and sets inheritable permissions using IDs from Phase 1. A 403 response is caught silently and treated as insufficient role — one consolidated warning is emitted without additional API calls.
- **Phase 3 — Grant admin consent** (Global Administrator only, or URL for non-admins): Checks for existing consent before opening a browser. Returns a consolidated URL when the user lacks the Global Administrator role.

`CopilotStudioSubcommand` is out of scope and continues to call `EnsureResourcePermissionsAsync` directly.

## SetupHelpers

The `SetupHelpers.cs` file contains shared functionality:

- **EnsureResourcePermissionsAsync** - Configures permissions for a single resource with retry logic; used by standalone `CopilotStudioSubcommand` and direct callers
- **WaitForPermissionPropagationAsync** - Waits for Entra ID permission propagation
- **ValidateConfigurationAsync** - Validates configuration before setup operations

---

## Cross-References

- **[Commands/](../README.md)** - Parent commands folder
- **[Services/](../../Services/README.md)** - Business logic services used by setup
- **[CLI Design](../../design.md)** - Permissions architecture details
