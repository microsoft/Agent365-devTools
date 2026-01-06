// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;

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

            var doc = await _graphApiService.GraphGetAsync(
                tenantId,
                $"/beta/applications/{blueprintObjectId}/federatedIdentityCredentials",
                cancellationToken);

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
                var id = item.GetProperty("id").GetString();
                var name = item.GetProperty("name").GetString();
                var issuer = item.GetProperty("issuer").GetString();
                var subject = item.GetProperty("subject").GetString();
                
                var audiences = new List<string>();
                if (item.TryGetProperty("audiences", out var audiencesElement))
                {
                    foreach (var audience in audiencesElement.EnumerateArray())
                    {
                        audiences.Add(audience.GetString() ?? string.Empty);
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
            _logger.LogError(ex, "Failed to check federated credential existence");
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

            var payloadJson = JsonSerializer.Serialize(payload);

            // Try the standard endpoint first
            var endpoint = $"/beta/applications/{blueprintObjectId}/federatedIdentityCredentials";
            
            var response = await _graphApiService.GraphPostWithResponseAsync(
                tenantId,
                endpoint,
                payloadJson,
                cancellationToken);

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
                _logger.LogDebug("Federated credential already exists (HTTP 409): {Name}", name);
                return new FederatedCredentialCreateResult
                {
                    Success = true,
                    AlreadyExisted = true
                };
            }

            // Log error details from standard endpoint
            _logger.LogWarning("Standard endpoint failed: HTTP {StatusCode} {ReasonPhrase}", response.StatusCode, response.ReasonPhrase);
            if (!string.IsNullOrWhiteSpace(response.Body))
            {
                _logger.LogDebug("Response body: {Body}", response.Body);
            }

            // Try fallback endpoint for agent blueprint
            _logger.LogDebug("Trying fallback endpoint for agent blueprint federated credential");
            endpoint = $"/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/federatedIdentityCredentials";
            
            response = await _graphApiService.GraphPostWithResponseAsync(
                tenantId,
                endpoint,
                payloadJson,
                cancellationToken);

            if (response.IsSuccess)
            {
                _logger.LogInformation("Successfully created federated credential using fallback endpoint: {Name}", name);
                return new FederatedCredentialCreateResult
                {
                    Success = true,
                    AlreadyExisted = false
                };
            }

            // Check for HTTP 409 (Conflict) - credential already exists
            if (response.StatusCode == 409)
            {
                _logger.LogDebug("Federated credential already exists (HTTP 409) on fallback endpoint: {Name}", name);
                return new FederatedCredentialCreateResult
                {
                    Success = true,
                    AlreadyExisted = true
                };
            }

            // Log error details from fallback endpoint
            _logger.LogError("Fallback endpoint also failed: HTTP {StatusCode} {ReasonPhrase}", response.StatusCode, response.ReasonPhrase);
            if (!string.IsNullOrWhiteSpace(response.Body))
            {
                _logger.LogDebug("Response body: {Body}", response.Body);
            }

            _logger.LogError("Failed to create federated credential: {Name}. Both standard and fallback endpoints returned errors.", name);
            return new FederatedCredentialCreateResult
            {
                Success = false,
                ErrorMessage = $"HTTP {response.StatusCode}: {response.ReasonPhrase}"
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
}

/// <summary>
/// Information about a federated identity credential.
/// </summary>
public class FederatedCredentialInfo
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Issuer { get; set; }
    public string? Subject { get; set; }
    public List<string> Audiences { get; set; } = new();
}

/// <summary>
/// Result of checking if a federated credential exists.
/// </summary>
public class FederatedCredentialCheckResult
{
    public bool Exists { get; set; }
    public FederatedCredentialInfo? ExistingCredential { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result of creating a federated credential.
/// </summary>
public class FederatedCredentialCreateResult
{
    public bool Success { get; set; }
    public bool AlreadyExisted { get; set; }
    public string? ErrorMessage { get; set; }
}
