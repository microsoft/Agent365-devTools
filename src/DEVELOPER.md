# Microsoft.Agents.A365.DevTools.Cli - Developer Guide

This guide is for contributors and maintainers of the Agent 365 CLI codebase. For end-user installation and usage, see [README.md](./README.md).

---

## Project Overview

The Agent 365 CLI (`a365`) is a .NET tool that automates the deployment and management of Agent 365 applications on Azure. It handles:

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
│  └─ DevelopCommand.cs         # a365 develop
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

- `a365 config init` — Interactively prompts for required config values and writes `a365.config.json`.
- `a365 config init -c <file>` — Imports and validates a config file, then writes it to the standard location.
- `a365 config display` — Prints the current configuration.

## Inheritable Permissions: Best Practice

Agent 365 CLI and the Agent 365 platform are designed to use inheritable permissions on agent blueprints. This means:

- **Agent identities automatically inherit OAuth2 scopes from the blueprint.**
- **No additional admin consent is required for agent identities** (as long as the blueprint’s service principal has been granted the required permissions).
- This is the default and recommended approach for all agent developers.

**If inheritable permissions are disabled:**
- Agent identities will NOT inherit permissions from the blueprint.
- You must manually assign permissions and request admin consent for each identity.
- This is NOT recommended and will create significant friction.

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

**Pattern**: `A365_{CATEGORY}_{ENVIRONMENT}`

**Supported Environment Variables:**

1. **MCP Platform App IDs**:
   ```bash
   # Override MCP Platform Application ID for specific environments
   A365_MCP_APP_ID_PREPROD=05879165-0320-489e-b644-f72b33f3edf0
   A365_MCP_APP_ID_TEST=05879165-0320-489e-b644-f72b33f3edf0
   A365_MCP_APP_ID_CUSTOM=your-custom-app-id
   ```

2. **Discover Endpoints**:

**Implementation Pattern** (in `ConfigConstants.cs`):
```csharp
public static string GetMcpPlatformResourceAppId(string environment)
{
    // Check for custom app ID in environment variable first
    var customAppId = Environment.GetEnvironmentVariable($"A365_MCP_APP_ID_{environment?.ToUpper()}");
    if (!string.IsNullOrEmpty(customAppId))
        return customAppId;

    // Default to production app ID
    return environment?.ToLower() switch
    {
        "prod" => "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1",
        _ => "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1"
    };
}
```

**Benefits:**
- ✅ **Public Repository Ready**: No internal endpoints or app IDs in source code
- ✅ **Flexible for Internal Development**: Microsoft developers can override via environment variables
- ✅ **Secure**: No secrets hardcoded in the codebase
- ✅ **Simple**: Easy to understand and maintain

**Usage Examples:**
```bash
# Custom deployment
export A365_MCP_APP_ID_STAGING=your-staging-app-id
export A365_DISCOVER_ENDPOINT_STAGING=https://staging.yourdomain.com/agents/discoverToolServers

# Run CLI with overrides
a365 setup --environment prod
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
git clone https://github.com/microsoft/Agent365.git
cd Agent365/utils/scripts/developer

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
# From developer/ directory
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

