# Agent 365 CLI

A command-line tool for deploying and managing Agent 365 applications on Azure. 

## Supported Platforms
- ✅ .NET Applications
- ✅ Node.js Applications  
- ✅ **Python Applications** (Auto-detects via `pyproject.toml`, handles Agent 365 dependencies, converts .env to Azure App Settings)

## Quick Start

### 1. Install the CLI

**From NuGet (Production):**
```bash
dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli
```

### 2. Configure

You can configure the CLI in two ways:

#### a) Interactive setup

```bash
a365 config init
```
This will prompt you for all required Azure and agent details, then write `a365.config.json` to the correct location.

The interactive setup includes helpful prompts with:
- **Smart defaults** based on your current context
- **Format validation** for fields like User Principal Names
- **Path verification** to ensure your project directory exists
- **Clear examples** and guidance for each field

**Minimum required properties:**
```json
{
  "tenantId": "your-tenant-id",
  "subscriptionId": "your-subscription-id",
  "resourceGroup": "rg-agent365-dev",
  "location": "eastus",
  "webAppName": "webapp-agent365-dev",
  "agentIdentityDisplayName": "Agent 365 Development Agent",
  "agentUserPrincipalName": "agent.username@yourdomain.onmicrosoft.com",
  "agentUserDisplayName": "Username's Agent User",
  "deploymentProjectPath": "./src"
}
```

**Required Fields Explained:**

- **`agentUserPrincipalName`**: The User Principal Name (UPN) for the agentic user in email format (e.g., `demo.agent@contoso.onmicrosoft.com`). This creates a dedicated user identity for your agent within Microsoft 365.
- **`agentUserDisplayName`**: Human-readable display name shown in Microsoft 365 applications (e.g., "Sales Assistant Agent" or "Demo Agent User").  
- **`deploymentProjectPath`**: Path to your agent project directory containing the application files. Supports both relative paths (e.g., `./src`) and absolute paths.

**Note:** The CLI automatically detects your project type (.NET, Node.js, or Python) and builds accordingly. No need to specify project files manually.

See `a365.config.example.json` for all available options and required properties.

### 3. Setup (Blueprint + Messaging Endpoint)

```bash
# Create agent blueprint and register messaging endpoint
a365 setup
```

- This command creates the agent blueprint and registers the messaging endpoint for your application.
- No subcommands are required. Deployment and messaging endpoint registration are handled together.

### 4. Create an agent instance (run each step in order)
```bash
a365 create-instance identity
a365 create-instance licenses
a365 create-instance enable-notifications
```

### 5. Query Microsoft Entra ID information
```bash
a365 query-entra blueprint-scopes
a365 query-entra instance-scopes
```

---

## Common Commands

See below for frequently used commands. For full details, run `a365 --help` or see the CLI reference in the documentation.

### Setup & Registration
```bash
a365 setup
```

### Instance Creation
```bash
a365 create-instance identity
a365 create-instance licenses
a365 create-instance enable-notifications
```

### Deploy & Cleanup
```bash
a365 deploy                 # Full build and deploy
a365 deploy --restart       # Skip build, deploy existing publish folder (quick iteration)
a365 deploy --inspect       # Pause before deployment to verify package contents
a365 deploy --restart --inspect  # Combine flags for quick redeploy with inspection
a365 cleanup
```

**Deploy Options Explained:**
- **Default** (`a365 deploy`): Full build pipeline - platform detection, environment validation, build, manifest creation, packaging, and deployment
- **`--restart`**: Skip all build steps and start from compressing the existing `publish/` folder. Perfect for quick iteration when you've manually modified files in the publish directory (e.g., tweaking `requirements.txt`, `.deployment`, or other config files)
- **`--inspect`**: Pause before deployment to review the publish folder and ZIP contents. Useful for verifying package structure before uploading to Azure
- **`--verbose`**: Enable detailed logging for all build and deployment steps
- **`--dry-run`**: Show what would be deployed without actually executing

### Query & Develop
```bash
a365 query-entra blueprint-scopes
a365 query-entra instance-scopes
a365 develop --list
```

### MCP Server Management
```bash
# List Dataverse environments
a365 develop-mcp list-environments

# List MCP servers in a specific environment
a365 develop-mcp list-servers -e "Default-12345678-1234-1234-1234-123456789abc"

# Publish an MCP server
a365 develop-mcp publish -e "Default-12345678-1234-1234-1234-123456789abc" -s "msdyn_MyMcpServer"

# Unpublish an MCP server  
a365 develop-mcp unpublish -e "Default-12345678-1234-1234-1234-123456789abc" -s "msdyn_MyMcpServer"

# Approve/block MCP servers
a365 develop-mcp approve -s "msdyn_MyMcpServer"
a365 develop-mcp block -s "msdyn_MyMcpServer"

# All commands support dry-run for safe testing
a365 develop-mcp publish -e "myenv" -s "myserver" --dry-run
```

