# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- Server-driven notice system: security advisories and critical upgrade prompts are displayed at startup when a maintainer updates `notices.json`. Notices are suppressed once the user upgrades past the specified `minimumVersion`. Results are cached locally for 4 hours to avoid network calls on every invocation.
- `a365 cleanup azure --dry-run` — preview resources that would be deleted without making any changes or requiring Azure authentication
- `AppServiceAuthRequirementCheck` — validates App Service deployment token before `a365 deploy` begins, catching revoked grants (AADSTS50173) early
- `a365 setup admin` — new command for Global Administrators to complete tenant-wide AllPrincipals OAuth2 permission grants after `a365 setup all` has been run by an Agent ID Admin
- MCP V1/V2 migration support — `a365 setup permissions mcp` and `a365 setup blueprint` now handle mixed manifests containing both V1 (`McpServers.*.All` / ATG audience) and V2 (`Tools.ListInvoke.All` / per-server audience) entries; scopes are written additively to the blueprint so agents on either SDK version continue to work
- `--remove-legacy-scopes` flag for `a365 setup permissions mcp` — removes shared ATG audience scopes from the blueprint once V2 SDK is confirmed live across all agents
- `a365 develop get-token` now acquires one token per audience when using manifest-based scope resolution — V2 entries receive a token scoped to their specific server AppId, V1 entries continue to use the shared ATG AppId
- `a365 develop get-token` now writes per-server bearer tokens to `.env` (Python/Node.js) and `launchSettings.json` (.NET) — V2 servers are written as `BEARER_TOKEN_<SERVER_NAME>` (e.g. `BEARER_TOKEN_MCP_WORDSERVER`), V1 shared-audience token continues to be written as `BEARER_TOKEN` for backward compatibility; local dev samples can now run correctly with V2 multi-audience manifests without needing agentic auth
- `a365 setup permissions mcp --remove-legacy-scopes --dry-run` now shows both what would be removed (shared ATG audience entries) and what would remain after removal, instead of only showing what would be configured

### Changed
- `a365 publish` updates manifest IDs, creates `manifest.zip`, and prints concise upload instructions for Microsoft 365 Admin Center (Agents > All agents > Upload custom agent). Interactive prompts only occur in interactive terminals; redirect stdin to suppress them in scripts.
- `a365 develop list-available` resolves MCP server catalog from the live V2 discover endpoint; `--version` column in `a365 develop list-configured` shows `V1` or `V2` based on scope pattern
- `ToolingManifest.json` duplicate server detection now falls back to `mcpServerName` when `mcpServerUniqueName` is absent, preventing false duplicate errors for older manifest entries

### Fixed
- Intermittent `ConnectionResetError (10054)` failures on corporate networks with TLS inspection proxies (Zscaler, Netskope) — Graph and ARM API calls now use direct MSAL.NET token acquisition instead of `az account get-access-token` subprocesses, bypassing the Python HTTP stack that triggered proxy resets (#321)
- `a365 cleanup` blueprint deletion now succeeds for Global Administrators even when the blueprint was created by a different user
- `a365 setup all` no longer times out for non-admin users — the CLI immediately surfaces a consent URL to share with an administrator instead of waiting for a browser prompt
- `a365 setup all` requests admin consent once for all resources instead of prompting once per resource
- Browser and WAM authentication blocked by Conditional Access Policy (AADSTS53003, AADSTS53000) now automatically falls back to device code flow (#294)
- macOS/Linux: device code fallback when browser authentication is unavailable (#309)
- Linux: MSAL fallback when PowerShell `Connect-MgGraph` fails in non-TTY environments (#309)
- Admin consent polling no longer times out after 180s — blueprint service principal now resolved with correct MSAL token (#309)
- `ConfigFileNotFoundException` now derives from `FileNotFoundException` so existing catch sites continue to work (#309)
- `a365 develop list-available` no longer displays `Required Scope: null` for servers that return a `"null"` string scope from the V2 catalog endpoint
- `a365 develop add-mcp-servers` no longer writes the literal string `"null"` as a scope value in `ToolingManifest.json` when the V2 catalog returns `"scope": "null"` — the field is omitted, allowing correct fallback to name-based scope mapping
- `a365 develop get-token` no longer requests a token with scope `"null"` when a manifest entry has a null scope from the V2 catalog
- `a365 setup permissions mcp` no longer passes a literal `"default"` string as an AAD resourceAppId — Dataverse custom servers (`McpServers.DataverseCustom.All`, `McpServers.Dataverse.All`) with `"audience": "default"` are now bucketed under the shared ATG AppId, the same as missing or `api://` legacy audiences

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
