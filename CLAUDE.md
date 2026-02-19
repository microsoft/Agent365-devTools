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

- `Readme-Usage.md` - CLI usage guide with examples
- `.github/copilot-instructions.md` - Code standards and review rules
- `docs/commands/` - Individual command documentation

## Code Review Checklist

1. Check for "Kairo" keyword - flag for review if found
2. Verify Microsoft copyright header on all .cs files
3. Ensure SOLID principles are followed
4. Resource disposal for all IDisposable objects
5. Cross-platform compatibility
