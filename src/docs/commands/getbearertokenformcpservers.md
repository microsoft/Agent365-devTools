# GetBearerTokenForMcpServers Command

## Overview

The `getbearertokenformcpservers` command retrieves a bearer token for accessing MCP (Model Context Protocol) servers through the Agent 365 Tools resource. The command acquires **a single token** with all required scopes for accessing multiple MCP servers.

**By default**, the command reads all unique scopes from your `ToolingManifest.json` file and requests a token with those scopes. You can optionally use the `--scopes` parameter to specify exactly which scopes you need.

## Usage

```bash
a365 getbearertokenformcpservers [options]
```

## Options

| Option | Alias | Description | Default |
|--------|-------|-------------|---------|
| `--config` | `-c` | Configuration file path | `a365.config.json` |
| `--manifest` | `-m` | Path to ToolingManifest.json | `<deploymentProjectPath>/ToolingManifest.json` |
| `--scopes` | | (Optional) Specific scopes to request (space-separated). If not provided, uses all unique scopes from ToolingManifest.json | Read from ToolingManifest.json |
| `--output` | `-o` | Output format: table, json, or raw | `table` |
| `--verbose` | `-v` | Show detailed output including full token | `false` |
| `--force-refresh` | | Force token refresh even if cached token is valid | `false` |

## Output Formats

### Table Format (Default)

Displays token information in a human-readable format:

```
=== MCP Server Bearer Token ===

Agent 365 Tools Resource App ID: ea9ffc3e-8a23-4a7d-836d-234d7c7565c1
Requesting scopes: McpServers.Mail.All, McpServers.Calendar.All

Acquiring access token...
? Token acquired successfully

Server: Agent 365 Tools (All MCP Servers)
  URL: https://agent365.svc.cloud.microsoft/agents/discoverToolServers
  Scope: McpServers.Mail.All, McpServers.Calendar.All
  Audience: ea9ffc3e-8a23-4a7d-836d-234d7c7565c1
  Status: ? Success
  Expires: ~2025-01-15 14:30:00
  Token: eyJ0eXAiOiJKV1QiLCJhbGci... (use --verbose to see full token)
```

### JSON Format

Outputs structured JSON suitable for automation and scripting:

```bash
a365 getbearertokenformcpservers --output json
```

```json
[
  {
    "serverName": "Agent 365 Tools (All MCP Servers)",
    "url": "https://agent365.svc.cloud.microsoft/agents/discoverToolServers",
    "scope": "McpServers.Mail.All, McpServers.Calendar.All",
    "audience": "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1",
    "success": true,
    "token": "eyJ0eXAiOiJKV1QiLCJhbGci...",
    "expiresOn": "2025-01-15T14:30:00.000Z",
    "error": null
  }
]
```

### Raw Format

Outputs only the bearer token, useful for piping to other commands:

```bash
a365 getbearertokenformcpservers --output raw
```

```
eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiIsIng1dCI6...
```

With `--verbose`, includes metadata as comments:

```bash
a365 getbearertokenformcpservers --output raw --verbose
```

```
# Agent 365 Tools (All MCP Servers)
# Scope: McpServers.Mail.All, McpServers.Calendar.All
# Audience: ea9ffc3e-8a23-4a7d-836d-234d7c7565c1
eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiIsIng1dCI6...
```

## Examples

### Get token with all scopes from ToolingManifest.json (default behavior)

This is the most common usage - reads all unique scopes from your ToolingManifest.json:

```bash
a365 getbearertokenformcpservers
```

### Get token with specific scopes (override manifest)

Use `--scopes` to request only specific scopes instead of reading from ToolingManifest.json:

```bash
a365 getbearertokenformcpservers --scopes McpServers.Mail.All McpServers.Calendar.All
```

### Get token for a single scope

```bash
a365 getbearertokenformcpservers --scopes McpServers.Mail.All
```

### Get token with custom manifest path

