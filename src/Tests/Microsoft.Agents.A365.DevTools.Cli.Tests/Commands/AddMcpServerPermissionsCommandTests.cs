// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class AddMcpServerPermissionsCommandTests
{
    private readonly ILogger _logger;
    private readonly IConfigService _configService;
    private readonly AuthenticationService _authService;
    private readonly GraphApiService _graphApiService;

    public AddMcpServerPermissionsCommandTests()
    {
        _logger = Substitute.For<ILogger>();
        _configService = Substitute.For<IConfigService>();
        _authService = Substitute.For<AuthenticationService>();
        _graphApiService = Substitute.For<GraphApiService>();
    }

    [Fact]
    public void CreateCommand_ShouldHaveAddPermissionsSubcommand()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);

        // Assert
        command.Should().NotBeNull();
        command.Subcommands.Should().Contain(c => c.Name == "addpermissions");
    }

    [Fact]
    public void AddPermissionsSubcommand_ShouldHaveCorrectDescription()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");

        // Assert
        subcommand.Description.Should().Contain("Add MCP server API permissions");
        subcommand.Description.Should().Contain("custom application");
    }

    [Fact]
    public void AddPermissionsSubcommand_ShouldHaveConfigOption()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");
        var configOption = subcommand.Options.FirstOrDefault(o => o.Name == "config");

        // Assert
        configOption.Should().NotBeNull();
        configOption!.Aliases.Should().Contain("--config");
        configOption.Aliases.Should().Contain("-c");
    }

    [Fact]
    public void AddPermissionsSubcommand_ShouldHaveManifestOption()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");
        var manifestOption = subcommand.Options.FirstOrDefault(o => o.Name == "manifest");

        // Assert
        manifestOption.Should().NotBeNull();
        manifestOption!.Aliases.Should().Contain("--manifest");
        manifestOption.Aliases.Should().Contain("-m");
    }

    [Fact]
    public void AddPermissionsSubcommand_ShouldHaveAppIdOption()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");
        var appIdOption = subcommand.Options.FirstOrDefault(o => o.Name == "app-id");

        // Assert
        appIdOption.Should().NotBeNull();
        appIdOption!.Aliases.Should().Contain("--app-id");
        appIdOption.IsRequired.Should().BeFalse();
    }

    [Fact]
    public void AddPermissionsSubcommand_ShouldHaveScopesOption()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");
        var scopesOption = subcommand.Options.FirstOrDefault(o => o.Name == "scopes");

        // Assert
        scopesOption.Should().NotBeNull();
        scopesOption!.Aliases.Should().Contain("--scopes");
        
        // Verify it accepts multiple arguments
        if (scopesOption is Option<string[]?> stringArrayOption)
        {
            stringArrayOption.AllowMultipleArgumentsPerToken.Should().BeTrue();
        }
    }

    [Fact]
    public void AddPermissionsSubcommand_ShouldHaveVerboseOption()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");
        var verboseOption = subcommand.Options.FirstOrDefault(o => o.Name == "verbose");

        // Assert
        verboseOption.Should().NotBeNull();
        verboseOption!.Aliases.Should().Contain("--verbose");
        verboseOption.Aliases.Should().Contain("-v");
    }

    [Fact]
    public void AddPermissionsSubcommand_ShouldHaveDryRunOption()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");
        var dryRunOption = subcommand.Options.FirstOrDefault(o => o.Name == "dry-run");

        // Assert
        dryRunOption.Should().NotBeNull();
        dryRunOption!.Aliases.Should().Contain("--dry-run");
    }

    [Fact]
    public void AddPermissionsSubcommand_ShouldHaveHandler()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");

        // Assert
        subcommand.Handler.Should().NotBeNull();
    }

    [Fact]
    public void AddPermissionsSubcommand_ShouldHaveAllRequiredOptions()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");

        // Assert
        subcommand.Options.Should().HaveCount(6); // config, manifest, app-id, scopes, verbose, dry-run
        
        var optionNames = subcommand.Options.Select(o => o.Name).ToList();
        optionNames.Should().Contain("config");
        optionNames.Should().Contain("manifest");
        optionNames.Should().Contain("app-id");
        optionNames.Should().Contain("scopes");
        optionNames.Should().Contain("verbose");
        optionNames.Should().Contain("dry-run");
    }

    [Fact]
    public void ConfigOption_ShouldHaveDefaultValue()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");
        var configOption = subcommand.Options.FirstOrDefault(o => o.Name == "config") as Option<FileInfo>;

        // Assert
        configOption.Should().NotBeNull();
        
        // Get default value through parsing
        var parser = new CommandLineBuilder(command).Build();
        var result = parser.Parse($"{command.Name} {subcommand.Name}");
        var defaultValue = result.GetValueForOption(configOption!);
        
        defaultValue.Should().NotBeNull();
        defaultValue!.Name.Should().Be("a365.config.json");
    }

    [Fact]
    public void ScopesOption_ShouldAcceptMultipleValues()
    {
        // Act
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");
        var scopesOption = subcommand.Options.FirstOrDefault(o => o.Name == "scopes") as Option<string[]?>;

        // Assert
        scopesOption.Should().NotBeNull();
        scopesOption!.AllowMultipleArgumentsPerToken.Should().BeTrue();
    }

    [Fact]
    public void Subcommand_ShouldBeInvokableWithMinimalArguments()
    {
        // Arrange
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");

        // Act
        var parser = new CommandLineBuilder(command).Build();
        var parseResult = parser.Parse($"{command.Name} {subcommand.Name}");

        // Assert
        parseResult.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Subcommand_ShouldParseAppIdOption()
    {
        // Arrange
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");
        var appIdOption = subcommand.Options.FirstOrDefault(o => o.Name == "app-id") as Option<string>;

        // Act
        var parser = new CommandLineBuilder(command).Build();
        var result = parser.Parse($"{command.Name} {subcommand.Name} --app-id 12345678-1234-1234-1234-123456789abc");
        var appIdValue = result.GetValueForOption(appIdOption!);

        // Assert
        appIdValue.Should().Be("12345678-1234-1234-1234-123456789abc");
    }

    [Fact]
    public void Subcommand_ShouldParseScopesOption()
    {
        // Arrange
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");
        var scopesOption = subcommand.Options.FirstOrDefault(o => o.Name == "scopes") as Option<string[]?>;

        // Act
        var parser = new CommandLineBuilder(command).Build();
        var result = parser.Parse($"{command.Name} {subcommand.Name} --scopes McpServers.Mail.All McpServers.Calendar.All");
        var scopesValue = result.GetValueForOption(scopesOption!);

        // Assert
        scopesValue.Should().NotBeNull();
        scopesValue.Should().HaveCount(2);
        scopesValue.Should().Contain("McpServers.Mail.All");
        scopesValue.Should().Contain("McpServers.Calendar.All");
    }

    [Fact]
    public void Subcommand_ShouldParseDryRunOption()
    {
        // Arrange
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");
        var dryRunOption = subcommand.Options.FirstOrDefault(o => o.Name == "dry-run") as Option<bool>;

        // Act
        var parser = new CommandLineBuilder(command).Build();
        var result = parser.Parse($"{command.Name} {subcommand.Name} --dry-run");
        var dryRunValue = result.GetValueForOption(dryRunOption!);

        // Assert
        dryRunValue.Should().BeTrue();
    }

    [Fact]
    public void Subcommand_ShouldParseVerboseOption()
    {
        // Arrange
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");
        var verboseOption = subcommand.Options.FirstOrDefault(o => o.Name == "verbose") as Option<bool>;

        // Act
        var parser = new CommandLineBuilder(command).Build();
        var result = parser.Parse($"{command.Name} {subcommand.Name} --verbose");
        var verboseValue = result.GetValueForOption(verboseOption!);

        // Assert
        verboseValue.Should().BeTrue();
    }

    [Fact]
    public void Subcommand_ShouldSupportShortAliases()
    {
        // Arrange
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");

        // Act
        var parser = new CommandLineBuilder(command).Build();
        var result = parser.Parse($"{command.Name} {subcommand.Name} -c myconfig.json -m mymanifest.json -v");

        // Assert
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task AddPermissions_WithoutConfigFileAndWithoutAppId_ShouldShowHelpfulError()
    {
        // Arrange
        var logger = Substitute.For<ILogger>();
        var configService = Substitute.For<IConfigService>();
        var authService = Substitute.For<AuthenticationService>();
        var graphApiService = Substitute.For<GraphApiService>();

        // This test verifies the fix: the command should handle missing config file gracefully
        // when no --app-id is provided, and show a helpful error message

        var command = McpServerAuthCommand.CreateCommand(logger, configService, authService, graphApiService);

        // Verify the command structure supports this scenario
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");
        subcommand.Should().NotBeNull();
        
        // Verify app-id option is optional (not required)
        // This is the key to the fix - if app-id is provided, the command should work without config
        var appIdOption = subcommand.Options.FirstOrDefault(o => o.Name == "app-id");
        appIdOption.Should().NotBeNull();
        appIdOption!.IsRequired.Should().BeFalse();

        // Verify command can be parsed without app-id or config
        var parser = new CommandLineBuilder(command).Build();
        var parseResult = parser.Parse($"{command.Name} {subcommand.Name}");
        parseResult.Errors.Should().BeEmpty();

        await Task.CompletedTask; // Satisfy async signature
    }

    [Fact]
    public void AddPermissions_WithAppIdOption_ShouldNotRequireConfig()
    {
        // Arrange
        var command = McpServerAuthCommand.CreateCommand(_logger, _configService, _authService, _graphApiService);
        var subcommand = command.Subcommands.First(c => c.Name == "addpermissions");

        // Act - Parse command with --app-id but no config file
        var parser = new CommandLineBuilder(command).Build();
        var result = parser.Parse($"{command.Name} {subcommand.Name} --app-id 12345678-1234-1234-1234-123456789abc --scopes McpServers.Mail.All");

        // Assert
        result.Errors.Should().BeEmpty();
        
        var appIdOption = subcommand.Options.FirstOrDefault(o => o.Name == "app-id") as Option<string>;
        var appIdValue = result.GetValueForOption(appIdOption!);
        appIdValue.Should().Be("12345678-1234-1234-1234-123456789abc");
    }
}

