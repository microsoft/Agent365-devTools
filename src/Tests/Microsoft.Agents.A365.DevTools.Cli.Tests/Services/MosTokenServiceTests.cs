// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using NSubstitute;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Unit tests for MosTokenService
/// </summary>
public class MosTokenServiceTests
{
    private readonly ILogger<MosTokenService> _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly MosTokenService _service;

    public MosTokenServiceTests()
    {
        _mockLogger = Substitute.For<ILogger<MosTokenService>>();
        _mockConfigService = Substitute.For<IConfigService>();
        _service = new MosTokenService(_mockLogger, _mockConfigService);
    }

    [Fact]
    public async Task AcquireTokenAsync_WhenPersonalTokenProvided_ReturnsPersonalToken()
    {
        // Arrange
        var personalToken = "test-personal-token";
        var environment = "prod";

        // Act
        var result = await _service.AcquireTokenAsync(environment, personalToken);

        // Assert
        result.Should().Be(personalToken);
    }

    [Fact]
    public async Task AcquireTokenAsync_WhenConfigNotFound_ThrowsException()
    {
        // Arrange
        var environment = "prod";
        _mockConfigService.LoadAsync().Returns(Task.FromException<Agent365Config>(new FileNotFoundException("Config not found")));

        // Act
        Func<Task> act = async () => await _service.AcquireTokenAsync(environment);

        // Assert
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task AcquireTokenAsync_WhenTenantIdMissing_ReturnsNull()
    {
        // Arrange
        var environment = "prod";
        Agent365Config? config = new Agent365Config { ClientAppId = "test-client-id", TenantId = "" };
        _mockConfigService.LoadAsync().Returns(Task.FromResult(config));

        // Act
        var result = await _service.AcquireTokenAsync(environment);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task AcquireTokenAsync_WhenUnsupportedEnvironment_ReturnsNull()
    {
        // Arrange
        var environment = "invalid-env";
        Agent365Config? config = new Agent365Config { ClientAppId = "test-client-id", TenantId = "test-tenant-id" };
        _mockConfigService.LoadAsync().Returns(Task.FromResult(config));

        // Act
        var result = await _service.AcquireTokenAsync(environment);

        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("sdf")]
    [InlineData("test")]
    [InlineData("gccm")]
    [InlineData("gcch")]
    [InlineData("dod")]
    public async Task AcquireTokenAsync_SupportedEnvironments_LoadsConfig(string environment)
    {
        // Arrange
        Agent365Config? config = new Agent365Config { ClientAppId = "test-client-id", TenantId = "test-tenant-id" };
        _mockConfigService.LoadAsync().Returns(Task.FromResult(config));
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately to avoid browser prompt

        // Act & Assert
        // Token acquisition will be cancelled before browser prompt
        var result = await _service.AcquireTokenAsync(environment, cancellationToken: cts.Token);
        
        // Verify config was loaded (means we got past environment validation)
        await _mockConfigService.Received(1).LoadAsync();
        
        // Result will be null due to cancellation
        result.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithValidParameters_CreatesInstance()
    {
        // Act
        var service = new MosTokenService(_mockLogger, _mockConfigService);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireTokenAsync_NormalizesEnvironmentToLowercase()
    {
        // Arrange
        Agent365Config? config = new Agent365Config { ClientAppId = "test-client-id", TenantId = "test-tenant-id" };
        _mockConfigService.LoadAsync().Returns(Task.FromResult(config));
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately to avoid browser prompt

        // Act
        var result = await _service.AcquireTokenAsync("PROD", cancellationToken: cts.Token);

        // Assert
        await _mockConfigService.Received(1).LoadAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task AcquireTokenAsync_TrimsWhitespaceFromEnvironment()
    {
        // Arrange
        Agent365Config? config = new Agent365Config { ClientAppId = "test-client-id", TenantId = "test-tenant-id" };
        _mockConfigService.LoadAsync().Returns(Task.FromResult(config));
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately to avoid browser prompt

        // Act
        var result = await _service.AcquireTokenAsync("  prod  ", cancellationToken: cts.Token);

        // Assert
        await _mockConfigService.Received(1).LoadAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task AcquireTokenAsync_WithPersonalToken_DoesNotLoadConfig()
    {
        // Arrange
        var personalToken = "test-token";

        // Act
        var result = await _service.AcquireTokenAsync("prod", personalToken);

        // Assert
        result.Should().Be(personalToken);
        await _mockConfigService.DidNotReceive().LoadAsync();
    }
}
