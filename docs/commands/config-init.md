# Agent 365 CLI - Configuration Initialization Guide

> **Command**: `a365 config init`  
> **Purpose**: Interactive wizard to configure Agent 365 with Azure CLI integration and smart defaults

## Overview

The `a365 config init` command provides an intelligent, interactive configuration wizard that minimizes manual input by leveraging Azure CLI integration and smart defaults. The wizard automatically detects your Azure subscription, suggests resource names, and validates your inputs to ensure a smooth setup experience.

## Quick Start

```bash
# Initialize configuration with interactive wizard
a365 config init

# Import existing config file
a365 config init --configfile path/to/existing/a365.config.json

# Create config in global directory (AppData)
a365 config init --global
```

## Key Features

- **Azure CLI Integration**: Automatically detects your Azure subscription, tenant, and available resources
- **Smart Defaults**: Generates sensible defaults for resource names, agent identities, and UPNs
- **Resource Discovery**: Lists existing resource groups, app service plans, and locations
- **Platform Detection**: Automatically detects project type (.NET, Node.js, Python)
- **Input Validation**: Validates paths, UPNs, emails, and Azure resources
- **Interactive Prompts**: Press Enter to accept defaults or type to customize

## Configuration Flow

### Step 1: Azure CLI Verification

The wizard first verifies your Azure CLI authentication:

```
Checking Azure CLI authentication...
Subscription ID: e09e22f2-9193-4f54-a335-01f59575eefd (My Subscription)
Tenant ID: adfa4542-3e1e-46f5-9c70-3df0b15b3f6c

NOTE: Defaulted from current Azure account. To use a different Azure subscription,
run 'az login' and then 'az account set --subscription <subscription-id>' before
running this command.
```

**If not logged in:**
```
ERROR: You are not logged in to Azure CLI.
Please run 'az login' and then try again.
```

### Step 2: Agent Name

Provide a unique name for your agent. This is used to generate derived names for resources:

```
Agent name [agent1114]: myagent
```

**Smart Defaults**: If no existing config, defaults to `agent` + current date (e.g., `agent1114`)

### Step 3: Client App ID

Provide the Application (client) ID of your custom Entra ID app registration:

```
=================================================================
IMPORTANT: Custom Client App Required
=================================================================
The Agent365 CLI requires a custom client app registration in your
Entra ID tenant with specific Microsoft Graph API permissions.

Required Delegated Permissions:
  • Application.ReadWrite.All
  • AgentIdentityBlueprint.ReadWrite.All
  • DelegatedPermissionGrant.ReadWrite.All
  • Directory.Read.All

If you haven't created this app yet, see:
  docs/guides/custom-client-app-registration.md

=================================================================

Client App ID (Application ID from Entra ID): a1b2c3d4-e5f6-g7h8-i9j0-k1l2m3n4o5p6

Validating client app...
✓ Client app found in tenant
✓ Required permissions configured
✓ Admin consent granted
```

**Validation**: The CLI performs comprehensive validation:
- ✅ GUID format check
- ✅ App exists in your tenant
- ✅ All four required permissions are configured
- ✅ Admin consent has been granted

**If Validation Fails**: You'll see specific error messages and have up to 3 attempts:
```
✗ Validation failed:
  - Missing permission: Application.ReadWrite.All
  - Admin consent not granted for: DelegatedPermissionGrant.ReadWrite.All

Common issues:
  • Not all required permissions have been added to the app
  • Admin consent has not been granted (click "Grant admin consent" in Azure Portal)
  • Using the wrong GUID (use Application ID, not Object ID)

See troubleshooting guide:
  docs/guides/custom-client-app-registration.md#troubleshooting

Retry (2 attempts remaining)? (Y/n):
```

**Prerequisites**: Before running config init, create your custom client app:
1. Follow the guide: [Custom Client App Registration](../guides/custom-client-app-registration.md)
2. Copy the **Application (client) ID** from Azure Portal
3. Ensure admin consent is granted for all permissions
4. Enter the ID when prompted during config init

**Security Note**: This app stays within your tenant's security boundaries and you control which permissions are granted.

### Step 4: Deployment Project Path

Specify the path to your agent project:

```
Deployment project path [C:\A365-Ignite-Demo\sample_agent]:
Detected DotNet project
```

**Features**:
- Defaults to current directory or existing config path
- Validates directory exists
- Detects project platform (.NET, Node.js, Python)
- Warns if no supported project type detected

### Step 5: Resource Group Selection

Choose from existing resource groups or create a new one:

```
Available resource groups:
  1. a365demorg
  2. another-rg
  3. <Create new resource group>

Select resource group (1-3) [1]: 1
```

**Smart Behavior**:
- Lists existing resource groups from your subscription
- Option to create new resource group
- Defaults to existing config value if available

