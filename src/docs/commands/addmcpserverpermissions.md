# AddMcpServerPermissions Command

## Overview

The `addmcpserverpermissions` command adds MCP (Model Context Protocol) server API permissions to a custom Azure AD application. This command reads the `ToolingManifest.json` file and configures the required API permissions in the application's manifest, making them visible in the Azure Portal's "API permissions" blade.

**Use Case**: When you have a custom Azure AD application (not the agent blueprint) that needs to access MCP servers, this command automates the process of adding the necessary API permissions to that application's manifest.

## Usage

```bash
a365 addmcpserverpermissions [options]
```

## Options

| Option | Alias | Description | Default |
|--------|-------|-------------|---------|
| `--config` | `-c` | Configuration file path | `a365.config.json` |
| `--manifest` | `-m` | Path to ToolingManifest.json | `<deploymentProjectPath>/ToolingManifest.json` |
| `--app-id` | | Application (client) ID to add permissions to | Blueprint ID from config |
| `--scopes` | | Specific scopes to add (space-separated). If not provided, uses all unique scopes from ToolingManifest.json | All scopes from ToolingManifest.json |
| `--verbose` | `-v` | Show detailed output including error stack traces | `false` |
| `--dry-run` | | Show what would be done without making changes | `false` |

## How It Works

1. **Application Target**: 
   - If `--app-id` is provided, adds permissions to that application
   - Otherwise, uses the `AgentBlueprintId` from the configuration file

2. **Scope Resolution**:
   - If `--scopes` is provided, uses those explicit scopes
   - Otherwise, reads all unique scopes from `ToolingManifest.json`

3. **Audience Detection**:
   - Reads all unique `audience` values from MCP servers in ToolingManifest.json
   - Each unique audience represents a separate resource API requiring permissions

4. **Permission Addition**:
   - For each unique audience, adds the specified scopes to the application's `requiredResourceAccess` collection
   - This makes the permissions visible in Azure Portal under "API permissions"

5. **Result**:
   - Permissions appear in the Azure Portal's API permissions blade
   - Admin consent may still be required after adding permissions
   - The application can now request tokens for the configured MCP server APIs

## Prerequisites

1. **Authentication**: Azure CLI authenticated with sufficient permissions
   - Requires `Application.ReadWrite.All` permission to modify application manifests
   - Typically requires Global Administrator, Application Administrator, or Cloud Application Administrator role

2. **Configuration File**: Valid `a365.config.json` with tenant information

3. **ToolingManifest.json** (required by default): Contains MCP server definitions with scopes and audiences

4. **Target Application**: The application must exist in Azure AD

## ToolingManifest.json Structure

The command expects a `ToolingManifest.json` file with MCP server definitions:

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
      "mcpServerName": "mcp_MailAttachments",
      "url": "https://agent365.svc.cloud.microsoft/agents/servers/mcp_MailAttachments",
      "scope": "McpServers.Mail.ReadWrite",
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

**Key Fields**:
- `scope`: The permission scope name (e.g., `McpServers.Mail.All`)
- `audience`: The resource API identifier (e.g., `api://mcp-mailtools`)

**Scope Deduplication**: The command automatically deduplicates scopes. In the example above, if multiple servers share the same scope, it's only added once.

**Audience Grouping**: Permissions are grouped by audience. In the example above:
- `api://mcp-mailtools` gets 2 scopes: `McpServers.Mail.All`, `McpServers.Mail.ReadWrite`
- `api://mcp-calendartools` gets 1 scope: `McpServers.Calendar.All`

## Examples

### Add all MCP server permissions from ToolingManifest.json

Uses all unique scopes and audiences from your ToolingManifest.json:

```bash
a365 addmcpserverpermissions
```

**What happens**:
- Reads all MCP servers from ToolingManifest.json
- Extracts unique scopes and audiences
- Adds permissions to the blueprint application from config
- Groups permissions by audience (resource API)

### Add permissions to a specific application

