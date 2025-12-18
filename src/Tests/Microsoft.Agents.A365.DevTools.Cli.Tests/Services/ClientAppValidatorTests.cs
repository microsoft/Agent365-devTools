// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Unit tests for ClientAppValidator service.
/// Tests validation logic for client app existence, permissions, and admin consent.
/// </summary>
public class ClientAppValidatorTests
{
    private readonly ILogger<ClientAppValidator> _logger;
    private readonly CommandExecutor _executor;
    private readonly ClientAppValidator _validator;

    private const string ValidClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6";
    private const string ValidTenantId = "12345678-1234-1234-1234-123456789012";
    private const string InvalidGuid = "not-a-guid";

    public ClientAppValidatorTests()
    {
        _logger = Substitute.For<ILogger<ClientAppValidator>>();
        
        // CommandExecutor requires a logger in its constructor for NSubstitute to create a proxy
        var executorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _executor = Substitute.ForPartsOf<CommandExecutor>(executorLogger);
        
        _validator = new ClientAppValidator(_logger, _executor);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => 
            new ClientAppValidator(null!, _executor));
        
        exception.ParamName.Should().Be("logger");
    }

    [Fact]
    public void Constructor_WithNullExecutor_ThrowsArgumentNullException()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => 
            new ClientAppValidator(_logger, null!));
        
        exception.ParamName.Should().Be("executor");
    }

    #endregion

    #region EnsureValidClientAppAsync - Input Validation Tests

    [Fact]
    public async Task EnsureValidClientAppAsync_WithNullClientAppId_ThrowsArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => 
            _validator.EnsureValidClientAppAsync(null!, ValidTenantId));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WithEmptyClientAppId_ThrowsArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _validator.EnsureValidClientAppAsync(string.Empty, ValidTenantId));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WithInvalidClientAppIdFormat_ReturnsInvalidFormatFailure()
    {
        // Act
        await Assert.ThrowsAsync<ClientAppValidationException>(async () => await _validator.EnsureValidClientAppAsync(InvalidGuid, ValidTenantId));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WithInvalidTenantIdFormat_ReturnsInvalidFormatFailure()
    {
        // Act
        await Assert.ThrowsAsync<ClientAppValidationException>(async () => await _validator.EnsureValidClientAppAsync(ValidClientAppId, InvalidGuid));
    }

    #endregion

    #region EnsureValidClientAppAsync - Token Acquisition Tests

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenTokenAcquisitionFails_ReturnsAuthenticationFailed()
    {
        // Arrange
        _executor.ExecuteAsync(
            Arg.Is<string>(s => s == "az"),
            Arg.Is<string>(s => s.Contains("account get-access-token")),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 1, StandardOutput = string.Empty, StandardError = "Not logged in" });

        // Act
        await Assert.ThrowsAsync<ClientAppValidationException>(async () => await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenTokenIsEmpty_ThrowsClientAppValidationException()
    {
        // Arrange
        _executor.ExecuteAsync(
            Arg.Is<string>(s => s == "az"),
            Arg.Is<string>(s => s.Contains("account get-access-token")),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = "   ", StandardError = string.Empty });

        // Act & Assert
        await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));
    }

    #endregion

    #region EnsureValidClientAppAsync - App Existence Tests

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAppDoesNotExist_ReturnsAppNotFound()
    {
        // Arrange
        var token = "fake-token-123";
        _executor.ExecuteAsync(
            Arg.Is<string>(s => s == "az"),
            Arg.Is<string>(s => s.Contains("account get-access-token")),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = token, StandardError = string.Empty });

        _executor.ExecuteAsync(
            Arg.Is<string>(s => s == "az"),
            Arg.Is<string>(s => s.Contains("rest --method GET") && s.Contains("/applications")),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = "{\"value\": []}", StandardError = string.Empty });

        // Act
        await Assert.ThrowsAsync<ClientAppValidationException>(async () => await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenGraphQueryFails_ThrowsClientAppValidationException()
    {
        // Arrange
        var token = "fake-token-123";
        _executor.ExecuteAsync(
            Arg.Is<string>(s => s == "az"),
            Arg.Is<string>(s => s.Contains("account get-access-token")),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = token, StandardError = string.Empty });

        _executor.ExecuteAsync(
            Arg.Is<string>(s => s == "az"),
            Arg.Is<string>(s => s.Contains("rest --method GET") && s.Contains("/applications")),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 1, StandardOutput = string.Empty, StandardError = "Graph API error" });

        // Act & Assert
        await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));
    }

    #endregion

    #region EnsureValidClientAppAsync - Permission Validation Tests

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAppHasNoRequiredResourceAccess_ReturnsMissingPermissions()
    {
        // Arrange
        var token = "fake-token-123";
        SetupTokenAcquisition(token);
        SetupAppExists(ValidClientAppId, "Test App", requiredResourceAccess: null);

        // Act
        await Assert.ThrowsAsync<ClientAppValidationException>(async () => await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAppMissingGraphPermissions_ThrowsClientAppValidationException()
    {
        // Arrange
        var token = "fake-token-123";
        SetupTokenAcquisition(token);
        
        var requiredResourceAccess = $$"""
        [
            {
                "resourceAppId": "some-other-app-id",
                "resourceAccess": []
            }
        ]
        """;
        
        SetupAppExists(ValidClientAppId, "Test App", requiredResourceAccess);

        // Act & Assert
        await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAppMissingSomePermissions_ThrowsClientAppValidationException()
    {
        // Arrange
        var token = "fake-token-123";
        SetupTokenAcquisition(token);
        SetupGraphPermissionResolution(token);
        
        // Only include Application.ReadWrite.All, missing others
        var requiredResourceAccess = $$"""
        [
            {
                "resourceAppId": "{{AuthenticationConstants.MicrosoftGraphResourceAppId}}",
                "resourceAccess": [
                    {
                        "id": "1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9",
                        "type": "Scope"
                    }
                ]
            }
        ]
        """;
        
        SetupAppExists(ValidClientAppId, "Test App", requiredResourceAccess);

        // Act & Assert
        await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));
    }

    #endregion

    #region EnsureValidClientAppAsync - Success Tests

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAllValidationsPass_DoesNotThrow()
    {
        // Arrange
        var token = "fake-token-123";
        SetupTokenAcquisition(token);
        SetupAppExistsWithAllPermissions(ValidClientAppId, "Test App");
        SetupAdminConsentGranted(ValidClientAppId);

        // Act & Assert - should not throw
        await _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId);
    }

    #endregion

    #region EnsureValidClientAppAsync Exception Tests

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAppNotFound_ThrowsClientAppValidationException()
    {
        // Arrange
        var token = "fake-token-123";
        SetupTokenAcquisition(token);
        _executor.ExecuteAsync(
            Arg.Is<string>(s => s == "az"),
            Arg.Is<string>(s => s.Contains("rest --method GET") && s.Contains("/applications")),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = "{\"value\": []}", StandardError = string.Empty });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        exception.IssueDescription.Should().Contain("not found in tenant");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenMissingPermissions_ThrowsClientAppValidationException()
    {
        // Arrange
        var token = "fake-token-123";
        SetupTokenAcquisition(token);
        SetupAppExists(ValidClientAppId, "Test App", requiredResourceAccess: "[]");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        exception.IssueDescription.Should().Contain("missing required API permissions");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenMissingAdminConsent_ThrowsClientAppValidationException()
    {
        // Arrange
        var token = "fake-token-123";
        SetupTokenAcquisition(token);
        SetupAppExistsWithAllPermissions(ValidClientAppId, "Test App");
        SetupAdminConsentNotGranted(ValidClientAppId);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ClientAppValidationException>(
            () => _validator.EnsureValidClientAppAsync(ValidClientAppId, ValidTenantId));

        exception.ErrorCode.Should().Be(ErrorCodes.ClientAppValidationFailed);
        exception.IssueDescription.Should().Contain("Admin consent");
    }

    #endregion

    #region Helper Methods

    private void SetupTokenAcquisition(string token)
    {
        _executor.ExecuteAsync(
            Arg.Is<string>(s => s == "az"),
            Arg.Is<string>(s => s.Contains("account get-access-token")),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = token, StandardError = string.Empty });
    }

    private void SetupAppExists(string appId, string displayName, string? requiredResourceAccess)
    {
        var resourceAccessJson = requiredResourceAccess ?? "[]";
        var appJson = $$"""
        {
            "value": [
                {
                    "id": "object-id-123",
                    "appId": "{{appId}}",
                    "displayName": "{{displayName}}",
                    "requiredResourceAccess": {{resourceAccessJson}}
                }
            ]
        }
        """;

        _executor.ExecuteAsync(
            Arg.Is<string>(s => s == "az"),
            Arg.Is<string>(s => s.Contains("rest --method GET") && s.Contains("/applications")),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = appJson, StandardError = string.Empty });
    }

    private void SetupAppExistsWithAllPermissions(string appId, string displayName)
    {
        var requiredResourceAccess = $$"""
        [
            {
                "resourceAppId": "{{AuthenticationConstants.MicrosoftGraphResourceAppId}}",
                "resourceAccess": [
                    {
                        "id": "1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9",
                        "type": "Scope",
                        "comment": "Application.ReadWrite.All"
                    },
                    {
                        "id": "8e8e4742-1d95-4f68-9d56-6ee75648c72a",
                        "type": "Scope",
                        "comment": "Directory.Read.All"
                    },
                    {
                        "id": "06da0dbc-49e2-44d2-8312-53f166ab848a",
                        "type": "Scope",
                        "comment": "DelegatedPermissionGrant.ReadWrite.All"
                    },
                    {
                        "id": "00000000-0000-0000-0000-000000000001",
                        "type": "Scope",
                        "comment": "AgentIdentityBlueprint.ReadWrite.All (placeholder GUID for test)"
                    },
                    {
                        "id": "00000000-0000-0000-0000-000000000002",
                        "type": "Scope",
                        "comment": "AgentIdentityBlueprint.UpdateAuthProperties.All (placeholder GUID for test)"
                    }
                ]
            }
        ]
        """;

        SetupAppExists(appId, displayName, requiredResourceAccess);
    }

    private void SetupAdminConsentGranted(string clientAppId)
    {
        // Setup service principal query
        var spJson = $$"""
        {
            "value": [
                {
                    "id": "sp-object-id-123",
                    "appId": "{{clientAppId}}"
                }
            ]
        }
        """;

        _executor.ExecuteAsync(
            Arg.Is<string>(s => s == "az"),
            Arg.Is<string>(s => s.Contains("rest --method GET") && s.Contains("/servicePrincipals")),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = spJson, StandardError = string.Empty });

        // Setup OAuth2 grants with required scopes (all 5 permissions)
        var grantsJson = """
        {
            "value": [
                {
                    "id": "grant-id-123",
                    "scope": "Application.ReadWrite.All AgentIdentityBlueprint.ReadWrite.All AgentIdentityBlueprint.UpdateAuthProperties.All DelegatedPermissionGrant.ReadWrite.All Directory.Read.All"
                }
            ]
        }
        """;

        _executor.ExecuteAsync(
            Arg.Is<string>(s => s == "az"),
            Arg.Is<string>(s => s.Contains("rest --method GET") && s.Contains("/oauth2PermissionGrants")),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = grantsJson, StandardError = string.Empty });
    }

    private void SetupAdminConsentNotGranted(string clientAppId)
    {
        // Setup service principal query
        var spJson = $$"""
        {
            "value": [
                {
                    "id": "sp-object-id-123",
                    "appId": "{{clientAppId}}"
                }
            ]
        }
        """;

        _executor.ExecuteAsync(
            Arg.Is<string>(s => s == "az"),
            Arg.Is<string>(s => s.Contains("rest --method GET") && s.Contains("/servicePrincipals")),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = spJson, StandardError = string.Empty });

        // Setup empty grants (no consent)
        var grantsJson = """
        {
            "value": []
        }
        """;

        _executor.ExecuteAsync(
            Arg.Is<string>(s => s == "az"),
            Arg.Is<string>(s => s.Contains("rest --method GET") && s.Contains("/oauth2PermissionGrants")),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = grantsJson, StandardError = string.Empty });
    }

    private void SetupGraphPermissionResolution(string token)
    {
        // Mock the Graph API call to retrieve Microsoft Graph's published permission definitions
        var graphPermissionsJson = """
        {
            "value": [
                {
                    "id": "graph-sp-id-123",
                    "oauth2PermissionScopes": [
                        {
                            "id": "1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9",
                            "value": "Application.ReadWrite.All"
                        },
                        {
                            "id": "8e8e4742-1d95-4f68-9d56-6ee75648c72a",
                            "value": "Directory.Read.All"
                        },
                        {
                            "id": "06da0dbc-49e2-44d2-8312-53f166ab848a",
                            "value": "DelegatedPermissionGrant.ReadWrite.All"
                        },
                        {
                            "id": "00000000-0000-0000-0000-000000000001",
                            "value": "AgentIdentityBlueprint.ReadWrite.All"
                        },
                        {
                            "id": "00000000-0000-0000-0000-000000000002",
                            "value": "AgentIdentityBlueprint.UpdateAuthProperties.All"
                        }
                    ]
                }
            ]
        }
        """;

        _executor.ExecuteAsync(
            Arg.Is<string>(s => s == "az"),
            Arg.Is<string>(s => s.Contains("rest --method GET") && s.Contains($"/servicePrincipals") && s.Contains($"appId eq '{AuthenticationConstants.MicrosoftGraphResourceAppId}'")),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = graphPermissionsJson, StandardError = string.Empty });
    }

    #endregion
}

