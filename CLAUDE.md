# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Microsoft Agent 365 DevTools CLI (`a365`) - A .NET CLI tool built on .NET 8.0 with support for running on .NET 8.0 or higher (e.g., .NET 9, 10). Used for deploying and managing Microsoft Agent 365 applications on Azure. Supports .NET, Node.js, and Python applications with auto-detection.

## Build Commands

```bash
# Install CLI locally (from repo root)
.\scripts\cli\install-cli.ps1

# Manual build and install
cd src/Microsoft.Agents.A365.DevTools.Cli
dotnet clean
dotnet build -c Release
dotnet pack -c Release --no-build
dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli --add-source ./bin/Release --prerelease

# Restore all dependencies
cd src
dotnet restore dirs.proj
dotnet restore tests.proj

# Build all projects
dotnet build dirs.proj --configuration Release
```

## Test Commands

```bash
# Run all tests
cd src
dotnet test tests.proj --configuration Release

# Run specific test class
dotnet test --filter "FullyQualifiedName~SetupCommandTests"

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

**Test Framework:** xUnit with FluentAssertions and NSubstitute

**Test Location:** `src/Tests/Microsoft.Agents.A365.DevTools.Cli.Tests/`

**Parallel Test Execution:** Tests modifying environment variables or shared resources must disable parallelization:
```csharp
[CollectionDefinition("EnvTests", DisableParallelization = true)]
public class EnvTestCollection { }

[Collection("EnvTests")]
public class MyTests { }
```

## Architecture

### Project Structure
```
src/Microsoft.Agents.A365.DevTools.Cli/
├── Commands/              # CLI command implementations (AsyncCommand<Settings>)
├── Services/              # Business logic (ConfigService, DeploymentService, etc.)
├── Models/                # Data models (Agent365Config, etc.)
├── Constants/             # Centralized error codes, messages, auth constants
├── Exceptions/            # Custom exceptions
├── Templates/             # Embedded resources (manifest.json, icons)
└── Helpers/               # Helper utilities
```

### Key Patterns

1. **Command Pattern:** Commands inherit from `AsyncCommand<Settings>`, return exit codes (0=success)

2. **Configuration Architecture (Two-file design):**
   - `a365.config.json` - Static, user-managed, version-controlled
   - `a365.generated.config.json` - Dynamic, CLI-managed, gitignored
   - `Agent365Config` model has init-only (static) and get/set (dynamic) properties

3. **Platform Builder Strategy:** `IPlatformBuilder` interface with implementations for DotNet, Node, Python

4. **Dependency Injection:** ServiceCollection in Program.cs with singletons for stateless services

### Key Services
- `ConfigService` - Configuration load/merge/save with environment variable overrides
- `DeploymentService` - Multiplatform deployment orchestration
- `PlatformDetector` - Auto-detect project type (.NET/Node/Python)
- `AuthenticationService` - MSAL.NET for Azure and Graph authentication
- `GraphApiService` - Microsoft Graph API interactions

## Code Standards

### Required Copyright Header (all .cs files)
```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
```

### Naming Conventions
- Commands: `{Verb}Command.cs`
- Services: `{Noun}Service.cs` or `{Noun}Configurator.cs`
- Tests: `{ClassName}Tests.cs`
- Private fields: `_camelCase`
- Public properties: `PascalCase`

### Code Quality
- No emojis in code, comments, logs, or output
- Nullable reference types enabled (strict null checking)
- Warnings treated as errors
- All `IDisposable` objects must be disposed (especially `HttpResponseMessage`)
- Cross-platform compatibility required (Windows, macOS, Linux)

### Comments
Comments are crisp: state *why* in one line, not *what* the code already shows. Do not write essays.
- A code comment (`//` or `///` `<summary>`) is **one or two lines**. If you need a paragraph of rationale, it belongs in the commit message or PR description, not in source.
- Keep an issue/PR reference (`(issue #460)`) but drop the surrounding narration.
- Never narrate the change ("previously we did X, now Y because...") — that is commit-message content.
- Trivial mechanical edits (log-level change, blank-line separator, `catch { throw; }`) get zero or one line, never a rationale block.

```csharp
// Bad: essay restating the change and its history
// Issue #460: when inheritable permissions were configured with kind=allAllowed (covering both
// scopes and roles) AND the blueprint SP was granted the app roles, the agent identity inherits
// them automatically — the same basis the OBO branch relies on. A direct grant then only adds a
// duplicate row and a spurious prompt, so we skip it and report the inherited grant as Granted.

// Good: one-line why + reference
// Skip the direct grant when the role is already inherited from the blueprint (issue #460).
```

### CHANGELOG entries
`CHANGELOG.md` `[Unreleased]` ships verbatim to nuget.org release notes. Each entry is **one crisp consumer-facing sentence** about the user-visible change — no class/method names, no implementation mechanism, no multi-sentence rationale. Keep the `(#NNN)` reference. When a change makes a sibling entry stale, fix it in the same edit.

