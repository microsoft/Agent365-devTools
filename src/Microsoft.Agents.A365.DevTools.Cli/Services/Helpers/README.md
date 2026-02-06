# Services/Helpers

This folder contains helper utilities used by service implementations. These are typically stateless utility classes that provide common functionality.

> **Parent:** [Services](../README.md) | **CLI Design:** [design.md](../../design.md)

---

## Helper Reference

| Helper | File | Description |
|--------|------|-------------|
| **BuilderHelper** | `BuilderHelper.cs` | Common build operations (copy files, create zip, etc.) |
| **DotNetProjectHelper** | `DotNetProjectHelper.cs` | .NET project file parsing and analysis |
| **PythonLocator** | `PythonLocator.cs` | Locate Python installation and entry points |
| **EndpointHelper** | `EndpointHelper.cs` | Construct and validate endpoint URLs |
| **RetryHelper** | `RetryHelper.cs` | Exponential backoff retry logic |
| **AdminConsentHelper** | `AdminConsentHelper.cs` | Admin consent URL generation and handling |
| **JsonDeserializationHelper** | `JsonDeserializationHelper.cs` | Safe JSON deserialization with error handling |
| **CleanConsoleFormatter** | `CleanConsoleFormatter.cs` | Console output formatting (no timestamps) |
| **FileLoggerProvider** | `FileLoggerProvider.cs` | File-based logging provider |
| **LoggerFactoryHelper** | `LoggerFactoryHelper.cs` | Logger factory creation utilities |

---

## RetryHelper

Provides exponential backoff retry logic used throughout the CLI:

```csharp
await RetryHelper.ExecuteWithRetryAsync(
    operation: async () => await SomeApiCallAsync(),
    maxRetries: 5,
    baseDelayMs: 2000,  // 2s, 4s, 8s, 16s, 32s
    shouldRetry: ex => ex is HttpRequestException
);
```

Default settings:
- Max retries: 5
- Base delay: 2 seconds
- Exponential backoff: 2s, 4s, 8s, 16s, 32s

---

## BuilderHelper

Common operations used by platform builders:

- **CopyDirectoryAsync** - Recursively copy directory with exclusions
- **CreateZipAsync** - Create deployment ZIP package
- **CleanDirectoryAsync** - Remove directory contents
- **ValidateRequiredFilesAsync** - Check for required project files

---

## PythonLocator

Locates Python installation and determines entry points:

- Searches PATH for `python`, `python3`, `py`
- Validates Python version (3.11+ required)
- Detects entry point files (`start_with_generic_host.py`, `app.py`, `main.py`)
- Analyzes entry point content for `if __name__ == "__main__"` pattern

---

## Cross-References

- **[Services/](../README.md)** - Parent services folder
- **[CLI Design](../../design.md)** - Architecture overview
- **[Exceptions/](../../Exceptions/README.md)** - Exception types thrown by helpers
