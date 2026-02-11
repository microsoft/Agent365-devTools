# Exceptions

This folder contains custom exception types used throughout the CLI. Custom exceptions provide specific error handling and user-friendly error messages.

> **Parent:** [CLI Design](../design.md)

---

## Exception Reference

### Base Exception

| Exception | File | Description |
|-----------|------|-------------|
| **Agent365Exception** | `Agent365Exception.cs` | Base exception for all CLI exceptions, includes error code |

### Configuration Exceptions

| Exception | File | Scenario |
|-----------|------|----------|
| **ConfigurationValidationException** | `ConfigurationValidationException.cs` | Invalid configuration values |
| **SetupValidationException** | `SetupValidationException.cs` | Setup prerequisites not met |
| **ClientAppValidationException** | `ClientAppValidationException.cs` | Invalid custom client app configuration |

### Azure Exceptions

| Exception | File | Scenario |
|-----------|------|----------|
| **AzureExceptions** | `AzureExceptions.cs` | Azure resource operation failures (contains multiple types) |

### Deployment Exceptions

| Exception | File | Scenario |
|-----------|------|----------|
| **DeployAppException** | `DeployAppException.cs` | Application deployment failure |
| **DeployMcpException** | `DeployMcpException.cs` | MCP server deployment failure |
| **DeployAppPythonCompileException** | `DeployAppPythonCompileException.cs` | Python compilation error during deployment |

### Platform Builder Exceptions

| Exception | File | Scenario |
|-----------|------|----------|
| **DotNetSdkVersionMismatchException** | `DotNetSdkVersionMismatchException.cs` | .NET SDK version incompatibility |
| **NodeProjectNotFoundException** | `NodeProjectNotFoundException.cs` | Missing package.json |
| **NodeDependencyInstallException** | `NodeDependencyInstallException.cs` | npm install failure |
| **NodeBuildFailedException** | `NodeBuildFailedException.cs` | Node.js build failure |
| **PythonLocatorException** | `PythonLocatorException.cs` | Python not found or wrong version |

### Authentication Exceptions

| Exception | File | Scenario |
|-----------|------|----------|
| **GraphTokenScopeException** | `GraphTokenScopeException.cs` | Invalid or missing Graph API scopes |

### Utility Exceptions

| Exception | File | Scenario |
|-----------|------|----------|
| **RetryExhaustedException** | `RetryExhaustedException.cs` | Maximum retries exceeded |

### Exception Handler

| Component | File | Description |
|-----------|------|-------------|
| **ExceptionHandler** | `ExceptionHandler.cs` | Global exception handling and user-friendly formatting |

---

## Agent365Exception Base Class

All custom exceptions inherit from `Agent365Exception`:

```csharp
public class Agent365Exception : Exception
{
    public int ErrorCode { get; }
    public string? Mitigation { get; }

    public Agent365Exception(string message, int errorCode, string? mitigation = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Mitigation = mitigation;
    }
}
```

**Properties:**
- `ErrorCode` - Exit code from `ErrorCodes` constants
- `Mitigation` - Optional user-friendly fix suggestion

---

## ExceptionHandler

The `ExceptionHandler` class provides global exception handling:

```csharp
public static class ExceptionHandler
{
    public static int Handle(Exception ex, ILogger logger)
    {
        if (ex is Agent365Exception a365Ex)
        {
            logger.LogError(a365Ex.Message);
            if (a365Ex.Mitigation != null)
                logger.LogInformation("Suggestion: {Mitigation}", a365Ex.Mitigation);
            return a365Ex.ErrorCode;
        }

        logger.LogError(ex, "An unexpected error occurred");
        return ErrorCodes.GeneralError;
    }
}
```

---

## Usage Pattern

```csharp
// Throwing a custom exception
if (string.IsNullOrEmpty(config.TenantId))
{
    throw new ConfigurationValidationException(
        "TenantId is required",
        ErrorCodes.ConfigurationError,
        "Run 'a365 config init' to configure your environment"
    );
}

// In command Execute method
try
{
    await DoWorkAsync();
    return ErrorCodes.Success;
}
catch (Agent365Exception ex)
{
    return ExceptionHandler.Handle(ex, _logger);
}
```

---

## Cross-References

- **[CLI Design](../design.md)** - Architecture overview
- **[Constants/](../Constants/README.md)** - Error codes and messages
- **[Commands/](../Commands/README.md)** - Commands that throw these exceptions
