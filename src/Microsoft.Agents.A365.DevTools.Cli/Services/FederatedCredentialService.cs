// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for managing federated identity credentials for agent blueprint applications.
/// Handles checking existing FICs and creating new ones with idempotency.
/// </summary>
public class FederatedCredentialService
{
    private readonly ILogger<FederatedCredentialService> _logger;
    private readonly GraphApiService _graphApiService;

    public FederatedCredentialService(ILogger<FederatedCredentialService> logger, GraphApiService graphApiService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _graphApiService = graphApiService ?? throw new ArgumentNullException(nameof(graphApiService));
    }

    /// <summary>
    /// Gets or sets the custom client app ID to use for Microsoft Graph authentication.
    /// </summary>
    public string? CustomClientAppId
    {
        get => _graphApiService.CustomClientAppId;
        set => _graphApiService.CustomClientAppId = value;
    }

    /// <summary>
    /// Get all federated credentials for a blueprint application.
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="blueprintObjectId">The blueprint application object ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of federated credentials</returns>
    public async Task<List<FederatedCredentialInfo>> GetFederatedCredentialsAsync(
        string tenantId,
        string blueprintObjectId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Retrieving federated credentials for blueprint: {ObjectId}", blueprintObjectId);

            var authScopes = AuthenticationConstants.PermissionGrantAuthScopes;

            // Try standard endpoint first
            var doc = await _graphApiService.GraphGetAsync(
                tenantId,
                $"/beta/applications/{blueprintObjectId}/federatedIdentityCredentials",
                cancellationToken,
                authScopes);

            // If standard endpoint returns data with credentials, use it
            if (doc != null && doc.RootElement.TryGetProperty("value", out var valueCheck) && valueCheck.GetArrayLength() > 0)
            {
                _logger.LogDebug("Standard endpoint returned {Count} credential(s)", valueCheck.GetArrayLength());
            }
            // If standard endpoint returns empty or null, try Agent Blueprint-specific endpoint
            else
            {
                _logger.LogDebug("Standard endpoint returned no credentials or failed, trying Agent Blueprint fallback endpoint");
                doc = await _graphApiService.GraphGetAsync(
                    tenantId,
                    $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/federatedIdentityCredentials",
                    cancellationToken,
                    authScopes);
            }

            if (doc == null)
            {
                _logger.LogDebug("No federated credentials found for blueprint: {ObjectId}", blueprintObjectId);
                return new List<FederatedCredentialInfo>();
            }

            var root = doc.RootElement;
            if (!root.TryGetProperty("value", out var valueElement))
            {
                return new List<FederatedCredentialInfo>();
            }

            var credentials = new List<FederatedCredentialInfo>();
            foreach (var item in valueElement.EnumerateArray())
            {
                try
                {
                    // Use TryGetProperty to handle missing fields gracefully
                    if (!item.TryGetProperty("id", out var idElement) || string.IsNullOrWhiteSpace(idElement.GetString()))
                    {
                        _logger.LogWarning("Skipping federated credential with missing or empty 'id' field");
                        continue;
                    }

                    if (!item.TryGetProperty("name", out var nameElement) || string.IsNullOrWhiteSpace(nameElement.GetString()))
                    {
                        _logger.LogWarning("Skipping federated credential with missing or empty 'name' field");
                        continue;
                    }

                    if (!item.TryGetProperty("issuer", out var issuerElement) || string.IsNullOrWhiteSpace(issuerElement.GetString()))
                    {
                        _logger.LogWarning("Skipping federated credential with missing or empty 'issuer' field");
                        continue;
                    }

                    if (!item.TryGetProperty("subject", out var subjectElement) || string.IsNullOrWhiteSpace(subjectElement.GetString()))
                    {
                        _logger.LogWarning("Skipping federated credential with missing or empty 'subject' field");
                        continue;
                    }

                    var id = idElement.GetString();
                    var name = nameElement.GetString();
                    var issuer = issuerElement.GetString();
                    var subject = subjectElement.GetString();
                    
                    var audiences = new List<string>();
                    if (item.TryGetProperty("audiences", out var audiencesElement))
                    {
                        foreach (var audience in audiencesElement.EnumerateArray())
                        {
                            var audienceValue = audience.GetString();
                            if (!string.IsNullOrWhiteSpace(audienceValue))
                            {
                                audiences.Add(audienceValue);
                            }
                        }
                    }

                    credentials.Add(new FederatedCredentialInfo
                    {
                        Id = id,
                        Name = name,
                        Issuer = issuer,
                        Subject = subject,
                        Audiences = audiences
                    });
                }
                catch (Exception itemEx)
                {
                    // Log individual credential parsing errors but continue processing remaining credentials
                    _logger.LogWarning(itemEx, "Failed to parse federated credential entry, skipping");
                }
            }

            _logger.LogDebug("Found {Count} federated credential(s) for blueprint: {ObjectId}", 
                credentials.Count, blueprintObjectId);

            return credentials;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve federated credentials for blueprint: {ObjectId}", blueprintObjectId);
            return new List<FederatedCredentialInfo>();
        }
    }

    /// <summary>
    /// Check if a federated credential exists with matching subject and issuer.
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="blueprintObjectId">The blueprint application object ID</param>
    /// <param name="subject">The subject to match (typically MSI principal ID)</param>
    /// <param name="issuer">The issuer to match</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if matching credential exists, false otherwise</returns>
    public async Task<FederatedCredentialCheckResult> CheckFederatedCredentialExistsAsync(
        string tenantId,
        string blueprintObjectId,
        string subject,
        string issuer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var credentials = await GetFederatedCredentialsAsync(tenantId, blueprintObjectId, cancellationToken);

            var match = credentials.FirstOrDefault(c => 
                string.Equals(c.Subject, subject, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Issuer, issuer, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                _logger.LogDebug("Found existing federated credential: {Name} (Subject: {Subject})", 
                    match.Name, subject);

                return new FederatedCredentialCheckResult
                {
                    Exists = true,
                    ExistingCredential = match
                };
            }

            _logger.LogDebug("No existing federated credential found with subject: {Subject}", subject);
            return new FederatedCredentialCheckResult
            {
                Exists = false
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve federated credentials for existence check. Assuming credential does not exist.");
            _logger.LogDebug("Error details: {Message}", ex.Message);
            return new FederatedCredentialCheckResult
            {
                Exists = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Create a new federated identity credential for a blueprint.
    /// Handles HTTP 409 (already exists) as a success case.
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="blueprintObjectId">The blueprint application object ID</param>
    /// <param name="name">The name for the federated credential</param>
    /// <param name="issuer">The issuer URL</param>
    /// <param name="subject">The subject (typically MSI principal ID)</param>
    /// <param name="audiences">List of audiences (typically ["api://AzureADTokenExchange"])</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result indicating success or failure</returns>
    public async Task<FederatedCredentialCreateResult> CreateFederatedCredentialAsync(
        string tenantId,
        string blueprintObjectId,
        string name,
        string issuer,
        string subject,
        List<string> audiences,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Creating federated credential: {Name} for blueprint: {ObjectId}", 
                name, blueprintObjectId);

            var payload = new
            {
                name,
                issuer,
                subject,
                audiences
            };

            // Try both standard and Agent Blueprint-specific endpoints
            var endpoints = new[]
            {
                $"/beta/applications/{blueprintObjectId}/federatedIdentityCredentials",
                $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/federatedIdentityCredentials"
            };

            foreach (var endpoint in endpoints)
            {
                _logger.LogDebug("Attempting federated credential creation with endpoint: {Endpoint}", endpoint);
                
                var authScopes = AuthenticationConstants.PermissionGrantAuthScopes;

                var response = await _graphApiService.GraphPostWithResponseAsync(
                    tenantId,
                    endpoint,
                    payload,
                    cancellationToken,
                    authScopes);

                if (response.IsSuccess)
                {
                    _logger.LogInformation("Successfully created federated credential: {Name}", name);
                    return new FederatedCredentialCreateResult
                    {
                        Success = true,
                        AlreadyExisted = false
                    };
                }

                // Check for HTTP 409 (Conflict) - credential already exists
                if (response.StatusCode == 409)
                {
                    _logger.LogInformation("Federated credential already exists: {Name}", name);
                    
                    return new FederatedCredentialCreateResult
                    {
                        Success = true,
                        AlreadyExisted = true
                    };
                }

                // Check if we should try the alternative endpoint
                if (response.StatusCode == 403 && response.Body?.Contains("Agent Blueprints are not supported") == true)
                {
                    _logger.LogDebug("Standard endpoint not supported for Agent Blueprints, trying specialized endpoint...");
                    continue;
                }

                // Check if we should try the alternative endpoint due to calling identity type
                if (response.StatusCode == 403 && response.Body?.Contains("This operation cannot be performed for the specified calling identity type") == true)
                {
                    _logger.LogDebug("Endpoint rejected calling identity type, trying alternative endpoint...");
                    continue;
                }

                // Check for HTTP 404 - resource not found (propagation delay)
                if (response.StatusCode == 404)
                {
                    _logger.LogDebug("Application not yet ready (HTTP 404), will retry...");
                    return new FederatedCredentialCreateResult
                    {
                        Success = false,
                        ErrorMessage = $"HTTP {response.StatusCode}: {response.ReasonPhrase}",
                        ShouldRetry = true
                    };
                }

                // For other errors on first endpoint, try second endpoint
                if (endpoint == endpoints[0])
                {
                    _logger.LogDebug("First endpoint failed with HTTP {StatusCode}, trying second endpoint...", response.StatusCode);
                    continue;
                }

                // Both endpoints failed
                _logger.LogError("Failed to create federated credential: HTTP {StatusCode} {ReasonPhrase}", response.StatusCode, response.ReasonPhrase);
                if (!string.IsNullOrWhiteSpace(response.Body))
                {
                    _logger.LogError("Error details: {Body}", response.Body);
                }

                _logger.LogError("Failed to create federated credential: {Name}", name);
                return new FederatedCredentialCreateResult
                {
                    Success = false,
                    ErrorMessage = $"HTTP {response.StatusCode}: {response.ReasonPhrase}"
                };
            }

            // Should not reach here, but handle it
            return new FederatedCredentialCreateResult
            {
                Success = false,
                ErrorMessage = "Failed after trying all endpoints"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create federated credential: {Name}", name);
            return new FederatedCredentialCreateResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Delete a federated credential from a blueprint application.
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="blueprintObjectId">The blueprint application object ID</param>
    /// <param name="credentialId">The federated credential ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted successfully or not found, false otherwise</returns>
    public async Task<bool> DeleteFederatedCredentialAsync(
        string tenantId,
        string blueprintObjectId,
        string credentialId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Deleting federated credential: {CredentialId} from blueprint: {ObjectId}", 
                credentialId, blueprintObjectId);

            // Try the standard endpoint first
            var endpoint = $"/beta/applications/{blueprintObjectId}/federatedIdentityCredentials/{credentialId}";            
            var authScopes = AuthenticationConstants.PermissionGrantAuthScopes;

            var success = await _graphApiService.GraphDeleteAsync(
                tenantId,
                endpoint,
                cancellationToken,
                treatNotFoundAsSuccess: true,
                authScopes);

            if (success)
            {
                _logger.LogDebug("Successfully deleted federated credential using standard endpoint: {CredentialId}", credentialId);
                return true;
            }

            // Try fallback endpoint for agent blueprint
            _logger.LogDebug("Standard endpoint failed, trying fallback endpoint for agent blueprint");
            endpoint = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/federatedIdentityCredentials/{credentialId}";
            
            success = await _graphApiService.GraphDeleteAsync(
                tenantId,
                endpoint,
                cancellationToken,
                treatNotFoundAsSuccess: true,
                authScopes);

            if (success)
            {
                _logger.LogDebug("Successfully deleted federated credential using fallback endpoint: {CredentialId}", credentialId);
                return true;
            }

            _logger.LogWarning("Failed to delete federated credential using both endpoints: {CredentialId}", credentialId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception deleting federated credential: {CredentialId}", credentialId);
            return false;
        }
    }

    /// <summary>
    /// Delete all federated credentials from a blueprint application.
    /// </summary>
    /// <param name="tenantId">The tenant ID for authentication</param>
    /// <param name="blueprintObjectId">The blueprint application object ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if all credentials deleted successfully, false otherwise</returns>
    public async Task<bool> DeleteAllFederatedCredentialsAsync(
        string tenantId,
        string blueprintObjectId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Retrieving federated credentials for deletion from blueprint: {ObjectId}", blueprintObjectId);
            
            var credentials = await GetFederatedCredentialsAsync(tenantId, blueprintObjectId, cancellationToken);
            
            if (credentials.Count == 0)
            {
                _logger.LogInformation("No federated credentials found to delete");
                return true;
            }

            _logger.LogInformation("Found {Count} federated credential(s) to delete", credentials.Count);
            
            bool allSuccess = true;
            foreach (var credential in credentials)
            {
                if (string.IsNullOrWhiteSpace(credential.Id))
                {
                    _logger.LogWarning("Skipping credential with missing ID");
                    continue;
                }

                _logger.LogInformation("Deleting federated credential: {Name}", credential.Name ?? credential.Id);
                
                var deleted = await DeleteFederatedCredentialAsync(
                    tenantId,
                    blueprintObjectId,
                    credential.Id,
                    cancellationToken);

                if (deleted)
                {
                    _logger.LogInformation("Federated credential deleted: {Name}", credential.Name ?? credential.Id);
                }
                else
                {
                    _logger.LogWarning("Failed to delete federated credential: {Name}", credential.Name ?? credential.Id);
                    allSuccess = false;
                }
            }

            return allSuccess;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception deleting federated credentials from blueprint: {ObjectId}", blueprintObjectId);
            return false;
        }
    }
}
