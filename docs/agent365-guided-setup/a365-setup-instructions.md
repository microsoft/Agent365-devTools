# Agent 365 CLI Setup Instructions for AI Agents

---

> **YOUR FIRST AND ONLY ACTION RIGHT NOW:** Create exactly 2 todos (Todo 1 and Todo 2 only — the remaining todos are created at the end of Step 2 once your path is determined). Mark Todo 1 in-progress and jump to Step 1. **Do NOT read further. Do NOT run any commands. Do NOT gather values. Do NOT ask questions.**
>
> - Todo 1: `Step 1: Verify and Install/Update the Agent 365 CLI`
> - Todo 2: `Step 2: Ensure Prerequisites and Environment Configuration`

**RULE 1 — CREATE 3 OR 5 TODOS DEPENDING ON PATH (determined at end of Step 2).**

Do NOT create all todos upfront. Create only Todo 1 and Todo 2 now. At the end of Step 2, you will ask the user which path they are on, then create the remaining todos:

**AI Teammate path (5 todos total):**
- Todo 1: `Step 1: Verify and Install/Update the Agent 365 CLI`
- Todo 2: `Step 2: Ensure Prerequisites and Environment Configuration`
- Todo 3: `Step 3: Configure the Agent 365 CLI (Initialize Configuration)`
- Todo 4: `Step 4: Run Agent 365 Setup to Provision Prerequisites`
- Todo 5: `Step 5: Publish and Deploy the Agent Application`

**Standard path (3 todos total):**
- Todo 1: `Step 1: Verify and Install/Update the Agent 365 CLI`
- Todo 2: `Step 2: Ensure Prerequisites and Environment Configuration`
- Todo 3: `Step 4: Run Agent 365 Setup to Provision Prerequisites`

**RULE 2 — ALWAYS BEGIN FROM STEP 1.** No step is optional within your path. Even if the CLI appears installed or Azure appears logged in, you MUST run the validation commands in each step. Step 3 (Configure) is only required on the AI Teammate path — it is skipped entirely on the Standard path.

**RULE 3 — SUB-SECTIONS ARE NOT SEPARATE TODOS.** Each `## Step` has internal sub-sections — these are tasks WITHIN that step, NOT separate todos.

**RULE 4 — ONE STEP AT A TIME.** Complete each step fully. Mark its todo in-progress when starting, complete when done. Do NOT run `az account show`, ask about deployment type, gather Azure values, or ask about AI Teammate mode — those belong to Steps 3 and 2 respectively.

**RULE 6 — SILENT EXECUTION.** Work silently. Do NOT narrate what you are about to do, announce step transitions ("Proceeding to Step 2", "CLI installed, moving on"), print todo state, emoji checklists, or step completion summaries. Only speak to the user when you need input, have an error to report, or need confirmation before a destructive action.

**RULE 5 — INPUT FIELDS.** In Step 3 (AI Teammate path only), present exactly 5 fields (Azure-hosted) or 2 fields (self-hosted). Do NOT ask the user for a client app ID — the CLI resolves it automatically by the well-known app name "Agent 365 CLI".

---

## Context

You are an AI coding agent with access to execute shell commands, read the Agent365-devTools repository (code and docs), and browse the web for documentation or GitHub issues. Your task is to set up, configure, and deploy all prerequisite components for a Microsoft Agent 365–compliant agent using the Agent 365 CLI. You must handle this end-to-end: from installation and configuration to deployment. Work step-by-step, and adapt to any issues or differences in CLI versions along the way.

> **CRITICAL BLOCKING PREREQUISITE:** Before running ANY `a365` CLI commands (including `config init`, `setup`, `publish`, or `deploy`), you MUST validate that the custom client app registration exists in Entra ID with all required permissions and admin consent. This is validated in Step 2. Failure to validate this will cause all CLI commands to fail. Do NOT skip this validation step.

---

## Step 1: Verify and Install/Update the Agent 365 CLI

> **DO NOT SKIP THIS STEP.** Even if you believe the CLI is already installed, you MUST run the version check and validate. Mark this todo in-progress now.

Check if the Agent 365 CLI is installed and up-to-date:

- Run a version check (e.g. `a365 --version` or `a365 -h`).
- If the CLI is not installed or the command is not found, you need to install it. If it is installed but the version is outdated, you should update it to the latest preview version.

### Ensure .NET is installed

The Agent 365 CLI is a .NET global tool. Verify that you have .NET 8.0 (or a compatible version) available by running `dotnet --version`. If not, instruct the user to install .NET 8.0 or install it yourself if you have the ability (the CLI cannot run without this).

### Install or update the Agent 365 CLI

