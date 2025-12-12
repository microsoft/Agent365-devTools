# a365 develop gettoken Command

## Overview

The `a365 develop gettoken` command retrieves bearer tokens for testing MCP servers during development. This command acquires tokens with explicit scopes using interactive browser authentication.

> **Note**: For production agent deployments, authentication is handled automatically through inheritable permissions configured during `a365 setup permissions mcp`. This command is for development testing and debugging.

## Usage

```bash
a365 develop gettoken [options]
```

## Options

| Option | Alias | Description | Default |
|--------|-------|-------------|---------|
| `--config` | `-c` | Configuration file path | `a365.config.json` |
| `--app-id` | | Application (client) ID for authentication | `clientAppId` from config |
| `--manifest` | `-m` | Path to ToolingManifest.json | `<deploymentProjectPath>/ToolingManifest.json` |
| `--scopes` | | Specific scopes to request (space-separated) | Read from ToolingManifest.json |
| `--output` | `-o` | Output format: table, json, or raw | `table` |
| `--verbose` | `-v` | Show detailed output including full token | `false` |
| `--force-refresh` | | Force token refresh bypassing cache | `false` |

## When to Use This Command

### Development & Testing
- Local development and debugging
- Manual API testing with Postman/curl
- Integration testing before deployment

### NOT for Production
- Production agents use inheritable permissions (`a365 setup permissions mcp`)

## Understanding the Application ID

This command retrieves tokens for a **single application**, which you can specify in two ways:

1. **Using config file** (default): `clientAppId` from `a365.config.json`
2. **Using command line**: `--app-id` parameter (overrides config)

The application you're getting a token for should be your **custom client app** that has the required MCP permissions. This is typically the same application you use across development commands.

**Example**: If your `a365.config.json` has `clientAppId: "12345678-..."`, running `a365 develop gettoken` will retrieve a token for that application.

> **Note**: For more details about the client application setup and how it's used across development commands, see the [develop addpermissions documentation](./develop-addpermissions.md#understanding-the-application-id).

## Prerequisites

1. **Azure CLI**: Run `az login` before using this command
2. **Client Application**: 
   - Must exist in Azure AD
   - Must have the required MCP scopes configured
   - Can be configured in `a365.config.json` as `clientAppId` OR provided via `--app-id`
3. **ToolingManifest.json** (optional): Can be bypassed with `--scopes` parameter

## ToolingManifest.json Structure

```json
{
  "mcpServers": [
    {
      "mcpServerName": "mcp_MailTools",
      "scope": "McpServers.Mail.All"
    },
    {
      "mcpServerName": "mcp_CalendarTools",
      "scope": "McpServers.Calendar.All"
    }
  ]
}
```

## Examples

### Get token with all scopes from manifest
```bash
a365 develop gettoken
```

### Get token with specific scopes
```bash
a365 develop gettoken --scopes McpServers.Mail.All McpServers.Calendar.All
```

### Get token with custom client app
```bash
a365 develop gettoken --app-id 98765432-4321-4321-4321-210987654321
```

### Export token to file
```bash
a365 develop gettoken --output raw > token.txt
```

### Use token in curl request
```bash
TOKEN=$(a365 develop gettoken --output raw)
curl -H "Authorization: Bearer $TOKEN" https://agent365.svc.cloud.microsoft/agents/discoverToolServers
```

## Authentication Flow

1. **Application Selection**: Uses `--app-id` or `clientAppId` from config
2. **Scope Resolution**: Uses `--scopes` or reads from `ToolingManifest.json`
3. **Token Acquisition**: Opens browser for interactive OAuth2 authentication
4. **Token Caching**: Cached in local storage for reuse (until expiration or `--force-refresh`)