Specify a custom application ID instead of using the blueprint:

```bash
a365 addmcpserverpermissions --app-id 12345678-1234-1234-1234-123456789abc
```

**Use case**: You have a separate application (not the agent blueprint) that needs MCP server access.

### Add specific scopes only

Override ToolingManifest.json and specify exact scopes:

```bash
a365 addmcpserverpermissions --scopes McpServers.Mail.All McpServers.Calendar.All
```

**What happens**:
- Ignores scopes from ToolingManifest.json
- Uses only the specified scopes
- Still reads audiences from ToolingManifest.json to determine resource APIs
- Adds the specified scopes to each discovered audience

### Add specific scopes to a custom application

Combine custom app ID with explicit scopes:

```bash
a365 addmcpserverpermissions --app-id 12345678-1234-1234-1234-123456789abc --scopes McpServers.Mail.All
```

**Use case**: Add only mail permissions to a custom application.

### Dry run - preview changes without applying

See what would be added without making changes:

```bash
a365 addmcpserverpermissions --dry-run
```

**Output example**:
```
DRY RUN: Add MCP Server Permissions
Would add the following permissions to application 87654321-4321-4321-4321-210987654321:

Resource: api://mcp-mailtools
  Scopes: McpServers.Mail.All, McpServers.Mail.ReadWrite

Resource: api://mcp-calendartools
  Scopes: McpServers.Calendar.All

No changes made (dry run mode)
```

### Verbose output with detailed logging

Show detailed information including error stack traces:

```bash
a365 addmcpserverpermissions --verbose
```

### Custom manifest path

Specify a different ToolingManifest.json location:

```bash
a365 addmcpserverpermissions --manifest ./custom/path/ToolingManifest.json
```

### Combine multiple options

```bash
a365 addmcpserverpermissions \
  --app-id 12345678-1234-1234-1234-123456789abc \
  --scopes McpServers.Mail.All McpServers.Calendar.All \
  --verbose \
  --dry-run
```

## Output Examples

### Successful execution

```
Adding MCP server permissions to application...

Target Application ID (from config): 87654321-4321-4321-4321-210987654321
Reading MCP server configuration from: D:\MyProject\ToolingManifest.json
Collected 3 unique scope(s) from manifest: McpServers.Mail.All, McpServers.Mail.ReadWrite, McpServers.Calendar.All
Found 2 unique audience(s): api://mcp-mailtools, api://mcp-calendartools

Adding permissions to application...

Processing audience: api://mcp-mailtools
  ? Successfully added permissions for api://mcp-mailtools

Processing audience: api://mcp-calendartools
  ? Successfully added permissions for api://mcp-calendartools

=== Summary ===
Succeeded: 2/2
Failed: 0/2

? All permissions added successfully!

Next steps:
  1. Review permissions in Azure Portal: https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/87654321-4321-4321-4321-210987654321
  2. Grant admin consent for the added permissions if required
  3. Test your application with the new permissions
```

### Partial failure

```
Adding MCP server permissions to application...

Target Application ID (from --app-id): 12345678-1234-1234-1234-123456789abc
Using user-specified scopes: McpServers.Mail.All, McpServers.Calendar.All

Found 2 unique audience(s): api://mcp-mailtools, api://mcp-calendartools

Adding permissions to application...

Processing audience: api://mcp-mailtools
  ? Successfully added permissions for api://mcp-mailtools

Processing audience: api://mcp-calendartools
  ? Failed to add permissions for api://mcp-calendartools

=== Summary ===
Succeeded: 1/2
Failed: 1/2

Some permissions failed to add. Review the errors above.
```

## What Gets Added to the Application

The command modifies the application's `requiredResourceAccess` collection in its manifest. This is what appears in the Azure Portal under:

**Azure Portal > Azure Active Directory > App registrations > [Your App] > API permissions**

### Before running the command:

```json
{
  "requiredResourceAccess": [
    {
      "resourceAppId": "00000003-0000-0000-c000-000000000000",
      "resourceAccess": [
        {
          "id": "e1fe6dd8-ba31-4d61-89e7-88639da4683d",
          "type": "Scope"
        }
      ]
    }
  ]
}
```

### After running the command:

```json
{
  "requiredResourceAccess": [
    {
      "resourceAppId": "00000003-0000-0000-c000-000000000000",
      "resourceAccess": [
        {
          "id": "e1fe6dd8-ba31-4d61-89e7-88639da4683d",
          "type": "Scope"
        }
      ]
    },
    {
      "resourceAppId": "api://mcp-mailtools",
      "resourceAccess": [
        {
          "id": "<permission-id-for-Mail.All>",
          "type": "Scope"
        },
        {
          "id": "<permission-id-for-Mail.ReadWrite>",
          "type": "Scope"
        }
      ]
    },
    {
      "resourceAppId": "api://mcp-calendartools",
      "resourceAccess": [
        {
          "id": "<permission-id-for-Calendar.All>",
          "type": "Scope"
        }
      ]
    }
  ]
}
```

## Differences from `a365 setup permissions mcp`

| Feature | `addmcpserverpermissions` | `setup permissions mcp` |
|---------|---------------------------|-------------------------|
| **Purpose** | Add API permissions to custom applications | Configure full MCP setup (OAuth2 grants + inheritable permissions) |
| **Target** | Any application (via --app-id) | Agent blueprint only |
| **What it does** | Modifies `requiredResourceAccess` in app manifest | Configures OAuth2 grants, inheritable permissions, and resource consents |
| **Admin consent** | Not granted automatically | May grant admin consent as part of setup |
| **Portal visibility** | ? Permissions appear in portal | ? Permissions appear in portal |
| **Use case** | Custom apps needing MCP access | Initial agent blueprint setup |

**Recommendation**: Use `setup permissions mcp` for blueprint configuration during initial setup. Use `addmcpserverpermissions` when you have custom applications that need MCP server access.

## Error Handling

### Application not found

```
Failed to retrieve application with appId 12345678-1234-1234-1234-123456789abc
Application not found with appId 12345678-1234-1234-1234-123456789abc
```

**Solution**: Verify the application ID is correct and exists in your tenant.

### ToolingManifest.json not found

```
ToolingManifest.json not found at: D:\MyProject\ToolingManifest.json

Please ensure ToolingManifest.json exists in your project directory
or specify scopes explicitly with --scopes option.

Example: a365 addmcpserverpermissions --scopes McpServers.Mail.All McpServers.Calendar.All
```

**Solution 1**: Create ToolingManifest.json in your project directory.

**Solution 2**: Use `--scopes` to specify permissions explicitly.

### No application ID specified

```
No application ID specified. Use --app-id or ensure AgentBlueprintId is set in config.

Example: a365 addmcpserverpermissions --app-id <your-app-id>
```

**Solution 1**: Add `--app-id` parameter.

**Solution 2**: Ensure `a365.generated.config.json` has a valid `AgentBlueprintId`.

### Resource service principal not found

```
Resource service principal not found for appId api://mcp-mailtools
```

**Solution**: Ensure the MCP server API is registered in your tenant. Contact your tenant administrator.

### Insufficient permissions

```
Failed to add required resource access: Insufficient privileges to complete the operation
```

**Solution**: Ensure you have `Application.ReadWrite.All` permission and appropriate admin role (Global Administrator, Application Administrator, or Cloud Application Administrator).

### Permission scope not found

```
Permission scope 'McpServers.InvalidScope' not found on resource api://mcp-mailtools
```

**Solution**: Verify the scope name is correct. Check the resource API's published permissions.

## Next Steps After Adding Permissions

1. **Review in Azure Portal**:
   - Navigate to: Azure Active Directory > App registrations > [Your App] > API permissions
   - Verify the MCP server permissions appear in the list