### Step 6: App Service Plan Selection

Choose from existing app service plans in the selected resource group:

```
Available app service plans in resource group 'a365demorg':
  1. a365agent-app-plan
  2. <Create new app service plan>

Select app service plan (1-2) [1]: 1
```

**Smart Behavior**:
- Only shows plans in the selected resource group
- Option to create new plan
- Defaults to existing config value

### Step 7: Manager Email

Provide the email address of the agent's manager:

```
Manager email [agent365demo.manager1@a365preview001.onmicrosoft.com]:
```

**Validation**: Ensures valid email format

### Step 8: Azure Location

Choose the Azure region for deployment:

```
Azure location [westus]:
```

**Smart Defaults**: Uses location from existing config or Azure account

### Step 9: Configuration Summary

Review all settings before saving:

```
=================================================================
 Configuration Summary
=================================================================
Agent Name             : myagent
Client App ID          : a1b2c3d4-e5f6-g7h8-i9j0-k1l2m3n4o5p6
Web App Name           : myagent-webapp-11140916
Agent Identity Name    : myagent Identity
Agent Blueprint Name   : myagent Blueprint
Agent UPN              : agent.myagent.11140916@yourdomain.onmicrosoft.com
Agent Display Name     : myagent Agent User
Manager Email          : agent365demo.manager1@a365preview001.onmicrosoft.com
Deployment Path        : C:\A365-Ignite-Demo\sample_agent
Resource Group         : a365demorg
App Service Plan       : a365agent-app-plan
Location               : westus
Subscription           : My Subscription (e09e22f2-9193-4f54-a335-01f59575eefd)
Tenant                 : adfa4542-3e1e-46f5-9c70-3df0b15b3f6c

Do you want to customize any derived names? (y/N):
```

### Step 10: Name Customization (Optional)

Optionally customize generated names:

```
Do you want to customize any derived names? (y/N): y

Web App Name [myagent-webapp-11140916]: myagent-prod
Agent Identity Display Name [myagent Identity]:
Agent Blueprint Display Name [myagent Blueprint]:
Agent User Principal Name [agent.myagent.11140916@yourdomain.onmicrosoft.com]:
Agent User Display Name [myagent Agent User]:
```

### Step 11: Confirmation

Final confirmation to save:

```
Save this configuration? (Y/n): Y

Configuration saved to: C:\Users\user\a365.config.json

You can now run:
  a365 setup      - Create Azure resources
  a365 deploy     - Deploy your agent
```

## Configuration Fields

The wizard automatically populates these fields:

### Authentication (Required for Microsoft Graph API)

| Field | Description | Source | Example |
|-------|-------------|--------|---------|
| **clientAppId** | Custom Entra ID app registration Application (client) ID | User provides after creating app | `a1b2c3d4-e5f6-g7h8-i9j0-k1l2m3n4o5p6` |

**Important**: This must be configured before setup. See [Custom Client App Registration Guide](../guides/custom-client-app-registration.md) for detailed setup instructions.

### Azure Infrastructure (Auto-detected from Azure CLI)

| Field | Description | Source | Example |
|-------|-------------|--------|---------|
| **tenantId** | Azure AD Tenant ID | Azure CLI (`az account show`) | `adfa4542-3e1e-46f5-9c70-3df0b15b3f6c` |
| **subscriptionId** | Azure Subscription ID | Azure CLI (`az account show`) | `e09e22f2-9193-4f54-a335-01f59575eefd` |
| **resourceGroup** | Azure Resource Group name | User selection from list | `a365demorg` |
| **location** | Azure region | Azure account or user input | `westus` |
| **appServicePlanName** | App Service Plan name | User selection from list | `a365agent-app-plan` |
| **appServicePlanSku** | Service Plan SKU | Default value | `B1` |

### Agent Identity (Auto-generated with customization option)

| Field | Description | Generation Logic | Example |
|-------|-------------|------------------|---------|
| **webAppName** | Web App name (globally unique) | `{agentName}-webapp-{timestamp}` | `myagent-webapp-11140916` |
| **agentIdentityDisplayName** | Agent identity in Azure AD | `{agentName} Identity` | `myagent Identity` |
| **agentBlueprintDisplayName** | Agent blueprint name | `{agentName} Blueprint` | `myagent Blueprint` |
| **agentUserPrincipalName** | UPN for the agentic user | `agent.{agentName}.{timestamp}@domain` | `agent.myagent.11140916@yourdomain.onmicrosoft.com` |
| **agentUserDisplayName** | Display name for agentic user | `{agentName} Agent User` | `myagent Agent User` |
| **agentDescription** | Description of your agent | `{agentName} - Agent 365 Demo Agent` | `myagent - Agent 365 Demo Agent` |

### User-Provided Fields

