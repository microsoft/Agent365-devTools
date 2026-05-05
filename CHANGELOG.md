# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Upgrade Notes

#### Existing agents: grant Observability API permissions

Agents provisioned before this release need `Agent365.Observability.OtelWrite` granted as both a **delegated** and an **application** permission on the blueprint app. Requires Global Administrator.

**Option A — Entra portal** (no config files required):

1. [Entra portal](https://entra.microsoft.com) > **App registrations** > select your **Blueprint** app > **API permissions**
2. **Add a permission** > **APIs my organization uses** > search `9b975845-388f-4429-889e-eab1ef63949c`
3. **Delegated permissions** > select `Agent365.Observability.OtelWrite` > **Add permissions**
4. Repeat step 2 > **Application permissions** > select `Agent365.Observability.OtelWrite` > **Add permissions**
5. **Grant admin consent for \<tenant\>** > confirm

**Option B — CLI** (`a365 setup admin`) has been removed in this release. Use Option A above, or copy the PowerShell instructions printed in the `a365 setup all` summary output.

### Added
- Version check: stable-channel users now see an informational notice when a newer preview release exists above the current stable version, without triggering the update-required banner.
- `setup requirements` Global Administrator path: when the well-known CLI client app is not found in a new tenant, Global Admins are prompted to create the app and grant admin consent automatically (enter an app ID or type `C` to create).
- `--authmode obo|s2s|both` option on `setup all` — controls how the agent identity service principal receives permissions:
  - `obo` (default): principal-scoped delegated grants (`consentType: "Principal"`); no Global Administrator required.
  - `s2s`: application role assignments on the agent identity SP; attempted programmatically, falls back to printed PowerShell instructions if the caller lacks Global Administrator.
  - `both`: applies both OBO delegated grants and S2S app role assignments.
  - Inheritable permissions (Phase 2a) and AllPrincipals grants (Phase 2b) are always skipped for non-DW agents regardless of `authMode`, to avoid requiring a Global Administrator role.
  - `authMode` can be persisted in `a365.config.json` to apply on every run without the flag.
- `--project-path <path>` option on `develop list-configured`, `develop add-mcp-servers`, and `develop remove-mcp-servers` — specify the manifest location without requiring `a365.config.json`.
- `setup requirements` runs without `a365.config.json` — system checks (PowerShell modules, Frontier enrollment) always run; client app checks run when a config file or Azure CLI session is available.
- `--agent-name` and `--tenant-id` options added to `setup blueprint`, `setup permissions` (all subcommands), `create-instance`, `publish`, and `query-entra` — all commands can now resolve configuration from Entra ID without requiring `a365.config.json`.
- `setup permissions custom --resource-app-id <guid> --scopes <scopes>` — apply inline custom API permissions to the agent blueprint without editing `a365.config.json`.
- `--m365` opt-in flag on `a365 setup blueprint`, `a365 cleanup blueprint`, and `a365 setup all` — when set, the CLI registers or clears the agent blueprint's messaging endpoint via the Teams Graph backend configuration endpoint on MCP Platform. Default is **off**: without `--m365`, endpoint registration is skipped and the CLI points users at the Teams Developer Portal (https://learn.microsoft.com/en-us/microsoft-agent-365/developer/create-instance#1-configure-agent-in-teams-developer-portal) for manual configuration. Intended for M365 agents; opt-in because the Teams Graph rollout on MCP Platform is ongoing.
- Messaging endpoint row added to `a365 setup all` summary output, with "registered"/"reused"/"skipped (non-M365)"/"manual config required"/"failed" states. When registration can't complete, the summary surfaces an "Action Required" entry with the Teams Developer Portal URL so the user knows exactly what to do next.
- Defensive fallback when the server rejects the new request with a known contract-mismatch signature — the CLI logs `"Automated messaging endpoint registration is not available for this tenant yet. You'll need to configure it manually."` and directs the user to the Teams Developer Portal. Same user-facing path is reused when registration fails because the signed-in user is not a blueprint owner.

### Fixed
- `setup permissions bot` no longer emits "Bot API permissions configured successfully" when any S2S app-role assignment fails; shows a warning with retry instructions instead.
- Consent-required message "You are running as a non-admin user and cannot grant admin consent" replaced with "An administrator must grant tenant-wide consent to proceed" — the message fires when tenant-wide consent for S2S scopes has not yet been granted, not when the caller lacks admin rights.
- `setup all --agent-name` re-runs no longer create a duplicate agent registration: the CLI now reads `agentRegistrationId` from `a365.generated.config.json` (when present) and checks for an existing registration before posting a new one.
- `setup all` now skips agent registration with a clear warning when the agent identity ID is not available, instead of silently sending an invalid request. Retry with `a365 setup all --agent-registration-only` once the identity is ready.
- `setup permissions bot` now returns a non-zero exit code when an S2S app role assignment fails, so callers and scripts can detect the failure.
- `setup all --agent-registration-only` reliability fixes: stored IDs are now correctly read in bootstrap (`--agent-name`) mode; falls back to a Graph API lookup when `agenticAppId` is missing; skips identity, permission, and project-settings steps that don't apply.

### Removed
- `a365 config` command family (`config init`, `config display`, `config permissions`) — replaced by `a365 setup all --agent-name` and `a365 setup permissions custom`.

### Breaking Changes
- **`a365 config init` removed** — replace with `a365 setup all --agent-name <name>`. This creates the agent blueprint, configures permissions, and registers the messaging endpoint in one step without requiring a pre-existing config file.
- **`a365 config display` removed** — use `a365 query-entra blueprint-scopes` to inspect live blueprint permissions and consent state.
- **`a365 config permissions` removed** — replace with `a365 setup permissions custom --resource-app-id <guid> --scopes <scopes>`.
- **`--config`/`-c` option removed from all commands** — config file is now always resolved from the current directory (`a365.config.json`). Scripts passing `--config <path>` will receive a parse error; change directory before running the CLI instead.
- **`--agent-instance-only` renamed to `--agent-registration-only`** on `a365 setup all` — update any scripts using the old flag name.
- **`setup permissions custom --resource-app-id --scopes` applies permissions directly to Entra ID** — unlike the former `a365 config permissions` which only wrote to `a365.config.json`, this inline mode immediately mutates the live blueprint in Entra and cannot be undone by editing a config file.
- `a365 setup` now writes the `Agent365Observability` placeholder section (`AgentId`, `AgentBlueprintId`, `TenantId`, `AgentName`, `AgentDescription`) and `EnableAgent365Exporter: false` to `appsettings.json` (.NET) and `ENABLE_A365_OBSERVABILITY_EXPORTER=false` to `.env` (Python/Node.js), so observability configuration is pre-populated for all three platforms after running setup
- Re-enabled `a365 create-instance` command (previously deprecated) — creates agent identity, agent user, and assigns licenses in a single command. The custom client app now requires the `User.ReadWrite.All` delegated permission for user creation and license assignment; existing users may need to update admin consent on their client app.
- `Agent365.Observability.OtelWrite` granted to all provisioned agent identities on the Observability API as both a **delegated** permission (OAuth2 grant) and an **application** permission (S2S app role assignment), enabling agents to write OpenTelemetry data to the Agent 365 observability service
- S2S app role assignment support in `a365 setup permissions` and `a365 setup admin` — the CLI now automatically grants application-type (`appRoleAssignments`) permissions on the blueprint service principal when a `ResourcePermissionSpec` defines `AppRoleScopes`. Global Administrator is required for S2S grants; non-admin users receive actionable PowerShell fallback instructions
- `ChannelMessage.Read.All` and `ChannelMessage.Send` added to default blueprint Microsoft Graph delegated scopes (`agentIdentityScopes`)
- `Files.ReadWrite.All`, `ChannelMessage.Read.All`, and `ChannelMessage.Send` added to default blueprint Microsoft Graph application scopes (`agentApplicationScopes`)
- Server-driven notice system: security advisories and critical upgrade prompts are displayed at startup when a maintainer updates `notices.json`. Notices are suppressed once the user upgrades past the specified `minimumVersion`. Results are cached locally for 4 hours to avoid network calls on every invocation.
- `a365 cleanup azure --dry-run` — preview resources that would be deleted without making any changes or requiring Azure authentication
- `AppServiceAuthRequirementCheck` — validates App Service deployment token before `a365 deploy` begins, catching revoked grants (AADSTS50173) early
- `a365 setup admin` — new command for Global Administrators to complete tenant-wide AllPrincipals OAuth2 permission grants after `a365 setup all` has been run by an Agent ID Admin
- `setup all --agent-name <name>` — config-free non-DW setup. No `a365.config.json` required. TenantId is auto-detected from `az account show`; ClientAppId resolved by finding an Entra app registration named `"Agent 365 CLI"` in the tenant.
- `setup all --tenant-id <id>` — override tenant auto-detection when using `--agent-name`.
- `cleanup --agent-name <name>` — config-free cleanup. No `a365.config.json` required. Loads resource IDs from the global generated config written by bootstrap setup. Tenant ID is auto-detected from `az account show` or overridden with `--tenant-id`.
- MCP V1/V2 migration support — `a365 setup permissions mcp` and `a365 setup blueprint` now handle mixed manifests containing both V1 (`McpServers.*.All` / ATG audience) and V2 (`Tools.ListInvoke.All` / per-server audience) entries; scopes are written additively to the blueprint so agents on either SDK version continue to work
- `--remove-legacy-scopes` flag for `a365 setup permissions mcp` — removes shared ATG audience scopes from the blueprint once V2 SDK is confirmed live across all agents
- `a365 develop get-token` now acquires one token per audience when using manifest-based scope resolution — V2 entries receive a token scoped to their specific server AppId, V1 entries continue to use the shared ATG AppId
- `a365 develop get-token` now writes per-server bearer tokens to `.env` (Python/Node.js) and `launchSettings.json` (.NET) — V2 servers are written as `BEARER_TOKEN_<SERVER_NAME>` (e.g. `BEARER_TOKEN_MCP_WORDSERVER`), V1 shared-audience token continues to be written as `BEARER_TOKEN` for backward compatibility; local dev samples can now run correctly with V2 multi-audience manifests without needing agentic auth
- `a365 setup permissions mcp --remove-legacy-scopes --dry-run` now shows both what would be removed (shared ATG audience entries) and what would remain after removal, instead of only showing what would be configured

### Changed
- Blueprint messaging endpoint registration migrated from Azure Bot Service (ABS) to Teams Graph backend configuration. The CLI now sends `{ agentIdentityBlueprintId, callbackUri, tenantId }` to MCP Platform instead of the ABS-shaped payload. `BotConfigurator` / `IBotConfigurator` are replaced by `TeamsGraphBackendConfigurator` / `ITeamsGraphBackendConfigurator`. Callers must pass `--m365` to opt in; see Added notes above.
- `setup all --dry-run` output is now column-aligned for readability
- `setup infrastructure` now defaults `deploymentProjectPath` to the current directory when not specified in config
- `setup all` now defaults to the blueprint agent flow. Use `--aiteammate` (no value required) to run the AI Teammate agent setup flow.
- `a365 setup blueprint` now sets `managerApplications` on the blueprint application to enable platform manageability. After May 1, blueprints without `managerApplications` will no longer be accepted, and must be recreated (delete and re-run `a365 setup blueprint`) or manually patched via Graph API to include this value.
- `New-Agent365ToolsServicePrincipalProdPublic.ps1` updated to support MCP V1 and V2 provisioning — adds `-Mode` (`V1`/`V2`/`All`, default `All`), `-ManifestPath` (auto-extracts V2 per-server AppIds from `ToolingManifest.json`), and `-V2AppIds` (explicit list) parameters; script is now idempotent across all AppIds (re-run safe) and covers the migration period where V1 and V2 servers coexist in the same tenant
- `a365 publish` updates manifest IDs, creates `manifest.zip`, and prints concise upload instructions for Microsoft 365 Admin Center (Agents > All agents > Upload custom agent). Interactive prompts only occur in interactive terminals; redirect stdin to suppress them in scripts.
- `a365 develop list-available` resolves MCP server catalog from the live V2 discover endpoint; `--version` column in `a365 develop list-configured` shows `V1` or `V2` based on scope pattern
- `develop list-available` no longer requires `a365.config.json`; reads environment from the `A365_ENVIRONMENT` env var (defaults to `prod`).
- `ToolingManifest.json` duplicate server detection now falls back to `mcpServerName` when `mcpServerUniqueName` is absent, preventing false duplicate errors for older manifest entries

### Fixed
- `setup all` dry-run with `--agent-name` no longer runs az CLI tenant detection — tenant ID is not shown in the plan, so the subprocess was unnecessary
- `setup all` live summary incorrectly showed `Inheritable Permissions: configured` for non-AI Teammate agents — now shows `skipped (permissions set directly on agent identity)`
- `AgentBlueprintService.SetInheritablePermissionsAsync` no longer crashes when the Graph PATCH call throws a transient exception (#366) — the exception is caught, logged, and surfaced as a structured error result
- `cleanup` now returns exit code 1 when no config file and no `--agent-name` are provided, instead of silently reporting success.
- `AgentBlueprintService.SetInheritablePermissionsAsync` now correctly propagates `OperationCanceledException` when the user cancels (Ctrl+C), instead of masking cancellation as a generic error
- `A365CreateInstanceRunner` sponsor handling: sponsor is now required (Graph API rejects requests without one) — removed fallback that silently stripped the sponsor on retry, which caused `BadRequest` errors
- Intermittent `ConnectionResetError (10054)` failures on corporate networks with TLS inspection proxies (Zscaler, Netskope) — Graph and ARM API calls now use direct MSAL.NET token acquisition instead of `az account get-access-token` subprocesses, bypassing the Python HTTP stack that triggered proxy resets (#321)
- `a365 cleanup` blueprint deletion now succeeds for Global Administrators even when the blueprint was created by a different user
- Admin consent URL for the Observability API used the non-existent scope `Maven.ReadWrite.All` (AADSTS650053) — replaced with the correct delegated scope `Agent365.Observability.OtelWrite`
- `AppRoleAssignment.ReadWrite.All` (admin-only) was incorrectly included in `RequiredPermissionGrantScopes`, causing it to be requested on non-admin paths (`a365 deploy`, `setup permissions`) — moved to a dedicated `RequiredS2SGrantScopes` constant used only on Global Administrator paths
- `a365 setup all` no longer times out for non-admin users — the CLI immediately surfaces a consent URL to share with an administrator instead of waiting for a browser prompt
- `a365 setup all` requests admin consent once for all resources instead of prompting once per resource
- Browser and WAM authentication blocked by Conditional Access Policy (AADSTS53003, AADSTS53000) now automatically falls back to device code flow (#294)
- macOS/Linux: device code fallback when browser authentication is unavailable (#309)
- Linux: MSAL fallback when PowerShell `Connect-MgGraph` fails in non-TTY environments (#309)
- Admin consent polling no longer times out after 180s — blueprint service principal now resolved with correct MSAL token (#309)
- `a365 cleanup --agent-name` no longer stalls — interactive browser auth failure in embedded terminals now automatically falls back to device code flow (same as Conditional Access fallback)
- `ConfigFileNotFoundException` now derives from `FileNotFoundException` so existing catch sites continue to work (#309)
- `a365 develop list-available` no longer displays `Required Scope: null` for servers that return a `"null"` string scope from the V2 catalog endpoint
- `a365 develop add-mcp-servers` no longer writes the literal string `"null"` as a scope value in `ToolingManifest.json` when the V2 catalog returns `"scope": "null"` — the field is omitted, allowing correct fallback to name-based scope mapping
- `a365 develop get-token` no longer requests a token with scope `"null"` when a manifest entry has a null scope from the V2 catalog
- `a365 setup permissions mcp` no longer passes a literal `"default"` string as an AAD resourceAppId — Dataverse custom servers (`McpServers.DataverseCustom.All`, `McpServers.Dataverse.All`) with `"audience": "default"` are now bucketed under the shared ATG AppId, the same as missing or `api://` legacy audiences
- `a365 setup blueprint` (non-DW) blueprint service principal creation no longer returns 403 — the CLI now uses `POST /v1.0/serviceprincipals/graph.agentIdentityBlueprintPrincipal` (Agent ID-specific endpoint) instead of the generic `/v1.0/servicePrincipals`, which required `Application.ReadWrite.All`
- `a365 setup all` (non-DW) agent identity idempotency pre-check no longer returns 403 — uses `AgentIdentity.Read.All` scope for `GET /beta/servicePrincipals/microsoft.graph.agentIdentity?$filter=agentIdentityBlueprintId eq '...'`
- Agent registration endpoint promoted from `/stagingbeta/copilot/agentRegistrations` to `/beta/copilot/agentRegistrations`

### Removed
- `a365 create-instance` command — temporarily removed due to differences in instance creation via Teams and CLI.
- `a365 deploy` command (`deploy app`, `deploy mcp`) — Azure App Service hosting is no longer managed by the CLI. Provide a `messagingEndpoint` in `a365.config.json` pointing to your externally hosted agent.
- `a365 setup infrastructure` subcommand — Azure App Service and App Service Plan provisioning has been removed. Hosting infrastructure must be provisioned externally before running `a365 setup all`.
- Config properties `subscriptionId`, `resourceGroup`, `appServicePlanName`, `appServicePlanSku`, `webAppName`, `needDeployment`, `location` — removed from `a365.config.json`. Generated config properties `deploymentLastTimestamp`, `deploymentLastStatus`, `deploymentLastCommitHash`, `deploymentLastBuildId` have also been removed.

## [1.1.0] - 2026-02

### Added
- Custom blueprint permissions configuration and management — configure any resource's OAuth2 grants and inheritable permissions via `a365.config.json` (#298)
- `setup requirements` subcommand with per-category checks: PowerShell modules, location, client app configuration, Frontier Program enrollment (#293)
- `setup permissions copilotstudio` subcommand for Power Platform `CopilotStudio.Copilots.Invoke` permission (#298)
- Persistent MSAL token cache to reduce repeated WAM login prompts on Windows (#261)
- Auto-detect endpoint name from project settings; globally unique names to prevent accidental collisions (#289)
- `.NET` runtime roll-forward — CLI now works on .NET 9 and later without reinstalling (#276)
- Mock tooling server MCP protocol compliance for Python and Node.js agents (#263)

### Fixed
- Prevent `InternalServerError` loop when `--update-endpoint` fails on create (#304)
- Correct endpoint name derivation for `needsDeployment=false` scenarios (#296)
- Browser auth falls back to device code on macOS when WAM/browser is unavailable (#290)
- `PublishCommand` now returns non-zero exit code on all error paths (#266)
- Azure CLI Graph token cached across publish command Graph API calls (#267)
- PowerShell 5.1 install compatibility and macOS auth testability improvements (#292)
- MOS token cache timezone comparison bug in `TryGetCachedToken` (#278)
- Location config validated before endpoint registration and deletion (#281)
- `CustomClientAppId` correctly set in `BlueprintSubcommand` to fix inheritable permissions (#272)
- Endpoint names trimmed of trailing hyphens to comply with Azure Bot Service naming rules (#257)
- Python projects without `pyproject.toml` handled in `a365 deploy` (#253)

## [1.0.0] - 2025-12

### Added
- `a365 setup blueprint` — creates and configures an Agent Identity Blueprint in Azure AD
- `a365 setup permissions mcp` / `bot` — configures OAuth2 grants and inheritable permissions
- `a365 deploy` — multi-platform deployment (`.NET`, `Node.js`, `Python`) with auto-detection
- `a365 config init` — initialize project configuration
- `a365 cleanup` — remove Azure resources and blueprint configuration
- Interactive browser authentication via MSAL with WAM on Windows
- Microsoft Graph operations using PowerShell `Microsoft.Graph` module
- Admin consent polling with automatic detection

[Unreleased]: https://github.com/microsoft/Agent365-devTools/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/microsoft/Agent365-devTools/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/microsoft/Agent365-devTools/releases/tag/v1.0.0
