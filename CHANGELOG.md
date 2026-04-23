# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- `--m365` opt-in flag on `a365 setup blueprint` and `a365 cleanup blueprint` — when set, the CLI registers or clears the agent blueprint's messaging endpoint via the Teams Graph backend configuration endpoint on MCP Platform. Default is **off**: without `--m365`, `--endpoint-only` and `--update-endpoint` skip the API call and print a pointer to the Teams Developer Portal (https://learn.microsoft.com/en-us/microsoft-agent-365/developer/create-instance#1-configure-agent-in-teams-developer-portal) for manual configuration. Intended for M365 agents; the flag is opt-in because the Teams Graph rollout on MCP Platform completes 2026-05-01.
- Rollout-aware contract-mismatch handling — if MCP Platform rejects the new request shape (still on the pre-migration Azure Bot Service contract on that environment), the CLI logs an informational message ("Teams registration not done") rather than a failure, and points the user at the Teams Developer Portal as a manual fallback. This escalates to warning-level once the rollout window closes on 2026-05-01.
- Re-enabled `a365 create-instance` command (previously deprecated) — creates agent identity, agent user, and assigns licenses in a single command. The custom client app now requires the `User.ReadWrite.All` delegated permission for user creation and license assignment; existing users may need to update admin consent on their client app.
- `Agent365.Observability.OtelWrite` scope now granted to all provisioned agent identities on the Observability API alongside `user_impersonation`, enabling agents to write OpenTelemetry data to the Agent 365 observability service
- `ChannelMessage.Read.All` and `ChannelMessage.Send` added to default blueprint Microsoft Graph delegated scopes (`agentIdentityScopes`)
- `Files.ReadWrite.All`, `ChannelMessage.Read.All`, and `ChannelMessage.Send` added to default blueprint Microsoft Graph application scopes (`agentApplicationScopes`)
- Server-driven notice system: security advisories and critical upgrade prompts are displayed at startup when a maintainer updates `notices.json`. Notices are suppressed once the user upgrades past the specified `minimumVersion`. Results are cached locally for 4 hours to avoid network calls on every invocation.
- `a365 cleanup azure --dry-run` — preview resources that would be deleted without making any changes or requiring Azure authentication
- `AppServiceAuthRequirementCheck` — validates App Service deployment token before `a365 deploy` begins, catching revoked grants (AADSTS50173) early
- `a365 setup admin` — new command for Global Administrators to complete tenant-wide AllPrincipals OAuth2 permission grants after `a365 setup all` has been run by an Agent ID Admin
### Changed
- Blueprint messaging endpoint registration migrated from Azure Bot Service (ABS) to Teams Graph backend configuration. The CLI now sends `{ agentIdentityBlueprintId, callbackUri, tenantId }` to MCP Platform instead of the ABS-shaped payload. `BotConfigurator` / `IBotConfigurator` are replaced by `TeamsGraphBackendConfigurator` / `ITeamsGraphBackendConfigurator`. Callers must pass `--m365` to opt in; see Added notes above.
- `a365 publish` updates manifest IDs, creates `manifest.zip`, and prints concise upload instructions for Microsoft 365 Admin Center (Agents > All agents > Upload custom agent). Interactive prompts only occur in interactive terminals; redirect stdin to suppress them in scripts.

### Fixed
- `A365CreateInstanceRunner` sponsor handling: sponsor is now required (Graph API rejects requests without one) — removed fallback that silently stripped the sponsor on retry, which caused `BadRequest` errors
- Intermittent `ConnectionResetError (10054)` failures on corporate networks with TLS inspection proxies (Zscaler, Netskope) — Graph and ARM API calls now use direct MSAL.NET token acquisition instead of `az account get-access-token` subprocesses, bypassing the Python HTTP stack that triggered proxy resets (#321)
- `a365 cleanup` blueprint deletion now succeeds for Global Administrators even when the blueprint was created by a different user
- `a365 setup all` no longer times out for non-admin users — the CLI immediately surfaces a consent URL to share with an administrator instead of waiting for a browser prompt
- `a365 setup all` requests admin consent once for all resources instead of prompting once per resource
- Browser and WAM authentication blocked by Conditional Access Policy (AADSTS53003, AADSTS53000) now automatically falls back to device code flow (#294)
- macOS/Linux: device code fallback when browser authentication is unavailable (#309)
- Linux: MSAL fallback when PowerShell `Connect-MgGraph` fails in non-TTY environments (#309)
- Admin consent polling no longer times out after 180s — blueprint service principal now resolved with correct MSAL token (#309)
- `ConfigFileNotFoundException` now derives from `FileNotFoundException` so existing catch sites continue to work (#309)

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
