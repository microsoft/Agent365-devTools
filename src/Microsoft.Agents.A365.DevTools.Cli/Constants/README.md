# Constants

This folder contains centralized constant definitions used throughout the CLI. Centralizing constants improves maintainability and ensures consistency.

> **Parent:** [CLI Design](../design.md)

---

## Constant Files

| File | Description | Usage |
|------|-------------|-------|
| **ErrorCodes.cs** | Exit code constants for CLI operations | Return from commands, exception handling |
| **ErrorMessages.cs** | User-facing error message templates | Logging, exception messages, console output |
| **AuthenticationConstants.cs** | OAuth scopes, redirect URIs, authority URLs | Authentication services |
| **ConfigConstants.cs** | Configuration-related constants, environment URLs | ConfigService, endpoint resolution |
| **McpConstants.cs** | MCP (Model Context Protocol) constants | Agent 365 Tools App IDs, MCP endpoints |
| **MosConstants.cs** | MOS (Microsoft Online Services) Titles constants | PublishCommand, MosTokenService |
| **GraphApiConstants.cs** | Microsoft Graph API constants | GraphApiService, permission configuration |
| **CommandNames.cs** | CLI command name strings | Command registration, help text |

---

## ErrorCodes

Defines integer exit codes for CLI operations:

```csharp
public static class ErrorCodes
{
    public const int Success = 0;
    public const int GeneralError = 1;
    public const int ConfigurationError = 2;
    public const int AuthenticationError = 3;
    public const int AzureError = 4;
    public const int GraphApiError = 5;
    public const int DeploymentError = 6;
    public const int ValidationError = 7;
    // ... more codes
}
```

**Usage:**
```csharp
return ErrorCodes.ConfigurationError;
```

---

## ErrorMessages

Provides user-facing error message templates:

```csharp
public static class ErrorMessages
{
    public static string ConfigFileNotFound(string path)
        => $"Configuration file not found: {path}";

    public static string GetMosServicePrincipalMitigation()
        => "Run the following commands to create required service principals:...";

    public static string GetMosAdminConsentMitigation(string clientAppId)
        => $"Admin consent required. Visit: https://login.microsoftonline.com/...";
}
```

**Guidelines:**
- Use methods with parameters for dynamic content
- Keep messages user-friendly and actionable
- Include mitigation steps where possible

---

## ConfigConstants

Configuration and endpoint constants with environment variable support:

```csharp
public static class ConfigConstants
{
    public static string GetAgent365ToolsResourceAppId(string environment)
    {
        // Check environment variable override first
        var envVar = Environment.GetEnvironmentVariable($"A365_MCP_APP_ID_{environment?.ToUpper()}");
        if (!string.IsNullOrEmpty(envVar)) return envVar;

        // Default to production
        return McpConstants.Agent365ToolsProdAppId;
    }

    public static string GetDiscoverEndpointUrl(string environment)
    {
        // Similar pattern with environment variable override
    }
}
```

**Design:** All test/preprod values removed from codebase. Internal developers use environment variables.

---

## McpConstants

MCP-related constants:

```csharp
public static class McpConstants
{
    // Production App ID (only hardcoded value - see source for actual value)
    public const string Agent365ToolsProdAppId = "...";

    // Resource identifiers
    public const string MessagingBotApiResourceId = "...";
    public const string ObservabilityApiResourceId = "...";
}
```

---

## MosConstants

MOS Titles service constants:

```csharp
public static class MosConstants
{
    // MOS Resource App IDs (for service principal creation - see source for actual values)
    public const string TpsAppServicesAppId = "...";
    public const string PowerPlatformApiAppId = "...";
    public const string MosTitlesApiAppId = "...";

    // Environment-specific scopes
    public static string GetMosScope(string environment) => environment switch
    {
        "prod" => "api://...",
        "sdf" => "api://...",
        _ => "api://..."
    };
}
```

---

## Cross-References

- **[CLI Design](../design.md)** - Architecture overview
- **[Commands/](../Commands/README.md)** - Commands that use these constants
- **[Exceptions/](../Exceptions/README.md)** - Exceptions that use error codes/messages
