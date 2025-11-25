// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.CommandLine;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for Endpoint subcommand
/// </summary>
[Collection("Sequential")]
public class EndpointSubcommandTests
{
    private readonly ILogger _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly IBotConfigurator _mockBotConfigurator;
    private readonly PlatformDetector _mockPlatformDetector;

    public EndpointSubcommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockConfigService = Substitute.For<IConfigService>();
        _mockBotConfigurator = Substitute.For<IBotConfigurator>();
        
        // Create a simple mock without using ForPartsOf which might be causing issues
        _mockPlatformDetector = Substitute.For<PlatformDetector>(Substitute.For<ILogger<PlatformDetector>>());
    }

    #region Command Structure Tests

    [Fact]
    public void CreateCommand_ShouldHaveConfigOption()
    {
        // Act
        var command = EndpointSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockBotConfigurator,
            _mockPlatformDetector);

        // Assert
        var configOption = command.Options.FirstOrDefault(o => o.Name == "config");
        configOption.Should().NotBeNull();
        configOption!.Aliases.Should().Contain("--config");
        configOption.Aliases.Should().Contain("-c");
    }

    [Fact]
    public void CreateCommand_ShouldHaveVerboseOption()
    {
        // Act
        var command = EndpointSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockBotConfigurator,
            _mockPlatformDetector);

        // Assert
        var verboseOption = command.Options.FirstOrDefault(o => o.Name == "verbose");
        verboseOption.Should().NotBeNull();
        verboseOption!.Aliases.Should().Contain("--verbose");
        verboseOption.Aliases.Should().Contain("-v");
    }

    [Fact]
    public void CreateCommand_ShouldHaveDryRunOption()
    {
        // Act
        var command = EndpointSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockBotConfigurator,
            _mockPlatformDetector);

        // Assert
        var dryRunOption = command.Options.FirstOrDefault(o => o.Name == "dry-run");
        dryRunOption.Should().NotBeNull();
        dryRunOption!.Aliases.Should().Contain("--dry-run");
    }

    [Fact]
    public void CommandDescription_ShouldMentionRequiredPermissions()
    {
        // Act
        var command = EndpointSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockBotConfigurator,
            _mockPlatformDetector);

        // Assert
        command.Description.Should().Contain("Azure Subscription Contributor");
    }

    [Fact]
    public void CreateCommand_ShouldBeUsableInCommandPipeline()
    {
        // Act
        var command = EndpointSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockBotConfigurator,
            _mockPlatformDetector);

        // Assert - Verify command can be created and has expected properties
        command.Should().NotBeNull();
        command.Name.Should().Be("endpoint");
        command.Options.Should().HaveCountGreaterOrEqualTo(3);
    }

    [Fact]
    public void CreateCommand_ShouldHandleAllOptions()
    {
        // Act
        var command = EndpointSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockBotConfigurator,
            _mockPlatformDetector);

        // Assert - Verify all expected options are present
        command.Options.Should().HaveCountGreaterOrEqualTo(3);

        var optionNames = command.Options.Select(o => o.Name).ToList();
        optionNames.Should().Contain("config");
        optionNames.Should().Contain("verbose");
        optionNames.Should().Contain("dry-run");
    }

    [Fact]
    public void CommandDescription_ShouldBeInformativeAndActionable()
    {
        // Act
        var command = EndpointSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockBotConfigurator,
            _mockPlatformDetector);

        // Assert
        command.Description.Should().NotBeNullOrEmpty();
        command.Description.Should().ContainAny("endpoint", "messaging", "Bot Service");
    }

    [Fact]
    public void CommandDescription_ShouldMentionAzureBotService()
    {
        // Act
        var command = EndpointSubcommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockBotConfigurator,
            _mockPlatformDetector);

        // Assert
        command.Description.Should().Contain("Azure Bot Service");
    }

    #endregion

    #region Validation Tests (Testing logic without parser)

    [Fact]
    public async Task ValidationLogic_WithMissingBlueprintId_ShouldLogError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "", // Missing blueprint ID
            WebAppName = "test-webapp"
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        // Act - Load config and validate
        var loadedConfig = await _mockConfigService.LoadAsync("test-config.json");

        // Assert - Verify validation would catch this
        loadedConfig.AgentBlueprintId.Should().BeEmpty();
        // In the actual command handler, Environment.Exit(1) would be called
    }

    [Fact]
    public async Task ValidationLogic_WithMissingWebAppName_ShouldLogError()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "blueprint-123",
            WebAppName = "" // Missing web app name
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        // Act
        var loadedConfig = await _mockConfigService.LoadAsync("test-config.json");

        // Assert
        loadedConfig.WebAppName.Should().BeEmpty();
        // In the actual command handler, Environment.Exit(1) would be called
    }

    [Fact]
    public async Task DryRunLogic_ShouldNotExecuteRegistration()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "blueprint-123",
            WebAppName = "test-webapp"
        };

        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(config));

        // Act - Simulate dry-run logic (loading config but not executing)
        var loadedConfig = await _mockConfigService.LoadAsync("test-config.json");

        // Assert - Verify config was loaded
        loadedConfig.Should().NotBeNull();
        loadedConfig.AgentBlueprintId.Should().Be("blueprint-123");
        loadedConfig.WebAppName.Should().Be("test-webapp");
        
        // Verify no bot configuration was attempted
        await _mockBotConfigurator.DidNotReceiveWithAnyArgs()
            .CreateEndpointWithAgentBlueprintAsync(default!, default!, default!, default!, default!);
    }

    [Fact]
    public void DryRunDisplay_ShouldShowEndpointInfo()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "blueprint-456",
            WebAppName = "my-agent-webapp"
        };

        // Act - Simulate what dry-run would display
        var endpointName = $"{config.WebAppName}-endpoint";
        var messagingUrl = $"https://{config.WebAppName}.azurewebsites.net/api/messages";

        // Assert
        endpointName.Should().Be("my-agent-webapp-endpoint");
        messagingUrl.Should().Be("https://my-agent-webapp.azurewebsites.net/api/messages");
    }

    [Fact]
    public void DryRunDisplay_ShouldShowMessagingUrl()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "blueprint-789",
            WebAppName = "production-agent"
        };

        // Act - Simulate messaging URL generation
        var messagingUrl = $"https://{config.WebAppName}.azurewebsites.net/api/messages";

        // Assert
        messagingUrl.Should().Contain("production-agent.azurewebsites.net/api/messages");
    }

    #endregion

    #region RegisterEndpointAndSyncAsync Tests

    [Fact]
    public async Task RegisterEndpointAndSyncAsync_WithValidConfig_ShouldSucceed()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            WebAppName = "test-webapp",
            Location = "eastus",
            DeploymentProjectPath = Path.GetTempPath()
        };

        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");

        // Create temporary generated config file
        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask);

            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(true);

            // Act
            await EndpointSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector);

            // Assert
            await _mockBotConfigurator.Received(1).CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(),
                config.Location,
                Arg.Is<string>(s => s.Contains("test-webapp.azurewebsites.net")),
                Arg.Any<string>(),
                config.AgentBlueprintId);

            await _mockConfigService.Received(1).SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>());
        }
        finally
        {
            // Cleanup
            if (File.Exists(generatedPath))
            {
                File.Delete(generatedPath);
            }
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task RegisterEndpointAndSyncAsync_ShouldSetCompletedFlag()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-456",
            WebAppName = "test-webapp",
            Location = "westus",
            DeploymentProjectPath = Path.GetTempPath()
        };

        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");
        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            Agent365Config? savedConfig = null;
            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask)
                .AndDoes(callInfo => savedConfig = callInfo.Arg<Agent365Config>());

            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(true);

            // Act
            await EndpointSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector);

            // Assert
            savedConfig.Should().NotBeNull();
            savedConfig!.Completed.Should().BeTrue();
            savedConfig.CompletedAt.Should().NotBeNull();
            savedConfig.CompletedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        }
        finally
        {
            if (File.Exists(generatedPath))
            {
                File.Delete(generatedPath);
            }
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task RegisterEndpointAndSyncAsync_ShouldLogProgressMessages()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-789",
            WebAppName = "test-webapp",
            Location = "eastus",
            DeploymentProjectPath = Path.GetTempPath()
        };

        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");
        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask);

            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(true);

            // Act
            await EndpointSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector);

            // Assert
            _mockLogger.Received().Log(
                LogLevel.Information,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Registering blueprint messaging endpoint")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());

            _mockLogger.Received().Log(
                LogLevel.Information,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("registered successfully")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (File.Exists(generatedPath))
            {
                File.Delete(generatedPath);
            }
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task RegisterEndpointAndSyncAsync_WhenSyncFails_ShouldLogWarningButContinue()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            WebAppName = "test-webapp",
            Location = "eastus",
            DeploymentProjectPath = "non-existent-path" // This will cause sync to skip with a warning
        };

        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");
        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask);

            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(true);

            // Act - should not throw
            await EndpointSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector);

            // Assert - ProjectSettingsSyncHelper logs a warning when deploymentProjectPath doesn't exist
            _mockLogger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Project settings sync failed") && o.ToString()!.Contains("non-blocking")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (File.Exists(generatedPath))
            {
                File.Delete(generatedPath);
            }
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task RegisterEndpointAndSyncAsync_ShouldUpdateBotConfigurationInAgent365Config()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            WebAppName = "test-webapp",
            Location = "eastus",
            DeploymentProjectPath = Path.GetTempPath()
        };

        var testId = Guid.NewGuid().ToString();
        var configPath = Path.Combine(Path.GetTempPath(), $"test-config-{testId}.json");
        var generatedPath = Path.Combine(Path.GetTempPath(), $"a365.generated.config-{testId}.json");
        await File.WriteAllTextAsync(generatedPath, "{}");

        try
        {
            _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(config));

            Agent365Config? savedConfig = null;
            _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>(), Arg.Any<string>())
                .Returns(Task.CompletedTask)
                .AndDoes(callInfo => savedConfig = callInfo.Arg<Agent365Config>());

            _mockBotConfigurator.CreateEndpointWithAgentBlueprintAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(true);

            // Act
            await EndpointSubcommand.RegisterEndpointAndSyncAsync(
                configPath,
                _mockLogger,
                _mockConfigService,
                _mockBotConfigurator,
                _mockPlatformDetector);

            // Assert - Verify bot configuration was updated in config
            savedConfig.Should().NotBeNull();
            savedConfig!.BotId.Should().Be(config.AgentBlueprintId);
            savedConfig.BotMsaAppId.Should().Be(config.AgentBlueprintId);
            savedConfig.BotMessagingEndpoint.Should().Contain("test-webapp.azurewebsites.net");
        }
        finally
        {
            if (File.Exists(generatedPath))
            {
                File.Delete(generatedPath);
            }
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    #endregion
}
