# Microsoft.Agents.A365.DevTools.Cli - Developer Guide

This guide is for contributors and maintainers of the Microsoft Agent 365 CLI codebase. For end-user installation and usage, see [README.md](./README.md).

---

## Project Overview

The Microsoft Agent 365 CLI (`a365`) is a .NET tool that automates the deployment and management of Microsoft Agent 365 applications on Azure. It handles:

- **Multiplatform deployment** (.NET, Node.js, Python) with automatic platform detection
- Agent blueprint and identity creation
- Messaging endpoint registration
- Application deployment with Oryx manifest generation
- Microsoft Graph API permissions and consent
- Teams notifications registration
- MCP (Model Context Protocol) server configuration

## Python Project Support

The CLI now fully supports Python Agent 365 projects with the following features:

- ✅ **Auto-detection** via `pyproject.toml` and `*.py` files
- ✅ **Runtime configuration** - Sets correct `PYTHON|3.11` runtime automatically
- ✅ **Environment variables** - Converts `.env` to Azure App Settings automatically
- ✅ **Local dependencies** - Handles Agent 365 package wheels in `dist/` folder using `--find-links`
- ✅ **Entry point detection** - Prioritizes `start_with_generic_host.py` with smart content analysis
- ✅ **Build automation** - Creates `.deployment` file to force Oryx Python build
- ✅ **Startup commands** - Sets correct startup command for Azure Web Apps automatically

**PythonBuilder:**
- Installs dependencies with `pip install -r requirements.txt -t .`
- Copies Python source files (excludes `venv`, `__pycache__`)
- Detects framework patterns (Flask, FastAPI, Django)
- Determines appropriate start command (gunicorn, uvicorn, python)
- Creates manifest: `gunicorn --bind=0.0.0.0:8000 app:app`
- **Python-Specific Features:**
  - Handles local wheel packages in `dist/` folder via `--find-links dist`
  - Creates `requirements.txt` with `--pre` flag to allow pre-release packages
  - Automatically converts `.env` to Azure App Settings
  - Detects Agent 365 entry points (prioritizes `start_with_generic_host.py`)
  - Smart entry point selection based on content analysis (checks for `if __name__ == "__main__"`)
  - Sets Python startup command via `az webapp config set`
  - Creates `.deployment` file to force Oryx Python build

### Python Deployment Flow
1. **Platform Detection** - Identifies Python projects via `pyproject.toml`
2. **Clean Build** - Removes old artifacts, copies project files (excludes `.env`, `__pycache__`, etc.)
3. **Local Packages** - Runs `uv build` if needed, copies `dist/` folder to deployment
4. **Requirements.txt** - Creates Azure-native `requirements.txt` with:
   - `--find-links dist` (use local wheels)
   - `--pre` (allow pre-release versions)
   - `-e .` (install project in editable mode)
5. **Environment Setup** - Converts `.env` to Azure App Settings via `az webapp config appsettings set`
6. **Build Configuration** - Creates `.deployment` file with `SCM_DO_BUILD_DURING_DEPLOYMENT=true`
7. **Deployment** - Uploads zip, Azure runs `pip install`, starts app with correct startup command

---

## Project Structure

```
Microsoft.Agents.A365.DevTools.Cli/
├─ Program.cs                    # CLI entry point, command registration
├─ Commands/                     # Command implementations
│  ├─ ConfigCommand.cs          # a365 config (init, display)
│  ├─ SetupCommand.cs           # a365 setup (blueprint + messaging endpoint)
│  ├─ CreateInstanceCommand.cs  # a365 create-instance (identity, licenses, enable-notifications)
│  ├─ DeployCommand.cs          # a365 deploy
│  ├─ QueryEntraCommand.cs      # a365 query-entra (blueprint-scopes, instance-scopes)
│  ├─ DevelopCommand.cs         # a365 develop
│  └─ DevelopMcpCommand.cs      # a365 develop-mcp (MCP server management)
├─ Services/                     # Business logic services
│  ├─ ConfigService.cs          # Configuration management
│  ├─ DeploymentService.cs      # Multiplatform Azure deployment
│  ├─ PlatformDetector.cs       # Automatic platform detection
│  ├─ IPlatformBuilder.cs       # Platform builder interface
│  ├─ DotNetBuilder.cs          # .NET project builder
│  ├─ NodeBuilder.cs            # Node.js project builder
│  ├─ PythonBuilder.cs          # Python project builder
│  ├─ BotConfigurator.cs        # Messaging endpoint registration
│  ├─ GraphApiService.cs        # Graph API interactions
│  └─ CommandExecutor.cs        # External process execution
├─ Models/                       # Data models
│  ├─ Agent365Config.cs         # Unified configuration model
│  ├─ ProjectPlatform.cs        # Platform enumeration
│  └─ OryxManifest.cs           # Azure Oryx manifest model
└─ Tests/                        # Unit tests
   ├─ Commands/
   ├─ Services/
   └─ Models/
```

### Configuration Command

The CLI provides a `config` command for managing configuration:

- `a365 config init` — Interactive wizard with Azure CLI integration and smart defaults. Prompts for agent name, deployment path, and manager email. Auto-generates resource names and validates configuration.
- `a365 config init -c <file>` — Imports and validates a config file from the specified path.
- `a365 config init --global` — Creates configuration in global directory (AppData) instead of current directory.
- `a365 config display` — Prints the current configuration.

**Configuration Wizard Features:**
- **Azure CLI Integration**: Automatically detects subscription, tenant, resource groups, and app service plans
- **Smart Defaults**: Uses existing configuration values or generates intelligent defaults
- **Minimal Input**: Only requires 2-3 core fields (agent name, deployment path, manager email)
- **Auto-Generation**: Creates webapp names, identity names, and UPNs from the agent name
- **Platform Detection**: Validates project type (.NET, Node.js, Python) in deployment path
- **Dual Save**: Saves to both local project directory and global cache for reuse

### MCP Server Management Command

The CLI provides a `develop-mcp` command for managing Model Context Protocol (MCP) servers in Dataverse environments. The command follows a **minimal configuration approach** - it defaults to the production environment and only requires additional configuration when needed.

**Configuration Approach:**
- **Default Environment**: Uses "prod" environment automatically
- **Optional Config File**: Use `--config/-c` to specify custom environment from a365.config.json
- **Production First**: Optimized for production workflows with minimal setup
- **KISS Principle**: Avoids over-engineering common use cases

