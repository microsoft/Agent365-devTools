// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Requirements;

/// <summary>
/// Unit tests for ClientAppRequirementCheck
/// </summary>
public class ClientAppRequirementCheckTests
{
    private readonly ILogger _mockLogger;
    private readonly IClientAppValidator _mockValidator;

    public ClientAppRequirementCheckTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockValidator = Substitute.For<IClientAppValidator>();
    }

    [Fact]
    public async Task CheckAsync_WithMissingClientAppId_ShouldReturnFailure()
    {
        // Arrange
        var check = new ClientAppRequirementCheck(_mockValidator);
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id"
            // clientAppId is null
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse("clientAppId is required");
        result.ErrorMessage.Should().Contain("clientAppId is not configured");
        result.ResolutionGuidance.Should().Contain("a365 config");
        result.Details.Should().Contain("clientAppId must be specified");
    }

    [Fact]
    public async Task CheckAsync_WithMissingTenantId_ShouldReturnFailure()
    {
        // Arrange
        var check = new ClientAppRequirementCheck(_mockValidator);
        var config = new Agent365Config
        {
            ClientAppId = "test-client-app-id"
            // tenantId is null
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse("tenantId is required");
        result.ErrorMessage.Should().Contain("tenantId is not configured");
        result.ResolutionGuidance.Should().Contain("a365 config");
        result.Details.Should().Contain("tenantId must be specified");
    }

    [Fact]
    public async Task CheckAsync_WithValidClientApp_ShouldReturnSuccess()
    {
        // Arrange
        var check = new ClientAppRequirementCheck(_mockValidator);
        var config = new Agent365Config
        {
            ClientAppId = "test-client-app-id",
            TenantId = "test-tenant-id"
        };

        _mockValidator.EnsureValidClientAppAsync(
            config.ClientAppId,
            config.TenantId,
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeTrue("client app is valid");
        result.Details.Should().Contain("properly configured");
        result.Details.Should().Contain(config.ClientAppId);
        result.ErrorMessage.Should().BeNullOrEmpty();
        result.ResolutionGuidance.Should().BeNullOrEmpty();

        // Verify validator was called
        await _mockValidator.Received(1).EnsureValidClientAppAsync(
            config.ClientAppId,
            config.TenantId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_WithClientAppValidationException_ShouldReturnFailure()
    {
        // Arrange
        var check = new ClientAppRequirementCheck(_mockValidator);
        var config = new Agent365Config
        {
            ClientAppId = "invalid-client-app-id",
            TenantId = "test-tenant-id"
        };

        var validationException = new ClientAppValidationException(
            "Client app not found",
            new List<string>
            {
                "Client app validation failed"
            },
            new List<string>
            {
                "Verify the client app ID is correct",
                "Run 'a365 setup blueprint' to create a new client app"
            });

        _mockValidator.EnsureValidClientAppAsync(
            config.ClientAppId,
            config.TenantId,
            Arg.Any<CancellationToken>())
            .Throws(validationException);

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse("validation failed");
        result.ErrorMessage.Should().Contain("Client app not found");
        result.ResolutionGuidance.Should().Contain("Verify the client app ID is correct");
        result.ResolutionGuidance.Should().Contain("Run 'a365 setup blueprint'");
        result.Details.Should().Contain("Client app validation failed");
        result.Details.Should().Contain(config.ClientAppId);
    }

    [Fact]
    public async Task CheckAsync_WithUnexpectedException_ShouldReturnFailure()
    {
        // Arrange
        var check = new ClientAppRequirementCheck(_mockValidator);
        var config = new Agent365Config
        {
            ClientAppId = "test-client-app-id",
            TenantId = "test-tenant-id"
        };

        var unexpectedException = new InvalidOperationException("Unexpected error");

        _mockValidator.EnsureValidClientAppAsync(
            config.ClientAppId,
            config.TenantId,
            Arg.Any<CancellationToken>())
            .Throws(unexpectedException);

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse("unexpected exception occurred");
        result.ErrorMessage.Should().Contain("Unexpected error validating client app");
        result.ErrorMessage.Should().Contain("Unexpected error");
        result.ResolutionGuidance.Should().Contain("Check the logs");
        result.ResolutionGuidance.Should().Contain("az login");
        result.Details.Should().Contain("InvalidOperationException");
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectName()
    {
        // Arrange
        var check = new ClientAppRequirementCheck(_mockValidator);

        // Act & Assert
        check.Name.Should().Be("Client App Configuration");
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectDescription()
    {
        // Arrange
        var check = new ClientAppRequirementCheck(_mockValidator);

        // Act & Assert
        check.Description.Should().Contain("custom client app");
        check.Description.Should().Contain("Microsoft Graph permissions");
        check.Description.Should().Contain("admin consent");
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectCategory()
    {
        // Arrange
        var check = new ClientAppRequirementCheck(_mockValidator);

        // Act & Assert
        check.Category.Should().Be("Authentication");
    }

    [Fact]
    public void Constructor_WithNullValidator_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        var act = () => new ClientAppRequirementCheck(null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("clientAppValidator");
    }

    [Fact]
    public async Task CheckAsync_WithEmptyStringClientAppId_ShouldReturnFailure()
    {
        // Arrange
        var check = new ClientAppRequirementCheck(_mockValidator);
        var config = new Agent365Config
        {
            ClientAppId = "   ", // whitespace
            TenantId = "test-tenant-id"
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse("clientAppId is whitespace");
        result.ErrorMessage.Should().Contain("clientAppId is not configured");
    }

    [Fact]
    public async Task CheckAsync_WithEmptyStringTenantId_ShouldReturnFailure()
    {
        // Arrange
        var check = new ClientAppRequirementCheck(_mockValidator);
        var config = new Agent365Config
        {
            ClientAppId = "test-client-app-id",
            TenantId = "   " // whitespace
        };

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse("tenantId is whitespace");
        result.ErrorMessage.Should().Contain("tenantId is not configured");
    }

    [Fact]
    public async Task CheckAsync_ShouldPassCancellationTokenToValidator()
    {
        // Arrange
        var check = new ClientAppRequirementCheck(_mockValidator);
        var config = new Agent365Config
        {
            ClientAppId = "test-client-app-id",
            TenantId = "test-tenant-id"
        };
        var cancellationToken = new CancellationToken();

        _mockValidator.EnsureValidClientAppAsync(
            config.ClientAppId,
            config.TenantId,
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Act
        await check.CheckAsync(config, _mockLogger, cancellationToken);

        // Assert
        await _mockValidator.Received(1).EnsureValidClientAppAsync(
            config.ClientAppId,
            config.TenantId,
            cancellationToken);
    }
}