---

## Multiplatform Deployment Support

The Agent 365 CLI automatically detects and deploys applications built with:

### .NET Applications
- **Detection:** Looks for `*.csproj`, `*.fsproj`, or `*.vbproj` files
- **Build Process:** `dotnet restore` → `dotnet publish`
- **Deployment:** Creates Oryx manifest with `dotnet YourApp.dll` command
- **Requirements:** .NET SDK installed

### Node.js Applications  
- **Detection:** Looks for `package.json` file
- **Build Process:** `npm ci` → `npm run build` (if build script exists)
- **Deployment:** Creates Oryx manifest with start script from `package.json`
- **Requirements:** Node.js and npm installed

### Python Applications
- **Detection:** Looks for `requirements.txt`, `setup.py`, `pyproject.toml`, or `*.py` files  
- **Build Process:** Copies project files, handles local wheel packages in `dist/`, creates deployment configuration
- **Deployment:** Creates Oryx manifest with appropriate start command (gunicorn, uvicorn, or python)
- **Requirements:** Python 3.11+ and pip installed
- **Special Features:**
  - Automatically converts `.env` to Azure App Settings
  - Handles local Agent 365 packages via `--find-links dist`
  - Creates `requirements.txt` with `--pre` flag for pre-release packages
  - Detects Agent 365 entry points (`start_with_generic_host.py`)
  - Sets correct Python startup command automatically

### Deployment Example
```bash
# Works for any supported platform - CLI auto-detects!
a365 deploy

# With verbose output to see build details
a365 deploy --verbose

# Test what would be deployed without executing
a365 deploy --dry-run
```

The CLI automatically:
1. Detects your project platform
2. Validates required tools are installed  
3. Cleans previous build artifacts
4. Builds your application using platform-specific tools
5. Creates an appropriate Oryx manifest for Azure App Service
6. Packages and deploys to Azure

---

## Configuration


The CLI always updates both `a365.config.json` (static config) and `a365.generated.config.json` (dynamic state) in:

- **%LocalAppData%\Microsoft.Agents.A365.DevTools.Cli** (Windows) or `$HOME/.config/Microsoft.Agents.A365.DevTools.Cli` (Linux/macOS) — this is the global user config/state location and is always kept up to date.
- The **current working directory** — but only if the file already exists there. The CLI will NOT create new config/state files in the current directory unless you explicitly do so.

This prevents leaving config "crumbs" in random folders and ensures your configuration and state are always available and consistent.

**Working across multiple directories:**

- If you run CLI commands in different folders, each folder may have its own `a365.generated.config.json`.
- The CLI will warn you if the local generated config is older than the global config in your user profile. This helps prevent using stale configuration by accident.
- If you see this warning, you should consider running `a365 setup` again in your current directory, or manually sync the latest config from your global config folder.
- Best practice: Work from a single project directory, or always ensure your local config is up to date before running commands.

You can create or update these files using `a365 config init` (interactive) or `a365 config init -c <file>` (import). If you want a config in your current directory, create it there first.

See `a365.config.example.json` for all available options and schema.

---

## Troubleshooting

### Configuration Issues
- **Config file not found:**
  - Create it: `cp a365.config.example.json a365.config.json`
  - Or specify with `--config path/to/config.json`
- **Missing mandatory fields:**
  - Run: `a365 config init` to interactively set required values
  - Ensure `agentUserPrincipalName` follows UPN format (username@domain)
  - Verify `deploymentProjectPath` points to an existing directory
- **Invalid UPN format:**
  - Use email-like format: `agent.name@yourdomain.onmicrosoft.com`
  - Avoid spaces or special characters except `.`, `@`, and `-`
- **Project path not found:**
  - Use absolute paths or paths relative to where you run the CLI
  - Ensure the directory exists and contains your agent project files
- **Not logged into Azure:**
  - Run: `az login --tenant YOUR_TENANT_ID`
  - Set subscription: `az account set --subscription YOUR_SUBSCRIPTION_ID`

### Deployment Issues
- **Platform not detected:**
  - Ensure your project has the required files (.csproj, package.json, requirements.txt, or .py files)
  - Check that `deploymentProjectPath` points to the correct directory
- **.NET deployment fails:**
  - Verify .NET SDK is installed: `dotnet --version`
  - Ensure project file is valid and builds locally: `dotnet build`
- **Node.js deployment fails:**
  - Verify Node.js and npm are installed: `node --version` and `npm --version`
  - Test local build: `npm install` and `npm run build` (if applicable)
