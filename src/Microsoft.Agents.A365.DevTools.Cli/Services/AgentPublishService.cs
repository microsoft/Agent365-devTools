// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for agent blueprint publish workflow operations including federated identity credentials,
/// service principal lookups, and app role assignments.
/// </summary>
public class AgentPublishService
{
    private readonly ILogger<AgentPublishService> _logger;
    private readonly GraphApiService _graphApiService;

    public AgentPublishService(ILogger<AgentPublishService> logger, GraphApiService graphApiService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _graphApiService = graphApiService ?? throw new ArgumentNullException(nameof(graphApiService));
    }

    /// <summary>
    /// Gets or sets the custom client app ID to use for Microsoft Graph authentication.
    /// This delegates to the underlying GraphApiService.
    /// </summary>
    public string? CustomClientAppId
    {
        get => _graphApiService.CustomClientAppId;
        set => _graphApiService.CustomClientAppId = value;
    }

    /// <summary>
    /// Executes the complete publish workflow for an agent blueprint including FIC creation,
    /// service principal lookups, and app role assignments.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="blueprintId">The blueprint application ID</param>
    /// <param name="manifestId">The manifest ID for FMI construction</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if all steps succeeded, false otherwise</returns>
    public virtual async Task<bool> ExecutePublishGraphStepsAsync(
        string tenantId,
        string blueprintId,
        string manifestId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Configuring agent blueprint for runtime authentication...");
            _logger.LogDebug("TenantId: {TenantId}", tenantId);
            _logger.LogDebug("BlueprintId: {BlueprintId}", blueprintId);
            _logger.LogDebug("ManifestId: {ManifestId}", manifestId);

            // Step 1: Derive federated identity subject using FMI ID logic
            _logger.LogDebug("[STEP 1] Deriving federated identity subject (FMI ID)...");
            
            // MOS3 App ID - well-known identifier for MOS (Microsoft Online Services)
            const string mos3AppId = "e8be65d6-d430-4289-a665-51bf2a194bda";
            var subjectValue = ConstructFmiId(tenantId, mos3AppId, manifestId);
            _logger.LogDebug("Subject value (FMI ID): {Subject}", subjectValue);

            // Step 2: Create federated identity credential
            _logger.LogInformation("Configuring workload identity authentication for agent runtime...");
            await CreateFederatedIdentityCredentialAsync(
                blueprintId, 
                subjectValue, 
                tenantId,
                manifestId,
                cancellationToken);

            // Step 3: Lookup Service Principal
            _logger.LogDebug("[STEP 3] Looking up service principal for blueprint {BlueprintId}...", blueprintId);
            var spObjectId = await LookupServicePrincipalAsync(tenantId, blueprintId, cancellationToken);
            if (string.IsNullOrWhiteSpace(spObjectId))
            {
                _logger.LogError("Failed to lookup service principal for blueprint {BlueprintId}", blueprintId);
                _logger.LogError("The agent blueprint service principal may not have been created yet.");
                _logger.LogError("Try running 'a365 deploy' or 'a365 setup' to create the agent identity first.");
                return false;
            }

            _logger.LogDebug("Service principal objectId: {ObjectId}", spObjectId);

            // Step 4: Lookup Microsoft Graph Service Principal
            _logger.LogDebug("[STEP 4] Looking up Microsoft Graph service principal...");
            var msGraphResourceId = await LookupMicrosoftGraphServicePrincipalAsync(tenantId, cancellationToken);
            if (string.IsNullOrWhiteSpace(msGraphResourceId))
            {
                _logger.LogError("Failed to lookup Microsoft Graph service principal");
                return false;
            }

            _logger.LogDebug("Microsoft Graph service principal objectId: {ObjectId}", msGraphResourceId);

            // Step 5: Assign app role (optional for agent applications)
            _logger.LogInformation("Granting Microsoft Graph permissions to agent blueprint...");
            await AssignAppRoleAsync(tenantId, spObjectId, msGraphResourceId, cancellationToken);

            _logger.LogInformation("Agent blueprint configuration completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publish graph steps failed: {Message}", ex.Message);
            return false;
        }
    }

    private async Task CreateFederatedIdentityCredentialAsync(
        string blueprintId,
        string subjectValue,
        string tenantId,
        string manifestId,
        CancellationToken cancellationToken)
    {
        try
        {
            var ficName = $"fic-{manifestId}";

            // Check if FIC already exists
            var existing = await _graphApiService.GraphGetAsync(tenantId, $"/beta/applications/{blueprintId}/federatedIdentityCredentials", cancellationToken);

            if (existing != null && existing.RootElement.TryGetProperty("value", out var fics))
            {
                foreach (var fic in fics.EnumerateArray())
                {
                    if (fic.TryGetProperty("subject", out var subject) && 
                        subject.GetString() == subjectValue)
                    {
                        _logger.LogInformation("Workload identity authentication already configured");
                        return;
                    }
                }
            }

            // Create new FIC
            var payload = new
            {
                name = ficName,
                issuer = $"https://login.microsoftonline.com/{tenantId}/v2.0",
                subject = subjectValue,
                audiences = new[] { "api://AzureADTokenExchange" }
            };

            var created = await _graphApiService.GraphPostAsync(tenantId, $"/beta/applications/{blueprintId}/federatedIdentityCredentials", payload, cancellationToken);

            if (created == null)
            {
                _logger.LogDebug("Failed to create FIC (expected in some scenarios)");
                return;
            }

            _logger.LogInformation("Workload identity authentication configured successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception creating federated identity credential");
        }
    }

    private async Task<string?> LookupServicePrincipalAsync(
        string tenantId,
        string blueprintId,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Looking up service principal for blueprint appId: {BlueprintId}", blueprintId);
            var doc = await _graphApiService.GraphGetAsync(tenantId, $"/v1.0/servicePrincipals?$filter=appId eq '{blueprintId}'", cancellationToken);

            if (doc == null)
            {
                _logger.LogError("Failed to lookup service principal for blueprint {BlueprintId}. Graph API request failed.", blueprintId);
                return null;
            }

            if (doc.RootElement.TryGetProperty("value", out var value) && value.GetArrayLength() > 0)
            {
                var sp = value[0];
                if (sp.TryGetProperty("id", out var id))
                {
                    var spObjectId = id.GetString();
                    _logger.LogDebug("Found service principal with objectId: {SpObjectId}", spObjectId);
                    return spObjectId;
                }
            }

            _logger.LogWarning("No service principal found for blueprint appId {BlueprintId}. The blueprint's service principal must be created before publish.", blueprintId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception looking up service principal for blueprint {BlueprintId}", blueprintId);
            return null;
        }
    }

    private async Task<string?> LookupMicrosoftGraphServicePrincipalAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            string msGraphAppId = AuthenticationConstants.MicrosoftGraphResourceAppId;
            var doc = await _graphApiService.GraphGetAsync(tenantId, $"/v1.0/servicePrincipals?$filter=appId eq '{msGraphAppId}'&$select=id,appId,displayName", cancellationToken);

            if (doc == null)
            {
                _logger.LogError("Failed to lookup Microsoft Graph service principal");
                return null;
            }

            if (doc.RootElement.TryGetProperty("value", out var value) && value.GetArrayLength() > 0)
            {
                var sp = value[0];
                if (sp.TryGetProperty("id", out var id))
                {
                    return id.GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception looking up Microsoft Graph service principal");
            return null;
        }
    }

    private async Task AssignAppRoleAsync(
        string tenantId,
        string spObjectId,
        string msGraphResourceId,
        CancellationToken cancellationToken)
    {
        try
        {
            // AgentIdUser.ReadWrite.IdentityParentedBy well-known role ID
            const string appRoleId = "4aa6e624-eee0-40ab-bdd8-f9639038a614";

            // Check if role assignment already exists
            var existing = await _graphApiService.GraphGetAsync(tenantId, $"/v1.0/servicePrincipals/{spObjectId}/appRoleAssignments", cancellationToken);

            if (existing != null && existing.RootElement.TryGetProperty("value", out var assignments))
            {
                foreach (var assignment in assignments.EnumerateArray())
                {
                    var resourceId = assignment.TryGetProperty("resourceId", out var r) ? r.GetString() : null;
                    var roleId = assignment.TryGetProperty("appRoleId", out var ar) ? ar.GetString() : null;

                    if (resourceId == msGraphResourceId && roleId == appRoleId)
                    {
                        _logger.LogInformation("Microsoft Graph permissions already configured");
                        return;
                    }
                }
            }

            // Create new app role assignment
            var payload = new
            {
                principalId = spObjectId,
                resourceId = msGraphResourceId,
                appRoleId = appRoleId
            };

            var created = await _graphApiService.GraphPostAsync(tenantId, $"/v1.0/servicePrincipals/{spObjectId}/appRoleAssignments", payload, cancellationToken);

            if (created == null)
            {
                _logger.LogWarning("Failed to grant Microsoft Graph permissions (continuing anyway)");
                return;
            }

            _logger.LogInformation("Microsoft Graph permissions granted successfully");
        }
        catch (Exception ex)
        {
            // Check if this is the known agent application limitation
            if (ex.Message.Contains("Service principals of agent applications cannot be set as the source type", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("App role assignment skipped: Agent applications have restrictions");
                _logger.LogInformation("Agent application permissions should be configured through admin consent URLs");
                return;
            }

            _logger.LogWarning(ex, "Exception assigning app role (continuing anyway)");
        }
    }

    private static string Base64UrlEncode(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            throw new ArgumentException("Data cannot be null or empty", nameof(data));
        }

        // Convert to Base64
        var base64 = Convert.ToBase64String(data);
        
        // Make URL-safe: Remove padding and replace characters
        return base64.TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string ConstructFmiId(string tenantId, string rmaId, string manifestId)
    {
        // Parse GUIDs
        if (!Guid.TryParse(tenantId, out var tenantGuid))
        {
            throw new ArgumentException($"Invalid tenant ID format: {tenantId}", nameof(tenantId));
        }

        if (!Guid.TryParse(rmaId, out var rmaGuid))
        {
            throw new ArgumentException($"Invalid RMA/App ID format: {rmaId}", nameof(rmaId));
        }

        // Encode GUIDs as Base64URL
        var tenantIdEncoded = Base64UrlEncode(tenantGuid.ToByteArray());
        var rmaIdEncoded = Base64UrlEncode(rmaGuid.ToByteArray());

        // Construct the FMI namespace
        var fmiNamespace = $"/eid1/c/pub/t/{tenantIdEncoded}/a/{rmaIdEncoded}";

        if (string.IsNullOrWhiteSpace(manifestId))
        {
            return fmiNamespace;
        }

        // Convert manifestId to Base64URL - this is what MOS will do when impersonating
        var manifestIdBytes = Encoding.UTF8.GetBytes(manifestId);
        var fmiPath = Base64UrlEncode(manifestIdBytes);

        return $"{fmiNamespace}/{fmiPath}";
    }
}
