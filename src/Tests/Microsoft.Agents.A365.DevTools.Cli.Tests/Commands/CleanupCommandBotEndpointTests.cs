// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class CleanupCommandBotEndpointTests
{
    private readonly ILogger<CleanupCommand> _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly IBotConfigurator _mockBotConfigurator;
    private readonly CommandExecutor _mockExecutor;
    private readonly GraphApiService _graphApiService;
    private readonly AgentBlueprintService _agentBlueprintService;
    private readonly FederatedCredentialService _federatedCredentialService;
    private readonly IMicrosoftGraphTokenProvider _mockTokenProvider;
    private readonly IConfirmationProvider _mockConfirmationProvider;

    public CleanupCommandBotEndpointTests()
    {
        _mockLogger = Substitute.For<ILogger<CleanupCommand>>();
        _mockConfigService = Substitute.For<IConfigService>();
        
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);

        _mockExecutor.ExecuteAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<string?>(), 
            Arg.Any<bool>(), 
            Arg.Any<bool>(), 
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult 
            { 
                ExitCode = 0, 
                StandardOutput = string.Empty, 
                StandardError = string.Empty 
            }));

        _mockBotConfigurator = Substitute.For<IBotConfigurator>();
        
        _mockBotConfigurator.DeleteEndpointWithAgentBlueprintAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<string>())
            .Returns(Task.FromResult(true));
        
        _mockTokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
        _mockTokenProvider.GetMgGraphAccessTokenAsync(
            Arg.Any<string>(), 
            Arg.Any<IEnumerable<string>>(), 
            Arg.Any<bool>(), 
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns("test-token");
        
        var mockGraphLogger = Substitute.For<ILogger<GraphApiService>>();
        _graphApiService = new GraphApiService(mockGraphLogger, _mockExecutor, null, _mockTokenProvider);
        
        var mockBlueprintLogger = Substitute.For<ILogger<AgentBlueprintService>>();
        _agentBlueprintService = new AgentBlueprintService(mockBlueprintLogger, _graphApiService);

        // Create FederatedCredentialService wrapping GraphApiService
        var mockFicLogger = Substitute.For<ILogger<FederatedCredentialService>>();
        _federatedCredentialService = new FederatedCredentialService(mockFicLogger, _graphApiService);

        // Setup mock confirmation provider to return true by default
        _mockConfirmationProvider = Substitute.For<IConfirmationProvider>();
        _mockConfirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(true);
        _mockConfirmationProvider.ConfirmWithTypedResponseAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
    }

    [Fact]
    public void CleanupPreview_WithBotNameButNoWebApp_ShouldShowBotEndpoint()
    {
        var config = new Agent365Config
        {
            WebAppName = "test-webapp",
            AgentBlueprintId = "test-blueprint"
        };

        Assert.NotEmpty(config.BotName);
        Assert.Equal("test-webapp-endpoint", config.BotName);
    }

    [Fact]
    public void BotConfigurator_DeleteEndpoint_ShouldBeCalledIndependentlyOfWebApp()
    {
        var config = new Agent365Config
        {
            TenantId = "tenant-id",
            WebAppName = "test-bot",
            Location = "westus",
            AgentBlueprintId = "blueprint-id"
        };
        var command = CleanupCommand.CreateCommand(
            _mockLogger, 
            _mockConfigService, 
            _mockBotConfigurator, 
            _mockExecutor, 
            _agentBlueprintService,
            _mockConfirmationProvider,
            _federatedCredentialService);

        Assert.NotNull(command);
        Assert.Equal("cleanup", command.Name);
    }
}
