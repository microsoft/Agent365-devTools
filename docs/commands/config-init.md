# Agent 365 CLI - Configuration Initialization Guide

> **Command**: `a365 config init`  
> **Purpose**: Initialize your Agent 365 configuration with all required settings for deployment

## Overview

The `a365 config init` command walks you through creating a complete configuration file (`a365.config.json`) for your Agent 365 deployment. This interactive process collects essential information about your Azure subscription, agent identity, and deployment settings.

## Quick Start

```bash
# Initialize configuration with interactive prompts
a365 config init

# Use existing config as starting point
a365 config init --config path/to/existing/a365.config.json
```

## Configuration Fields

### Azure Infrastructure

| Field | Description | Example | Required |
|-------|-------------|---------|----------|
| **tenantId** | Azure AD Tenant ID | `12345678-1234-...` | ? Yes |
| **subscriptionId** | Azure Subscription ID | `87654321-4321-...` | ? Yes |
| **resourceGroup** | Azure Resource Group name | `my-agent-rg` | ? Yes |
| **location** | Azure region | `eastus`, `westus2` | ? Yes |
| **appServicePlanName** | App Service Plan name | `my-agent-plan` | ? Yes |
| **appServicePlanSku** | Service Plan SKU | `B1`, `S1`, `P1V2` | ? No (defaults to `B1`) |
| **webAppName** | Web App name (must be globally unique) | `my-agent-webapp` | ? Yes |

### Agent Identity

| Field | Description | Example | Required |
|-------|-------------|---------|----------|
| **agentIdentityDisplayName** | Name shown in Azure AD for the agent identity | `My Agent Identity` | ? Yes |
| **agentBlueprintDisplayName** | Name for the agent blueprint | `My Agent Blueprint` | ? Yes |
| **agentUserPrincipalName** | UPN for the agentic user | `demo.agent@contoso.onmicrosoft.com` | ? Yes |
| **agentUserDisplayName** | Display name for the agentic user | `Demo Agent` | ? Yes |
| **agentDescription** | Description of your agent | `My helpful support agent` | ? No |
| **managerEmail** | Email of the agent's manager | `manager@contoso.com` | ? No |
| **agentUserUsageLocation** | Country code for license assignment | `US`, `GB`, `DE` | ? No (defaults to `US`) |

### Deployment Settings

| Field | Description | Example | Required |
|-------|-------------|---------|----------|
| **deploymentProjectPath** | Path to agent project directory | `C:\projects\my-agent` or `./my-agent` | ? Yes |

## Interactive Prompts

When you run `a365 config init`, you'll see detailed prompts for each field:

### Example: Agent User Principal Name

```
----------------------------------------------
 Agent User Principal Name (UPN)
----------------------------------------------
Description : Email-like identifier for the agentic user in Azure AD.
              Format: <username>@<domain>.onmicrosoft.com or @<verified-domain>
              Example: demo.agent@contoso.onmicrosoft.com
              This must be unique in your tenant.

Current Value: [agent.john@yourdomain.onmicrosoft.com]

> demo.agent@contoso.onmicrosoft.com
```

### Example: Deployment Project Path

```
----------------------------------------------
 Deployment Project Path
----------------------------------------------
Description : Path to your agent project directory for deployment.
              This should contain your agent's source code and configuration files.
              The directory must exist and be accessible.
              You can use relative paths (e.g., ./my-agent) or absolute paths.

Current Value: [C:\Users\john\projects\current-directory]

> ./my-agent
```

## Field Validation

The CLI validates your input to catch errors early:

### Agent User Principal Name (UPN)

? **Valid formats**:
- `demo.agent@contoso.onmicrosoft.com`
- `support-bot@verified-domain.com`

? **Invalid formats**:
- `invalidupn` (missing @)
- `user@` (missing domain)
- `@domain.com` (missing username)

### Deployment Project Path

? **Valid paths**:
- `./my-agent` (relative path)
- `C:\projects\my-agent` (absolute path)
- `../parent-folder/my-agent` (parent directory)

? **Invalid paths**:
- `Z:\nonexistent\path` (directory doesn't exist)
- `C:\|invalid` (illegal characters)

### Empty Values

All required fields must have values:

```
? This field is required. Please provide a value.
```

## Generated Configuration File

After completing the prompts, `a365 config init` creates `a365.config.json`:

```json
{
  "tenantId": "12345678-1234-1234-1234-123456789012",
  "subscriptionId": "87654321-4321-4321-4321-210987654321",
  "resourceGroup": "my-agent-rg",
  "location": "eastus",
  "appServicePlanName": "my-agent-plan",
  "appServicePlanSku": "B1",
  "webAppName": "my-agent-webapp",
  "agentIdentityDisplayName": "My Agent Identity",
  "agentBlueprintDisplayName": "My Agent Blueprint",
  "agentUserPrincipalName": "demo.agent@contoso.onmicrosoft.com",
  "agentUserDisplayName": "Demo Agent",
  "deploymentProjectPath": "C:\\projects\\my-agent",
  "agentDescription": "My helpful support agent",
  "managerEmail": "manager@contoso.com",
  "agentUserUsageLocation": "US"
}
```

## Smart Defaults

The CLI provides intelligent defaults based on your environment:

| Field | Default Value | Logic |
|-------|---------------|-------|
| **agentIdentityDisplayName** | `John's Agent 365 Instance 20241112T153045` | `<Username>'s Agent 365 Instance <Timestamp>` |
| **agentBlueprintDisplayName** | `John's Agent 365 Blueprint` | `<Username>'s Agent 365 Blueprint` |
| **agentUserPrincipalName** | `agent.john@yourdomain.onmicrosoft.com` | `agent.<username>@yourdomain.onmicrosoft.com` |
| **agentUserDisplayName** | `John's Agent User` | `<Username>'s Agent User` |
| **deploymentProjectPath** | `C:\projects\current-directory` | Current working directory |
| **agentUserUsageLocation** | `US` | United States |

## Usage with Other Commands

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
   cat a365.config.json
   ```

2. **Run setup** to create Azure resources:
   ```bash
   a365 setup
   ```

3. **Create agent instance**:
   ```bash
   a365 create-instance
   ```

4. **Deploy your agent**:
   ```bash
   a365 deploy
   ```

## Support

For issues or questions:
- **GitHub Issues**: [Agent 365 Repository](https://github.com/microsoft/Agent365-devTools/issues)
- **Documentation**: [Microsoft Learn](https://learn.microsoft.com/agent365)
- **Community**: [Microsoft Tech Community](https://techcommunity.microsoft.com)
