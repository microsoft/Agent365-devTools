# Agent 365 CLI Setup Instructions for AI Agents

## Context

You are an AI coding agent with access to execute shell commands, read the Agent365-devTools repository (code and docs), and browse the web for documentation or GitHub issues. Your task is to set up, configure, and deploy all prerequisite components for a Microsoft Agent 365–compliant agent using the Agent 365 CLI. You must handle this end-to-end: from installation and configuration to deployment. Work step-by-step, and adapt to any issues or differences in CLI versions along the way.

---

## Step 1: Verify and Install/Update the Agent 365 CLI

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

---

## Step 2: Ensure Prerequisites and Environment Configuration

### Azure CLI & Authentication

The Agent 365 CLI relies on Azure context for deploying resources and may use your Azure credentials. Verify that the Azure CLI (`az`) is installed by running `az --version`. If it's not available, install the [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) for your platform or prompt the user to do so.

If the Azure CLI is installed, ensure that you are logged in to the correct Azure account and tenant. Run `az login` (and `az account set -s <SubscriptionNameOrID>` if you need to select a specific subscription). If you cannot perform an interactive login directly, output a clear instruction for the user to log in (the user may need to follow a device-code login URL if running in a headless environment). The Agent 365 CLI will use this Azure authentication context to create resources.

### Microsoft Entra ID (Azure AD) roles

The user account you authenticate with must have sufficient privileges to create the necessary resources. According to documentation, the account needs to be at least an **Agent ID Administrator** or **Agent ID Developer**, and certain commands (like the full environment setup) require **Global Administrator + Azure Contributor** roles. If you attempt an operation without adequate permissions, it will fail. Thus, before proceeding, confirm that the logged-in user has one of the required roles (Global Admin is the safest choice for preview setups). If not, prompt the user to either use an appropriate account or have an admin grant the needed roles.

### Custom client app registration

Verify that a custom client application is registered in Entra ID (Azure AD) for Agent 365 authentication. This is critical for the CLI to function (it uses this app to manage Agent Identity Blueprints). The user should have created this app as part of the prerequisites. If you have access to query Azure AD, you might attempt to find an app with a known name (by default, they might name it "Agent365 CLI") or you may simply proceed to the configuration step and see if the CLI can detect it. If the CLI prompts for an Application (client) ID or cannot find the app, you must obtain this information from the user or guide them to create the app registration. In summary:

- Ensure the custom app exists in the Azure AD tenant. If not, instruct the user to follow the official setup guide to create one. (The guide involves registering a new app in Azure AD, typically named "Agent365 CLI", as a Public client with redirect URI `http://localhost:8400`, and granting it certain permissions with admin consent.)

### Required Graph API Delegated Permissions

The app registration must have the following delegated (not application) Microsoft Graph permissions, with admin consent granted for each:

- `AgentIdentityBlueprint.ReadWrite.All`
- `AgentIdentityBlueprint.UpdateAuthProperties.All`
- `Application.ReadWrite.All`
- `DelegatedPermissionGrant.ReadWrite.All`
- `Directory.Read.All`

(These correspond to managing Agent 365 Blueprints and related Azure AD applications and permissions. They are documented in the Agent 365 CLI prerequisites.)

If the custom app is not set up or missing permissions, the configuration step will fail. In that case, pause and direct the user to complete the Custom Client App Registration process as documented, then continue. Do not attempt to automatically create this app via script unless explicitly authorized, because it requires admin privileges and specific consent.

### Other prerequisites

Ensure that any language-specific tools needed for your agent's code are available. The Agent 365 CLI supports .NET, Node.js, and Python projects. Depending on the type of agent you are deploying, check that the relevant runtime or build tools are installed:

- **For .NET agents:** Check `dotnet --list-sdks` and ensure the required .NET SDK for building the project is available.
- **For Node.js agents:** Check `node --version` and `npm --version`. Ensure you can run a build (e.g., `npm install` and `npm run build` if applicable).
- **For Python agents:** Check `python --version` and `pip --version`. You should also ensure any Python dependencies (especially any Microsoft 365-related SDKs) can be installed (for example, run `pip install -r requirements.txt` in the project directory to verify).