**Environment Management:**
- `a365 develop-mcp list-environments` — List all available Dataverse environments for MCP server management

**Server Management:**
- `a365 develop-mcp list-servers -e <environment-id>` — List MCP servers in a specific Dataverse environment
- `a365 develop-mcp publish -e <environment-id> -s <server-name>` — Publish an MCP server to a Dataverse environment
- `a365 develop-mcp unpublish -e <environment-id> -s <server-name>` — Unpublish an MCP server from a Dataverse environment

**Server Approval (Global Operations):**
- `a365 develop-mcp approve -s <server-name>` — Approve an MCP server
- `a365 develop-mcp block -s <server-name>` — Block an MCP server

**Key Features:**
- **Azure CLI Style Parameters:** Uses named options (`--environment-id/-e`, `--server-name/-s`) for better UX
- **Dry Run Support:** All commands support `--dry-run` for safe testing
- **Optional Configuration:** Use `--config/-c` only when non-production environment is needed
- **Production Default:** Works out-of-the-box with prod environment, no config file required
- **Verbose Logging:** Use `--verbose` for detailed output and debugging
- **Interactive Prompts:** Missing required parameters prompt for user input
- **Comprehensive Logging:** Detailed logging for debugging and audit trails

**Configuration Options:**
- **No Config (Default)**: Uses production environment automatically
- **With Config File**: `--config path/to/a365.config.json` to specify custom environment
- **Verbose Output**: `--verbose` for detailed logging and debugging information

**Examples:**

```bash
# Default usage (production environment, no config needed)
a365 develop-mcp list-environments

# List servers in a specific environment  
a365 develop-mcp list-servers -e "Default-12345678-1234-1234-1234-123456789abc"

# Publish a server with alias and display name
a365 develop-mcp publish \
  --environment-id "Default-12345678-1234-1234-1234-123456789abc" \
  --server-name "msdyn_MyMcpServer" \
  --alias "my-server" \
  --display-name "My Custom MCP Server"

# Quick unpublish with short aliases
a365 develop-mcp unpublish -e "Default-12345678-1234-1234-1234-123456789abc" -s "msdyn_MyMcpServer"

# Approve a server (global operation)
a365 develop-mcp approve --server-name "msdyn_MyMcpServer"

# Test commands safely with dry-run
a365 develop-mcp publish -e "myenv" -s "myserver" --dry-run

# Use custom environment from config file (internal developers)
a365 develop-mcp list-environments --config ./dev-config.json

# Verbose output for debugging
a365 develop-mcp list-servers -e "myenv" --verbose
```

**Architecture Notes:**
- Uses constructor injection pattern for environment configuration
- Agent365ToolingService receives environment parameter via dependency injection
- Program.cs detects --config option and extracts environment from config file
- Defaults to "prod" when no config file is specified
- Follows KISS principles to avoid over-engineering common scenarios

### Publish Command

The `publish` command packages and publishes your agent manifest to the MOS (Microsoft Online Services) Titles service. It uses **embedded templates** for complete portability - no external file dependencies required.

**Key Features:**
- **Embedded Templates**: Manifest templates (JSON + PNG) are embedded in the CLI binary
- **Fully Portable**: No external file dependencies - works from any directory
- **Automatic ID Updates**: Updates both `manifest.json` and `agenticUserTemplateManifest.json` with agent blueprint ID
- **Interactive Customization**: Prompts for manifest customization before upload
- **Graceful Degradation**: Falls back to manual upload if permissions are insufficient
- **Graph API Integration**: Configures federated identity credentials and role assignments

**Command Options:**
- `a365 publish` — Publish agent manifest with embedded templates
- `a365 publish --dry-run` — Preview changes without uploading
- `a365 publish --skip-graph` — Skip Graph API operations (federated identity, role assignments)
- `a365 publish --mos-env <env>` — Target specific MOS environment (default: prod)
- `a365 publish --mos-token <token>` — Override MOS authentication token

**Manifest Structure:**

The publish command works with two manifest files:

1. **`manifest.json`** - Teams app manifest with agent metadata
   - Updated fields: `id`, `name.short`, `name.full`, `bots[0].botId`
   
2. **`agenticUserTemplateManifest.json`** - Agent identity blueprint configuration
   - Updated fields: `agentIdentityBlueprintId` (replaces old `webApplicationInfo.id`)

**Workflow:**

```bash
# 1. Ensure you have a valid configuration
a365 config display

# 2. Run setup to create agent blueprint (if not already done)
a365 setup all

# 3. Publish the manifest
a365 publish
```

**Interactive Customization Prompt:**

Before uploading, you'll be prompted to customize:
- **Version**: Must increment for republishing (e.g., 1.0.0 → 1.0.1)
- **Agent Name**: Short (≤30 chars) and full display names
- **Descriptions**: Short (1-2 sentences) and full capabilities
- **Developer Info**: Name, website URL, privacy URL
- **Icons**: Custom branding (color.png, outline.png)

**Manual Upload Fallback:**

If you receive an authorization error (401/403), the CLI will:
1. Create the manifest package locally in a temporary directory
2. Display the package location
3. Provide instructions for manual upload to MOS Titles portal
4. Reference documentation for detailed steps

**Example:**

```bash
# Standard publish
a365 publish

# Dry run to preview changes
a365 publish --dry-run

# Skip Graph API operations
a365 publish --skip-graph

# Use custom MOS environment
$env:MOS_TITLES_URL = "https://titles.dev.mos.microsoft.com"
a365 publish
```

**Manual Upload Instructions:**

If automated upload fails due to insufficient privileges:

1. Locate the generated `manifest.zip` file (path shown in error message)
2. Navigate to MOS Titles portal: `https://titles.prod.mos.microsoft.com`
3. Go to Packages section
4. Upload the manifest.zip file
5. Follow the portal workflow to complete publishing

