# Custom Client App Registration Guide

## Overview

The Agent365 CLI requires a custom client app registration in your Entra ID tenant to authenticate and manage Agent Identity Blueprints.

## Quick Setup

### Prerequisites

**To register the app** (Steps 1-2):
- Any developer with basic Entra ID access can register an application

**To add permissions and grant consent** (Steps 3-4):
- **One of these admin roles** is required:
  - **Application Administrator** (recommended - can manage app registrations and grant consent)
  - **Cloud Application Administrator** (can manage app registrations and grant consent)
  - **Global Administrator** (has all permissions, but not required)

> **Don't have admin access?** You can complete Steps 1-2 yourself, then ask your tenant administrator to complete Steps 3-4. Provide them:
> - Your **Application (client) ID** from Step 2
> - A link to this guide: [Custom Client App Registration](#3-configure-api-permissions)

**Optional**:
- Azure CLI (only needed if you prefer command-line automation instead of Graph Explorer)

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

> **⚠️ Admin privileges required for this step and Step 4.** If you're a developer without admin access, send your **Application (client) ID** from Step 2 to your tenant administrator and have them complete Steps 3-4.

**Choose Your Method**: The two `AgentIdentityBlueprint.*` permissions are beta APIs and may not be visible in the Azure Portal UI. You can either:
- **Option A**: Use Azure Portal for all permissions (if beta permissions are visible)
- **Option B**: Use Microsoft Graph API to add all permissions (recommended if beta permissions not visible)

#### Option A: Azure Portal (Standard Method)

**If beta permissions are visible in your tenant**:

1. In your app registration, go to **API permissions**
2. Click **Add a permission** → **Microsoft Graph** → **Delegated permissions**
3. Search for and add these 5 permissions:

> **Important**: You MUST use **Delegated permissions** (NOT Application permissions). The CLI authenticates interactively - you sign in, and it acts on your behalf. See [Troubleshooting](#wrong-permission-type-delegated-vs-application) if you accidentally add Application permissions.

| Permission | Purpose |
|-----------|---------|
| `AgentIdentityBlueprint.ReadWrite.All` | Manage Agent Blueprint configurations (beta API) |
| `AgentIdentityBlueprint.UpdateAuthProperties.All` | Update Agent Blueprint inheritable permissions (beta API) |
| `Application.ReadWrite.All` | Create and manage applications and Agent Blueprints |
| `DelegatedPermissionGrant.ReadWrite.All` | Grant permissions for agent blueprints |
| `Directory.Read.All` | Read directory data for validation |

4. Click **Grant admin consent for [Your Tenant]**
   - **Why is this required?** Agent Identity Blueprints are tenant-wide resources that multiple users and applications can reference. Without tenant-wide consent, the CLI will fail during authentication.
   - **What if it fails?** You need Application Administrator, Cloud Application Administrator, or Global Administrator role. Ask your tenant admin for help.
5. Verify all permissions show green checkmarks under "Status"

If the beta permissions (`AgentIdentityBlueprint.*`) are **not visible**, proceed to **Option B** below.

#### Option B: Microsoft Graph API (For Beta Permissions)

**Use this method if `AgentIdentityBlueprint.*` permissions are not visible in Azure Portal**.

1. **Open Graph Explorer**: Go to https://developer.microsoft.com/graph/graph-explorer
2. **Sign in** with your admin account (Application Administrator or Cloud Application Administrator)
3. **Grant admin consent using Graph API**:

   **Step 1**: Get your service principal ID and Graph resource ID:
   
   ```
   https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId eq 'YOUR_CLIENT_APP_ID'&$select=id
   ```
   
   Copy the `id` value (this is your `SP_OBJECT_ID`)
   
   ```
   https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId eq '00000003-0000-0000-c000-000000000000'&$select=id
   ```
   
   Copy the `id` value (this is your `GRAPH_RESOURCE_ID`)

   **Step 2**: Grant admin consent with all 5 permissions:
   
   Change method to **POST** and use this URL:
   ```
   https://graph.microsoft.com/v1.0/oauth2PermissionGrants
   ```
   
   **Request Body**:
   ```json
   {
     "clientId": "SP_OBJECT_ID_FROM_STEP1",
     "consentType": "AllPrincipals",
     "principalId": null,
     "resourceId": "GRAPH_RESOURCE_ID_FROM_STEP1",
     "scope": "Application.ReadWrite.All Directory.Read.All DelegatedPermissionGrant.ReadWrite.All AgentIdentityBlueprint.ReadWrite.All AgentIdentityBlueprint.UpdateAuthProperties.All"
   }
   ```

   Click **Run query** - you should get a `201 Created` response.

   **Verification**: Query the grant to confirm:
   
   Change method to **GET** and use this URL:
   ```
   https://graph.microsoft.com/v1.0/oauth2PermissionGrants?$filter=clientId eq 'SP_OBJECT_ID_FROM_STEP1'
   ```
   
   The `scope` field should contain all 5 permission names.

**Important**: 
- The `consentType: "AllPrincipals"` in the request body grants **tenant-wide admin consent**, which is required because Agent Identity Blueprints are tenant-wide resources.
- **Do NOT click "Grant admin consent" in Azure Portal** after using this method, as it will remove the beta permissions.

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

### Wrong Permission Type (Delegated vs Application)

**Symptom**: CLI fails with authentication errors or permission denied errors.

**Root cause**: You added **Application permissions** instead of **Delegated permissions**.

| Permission Type | When to Use | How Agent365 Uses It |
|----------------|-------------|---------------------|
| **Delegated** ("Scope") | User signs in interactively | **Agent365 CLI uses this** - You sign in, CLI acts on your behalf |
| **Application** ("Role") | Service runs without user | **Don't use** - For background services/daemons only |

**Why Delegated?**
- You sign in interactively (browser authentication)
- CLI performs actions **as you** (audit trails show your identity)
- More secure - limited by your actual permissions
- Ensures accountability and compliance

**Solution**: 
1. Go to Azure Portal → App registrations → Your app → API permissions
2. **Remove** any Application permissions (these show as "Admin" in the Type column)
3. **Add** the same permissions as **Delegated** permissions
4. Grant admin consent again

**Common mistake**: Adding `Directory.Read.All` as **Application** instead of **Delegated**.

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
