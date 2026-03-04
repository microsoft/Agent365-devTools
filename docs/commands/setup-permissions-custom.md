# Agent 365 CLI - Custom Blueprint Permissions Guide

> **Command**: `a365 setup permissions custom`
> **Purpose**: Configure custom resource OAuth2 grants and inheritable permissions for your agent blueprint

## Overview

The `a365 setup permissions custom` command applies custom API permissions to your agent blueprint that go beyond the standard permissions required for agent operation. This allows your agent to access additional Microsoft Graph scopes (like Presence, Files, Chat) or custom APIs that your organization has developed.

## Quick Start

```bash
# Step 1: Configure custom permissions in config
a365 config permissions \
  --resource-app-id 00000003-0000-0000-c000-000000000000 \
  --scopes Presence.ReadWrite,Files.Read.All

# Step 2: Apply permissions to blueprint
a365 setup permissions custom

# Or run as part of full setup
a365 setup all
```

## Key Features

- **Generic Resource Support**: Works with Microsoft Graph, custom APIs, and first-party Microsoft services
- **OAuth2 Grants**: Automatically configures delegated permission grants with admin consent
- **Inheritable Permissions**: Enables agent users to inherit permissions from the blueprint
- **Portal Visibility**: Permissions appear in Azure Portal under API permissions
- **Reconciling**: Removes permissions from Azure AD when they are removed from config
- **Idempotent**: Safe to run multiple times - skips already-configured permissions
- **Dry Run Support**: Preview changes before applying with `--dry-run`

## Prerequisites

1. **Blueprint Created**: Run `a365 setup blueprint` first to create the agent blueprint
2. **Custom Permissions Configured**: Add custom permissions to `a365.config.json` using `a365 config permissions`
3. **Global Administrator**: You must have Global Administrator role to grant admin consent

## Configuration

### Step 1: Add Custom Permissions to Config

Use the `a365 config permissions` command to add custom permissions:

```bash
# Add Microsoft Graph extended permissions
a365 config permissions \
  --resource-app-id 00000003-0000-0000-c000-000000000000 \
  --scopes Presence.ReadWrite,Files.Read.All,Chat.Read

# Add custom API permissions
a365 config permissions \
  --resource-app-id abcd1234-5678-90ab-cdef-1234567890ab \
  --scopes CustomScope.Read,CustomScope.Write
```

**Expected Output**:
```
Permission added successfully.
Configuration saved to: C:\Users\user\a365.config.json

Next step: Run 'a365 setup permissions custom' to apply these permissions to your blueprint.
```

> **Note**: The resource name is not prompted for during configuration. It will be automatically resolved from Azure during the `a365 setup permissions custom` step.

### Step 2: Verify Configuration

Check your `a365.config.json` file:

```json
{
  "tenantId": "...",
  "clientAppId": "...",
  "customBlueprintPermissions": [
    {
      "resourceAppId": "00000003-0000-0000-c000-000000000000",
      "resourceName": null,
      "scopes": [
        "Presence.ReadWrite",
        "Files.Read.All",
        "Chat.Read"
      ]
    },
    {
      "resourceAppId": "abcd1234-5678-90ab-cdef-1234567890ab",
      "resourceName": null,
      "scopes": [
        "CustomScope.Read",
        "CustomScope.Write"
      ]
    }
  ]
}
```

> **Note**: The `resourceName` field is optional and can be left as `null`. The display name is auto-resolved from Azure during `a365 setup permissions custom` for logging purposes only — the resolved name is not written back to any config file.

## Usage

### Apply Custom Permissions

```bash
# Apply all configured custom permissions
a365 setup permissions custom

# Preview what would be configured (dry run)
a365 setup permissions custom --dry-run

# Specify custom config file
a365 setup permissions custom --config path/to/a365.config.json
```

### Example Output

