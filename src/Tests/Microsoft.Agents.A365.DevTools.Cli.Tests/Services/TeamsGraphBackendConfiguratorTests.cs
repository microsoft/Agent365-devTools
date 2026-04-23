// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Guard-rail tests for <see cref="TeamsGraphBackendConfigurator"/>. The happy-path and HTTP
/// wire-level behavior is covered by the E2E suite (CLI → local MCP Platform → Teams Graph dev)
/// documented in docs/testing-results-phase3.5.md — mocking the internal HttpClient here would
/// bind tests to implementation details without adding coverage. These tests cover the cheap
/// pre-flight guards (missing tenant ID) that do not require an HTTP call.
/// </summary>
public class TeamsGraphBackendConfiguratorTests
{
    private readonly ILogger<ITeamsGraphBackendConfigurator> _logger;
    private readonly IConfigService _configService;
    private readonly AuthenticationService _authService;
    private readonly ITeamsGraphBackendConfigurator _configurator;

    public TeamsGraphBackendConfiguratorTests()
    {
        _logger = Substitute.For<ILogger<ITeamsGraphBackendConfigurator>>();
        _configService = Substitute.For<IConfigService>();
        _authService = Substitute.For<AuthenticationService>(Substitute.For<ILogger<AuthenticationService>>());
        _configurator = new TeamsGraphBackendConfigurator(_logger, _configService, _authService);
    }

    [Fact]
    public async Task SetBackendConfigurationAsync_MissingTenantId_ReturnsFailed()
    {
        // Arrange — config has no tenant ID.
        _configService.LoadAsync().Returns(Task.FromResult(new Cli.Models.Agent365Config()));

        // Act
        var (result, failureReason) = await _configurator.SetBackendConfigurationAsync(
            agentBlueprintId: "11111111-1111-1111-1111-111111111111",
            messagingEndpoint: "https://example.com/api/messages");

        // Assert
        Assert.Equal(EndpointRegistrationResult.Failed, result);
        Assert.Equal("Other", failureReason);
    }

    [Fact]
    public async Task ClearBackendConfigurationAsync_MissingTenantId_ReturnsFalse()
    {
        // Arrange — config has no tenant ID.
        _configService.LoadAsync().Returns(Task.FromResult(new Cli.Models.Agent365Config()));

        // Act
        var result = await _configurator.ClearBackendConfigurationAsync(
            agentBlueprintId: "11111111-1111-1111-1111-111111111111");

        // Assert
        Assert.False(result);
    }
}
