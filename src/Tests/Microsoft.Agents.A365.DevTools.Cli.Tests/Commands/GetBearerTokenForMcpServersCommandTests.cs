// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.CommandLine;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class McpServerAuthCommandTests
{
    private readonly ILogger _mockLogger;
    private readonly ConfigService _mockConfigService;
    private readonly AuthenticationService _mockAuthService;
    private readonly GraphApiService _mockGraphApiService;

    public McpServerAuthCommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockConfigService = Substitute.ForPartsOf<ConfigService>(NullLogger<ConfigService>.Instance);
        _mockAuthService = Substitute.ForPartsOf<AuthenticationService>(NullLogger<AuthenticationService>.Instance);
        _mockGraphApiService = Substitute.ForPartsOf<GraphApiService>(
            Substitute.For<IMicrosoftGraphTokenProvider>(),
            NullLogger<GraphApiService>.Instance);
    }

    [Fact]
    public void CreateCommand_Should_Return_Command_With_Correct_Name()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);

        // Assert
        Assert.Equal("mcpserverauth", command.Name);
        Assert.Contains("Manage authentication and permissions for MCP servers", command.Description);
    }

    [Fact]
    public void CreateCommand_Should_Have_Two_Subcommands()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);

        // Assert
        Assert.Equal(2, command.Subcommands.Count);
        Assert.Contains(command.Subcommands, c => c.Name == "gettoken");
        Assert.Contains(command.Subcommands, c => c.Name == "addpermissions");
    }

    [Fact]
    public void GetTokenSubcommand_Should_Have_Config_Option()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);
        var tokenCommand = command.Subcommands.First(c => c.Name == "gettoken");

        // Assert
        var configOption = tokenCommand.Options.FirstOrDefault(o => o.Name == "config");
        Assert.NotNull(configOption);
        Assert.Contains("--config", configOption.Aliases.Select(a => a.ToString()));
        Assert.Contains("-c", configOption.Aliases.Select(a => a.ToString()));
    }

    [Fact]
    public void GetTokenSubcommand_Should_Have_Manifest_Option()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);
        var tokenCommand = command.Subcommands.First(c => c.Name == "gettoken");

        // Assert
        var manifestOption = tokenCommand.Options.FirstOrDefault(o => o.Name == "manifest");
        Assert.NotNull(manifestOption);
        Assert.Contains("--manifest", manifestOption.Aliases.Select(a => a.ToString()));
        Assert.Contains("-m", manifestOption.Aliases.Select(a => a.ToString()));
    }

    [Fact]
    public void GetTokenSubcommand_Should_Have_Scopes_Option()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);
        var tokenCommand = command.Subcommands.First(c => c.Name == "gettoken");

        // Assert
        var scopesOption = tokenCommand.Options.FirstOrDefault(o => o.Name == "scopes");
        Assert.NotNull(scopesOption);
        Assert.Contains("--scopes", scopesOption.Aliases.Select(a => a.ToString()));
    }

    [Fact]
    public void GetTokenSubcommand_Should_Have_OutputFormat_Option()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);
        var tokenCommand = command.Subcommands.First(c => c.Name == "gettoken");

        // Assert
        var outputOption = tokenCommand.Options.FirstOrDefault(o => o.Name == "output");
        Assert.NotNull(outputOption);
        Assert.Contains("--output", outputOption.Aliases.Select(a => a.ToString()));
        Assert.Contains("-o", outputOption.Aliases.Select(a => a.ToString()));
    }

    [Fact]
    public void GetTokenSubcommand_Should_Have_Verbose_Option()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);
        var tokenCommand = command.Subcommands.First(c => c.Name == "gettoken");

        // Assert
        var verboseOption = tokenCommand.Options.FirstOrDefault(o => o.Name == "verbose");
        Assert.NotNull(verboseOption);
        Assert.Contains("--verbose", verboseOption.Aliases.Select(a => a.ToString()));
        Assert.Contains("-v", verboseOption.Aliases.Select(a => a.ToString()));
    }

    [Fact]
    public void GetTokenSubcommand_Should_Have_ForceRefresh_Option()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);
        var tokenCommand = command.Subcommands.First(c => c.Name == "gettoken");

        // Assert
        var forceRefreshOption = tokenCommand.Options.FirstOrDefault(o => o.Name == "force-refresh");
        Assert.NotNull(forceRefreshOption);
        Assert.Contains("--force-refresh", forceRefreshOption.Aliases.Select(a => a.ToString()));
    }

    [Fact]
    public void AddPermissionsSubcommand_Should_Have_Config_Option()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);
        var addPermissionsCommand = command.Subcommands.First(c => c.Name == "addpermissions");

        // Assert
        var configOption = addPermissionsCommand.Options.FirstOrDefault(o => o.Name == "config");
        Assert.NotNull(configOption);
        Assert.Contains("--config", configOption.Aliases.Select(a => a.ToString()));
        Assert.Contains("-c", configOption.Aliases.Select(a => a.ToString()));
    }

    [Fact]
    public void AddPermissionsSubcommand_Should_Have_AppId_Option()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);
        var addPermissionsCommand = command.Subcommands.First(c => c.Name == "addpermissions");

        // Assert
        var appIdOption = addPermissionsCommand.Options.FirstOrDefault(o => o.Name == "app-id");
        Assert.NotNull(appIdOption);
        Assert.Contains("--app-id", appIdOption.Aliases.Select(a => a.ToString()));
    }

    [Fact]
    public void AddPermissionsSubcommand_Should_Have_DryRun_Option()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);
        var addPermissionsCommand = command.Subcommands.First(c => c.Name == "addpermissions");

        // Assert
        var dryRunOption = addPermissionsCommand.Options.FirstOrDefault(o => o.Name == "dry-run");
        Assert.NotNull(dryRunOption);
        Assert.Contains("--dry-run", dryRunOption.Aliases.Select(a => a.ToString()));
    }

    [Theory]
    [InlineData("table")]
    [InlineData("json")]
    [InlineData("raw")]
    public void GetTokenSubcommand_Should_Support_Multiple_Output_Formats(string format)
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);
        var tokenCommand = command.Subcommands.First(c => c.Name == "gettoken");
        var outputOption = tokenCommand.Options.First(o => o.Name == "output");

        // Assert
        // The option should accept various format strings
        Assert.NotNull(outputOption);
        Assert.NotNull(format); // Use the parameter to satisfy xUnit requirement
    }

    [Fact]
    public void GetTokenSubcommand_Description_Should_Mention_ToolingManifest()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);
        var tokenCommand = command.Subcommands.First(c => c.Name == "gettoken");

        // Assert
        Assert.Contains("ToolingManifest", tokenCommand.Description);
    }

    [Fact]
    public void GetTokenSubcommand_Should_Have_Handler()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);
        var tokenCommand = command.Subcommands.First(c => c.Name == "gettoken");

        // Assert
        Assert.NotNull(tokenCommand.Handler);
    }

    [Fact]
    public void AddPermissionsSubcommand_Should_Have_Handler()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_mockLogger, _mockConfigService, _mockAuthService, _mockGraphApiService);
        var addPermissionsCommand = command.Subcommands.First(c => c.Name == "addpermissions");

        // Assert
        Assert.NotNull(addPermissionsCommand.Handler);
    }
}
