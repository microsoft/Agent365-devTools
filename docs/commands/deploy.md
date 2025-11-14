# Agent365 CLI – Deploy Command Guide

> **Command**: `a365 deploy`  
> **Purpose**: Deploy your application to Azure App Service **and** update Agent365 Tool (MCP) permissions in one run. Subcommands let you run either phase independently.
---

## TL;DR

```bash
# Full two-phase deploy (App binaries, then MCP permissions)
a365 deploy

# App-only deploy
a365 deploy app

# MCP-only permissions update
a365 deploy mcp

# Common flags
a365 deploy app --restart     # reuse existing publish/ (skip build)
a365 deploy app --inspect     # pause to review publish/ and zip
a365 deploy --dry-run     # print actions, no changes
a365 deploy --verbose     # detailed logs
```

---

## What the command actually does

### Default (`a365 deploy`)
Runs **two phases sequentially**:

**Step 1 — App Binaries**
1. Load `a365.config.json` (+ dynamic state from generated config).
2. **Azure preflight**  
   - Validates Azure CLI auth + subscription context (`ValidateAllAsync`).  
   - Ensures target Web App exists via `az webapp show`.
3. Build/package via `DeploymentService.DeployAsync(...)` (supports `--inspect` and `--restart`).
4. Log success/failure.

**Step 2 — MCP Permissions**
1. Re-load config (same path).
2. Read required scopes from `deploymentProjectPath/toolingManifest.json`.
3. Apply **in order**:
   - **OAuth2 grant**: `ReplaceOauth2PermissionGrantAsync`
   - **Inheritable permissions**: `SetInheritablePermissionsAsync`
   - **Admin consent (agent identity)**: `ReplaceOauth2PermissionGrantAsync`
4. Log success/failure.

---

## Subcommands & Flags

### `a365 deploy` (default, two-phase)
- **Options**: `--config|-c`, `--verbose|-v`, `--dry-run`, `--inspect`, `--restart`
- **Behavior**: Runs **App** then **MCP**, prints “Part 1…” and “Part 2…” sections (even on `--dry-run`).

### `a365 deploy app` (app-only)
- **Options**: `--config|-c`, `--verbose|-v`, `--dry-run`, `--inspect`, `--restart`
- **Behavior**: Only runs the App phase (includes the same Azure validations and Web App existence check).

### `a365 deploy mcp` (MCP-only)
- **Options**: `--config|-c`, `--verbose|-v`, `--dry-run`
- **Behavior**: Only runs the MCP permissions sequence (no `--inspect` or `--restart` here).

---

## Preflight Checks

- **Azure auth & subscription**: Validated via `ValidateAllAsync(subscriptionId)`.  
  If invalid, deployment is stopped with a clear error.
- **Web App existence**: `az webapp show --resource-group <rg> --name <app> --subscription <sub>` must succeed before app deploy proceeds.

---

## Configuration Inputs

- **`a365.config.json`** (user-maintained) and **`a365.generated.config.json`** (dynamic state)
- **Tooling scopes**: Read from `<deploymentProjectPath>/toolingManifest.json` during the MCP phase
- `--config` defaults to `a365.config.json` in the current directory

> The CLI also keeps **global** copies of config/state in:
> - Windows: `%LocalAppData%\Microsoft.Agents.A365.DevTools.Cli`
> - Linux/macOS: `$HOME/.config/Microsoft.Agents.A365.DevTools.Cli`
---

## Flags (behavior details)

- `--restart`  
  Skip a fresh build and start from compressing the **existing** `publish/` folder. If `publish/` is missing, the deploy fails with guidance to run a full deploy.

- `--inspect`  
  Pause before upload so you can inspect `publish/` and the generated ZIP. (App phase only.)

- `--dry-run`  
  Print everything that would happen. The default command shows **two sections**:
  - *Part 1 — Deploy application binaries* (target RG/app, config path)
  - *Part 2 — Deploy/update Agent 365 Tool permissions* (the three MCP steps)
  No changes are made.

- `--verbose`  
  Enables detailed logging in both phases.

---

## MCP Permission Update Flow (exact order)

When running `a365 deploy` or `a365 deploy mcp`:

1. **OAuth2 permission grant**  
   `ReplaceOauth2PermissionGrantAsync(tenant, blueprintSp, mcpPlatformSp, scopes)`

2. **Inheritable permissions**  
   `SetInheritablePermissionsAsync(tenant, agentBlueprintAppId, mcpResourceAppId, scopes)`

3. **Admin consent** (agent identity → MCP platform)  
   `ReplaceOauth2PermissionGrantAsync(tenant, agenticAppSpObjectId, mcpPlatformResourceSpObjectId, scopes)`

> All scopes are sourced from `toolingManifest.json` in your project root.
---

## Typical Flows

### Full two-phase deploy with visibility
```bash
a365 deploy --verbose
```

### Quick iteration (reuse last build)
```bash
a365 deploy app --restart
```

### MCP only (permissions/scopes refresh)
```bash
a365 deploy mcp --verbose
```

### Validate everything without changing anything
```bash
a365 deploy app --dry-run --inspect
```

---

## Troubleshooting

- **“Not logged into Azure” or wrong subscription**  
  Fix with `az login --tenant <TENANT_ID>` and `az account set --subscription <SUBSCRIPTION_ID>`.

- **Web App not found**  
  Ensure `a365 setup` has been run or correct `webAppName`/`resourceGroup` are in `a365.config.json`.

- **Permissions update fails**  
  - Confirm `AgentBlueprintId`, `AgenticAppId`, and environment are set in config.
  - Ensure your account has rights to manage service principals and grants.
  - Verify `toolingManifest.json` exists and contains valid scopes.

- **`--restart` fails**  
  Run a full build once (no `--restart`) to produce `publish/`.

---

## Logs

**CLI logs**
- Windows: `%LocalAppData%\Microsoft.Agents.A365.DevTools.Cli\logs\`
- Linux/macOS: `~/.config/a365/logs/`

Tail latest deploy logs:
```powershell
# Windows
Get-Content $env:LOCALAPPDATA\Microsoft.Agents.A365.DevTools.Cli\logs\a365.deploy.log -Tail 80
```

```bash
# Linux/Mac
tail -80 ~/.config/a365/logs/a365.deploy.log
```

**App Service logs**
Use Log Stream in the Azure Portal for runtime stdout/stderr.

---

## Related

- [`a365 setup`](./setup.md) — provision resources and register the messaging endpoint
- [`a365 create-instance`](./create-instance.md) — create agent identity/user and licenses
- [`a365 config init`](./config-init.md) — initialize configuration

---