```
Configuring custom blueprint permissions...

Configuring Microsoft Graph Extended (00000003-0000-0000-c000-000000000000)...
  - Configuring OAuth2 permission grants...
  - Setting inheritable permissions...
  - Microsoft Graph Extended configured successfully

Configuring Contoso Custom API (abcd1234-5678-90ab-cdef-1234567890ab)...
  - Configuring OAuth2 permission grants...
  - Setting inheritable permissions...
  - Contoso Custom API configured successfully

Custom blueprint permissions configured successfully
```

### Dry Run Output

```bash
$ a365 setup permissions custom --dry-run

DRY RUN: Configure Custom Blueprint Permissions
Would configure the following custom permissions:
  - Microsoft Graph Extended (00000003-0000-0000-c000-000000000000)
    Scopes: Presence.ReadWrite, Files.Read.All, Chat.Read
  - Contoso Custom API (abcd1234-5678-90ab-cdef-1234567890ab)
    Scopes: CustomScope.Read, CustomScope.Write
```

## Integration with Setup All

Custom permissions are automatically configured when you run `a365 setup all`:

```bash
# Full setup including custom permissions
a365 setup all
```

**Setup Flow**:
1. Infrastructure (Resource Group, App Service Plan, Web App)
2. Agent Blueprint
3. MCP Tools Permissions
4. Bot API Permissions
5. **Custom Blueprint Permissions** (if configured)
6. Messaging Endpoint

## Common Use Cases

### Use Case 1: Extended Microsoft Graph Permissions

**Scenario**: Your agent needs access to user presence and files in OneDrive.

**Solution**:
```bash
# Configure Microsoft Graph extended permissions
a365 config permissions \
  --resource-app-id 00000003-0000-0000-c000-000000000000 \
  --scopes Presence.ReadWrite,Files.Read.All
```

**Scopes**:
- `Presence.ReadWrite`: Read and update user presence information
- `Files.Read.All`: Read files in all site collections

### Use Case 2: Teams Chat Integration

**Scenario**: Your agent needs to read and send Teams chat messages.

**Solution**:
```bash
# Configure Teams Chat permissions
a365 config permissions \
  --resource-app-id 00000003-0000-0000-c000-000000000000 \
  --scopes Chat.Read,Chat.ReadWrite,ChatMessage.Send
```

**Scopes**:
- `Chat.Read`: Read user's chat messages
- `Chat.ReadWrite`: Read and write user's chat messages
- `ChatMessage.Send`: Send chat messages as the user

### Use Case 3: Custom API Access

**Scenario**: Your organization has a custom API that agents need to access.

**Solution**:
```bash
# Configure custom API permissions
a365 config permissions \
  --resource-app-id YOUR-CUSTOM-API-APP-ID \
  --scopes api://your-api/Read,api://your-api/Write
```

**Prerequisites**:
- Your custom API must be registered in Entra ID
- The API must expose delegated permissions
- You need the Application (client) ID of the API

### Use Case 4: Multiple Custom Resources

**Scenario**: Your agent needs permissions to multiple resources.

**Solution**:
```bash
# Add first resource
a365 config permissions \
  --resource-app-id 00000003-0000-0000-c000-000000000000 \
  --scopes Presence.ReadWrite,Files.Read.All

# Add second resource (run command again)
a365 config permissions \
  --resource-app-id YOUR-CUSTOM-API-APP-ID \
  --scopes CustomScope.Read

# Apply all permissions
a365 setup permissions custom
```

## Managing Custom Permissions

### View Current Permissions

```bash
# View all configured custom permissions
a365 config permissions
```

**Output**:
```
Current custom blueprint permissions:
  1. Microsoft Graph Extended (00000003-0000-0000-c000-000000000000)
     Scopes: Presence.ReadWrite, Files.Read.All
  2. Contoso Custom API (abcd1234-5678-90ab-cdef-1234567890ab)
     Scopes: CustomScope.Read, CustomScope.Write
```

### Update Existing Permission

```bash
# Update scopes for an existing resource
a365 config permissions \
  --resource-app-id 00000003-0000-0000-c000-000000000000 \
  --scopes Presence.ReadWrite,Files.Read.All,Chat.Read
```