2. **Grant Admin Consent** (if required):
   - Click "Grant admin consent for [Tenant Name]" in the API permissions blade
   - This is required for the application to use the permissions

3. **Test the Application**:
   - Use `a365 getbearertokenformcpservers` to acquire tokens
   - Verify the application can access MCP servers with the new permissions

4. **Update Application Code**:
   - Ensure your application requests the correct scopes when acquiring tokens
   - Example: Request `api://mcp-mailtools/.default` scope for mail MCP servers

## Use Cases

### Use Case 1: Custom Backend Service

You have a custom backend service (not an agent) that needs to call MCP servers:

```bash
# Add permissions to your backend service application
a365 addmcpserverpermissions --app-id <backend-service-app-id>

# Grant admin consent in Azure Portal

# Your backend can now acquire tokens for MCP servers
```

### Use Case 2: Development/Testing Application

Create a test application with specific MCP permissions:

```bash
# Add only mail permissions for testing
a365 addmcpserverpermissions \
  --app-id <test-app-id> \
  --scopes McpServers.Mail.All
```

### Use Case 3: Multi-Environment Setup

Different applications for dev/test/prod environments:

```bash
# Development environment
a365 addmcpserverpermissions \
  --app-id <dev-app-id> \
  --config a365.dev.config.json

# Production environment  
a365 addmcpserverpermissions \
  --app-id <prod-app-id> \
  --config a365.prod.config.json
```

### Use Case 4: Incremental Permission Addition

Add permissions incrementally as you develop new features:

```bash
# Initially, add mail permissions
a365 addmcpserverpermissions --scopes McpServers.Mail.All

# Later, add calendar permissions (merges with existing)
a365 addmcpserverpermissions --scopes McpServers.Calendar.All
```

**Note**: The command merges permissions. If a resource already has some permissions, new ones are added without removing existing ones.

## Related Commands

- `a365 setup permissions mcp`: Configure MCP permissions for agent blueprint (full setup)
- `a365 getbearertokenformcpservers`: Retrieve bearer tokens for MCP server access
- `a365 config display`: View current configuration including blueprint ID

## Troubleshooting

### Permissions don't appear in portal

**Problem**: Command succeeded but permissions don't show in Azure Portal.

**Solutions**:
1. Refresh the Azure Portal page (Ctrl+F5)
2. Wait 1-2 minutes for Azure AD propagation
3. Verify you're looking at the correct application
4. Check the application's manifest JSON directly

### Admin consent still required after command

**Problem**: Added permissions show "Not granted for [Tenant]" in portal.

**Explanation**: This command adds permissions to the manifest but doesn't grant admin consent.

**Solution**: 
1. Navigate to: Azure Portal > API permissions
2. Click "Grant admin consent for [Tenant Name]"
3. Confirm the consent grant

### Command fails with "Application.ReadWrite.All" error

**Problem**: Insufficient permissions to modify application manifest.

**Solutions**:
1. Contact your tenant administrator to grant required permissions
2. Ensure you're assigned an appropriate admin role
3. Try running with a Global Administrator account

## Security Considerations

1. **Least Privilege**: Only add permissions your application actually needs
2. **Admin Consent**: Some permissions require admin consent before use
3. **Scope Review**: Review added permissions in Azure Portal regularly
4. **Audit Logs**: Azure AD logs all permission changes for compliance
5. **Test Applications**: Test with non-production apps before modifying production applications

## Implementation Details

The command is implemented in:
- **Command**: `Microsoft.Agents.A365.DevTools.Cli/Commands/AddMcpServerPermissionsCommand.cs`
- **Tests**: `Tests/Microsoft.Agents.A365.DevTools.Cli.Tests/Commands/AddMcpServerPermissionsCommandTests.cs`
- **API Method**: `GraphApiService.AddRequiredResourceAccessAsync()`

The command uses the Microsoft Graph API's `PATCH /applications/{id}` endpoint to modify the `requiredResourceAccess` collection.