If any required runtime or tool is missing, attempt to install it (if you have the ability), or instruct the user on how to install it. The deployment will fail if the agent's code can't be built or if required interpreters aren't present.

---

## Step 3: Configure the Agent 365 CLI (Initialize Configuration)

Once all prerequisites are in place (CLI installed, Azure CLI logged in, custom app ready), initialize the Agent 365 CLI configuration. This is typically done via the interactive command `a365 config init`. There are two ways to configure: interactive wizard or using a config file. Prefer the interactive approach unless running in a fully non-interactive environment.

### Run the configuration wizard

Execute `a365 config init`. The CLI will prompt for various configuration values. Provide answers to the prompts. Based on official documentation and the tool's usage guide, you should expect the following inputs:

- **Application (client) ID** of the custom Entra ID app registration (the CLI will ask for this to tie into your tenant). Have this GUID ready (likely provided by the user or from the prerequisites).
- **Agent name** – a unique name for your agent (alphanumeric). This will be used to name resources (like the blueprint and Azure assets). If not specified by the user, you may choose a sensible name or ask the user for one.
- **Deployment project path** – path to the agent's code project on the file system. If the repository containing this prompt also includes the agent's source code, determine the correct path (for example, it might be the current directory or a `src/` subfolder). Ensure the path is correct because the CLI uses it to detect the project type and to package the code for deployment. If unsure, you may ask the user or search the repo for the project.
- **Manager email** – an email address of the person (could be the user or someone responsible) who will be designated as the manager of the agent. (This might be used for sending notifications or assigning ownership in some contexts.) If not provided, prompt the user for a valid email address in the organization.
- **Azure resource selections** – the CLI might ask you to select or confirm Azure resources such as an existing Resource Group, App Service Plan, region, etc., for deploying the agent. It may offer to reuse resources or create new ones. Provide inputs as appropriate: you can typically accept defaults or choose to create new resources using the agent name as a prefix. For example, it might suggest a resource group name like `rg-<agentname>` and an App Service Plan name; confirm or adjust as needed. Ensure the region selected is one of the supported regions for Agent 365.
- **Any other settings** – The wizard could prompt for things like whether to configure a global (machine-wide) config or just local. Usually, proceed with a local config in the project directory (unless you know you want a global config across projects; the command `--global` flag could be used if needed).

### Handle interactive input carefully

Since you are an AI agent, if the environment allows, capture the prompts and programmatically provide the answers. If you do not have a mechanism to respond to interactive prompts (for instance, if running in a pure non-interactive script mode), you can instead create a configuration JSON file and import it. For example, you could prepare a JSON following the structure of `a365.config.json` (see the CLI docs or the `a365.config.example.json` in the repo for all fields) and then run `a365 config init -c <path-to-json>` to import it. The minimum required fields are the ones listed above (tenant ID, subscription ID, resource group, region, web app name, agent identity info, project path, etc.). If you choose this route, ensure the JSON is accurate and all needed values are filled, then run the import command.

### Validation

The `config init` process will attempt to validate your inputs. Notably, it will check:

- That the provided Application (client) ID corresponds to an existing app in the tenant and that it has the required permissions (the CLI might automatically verify the presence of the Graph permissions and admin consent). If this validation fails (for example, "app not found" or "missing permission X"), do not proceed further until the issue is resolved. Refer back to the app registration guide and fix the configuration (you may need the user's help to adjust the app's settings or wait for an admin consent).
- **Azure subscription and resource availability:** it might check that the subscription ID is accessible and you have Contributor rights (if you logged in via Azure CLI, this should be okay).
- It could also test the project path for a recognizable project (looking for a `.csproj`, `package.json`, or `pyproject.toml` to identify .NET/Node/Python). If it warns that it "could not detect project platform" or similar, double-check the `deploymentProjectPath` you provided. If it's wrong, update it (you can re-run `a365 config init` or manually edit the generated `a365.config.json`). For instance, if your code is in a subdirectory, ensure the path points there.

If any other configuration aspect fails (like lacking Azure access, etc.), address it now. The `a365.config.json` file will be created in your directory (and sometimes also a copy in a global location). You can open this file to verify all details are correct (tenantId, subscriptionId, resourceGroup, etc.).