```bash
a365 getbearertokenformcpservers --manifest ./custom/path/ToolingManifest.json
```

### Get token in JSON format for automation

```bash
a365 getbearertokenformcpservers --output json
```

### Get token with verbose output (show full token)

```bash
a365 getbearertokenformcpservers --verbose
```

### Force token refresh (bypass cache)

```bash
a365 getbearertokenformcpservers --force-refresh
```

### Export token to a file

```bash
a365 getbearertokenformcpservers --output raw > token.txt
```

### Use token in curl request

```bash
TOKEN=$(a365 getbearertokenformcpservers --output raw)
curl -H "Authorization: Bearer $TOKEN" https://agent365.svc.cloud.microsoft/agents/...
```

### Specify multiple scopes for different MCP servers

```bash
# Get token with mail and calendar scopes
a365 getbearertokenformcpservers --scopes McpServers.Mail.All McpServers.Calendar.All McpServers.Teams.All
```

## Prerequisites

1. **Configuration File**: Valid `a365.config.json` with tenant information
2. **ToolingManifest.json** (required by default): The command reads scopes from this file unless you use `--scopes` parameter
3. **Authentication**: Interactive authentication (browser or device code flow) on first use

## ToolingManifest.json Structure

By default, the command reads scopes from `ToolingManifest.json`. Here's the expected structure:

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

**Note**: The `audience` field represents each MCP server's own audience, but is not used for authentication by this command. Authentication happens against the **Agent 365 Tools** resource using the scopes specified.

**Scope Collection**: The command collects all **unique scopes** from all MCP servers defined in the manifest. For example, if you have 10 MCP servers but only 3 unique scopes, the token will include those 3 scopes.

**Using --scopes to override**: If you provide the `--scopes` parameter, the command ignores ToolingManifest.json and uses only the scopes you specify.

## Authentication Flow

1. **Scope Determination**:
   - **Default behavior**: Reads all unique scopes from `ToolingManifest.json`
   - **With --scopes**: Uses only the scopes you specify, ignoring ToolingManifest.json
   - Example: If ToolingManifest.json has 5 servers with scopes [Mail.All, Mail.All, Calendar.All, Teams.All, Calendar.All], the command requests 3 unique scopes: Mail.All, Calendar.All, Teams.All

2. **Resource Identification**:
   - Command targets the **Agent 365 Tools** resource (App ID: `ea9ffc3e-8a23-4a7d-836d-234d7c7565c1` for production)
   - This is a unified resource that provides access to all MCP servers

3. **Token Acquisition**:
   - Checks token cache first
   - If no valid cached token exists, prompts for interactive authentication
   - Acquires **ONE token** for Agent 365 Tools with all specified scopes
   - Token typically valid for 1 hour

4. **OAuth2 Pattern**:
   - Uses `{resourceAppId}/.default` scope format
   - All specified scopes must be pre-consented in Azure AD for your application
   - Single token contains all necessary permissions for multiple MCP servers

**Example**: If you request 5 different scopes (`McpServers.Mail.All`, `McpServers.Calendar.All`, etc.), the command acquires **ONE token** with all 5 scopes, not 5 separate tokens.

## Token Caching

The token is automatically cached in:
- **Windows**: `%LocalAppData%\Microsoft.Agents.A365.DevTools.Cli\auth-token.json`
- **Linux/Mac**: `~/.local/share/Microsoft.Agents.A365.DevTools.Cli/auth-token.json`

Cache structure (single entry for Agent 365 Tools resource):
```json
{
  "Tokens": {
    "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1": {
      "AccessToken": "eyJ0eXAiOiJKV1QiLCJ...",
      "ExpiresOn": "2025-01-15T15:30:00Z",
      "TenantId": "..."
    }
  }
}
```

**Cache Key**: The resource App ID (Agent 365 Tools), not individual MCP server audiences.

Use `--force-refresh` to bypass the cache and acquire a new token.

## Error Handling

The command provides clear error messages for common issues:

- **ToolingManifest.json not found** (when --scopes not specified): Indicates where the file is expected and suggests using --scopes
- **No scopes found**: When neither --scopes nor ToolingManifest.json provides scopes
- **Token acquisition failure**: Shows authentication error details
- **Authentication errors**: Guides user through authentication process

Exit codes:
- `0`: Success (token acquired)
- `1`: Failure (token acquisition failed)

## Use Cases

### Development Testing

Test authentication and access to MCP servers during development:

```bash
a365 getbearertokenformcpservers --verbose
```

### CI/CD Automation

Acquire tokens in automated pipelines:

```bash
# Get token in JSON format for parsing
a365 getbearertokenformcpservers --output json > token.json

# Extract token using jq
MCP_TOKEN=$(a365 getbearertokenformcpservers --output raw)
echo "MCP_BEARER_TOKEN=$MCP_TOKEN" >> $GITHUB_ENV
```

### Manual API Testing

Get tokens for manual testing with tools like Postman or curl:

```bash
# Copy token to clipboard (Windows)
a365 getbearertokenformcpservers --output raw | clip

# Copy token to clipboard (Mac)
a365 getbearertokenformcpservers --output raw | pbcopy

# Copy token to clipboard (Linux)
a365 getbearertokenformcpservers --output raw | xclip -selection clipboard
```

### Debugging Authentication Issues

Use verbose mode to troubleshoot authentication problems:

```bash
a365 getbearertokenformcpservers --verbose --force-refresh
```

### Requesting Specific Scopes

Get token with only the scopes you need:

```bash
# Token for mail operations only
a365 getbearertokenformcpservers --scopes McpServers.Mail.All

# Token for multiple scopes
a365 getbearertokenformcpservers --scopes McpServers.Mail.All McpServers.Calendar.All McpServers.Teams.All
```

## Related Commands

- `a365 setup permissions mcp`: Configure MCP server permissions
- `a365 develop list-available`: List available MCP servers
- `a365 config display`: View current configuration

## Security Considerations

1. **Token Storage**: Tokens are cached on disk - ensure proper file system permissions
2. **Token Lifetime**: Tokens typically expire after 1 hour
3. **Verbose Mode**: Use with caution in shared environments (exposes full tokens)
4. **Raw Output**: Be careful when piping tokens to files or logs

## Troubleshooting

### "ToolingManifest.json not found"

**Solution 1**: Ensure the file exists in your project directory or specify the path with `--manifest`:
```bash
a365 getbearertokenformcpservers --manifest ./path/to/ToolingManifest.json
```

**Solution 2**: Use `--scopes` to bypass the need for ToolingManifest.json:
```bash
a365 getbearertokenformcpservers --scopes McpServers.Mail.All McpServers.Calendar.All
```

### "No scopes found in ToolingManifest.json"

The manifest file exists but doesn't contain any MCP servers with scopes defined. Use `--scopes` to specify them explicitly:
```bash
a365 getbearertokenformcpservers --scopes McpServers.Mail.All
```

### "Failed to acquire token"

- Check your Azure AD credentials
- Verify network connectivity
- Ensure the scopes are pre-consented in your tenant
- Try `--force-refresh` to bypass cache
- Verify you have the correct permissions in Azure AD

### Authentication prompt appears unexpectedly

The cached token may have expired. Complete the authentication flow to refresh it, or use `--force-refresh` to force a new token acquisition.

## Implementation Details

The command is implemented in:
- **Command**: `Microsoft.Agents.A365.DevTools.Cli/Commands/GetBearerTokenForMcpServersCommand.cs`
- **Tests**: `Tests/Microsoft.Agents.A365.DevTools.Cli.Tests/Commands/GetBearerTokenForMcpServersCommandTests.cs`
- **Authentication**: Uses `AuthenticationService` for token acquisition
- **Configuration**: Uses `ConfigService` for config file management
- **Token Cache**: Managed by `AuthenticationService`
