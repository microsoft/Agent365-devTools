# Microsoft.Agents.A365.DevTools.Cli - Developer Guide

This guide is for contributors and maintainers of the Microsoft Agent 365 CLI codebase. For end-user installation and usage, see [README.md](./README.md).

> **Architecture Documentation:** For architecture, design patterns, and architectural decisions, see [docs/design.md](../docs/design.md). This guide focuses on **how-to** information for development workflows.

---

## Quick Links

| Document | Purpose |
|----------|---------|
| **[Repository Design](../docs/design.md)** | High-level architecture, patterns, decisions |
| **[CLI Design](Microsoft.Agents.A365.DevTools.Cli/design.md)** | CLI project architecture, configuration system |
| **[MockToolingServer Design](Microsoft.Agents.A365.DevTools.MockToolingServer/design.md)** | Mock MCP server architecture |
| **This Guide** | Build, test, add commands, contribute |

## Python Project Support

The CLI fully supports Python Agent 365 projects:

- **Auto-detection** via `pyproject.toml` and `*.py` files
- **Runtime configuration** - Sets correct `PYTHON|3.11` runtime automatically
- **Environment variables** - Converts `.env` to Azure App Settings automatically
- **Local dependencies** - Handles Agent 365 package wheels in `dist/` folder using `--find-links`
- **Entry point detection** - Prioritizes `start_with_generic_host.py` with smart content analysis
- **Build automation** - Creates `.deployment` file to force Oryx Python build
- **Startup commands** - Sets correct startup command for Azure Web Apps automatically

### Python Deployment Flow
1. **Platform Detection** - Identifies Python projects via `pyproject.toml`
2. **Clean Build** - Removes old artifacts, copies project files
3. **Local Packages** - Runs `uv build` if needed, copies `dist/` folder
4. **Requirements.txt** - Creates Azure-native requirements with `--find-links dist`, `--pre`
5. **Environment Setup** - Converts `.env` to Azure App Settings
6. **Build Configuration** - Creates `.deployment` file with `SCM_DO_BUILD_DURING_DEPLOYMENT=true`
7. **Deployment** - Uploads zip, Azure runs `pip install`, starts app

---

## Project Structure

> **Detailed architecture:** See [CLI Design](Microsoft.Agents.A365.DevTools.Cli/design.md) for folder structure, configuration system, and component details.

```
Microsoft.Agents.A365.DevTools.Cli/
├─ Program.cs                    # CLI entry point, DI registration
├─ Commands/                     # Command implementations
├─ Services/                     # Business logic services
├─ Models/                       # Data models
├─ Constants/                    # Error codes, messages, auth constants
├─ Exceptions/                   # Custom exception types
├─ Helpers/                      # Utility helpers
└─ Templates/                    # Embedded resources
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

> **Detailed documentation:** See [CLI Design - Permissions Architecture](Microsoft.Agents.A365.DevTools.Cli/design.md#permissions-architecture).

The CLI configures three layers of permissions for agent blueprints:
1. **OAuth2 Grants** - Admin consent via Graph API
2. **Required Resource Access** - Portal-visible permissions
3. **Inheritable Permissions** - Blueprint-level permissions that instances inherit

Agent instances automatically inherit permissions from blueprint - no additional admin consent required.

### Adding/Extending Config Properties

To add a new configuration property:

1. Add the property to `Agent365Config.cs` (with appropriate `[JsonPropertyName]` attribute).
2. Update the validation logic in `Agent365Config.Validate()` if needed.
3. Update `a365.config.schema.json` and `a365.config.example.json`.
4. (Optional) Update prompts in `ConfigCommand.cs` for interactive init.
5. Add or update tests in `Tests/Commands/ConfigCommandTests.cs`.


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

Version is managed automatically by [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning) via `src/version.json`. The NuGet publish process is fully automated through GitHub Actions.

**Steps to release:**

1. **Update CHANGELOG.md** — move items from `[Unreleased]` to a new version section (e.g., `[1.2.0] - YYYY-MM`). Update the comparison links at the bottom.

2. **Merge to main** — CI runs automatically: builds, tests, and uploads the NuGet package as a build artifact.

3. **Publish the GitHub release draft** — release-drafter auto-creates a draft release from merged PR titles and labels. Go to [GitHub Releases](https://github.com/microsoft/Agent365-devTools/releases), review the draft, set the correct version tag (e.g., `v1.2.0`), and click **Publish release**.

4. **NuGet publish runs automatically** — the `release.yml` workflow triggers on `release: published` and pushes the package to NuGet.org using the `NUGET_API_KEY` repository secret.

**Test locally before releasing:**
```bash
cd src
dotnet build dirs.proj -c Release
dotnet pack dirs.proj -c Release --output ../NuGetPackages

dotnet tool uninstall -g Microsoft.Agents.A365.DevTools.Cli
dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli \
  --add-source ../NuGetPackages \
  --prerelease
a365 --version
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
- [ ] CHANGELOG.md updated in `[Unreleased]` (required for user-facing changes: features, bug fixes, behavioral changes)

---

## Resources

- **Spectre.Console:** https://spectreconsole.net/
- **Azure CLI Reference:** https://learn.microsoft.com/cli/azure/
- **Microsoft Graph API:** https://learn.microsoft.com/graph/
- **xUnit Testing:** https://xunit.net/

---

## Architecture Decisions

> **Detailed documentation:** See [Repository Design - Architecture Decisions](../docs/design.md#architecture-decisions).

Key decisions documented in the architecture docs:
- Why unified config model (single `Agent365Config` vs multiple files)
- Why two config files (user-managed vs CLI-managed separation)
- Why Spectre.Console (rich console output, parsing, active community)

---

For end-user documentation, see [../README.md](../README.md).

---

## See Also

- **[Repository Design](../docs/design.md)** - High-level architecture, patterns, decisions
- **[CLI Design](Microsoft.Agents.A365.DevTools.Cli/design.md)** - CLI project architecture
- **[MockToolingServer Design](Microsoft.Agents.A365.DevTools.MockToolingServer/design.md)** - Mock MCP server architecture


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