### Proceed when config is successful

Once `a365 config init` completes without errors, you have a baseline configuration ready. The CLI now knows your environment details and is authenticated. This configuration will be used by subsequent commands.

---

## Step 4: Run Agent 365 Setup to Provision Prerequisites

With the CLI configured, the next major step is to set up the cloud resources and Agent 365 blueprint required for your agent. The CLI provides a one-stop command to do this:

### Execute the setup command

Run `a365 setup all`. This single command performs all the necessary setup steps in sequence. Under the hood, it will:

- Create or validate the Azure infrastructure for the agent (Resource Group, App Service Plan, Web App, and enabling a system-assigned Managed Identity on the web app).
- Create the Agent 365 Blueprint in your Microsoft Entra ID (Azure AD). This involves creating an Azure AD application (the "blueprint") that represents the agent's identity and blueprint configuration. The CLI uses Microsoft Graph API for this.
- Configure the blueprint's permissions (for MCP and for the bot/App Service). This likely entails granting certain API permissions or setting up roles so that the agent's identity can function (for example, granting the blueprint the ability to have "inheritable permissions" or other settings, which requires Graph API operations).
- Register the messaging endpoint for the agent's integration (this ties the web application to the Agent 365 service so that Teams and other Microsoft 365 apps can communicate with the agent).

In summary, "setup all" carries out what used to be multiple sub-commands (`setup infrastructure`, `setup blueprint`, `setup permissions mcp`, `setup permissions bot`, etc.), so running it will perform a comprehensive initial setup.

### Monitor the output

This command may take a few minutes as it provisions cloud resources and does Graph API calls. Monitor the console output carefully:

- The CLI will log progress in multiple steps (often numbered like `[0/5]`, `[1/5]`, etc.). Watch for any errors or warnings. Common points of failure include: Azure resource creation issues (quota exceeded, region not available, etc.), or Graph permission issues when creating the blueprint (e.g. insufficient privileges causing a "Forbidden" or "Authorization_RequestDenied" error).
- If the CLI outputs a warning about Azure CLI using 32-bit Python on 64-bit system (on Windows) or similar performance notices, you can note them but they don't block execution — they just suggest installing a 64-bit Azure CLI for better performance. This is not critical for functionality.
- If resource group or app services already exist (maybe from a previous run or a partially completed setup), the CLI will usually detect them and skip creating duplicates, which is fine.

### Important considerations