- **Python deployment fails:**
  - Verify Python and pip are installed: `python --version` and `pip --version`
  - Test local install: `pip install -r requirements.txt`
- **`--restart` fails with "Publish folder not found":**
  - Run full build first: `a365 deploy` (without `--restart`)
  - Verify `publish/` folder exists in your project directory
  - Check that `deploymentProjectPath` in config points to correct location

### Authentication & Permissions
- **Admin consent required:**
  - Open consent URLs printed by the CLI and approve as Global Admin
- **Agent identity/user IDs not saved:**
  - Re-run: `a365 create-instance identity`
  - Check `a365.generated.config.json` for IDs
- **Messaging endpoint registration failed:**
  - Ensure your tenant has required M365 licenses

### General Issues
- **Windows: Azure CLI issues:**
  - Verify Azure CLI: `az --version`
  - Reinstall CLI: `dotnet tool uninstall -g Microsoft.Agents.A365.DevTools.Cli` then `pwsh ./install-cli.ps1`

### Debugging with Log Files

The CLI automatically logs all commands to help with debugging. When reporting issues, share the relevant log file.

**Log Locations:**
- **Windows:** `%LocalAppData%\Microsoft.Agents.A365.DevTools.Cli\logs\`
- **Linux/Mac:** `~/.config/a365/logs/`

**View Latest Logs:**
```powershell
# Windows (PowerShell)
Get-Content $env:LOCALAPPDATA\Microsoft.Agents.A365.DevTools.Cli\logs\a365.setup.log -Tail 50
```

```bash
# Linux/Mac
tail -50 ~/.config/a365/logs/a365.setup.log
```

Each command has its own log file (`a365.setup.log`, `a365.deploy.log`, etc.). The CLI keeps only the latest run of each command.

---

## Getting Help

```bash
# General help
a365 --help

# Command-specific help
a365 setup --help
a365 create-instance --help
a365 deploy --help
a365 develop --help
a365 develop-mcp --help
a365 query-entra --help
a365 config --help

```


---

## Developer & Contributor Info

For build, test, architecture, and contributing instructions, see [DEVELOPER.md](./DEVELOPER.md).

---

## License

Copyright (c) Microsoft Corporation. All rights reserved.

Licensed under the MIT License - see the [LICENSE](LICENSE.md) file for details.

---

## Getting Help

```bash
# General help
a365 --help

# Command-specific help
a365 setup --help
a365 createinstance --help
a365 deploy --help
a365 develop --help
```

---

## Technical Notes

### Messaging Endpoint Registration Architecture

The `a365 setup` command configures the agent blueprint and registers the messaging endpoint using the blueprint identity. This ensures proper identity isolation and secure communication for your agent application.

**Key Technical Details:**
- Messaging endpoint registration uses the agent blueprint identity (from `a365.generated.config.json`)
- The endpoint is registered for Teams/channel communication
- App Service managed identity handles Azure resource access (Key Vault, etc.)
- This architecture follows Azure security best practices for identity isolation

**Command ordering:** Messaging endpoint registration happens after blueprint creation to use the actual deployed web app URL for the endpoint.

**Generated during:**
```bash
a365 setup  # Creates agent blueprint and registers messaging endpoint
```

---

## Prerequisites

### Required for All Projects
- **Azure CLI** (`az`) - logged into your tenant
- **PowerShell 7+** (for development scripts)
- **Azure Global Administrator role** (for admin consent)
- **M365 licenses** in your tenant (for agent users)

### Platform-Specific Requirements
Choose based on your application type:

- **.NET Projects:** .NET 8.0 SDK or later
- **Node.js Projects:** Node.js (18+ recommended) and npm
- **Python Projects:** Python 3.11+ and pip

The CLI will validate that required tools are installed before deployment.

---

## License

MIT License

Copyright (c) 2025 Microsoft

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.



## 📋 **Telemetry**

Data Collection. The software may collect information about you and your use of the software and send it to Microsoft. Microsoft may use this information to provide services and improve our products and services. You may turn off the telemetry as described in the repository. There are also some features in the software that may enable you and Microsoft to collect data from users of your applications. If you use these features, you must comply with applicable law, including providing appropriate notices to users of your applications together with a copy of Microsoft's privacy statement. Our privacy statement is located at https://go.microsoft.com/fwlink/?LinkID=824704. You can learn more about data collection and use in the help documentation and our privacy statement. Your use of the software operates as your consent to these practices.

## Trademarks

*Microsoft, Windows, Microsoft Azure and/or other Microsoft products and services referenced in the documentation may be either trademarks or registered trademarks of Microsoft in the United States and/or other countries. The licenses for this project do not grant you rights to use any Microsoft names, logos, or trademarks. Microsoft's general trademark guidelines can be found at http://go.microsoft.com/fwlink/?LinkID=254653.*
