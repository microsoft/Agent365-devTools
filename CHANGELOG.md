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

**Option B — CLI** (requires `a365.config.json` and `a365.generated.config.json` in the config directory):

```bash
a365 setup admin --config-dir "<path-to-config-dir>"
```

### Added
- `a365 status` — new command displaying agent configuration and live Entra registration state. Supports `--offline` to skip live Graph lookups, `--field <name>` for machine-readable single-value output, and `--agent-name` / `--tenant-id` for config-free use.
- `--agent-name` and `--tenant-id` options added to `setup blueprint`, `setup permissions` (all subcommands), `create-instance`, `publish`, and `query-entra` — all commands can now resolve configuration from Entra ID without requiring `a365.config.json`.
- `setup permissions custom --resource-app-id <guid> --scopes <scopes>` — apply inline custom API permissions to the agent blueprint without editing `a365.config.json`.

### Removed
- `a365 config` command family (`config init`, `config display`, `config permissions`) — replaced by `a365 setup all --agent-name`, `a365 status`, and `a365 setup permissions custom`.

### Breaking Changes
- **`a365 config init` removed** — replace with `a365 setup all --agent-name <name>`. This creates the agent blueprint, configures permissions, and registers the messaging endpoint in one step without requiring a pre-existing config file.
- **`a365 config display` removed** — replace with `a365 status`. Use `a365 status --field <FieldName>` for scripting (e.g., `a365 status --field AgentBlueprintId`).
- **`a365 config permissions` removed** — replace with `a365 setup permissions custom --resource-app-id <guid> --scopes <scopes>`.

### Added
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
- `setup all --dry-run` output is now column-aligned for readability
- `setup infrastructure` now defaults `deploymentProjectPath` to the current directory when not specified in config
- `setup all` now defaults to the non-AI Teammate (blueprint) flow. Use `--aiteammate true` to run the Digital Worker (AI Teammate) setup flow.
- `a365 setup blueprint` now sets `managerApplications` on the blueprint application to enable platform manageability. After May 1, blueprints without `managerApplications` will no longer be accepted, and must be recreated (delete and re-run `a365 setup blueprint`) or manually patched via Graph API to include this value.
- `New-Agent365ToolsServicePrincipalProdPublic.ps1` updated to support MCP V1 and V2 provisioning — adds `-Mode` (`V1`/`V2`/`All`, default `All`), `-ManifestPath` (auto-extracts V2 per-server AppIds from `ToolingManifest.json`), and `-V2AppIds` (explicit list) parameters; script is now idempotent across all AppIds (re-run safe) and covers the migration period where V1 and V2 servers coexist in the same tenant
- `a365 publish` updates manifest IDs, creates `manifest.zip`, and prints concise upload instructions for Microsoft 365 Admin Center (Agents > All agents > Upload custom agent). Interactive prompts only occur in interactive terminals; redirect stdin to suppress them in scripts.
- `a365 develop list-available` resolves MCP server catalog from the live V2 discover endpoint; `--version` column in `a365 develop list-configured` shows `V1` or `V2` based on scope pattern
- `ToolingManifest.json` duplicate server detection now falls back to `mcpServerName` when `mcpServerUniqueName` is absent, preventing false duplicate errors for older manifest entries

### Fixed
- `AgentBlueprintService.SetInheritablePermissionsAsync` no longer crashes when the Graph PATCH call throws a transient exception (#366) — the exception is caught, logged, and surfaced as a structured error result
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

### Removed
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
