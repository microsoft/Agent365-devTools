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

### Prerequisites

- **Azure role**: Global Administrator or Application Administrator
- **Azure CLI**: Installed and signed in (`az login`)
- **Tenant access**: Entra ID tenant where you'll deploy Agent365

### 1. Register Application

Follow [Microsoft's quickstart guide](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app) to create an app registration:

1. Go to **Azure Portal** → **Entra ID** → **App registrations** → **New registration**
2. Enter:
   - **Name**: `Agent365 CLI` (or your preferred name)
   - **Supported account types**: **Single tenant** (Accounts in this organizational directory only)
   - **Redirect URI**: Select **Public client/native (mobile & desktop)** → Enter `http://localhost:8400/`
3. Click **Register**

> **Note**: The CLI uses port 8400 for the OAuth callback. Ensure this port is not blocked by your firewall.

### 2. Copy Application (client) ID

From the app's **Overview** page, copy the **Application (client) ID** (GUID format). You'll enter this during `a365 config init`.

> **Tip**: Don't confuse this with **Object ID** - you need the **Application (client) ID**.

### 3. Configure API Permissions

**Choose Your Method**: The two `AgentIdentityBlueprint.*` permissions are beta APIs and may not be visible in the Azure Portal UI. You can either:
- **Option A**: Use Azure Portal for all permissions (if beta permissions are visible)
- **Option B**: Use Microsoft Graph API to add all permissions (recommended if beta permissions not visible)

#### Option A: Azure Portal (Standard Method)

**If beta permissions are visible in your tenant**:

1. In your app registration, go to **API permissions**
2. Click **Add a permission** → **Microsoft Graph** → **Delegated permissions**
3. Search for and add these 5 permissions:

| Permission | Purpose |
|-----------|---------|
| `Application.ReadWrite.All` | Create and manage applications and Agent Blueprints |
| `AgentIdentityBlueprint.ReadWrite.All` | Manage Agent Blueprint configurations (beta API) |
| `AgentIdentityBlueprint.UpdateAuthProperties.All` | Update Agent Blueprint inheritable permissions (beta API) |
| `DelegatedPermissionGrant.ReadWrite.All` | Grant permissions for agent blueprints |
| `Directory.Read.All` | Read directory data for validation |

4. Click **Grant admin consent for [Your Tenant]** (requires Global Admin or Application Admin role)
5. Verify all permissions show green checkmarks under "Status"

**Important**: Use **Delegated permissions** (NOT Application permissions). The CLI requires delegated permissions because you sign in interactively.

If the beta permissions (`AgentIdentityBlueprint.*`) are **not visible**, proceed to **Option B** below.

#### Option B: Microsoft Graph API (For Beta Permissions)

**Use this method if `AgentIdentityBlueprint.*` permissions are not visible in Azure Portal**.

##### Step 1: Add permissions to app manifest

First, ensure you're signed in with admin privileges:

```bash
az login
```

Update the app registration's `requiredResourceAccess` to include all 5 permissions:

```bash
# Replace YOUR_CLIENT_APP_ID with your Application (client) ID from Step 2
az ad app update --id YOUR_CLIENT_APP_ID --required-resource-accesses @- <<EOF
[
  {
    "resourceAppId": "00000003-0000-0000-c000-000000000000",
    "resourceAccess": [
      {
        "id": "1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9",
        "type": "Scope"
      },
      {
        "id": "e1fe6dd8-ba31-4d61-89e7-88639da4683d",
        "type": "Scope"
      },
      {
        "id": "06b708a9-e830-4db3-a914-8e69da51d44f",
        "type": "Scope"
      },
      {
        "id": "8f6a01e7-0391-4ee5-aa22-a3af122cef27",
        "type": "Scope"
      },
      {
        "id": "06da0dbc-49e2-44d2-8312-53f166ab848a",
        "type": "Scope"
      }
    ]
  }
]
EOF
```

> **Permission ID mapping**:
> - `1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9` = `Application.ReadWrite.All`
> - `e1fe6dd8-ba31-4d61-89e7-88639da4683d` = `Directory.Read.All`
> - `06b708a9-e830-4db3-a914-8e69da51d44f` = `DelegatedPermissionGrant.ReadWrite.All`
> - `8f6a01e7-0391-4ee5-aa22-a3af122cef27` = `AgentIdentityBlueprint.ReadWrite.All`
> - `06da0dbc-49e2-44d2-8312-53f166ab848a` = `AgentIdentityBlueprint.UpdateAuthProperties.All`

##### Step 2: Create service principal (if not exists)

```bash
# This creates the enterprise app / service principal for your app registration
az ad sp create --id YOUR_CLIENT_APP_ID
```

If the service principal already exists, this command will return its details (safe to run).

##### Step 3: Grant admin consent via API

Get the service principal object ID:

```bash
SP_OBJECT_ID=$(az ad sp list --filter "appId eq 'YOUR_CLIENT_APP_ID'" --query "[0].id" -o tsv)
echo "Service Principal Object ID: $SP_OBJECT_ID"
```

Get Microsoft Graph service principal ID:

```bash
GRAPH_SP_ID=$(az ad sp list --filter "appId eq '00000003-0000-0000-c000-000000000000'" --query "[0].id" -o tsv)
echo "Microsoft Graph SP ID: $GRAPH_SP_ID"
```

Create the admin consent grant:

```bash
az rest --method POST \
  --url "https://graph.microsoft.com/v1.0/oauth2PermissionGrants" \
  --body "{
    \"clientId\": \"$SP_OBJECT_ID\",
    \"consentType\": \"AllPrincipals\",
    \"principalId\": null,
    \"resourceId\": \"$GRAPH_SP_ID\",
    \"scope\": \"Application.ReadWrite.All Directory.Read.All DelegatedPermissionGrant.ReadWrite.All AgentIdentityBlueprint.ReadWrite.All AgentIdentityBlueprint.UpdateAuthProperties.All\"
  }"
```

**Verification**: Run this command to verify the grant was created:

```bash
az rest --method GET \
  --url "https://graph.microsoft.com/v1.0/oauth2PermissionGrants?\$filter=clientId eq '$SP_OBJECT_ID'" \
  --query "value[0].scope" -o tsv
```

You should see all 5 permission names listed.

**Critical**: **Do NOT click "Grant admin consent" in Azure Portal** after using the API method. This will remove the beta permissions in tenants where they're not visible in the UI.

### 4. Use in Agent365 CLI

Run the configuration wizard and enter your Application (client) ID when prompted:

```powershell
a365 config init
```

The CLI automatically validates:
- App exists in your tenant  
- Required permissions are configured
- Admin consent has been granted

## Troubleshooting

### Beta Permissions Disappear After Portal Admin Consent

**Symptom**: You used the API method (Option B) to add beta permissions, but they disappeared after clicking "Grant admin consent" in Azure Portal.

**Root cause**: Azure Portal doesn't show beta permissions in the UI, so when you click "Grant admin consent" in Portal, it only grants the *visible* permissions and overwrites the API-granted consent.

**Solution**: Never use Portal admin consent after API method. The API method already grants admin consent (consentType: "AllPrincipals").

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
