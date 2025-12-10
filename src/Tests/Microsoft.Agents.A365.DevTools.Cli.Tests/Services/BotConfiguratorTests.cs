// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class BotConfiguratorTests
{
    private readonly ILogger<IBotConfigurator> _logger;
    private readonly CommandExecutor _executor;
    private readonly IBotConfigurator _configurator;
    private readonly IConfigService _configService;
    private readonly AuthenticationService _authService;

    public BotConfiguratorTests()
    {
        _logger = Substitute.For<ILogger<IBotConfigurator>>();
        _executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        _configService = Substitute.For<IConfigService>();
        _authService = Substitute.For<AuthenticationService>(Substitute.For<ILogger<AuthenticationService>>());
        _configurator = new BotConfigurator(_logger, _executor, _configService, _authService);
    }



    [Fact]
    public async Task CreateEndpointWithAgentBlueprintAsync_IdentityDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var botName = "test-bot";
        var location = "westus2";
        var messagingEndpoint = "https://test.azurewebsites.net/api/messages";
        var description = "Test Bot Description";
        var agentBlueprintId = "test-agent-blueprint-id";

        var subscriptionResult = new CommandResult { ExitCode = 1, StandardError = "Subscription not found" };

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("account show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(subscriptionResult));

        // Act
        var result = await _configurator.CreateEndpointWithAgentBlueprintAsync(
            botName, location, messagingEndpoint, description, agentBlueprintId);

        // Assert
        Assert.Equal(EndpointRegistrationResult.Failed, result);
    }

    [Fact(Skip = "Test requires interactive confirmation - bot creation commands now enforce user confirmation instead of --force")]
    public async Task CreateEndpointWithAgentBlueprintAsync_BotCreationSucceeds_ReturnsTrue()
    {
        // Arrange
        var botName = "test-bot";
        var location = "westus2";
        var messagingEndpoint = "https://test.azurewebsites.net/api/messages";
        var description = "Test Bot Description";
        var agentBlueprintId = "test-agent-blueprint-id";

        var subscriptionResult = new CommandResult 
        { 
            ExitCode = 0, 
            StandardOutput = """
                {
                  "tenantId": "test-tenant-id"
                }
                """ 
        };

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("account show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(subscriptionResult));

        // Act
        var result = await _configurator.CreateEndpointWithAgentBlueprintAsync(
            botName, location, messagingEndpoint, description, agentBlueprintId);

        // Assert
        Assert.Equal(EndpointRegistrationResult.Created, result);
    }
}
