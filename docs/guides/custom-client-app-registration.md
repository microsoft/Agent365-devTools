# Custom Client App Registration Guide

## Overview

The Agent365 CLI requires a custom client app registration in your Entra ID tenant to authenticate and manage Agent Identity Blueprints. This guide covers the Agent365-specific requirements.

**For general app registration steps**, see Microsoft's official documentation:
- [Quickstart: Register an application](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app)
- [Grant tenant-wide admin consent](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/grant-admin-consent)

### CRITICAL: Delegated vs Application Permissions

**You MUST use Delegated permissions (NOT Application permissions)** for all five required permissions.

| Permission Type | When to Use | How Agent365 Uses It |
|----------------|-------------|---------------------|
| **Delegated** ("Scope") | User signs in interactively | **Agent365 CLI uses this** - You sign in, CLI acts on your behalf |
| **Application** ("Role") | Service runs without user | **Don't use** - For background services/daemons only |

**Why Delegated?**
- You sign in interactively (`az login`, browser authentication)
- CLI performs actions **as you** (audit trails show your identity)
- More secure - limited by your actual permissions
- Ensures accountability and compliance

**Common mistake**: Adding `Directory.Read.All` as **Application** instead of **Delegated**.

## Quick Setup

### 1. Register Application

Follow [Microsoft's quickstart guide](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app) to create an app registration with:

- **Name**: `Agent365 CLI` (or your preferred name)
- **Supported account types**: **Single tenant** (Accounts in this organizational directory only)
- **Redirect URI**: **Public client/native (mobile & desktop)** → `http://localhost:8400/`

> **Note**: The CLI uses port 8400 for the OAuth callback. Ensure this port is not blocked by your firewall.

### 2. Copy Application (client) ID

From the app's **Overview** page, copy the **Application (client) ID**. You'll enter this during `a365 config init`.

### 3. Configure API Permissions

**Add as DELEGATED permissions (NOT Application)**:

In Azure Portal: **API permissions** → **Add a permission** → **Microsoft Graph** → **Delegated permissions**

| Permission | Purpose |
|-----------|---------|
| `Application.ReadWrite.All` | Create and manage applications and Agent Blueprints |
| `AgentIdentityBlueprint.ReadWrite.All` | Manage Agent Blueprint configurations (beta API) |
| `AgentIdentityBlueprint.UpdateAuthProperties.All` | Update Agent Blueprint inheritable permissions (required for MCP setup) |
| `DelegatedPermissionGrant.ReadWrite.All` | Grant permissions for agent blueprints |
| `Directory.Read.All` | Read directory data for validation |

**Important**: 
- Use **Delegated permissions** (NOT Application permissions)
- See [Microsoft Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference) for permission details
- All five permissions are required for Agent Blueprint operations
- The two `AgentIdentityBlueprint.*` permissions are beta APIs and may not be visible in all tenants yet

### 4. Grant Admin Consent

**CRITICAL**: [Grant tenant-wide admin consent](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/grant-admin-consent) for all five permissions.

The CLI validates that admin consent has been granted before allowing blueprint operations.

### 5. Use in Agent365 CLI

Run the configuration wizard and enter your Application (client) ID when prompted:

```powershell
a365 config init
```

The CLI automatically validates:
- App exists in your tenant  
- Required permissions are configured
- Admin consent has been granted

## Troubleshooting

### Permissions "AgentIdentityBlueprint.*" Not Found

The two `AgentIdentityBlueprint` permissions are **beta API permissions** that may not yet be available in all tenants:
- `AgentIdentityBlueprint.ReadWrite.All`
- `AgentIdentityBlueprint.UpdateAuthProperties.All`

**Solution**: 
1. Ensure you're adding **Microsoft Graph** delegated permissions (not Application permissions)
2. Contact Microsoft support if these permissions aren't visible in your tenant

### Validation Errors

The CLI automatically validates your client app when running `a365 setup all` or `a365 config init`.

Common issues:
- **App not found**: Verify you copied the **Application (client) ID** (not Object ID)
- **Missing permissions**: Add all five required permissions
- **Admin consent not granted**: Click "Grant admin consent" in Azure Portal
- **Wrong permission type**: Use Delegated permissions, not Application permissions

For detailed troubleshooting, see [Microsoft's app registration documentation](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app).

## Security Best Practices

**Do**:
- Use single-tenant registration
- Grant only the five required delegated permissions
- Audit permissions regularly
- Remove the app when no longer needed

**Don't**:
- Grant Application permissions (use Delegated only)
- Share the Client ID publicly
- Grant additional unnecessary permissions
- Use the app for other purposes

## Additional Resources

- [Microsoft Graph Permissions Reference](https://learn.microsoft.com/en-us/graph/permissions-reference)
- [Entra ID App Registration](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app)
- [Grant Admin Consent](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/grant-admin-consent)
- [Agent365 CLI Documentation](../README.md)