| Field | Description | Validation | Example |
|-------|-------------|------------|---------|
| **managerEmail** | Email of the agent's manager | Email format | `manager@contoso.com` |
| **deploymentProjectPath** | Path to agent project directory | Directory exists, platform detection | `C:\projects\my-agent` |
| **agentUserUsageLocation** | Country code for license | Auto-detected from Azure account | `US` |

## Command Options

```bash
# Display help
a365 config init --help

# Import existing configuration file
a365 config init --configfile path/to/config.json
a365 config init -c path/to/config.json

# Create config in global directory (AppData)
a365 config init --global
a365 config init -g
```

## Generated Configuration File

After completing the wizard, `a365.config.json` is created:

```json
{
  "tenantId": "adfa4542-3e1e-46f5-9c70-3df0b15b3f6c",
  "clientAppId": "a1b2c3d4-e5f6-g7h8-i9j0-k1l2m3n4o5p6",
  "subscriptionId": "e09e22f2-9193-4f54-a335-01f59575eefd",
  "resourceGroup": "a365demorg",
  "location": "westus",
  "environment": "prod",
  "appServicePlanName": "a365agent-app-plan",
  "appServicePlanSku": "B1",
  "webAppName": "myagent-webapp-11140916",
  "agentIdentityDisplayName": "myagent Identity",
  "agentBlueprintDisplayName": "myagent Blueprint",
  "agentUserPrincipalName": "agent.myagent.11140916@yourdomain.onmicrosoft.com",
  "agentUserDisplayName": "myagent Agent User",
  "managerEmail": "manager@contoso.com",
  "agentUserUsageLocation": "US",
  "deploymentProjectPath": "C:\\projects\\my-agent",
  "agentDescription": "myagent - Agent 365 Demo Agent"
}
```

## Smart Default Generation

The wizard uses intelligent algorithms to generate defaults:

### Agent Name Derivation

**Input**: `myagent`

**Generated Names**:
```
webAppName               = myagent-webapp-11140916
agentIdentityDisplayName = myagent Identity
agentBlueprintDisplayName = myagent Blueprint
agentUserPrincipalName   = agent.myagent.11140916@yourdomain.onmicrosoft.com
agentUserDisplayName     = myagent Agent User
agentDescription         = myagent - Agent 365 Demo Agent
```

**Timestamp**: `MMddHHmm` format (e.g., `11140916` = Nov 14, 09:16 AM)

### Usage Location Detection

Automatically determined from Azure account home tenant location:
- US-based tenants → `US`
- UK-based tenants → `GB`
- Canada-based tenants → `CA`
- Falls back to `US` if unable to detect

## Validation Rules

### Deployment Project Path

- **Existence**: Directory must exist on the file system
- **Platform Detection**: Warns if no supported project type (.NET, Node.js, Python) is detected
- **Confirmation**: User can choose to continue even without detected platform

```
WARNING: Could not detect a supported project type (.NET, Node.js, or Python)
in the specified directory.
Continue anyway? (y/N):
```

### Resource Group

- **Existence**: Must select from existing resource groups or create new
- **Format**: Azure naming conventions (lowercase, alphanumeric, hyphens)

### App Service Plan

- **Scope**: Must exist in the selected resource group
- **Fallback**: Option to create new plan if none exist

### Manager Email

- **Format**: Valid email address (contains `@` and domain)

- **Format**: Valid email address (contains `@` and domain)

## Azure CLI Integration

The wizard leverages Azure CLI for automatic resource discovery:

### Prerequisites

```bash
# Install Azure CLI (if not already installed)
# Windows: https://learn.microsoft.com/cli/azure/install-azure-cli-windows
# macOS: brew install azure-cli
# Linux: curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash

# Login to Azure
az login

# Set active subscription (if you have multiple)
az account set --subscription "My Subscription"

# Verify current account
az account show
```

### What the Wizard Fetches

1. **Current Azure Account**:
   - Subscription ID and Name
   - Tenant ID
   - User information
   - Home tenant location (for usage location)

2. **Resource Groups**:
   - Lists all resource groups in your subscription
   - Allows selection or creation of new group

3. **App Service Plans**:
   - Lists plans in the selected resource group
   - Filters by location compatibility
   - Shows SKU and pricing tier

4. **Azure Locations**:
   - Lists available Azure regions
   - Suggests location based on account or existing config

### Error Handling

**Not logged in**:
```
ERROR: You are not logged in to Azure CLI.
Please run 'az login' and then try again.
```

**Solution**: Run `az login` and complete browser authentication

**Multiple subscriptions**:
```
Subscription ID: e09e22f2-9193-4f54-a335-01f59575eefd (Subscription 1)

NOTE: To use a different Azure subscription, run 'az login' and then
'az account set --subscription <subscription-id>' before running this command.
```

