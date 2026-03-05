# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Fixed
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