For detailed MOS upload instructions, see the [MOS Titles Documentation](https://aka.ms/mos-titles-docs).

**MOS Token Authentication:**

The publish command uses **custom client app** authentication to acquire MOS (Microsoft Office Store) tokens:

- **MosTokenService**: Native C# service using MSAL.NET for interactive authentication
- **Custom Client App**: Uses the client app ID configured during `a365 config init` (not hardcoded Microsoft IDs)
- **Tenant-Specific Authorities**: Uses `https://login.microsoftonline.com/{tenantId}` for single-tenant app support (not `/common` endpoint)
- **Token Caching**: Caches tokens locally in `.mos-token-cache.json` to reduce auth prompts
- **MOS Environments**: Supports prod, sdf, test, gccm, gcch, and dod environments
- **Redirect URI**: Uses `http://localhost:8400/` for OAuth callback (aligns with custom client app configuration)

**Important:** Single-tenant apps (created after October 15, 2018) cannot use the `/common` endpoint due to Azure policy. The CLI automatically uses tenant-specific authority URLs built from the `TenantId` in your configuration to ensure compatibility.

**MOS Prerequisites (Auto-Configured):**

On first run, `a365 publish` automatically configures MOS API access:

1. **Service Principal Creation**: Creates service principals for MOS resource apps in your tenant:
   - `6ec511af-06dc-4fe2-b493-63a37bc397b1` (TPS AppServices 3p App - MOS publishing)
   - `8578e004-a5c6-46e7-913e-12f58912df43` (Power Platform API - MOS token acquisition)
   - `e8be65d6-d430-4289-a665-51bf2a194bda` (MOS Titles API - titles.prod.mos.microsoft.com access)

2. **Idempotency Check**: Skips setup if MOS permissions already exist in custom client app

3. **Admin Consent Detection**: Checks OAuth2 permission grants and prompts user to grant admin consent if missing

4. **Fail-Fast on Privilege Errors**: If you lack Application Administrator/Cloud Application Administrator/Global Administrator role, the CLI shows manual service principal creation commands:
   ```bash
   az ad sp create --id 6ec511af-06dc-4fe2-b493-63a37bc397b1
   az ad sp create --id 8578e004-a5c6-46e7-913e-12f58912df43
   az ad sp create --id e8be65d6-d430-4289-a665-51bf2a194bda
   ```

**Architecture Details:**

- **MosConstants.cs**: Centralized constants for MOS resource app IDs, environment scopes, authorities, redirect URI
- **MosTokenService.cs**: Handles token acquisition using MSAL.NET PublicClientApplication with tenant-specific authorities:
  - Validates both `ClientAppId` and `TenantId` from configuration
  - Builds authority URL dynamically: `https://login.microsoftonline.com/{tenantId}`
  - Government cloud: `https://login.microsoftonline.us/{tenantId}`
  - Returns null if TenantId is missing (fail-fast validation)
- **PublishHelpers.EnsureMosPrerequisitesAsync**: Just-in-time provisioning of MOS prerequisites with idempotency and error handling
- **ManifestTemplateService**: Handles embedded resource extraction and manifest customization
- **Embedded Resources**: 4 files embedded at build time:
  - `manifest.json` - Base Teams app manifest
  - `agenticUserTemplateManifest.json` - Agent identity blueprint manifest
  - `color.png` - Color icon (192x192)
  - `outline.png` - Outline icon (32x32)
- **Temporary Working Directory**: Templates extracted to temp directory, customized, then zipped
- **Automatic Cleanup**: Temp directory removed after successful publish

**Error Handling:**

- **AADSTS650052 (Missing Service Principal/Admin Consent)**: Shows Portal URL for admin consent or prompts interactive consent
- **AADSTS50194 (Single-Tenant App / Multi-Tenant Endpoint)**: Fixed by using tenant-specific authority URLs instead of `/common` endpoint
- **MOS Prerequisites Failure**: Displays manual `az ad sp create` commands for all three MOS resource apps if automatic creation fails
- **401 Unauthorized / 403 Forbidden**: Graceful fallback with manual upload instructions
- **Missing Blueprint ID**: Clear error message directing user to run `a365 setup`
- **Missing TenantId**: MosTokenService returns null if TenantId is not configured (fail-fast validation)
- **Invalid Manifest**: JSON validation errors with specific field information
- **Network Errors**: Detailed HTTP status codes and response bodies for troubleshooting
- **Consistent Error Codes**: Uses `ErrorCodes.MosTokenAcquisitionFailed`, `ErrorCodes.MosPrerequisitesFailed`, `ErrorCodes.MosAdminConsentRequired`
- **Centralized Messages**: Error guidance from `ErrorMessages.GetMosServicePrincipalMitigation()` and `ErrorMessages.GetMosAdminConsentMitigation()`

## Permissions Architecture

The CLI configures three layers of permissions for agent blueprints:

1. **OAuth2 Grants** - Admin consent via Graph API `/oauth2PermissionGrants`
2. **Required Resource Access** - Portal-visible permissions (Entra ID "API permissions")
3. **Inheritable Permissions** - Blueprint-level permissions that instances inherit automatically

**Unified Configuration:** `SetupHelpers.EnsureResourcePermissionsAsync` handles all three layers plus verification with retry logic (exponential backoff: 2s → 4s → 8s → 16s → 32s, max 5 retries).

**Per-Resource Tracking:** `ResourceConsent` model tracks inheritance state per resource (Agent 365 Tools, Messaging Bot API, Observability API). Check global status with `config.IsInheritanceConfigured()`.

**Best Practice:** Agent instances automatically inherit permissions from blueprint - no additional admin consent required.

Validation is enforced for required fields in both interactive and import flows. The config model is strongly typed (`Agent365Config`).

### Adding/Extending Config Properties

To add a new configuration property:

1. Add the property to `Agent365Config.cs` (with appropriate `[JsonPropertyName]` attribute).
2. Update the validation logic in `Agent365Config.Validate()` if needed.
3. Update `a365.config.schema.json` and `a365.config.example.json`.
4. (Optional) Update prompts in `ConfigCommand.cs` for interactive init.
5. Add or update tests in `Tests/Commands/ConfigCommandTests.cs`.

---

## Architecture

### Configuration System

The CLI uses a **unified configuration model** with a clear separation between static (user-managed) and dynamic (CLI-managed) data.

#### Configuration File Storage and Portability

Both `a365.config.json` and `a365.generated.config.json` are stored in **two locations**:

1. **Project Directory** (optional, for local development)
2. **%LocalAppData%\Microsoft.Agents.A365.DevTools.Cli** (authoritative, for portability)

This dual-storage design enables **CLI portability** - users can run `a365` commands from any directory on their system, not just the project directory. The `deploymentProjectPath` property in `a365.config.json` points to the actual project location.

**File Resolution Strategy:**
- **Load**: Current directory first, then %LocalAppData% (fallback)
- **Save**: Write to **both** locations to maintain consistency
- **Sync**: When static config is loaded from current directory, it's automatically synced to %LocalAppData%

**Example Workflow:**
```sh
# User runs config init in project directory
C:\projects\my-agent> a365 config init
# Creates: C:\projects\my-agent\a365.config.json
# Syncs to: %LocalAppData%\Microsoft.Agents.A365.DevTools.Cli\a365.config.json

# User can now run commands from ANY directory
C:\Users\user1> a365 setup
# CLI reads from %LocalAppData%, operates on project at deploymentProjectPath
```

**Design Note - Stale Data Warning:**
> **TODO**: Current implementation warns when local config is older than %LocalAppData% but still uses the local (stale) data. This design needs to be revisited to determine the best behavior:
> - Option 1: Always prefer %LocalAppData% as authoritative source
> - Option 2: Prompt user to choose which config to use
> - Option 3: Auto-sync from newer to older location
> - Option 4: Make %LocalAppData% read-only and always require local config
>
> For now, the warning helps users identify potential configuration drift.

#### Two-File Design

1. **`a365.config.json`** (Static Configuration)
   - User-editable
   - Version controlled (without secrets)
   - Contains immutable setup values (tenant ID, resource names, etc.)
   - Synced to %LocalAppData% for portability

2. **`a365.generated.config.json`** (Dynamic State)
   - Auto-generated by CLI
   - Gitignored
   - Contains runtime state (agent IDs, timestamps, secrets)
   - Always written to both current directory and %LocalAppData%

#### Configuration Model (`Agent365Config.cs`)

The unified model uses C# property patterns to enforce immutability:

```csharp
public class Agent365Config
{
    // STATIC PROPERTIES (init-only) - from a365.config.json
    // Set once, never change
    public string TenantId { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public string ResourceGroup { get; init; } = string.Empty;
    
    // DYNAMIC PROPERTIES (get/set) - from a365.generated.config.json
    // Modified at runtime by CLI
    public string? AgentBlueprintId { get; set; }
    public string? AgentIdentityId { get; set; }
    public string? AgentUserId { get; set; }
    public string? AgentUserPrincipalName { get; set; }
    public bool? Consent1Granted { get; set; }
    public bool? Consent2Granted { get; set; }
    public bool? Consent3Granted { get; set; }
}
```

**Key Design Principles:**

- **`init`** properties → Immutable after construction → Static config
- **`get; set`** properties → Mutable → Dynamic state
- `ConfigService` handles merge (load) and split (save) logic
- PowerShell scripts (`a365-createinstance.ps1`) save state by modifying the `$instance` object and calling `Save-Instance`, which writes to `a365.generated.config.json`

#### Why This Design?

**Before (Separate Models):** 
- 3+ config files (`setup.config.json`, `createinstance.config.json`, `deploy.config.json`)
- Data duplication across files
- Manual merging required
- Type mismatches and errors

**After (Unified Model):**
- Single source of truth (`Agent365Config`)
- Type-safe property access
- Clear immutability semantics
- Automatic merge/split via `ConfigService`

#### Environment Variable Overrides

For security and flexibility, the CLI supports environment variable overrides for sensitive configuration values and internal endpoints. This allows the public codebase to remain clean while enabling internal Microsoft development workflows.

**Pattern**: `A365_{CATEGORY}_{ENVIRONMENT}` or `A365_{CATEGORY}` (for simple overrides)

**Supported Environment Variables:**

1. **Agent 365 Tools App ID (Authentication)**:
   ```bash
   # Override Agent 365 Tools App ID for authentication
   # Used by AuthenticationService when authenticating to Agent 365 endpoints
   export A365_MCP_APP_ID=your-custom-app-id
   ```

2. **MCP Platform App IDs (Per-Environment)**:
   ```bash
   # Override MCP Platform Application ID for specific environments
   # Used by ConfigConstants.GetAgent365ToolsResourceAppId()
   # Internal use only - customers should not need these overrides
   export A365_MCP_APP_ID_STAGING=your-staging-app-id
   export A365_MCP_APP_ID_CUSTOM=your-custom-app-id
   ```

3. **Discover Endpoints (Per-Environment)**:
   ```bash
   # Override discover endpoint URLs for specific environments
   # Used by ConfigConstants.GetDiscoverEndpointUrl()
   # Internal use only - customers should not need these overrides
   export A365_DISCOVER_ENDPOINT_STAGING=https://staging.agent365.example.com/agents/discoverToolServers
   export A365_DISCOVER_ENDPOINT_CUSTOM=https://custom.agent365.example.com/agents/discoverToolServers
   ```

4. **MOS Titles Service URL**:
   ```bash
   # Override MOS Titles service URL (used by PublishCommand)
   # Default: https://titles.prod.mos.microsoft.com
   # Internal use only - for non-production Microsoft environments
   export MOS_TITLES_URL=https://custom.titles.mos.example.com
   ```

5. **Power Platform API URL**:
   ```bash
   # Override Power Platform API URL (for custom environments)
   # Default: https://api.powerplatform.com
   # Internal use only - for non-production Microsoft environments
   export POWERPLATFORM_API_URL=https://api.custom.powerplatform.example.com
   ```

6. **Create endpoint URL**:
   ```bash
   # Override create endpoint URL (for custom environments)
   # Internal use only - for non-production Microsoft environments
   export A365_CREATE_ENDPOINT_STAGING=https://staging.agent365.example.com/agents/createAgentBlueprint
   export A365_CREATE_ENDPOINT_CUSTOM=https://custom.agent365.example.com/agents/createAgentBlueprint
   ```

7. **Delete endpoint URL**:
   ```bash
   # Override delete endpoint URL (for custom environments)
   # Internal use only - for non-production Microsoft environments
   export A365_DELETE_ENDPOINT_STAGING=https://staging.agent365.example.com/agents/deleteAgentBlueprint
   export A365_DELETE_ENDPOINT_CUSTOM=https://custom.agent365.example.com/agents/deleteAgentBlueprint
   ```

8. **Endpoint deployment Environment**:
   ```bash
   # Override endpoint deployment environment (for custom environments)
   # Internal use only - for non-production Microsoft environments
   export A365_DEPLOYMENT_ENVIRONMENT_STAGING=staging
   export A365_DEPLOYMENT_ENVIRONMENT_CUSTOM=custom
   ```

9. **Endpoint cluster category**:
   ```bash
   # Override endpoint cluster category (for custom environments)
   # Internal use only - for non-production Microsoft environments
   export A365_CLUSTER_CATEGORY_STAGING=staging
   export A365_CLUSTER_CATEGORY_CUSTOM=custom
   ```

**Implementation Pattern**:

**ConfigConstants.cs** (Per-environment with suffix):
```csharp
public static string GetAgent365ToolsResourceAppId(string environment)
{
    // Check for custom app ID in environment variable first
    var customAppId = Environment.GetEnvironmentVariable($"A365_MCP_APP_ID_{environment?.ToUpper()}");
    if (!string.IsNullOrEmpty(customAppId))
        return customAppId;

    // Default to production app ID
    return environment?.ToLower() switch
    {
        "prod" => McpConstants.Agent365ToolsProdAppId,
        _ => McpConstants.Agent365ToolsProdAppId
    };
}
```

**AuthenticationService.cs** (Simple override without environment suffix):
```csharp
// Use production App ID by default, allow override via A365_MCP_APP_ID
var appId = Environment.GetEnvironmentVariable("A365_MCP_APP_ID") ?? McpConstants.Agent365ToolsProdAppId;
```

**PublishCommand.cs** (MOS Titles URL):
```csharp
private static string GetMosTitlesUrl(string? tenantId)
{
    // Check for environment variable override
    var envUrl = Environment.GetEnvironmentVariable("MOS_TITLES_URL");
    if (!string.IsNullOrWhiteSpace(envUrl))
        return envUrl;
    
    return MosTitlesUrlProd;
}
```

**Benefits:**
- ✅ **Public Repository Ready**: No internal/test/preprod endpoints or app IDs hardcoded in source code
- ✅ **Flexible for Internal Development**: Microsoft developers can override via environment variables
- ✅ **Secure**: No secrets or internal App IDs hardcoded in the codebase
- ✅ **Simple**: Easy to understand and maintain
- ✅ **Production by Default**: Customers can only access production endpoints without configuration

**Key Design Decision:**
All test/preprod App IDs and URLs have been removed from the codebase. The production App ID (`ea9ffc3e-8a23-4a7d-836d-234d7c7565c1`) is the only value hardcoded in `McpConstants.Agent365ToolsProdAppId`. Internal Microsoft developers must use environment variables for non-production testing.

**Usage Examples:**
```bash
# Custom deployment for internal Microsoft development
export A365_MCP_APP_ID_STAGING=your-staging-app-id
export A365_DISCOVER_ENDPOINT_STAGING=https://staging.yourdomain.com/agents/discoverToolServers

# Run CLI with overrides
a365 setup --environment staging
```

---

### Command Pattern

Commands follow the Spectre.Console command pattern:

```csharp
public class SetupCommand : AsyncCommand<SetupCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--config")]
        public string? ConfigFile { get; init; }
        
        [CommandOption("--non-interactive")]
        public bool NonInteractive { get; init; }
    }
    
    public override async Task<int> ExecuteAsync(
        CommandContext context, 
        Settings settings)
    {
        // Implementation
    }
}
```
    ## Build, Test, and Local Install
**Guidelines:**
- Keep commands thin - delegate to services
- Use dependency injection for services
- Return 0 for success, non-zero for errors
- Log progress with ILogger

---

### Multiplatform Deployment Architecture

The CLI supports deploying .NET, Node.js, and Python applications using a builder pattern architecture:

#### Platform Detection (`PlatformDetector`)

```csharp
public enum ProjectPlatform
{
    Unknown, DotNet, NodeJs, Python
}

public class PlatformDetector
{
    public ProjectPlatform Detect(string projectPath)
    {
        // Priority: .NET → Node.js → Python → Unknown
        // .NET: *.csproj, *.fsproj, *.vbproj
        // Node.js: package.json
        // Python: requirements.txt, setup.py, pyproject.toml, *.py
    }
}
```

#### Platform Builder Interface (`IPlatformBuilder`)

```csharp
public interface IPlatformBuilder
{
    Task<bool> ValidateEnvironmentAsync();      // Check tools installed
    Task CleanAsync(string projectDir);        // Clean build artifacts
    Task<string> BuildAsync(string projectDir, string outputPath, bool verbose);
    Task<OryxManifest> CreateManifestAsync(string projectDir, string publishPath);
}
```

#### Deployment Pipeline

1. **Platform Detection:** Auto-detect project type from files
2. **Environment Validation:** Check required tools (dotnet/node/python)
3. **Clean:** Remove previous build artifacts
4. **Build:** Platform-specific build process
5. **Manifest Creation:** Generate Azure Oryx manifest
6. **Package:** Create deployment ZIP
7. **Deploy:** Upload to Azure App Service

**Restart Mode (`--restart` flag):**

When you need to quickly redeploy after making manual changes to the `publish/` folder:

```bash
# Normal flow: All 7 steps
a365 deploy

# Quick iteration: Skip steps 1-5, start from step 6 (packaging)
a365 deploy --restart
```

**Use Cases for `--restart`:**
- Testing configuration changes without rebuilding
- Manually tweaking `requirements.txt` or `.deployment` files
- Adding/removing files from the publish directory
- Quick debugging of deployment package contents
- Iterating on Azure-specific configurations

**What `--restart` Skips:**
1. ✓ Platform detection (assumes existing publish folder is correct)
2. ✓ Environment validation (tools already validated in first build)
3. ✓ Clean step (preserves your manual changes)
4. ✓ Build process (uses existing built artifacts)
5. ✓ Manifest creation (uses existing manifest or creates from publish folder)

**What `--restart` Executes:**
6. ✓ Create deployment ZIP from existing `publish/` folder
7. ✓ Deploy ZIP to Azure App Service

**Error Handling:**
- Validates `publish/` folder exists before attempting deployment
- Provides clear error message if folder is missing
- Suggests running full `a365 deploy` first if no publish folder found

**Example Workflow:**
```bash
# 1. Initial deployment with full build
a365 deploy

# 2. Make manual changes to publish folder
cd publish
nano requirements.txt  # Edit to add --pre flag
nano .deployment       # Verify SCM_DO_BUILD_DURING_DEPLOYMENT=true

# 3. Quick redeploy with changes (takes seconds instead of minutes)
cd ..
a365 deploy --restart

# 4. Optional: Inspect before deploying
a365 deploy --restart --inspect
```

---

## Development Workflow

### Setup Development Environment

```bash
# Clone repository
git clone https://github.com/microsoft/Agent365-devTools.git
cd Agent365-devTools/utils/scripts/developer

# Restore dependencies
cd Microsoft.Agents.A365.DevTools.Cli
dotnet restore

# Build
dotnet build

# Run tests
dotnet test
```

### Build and Install Locally

Use the convenient script:

```bash
# From scripts/cli directory
.\install-cli.ps1
```

Or manually:

```bash
cd Microsoft.Agents.A365.DevTools.Cli

# Clean and build
dotnet clean
dotnet build -c Release

# Pack as NuGet package
dotnet pack -c Release --no-build

# Uninstall old version
dotnet tool uninstall -g Microsoft.Agents.A365.DevTools.Cli

# Install new version
dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli \
  --add-source ./bin/Release \
  --prerelease
```

### Testing

```bash
# Run all tests
dotnet test

# Run specific test file
dotnet test --filter "FullyQualifiedName~SetupCommandTests"

# Run multiplatform deployment tests
dotnet test --filter "FullyQualifiedName~PlatformDetectorTests"
dotnet test --filter "FullyQualifiedName~DeploymentServiceTests"

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

#### Testing Multiplatform Deployment

The multiplatform deployment system includes comprehensive tests:

- **`PlatformDetectorTests`** - Tests platform detection logic for .NET, Node.js, and Python
- **`DeploymentServiceTests`** - Tests the overall deployment pipeline
- **Platform Builder Tests** - Individual tests for each platform builder
- **Integration Tests** - End-to-end deployment tests with sample projects

For manual testing, create sample projects in `test-projects/`:
```
test-projects/
├── dotnet-webapi/     # Sample .NET Web API
├── nodejs-express/    # Sample Express.js app  
└── python-flask/      # Sample Flask app
```

---

## Adding a New Command

## Cleanup Command Design

The cleanup command follows a **default-to-complete** UX pattern:

- `a365 cleanup` → Deletes ALL resources (blueprint, instance, Azure resources)
- `a365 cleanup blueprint` → Only deletes blueprint application
- `a365 cleanup azure` → Only deletes Azure resources
- `a365 cleanup instance` → Only deletes instance (identity + user)

**Design Rationale:**
- Most intuitive: "cleanup" naturally means "clean everything"
- Subcommands provide granular control when needed
- Matches user mental model without requiring "all" parameter

**Implementation:**
- Parent command has default handler calling `ExecuteAllCleanupAsync()`
- Subcommands override for selective cleanup
- Shared async method prevents code duplication
- Double confirmation (y/N + type "DELETE") protects against accidents

---

## Extending Multiplatform Support

### Adding a New Platform

To add support for a new platform (e.g., Java, Go, Ruby):

#### 1. Add Platform Enum Value

```csharp
// Models/ProjectPlatform.cs
public enum ProjectPlatform
{
    Unknown, DotNet, NodeJs, Python,
    Java  // Add new platform
}
```

#### 2. Update Platform Detection

```csharp
// Services/PlatformDetector.cs
public ProjectPlatform Detect(string projectPath)
{
    // Add Java detection logic
    if (File.Exists(Path.Combine(projectPath, "pom.xml")) ||
        File.Exists(Path.Combine(projectPath, "build.gradle")))
    {
        return ProjectPlatform.Java;
    }
    // ... existing logic
}
```

#### 3. Create Platform Builder

```csharp
// Services/JavaBuilder.cs
public class JavaBuilder : IPlatformBuilder
{
    public async Task<bool> ValidateEnvironmentAsync()
    {
        // Check java and maven/gradle installation
    }
    
    public async Task CleanAsync(string projectDir)
    {
        // mvn clean or gradle clean
    }
    
    public async Task<string> BuildAsync(string projectDir, string outputPath, bool verbose)
    {
        // mvn package or gradle build
    }
    
    public async Task<OryxManifest> CreateManifestAsync(string projectDir, string publishPath)
    {
        return new OryxManifest
        {
            Platform = "java",
            Version = "17", // Detect from project
            Command = "java -jar app.jar"
        };
    }
}
```

#### 4. Register Builder in DeploymentService

```csharp
// Services/DeploymentService.cs constructor
_builders = new Dictionary<ProjectPlatform, IPlatformBuilder>
{
    { ProjectPlatform.DotNet, new DotNetBuilder(dotnetLogger, executor) },
    { ProjectPlatform.NodeJs, new NodeBuilder(nodeLogger, executor) },
    { ProjectPlatform.Python, new PythonBuilder(pythonLogger, executor) },
    { ProjectPlatform.Java, new JavaBuilder(javaLogger, executor) } // Add here
};
```

#### 5. Add Tests

Create comprehensive tests for the new platform following the existing test patterns.

---

## Adding a New Command

### 1. Create Command Class

Create `Commands/MyNewCommand.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Spectre.Console.Cli;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

public class MyNewCommand : AsyncCommand<MyNewCommand.Settings>
{
    private readonly ILogger<MyNewCommand> _logger;
    private readonly ConfigService _configService;

    public MyNewCommand(
        ILogger<MyNewCommand> logger, 
        ConfigService configService)
    {
        _logger = logger;
        _configService = configService;
    }

    public class Settings : CommandSettings
    {
        [CommandOption("--config")]
        [Description("Path to configuration file")]
        public string ConfigFile { get; init; } = "a365.config.json";
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, 
        Settings settings)
    {
        _logger.LogInformation("Executing new command...");
        
        // Load config
        var config = await _configService.LoadAsync(
            settings.ConfigFile, 
            ConfigService.GeneratedConfigFileName);
        
        // Your logic here
        
        return 0; // Success
    }
}
```

### 2. Register Command

In `Program.cs`:

```csharp
app.Configure(config =>
{
    // ... existing commands ...
    
    config.AddCommand<MyNewCommand>("mynew")
        .WithDescription("Description of my new command")
        .WithExample(new[] { "mynew", "--config", "myconfig.json" });
});
```

### 3. Add Tests

Create `Tests/Commands/MyNewCommandTests.cs`:

```csharp
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class MyNewCommandTests
{
    [Fact]
    public async Task ExecuteAsync_Should_Succeed()
    {
        // Arrange
        var logger = NullLogger<MyNewCommand>.Instance;
        var configService = new ConfigService(/* ... */);
        var command = new MyNewCommand(logger, configService);
        
        // Act
        var result = await command.ExecuteAsync(/* ... */);
        
        // Assert
        Assert.Equal(0, result);
    }
}
```

---

## Adding a Configuration Property

### 1. Determine Property Type

**Static property (init)?**
- User configures once (tenant ID, resource names, etc.)
- Never changes at runtime
- Stored in `a365.config.json`

**Dynamic property (get/set)?**
- Generated by CLI (IDs, timestamps, secrets)
- Modified at runtime
- Stored in `a365.generated.config.json`

### 2. Add to Model

In `Models/Agent365Config.cs`:

```csharp
// For static property
/// <summary>
/// Description of the property.
/// </summary>
[JsonPropertyName("myProperty")]
public string MyProperty { get; init; } = string.Empty;

// For dynamic property
/// <summary>
/// Description of the property.
/// </summary>
[JsonPropertyName("myRuntimeProperty")]
public string? MyRuntimeProperty { get; set; }
```

### 3. Update JSON Schema

In `a365.config.schema.json`:

```json
{
  "properties": {
    "myProperty": {
      "type": "string",
      "description": "Description of the property",
      "examples": ["example-value"]
    }
  }
}
```

### 4. Update Example Config

In `a365.config.example.json`:

```json
{
  "myProperty": "example-value"
}
```

### 5. Add Tests

Update `Tests/Models/Agent365ConfigTests.cs`:

```csharp
[Fact]
public void MyProperty_ShouldBeImmutable()
{
    var config = new Agent365Config
    {
        MyProperty = "test-value"
    };
    
    Assert.Equal("test-value", config.MyProperty);
    // Cannot reassign - this would be a compile error:
    // config.MyProperty = "new-value";
}
```

---

## Code Conventions

### Naming

- **Commands:** `{Verb}Command.cs` (e.g., `SetupCommand.cs`)
- **Services:** `{Noun}Service.cs` or `{Noun}Configurator.cs`
- **Tests:** `{ClassName}Tests.cs`
- **Private fields:** `_camelCase` with underscore
- **Public properties:** `PascalCase`

### Logging

Use structured logging with ILogger:

```csharp
_logger.LogInformation("Starting deployment to {ResourceGroup}", 
    config.ResourceGroup);
    
_logger.LogWarning("Configuration {Property} is missing", 
    nameof(config.TenantId));
    
_logger.LogError("Deployment failed: {Error}", ex.Message);
```

### Error Handling

```csharp
// Return non-zero for errors
if (string.IsNullOrEmpty(config.TenantId))
{
    _logger.LogError("Tenant ID is required");
    return 1;
}

// Catch and log exceptions
try
{
    await DeployAsync();
}
catch (Exception ex)
{
    _logger.LogError(ex, "Deployment failed");
    return 1;
}

return 0; // Success
```

### Configuration Access

```csharp
// Load merged config
var config = await _configService.LoadAsync(
    userConfigPath, 
    stateConfigPath);

// Modify dynamic properties
config.AgentBlueprintId = "new-id";
config.LastUpdated = DateTime.UtcNow;

// Save state (only dynamic properties)
await _configService.SaveStateAsync(config, stateConfigPath);
```

---

## Testing Strategy

### Unit Tests

- Test individual services in isolation
- Mock dependencies
- Use xUnit framework
- Test both success and failure cases

### Integration Tests

- Test command execution end-to-end
- Use test configurations
- Clean up resources after tests

### Test Organization

```
Tests/
├─ Commands/         # Command execution tests
├─ Services/         # Service logic tests
└─ Models/           # Model serialization tests
```

---

## Debugging

### Debug in VS Code

1. Open `Microsoft.Agents.A365.DevTools.Cli.sln` in VS Code
2. Set breakpoints
3. Press F5 or use "Run and Debug"
4. Arguments configured in `.vscode/launch.json`

### Debug Installed Tool

```bash
# Get tool path
where a365  # Windows
which a365  # Linux/Mac

# Attach debugger to process
# Or add: System.Diagnostics.Debugger.Launch(); to code
```

### Verbose Logging

```bash
# Enable detailed logging
$env:LOGGING__LOGLEVEL__DEFAULT = "Debug"
a365 setup
```

---

## Release Process

### Version Numbering

Follow Semantic Versioning: `MAJOR.MINOR.PATCH[-PRERELEASE]`

- **MAJOR:** Breaking changes
- **MINOR:** New features (backward compatible)
- **PATCH:** Bug fixes
- **PRERELEASE:** `-beta.1`, `-rc.1`, etc.

### Create Release

1. Update version in `Microsoft.Agents.A365.DevTools.Cli.csproj`:
   ```xml
   <Version>1.0.0-beta.2</Version>
   ```

2. Build and pack:
   ```bash
   dotnet clean
   dotnet build -c Release
   dotnet pack -c Release
   ```

3. Test locally:
   ```bash
   dotnet tool uninstall -g Microsoft.Agents.A365.DevTools.Cli
   dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli \
     --add-source ./bin/Release \
     --prerelease
   ```

4. Publish to NuGet (when ready):
   ```bash
   dotnet nuget push ./bin/Release/Microsoft.Agents.A365.DevTools.Cli.1.0.0-beta.2.nupkg \
     --source https://api.nuget.org/v3/index.json \
     --api-key YOUR_API_KEY
   ```

---

## Troubleshooting Development Issues

### Build Errors

**Error: "The type or namespace name '...' could not be found"**
- Run: `dotnet restore`

**Error: "Duplicate resource"**
- Run: `dotnet clean` then rebuild

### Test Failures

**Tests fail with "Config file not found"**
- Ensure test config files exist in test project
- Use `Path.Combine` for cross-platform paths

**Tests fail with Azure CLI errors**
- Mock `CommandExecutor` in tests
- Don't call real Azure CLI in unit tests

### Installation Issues

**Tool already installed error**
- Uninstall first: `dotnet tool uninstall -g Microsoft.Agents.A365.DevTools.Cli`
- Use `.\install-cli.ps1` which handles this automatically

**"a365: The term 'a365' is not recognized" after installation**

This happens when `%USERPROFILE%\.dotnet\tools` is not in your PATH environment variable.

**Quick Fix (Current Session Only):**
```powershell
# Add to current PowerShell session
$env:PATH += ";$env:USERPROFILE\.dotnet\tools"
a365 --version  # Test it works
```

**Permanent Fix (Recommended):**
```powershell
# Add permanently to user PATH
$userToolsPath = "$env:USERPROFILE\.dotnet\tools"
$currentUserPath = [Environment]::GetEnvironmentVariable("Path", "User")

if ($currentUserPath -like "*$userToolsPath*") {
    Write-Host "Already in user PATH: $userToolsPath" -ForegroundColor Green
} else {
    [Environment]::SetEnvironmentVariable("Path", "$currentUserPath;$userToolsPath", "User")
    Write-Host "Added to user PATH permanently" -ForegroundColor Green
    Write-Host "Restart PowerShell/Terminal for this to take effect" -ForegroundColor Yellow
}
```

After permanent fix:
1. Close and reopen PowerShell/Terminal
2. Run `a365 --version` to verify

**Alternative: Manual PATH Update (Windows)**
1. Open System Properties → Environment Variables
2. Under "User variables", select "Path" → Edit
3. Add new entry: `C:\Users\YourUsername\.dotnet\tools`
4. Click OK and restart terminal

**Linux/Mac:**
Add to `~/.bashrc` or `~/.zshrc`:
```bash
export PATH="$PATH:$HOME/.dotnet/tools"
```
Then run: `source ~/.bashrc` (or `source ~/.zshrc`)

---

## Contributing

### Pull Request Process

1. Create feature branch: `git checkout -b feature/my-feature`
2. Make changes and add tests
3. Ensure all tests pass: `dotnet test`
4. Update documentation if needed
5. Submit PR with clear description

### Code Review Checklist

- [ ] Tests added/updated
- [ ] Documentation updated
- [ ] Follows code conventions
- [ ] No breaking changes (or documented)
- [ ] Error handling implemented
- [ ] Logging added

---

## Resources

- **Spectre.Console:** https://spectreconsole.net/
- **Azure CLI Reference:** https://learn.microsoft.com/cli/azure/
- **Microsoft Graph API:** https://learn.microsoft.com/graph/
- **xUnit Testing:** https://xunit.net/

---

## Architecture Decisions

### Why Unified Config Model?

**Problem:** Multiple config files with duplicated data led to:
- Inconsistency between setup/createinstance/deploy configs
- Manual merging required
- Type mismatches
- Difficult to maintain

**Solution:** Single `Agent365Config` model with:
- Clear static (init) vs dynamic (get/set) semantics
- Automatic merge/split via ConfigService
- Type safety across all commands
- Single source of truth

### Why Two Config Files?

**Why not one file?**
- Separating user config from generated state
- User config can be version controlled (without secrets)
- Generated state is gitignored (contains IDs and secrets)
- Clear ownership: users edit their config, CLI manages state

**Why not three+ files?**
- Previous approach (setup/createinstance/deploy configs) caused duplication
- Unified model reduces cognitive load
- Easier to understand data flow

### Why Spectre.Console?

- Rich, colorful console output
- Progress indicators and spinners
- Table formatting
- Command-line parsing
- Active development and community

---

For end-user documentation, see [../README.md](../README.md).


## Logging and Debugging

### Automatic Command Logging

The CLI automatically logs all command execution to per-command log files for debugging. This follows Microsoft CLI patterns (Azure CLI, .NET CLI).

**Log Location:**
- **Windows:** `%LocalAppData%\Microsoft.Agents.A365.DevTools.Cli\logs\`
- **Linux/Mac:** `~/.config/a365/logs/`

**Log Files:**
```
logs/
??? a365.setup.log           # Latest 'a365 setup' execution
??? a365.deploy.log          # Latest 'a365 deploy' execution  
??? a365.create-instance.log # Latest 'a365 create-instance' execution
??? a365.cleanup.log         # Latest 'a365 cleanup' execution
```

**Behavior:**
- Always on - No configuration needed
- Per-command - Each command has its own log file
- Auto-overwrite - Keeps only the latest run (simplifies debugging)
- Detailed timestamps - `[yyyy-MM-dd HH:mm:ss.fff] [LEVEL] Message`
- Includes exceptions - Full stack traces for errors
- 10 MB limit - Prevents disk space issues

**Example Log Output:**
```
==========================================================
Agent365 CLI - Command: setup
Version: 1.0.0
Log file: C:\Users\...\logs\a365.setup.log
Started at: 2025-11-15 10:30:45
==========================================================

[2024-01-15 10:30:45.123] [INF] Agent365 Setup - Starting...
[2024-01-15 10:30:45.456] [INF] Subscription: abc123-...
[2024-01-15 10:30:46.789] [ERR] Configuration validation failed
[2024-01-15 10:30:46.790] [ERR]    WebAppName can only contain alphanumeric characters and hyphens
```

**Finding Your Logs:**

**Windows (PowerShell):**
```powershell
# View latest setup log
Get-Content $env:LOCALAPPDATA\Microsoft.Agents.A365.DevTools.Cli\logs\a365.setup.log -Tail 50

# Open logs directory
explorer $env:LOCALAPPDATA\Microsoft.Agents.A365.DevTools.Cli\logs
```

**Linux/Mac:**
```bash
# View latest setup log
tail -50 ~/.config/a365/logs/a365.setup.log

# Open logs directory
open ~/.config/a365/logs  # Mac
xdg-open ~/.config/a365/logs  # Linux
```

**Debugging Failed Commands:**

When a command fails:
1. Locate the log file for that command (see paths above)
2. Search for `[ERR]` entries
3. Check the full stack trace at the end of the log
4. Share the log file when reporting issues

**Implementation Details:**

Logging is implemented using Serilog with dual sinks:
- **Console sink** - User-facing output (clean, no timestamps)
- **File sink** - Debugging output (detailed, with timestamps and stack traces)

Command name detection is automatic - the CLI analyzes command-line arguments to determine which command is running.

---