**Solution**: Set desired subscription with `az account set`

## Updating Existing Configuration

Re-run the wizard to update your configuration:

```bash
# Wizard will load existing values as defaults
a365 config init

# Or import from a different file
a365 config init --configfile production.config.json
```

**Workflow**:
1. Wizard detects existing `a365.config.json`
2. Displays message: "Found existing configuration. Default values will be used where available."
3. Each prompt shows current value in brackets: `[current-value]`
4. Press **Enter** to keep current value
5. Type new value to update

**Example**:
```
Agent name [myagent]: myagent-v2
Deployment project path [C:\projects\my-agent]:  ← Press Enter to keep
Resource group [a365demorg]: new-rg  ← Type to update
```

### Setup Command

```bash
# Initialize config first
a365 config init

# Then run setup to create Azure resources and agent blueprint
a365 setup
```

The `setup` command uses:
- **Azure Infrastructure fields**: To create App Service, Plan, and Resource Group
- **Agent Identity fields**: To create agent blueprint and agentic user
- **deploymentProjectPath**: To detect project platform (DotNet, Node.js, Python)

### Create Instance Command

```bash
# Create agent instance after setup
a365 create-instance identity
```

Uses:
- **agentUserPrincipalName**: To create the agentic user in Azure AD
- **agentUserDisplayName**: Display name shown in Microsoft 365
- **managerEmail**: To assign a manager to the agent user
- **agentUserUsageLocation**: For license assignment

### Deploy Command

```bash
# Deploy your agent to Azure
a365 deploy app
```

Uses:
- **deploymentProjectPath**: Source code location
- **webAppName**: Deployment target
- **resourceGroup**: Azure resource location

## Updating Existing Configuration

You can edit `a365.config.json` manually or re-run `a365 config init`:

```bash
# Load existing config and update specific fields
a365 config init --config a365.config.json
```

The CLI will:
1. Load current values from the file
2. Show them as defaults in prompts
3. Press **Enter** to keep existing values
4. Or type new values to update

## Best Practices

### 1. Use Descriptive Names

```json
{
  "agentIdentityDisplayName": "Support Agent - Production",
  "agentUserPrincipalName": "support.agent.prod@contoso.onmicrosoft.com",
  "agentUserDisplayName": "Support Agent (Prod)"
}
```

### 2. Follow Naming Conventions

- **Resource names**: Use lowercase with hyphens (`my-agent-rg`)
- **Display names**: Use Title Case (`My Agent Identity`)
- **UPNs**: Use descriptive prefixes (`support.agent`, `demo.agent`)

### 3. Organize by Environment

```
configs/
  ??? a365.config.dev.json
  ??? a365.config.staging.json
  ??? a365.config.prod.json
```

```bash
# Use environment-specific configs
a365 setup --config configs/a365.config.prod.json
```

### 4. Secure Sensitive Data

**? Safe to commit** (public information):
- `a365.config.json` (static configuration)

**? Never commit** (sensitive secrets):
- `a365.generated.config.json` (contains secrets like client secrets)
- Add to `.gitignore`:

```gitignore
# Agent 365 generated configs with secrets
a365.generated.config.json
```

## Troubleshooting

### Issue: "Directory does not exist"

**Symptom**: Path validation fails during config init

**Solution**:
```bash
# Create the directory first
mkdir my-agent
cd my-agent

# Then run config init
a365 config init
```

### Issue: "Invalid UPN format"

**Symptom**: Agent User Principal Name validation fails

**Solution**: Ensure format is `username@domain`:
```
? Correct: demo.agent@contoso.onmicrosoft.com
? Incorrect: demo.agent (missing domain)
```

### Issue: "Web App name already exists"

**Symptom**: Setup fails because web app name is taken

**Solution**: Web app names must be globally unique in Azure:
```json
{
  "webAppName": "my-agent-webapp-prod-12345"
}
```

## Command Options

```bash
# Display help
a365 config init --help

# Specify custom config file path
a365 config init --config path/to/config.json

# Specify custom output path (generated config)
a365 config init --output path/to/generated.json
```

## Next Steps

After running `a365 config init`:

1. **Review the generated config**:
   ```bash
   # View static configuration
   a365 config display
   ```

2. **Run setup** to create Azure resources:
   ```bash
   a365 setup
   ```

3. **Deploy your agent**:
   ```bash
   a365 deploy
   ```

## Additional Resources

- **Command Reference**: [a365 config display](config-display.md)
- **Setup Guide**: [a365 setup](setup.md)
- **Deployment Guide**: [a365 deploy](deploy.md)
- **GitHub Issues**: [Agent 365 Repository](https://github.com/microsoft/Agent365-devTools/issues)
- **Documentation**: [Microsoft Learn](https://learn.microsoft.com/agent365)

