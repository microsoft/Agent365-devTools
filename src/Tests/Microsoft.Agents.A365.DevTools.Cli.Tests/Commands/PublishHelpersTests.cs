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
    private readonly Mock<AgentBlueprintService> _mockBlueprintService;
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
        
        // Create AgentBlueprintService mock
        var mockBlueprintLogger = new Mock<ILogger<AgentBlueprintService>>();
        _mockBlueprintService = new Mock<AgentBlueprintService>(
            mockBlueprintLogger.Object,
            _mockGraphService.Object)
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
            _mockGraphService.Object, _mockBlueprintService.Object, config, _mockLogger.Object);

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
            _mockGraphService.Object, _mockBlueprintService.Object, _testConfig, _mockLogger.Object);

        // Assert
        await act.Should().ThrowAsync<SetupValidationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task EnsureMosPrerequisitesAsync_WhenPermissionsAlreadyExist_ReturnsTrue()
    {
        // Arrange - app with ALL MOS permissions correctly configured
        var appWithMosPermissions = JsonDocument.Parse($@"{{
            ""value"": [{{
                ""id"": ""app-object-id"",
                ""requiredResourceAccess"": [
                    {{
                        ""resourceAppId"": ""{MosConstants.TpsAppServicesResourceAppId}"",
                        ""resourceAccess"": [{{ ""id"": ""{MosConstants.ResourcePermissions.TpsAppServices.ScopeId}"", ""type"": ""Scope"" }}]
                    }},
                    {{
                        ""resourceAppId"": ""{MosConstants.PowerPlatformApiResourceAppId}"",
                        ""resourceAccess"": [{{ ""id"": ""{MosConstants.ResourcePermissions.PowerPlatformApi.ScopeId}"", ""type"": ""Scope"" }}]
                    }},
                    {{
                        ""resourceAppId"": ""{MosConstants.MosTitlesApiResourceAppId}"",
                        ""resourceAccess"": [{{ ""id"": ""{MosConstants.ResourcePermissions.MosTitlesApi.ScopeId}"", ""type"": ""Scope"" }}]
                    }}
                ]
            }}]
        }}");
        
        var tpsConsentGrantDoc = JsonDocument.Parse(@"{
            ""value"": [{
                ""scope"": ""AuthConfig.Read""
            }]
        }");
        
        var powerPlatformConsentGrantDoc = JsonDocument.Parse(@"{
            ""value"": [{
                ""scope"": ""EnvironmentManagement.Environments.Read""
            }]
        }");
        
        var mosTitlesConsentGrantDoc = JsonDocument.Parse(@"{
            ""value"": [{
                ""scope"": ""Title.ReadWrite.All""
            }]
        }");
        
        _mockGraphService.Setup(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains($"appId eq '{_testConfig.ClientAppId}'")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(appWithMosPermissions);

        // Mock consent grants - since all SP lookups return "sp-object-id", 
        // the consent grant query filter will always contain "sp-object-id" for both client and resource
        // We need to mock based on the actual filter pattern used in CheckMosPrerequisitesAsync
        _mockGraphService.Setup(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains("oauth2PermissionGrants")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync((string tenant, string path, CancellationToken ct, IEnumerable<string>? headers) =>
            {
                // Return appropriate consent based on which check is being done
                // Since we can't differentiate between resources (all return same SP ID),
                // return a combined consent that satisfies all checks
                return JsonDocument.Parse(@"{
                    ""value"": [{
                        ""scope"": ""AuthConfig.Read EnvironmentManagement.Environments.Read Title.ReadWrite.All""
                    }]
                }");
            });

        _mockGraphService.Setup(x => x.LookupServicePrincipalByAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync("sp-object-id");

        _mockGraphService.Setup(x => x.GraphPatchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(true);

        // Act
        var result = await PublishHelpers.EnsureMosPrerequisitesAsync(
            _mockGraphService.Object, _mockBlueprintService.Object, _testConfig, _mockLogger.Object);

        // Assert
        result.Should().BeTrue();
        
        // When all prerequisites exist, EnsureServicePrincipalForAppIdAsync should NOT be called
        _mockGraphService.Verify(x => x.EnsureServicePrincipalForAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()), 
            Times.Never());
        
        // Should verify all service principals exist via lookup
        _mockGraphService.Verify(x => x.LookupServicePrincipalByAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()), 
            Times.AtLeast(1 + MosConstants.AllResourceAppIds.Length));
    }

    [Fact]
    public async Task EnsureMosPrerequisitesAsync_WhenPermissionsMissing_CreatesServicePrincipals()
    {
        // Arrange - app with NO MOS permissions
        var appWithoutMosPermissions = JsonDocument.Parse(@"{
            ""value"": [{
                ""id"": ""app-object-id"",
                ""requiredResourceAccess"": []
            }]
        }");
        
        var emptyConsentDoc = JsonDocument.Parse(@"{ ""value"": [] }");
        
        _mockGraphService.Setup(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains($"appId eq '{_testConfig.ClientAppId}'")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(appWithoutMosPermissions);

        _mockGraphService.Setup(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains("oauth2PermissionGrants")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(emptyConsentDoc);

        // Service principals don't exist initially (return null), then exist after creation (return ID)
        // Track which SPs have been created
        var createdSps = new HashSet<string>();
        _mockGraphService.Setup(x => x.LookupServicePrincipalByAppIdAsync(
            It.IsAny<string>(), It.Is<string>(appId => appId == MosConstants.TpsAppServicesClientAppId), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(() => createdSps.Contains(MosConstants.TpsAppServicesClientAppId) ? "sp-object-id" : null);
        
        foreach (var resourceAppId in MosConstants.AllResourceAppIds)
        {
            var capturedAppId = resourceAppId; // Capture for closure
            _mockGraphService.Setup(x => x.LookupServicePrincipalByAppIdAsync(
                It.IsAny<string>(), It.Is<string>(appId => appId == capturedAppId), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
                .ReturnsAsync(() => createdSps.Contains(capturedAppId) ? "sp-object-id" : null);
        }

        _mockGraphService.Setup(x => x.EnsureServicePrincipalForAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync((string tenantId, string appId, CancellationToken ct, IEnumerable<string>? authScopes) =>
            {
                createdSps.Add(appId); // Mark as created
                return "sp-object-id";
            });

        _mockGraphService.Setup(x => x.GraphPatchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(true);

        _mockBlueprintService.Setup(x => x.ReplaceOauth2PermissionGrantAsync(
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<IEnumerable<string>>(), 
            default))
            .ReturnsAsync(true);

        // Act
        var result = await PublishHelpers.EnsureMosPrerequisitesAsync(
            _mockGraphService.Object, _mockBlueprintService.Object, _testConfig, _mockLogger.Object);

        // Assert
        result.Should().BeTrue();
        
        // Should create service principals for first-party client app + MOS resource apps
        var expectedServicePrincipalCalls = 1 + MosConstants.AllResourceAppIds.Length;
        _mockGraphService.Verify(x => x.EnsureServicePrincipalForAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()), 
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
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ThrowsAsync(new InvalidOperationException("Failed to create service principal"));

        // Act
        Func<Task> act = async () => await PublishHelpers.EnsureMosPrerequisitesAsync(
            _mockGraphService.Object, _mockBlueprintService.Object, _testConfig, _mockLogger.Object);

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
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ThrowsAsync(new Exception("403 Forbidden"));

        // Act
        Func<Task> act = async () => await PublishHelpers.EnsureMosPrerequisitesAsync(
            _mockGraphService.Object, _mockBlueprintService.Object, _testConfig, _mockLogger.Object);

        // Assert
        await act.Should().ThrowAsync<SetupValidationException>()
            .WithMessage("*Insufficient privileges*");
    }

    [Fact]
    public async Task EnsureMosPrerequisitesAsync_WhenCalledTwice_IsIdempotent()
    {
        // Arrange - app with ALL MOS permissions and consent correctly configured
        var appWithMosPermissions = JsonDocument.Parse($@"{{
            ""value"": [{{
                ""id"": ""app-object-id"",
                ""requiredResourceAccess"": [
                    {{
                        ""resourceAppId"": ""{MosConstants.TpsAppServicesResourceAppId}"",
                        ""resourceAccess"": [{{ ""id"": ""{MosConstants.ResourcePermissions.TpsAppServices.ScopeId}"", ""type"": ""Scope"" }}]
                    }},
                    {{
                        ""resourceAppId"": ""{MosConstants.PowerPlatformApiResourceAppId}"",
                        ""resourceAccess"": [{{ ""id"": ""{MosConstants.ResourcePermissions.PowerPlatformApi.ScopeId}"", ""type"": ""Scope"" }}]
                    }},
                    {{
                        ""resourceAppId"": ""{MosConstants.MosTitlesApiResourceAppId}"",
                        ""resourceAccess"": [{{ ""id"": ""{MosConstants.ResourcePermissions.MosTitlesApi.ScopeId}"", ""type"": ""Scope"" }}]
                    }}
                ]
            }}]
        }}");
        
        // Mock consent grants for each MOS resource app with correct scopes
        var tpsConsentDoc = JsonDocument.Parse($@"{{
            ""value"": [{{
                ""scope"": ""{MosConstants.ResourcePermissions.TpsAppServices.ScopeName}""
            }}]
        }}");
        
        var ppConsentDoc = JsonDocument.Parse($@"{{
            ""value"": [{{
                ""scope"": ""{MosConstants.ResourcePermissions.PowerPlatformApi.ScopeName}""
            }}]
        }}");
        
        var titlesConsentDoc = JsonDocument.Parse($@"{{
            ""value"": [{{
                ""scope"": ""{MosConstants.ResourcePermissions.MosTitlesApi.ScopeName}""
            }}]
        }}");
        
        _mockGraphService.Setup(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains($"appId eq '{_testConfig.ClientAppId}'")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(appWithMosPermissions);

        // Mock consent grants based on resourceId (SP object ID) in the query filter
        _mockGraphService.Setup(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains("oauth2PermissionGrants") && s.Contains("sp-tps")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(tpsConsentDoc);
            
        _mockGraphService.Setup(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains("oauth2PermissionGrants") && s.Contains("sp-pp")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(ppConsentDoc);
            
        _mockGraphService.Setup(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains("oauth2PermissionGrants") && s.Contains("sp-titles")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(titlesConsentDoc);

        // Mock service principal lookups - return unique IDs for each resource app
        _mockGraphService.Setup(x => x.LookupServicePrincipalByAppIdAsync(
            It.IsAny<string>(), MosConstants.TpsAppServicesClientAppId, It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync("sp-first-party-client");
            
        _mockGraphService.Setup(x => x.LookupServicePrincipalByAppIdAsync(
            It.IsAny<string>(), MosConstants.TpsAppServicesResourceAppId, It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync("sp-tps");
            
        _mockGraphService.Setup(x => x.LookupServicePrincipalByAppIdAsync(
            It.IsAny<string>(), MosConstants.PowerPlatformApiResourceAppId, It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync("sp-pp");
            
        _mockGraphService.Setup(x => x.LookupServicePrincipalByAppIdAsync(
            It.IsAny<string>(), MosConstants.MosTitlesApiResourceAppId, It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync("sp-titles");

        _mockGraphService.Setup(x => x.GraphPatchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(true);

        // Act
        var result1 = await PublishHelpers.EnsureMosPrerequisitesAsync(
            _mockGraphService.Object, _mockBlueprintService.Object, _testConfig, _mockLogger.Object);
        var result2 = await PublishHelpers.EnsureMosPrerequisitesAsync(
            _mockGraphService.Object, _mockBlueprintService.Object, _testConfig, _mockLogger.Object);

        // Assert
        result1.Should().BeTrue();
        result2.Should().BeTrue();
        
        // Should query the app once per call
        _mockGraphService.Verify(x => x.GraphGetAsync(
            It.IsAny<string>(), 
            It.Is<string>(s => s.Contains($"appId eq '{_testConfig.ClientAppId}'")), 
            It.IsAny<CancellationToken>(),
            It.IsAny<IEnumerable<string>?>()), Times.Exactly(2));
        
        // When all prerequisites exist, EnsureServicePrincipalForAppIdAsync should NEVER be called (truly idempotent)
        _mockGraphService.Verify(x => x.EnsureServicePrincipalForAppIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()), 
            Times.Never());
        
        // GraphPatchAsync should never be called since permissions are already correct
        _mockGraphService.Verify(x => x.GraphPatchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()), 
            Times.Never());
        
        // ReplaceOauth2PermissionGrantAsync should never be called since consent already exists
        _mockBlueprintService.Verify(x => x.ReplaceOauth2PermissionGrantAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), 
            Times.Never());
    }
}