- **Quota limits:** If you see an error like "Operation cannot be completed without additional quota" during App Service plan creation, that means the Azure subscription has hit a quota limit (for example, no free capacity for new App Service in that region or SKU). In this case, you might need to change the region or service plan SKU, or have the user request a quota increase. This is an Azure issue, not a CLI bug. Report this clearly to the user and halt, or try choosing a different region if possible (you would need to update the config's `location` and possibly rerun setup).
- **Region support:** If you see errors related to Azure region support (for instance, an error about an Azure resource not available in region), recall that Agent 365 preview might support only certain regions for Bot Service or other components. If that happens, choose a supported region (update your `a365.config.json` with a supported `location` and run `a365 setup all` again).
- **Graph API permission errors:** If there are Graph API permission errors while creating the blueprint (e.g., a "Forbidden" error creating the application or setting permissions), this likely indicates the account running the CLI lacks a required directory role or the custom app's permissions aren't correctly consented. For example, an error containing "Authorization_RequestDenied" or mention of missing `AgentIdentityBlueprint` permissions suggests the custom app might not have those delegated permissions with admin consent. In such a case, stop and resolve the permission issue (see Step 2). You may need to have a Global Admin grant the consent or use an account with the appropriate role. After fixing, you can retry `a365 setup all`.
- **Interactive authentication during setup:** The CLI might attempt to do an interactive login to Azure AD (especially for granting some permissions or acquiring tokens for Graph). If running in a headless environment, this could fail (e.g., you see an error about `InteractiveBrowserCredential` or needing a GUI window). The CLI should ideally use the Azure CLI token, but for certain Graph calls (like `AgentIdentityBlueprint.ReadWrite.All` which might not be covered by Azure CLI's token), it might launch a browser auth. If this happens, see troubleshooting below for how to handle interactive auth in a non-interactive setting.

### Completion of setup

If `a365 setup all` completes successfully, you should see a confirmation in the output. It typically indicates that the blueprint is created and the messaging endpoint is registered. The CLI might output important information such as: the Agent Blueprint Application ID it created, or any Consent URLs for adding additional permissions. For instance, sometimes after setup, the CLI might provide a URL for admin consent (though if the custom app was properly set up with consent, ideally this isn't needed). If any consent URL or similar is printed, make sure to surface that to the user with an explanation (e.g., "The CLI is asking for admin consent for additional permissions; please open the provided URL in a browser and approve it as a Global Admin, then press Enter to continue."). The CLI may pause until consent is granted in such cases.

### Note on Idempotency

You can generally re-run `a365 setup all` if something went wrong and you fixed it. The CLI is designed to skip or reuse existing resources, as seen in the logs (e.g., resource group already exists, etc.). So don't hesitate to run it again after addressing an issue. If for some reason you need to start over, the CLI provides a cleanup command (`a365 cleanup`) to remove resources, but use that with caution (it can delete a lot). It's usually not necessary unless you want to wipe everything and retry from scratch.

---

## Step 5: Publish and Deploy the Agent Application

At this stage, your environment (Azure infrastructure and identity blueprint) is set up. Next, you need to publish the agent and deploy the application code so that the agent is live.

### Publish the agent manifest

Run `a365 publish`. This step updates the agent's manifest identifiers and publishes the agent package to Microsoft Online Services (specifically, it registers the agent with the Microsoft 365 admin center under your tenant). What this does:

- It takes your project's `manifest.json` (which should define your agent's identity and capabilities) and updates certain IDs in it (the CLI will inject the Azure AD application IDs – the blueprint and instance IDs – where needed).
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

### Post-deployment

Once deployed, the agent's backend is live on Azure. At this point, from the perspective of the CLI and Azure, the agent is set up. However, there is one more manual step to fully activate the agent in the Microsoft 365 environment: "hiring" the agent. In the Agent 365 paradigm, after publishing, an admin or user needs to create an instance of the agent (essentially install the agent into an application like Microsoft Teams). The CLI used to handle this via a `create-instance` command, but that has been removed in favor of using the Microsoft 365 admin center or Teams to do it.

**Inform the user:** If not already done by the CLI output, explicitly inform the user that they should go to the appropriate interface (likely the Teams Developer Portal or the Microsoft 365 Admin Center) to add an instance of the published agent for testing. Typically, one would use the Teams client (or Teams Developer Portal) to create a new Custom Live Agent (or however Agent 365 agents are represented) referencing the published package. (Refer them to the official guide for "Onboard agents" if available.)

The user will need to, in Microsoft Teams, use the agent's manifest to register it or simply trigger an install. Often this is phrased as "hire your agent via Microsoft Teams to complete the onboarding." This step will connect the front-end (Teams, etc.) with your deployed back-end.

Once "hired," the agent should start responding to whatever triggers it's designed for (e.g. @mention in Teams chat).

At this point, if all previous steps succeeded, the Agent 365–compliant agent should be fully set up: The Azure infrastructure is in place, the agent's identity blueprint is configured, the agent code is deployed, and the agent is published in the tenant. The remaining part (agent instance in Teams) is a user action outside of CLI scope, but you have communicated it.

---

## Error Handling and Troubleshooting

Things don't always go smoothly. As an AI agent, you must be prepared to diagnose and fix issues at each stage. Here's how to systematically troubleshoot:

### General approach

If any step results in an error or unexpected output, stop and analyze the error message carefully. Cross-reference the error with available documentation or known issues. Utilize all resources at your disposal: the official Microsoft Learn docs for Agent 365 CLI, the content of the Agent365-devTools GitHub repository (including its README, usage guide, and any scripts or code), and the GitHub issues (both open and closed) for this project. Often, others have encountered similar problems, and maintainers' responses in issue threads can provide solutions or workarounds.

### Installation issues

**NuGet or network errors during `dotnet tool install`:** If the CLI installation fails with an error about retrieving the package (for example, "NuGet package not found" or connectivity issues), ensure internet access is available. The `Microsoft.Agents.A365.DevTools.Cli` package is hosted on NuGet; a common issue when the CLI was just released was needing the `--prerelease` flag (which we already include). Verify that you included `--prerelease`. If the error persists, try again after a short wait (NuGet may have been temporarily unreachable). If there is a persistent version resolution issue, you can search the GitHub issues; for instance, one issue reported an installation glitch that was resolved in later versions. Upgrading dotnet SDK or clearing NuGet caches might help in some cases.

**CLI command not found after installation:** If `a365` still isn't found after a successful install, ensure that the dotnet tools path is in the system PATH. You may need to manually add it or restart the shell. By default on Windows, it's in `%USERPROFILE%\.dotnet\tools`, and on Linux/Mac in `~/.dotnet/tools`. If the agent environment doesn't pick up changes to PATH, you might have to call the binary via its full path.

### Azure CLI / Authentication issues

If commands fail because you are not logged in (for example, an error explicitly saying you need to login or "No subscription found"), run `az login` and ensure the correct subscription.

If `a365 setup` or other commands attempt to do an interactive login (for Graph) and fail in a headless environment (e.g., error: "InteractiveBrowserCredential authentication failed: A window handle must be configured" or any mention of `InteractiveBrowserCredential`), this is a known limitation in non-interactive terminals. Workarounds include:

- Ensure you have the latest CLI version, as improvements might be made to support device code flow. (Check the release notes or issues if such a feature is available, e.g., an issue suggests a `--use-device-code` flag or automatic fallback might be introduced.) If such an option exists, try running the command with that flag to force a device code authentication (which will output a code for the user to enter at https://microsoft.com/devicelogin).
- If no such option in the CLI, you can attempt to manually pre-authenticate: For example, use the PowerShell Microsoft Graph module or Azure CLI to obtain tokens. However, the CLI may not reuse those for the specific Graph scope it needs (as noted in an issue, the CLI spawned its own process that didn't reuse the parent token cache). In short, the robust solution is likely beyond your direct control. Therefore, the best approach is to inform the user that the operation requires interactive login. For instance, instruct: "This command needs to open a browser to acquire a Graph token. Please run it in an environment where a web browser is available, or use a local machine instead of a headless server for this step." You might also mention that a future CLI update may address this, and reference the relevant issue if appropriate.
- If the issue persists and blocks progress, treat it as a potential bug (see "Escalating to GitHub" below). 

If `a365 setup` fails at the "setup permissions mcp" stage with an authentication error, this is likely the same issue as above (needing an interactive login for the delegated permissions to configure the MCP – Model Context Protocol – server permissions). The workaround until it's fixed would be the same: use an interactive environment or file a bug.

### Graph permission or consent issues

An error containing "Failed to acquire token" or "insufficient privileges" or anything about authorization failed during setup or publish indicates something amiss with the Graph permissions setup. Double-check that the custom app registration's delegated permissions are exactly as required and that admin consent has been granted. You might retrieve the current permissions via Azure Portal or Azure AD PowerShell to confirm. If a permission was missed or not consented, add/consent it and try again.

If the CLI specifically prints a URL for admin consent (often the case if it tried to do something and realized you need tenant-wide approval), make sure the user (Global Admin) completes that step. The CLI logs or error might mention which permission was lacking when it failed. Provide guidance to the user on granting that permission. Once done, re-run the failed command.

### Azure provisioning issues

**Resource already exists:** If you run `a365 setup all` multiple times, you might see warnings or errors about existing resources (for example, if you had partially run it before). The CLI is generally idempotent, but in case some resource is in a bad state, you may use CLI or Azure Portal to inspect it. For instance, if a web app was created but endpoint registration failed, you might delete that web app manually (or use `a365 cleanup azure`) and then retry setup. Only use `a365 cleanup` as a last resort because it will delete many things (it's meant to remove everything the CLI created).

**Quotas and limits:** As mentioned, if you hit a quota, the error message from Azure will indicate which resource type. The user might need to free up or raise the quota. A quick alternative is to try deploying to a different Azure region or SKU that has available capacity (update the config and run `a365 setup all` again).

**Unsupported region or service:** If the error implies something like "The region is not supported" for an Azure resource (especially likely for Azure Bot Service or related to Teams integration), consult the documentation or known issues for supported regions. The 2025 preview limited certain features (e.g., bot registration) to specific regions. Changing the region to one of the known working ones (as noted earlier) can resolve this.

### Application deployment issues

**Build failures:** If `a365 deploy` fails while building the project, the error will usually show in the console (like MSBuild errors for .NET, or npm errors for Node). Solve these as you would normally: check that all project files are correct, all dependencies are listed, etc. You can attempt to build outside the CLI to replicate the issue (e.g., run `dotnet build` or `npm run build` manually) to get more detail. Address code issues or missing dependencies accordingly.

**Python-specific:** If deploying a Python agent and it fails to detect or install dependencies, ensure that your project has a `requirements.txt` or `pyproject.toml` that lists Agent 365 SDK and others. The CLI tries to convert local `.env` to Azure settings; ensure your environment variables are set in config or `.env` so it picks them up.

**Publish folder not found:** If you used the `--restart` flag on deploy (to skip rebuild) and hit "publish folder not found," it means no previous build output is present. Simply run `a365 deploy` without `--restart` at least once to generate the publish folder, or ensure the `deploymentProjectPath` is correct. We addressed this earlier; follow the fix of doing a full deploy first.

### Using the repository and docs for insight

The Agent365-devTools repo contains a `Readme-Usage.md` (which we have effectively followed) and possibly other docs in the `docs/` folder. If a certain command is not behaving as expected, consider reading the relevant section in those docs or the CLI reference in Microsoft Learn. For example, if uncertain how a subcommand works, you can run `a365 <command> --help` for quick info, or check the `docs/commands/` directory in the repo for detailed reference markdown files.

Search the GitHub issues by error message. If you encounter "ERROR: Web app creation failed" or "Failed to configure XYZ," search those phrases. Often you will find an issue thread where maintainers offer a workaround or it might indicate the bug is fixed in a newer version (prompting you to update the CLI if you haven't).

If you suspect the issue is in the CLI's logic, you can even browse the source code (in `src/`) to understand what it's trying to do. For instance, if a certain property isn't being applied, the source might reveal it. This is advanced and usually not required unless diagnosing a bug for reporting.

### Escalating to GitHub (Drafting an Issue)

If you have exhausted the troubleshooting steps and it appears that the problem is due to a bug or unimplemented feature in the Agent 365 CLI itself (not a user error or missing prerequisite), then prepare to create a GitHub issue for the maintainers. Examples might include: a crash or unhandled exception in the CLI, a scenario where the CLI's behavior contradicts the documentation, or an inability to proceed due to a limitation in the tool.

Before writing a new issue, quickly search the existing issues (open and closed) to see if it's already reported. If it is, you might find temporary fixes or at least avoid duplicate reporting. If you have additional details to contribute, you can plan to mention them in the existing issue thread instead of opening a new one.

**Collect information for the issue:** Gather the relevant details:

- **Descriptive title:** Summarize the problem in a concise way (e.g., "a365 setup all fails to acquire Graph token in headless environment" or "Error XYZ during deploy on Linux"). This will be the issue title.
- **Environment details:** Note the CLI version (`a365 --version` output), OS platform and version, shell or environment (PowerShell, Bash on Ubuntu, etc.), and any other relevant environment info (Azure CLI version if relevant, or whether you're using a headless server or behind a proxy, etc.).
- **Steps to reproduce:** Write down the exact sequence of commands you ran and in what context that led to the issue. Be as precise as possible, including any configuration choices that might matter. The goal is for the maintainers (or any developer) to replicate the issue easily. Example: "1. Installed CLI v1.0.49, 2. Ran a365 config init with app ID X... 3. Ran a365 setup all in a Windows Terminal on Windows 10, user is Global Admin, Azure region WestUS, 4. Saw error ..."
- **The actual error message and logs:** Copy the relevant error output. If it's long, you can provide the tail of the log or the key error snippet. The logs are also available in files (`a365.setup.log`, etc., as noted in the Readme-Usage). You can open and include sections of those log files if they provide more detail than the console output. Make sure to remove or redact any sensitive info (like GUIDs that might be tenant or subscription IDs, if needed). Usually, error messages and stack traces are safe to share.
- **Expected behavior:** Describe what you expected to happen if the bug was not present (e.g., "the command should complete without errors and the agent should be set up" or "the token acquisition should fall back to device code instead of failing"). This helps clarify the discrepancy.
- **Workarounds attempted:** List any steps you tried to resolve it (e.g., "I tried re-running with --verbose, tried manual token generation, but none worked"). This helps the maintainers know what you've already done and not suggest the same things again.
- **Potential cause or fix (optional):** If you have insight from the error or code, include your thoughts. For example, "It appears the CLI does not support device code flow for Graph auth in headless scenarios. Perhaps adding `-UseDeviceAuthentication` to the PowerShell `Connect-MgGraph` call would solve this." Even if you're not 100% sure, this shows you did homework and could speed up the fix. If you have identified exactly where in the code the problem is and can suggest a specific code change, that's even better. For instance, "The error comes from A365SetupRunner when calling the Graph SDK. Catching the exception and retrying with a device code might resolve it." (Keep a respectful tone and frame it as a suggestion.)

Once you compile this information, format it as a new GitHub issue. Follow the style of existing issues: start with a brief description, then headings like "To Reproduce", "Expected behavior", "Actual behavior" (or "Error details"), and "Environment". Attach log excerpts or screenshots if helpful (text is preferable for logs).

**Do not actually create the GitHub issue on your own** (unless explicitly authorized). Instead, present the draft to the user or maintainers. For example, you can output: "Draft Issue Report: ...". This allows the user to review and post it themselves, or gives the maintainers the info if they are following along.

If the bug is blocking your progress and there's no workaround, gracefully stop after providing the draft issue and explanation. It's better to wait for a fix or guidance than to continue in a broken state. In your communication with the user, emphasize that the issue appears to be on the tool's side and that you've prepared a report for the developers.

### Logging and verbosity

If you need more information while troubleshooting, remember that many `a365` commands support a `-v` or `--verbose` flag (as shown in help messages). For example, `a365 setup all -v` might output more detailed logs. Use this when an operation fails without enough info; the extra logs could reveal the failing step. Also, you can check the log files mentioned in the Readme (e.g., `~/.config/a365/logs/a365.setup.log` on Linux/Mac or the AppData path on Windows) for more detail. Include relevant parts of these logs in your analysis or in the GitHub issue if one is being filed.

### Reverting changes

In some cases, you might want to undo partial changes (for example, if the deployment got half-way and you want to clean up before retrying). The CLI's `a365 cleanup` commands can remove resources: `a365 cleanup azure` to delete Azure components, `a365 cleanup blueprint` to remove the Entra ID application (blueprint), etc. Use them carefully and only if you plan to fully retry the setup or if you want to roll back everything. If only a minor fix is needed, it's usually not necessary to clean up; you can just re-run the failing step.

### Reference official documentation

Throughout the process, if you are unsure how to proceed or want to verify the proper usage of a command, refer to the official documentation on Microsoft Learn. The main pages of interest are:

- **Agent 365 CLI overview and installation** – provides info on prerequisites and install/update process.
- **Agent 365 CLI Reference** – lists all commands and options in detail.
- **Specific command reference pages** – e.g., "setup" command, "config" command, "deploy" command references, which detail what each sub-step does and any options or requirements.
- **Custom client app registration guide** – details how to do the Entra ID app setup (we summarized it above).

These docs can be accessed online (links were given) or might be included in the repository's docs folder. Use them as needed to double-check correct behavior.

---

By following the above steps and using thorough troubleshooting practices, you should be able to successfully guide the Agent 365 CLI through installing all prerequisites, configuring the environment, and deploying the agent. Always prioritize resolving any errors before moving on to the next step, to ensure a smooth setup. Once completed, confirm with the user that the agent is up and running, and provide any final instructions (like how to interact with the agent in Teams or where to find logs for the running agent).
