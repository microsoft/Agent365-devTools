// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Moq;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for MOS prerequisites in PublishHelpers
/// </summary>
public class PublishHelpersTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly Mock<GraphApiService> _mockGraphService;
    private readonly Agent365Config _testConfig;

    public PublishHelpersTests()
    {
        _mockLogger = new Mock<ILogger>();
        
        // Create GraphApiService with all mocked dependencies to prevent real API calls
        // This matches the pattern used in GraphApiServiceTests
        var mockGraphLogger = new Mock<ILogger<GraphApiService>>();
        var mockExecutor = new Mock<CommandExecutor>(MockBehavior.Loose, NullLogger<CommandExecutor>.Instance);
        var mockTokenProvider = new Mock<IMicrosoftGraphTokenProvider>();
        
        // Create mock using constructor with all dependencies to prevent real HTTP/Auth calls
        _mockGraphService = new Mock<GraphApiService>(
            mockGraphLogger.Object, 
            mockExecutor.Object, 
            It.IsAny<HttpMessageHandler>(), 
            mockTokenProvider.Object) 
        { 
            CallBase = false 
        };
        
        _testConfig = new Agent365Config
        {
            TenantId = "test-tenant-id",
            ClientAppId = "test-client-app-id"
        };
    }

    [Fact]
    public async Task EnsureMosPrerequisitesAsync_WhenClientAppIdMissing_ThrowsSetupValidationException()
    {
        // Arrange
        var config = new Agent365Config { ClientAppId = "" };

        // Act
        Func<Task> act = async () => await PublishHelpers.EnsureMosPrerequisitesAsync(
            _mockGraphService.Object, config, _mockLogger.Object);

        // Assert
        await act.Should().ThrowAsync<SetupValidationException>()
            .WithMessage("*Custom client app ID is required*");
    }

    [Fact]
    public async Task EnsureMosPrerequisitesAsync_WhenCustomAppNotFound_ThrowsSetupValidationException()
    {
        // Arrange
        var emptyAppsResponse = JsonDocument.Parse("{\"value\": []}");
        _mockGraphService.Setup(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains($"appId eq '{_testConfig.ClientAppId}'")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(emptyAppsResponse);

        // Act
        Func<Task> act = async () => await PublishHelpers.EnsureMosPrerequisitesAsync(
            _mockGraphService.Object, _testConfig, _mockLogger.Object);

        // Assert
        await act.Should().ThrowAsync<SetupValidationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task EnsureMosPrerequisitesAsync_WhenPermissionsAlreadyExist_ReturnsTrue()
    {
        // Arrange
        var appWithMosPermissions = JsonDocument.Parse(@"{
            ""value"": [{
                ""id"": ""app-object-id"",
                ""requiredResourceAccess"": [{
                    ""resourceAppId"": ""6ec511af-06dc-4fe2-b493-63a37bc397b1"",
                    ""resourceAccess"": []
                }]
            }]
        }");
        
        _mockGraphService.Setup(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains($"appId eq '{_testConfig.ClientAppId}'")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(appWithMosPermissions);

        _mockGraphService.Setup(x => x.CheckServicePrincipalCreationPrivilegesAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, new List<string> { "Application Administrator" }));

        _mockGraphService.Setup(x => x.EnsureServicePrincipalForAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sp-object-id");

        _mockGraphService.Setup(x => x.GraphPatchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), default, null))
            .ReturnsAsync(true);

        _mockGraphService.Setup(x => x.LookupServicePrincipalByAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("client-sp-object-id");

        _mockGraphService.Setup(x => x.ReplaceOauth2PermissionGrantAsync(
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<IEnumerable<string>>(), 
            default))
            .ReturnsAsync(true);

        // Act
        var result = await PublishHelpers.EnsureMosPrerequisitesAsync(
            _mockGraphService.Object, _testConfig, _mockLogger.Object);

        // Assert
        result.Should().BeTrue();
        
        // Service principals are always created for MOS resource apps (4 apps total)
        _mockGraphService.Verify(x => x.EnsureServicePrincipalForAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), 
            Times.Exactly(1 + MosConstants.AllResourceAppIds.Length));
    }

    [Fact]
    public async Task EnsureMosPrerequisitesAsync_WhenPermissionsMissing_CreatesServicePrincipals()
    {
        // Arrange
        var appWithoutMosPermissions = JsonDocument.Parse(@"{
            ""value"": [{
                ""id"": ""app-object-id"",
                ""requiredResourceAccess"": []
            }]
        }");
        
        _mockGraphService.Setup(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains($"appId eq '{_testConfig.ClientAppId}'")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(appWithoutMosPermissions);

        _mockGraphService.Setup(x => x.CheckServicePrincipalCreationPrivilegesAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, new List<string> { "Application Administrator" }));

        _mockGraphService.Setup(x => x.EnsureServicePrincipalForAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sp-object-id");

        _mockGraphService.Setup(x => x.GraphPatchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), default, null))
            .ReturnsAsync(true);

        _mockGraphService.Setup(x => x.LookupServicePrincipalByAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("client-sp-object-id");

        _mockGraphService.Setup(x => x.ReplaceOauth2PermissionGrantAsync(
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<IEnumerable<string>>(), 
            default))
            .ReturnsAsync(true);

        // Act
        var result = await PublishHelpers.EnsureMosPrerequisitesAsync(
            _mockGraphService.Object, _testConfig, _mockLogger.Object);

        // Assert
        result.Should().BeTrue();
        
        // Should create service principals for first-party client app + MOS resource apps
        var expectedServicePrincipalCalls = 1 + MosConstants.AllResourceAppIds.Length;
        _mockGraphService.Verify(x => x.EnsureServicePrincipalForAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), 
            Times.Exactly(expectedServicePrincipalCalls));
    }

    [Fact]
    public async Task EnsureMosPrerequisitesAsync_WhenServicePrincipalCreationFails_ThrowsSetupValidationException()
    {
        // Arrange
        var appWithoutMosPermissions = JsonDocument.Parse(@"{
            ""value"": [{
                ""id"": ""app-object-id"",
                ""requiredResourceAccess"": []
            }]
        }");
        
        _mockGraphService.Setup(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains($"appId eq '{_testConfig.ClientAppId}'")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(appWithoutMosPermissions);

        _mockGraphService.Setup(x => x.CheckServicePrincipalCreationPrivilegesAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, new List<string> { "Application Administrator" }));

        _mockGraphService.Setup(x => x.EnsureServicePrincipalForAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Failed to create service principal"));

        // Act
        Func<Task> act = async () => await PublishHelpers.EnsureMosPrerequisitesAsync(
            _mockGraphService.Object, _testConfig, _mockLogger.Object);

        // Assert
        await act.Should().ThrowAsync<SetupValidationException>()
            .WithMessage("*Failed to create service principal*");
    }

    [Fact]
    public async Task EnsureMosPrerequisitesAsync_WhenInsufficientPrivileges_ThrowsWithAzCliGuidance()
    {
        // Arrange
        var appWithoutMosPermissions = JsonDocument.Parse(@"{
            ""value"": [{
                ""id"": ""app-object-id"",
                ""requiredResourceAccess"": []
            }]
        }");
        
        _mockGraphService.Setup(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains($"appId eq '{_testConfig.ClientAppId}'")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(appWithoutMosPermissions);

        _mockGraphService.Setup(x => x.CheckServicePrincipalCreationPrivilegesAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, new List<string>()));

        _mockGraphService.Setup(x => x.EnsureServicePrincipalForAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("403 Forbidden"));

        // Act
        Func<Task> act = async () => await PublishHelpers.EnsureMosPrerequisitesAsync(
            _mockGraphService.Object, _testConfig, _mockLogger.Object);

        // Assert
        await act.Should().ThrowAsync<SetupValidationException>()
            .WithMessage("*Insufficient privileges*");
    }

    [Fact]
    public async Task EnsureMosPrerequisitesAsync_WhenCalledTwice_IsIdempotent()
    {
        // Arrange
        var appWithMosPermissions = JsonDocument.Parse(@"{
            ""value"": [{
                ""id"": ""app-object-id"",
                ""requiredResourceAccess"": [{
                    ""resourceAppId"": ""6ec511af-06dc-4fe2-b493-63a37bc397b1"",
                    ""resourceAccess"": []
                }]
            }]
        }");
        
        _mockGraphService.Setup(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains($"appId eq '{_testConfig.ClientAppId}'")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(appWithMosPermissions);

        _mockGraphService.Setup(x => x.CheckServicePrincipalCreationPrivilegesAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, new List<string> { "Application Administrator" }));

        _mockGraphService.Setup(x => x.EnsureServicePrincipalForAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sp-object-id");

        _mockGraphService.Setup(x => x.GraphPatchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), default, null))
            .ReturnsAsync(true);

        _mockGraphService.Setup(x => x.LookupServicePrincipalByAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("client-sp-object-id");

        _mockGraphService.Setup(x => x.ReplaceOauth2PermissionGrantAsync(
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<IEnumerable<string>>(), 
            default))
            .ReturnsAsync(true);

        // Act
        var result1 = await PublishHelpers.EnsureMosPrerequisitesAsync(
            _mockGraphService.Object, _testConfig, _mockLogger.Object);
        var result2 = await PublishHelpers.EnsureMosPrerequisitesAsync(
            _mockGraphService.Object, _testConfig, _mockLogger.Object);

        // Assert
        result1.Should().BeTrue();
        result2.Should().BeTrue();
        
        // Should query the app once per call
        _mockGraphService.Verify(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains($"appId eq '{_testConfig.ClientAppId}'")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()), Times.Exactly(2));
        
        // Service principals are created on each call (twice: 2 * (1 + 3 MOS apps) = 8 total)
        _mockGraphService.Verify(x => x.EnsureServicePrincipalForAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), 
            Times.Exactly(2 * (1 + MosConstants.AllResourceAppIds.Length)));
    }
}
