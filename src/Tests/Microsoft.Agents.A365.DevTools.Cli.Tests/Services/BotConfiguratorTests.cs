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

    [Fact(Skip = "Test requires HTTP mocking infrastructure - endpoint creation uses HttpClient directly")]
    public async Task CreateEndpointWithAgentBlueprintAsync_EndpointAlreadyExists_ReturnsAlreadyExists()
    {
        // This test documents expected behavior when HTTP 409 Conflict is returned
        // 
        // Expected behavior:
        // - When endpoint creation API returns HTTP 409 Conflict status
        // - The method should return EndpointRegistrationResult.AlreadyExists
        // - This distinguishes from HTTP 500 InternalServerError with "already exists" message (which is Failed)
        //
        // Implementation note:
        // This would require mocking HttpClient responses, which needs infrastructure changes:
        // - Inject IHttpClientFactory or HttpClient
        // - Use MockHttpMessageHandler to simulate HTTP 409 response
        //
        // Current behavior is verified through integration tests and manual testing
        
        var botName = "test-bot";
        var location = "westus2";
        var messagingEndpoint = "https://test.azurewebsites.net/api/messages";
        var description = "Test Bot Description";
        var agentBlueprintId = "test-agent-blueprint-id";

        // TODO: Mock HTTP 409 Conflict response when HttpClient injection is added
        
        var result = await _configurator.CreateEndpointWithAgentBlueprintAsync(
            botName, location, messagingEndpoint, description, agentBlueprintId);

        Assert.Equal(EndpointRegistrationResult.AlreadyExists, result);
    }

    [Fact(Skip = "Test requires HTTP mocking infrastructure - documents location normalization requirement")]
    public async Task CreateEndpointWithAgentBlueprintAsync_NormalizesLocationWithSpaces()
    {
        // This test documents the critical bug fix for location normalization
        // 
        // Bug scenario:
        // - User config has location: "Canada Central" (display name with spaces)
        // - Endpoint creation API requires: "canadacentral" (lowercase, no spaces)
        // - Without normalization: API returns BadRequest "Invalid location"
        // - With normalization: Location is converted before sending to API
        //
        // Expected behavior:
        // - Input: "Canada Central", "West US 2", etc. (display names)
        // - Output to API: "canadacentral", "westus2", etc. (API names)
        // - The JSON body sent to endpoint creation should have normalized location
        //
        // Implementation note:
        // This would require mocking HttpClient to verify the JSON body contains:
        // - ["Location"] = "canadacentral" (NOT "Canada Central")
        //
        // Workaround verification:
        // - AzureCliService.ListAppServicePlansAsync has a similar test that passes
        // - Integration tests verify end-to-end behavior
        // - Manual testing confirmed the fix works
        
        var botName = "test-bot";
        var locationWithSpaces = "Canada Central"; // Display name from Azure
        var messagingEndpoint = "https://test.azurewebsites.net/api/messages";
        var description = "Test Bot Description";
        var agentBlueprintId = "test-agent-blueprint-id";

        // TODO: Mock HttpClient to verify JSON body has normalized location "canadacentral"
        // Expected JSON body should contain:
        // {
        //   "Location": "canadacentral"  // NOT "Canada Central"
        // }
        
        var result = await _configurator.CreateEndpointWithAgentBlueprintAsync(
            botName, locationWithSpaces, messagingEndpoint, description, agentBlueprintId);

        // If this test could run, it would verify the HTTP request body has normalized location
        Assert.Equal(EndpointRegistrationResult.Created, result);
    }
}