### Input Validation
User-controlled input that reaches file system operations must be validated before use. This applies to CLI arguments, config values read from disk, and any value whose origin is outside this process.

- Validate against an explicit allowlist pattern (e.g. `^[a-z0-9-]+$`) before interpolating into file names or paths
- Reject inputs containing path separators, `..` segments, or characters outside the allowed set
- Apply `ArgumentException.ThrowIfNullOrWhiteSpace` for string parameters where an empty or whitespace value would cause a silent failure — a non-nullable type alone is insufficient
- CLI `Option<string?>` values default to null when omitted but can be explicitly passed as empty or whitespace by the user — check `IsNullOrWhiteSpace` on option values before use, and emit a targeted error rather than falling through to a confusing downstream failure (e.g. `Directory.Exists("")`)

```csharp
// Correct: validate user input before using it in a path
if (!Regex.IsMatch(commandName, @"^[a-z0-9-]+$"))
{
    logger.LogError("Invalid command name '{Name}'. Use lowercase letters, digits, and hyphens only.", commandName);
    context.ExitCode = 1;
    return;
}

// Correct: guard non-nullable string parameters against empty/whitespace
ArgumentException.ThrowIfNullOrWhiteSpace(generatedConfigPath);
```

### CLI Command Exit Codes
Every failure path in a command handler must set `context.ExitCode = 1` before returning or continuing. This includes `continue` statements inside loops, not just top-level `return` statements.

- Trace every branch — `if (!condition) { log; continue; }` is a failure path and needs `ExitCode = 1`
- Verify the post-loop summary block covers all zero-export cases, not just the auto-discovery case

```csharp
// Wrong: warning logged but ExitCode left at 0
logger.LogWarning("No log found for '{Name}'.", name);
continue;

// Correct: set exit code before every failure exit
logger.LogWarning("No log found for '{Name}'.", name);
context.ExitCode = 1;
continue;
```

### Data Flow Consistency
When a transformation (redaction, sanitization, encoding) is applied to a value, identify every place that same value is written and apply the transformation consistently. Header lines, log output, and file names are common places where the same data appears a second time without the transformation.

- After writing redaction or sanitization logic, ask: "where else does this raw value appear in the output?"
- The source file path, for example, may appear both in log content and in a header — both need the same redaction pass

### Error Handling
- Use centralized error codes from `Constants/ErrorCodes.cs`
- Use centralized messages from `Constants/ErrorMessages.cs`
- Structured logging with `ILogger<T>` and named placeholders

## Package Management

Central NuGet package management in `src/Directory.Packages.props`. Key dependencies:
- System.CommandLine v2.0.0-beta4
- Microsoft.Identity.Client (MSAL.NET)
- Azure.ResourceManager.* (Azure SDK)
- Microsoft.Graph (Graph API)
- ModelContextProtocol (MCP support)

## Architecture Documentation

- **[docs/design.md](docs/design.md)** - Repository-level architecture, patterns, decisions
- **[src/Microsoft.Agents.A365.DevTools.Cli/design.md](src/Microsoft.Agents.A365.DevTools.Cli/design.md)** - CLI project architecture, configuration system
- **[src/Microsoft.Agents.A365.DevTools.MockToolingServer/design.md](src/Microsoft.Agents.A365.DevTools.MockToolingServer/design.md)** - Mock MCP server architecture
- **[src/DEVELOPER.md](src/DEVELOPER.md)** - How to develop, build, test, contribute

## Key Documentation

- [Agent 365 CLI reference](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/) - CLI usage guide with examples
- `.github/copilot-instructions.md` - Code standards and review rules
- `docs/commands/` - Index/pointers to CLI command documentation on Microsoft Learn

## Code Review Checklist

1. Check for "Kairo" keyword - flag for review if found
2. Verify Microsoft copyright header on all .cs files
3. Ensure SOLID principles are followed
4. Resource disposal for all IDisposable objects
5. Cross-platform compatibility
6. **Input validation:** Any CLI argument or external value used in a file path or file name is validated against an allowlist pattern before use
7. **Value safety:** String parameters that must be non-empty use `ArgumentException.ThrowIfNullOrWhiteSpace`, not just a non-nullable type
8. **Exit code completeness:** Every failure branch in a command handler (including `continue` inside loops) sets `context.ExitCode = 1`
9. **Data flow consistency:** Any transformation applied to a value (redaction, sanitization) is applied everywhere that value is written — including headers, summaries, and log lines, not just the primary content
10. **Command handler branch tests:** Every new `SetHandler` command handler must have invocation tests covering: invalid input → exit code 1, missing resource → exit code 1, bad output path → exit code 1, and successful path → exit code 0 with expected side effect (file written, message logged)
