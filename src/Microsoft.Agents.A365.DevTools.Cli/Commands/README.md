# Commands

This folder contains CLI command implementations. Each command inherits from `AsyncCommand<Settings>` (Spectre.Console) and returns an integer exit code.

> **Parent:** [CLI Design](../design.md) | **How-to:** [Adding a New Command](../../DEVELOPER.md#adding-a-new-command)

---

## Command Reference

| Command | File | Description |
|---------|------|-------------|
| **config** | `ConfigCommand.cs` | Configuration management (`init`, `display`) |
| **setup** | `SetupCommand.cs` | Agent blueprint creation and messaging endpoint registration |
| **create-instance** | `CreateInstanceCommand.cs` | Agent identity, licenses, and notifications setup |
| **deploy** | `DeployCommand.cs` | Multiplatform deployment to Azure App Service |
| **cleanup** | `CleanupCommand.cs` | Delete agent resources (blueprint, instance, Azure) |
| **publish** | `PublishCommand.cs` | Publish agent manifest to MOS Titles service |
| **query-entra** | `QueryEntraCommand.cs` | Query Entra ID scopes for blueprints and instances |
| **develop** | `DevelopCommand.cs` | Development utilities (tokens, permissions, mock server) |
| **develop-mcp** | `DevelopMcpCommand.cs` | MCP server management in Dataverse environments |

---

## Subcommand Folders

| Folder | Description |
|--------|-------------|
| **[SetupSubcommands/](SetupSubcommands/README.md)** | Setup workflow components (blueprint, infrastructure, permissions) |
| **DevelopSubcommands/** | Development command components (tokens, permissions, mock server) |

---

## Command Pattern

All commands follow this pattern:

```csharp
public class MyCommand : AsyncCommand<MyCommand.Settings>
{
    private readonly ILogger<MyCommand> _logger;
    private readonly IConfigService _configService;

    public MyCommand(ILogger<MyCommand> logger, IConfigService configService)
    {
        _logger = logger;
        _configService = configService;
    }

    public class Settings : CommandSettings
    {
        [CommandOption("--config")]
        [Description("Path to configuration file")]
        public string? ConfigFile { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // 1. Load configuration
        var config = await _configService.LoadAsync(settings.ConfigFile);

        // 2. Execute business logic (via services)
        // ...

        // 3. Return exit code
        return 0; // Success, or ErrorCodes.* for failure
    }
}
```

**Guidelines:**
- Keep commands thin - delegate to services
- Use `ErrorCodes` constants for exit codes
- Log with structured placeholders: `_logger.LogInformation("Processing {Item}", item)`
- Dispose `IDisposable` resources (especially `HttpResponseMessage`)

---

## Cross-References

- **[CLI Design](../design.md)** - Overall CLI architecture
- **[Services/](../Services/README.md)** - Business logic implementations
- **[Constants/](../Constants/README.md)** - Error codes and messages
- **[DEVELOPER.md](../../DEVELOPER.md)** - How to add new commands