**Confirmation Prompt**:
```
Resource 00000003-0000-0000-c000-000000000000 already exists with scopes:
  Presence.ReadWrite, Files.Read.All

Do you want to overwrite with new scopes? (y/N): y

Permission updated successfully.
Configuration saved to: C:\Users\user\a365.config.json
```

### Remove Custom Permissions

To fully remove a custom permission — from both config and Azure AD — run the two-step process:

```bash
# Step 1: Remove from config (one specific resource, or all)
a365 config permissions --reset

# Step 2: Reconcile Azure AD with the updated config
a365 setup permissions custom
# OR equivalently:
a365 setup blueprint
```

`config permissions --reset` only updates `a365.config.json`. The second command detects the removed entries and cleans them up from Azure AD (both inheritable permissions and the OAuth2 grant).

**Step 1 output** (`config permissions --reset`):
```
Clearing all custom blueprint permissions...

Configuration saved to: C:\Users\user\a365.config.json
```

**Step 2 output** (`setup permissions custom`):
```
Configuring custom blueprint permissions...

Removing 1 stale custom permission(s) no longer in config...
  Removing stale permission for 00000003-0000-0ff1-ce00-000000000000...
  - Inheritable permissions removed for 00000003-0000-0ff1-ce00-000000000000
  - OAuth2 grant revoked for 00000003-0000-0ff1-ce00-000000000000
No custom blueprint permissions configured.
```

## Validation

The CLI validates custom permissions at multiple stages:

### Config Validation

When adding permissions via `a365 config permissions` (or the `a365 config init` wizard):
- ✅ **GUID Format**: Resource App ID must be a valid GUID
- ✅ **Required Fields**: Resource App ID (GUID) and scopes are required; resource name is optional and will be auto-resolved during setup if not provided
- ✅ **Scopes**: At least one scope must be specified
- ✅ **Duplicates**: No duplicate scopes within a permission
- ✅ **Unique Resources**: No duplicate resource App IDs

### Setup Validation

When applying permissions via `a365 setup permissions custom`:
- ✅ **Blueprint Exists**: Verifies agent blueprint ID exists
- ✅ **Permission Format**: Re-validates each permission
- ✅ **API Existence**: Checks if resource API exists in tenant (best effort)
- ✅ **Scope Availability**: Validates scopes are exposed by the API

## Error Handling

### Error: Blueprint Not Found

```
ERROR: Blueprint ID not found. Run 'a365 setup blueprint' first.
```

**Solution**: Create the agent blueprint before configuring permissions:
```bash
a365 setup blueprint
```

### Error: Invalid Resource App ID

```
ERROR: Invalid resourceAppId 'not-a-guid'. Must be a valid GUID format.
```

**Solution**: Use a valid GUID format (e.g., `00000003-0000-0000-c000-000000000000`)

### Error: Invalid Permission Configuration

```
ERROR: Invalid custom permission configuration: resourceAppId must be a valid GUID, At least one scope is required
```

**Solution**: Ensure all required fields are properly configured in `a365.config.json`

## Idempotency and Reconciliation

The `a365 setup permissions custom` command is **reconciling**: it syncs Azure AD to match the current config — adding what is configured and removing what is not.

- ✅ Safe to run multiple times
- ✅ Skips already-configured permissions (no-op if nothing changed)
- ✅ Applies new or updated permissions
- ✅ Removes permissions that were deleted from config
- ✅ Standard permissions (MCP, Bot API, Graph) are never removed

**Rerun with no changes**:
```
Configuring custom blueprint permissions...

Configuring Microsoft Graph Extended (00000003-0000-0000-c000-000000000000)...
  - Inheritable permissions already configured for Microsoft Graph Extended
  - Microsoft Graph Extended configured successfully
Custom blueprint permissions configured successfully
```

**After removing a permission from config**:
```
Configuring custom blueprint permissions...

Removing 1 stale custom permission(s) no longer in config...
  Removing stale permission for 00000003-0000-0ff1-ce00-000000000000...
  - Inheritable permissions removed for 00000003-0000-0ff1-ce00-000000000000
  - OAuth2 grant revoked for 00000003-0000-0ff1-ce00-000000000000
No custom blueprint permissions configured.
```

