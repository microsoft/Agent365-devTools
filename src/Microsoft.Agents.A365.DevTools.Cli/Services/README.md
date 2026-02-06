# Services

This folder contains business logic services used by CLI commands. Services are registered in `Program.cs` via dependency injection and injected into commands via constructor.

> **Parent:** [CLI Design](../design.md) | **Subfolders:** [Helpers/](Helpers/README.md), [Internal/](Internal/), [Requirements/](Requirements/)

---

## Service Groups

### Core Services

| Service | File | Description |
|---------|------|-------------|
| **IConfigService** | `IConfigService.cs` | Configuration management interface |
| **ConfigService** | `ConfigService.cs` | Load/merge/save configuration (two-file model) |
| **ConfigurationWizardService** | `ConfigurationWizardService.cs` | Interactive configuration wizard |
| **IProcessService** | `IProcessService.cs` | External process execution interface |
| **ProcessService** | `ProcessService.cs` | Run external commands (az, dotnet, etc.) |
| **CommandExecutor** | `CommandExecutor.cs` | Execute shell commands with output capture |
| **IVersionCheckService** | `IVersionCheckService.cs` | CLI version checking interface |
| **VersionCheckService** | `VersionCheckService.cs` | Check for CLI updates |

### Agent Blueprint Services

| Service | File | Description |
|---------|------|-------------|
| **IAgentBlueprintService** | `IAgentBlueprintService.cs` | Blueprint management interface |
| **AgentBlueprintService** | `AgentBlueprintService.cs` | Create/manage agent blueprints |
| **BlueprintLookupService** | `BlueprintLookupService.cs` | Query existing blueprints |
| **AgentPublishService** | `AgentPublishService.cs` | Publish agents to MOS Titles |
| **ManifestTemplateService** | `ManifestTemplateService.cs` | Embedded manifest template handling |

### Platform Builders

| Service | File | Description |
|---------|------|-------------|
| **IPlatformBuilder** | `IPlatformBuilder.cs` | Platform builder interface |
| **PlatformDetector** | `PlatformDetector.cs` | Auto-detect project platform (.NET/Node/Python) |
| **DotNetBuilder** | `DotNetBuilder.cs` | .NET project build and deployment |
| **NodeBuilder** | `NodeBuilder.cs` | Node.js project build and deployment |
| **PythonBuilder** | `PythonBuilder.cs` | Python project build and deployment |
| **DeploymentService** | `DeploymentService.cs` | Multiplatform deployment orchestration |

### Azure Services

| Service | File | Description |
|---------|------|-------------|
| **IAzureCliService** | `IAzureCliService.cs` | Azure CLI operations interface |
| **AzureCliService** | `AzureCliService.cs` | Azure CLI wrapper (webapp, account, etc.) |
| **IAzureSetupService** | `IAzureSetupService.cs` | Azure resource setup interface |
| **AzureWebAppCreator** | `AzureWebAppCreator.cs` | Create and configure Azure Web Apps |
| **AzureValidator** | `AzureValidator.cs` | Validate Azure configuration |
| **AzureAuthValidator** | `AzureAuthValidator.cs` | Validate Azure authentication |
| **AzureEnvironmentValidator** | `AzureEnvironmentValidator.cs` | Validate Azure environment settings |
| **IBotConfigurator** | `IBotConfigurator.cs` | Bot configuration interface |
| **BotConfigurator** | `BotConfigurator.cs` | Register messaging endpoints |

### Authentication Services

| Service | File | Description |
|---------|------|-------------|
| **AuthenticationService** | `AuthenticationService.cs` | MSAL.NET authentication orchestration |
| **InteractiveGraphAuthService** | `InteractiveGraphAuthService.cs` | Interactive browser authentication |
| **MsalBrowserCredential** | `MsalBrowserCredential.cs` | MSAL browser credential provider |
| **MosTokenService** | `MosTokenService.cs` | MOS Titles token acquisition |
| **DelegatedConsentService** | `DelegatedConsentService.cs` | Handle delegated consent flows |
| **AdminConsentHelper** | `AdminConsentHelper.cs` | Admin consent URL generation |

### Graph API Services

| Service | File | Description |
|---------|------|-------------|
| **GraphApiService** | `GraphApiService.cs` | Microsoft Graph API interactions |
| **FederatedCredentialService** | `FederatedCredentialService.cs` | Manage federated identity credentials |
| **IClientAppValidator** | `IClientAppValidator.cs` | Client app validation interface |
| **ClientAppValidator** | `ClientAppValidator.cs` | Validate custom client applications |

### MCP/Dataverse Services

| Service | File | Description |
|---------|------|-------------|
| **IAgent365ToolingService** | `IAgent365ToolingService.cs` | Tooling service interface |
| **Agent365ToolingService** | `Agent365ToolingService.cs` | MCP server management in Dataverse |
| **A365CreateInstanceRunner** | `A365CreateInstanceRunner.cs` | Agent instance creation workflow |

### Other Services

| Service | File | Description |
|---------|------|-------------|
| **IConfirmationProvider** | `IConfirmationProvider.cs` | User confirmation interface |
| **ConsoleConfirmationProvider** | `ConsoleConfirmationProvider.cs` | Console-based confirmation prompts |
| **ISubCommand** | `ISubCommand.cs` | Subcommand execution interface |

---

## Service Registration (Program.cs)

Services are registered as singletons (stateless) or transient (command-specific):

```csharp
services.AddSingleton<IConfigService, ConfigService>();
services.AddSingleton<IProcessService, ProcessService>();
services.AddSingleton<IPlatformBuilder, DotNetBuilder>();
services.AddSingleton<IPlatformBuilder, NodeBuilder>();
services.AddSingleton<IPlatformBuilder, PythonBuilder>();
services.AddTransient<GraphApiService>();
```

---

## Cross-References

- **[CLI Design](../design.md)** - Overall CLI architecture
- **[Commands/](../Commands/README.md)** - Command implementations
- **[Helpers/](Helpers/README.md)** - Service helper utilities
- **[DEVELOPER.md](../../DEVELOPER.md)** - How to add new services