Use the [official documentation](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-cli#install-the-agent-365-cli) to install/update the CLI globally. Always include the `--prerelease` flag to get the latest preview:

- **If not installed:** run `dotnet tool install --global Microsoft.Agents.A365.DevTools.Cli --prerelease`
- **If an older version is installed:** run `dotnet tool update --global Microsoft.Agents.A365.DevTools.Cli --prerelease`
- **On Windows environments:** If the above command fails or if you prefer, you can use the provided PowerShell script from the repository to install the CLI. For example, run the `scripts/cli/install-cli.ps1` script (after uninstalling any existing version with `dotnet tool uninstall -g Microsoft.Agents.A365.DevTools.Cli`).

### Verify installation

After installing or updating, confirm the CLI is ready by running `a365 -h` to display help. This also ensures the CLI is on the PATH. It should show usage information rather than an error.

### Adapt to CLI version differences

The CLI is under active development, and some commands may have changed in recent versions. The instructions in this prompt assume you have the latest version. If you discover that a command referenced later (such as `publish`) is not recognized, it means you have an older version – in that case, upgrade the CLI. Using the latest version is essential because older flows (e.g. the `create-instance` command) have been deprecated in favor of new commands (`publish`, etc.). If upgrading isn't possible, adjust your steps according to the older CLI's documentation (for example, use the old `a365 create-instance` command in place of `publish`), but prefer to upgrade if at all feasible.

### Step 1 completion

> **BEFORE MOVING ON:** Mark Todo 1 (Step 1) as **completed** now. Then mark Todo 2 (Step 2) as **in-progress**. Only then proceed to Step 2 below. Do NOT jump ahead to Step 3.

---

## Step 2: Ensure Prerequisites and Environment Configuration

> **DO NOT SKIP THIS STEP.** You MUST validate Azure CLI login, Entra ID roles, the custom client app registration, and language-specific build tools. These validations are required before ANY `a365` CLI commands will work. Mark this todo in-progress now.

### Azure CLI & Authentication

The Agent 365 CLI relies on Azure context for deploying resources and may use your Azure credentials. Verify that the Azure CLI (`az`) is installed by running `az --version`. If it's not available, install the [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) for your platform or prompt the user to do so.

If the Azure CLI is installed, ensure that you are logged in to the correct Azure account and tenant. Run `az login` (and `az account set -s <SubscriptionNameOrID>` if you need to select a specific subscription). If you cannot perform an interactive login directly, output a clear instruction for the user to log in (the user may need to follow a device-code login URL if running in a headless environment). The Agent 365 CLI will use this Azure authentication context to create resources.

### Microsoft Entra ID (Azure AD) roles

The user account you authenticate with must have sufficient privileges to create the necessary resources. According to documentation, the account needs to be at least an **Agent ID Administrator** or **Agent ID Developer**, and certain commands (like the full environment setup) require **Global Administrator + Azure Contributor** roles. If you attempt an operation without adequate permissions, it will fail. Thus, before proceeding, confirm that the logged-in user has one of the required roles (Global Admin is the safest choice for preview setups). If not, prompt the user to either use an appropriate account or have an admin grant the needed roles.

### Custom client app

The Agent 365 CLI resolves the client app automatically by the well-known display name **"Agent 365 CLI"** registered in the tenant. Do NOT ask the user for a client app ID.

The CLI will validate permissions and prompt for consent at runtime if anything is missing. If the CLI reports that the "Agent 365 CLI" app cannot be found in the tenant, inform the user that an admin must register an Entra app with that exact display name and grant admin consent for the required permissions, then retry.

### Validate language-specific prerequisites (REQUIRED)

> **BLOCKING PREREQUISITE:** You MUST validate that language-specific build tools are installed BEFORE proceeding to Step 3. The deployment will fail if the agent's code cannot be built. Do NOT skip this validation step.

The Agent 365 CLI supports .NET, Node.js, and Python projects. You MUST check that the relevant runtime and build tools are installed for the project type you are deploying.

#### Detect project type

First, detect the project type by checking for project files in the deployment directory:

```bash
# Check for .NET project (.csproj)
find . -name "*.csproj" -print -quit

# Check for Node.js project (package.json)
test -f "package.json" && echo "Node.js project detected"

# Check for Python project (requirements.txt or pyproject.toml)
{ test -f "requirements.txt" || test -f "pyproject.toml"; } && echo "Python project detected"
```

#### Validate required tools based on project type

**For .NET agents (REQUIRED if .csproj files exist):**

Run these commands and verify the output:
```bash
dotnet --version
dotnet --list-sdks
```

- [ ] Confirm .NET SDK 8.0 or later is installed
- [ ] If not installed, instruct the user to install .NET 8.0 SDK from https://dotnet.microsoft.com/download

**For Node.js agents (REQUIRED if package.json exists):**

Run these commands and verify the output:
```bash
node --version
npm --version
```

- [ ] Confirm Node.js 18.x or later is installed
- [ ] Confirm npm is available
- [ ] If not installed, instruct the user to install Node.js from https://nodejs.org/

**For Python agents (REQUIRED if requirements.txt or pyproject.toml exists):**

Run these commands and verify the output:
```bash
python --version
pip --version
```

- [ ] Confirm Python 3.10 or later is installed
- [ ] Confirm pip is available
- [ ] If not installed, instruct the user to install Python from https://python.org/

#### Validation checkpoint

> **STOP AND CONFIRM:** Before proceeding to Step 3, you MUST have validated:
> - [ ] Project type detected (at least one of: .NET, Node.js, or Python)
> - [ ] Required build tools installed and verified for the detected project type
> - [ ] All previous Step 2 validations passed (Azure CLI, custom client app, permissions)
>
> If any validation failed, resolve the issue before continuing. Do NOT proceed to Step 3 until all checks pass.

### Step 2 completion

> **BEFORE MOVING ON:** Mark Todo 2 (Step 2) as **completed** now. Summarize to the user what was validated. Then proceed to the path determination section below — do NOT jump to Step 3 yet.

---

### Determine your setup path (REQUIRED before proceeding)

**STOP. Ask the user the following question and wait for their response before doing anything else:**

---

**Is this agent being set up as an AI Teammate (Digital Worker)?**

- **Yes (AI Teammate)** — the agent will be registered as a managed Digital Worker in your tenant.
- **No (Standard agent)** — a regular agent that shows up in Agent Registry.

Reply with **yes** or **no**.

---

> **STOP. Do NOT proceed until the user has answered.**

**If the user answers yes (AI Teammate path):**

Store `isAITeammate = true`. Create todos 3, 4, and 5:
- Todo 3: `Step 3: Configure the Agent 365 CLI (Initialize Configuration)`
- Todo 4: `Step 4: Run Agent 365 Setup to Provision Prerequisites`
- Todo 5: `Step 5: Publish and Deploy the Agent Application`

Mark Todo 3 in-progress. Proceed to Step 3.

> **VERIFY YOUR TODO STATE (AI Teammate path):**
> - Todo 1: **completed** | Todo 2: **completed** | Todo 3: **in-progress** | Todo 4: not-started | Todo 5: not-started

**If the user answers no (Standard path):**

Store `isAITeammate = false`. Create todo 3:
- Todo 3: `Step 4: Run Agent 365 Setup to Provision Prerequisites`

Mark Todo 3 in-progress. **Skip Step 3 entirely. Jump directly to Step 4.**

> **VERIFY YOUR TODO STATE (Standard path):**
> - Todo 1: **completed** | Todo 2: **completed** | Todo 3: **in-progress**

---

## Step 3: Configure the Agent 365 CLI (Initialize Configuration)

> **AI TEAMMATE PATH ONLY.**
>
> If `isAITeammate = false` (Standard path), you should NOT be here. Go back, mark Todo 3 (Step 4) in-progress, and jump directly to Step 4.
>
> If `isAITeammate = true`, continue below.

> **MANDATORY GATE — DO NOT PROCEED WITHOUT VERIFICATION:**
> 
> Before executing ANY part of this step, verify ALL of the following:
> - [ ] You created exactly 5 todos (AI Teammate path — RULE 1)
> - [ ] Todo 1 (Step 1) is marked **completed** — CLI was verified/installed
> - [ ] Todo 2 (Step 2) is marked **completed** — Azure CLI login confirmed, custom client app validated, build tools verified
> - [ ] Todo 3 (Step 3) is marked **in-progress**
> 
> **If ANY checkbox above is not satisfied, STOP. Go back to the incomplete step and finish it first.**
> 
> Common mistake: Jumping to this step first because it has `az account show` commands. Those commands are for Step 3 ONLY — Steps 1 and 2 must be done first.

Once all prerequisites are in place (CLI installed, Azure CLI logged in, **custom app validated**, **build tools verified**), create the Agent 365 CLI configuration file. The `a365 config init` command is non-interactive, so you must create an `a365.config.json` file directly and then import it.

### Gather auto-detected values

Retrieve the following values automatically using the Azure CLI:

```bash
# Get tenant ID and subscription ID
az account show --query "{tenantId:tenantId, subscriptionId:id}" -o json
```

You should already have the `clientAppId` from the Step 2 validation.

Set `deploymentProjectPath` to the current working directory (use absolute path).

### Ask deployment type

Send the user the following message and then **STOP and WAIT for their reply**. Your message must contain **ONLY** the text below — no tables, no input fields, no additional questions, no follow-up content:

---

**Do you want to create a web app in Azure for this agent? (yes/no)**

- **Yes** = Azure-hosted (recommended for production)
- **No** = Self-hosted (e.g., local development with dev tunnel)

---

> ⛔ **STOP. OUTPUT ONLY THE QUESTION ABOVE. DO NOT INCLUDE ANYTHING ELSE.**
> Do NOT show input fields. Do NOT show a table. Do NOT mention resource groups, agent names, or any configuration values.
> The next section ("Collect configuration inputs") must NOT appear in this message.
> WAIT for the user to respond before doing anything else.

After the user responds, set the internal value:
- If **yes**: `needDeployment: true`
- If **no**: `needDeployment: false`

Then proceed to "Collect configuration inputs" below.

---

### Collect configuration inputs

> ⛔ **DO NOT EXECUTE THIS SECTION** until the user has answered the deployment type question above.
> If you have not yet received the user's yes/no answer, STOP and go back to ask it.

#### First: Query the subscription for real example values

Before presenting input fields, run the following **single command** to gather real values from the user's Azure subscription. Use these values as **examples** in the input table so the user sees context-specific suggestions instead of generic placeholders.

```bash
az ad signed-in-user show --query userPrincipalName -o tsv; az group list --query "[].{Name:name, Location:location}" -o table; az appservice plan list --query "[].{Name:name, ResourceGroup:resourceGroup, Location:location}" -o table
```

> **Run this as ONE command.** Do NOT split into separate terminal calls.

From the output, extract:
- `{loggedInUser}` — the signed-in user's UPN (e.g., `admin@contoso.onmicrosoft.com`)
- `{existingResourceGroup}` — name of an existing resource group (e.g., `agent365-rg`)
- `{existingLocations}` — locations from the resource groups (e.g., `eastus, canadacentral, westus2`)
- `{existingAppServicePlan}` — name of an existing App Service plan (e.g., `agent365-plan`)

If a query returns no results (e.g., no existing resource groups or App Service plans), use a descriptive fallback like `my-agent-rg` or `my-agent-plan`.

#### Present the input fields

Based on the user's deployment type answer, present the appropriate set of input fields **with the real values you queried above as examples**.

#### If Azure-hosted (`needDeployment: true`)

Present the following fields in a single prompt:

**"Please provide the following values to configure your Azure-hosted agent:"**

| Field | Description | Example |
|-------|-------------|---------|
| **Resource Group** | Azure Resource Group (new or existing) | `{existingResourceGroup}` |
| **Location** | Azure region for deployment | `{existingLocations}` |
| **Agent Name** | Unique name for your agent (see rules below) | `contoso-support-agent` |
| **Manager Email** | M365 manager email (must be from your tenant) | `{loggedInUser}` |
| **App Service Plan** | Azure App Service Plan name | `{existingAppServicePlan}` |

> **Agent Name rules:** Must be **globally unique across all of Azure**. Used to derive the web app URL (`{name}-webapp.azurewebsites.net`), Agent Identity, Blueprint, and User Principal Name. Lowercase letters, numbers, hyphens only. Start with a letter. 3-20 chars recommended. Tip: include your org name.
>
> **Examples** show real values from your subscription. You can reuse existing resources or provide new names — the CLI will create them if they don't exist.
>
> **Do NOT ask for `clientAppId` here.** It was already collected and validated in Step 2. Present ONLY the 5 fields listed above.

#### If self-hosted (`needDeployment: false`)

Present the following fields in a single prompt:

**"Please provide the following values to configure your self-hosted agent:"**

| Field | Description | Example |
|-------|-------------|---------|
| **Resource Group** | Azure Resource Group (new or existing) | `{existingResourceGroup}` |
| **Location** | Azure region for deployment | `{existingLocations}` |
| **Agent Name** | Unique name for your agent (see rules below) | `contoso-support-agent` |
| **Manager Email** | M365 manager email (must be from your tenant) | `{loggedInUser}` |

> **Agent Name rules:** Must be **globally unique across all of Azure**. Used to derive Agent Identity, Blueprint, and User Principal Name. Lowercase letters, numbers, hyphens only. Start with a letter. 3-20 chars recommended. Tip: include your org name.

After collecting these inputs, proceed to Step 3.3.1 to determine the messaging endpoint.

#### After receiving the user's answers

1. **Validate the inputs** — Check that all required fields are provided, the email format looks valid, and the agent name meets the naming requirements.
2. **If any field is missing or unclear**, ask only about that specific field — do not re-ask for all inputs.
3. **Proceed** to derive naming values (or determine the messaging endpoint first for self-hosted deployments).

#### Determine messaging endpoint (non-Azure deployments only)

Only perform this step if the user chose self-hosted deployment.

Ask: **"Would you like to use a dev tunnel for local development, or provide a custom messaging endpoint? (devtunnel/custom)"**

Provide this context:
- **Dev tunnel**: Creates a secure tunnel from the internet to your local machine. Ideal for development and testing - no need to deploy your code anywhere. The tunnel URL will be your messaging endpoint.
- **Custom endpoint**: Use this if you already have a publicly accessible HTTPS URL where your agent is hosted (e.g., on another cloud provider, on-premises with a public IP, or behind a reverse proxy).

- If **devtunnel**: Proceed to set up a dev tunnel (next section). The dev tunnel URL will be used as the `messagingEndpoint`.
- If **custom**: Ask the user to provide their `messagingEndpoint` URL (e.g., `https://myagent.example.com/api/messages`).

#### Set up a dev tunnel (for local development)

### Derive naming values from base name

Using the `agentBaseName` provided by the user and the domain extracted from `managerEmail`, derive the following values:

| Field | Pattern | Example (baseName=`mya365agent`, domain=`contoso.onmicrosoft.com`) |
|-------|---------|---------|
| `agentIdentityDisplayName` | `{baseName} Identity` | `mya365agent Identity` |
| `agentBlueprintDisplayName` | `{baseName} Blueprint` | `mya365agent Blueprint` |
| `agentUserPrincipalName` | `UPN.{baseName}@{domain}` | `UPN.mya365agent@contoso.onmicrosoft.com` |
| `agentUserDisplayName` | `{baseName} Agent User` | `mya365agent Agent User` |
| `agentDescription` | `{baseName} - Agent 365 Agent` | `mya365agent - Agent 365 Agent` |
| `webAppName` (Azure-hosted only) | `{baseName}-webapp` | `mya365agent-webapp` |

### Confirm derived values with user

After deriving the values above, present them to the user and ask for confirmation. Display the derived values in a clear format:

**"Based on your inputs, the following values have been derived as defaults:"**

| Field | Derived Value |
|-------|---------------|
| `agentIdentityDisplayName` | `{baseName} Identity` |
| `agentBlueprintDisplayName` | `{baseName} Blueprint` |
| `agentUserPrincipalName` | `UPN.{baseName}@{domain}` |
| `agentUserDisplayName` | `{baseName} Agent User` |
| `agentDescription` | `{baseName} - Agent 365 Agent` |
| `webAppName` (if Azure-hosted) | `{baseName}-webapp` |

Then ask: **"Would you like to update any of these derived values, or proceed with the defaults? (update/proceed)"**

- If the user chooses **"proceed"**: Continue to create the config file with the derived default values.
- If the user chooses **"update"**: Ask which field(s) they want to change and collect the new value(s). After updates, display the final values again for confirmation before proceeding.

### Create the a365.config.json file

Create the `a365.config.json` file in the current working directory with all gathered and derived values.

**Template for Azure-hosted deployment** (`needDeployment: true`):

```json
{
  "tenantId": "<from az account show>",
  "subscriptionId": "<from az account show>",
  "resourceGroup": "<user provided>",
  "location": "<user provided>",
  "environment": "prod",
  "needDeployment": true,
  "clientAppId": "<from Step 2 validation>",
  "appServicePlanName": "<user provided>",
  "webAppName": "<derived from baseName>",
  "agentIdentityDisplayName": "<derived from baseName>",
  "agentBlueprintDisplayName": "<derived from baseName>",
  "agentUserPrincipalName": "<derived from baseName and domain>",
  "agentUserDisplayName": "<derived from baseName>",
  "managerEmail": "<user provided>",
  "agentUserUsageLocation": "US",
  "deploymentProjectPath": "<current working directory>",
  "agentDescription": "<derived from baseName>"
}
```

**Template for non-Azure hosted deployment** (`needDeployment: false`):

```json
{
  "tenantId": "<from az account show>",
  "subscriptionId": "<from az account show>",
  "resourceGroup": "<user provided>",
  "location": "<user provided>",
  "environment": "prod",
  "messagingEndpoint": "<user provided>",
  "needDeployment": false,
  "clientAppId": "<from Step 2 validation>",
  "agentIdentityDisplayName": "<derived from baseName>",
  "agentBlueprintDisplayName": "<derived from baseName>",
  "agentUserPrincipalName": "<derived from baseName and domain>",
  "agentUserDisplayName": "<derived from baseName>",
  "managerEmail": "<user provided>",
  "agentUserUsageLocation": "US",
  "deploymentProjectPath": "<current working directory>",
  "agentDescription": "<derived from baseName>"
}
```

### Import the configuration

After creating the `a365.config.json` file, import it using:

```bash
a365 config init -c ./a365.config.json
```

### Validation

The `config init` process will attempt to validate your inputs. Notably, it will check:

- That the provided Application (client) ID corresponds to an existing app in the tenant and that it has the required permissions (the CLI might automatically verify the presence of the Graph permissions and admin consent). If this validation fails (for example, "app not found" or "missing permission X"), do not proceed further until the issue is resolved. Refer back to the app registration guide and fix the configuration (you may need the user's help to adjust the app's settings or wait for an admin consent).
- **Azure subscription and resource availability:** it might check that the subscription ID is accessible and you have Contributor rights (if you logged in via Azure CLI, this should be okay).
- It could also test the project path for a recognizable project (looking for a `.csproj`, `package.json`, or `pyproject.toml` to identify .NET/Node/Python). If it warns that it "could not detect project platform" or similar, double-check the `deploymentProjectPath` you provided. If it's wrong, update it and re-import the configuration.

If any validation fails, correct the `a365.config.json` file and re-run `a365 config init -c ./a365.config.json`.

### Proceed when config is successful

Once `a365 config init` completes without errors, you have a baseline configuration ready. The CLI now knows your environment details and is authenticated. This configuration will be used by subsequent commands.

---

## Step 4: Run Agent 365 Setup to Provision Prerequisites

> **Skill tip:** If you have the Agent 365 devTools repository cloned and the Claude Code skills extension installed, you can use the `/provision` slash command instead of following steps 4.1–4.4. The skill is a packaged version of this exact flow. If `/provision` appears in your Claude Code slash commands, type `/provision <agent_name>` and follow the prompts — then skip the rest of this step.
>
> If you do NOT have the skill installed, continue below. The inline flow is equivalent.

---

### 4.1 — Collect provisioning inputs

**For the Standard path (`isAITeammate = false`):**

Ask the user two questions (one at a time, wait for each response):

1. **"What agent name should be used for provisioning?"**
   - Must be globally unique across Azure
   - Lowercase letters, numbers, and hyphens only; start with a letter; 3–20 characters recommended
   - Example: `contoso-support-agent`
   - If the user replies `default`, use `developer`

   Store as `agent_name`.

2. **"What is the project directory containing your agent code? Reply with a full path, or reply 'current' to use the current working directory."**

   Store as `project_dir`. If the user replies `current`, use the current working directory.

**For the AI Teammate path (`isAITeammate = true`):**

- `agent_name` is derived from `agentBaseName` collected in Step 3 — do NOT ask again.
- `project_dir` is the `deploymentProjectPath` from the config — do NOT ask again.

---

### 4.2 — Dry-run preview (REQUIRED — do not skip)

> **This is a safety check. You MUST run the dry-run and show the output to the user before applying anything.**

Run the following command and display the full output to the user:

**Standard path:**
```bash
cd "<project_dir>" && a365 setup all --agent-name <agent_name> --dry-run
```

**AI Teammate path:**
```bash
cd "<project_dir>" && a365 setup all --dry-run
```

After displaying the full output, ask the user:

**"Do you want to proceed with the setup shown above? (yes/no)"**

- If **no** (or anything other than yes/y): Stop. Tell the user "Setup cancelled. Return to Step 4 when ready." Do NOT proceed.
- If **yes**: Proceed to 4.3.

---

### 4.3 — Apply setup

Run the following command from `project_dir` and stream all output:

**Standard path:**
```bash
cd "<project_dir>" && a365 setup all --agent-name <agent_name>
```

**AI Teammate path:**
```bash
cd "<project_dir>" && a365 setup all
```

This single command performs all necessary setup steps in sequence:
- Creates or validates the Azure infrastructure (Resource Group, App Service Plan, Web App, Managed Identity)
- Creates the Agent 365 Blueprint in Microsoft Entra ID
- Configures the blueprint's permissions
- Registers the messaging endpoint

This command may take several minutes. Monitor output carefully:

- The CLI logs progress in numbered steps (e.g., `[1/5]`, `[2/5]`). Watch for errors or warnings.
- Performance notices (e.g., 32-bit Azure CLI on 64-bit system) are non-blocking — note them but continue.
- If existing resources are detected from a previous run, the CLI will skip recreating them — this is expected.

**Important considerations:**

- **Quota limits:** An error like "Operation cannot be completed without additional quota" means the Azure subscription has hit a capacity limit for that region/SKU. Report this to the user and halt. If possible, update `location` in the config (AI Teammate path) or ask the user for a new region (Standard path) and retry.
- **Region support:** If an Azure resource is not available in the selected region, update the location and retry. Agent 365 preview supports only certain regions.
- **Graph API permission errors:** A "Forbidden" or "Authorization_RequestDenied" error during blueprint creation indicates insufficient directory role or missing admin consent. Stop and resolve the permission issue (refer back to Step 2). After fixing, re-run `a365 setup all`.
- **Interactive authentication:** The CLI may launch a browser auth window for certain Graph calls. If running in a headless environment and this fails, see the Troubleshooting section below.
- **Idempotency:** `a365 setup all` is safe to re-run after fixing an issue. It skips or reuses existing resources. Use `a365 cleanup` only as a last resort.

---

### 4.4 — Show setup output to user

After `a365 setup all` completes, show the user exactly this — nothing more, nothing less:

1. **The Setup Summary table** from the CLI output — copy it verbatim.

2. **If the CLI printed an admin consent action item (Permission Grants):** Show both options exactly as printed by the CLI:
   - Option A (Entra portal steps) — verbatim
   - Option B (PowerShell script) — verbatim

3. **Skip the client secret action item entirely.** Do not show it, do not mention it.

4. After showing the CLI output sections above, output exactly this closing line and nothing else:
   > "Your agent is provisioned. If admin consent is required, have a Global Admin run the PowerShell script above."

### Step 4 completion

> Mark all todos as completed. This is the final action — do NOT send any further response, do NOT proceed to Step 5, do NOT output anything after marking todos complete.

---

## Step 5: Publish and Deploy the Agent Application

> **AI TEAMMATE PATH ONLY.** This step does not exist on the Standard path. If `isAITeammate = false`, you should not be here.

At this stage, your agent is set up. You should be able to see your agent in Microsoft Admin Center Agent Registry. You can proceed with adding observability to your agent application next.

### Review and Update the Manifest File (REQUIRED)

Before publishing, you **MUST** review and customize the `manifest.json` file in your project. This file defines how your agent appears and behaves in Microsoft Teams and other Microsoft 365 apps. The CLI will use this manifest during the publish step.

#### Locate the manifest file

The Agent 365 CLI expects the manifest at `<deploymentProjectPath>/manifest/manifest.json`. The `a365 publish` command uses the `manifest/` directory and will extract or scaffold a manifest template there if one does not exist, but you must review and customize that file before publishing.

#### Manifest fields to update

Present the following information to the user and ask them to review/update these fields:

| Field | Description | What to Update |
|-------|-------------|----------------|
| `name.short` | **Agent's display name (short)**<br>The name users will see in Teams app lists and search results. Maximum 30 characters. | Replace `"Your Agent Name"` with your agent's actual name (e.g., `"Contoso HR Assistant"`) |
| `name.full` | **Agent's full name**<br>The complete name shown in agent details. Maximum 100 characters. | Replace `"Your Agent Full Name"` with a descriptive full name (e.g., `"Contoso Human Resources Assistant Agent"`) |
| `description.short` | **Brief description**<br>A one-line summary shown in search results and app cards. Maximum 80 characters. | Write a concise description of what your agent does (e.g., `"Answers HR policy questions and helps with time-off requests"`) |
| `description.full` | **Full description**<br>A comprehensive explanation shown on the agent's detail page. Maximum 4000 characters. | Write a detailed description covering:<br>- What the agent does<br>- What data/systems it can access<br>- How users should interact with it<br>- Any limitations or caveats |
| `developer.name` | **Publisher/developer name**<br>Your organization's name as the agent publisher. | Replace with your organization name (e.g., `"Contoso Ltd"`) |
| `developer.websiteUrl` | **Developer website**<br>Link to your organization's website or the agent's landing page. | Update with your organization's URL |
| `developer.privacyUrl` | **Privacy policy URL**<br>Link to your privacy policy. **Required for production agents.** | Update with your privacy policy URL |
| `developer.termsOfUseUrl` | **Terms of use URL**<br>Link to your terms of service. **Required for production agents.** | Update with your terms of use URL |
| `icons.color` | **Color icon (192x192 PNG)**<br>Full-color icon for the agent. | Ensure you have a `color.png` file (192x192 pixels) in your project |
| `icons.outline` | **Outline icon (32x32 PNG)**<br>Transparent outline icon with single color. | Ensure you have an `outline.png` file (32x32 pixels) in your project |
| `accentColor` | **Accent color**<br>Hex color code used as background for icons. | Update to match your branding (e.g., `"#0078D4"` for Microsoft blue) |
| `version` | **Manifest version**<br>Semantic version of your agent package. | Update when making changes (e.g., `"1.0.0"`, `"1.2.3"`) |

#### Example manifest customization

Show the user an example of a customized manifest:

```json
{
  "$schema": "https://developer.microsoft.com/en-us/json-schemas/teams/vdevPreview/MicrosoftTeams.schema.json",
  "id": "<auto-generated-by-cli>",
  "name": {
    "short": "Contoso HR Bot",
    "full": "Contoso Human Resources Assistant"
  },
  "description": {
    "short": "Get answers to HR questions and submit time-off requests.",
    "full": "The Contoso HR Assistant helps employees with common HR tasks. You can ask about company policies, check your PTO balance, submit time-off requests, and get information about benefits. The agent has access to HR policies and can look up your personal leave balance. Note: For sensitive matters like performance reviews or complaints, please contact HR directly."
  },
  "icons": {
    "outline": "outline.png",
    "color": "color.png"
  },
  "accentColor": "#0078D4",
  "version": "1.0.0",
  "manifestVersion": "devPreview",
  "developer": {
    "name": "Contoso Ltd",
    "mpnId": "",
    "websiteUrl": "https://www.contoso.com",
    "privacyUrl": "https://www.contoso.com/privacy",
    "termsOfUseUrl": "https://www.contoso.com/terms"
  },
  "agenticUserTemplates": [
    {
      "id": "<auto-generated>",
      "file": "agenticUserTemplateManifest.json"
    }
  ]
}
```

#### Prompt the user

Ask the user: **"Please review and update your manifest.json file with your agent's details. Have you updated the manifest with your agent's name, description, and developer information? (yes/no)"**

- If **no**: Wait for the user to update the manifest before proceeding.
- If **yes**: Proceed to publish the agent manifest.

> **Important:** The `id` field and `agenticUserTemplates[].id` will be automatically populated by the CLI during publish. Do not manually set these values.

### Publish the agent manifest

Run `a365 publish`. This step updates the agent's manifest identifiers and publishes the agent package to Microsoft Online Services (specifically, it registers the agent with the Microsoft 365 admin center under your tenant). What this does:

- It takes your project's `manifest.json` (which should define your agent's identity and capabilities) and updates certain identifiers in it (the CLI will inject the Azure AD application blueprint ID where needed).
- It then publishes the agent manifest/package to your tenant's catalog (so that the agent can be "hired" or installed in Teams and other apps).