## Troubleshooting

### Issue: Permission not appearing in Azure Portal

**Symptom**: Custom permission is not visible in the blueprint's API permissions

**Solution**:
1. Wait a few minutes for Azure AD replication
2. Refresh the Azure Portal page
3. Navigate to: Azure Portal → Entra ID → Applications → [Your Blueprint] → API permissions

### Issue: "Insufficient privileges" error

**Symptom**: Permission setup fails with insufficient privileges

**Solution**: You need Global Administrator role to grant admin consent:
```bash
# Check your current role
az ad signed-in-user show --query '[displayName, userPrincipalName, id]'

# Contact your Global Administrator to run the command
```

### Issue: Custom API not found

**Symptom**: Setup fails because custom API doesn't exist

**Solution**:
1. Verify the API is registered in your Entra ID tenant
2. Check the Application (client) ID is correct
3. Ensure the API exposes the requested scopes

### Issue: Scope not available

**Symptom**: Requested scope doesn't exist on the resource API

**Solution**:
1. Verify the scope name is correct (case-sensitive)
2. Check the API's exposed permissions in Azure Portal
3. Update the scope name to match exactly

## Command Options

```bash
# Display help
a365 setup permissions custom --help

# Specify custom config file
a365 setup permissions custom --config path/to/a365.config.json
a365 setup permissions custom -c path/to/a365.config.json

# Preview changes without applying
a365 setup permissions custom --dry-run

# Show detailed output
a365 setup permissions custom --verbose
a365 setup permissions custom -v
```

## Azure Portal Verification

After running `a365 setup permissions custom`, verify in Azure Portal:

1. Navigate to **Azure Portal** → **Entra ID** → **Applications**
2. Find your agent blueprint application
3. Click **API permissions**
4. You should see your custom permissions listed under **Configured permissions**
5. Verify **Status** column shows "Granted for [Your Tenant]"

## Best Practices

### 1. Use Least Privilege Principle

Only request the minimum scopes your agent needs:
```json
{
  "scopes": ["Files.Read.All"]  // ✅ Good: Only read access
}
```

Avoid overly broad permissions:
```json
{
  "scopes": ["Files.ReadWrite.All", "Sites.FullControl.All"]  // ❌ Too broad
}
```

### 2. Document Your Custom Permissions

Add comments to your config explaining why each permission is needed:
```json
{
  "customBlueprintPermissions": [
    {
      "resourceName": "Microsoft Graph Extended",
      "resourceAppId": "00000003-0000-0000-c000-000000000000",
      "scopes": [
        "Presence.ReadWrite",  // Required for status updates
        "Files.Read.All"       // Required for document retrieval
      ]
    }
  ]
}
```

### 3. Test with Dry Run First

Always preview changes before applying:
```bash
# Preview first
a365 setup permissions custom --dry-run

# Then apply
a365 setup permissions custom
```

### 4. Version Control Your Config

Keep `a365.config.json` in version control:
```gitignore
# Safe to commit (no secrets)
a365.config.json

# Never commit (contains secrets)
a365.generated.config.json
```

## Next Steps

After configuring custom permissions:

1. **Test the agent**: Verify it can access the custom resources
2. **Monitor usage**: Check Azure Portal for API call patterns
3. **Update as needed**: Add or remove scopes using `a365 config permissions`
4. **Deploy updates**: Run `a365 setup permissions custom` to apply changes

## Additional Resources

- **Configuration Guide**: [a365 config init](config-init.md)
- **Setup Guide**: [a365 setup](setup.md)
- **Microsoft Graph Permissions**: [Graph Permissions Reference](https://learn.microsoft.com/graph/permissions-reference)
- **GitHub Issues**: [Agent 365 Repository](https://github.com/microsoft/Agent365-devTools/issues)
- **Issue #194**: [Original feature request](https://github.com/microsoft/Agent365-devTools/issues/194)
