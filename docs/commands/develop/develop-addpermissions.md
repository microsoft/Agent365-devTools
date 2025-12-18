# a365 develop add-permissions Command

## Overview

The `a365 develop add-permissions` command adds MCP (Model Context Protocol) server API permissions to Azure AD applications. This command is designed for **development scenarios** where you need to configure custom applications (not agent blueprints) to access MCP servers.

## Usage

```bash
a365 develop add-permissions [options]
```

## Options

| Option | Alias | Description | Default |
|--------|-------|-------------|---------|
| `--config` | `-c` | Configuration file path | `a365.config.json` |
| `--manifest` | `-m` | Path to ToolingManifest.json | `<deploymentProjectPath>/ToolingManifest.json` |
| `--app-id` | | Application (client) ID to add permissions to | `clientAppId` from config |
| `--scopes` | | Specific scopes to add (space-separated) | All scopes from ToolingManifest.json |
| `--verbose` | `-v` | Show detailed output | `false` |
| `--dry-run` | | Show what would be done without making changes | `false` |

## When to Use This Command

### Development Scenarios
- Custom backend services needing MCP access
- Testing applications with specific MCP permissions
- Third-party integrations calling MCP servers

### NOT for Agent Blueprints
- Use `a365 setup permissions mcp` for agent blueprint setup

## Understanding the Application ID

This command adds permissions to a **single application**, which you can specify in two ways:

1. **Using config file** (default): `clientAppId` from `a365.config.json`
2. **Using command line**: `--app-id` parameter (overrides config)

The application you're adding permissions to can be the **same application** you use for authentication (your custom client app). This is the typical scenario:
- Your custom client app authenticates to Microsoft Graph API
- The same app needs MCP permissions added to it
- You can reuse the same `clientAppId` for both purposes

**Example**: If your `a365.config.json` has `clientAppId: "12345678-..."`, running `a365 develop add-permissions` will add MCP permissions to that same application.

> **Note**: The `clientAppId` must be a **client application you create in your Entra ID tenant** with `Application.ReadWrite.All` permission. See the [custom client app registration guide](../../guides/custom-client-app-registration.md) for setup instructions.

## Prerequisites

1. **Azure CLI Authentication**: `az login` with appropriate permissions
2. **Client Application**: 
   - Must exist in Azure AD
   - Must have `Application.ReadWrite.All` permission (to modify app registrations)
   - Can be configured in `a365.config.json` as `clientAppId` OR provided via `--app-id`

## ToolingManifest.json Structure

```json
{
  "mcpServers": [
    {
      "mcpServerName": "mcp_MailTools",
      "url": "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailTools",
      "scope": "McpServers.Mail.All",
      "audience": "api://mcp-mailtools"
    },
    {
      "mcpServerName": "mcp_CalendarTools",
      "url": "https://agent365.svc.cloud.microsoft/agents/servers/mcp_CalendarTools",
      "scope": "McpServers.Calendar.All",
      "audience": "api://mcp-calendartools"
    }
  ]
}
```

## Examples

### Add all scopes from manifest to the app in config
```bash
# Uses clientAppId from a365.config.json as the target application
a365 develop add-permissions
```

### Add permissions to a different application
```bash
# Override the config and add permissions to a different app
a365 develop add-permissions --app-id 87654321-4321-4321-4321-210987654321
```

### Add specific scopes only
```bash
# Add only specific scopes to the app from config
a365 develop add-permissions --scopes McpServers.Mail.All McpServers.Calendar.All
```

### Combine options with dry-run
```bash
# Preview changes to a specific app with specific scopes
a365 develop add-permissions --app-id 12345678-1234-1234-1234-123456789abc --scopes McpServers.Mail.All --dry-run
```

### Without config file
```bash
# When no config exists, you must provide --app-id
a365 develop add-permissions --app-id 12345678-1234-1234-1234-123456789abc --scopes McpServers.Mail.All
```