Watch for output messages. Successful publish will indicate that the agent manifest is updated and that you can proceed to create an instance of the agent. If there's an error during publish, read it closely. For example, if the CLI complains about being unable to update some manifest or reach the admin center, ensure your account has the necessary privileges and that the custom app registration has the permissions for `Application.ReadWrite.All` (since publish might call Graph to update applications). Also, ensure your internet connectivity is good.

### Deploy the agent code to Azure

Run `a365 deploy`. This will take the agent's application (the code project you pointed to in the config) and deploy it to the Azure Web App that was set up earlier. Specifically, `a365 deploy` will typically:

- Build your project (if it's .NET or Node, it will compile or bundle the code; if Python, it might collect requirements, etc.).
- Package the build output and deploy it to the Azure App Service (the web app). This could be via zip deploy or other Azure deployment mechanism automated by the CLI.
- Ensure that any required application settings (like environment variables, or any connection info) are configured. (For example, the CLI might convert a local `.env` to Azure App Settings for Python projects, as noted in its features.)
- It will also finalize any remaining permission setups (for instance, adding any last-minute Microsoft 365 permissions through the Graph if needed for the agent's operation; the CLI documentation mentions "update Agent 365 Tool permissions," which likely happens here or in publish).

**Note:** If you only want to deploy code without touching permissions (say, on subsequent iterations), the CLI offers subcommands `a365 deploy app` (just deploy binaries) and `a365 deploy mcp` (update tool permissions). But in a first-time setup, just running the full `a365 deploy` is fine, as it covers everything.

Monitor this process. If the build fails (maybe due to code issues or missing build tools), address the build error (you might need to install additional dependencies or fix a build script). If the deployment fails (e.g., network issues uploading, or Azure App Service issues), note the error and retry as needed.

On success, the CLI will indicate that the application was deployed. You should now have an Azure Web App running your agent's code.

### Post-deployment (User action required)

Once deployed, the agent's backend is live. At this point, from the perspective of the CLI, the agent is set up. However, there are additional steps to fully activate the agent in the Microsoft 365 environment: configuring the agent in Teams Developer Portal and creating an agent instance.

> **Important:** The following post-deployment steps must be completed by the user manually. These steps require browser-based interactions with the Teams Developer Portal and Microsoft Teams that cannot be automated by an AI agent. Provide the user with these instructions so they can complete them on their own.

For complete details, see [Create agent instances](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/create-instance).

#### Configure agent in Teams Developer Portal (User action)

**Instruct the user** to configure the agent blueprint in Teams Developer Portal to connect their agent to the Microsoft 365 messaging infrastructure. Without this configuration, the agent won't receive messages from Teams, email, or other Microsoft 365 services.

Provide the user with the following instructions:

1. **Get your blueprint app ID** by running:
   ```bash
   a365 config display -g
   ```
   Copy the `agentBlueprintAppId` value from the output.

2. **Navigate to Developer Portal** by opening your browser and going to:
   ```
   https://dev.teams.microsoft.com/tools/agent-blueprint/<your-blueprint-app-id>/configuration
   ```
   Replace `<your-blueprint-app-id>` with the value you copied.

3. **Configure the agent** in the Developer Portal:
   - Set **Agent Type** to `Bot Based`
   - Set **Bot ID** to your `agentBlueprintAppId` value
   - Select **Save**

> **Note:** If the user doesn't have access to the Developer Portal, they should contact their tenant administrator to grant access or complete this configuration on their behalf.

#### Create agent instance (User action)

**Instruct the user** to request an instance of the agent blueprint from Teams. For more details, see [How to discover, create, and onboard an agent](https://learn.microsoft.com/en-us/microsoft-agent-365/onboard).

Provide the user with the following instructions:

1. Open **Teams > Apps** and search for your agent name
2. Select your agent and click **Request Instance** (or **Create Instance**)
3. Teams sends the request to your tenant admin for approval

Admins can review and approve requests from the [Microsoft admin center - Requested Agents](https://admin.cloud.microsoft/#/agents/all/requested) page. After approval, Teams creates the agent instance and makes it available.

> **Important:** The user needs to be part of the [Frontier preview program](https://adoption.microsoft.com/copilot/frontier-program/) to create agent instances and interact with agents in Microsoft Teams while Agent 365 is in preview. They should contact their tenant administrator if they don't have access.

#### Test your deployed agent (User action)

**Instruct the user** to test the agent instance in Microsoft Teams after it's created:

1. Search for the new agent user in Teams
   > **Note:** The agent user creation process is asynchronous and can take a few minutes to a few hours for the agent user to become searchable after it's created.

2. Start a new chat with the newly created agent instance

3. Send test messages to verify agent functionality (e.g., "Hello!")

4. If tools are configured (e.g., Email MCP server), test tool functionality

**View the agent in the admin center:** Go to the [Microsoft 365 admin center - Agents](https://admin.cloud.microsoft/#/agents/all) to view the published agent, manage settings, monitor usage, and configure permissions.

**Check application logs** (for Azure-hosted deployments):
```bash
az webapp log tail --name <your-web-app> --resource-group <your-resource-group>
```

If your agent instance isn't working as expected, see the Troubleshooting section below or the [Agent 365 Troubleshooting Guide](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/troubleshooting).

---

## Error Handling and Troubleshooting

If any step results in an error, stop and analyze the error message carefully. For detailed troubleshooting guidance, refer to the official documentation:

- **[Agent 365 Troubleshooting Guide](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/troubleshooting)** — comprehensive coverage of common errors, authentication issues, Graph permission problems, Azure provisioning failures, and deployment issues.
- **[Agent 365 CLI Reference](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-cli)** — command-specific options and usage details.
- **[GitHub Issues](https://github.com/microsoft/Agent365-devTools/issues)** — search by error message for known issues and workarounds.

### Quick tips

- Run failing commands with `-v` / `--verbose` for detailed logs.
- Check log files: Windows `%APPDATA%/a365/logs/`, Linux/Mac `~/.config/a365/logs/`.
- Most `a365` commands are idempotent — safe to re-run after fixing an issue.
- Use `a365 cleanup azure` or `a365 cleanup blueprint` only as a last resort to remove created resources.

### Dev tunnel issues

**Dev tunnel CLI not found:** Ensure the installation completed and the binary is on your PATH. On Windows, restart your terminal or add the installation directory manually.

**Authentication failures:** If `devtunnel user login` fails in a headless environment, use device code auth:
```bash
devtunnel user login --device-code
```

**Tunnel not receiving messages:**
- Verify the tunnel is actively running (`devtunnel host <tunnel-name>` must be running).
- Confirm the local port matches what your agent is listening on.
- Check that `--allow-anonymous` was used when creating the tunnel.
- Test the tunnel URL in a browser to confirm connectivity.

**Tunnel URL changed:** Update the messaging endpoint:
```bash
a365 setup blueprint --update-endpoint https://<new-tunnel-id>-<port>.devtunnels.ms/api/messages
```
> **Tip:** Use a persistent (named) tunnel to keep a consistent URL across sessions.

**Port already in use:**
```bash
devtunnel port delete <tunnel-name> --port-number <old-port>
devtunnel port create <tunnel-name> --port-number <new-port>
```

**Tunnel expires or disconnects:** Re-run `devtunnel host <tunnel-name>` to restart. For long-running agents, consider Azure-hosted deployment instead.

**Cannot access tunnel from Teams:**
- Ensure `--allow-anonymous` flag was used.
- Verify firewall allows outbound connections to `*.devtunnels.ms`.
- Confirm the full messaging endpoint URL includes the correct path (e.g., `/api/messages`).

### Escalating to GitHub

If the issue appears to be a CLI bug, draft an issue with: CLI version (`a365 --version`), OS/shell, exact steps to reproduce, error output, and expected vs actual behavior. Present the draft to the user — do not create the issue unless authorized.
