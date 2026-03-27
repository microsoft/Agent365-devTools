// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Validates that a client app exists and has the required permissions for a365 CLI operations.
/// Uses GraphApiService for direct HTTP calls to Microsoft Graph, eliminating az-subprocess overhead
/// (~20-30s per call) from the requirements check phase.
/// </summary>
public sealed class ClientAppValidator : IClientAppValidator
{
    private readonly ILogger<ClientAppValidator> _logger;
    private readonly GraphApiService _graphApiService;
    private readonly IConfirmationProvider? _confirmationProvider;

    public ClientAppValidator(ILogger<ClientAppValidator> logger, GraphApiService graphApiService, IConfirmationProvider? confirmationProvider = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _graphApiService = graphApiService ?? throw new ArgumentNullException(nameof(graphApiService));
        _confirmationProvider = confirmationProvider;
    }

    /// <summary>
    /// Ensures the client app exists and has required permissions granted.
    /// Throws ClientAppValidationException if validation fails.
    /// Does not log - caller is responsible for error presentation.
    /// </summary>
    /// <param name="clientAppId">The client app ID to validate</param>
    /// <param name="tenantId">The tenant ID where the app should exist</param>
    /// <param name="skipConfirmation">When true, applies any required app registration fixes without prompting the user.
    /// Use for non-interactive or CI scenarios. Defaults to false (prompt before modifying the app registration).</param>
    /// <param name="ct">Cancellation token</param>
    /// <exception cref="ClientAppValidationException">Thrown when validation fails</exception>
    public async Task EnsureValidClientAppAsync(
        string clientAppId,
        string tenantId,
        bool skipConfirmation = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Step 1: Validate GUID format
        if (!Guid.TryParse(clientAppId, out _))
        {
            throw ClientAppValidationException.ValidationFailed(
                $"clientAppId must be a valid GUID format (received: {clientAppId})",
                new List<string>(),
                clientAppId);
        }

        if (!Guid.TryParse(tenantId, out _))
        {
            throw ClientAppValidationException.ValidationFailed(
                $"tenantId must be a valid GUID format (received: {tenantId})",
                new List<string>(),
                clientAppId);
        }

        try
        {
            // Step 2: Verify app exists (token acquisition is handled inside GraphApiService)
            var appInfo = await GetClientAppInfoAsync(clientAppId, tenantId, ct);
            if (appInfo == null)
            {
                throw ClientAppValidationException.AppNotFound(clientAppId, tenantId);
            }

            _logger.LogDebug("Found client app: {DisplayName} ({AppId})", appInfo.DisplayName, clientAppId);

            // Step 3: Validate permissions in manifest (read-only)
            var missingPermissions = await ValidatePermissionsConfiguredAsync(appInfo, tenantId, ct);

            // Step 3.5: For any unresolvable permissions (beta APIs), check oauth2PermissionGrants as fallback
            if (missingPermissions.Count > 0)
            {
                var consentedPermissions = await GetConsentedPermissionsAsync(clientAppId, tenantId, ct);
                // Remove permissions that have been consented even if not in app registration
                missingPermissions.RemoveAll(p => consentedPermissions.Contains(p, StringComparer.OrdinalIgnoreCase));

                if (consentedPermissions.Count > 0)
                {
                    _logger.LogDebug("Found {Count} consented permissions via oauth2PermissionGrants (including beta APIs)", consentedPermissions.Count);
                }
            }

            // Read-only pre-flight: collect what redirect URIs and public client settings need fixing
            var missingRedirectUris = await CollectMissingRedirectUrisAsync(clientAppId, tenantId, ct);
            var publicClientNeedsEnabling = await IsPublicClientFlowsDisabledAsync(clientAppId, tenantId, ct);

            // Determine what mutations are needed
            bool hasMissingPermissions = missingPermissions.Count > 0;
            bool hasMissingRedirectUris = missingRedirectUris.Count > 0;
            bool needsPublicClientEnabled = publicClientNeedsEnabling;
            bool hasPendingMutations = hasMissingPermissions || hasMissingRedirectUris || needsPublicClientEnabled;

            // Prompt the user before making any changes (unless skipConfirmation or no confirmation provider)
            bool applyFixes = true;
            if (hasPendingMutations && _confirmationProvider != null && !skipConfirmation)
            {
                _logger.LogInformation("The following changes will be applied to app registration ({AppId}):", clientAppId);
                _logger.LogInformation("");
                if (hasMissingPermissions)
                {
                    _logger.LogInformation("  - Add permissions and grant admin consent:");
                    foreach (var perm in missingPermissions)
                        _logger.LogInformation("      {Permission}", perm);
                }
                if (hasMissingRedirectUris)
                {
                    _logger.LogInformation("  - Add redirect URIs:");
                    foreach (var uri in missingRedirectUris)
                        _logger.LogInformation("      {Uri}", uri);
                }
                if (needsPublicClientEnabled)
                    _logger.LogInformation("  - Enable 'Allow public client flows' (required for device code fallback)");
                _logger.LogInformation("For more information: https://learn.microsoft.com/en-us/microsoft-agent-365/developer/custom-client-app-registration");
                _logger.LogInformation("");

                applyFixes = await _confirmationProvider.ConfirmAsync("Do you want to proceed? (y/N): ");
                if (!applyFixes)
                {
                    _logger.LogInformation("App registration was not modified. Re-run and accept the prompt, or configure manually.");
                }
            }

            // Step 3.6: Auto-provision any remaining missing permissions (self-healing)
            if (applyFixes && missingPermissions.Count > 0)
            {
                _logger.LogInformation("Auto-provisioning {Count} missing permission(s): {Permissions}",
                    missingPermissions.Count, string.Join(", ", missingPermissions));

                var provisioned = await EnsurePermissionsConfiguredAsync(appInfo, missingPermissions, clientAppId, tenantId, ct);

                if (provisioned)
                {
                    // Re-fetch fresh app info and re-validate to confirm provisioning succeeded
                    var freshAppInfo = await GetClientAppInfoAsync(clientAppId, tenantId, ct);
                    if (freshAppInfo != null)
                    {
                        missingPermissions = await ValidatePermissionsConfiguredAsync(freshAppInfo, tenantId, ct);

                        // Re-run the consent fallback check on the remaining missing list
                        if (missingPermissions.Count > 0)
                        {
                            var consentedAfterProvision = await GetConsentedPermissionsAsync(clientAppId, tenantId, ct);
                            missingPermissions.RemoveAll(p => consentedAfterProvision.Contains(p, StringComparer.OrdinalIgnoreCase));
                        }
                    }
                }
            }

            if (missingPermissions.Count > 0)
            {
                throw ClientAppValidationException.MissingPermissions(clientAppId, missingPermissions);
            }

            // Step 4: Verify admin consent
            if (!await ValidateAdminConsentAsync(clientAppId, tenantId, ct))
            {
                throw ClientAppValidationException.MissingAdminConsent(clientAppId);
            }

            // Step 5: Verify and fix redirect URIs
            if (applyFixes)
                await EnsureRedirectUrisAsync(clientAppId, tenantId, ct);

            // Step 6: Verify and fix public client flows (required for device code fallback)
            if (applyFixes)
                await EnsurePublicClientFlowsEnabledAsync(clientAppId, tenantId, ct);

            _logger.LogDebug("Client app validation successful for {ClientAppId}", clientAppId);
        }
        catch (ClientAppValidationException)
        {
            // Re-throw validation exceptions as-is
            throw;
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C / cancellation — propagate immediately without wrapping
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "JSON parsing error during validation");
            throw ClientAppValidationException.ValidationFailed(
                "Failed to parse Microsoft Graph response",
                new List<string> { ex.Message },
                clientAppId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unexpected error during validation");
            throw ClientAppValidationException.ValidationFailed(
                "Unexpected error during client app validation",
                new List<string> { ex.Message },
                clientAppId);
        }
    }

    /// <summary>
    /// Ensures the client app has required redirect URIs configured for Microsoft Graph PowerShell SDK.
    /// Automatically adds missing redirect URIs if needed (self-healing).
    /// </summary>
    /// <param name="clientAppId">The client app ID</param>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="ct">Cancellation token</param>
    public async Task EnsureRedirectUrisAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        try
        {
            _logger.LogDebug("Checking redirect URIs for client app {ClientAppId}", clientAppId);

            using var appDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/applications?$filter=appId eq '{clientAppId}'&$select=id,publicClient", ct);

            if (appDoc == null)
            {
                _logger.LogWarning("Could not verify redirect URIs: Graph request failed");
                return;
            }

            var response = JsonNode.Parse(appDoc.RootElement.GetRawText());
            var apps = response?["value"]?.AsArray();

            if (apps == null || apps.Count == 0)
            {
                _logger.LogWarning("Client app not found when checking redirect URIs");
                return;
            }

            var app = apps[0]!.AsObject();
            var objectId = app["id"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(objectId))
            {
                _logger.LogWarning("Could not get application object ID for redirect URI update");
                return;
            }

            var publicClient = app["publicClient"]?.AsObject();
            var currentRedirectUris = publicClient?["redirectUris"]?.AsArray()
                ?.Select(uri => uri?.GetValue<string>())
                .Where(uri => !string.IsNullOrWhiteSpace(uri))
                .Select(uri => uri!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Check if required URIs are present (including WAM broker URI)
            var requiredUris = AuthenticationConstants.GetRequiredRedirectUris(clientAppId);
            var missingUris = requiredUris
                .Where(uri => !currentRedirectUris.Contains(uri))
                .ToList();

            if (missingUris.Count == 0)
            {
                _logger.LogDebug("All required redirect URIs are configured");
                return;
            }

            // Add missing URIs
            _logger.LogInformation("Adding missing redirect URIs to client app: {MissingUris}",
                string.Join(", ", missingUris));

            var allUris = currentRedirectUris.Union(missingUris).ToList();
            var urisArray = new JsonArray();
            foreach (var uri in allUris)
                urisArray.Add(JsonValue.Create(uri));

            var patchSuccess = await _graphApiService.GraphPatchAsync(tenantId,
                $"/v1.0/applications/{objectId}",
                new JsonObject { ["publicClient"] = new JsonObject { ["redirectUris"] = urisArray } },
                ct);

            if (!patchSuccess)
            {
                _logger.LogWarning("Failed to update redirect URIs");
                return;
            }

            _logger.LogInformation("Successfully added redirect URIs: {AddedUris}",
                string.Join(", ", missingUris));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error ensuring redirect URIs (non-fatal)");
        }
    }

    /// <summary>
    /// Ensures the app registration has "Allow public client flows" enabled.
    /// This setting is required for MSAL device code authentication fallback on non-Windows
    /// platforms where interactive browser auth is unavailable (macOS headless, Linux, WSL).
    /// Automatically enables it if disabled (self-healing).
    /// </summary>
    private async Task EnsurePublicClientFlowsEnabledAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug("Checking 'Allow public client flows' for client app {ClientAppId}", clientAppId);

            using var appDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/applications?$filter=appId eq '{clientAppId}'&$select=id,isFallbackPublicClient", ct);

            if (appDoc == null)
            {
                _logger.LogWarning("Could not check 'Allow public client flows': Graph request failed");
                return;
            }

            var response = JsonNode.Parse(appDoc.RootElement.GetRawText());
            var apps = response?["value"]?.AsArray();

            if (apps == null || apps.Count == 0)
            {
                _logger.LogWarning("Client app not found when checking 'Allow public client flows'");
                return;
            }

            var app = apps[0]!.AsObject();
            var objectId = app["id"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(objectId))
            {
                _logger.LogWarning("Could not get application object ID when checking 'Allow public client flows'");
                return;
            }

            var isFallbackPublicClient = app["isFallbackPublicClient"]?.GetValue<bool>() ?? false;
            if (isFallbackPublicClient)
            {
                _logger.LogDebug("'Allow public client flows' is already enabled");
                return;
            }

            _logger.LogInformation(
                "Enabling 'Allow public client flows' on app registration " +
                "(required for device code authentication fallback on macOS, Linux, WSL, " +
                "headless environments, and as a Conditional Access Policy fallback on Windows).");
            _logger.LogInformation("Run 'a365 setup requirements' at any time to re-verify and auto-fix this setting.");

            var patchSuccess = await _graphApiService.GraphPatchAsync(tenantId,
                $"/v1.0/applications/{objectId}",
                new { isFallbackPublicClient = true },
                ct);

            if (!patchSuccess)
            {
                _logger.LogWarning("Failed to enable 'Allow public client flows'");
                return;
            }

            _logger.LogInformation("Successfully enabled 'Allow public client flows' on app registration.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error ensuring 'Allow public client flows' is enabled (non-fatal)");
        }
    }

    /// <summary>
    /// Auto-provisions missing permissions onto the client app registration (self-healing).
    /// Patches requiredResourceAccess to add missing permission GUIDs, then tries to extend
    /// the existing oauth2PermissionGrant scope so the consent is effective immediately.
    /// Returns true if the requiredResourceAccess patch succeeded; false if it could not be applied.
    /// </summary>
    private async Task<bool> EnsurePermissionsConfiguredAsync(
        ClientAppInfo appInfo,
        List<string> missingPermissions,
        string clientAppId,
        string tenantId,
        CancellationToken ct)
    {
        try
        {
            // Resolve permission GUIDs for the missing permission names
            var permissionNameToIdMap = await ResolvePermissionIdsAsync(tenantId, ct);

            // Build an updated requiredResourceAccess array, inserting the missing GUIDs
            // into (or alongside) the Microsoft Graph resource entry.
            var updatedResourceAccess = new JsonArray();
            bool graphEntryFound = false;

            if (appInfo.RequiredResourceAccess != null)
            {
                foreach (var resourceNode in appInfo.RequiredResourceAccess)
                {
                    var resourceObj = resourceNode?.AsObject();
                    if (resourceObj == null) continue;

                    var resourceAppId = resourceObj["resourceAppId"]?.GetValue<string>();
                    if (string.Equals(resourceAppId, AuthenticationConstants.MicrosoftGraphResourceAppId, StringComparison.OrdinalIgnoreCase))
                    {
                        graphEntryFound = true;

                        // Collect existing permission IDs
                        var existingAccess = resourceObj["resourceAccess"]?.AsArray();
                        var existingIds = existingAccess?
                            .Select(a => a?.AsObject()?["id"]?.GetValue<string>())
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Select(id => id!)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase)
                            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        // Clone existing entries
                        var newAccess = new JsonArray();
                        if (existingAccess != null)
                        {
                            foreach (var item in existingAccess)
                                newAccess.Add(item?.DeepClone());
                        }

                        // Append each missing permission that could be resolved
                        foreach (var permName in missingPermissions)
                        {
                            if (permissionNameToIdMap.TryGetValue(permName, out var permId)
                                && !existingIds.Contains(permId))
                            {
                                newAccess.Add(new JsonObject
                                {
                                    ["id"] = permId,
                                    ["type"] = "Scope"
                                });
                                _logger.LogDebug("Staging permission for manifest: {Permission} ({Id})", permName, permId);
                            }
                        }

                        updatedResourceAccess.Add(new JsonObject
                        {
                            ["resourceAppId"] = AuthenticationConstants.MicrosoftGraphResourceAppId,
                            ["resourceAccess"] = newAccess
                        });
                    }
                    else
                    {
                        updatedResourceAccess.Add(resourceNode?.DeepClone());
                    }
                }
            }

            if (!graphEntryFound)
            {
                // No existing Microsoft Graph entry — create one from scratch
                var newAccess = new JsonArray();
                foreach (var permName in missingPermissions)
                {
                    if (permissionNameToIdMap.TryGetValue(permName, out var permId))
                    {
                        newAccess.Add(new JsonObject
                        {
                            ["id"] = permId,
                            ["type"] = "Scope"
                        });
                    }
                }
                updatedResourceAccess.Add(new JsonObject
                {
                    ["resourceAppId"] = AuthenticationConstants.MicrosoftGraphResourceAppId,
                    ["resourceAccess"] = newAccess
                });
            }

            var patchSuccess = await _graphApiService.GraphPatchAsync(tenantId,
                $"/v1.0/applications/{appInfo.ObjectId}",
                new JsonObject { ["requiredResourceAccess"] = updatedResourceAccess },
                ct);

            if (!patchSuccess)
            {
                _logger.LogWarning("Failed to update app registration with missing permissions");
                return false;
            }

            _logger.LogInformation("Added {Count} permission(s) to app registration: {Permissions}",
                missingPermissions.Count, string.Join(", ", missingPermissions));

            // Best-effort: also extend the existing oauth2PermissionGrant so consent takes effect immediately
            await TryExtendConsentGrantScopesAsync(clientAppId, missingPermissions, tenantId, ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error auto-provisioning permissions (non-fatal): {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Best-effort: appends new scope names to the existing oauth2PermissionGrant so that the
    /// delegated consent is effective without requiring a fresh admin consent flow.
    /// Silently logs and returns on any failure.
    /// </summary>
    private async Task TryExtendConsentGrantScopesAsync(
        string clientAppId,
        List<string> newScopes,
        string tenantId,
        CancellationToken ct)
    {
        try
        {
            // Look up the service principal for the client app
            using var spDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/servicePrincipals?$filter=appId eq '{clientAppId}'&$select=id", ct);

            if (spDoc == null) return;

            var spJson = JsonNode.Parse(spDoc.RootElement.GetRawText());
            var spObjectId = spJson?["value"]?.AsArray().FirstOrDefault()?.AsObject()["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(spObjectId)) return;

            // Find the oauth2PermissionGrant that targets Microsoft Graph
            using var grantsDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{spObjectId}'", ct);

            if (grantsDoc == null) return;

            var grantsJson = JsonNode.Parse(grantsDoc.RootElement.GetRawText());
            var grants = grantsJson?["value"]?.AsArray();
            if (grants == null) return;

            // Look up the Microsoft Graph service principal ID to match against resourceId
            string? graphSpObjectId = null;
            using var graphSpDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/servicePrincipals?$filter=appId eq '{AuthenticationConstants.MicrosoftGraphResourceAppId}'&$select=id", ct);

            if (graphSpDoc != null)
            {
                var graphSpJson = JsonNode.Parse(graphSpDoc.RootElement.GetRawText());
                graphSpObjectId = graphSpJson?["value"]?.AsArray().FirstOrDefault()?.AsObject()["id"]?.GetValue<string>();
            }

            foreach (var grantNode in grants)
            {
                var grant = grantNode?.AsObject();
                if (grant == null) continue;

                var grantId = grant["id"]?.GetValue<string>();
                var resourceId = grant["resourceId"]?.GetValue<string>();
                var existingScope = grant["scope"]?.GetValue<string>() ?? string.Empty;

                // Match on the Microsoft Graph resource (by SP object ID if available, always fallback to scope content)
                bool isGraphGrant = (!string.IsNullOrWhiteSpace(graphSpObjectId) &&
                                     string.Equals(resourceId, graphSpObjectId, StringComparison.OrdinalIgnoreCase))
                                    || AuthenticationConstants.RequiredClientAppPermissions
                                        .Any(p => existingScope.Contains(p, StringComparison.OrdinalIgnoreCase));

                if (!isGraphGrant || string.IsNullOrWhiteSpace(grantId)) continue;

                // Append any scopes not already in the grant
                var existingScopes = existingScope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var scopesToAdd = newScopes.Where(s => !existingScopes.Contains(s)).ToList();
                if (scopesToAdd.Count == 0) continue;

                var updatedScope = string.Join(' ', existingScopes.Concat(scopesToAdd));

                var patchSuccess = await _graphApiService.GraphPatchAsync(tenantId,
                    $"/v1.0/oauth2PermissionGrants/{grantId}",
                    new { scope = updatedScope },
                    ct);

                if (patchSuccess)
                {
                    _logger.LogInformation("Extending admin consent grant with {Count} new permission(s): {Scopes}.",
                        scopesToAdd.Count, string.Join(", ", scopesToAdd));
                    // Invalidate the process-level az CLI token cache so the next Graph call
                    // re-acquires a token that includes the newly consented scope(s).
                    Services.Helpers.AzCliHelper.InvalidateAzCliTokenCache();
                }
                else
                {
                    _logger.LogDebug("Could not extend consent grant (may require admin role)");
                }

                break; // Only one grant per resource
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TryExtendConsentGrantScopesAsync failed (non-fatal): {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Returns the subset of <see cref="AuthenticationConstants.RequiredClientAppPermissions"/>
    /// that are not yet present in the client app's oauth2PermissionGrant (i.e. not consented).
    /// </summary>
    public async Task<List<string>> GetUnconsentedRequiredPermissionsAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct = default)
    {
        var consented = await GetConsentedPermissionsAsync(clientAppId, tenantId, ct);
        return AuthenticationConstants.RequiredClientAppPermissions
            .Where(p => !consented.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Extends the client app's oauth2PermissionGrant to include the specified permissions.
    /// Call after the user has confirmed they want to grant admin consent.
    /// </summary>
    public Task GrantConsentForPermissionsAsync(
        string clientAppId,
        List<string> permissions,
        string tenantId,
        CancellationToken ct = default)
        => TryExtendConsentGrantScopesAsync(clientAppId, permissions, tenantId, ct);


    /// <summary>
    /// Read-only check: returns the redirect URIs that are missing from the app registration
    /// without making any changes. Used to build the pre-flight mutation summary.
    /// </summary>
    private async Task<List<string>> CollectMissingRedirectUrisAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct)
    {
        try
        {
            using var appDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/applications?$filter=appId eq '{clientAppId}'&$select=id,publicClient", ct);

            if (appDoc == null) return new List<string>();

            var response = JsonNode.Parse(appDoc.RootElement.GetRawText());
            var apps = response?["value"]?.AsArray();
            if (apps == null || apps.Count == 0) return new List<string>();

            var publicClient = apps[0]!.AsObject()["publicClient"]?.AsObject();
            var currentRedirectUris = publicClient?["redirectUris"]?.AsArray()
                ?.Select(uri => uri?.GetValue<string>())
                .Where(uri => !string.IsNullOrWhiteSpace(uri))
                .Select(uri => uri!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return AuthenticationConstants.GetRequiredRedirectUris(clientAppId)
                .Where(uri => !currentRedirectUris.Contains(uri))
                .ToList();
        }
        catch (Exception ex)
        {
            // On error, assume all redirect URIs are missing so the prompt still appears.
            // Failing closed (prompt) is safer than failing open (silent mutation without disclosure).
            _logger.LogDebug("CollectMissingRedirectUrisAsync failed — assuming all redirect URIs missing: {Message}", ex.Message);
            return AuthenticationConstants.GetRequiredRedirectUris(clientAppId).ToList();
        }
    }

    /// <summary>
    /// Read-only check: returns true if 'Allow public client flows' (isFallbackPublicClient)
    /// is currently disabled on the app registration, without making any changes.
    /// </summary>
    private async Task<bool> IsPublicClientFlowsDisabledAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct)
    {
        try
        {
            using var appDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/applications?$filter=appId eq '{clientAppId}'&$select=id,isFallbackPublicClient", ct);

            if (appDoc == null) return false;

            var response = JsonNode.Parse(appDoc.RootElement.GetRawText());
            var apps = response?["value"]?.AsArray();
            if (apps == null || apps.Count == 0) return false;

            var isFallbackPublicClient = apps[0]!.AsObject()["isFallbackPublicClient"]?.GetValue<bool>() ?? false;
            return !isFallbackPublicClient;
        }
        catch (Exception ex)
        {
            // On error, assume public client flows need enabling so the prompt still appears.
            // Failing closed (prompt) is safer than failing open (silent mutation without disclosure).
            _logger.LogDebug("IsPublicClientFlowsDisabledAsync failed — assuming public client flows need enabling: {Message}", ex.Message);
            return true;
        }
    }

    #region Private Helper Methods

    private async Task<ClientAppInfo?> GetClientAppInfoAsync(string clientAppId, string tenantId, CancellationToken ct)
    {
        _logger.LogDebug("Checking if client app exists in tenant...");

        const string path = "/v1.0/applications?$filter=appId eq '{0}'&$select=id,appId,displayName,requiredResourceAccess";
        var graphResponse = await _graphApiService.GraphGetWithResponseAsync(tenantId,
            string.Format(path, clientAppId), ct);

        if (graphResponse == null || !graphResponse.IsSuccess)
        {
            // Only retry on 401 — a stale token due to CAE revocation. Transient errors (503,
            // network failure) surface the real error to the caller rather than masking it as
            // "token revoked". StatusCode 0 means token acquisition itself failed.
            if (graphResponse?.StatusCode != 401)
            {
                _logger.LogDebug("Graph app query failed with {StatusCode} — not retrying", graphResponse?.StatusCode);
                return null;
            }

            _logger.LogDebug("Graph app query returned 401 — invalidating token cache and retrying (possible CAE revocation)");
            AzCliHelper.InvalidateAzCliTokenCache();
            graphResponse = await _graphApiService.GraphGetWithResponseAsync(tenantId,
                string.Format(path, clientAppId), ct);

            if (!graphResponse.IsSuccess)
                throw ClientAppValidationException.TokenRevoked(clientAppId);
        }

        using var doc = graphResponse.Json;
        if (doc == null) return null;

        var response = JsonNode.Parse(doc.RootElement.GetRawText());
        var apps = response?["value"]?.AsArray();
        if (apps == null || apps.Count == 0) return null;

        var app = apps[0]!.AsObject();
        return new ClientAppInfo(
            app["id"]?.GetValue<string>() ?? string.Empty,
            app["displayName"]?.GetValue<string>() ?? string.Empty,
            app["requiredResourceAccess"]?.AsArray());
    }

    private async Task<List<string>> ValidatePermissionsConfiguredAsync(
        ClientAppInfo appInfo,
        string tenantId,
        CancellationToken ct)
    {
        var missingPermissions = new List<string>();

        if (appInfo.RequiredResourceAccess == null || appInfo.RequiredResourceAccess.Count == 0)
        {
            return AuthenticationConstants.RequiredClientAppPermissions.ToList();
        }

        // Find Microsoft Graph resource in required permissions
        var graphResource = appInfo.RequiredResourceAccess
            .Select(r => r?.AsObject())
            .FirstOrDefault(obj => obj?["resourceAppId"]?.GetValue<string>() == AuthenticationConstants.MicrosoftGraphResourceAppId);

        if (graphResource == null)
        {
            return AuthenticationConstants.RequiredClientAppPermissions.ToList();
        }

        var resourceAccess = graphResource["resourceAccess"]?.AsArray();
        if (resourceAccess == null || resourceAccess.Count == 0)
        {
            return AuthenticationConstants.RequiredClientAppPermissions.ToList();
        }

        // Build set of configured permission IDs
        var configuredPermissionIds = resourceAccess
            .Select(access => access?.AsObject())
            .Select(accessObj => new
            {
                PermissionId = accessObj?["id"]?.GetValue<string>(),
                PermissionType = accessObj?["type"]?.GetValue<string>()
            })
            .Where(x => x.PermissionType == "Scope" && !string.IsNullOrWhiteSpace(x.PermissionId))
            .Select(x => x.PermissionId!)
            .ToHashSet();

        // Resolve ALL permission IDs dynamically from Microsoft Graph
        // This ensures compatibility across different tenants and API versions
        var permissionNameToIdMap = await ResolvePermissionIdsAsync(tenantId, ct);

        // Check each required permission
        foreach (var permissionName in AuthenticationConstants.RequiredClientAppPermissions)
        {
            if (permissionNameToIdMap.TryGetValue(permissionName, out var permissionId))
            {
                if (!configuredPermissionIds.Contains(permissionId))
                {
                    missingPermissions.Add(permissionName);
                }
                _logger.LogDebug("Validated permission {PermissionName} (ID: {PermissionId})", permissionName, permissionId);
            }
            else
            {
                _logger.LogWarning("Could not resolve permission ID for: {PermissionName}", permissionName);
                _logger.LogWarning("This permission may be a beta API or unavailable in your tenant. Validation cannot verify its presence.");
                // Don't add to missing list - we can't verify it
            }
        }

        return missingPermissions;
    }

    /// <summary>
    /// Resolves permission names to their GUIDs by querying Microsoft Graph's published permission definitions.
    /// This approach is tenant-agnostic and works across different API versions.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolvePermissionIdsAsync(string tenantId, CancellationToken ct)
    {
        var permissionNameToIdMap = new Dictionary<string, string>();

        try
        {
            using var doc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/servicePrincipals?$filter=appId eq '{AuthenticationConstants.MicrosoftGraphResourceAppId}'&$select=id,oauth2PermissionScopes",
                ct);

            if (doc == null)
            {
                _logger.LogWarning("Failed to query Microsoft Graph for permission definitions");
                return permissionNameToIdMap;
            }

            var response = JsonNode.Parse(doc.RootElement.GetRawText());
            var graphSps = response?["value"]?.AsArray();

            if (graphSps == null || graphSps.Count == 0)
            {
                _logger.LogWarning("No Microsoft Graph service principal found");
                return permissionNameToIdMap;
            }

            var graphSp = graphSps[0]!.AsObject();
            var oauth2PermissionScopes = graphSp["oauth2PermissionScopes"]?.AsArray();

            if (oauth2PermissionScopes == null)
            {
                _logger.LogWarning("No permission scopes found in Microsoft Graph service principal");
                return permissionNameToIdMap;
            }

            // Build map of all available permissions (name -> GUID)
            permissionNameToIdMap = oauth2PermissionScopes
                .Select(scopeNode => scopeNode?.AsObject())
                .Select(scopeObj => new
                {
                    Value = scopeObj?["value"]?.GetValue<string>(),
                    Id = scopeObj?["id"]?.GetValue<string>()
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Value) && !string.IsNullOrWhiteSpace(x.Id))
                .ToDictionary(x => x.Value!, x => x.Id!);

            _logger.LogDebug("Retrieved {Count} permission definitions from Microsoft Graph", permissionNameToIdMap.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not retrieve Microsoft Graph permission definitions: {Message}", ex.Message);
        }

        return permissionNameToIdMap;
    }

    /// <summary>
    /// Gets the list of permissions that have been consented for the app via oauth2PermissionGrants.
    /// This is used as a fallback for beta permissions that may not be visible in the app registration's requiredResourceAccess.
    /// </summary>
    private async Task<HashSet<string>> GetConsentedPermissionsAsync(string clientAppId, string tenantId, CancellationToken ct)
    {
        var consentedPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Get service principal for the app
            using var spDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/servicePrincipals?$filter=appId eq '{clientAppId}'&$select=id", ct);

            if (spDoc == null)
            {
                _logger.LogDebug("Could not query service principal for consent check");
                return consentedPermissions;
            }

            var spJson = JsonNode.Parse(spDoc.RootElement.GetRawText());
            var servicePrincipals = spJson?["value"]?.AsArray();

            if (servicePrincipals == null || servicePrincipals.Count == 0)
            {
                _logger.LogDebug("Service principal not found for consent check");
                return consentedPermissions;
            }

            var sp = servicePrincipals[0]!.AsObject();
            var spObjectId = sp["id"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(spObjectId))
            {
                return consentedPermissions;
            }

            // Get oauth2PermissionGrants
            using var grantsDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{spObjectId}'", ct);

            if (grantsDoc == null)
            {
                _logger.LogDebug("Could not query oauth2PermissionGrants");
                return consentedPermissions;
            }

            var grantsJson = JsonNode.Parse(grantsDoc.RootElement.GetRawText());
            var grants = grantsJson?["value"]?.AsArray();

            if (grants == null || grants.Count == 0)
            {
                return consentedPermissions;
            }

            // Extract all scopes from grants
            foreach (var grant in grants)
            {
                var grantObj = grant?.AsObject();
                var scope = grantObj?["scope"]?.GetValue<string>();

                if (!string.IsNullOrWhiteSpace(scope))
                {
                    var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var s in scopes)
                    {
                        consentedPermissions.Add(s);
                    }
                }
            }

            _logger.LogDebug("Found {Count} consented permissions from oauth2PermissionGrants", consentedPermissions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error retrieving consented permissions: {Message}", ex.Message);
        }

        return consentedPermissions;
    }

    private async Task<bool> ValidateAdminConsentAsync(string clientAppId, string tenantId, CancellationToken ct)
    {
        _logger.LogDebug("Checking admin consent status for {ClientAppId}", clientAppId);

        // Get service principal for the app
        using var spDoc = await _graphApiService.GraphGetAsync(tenantId,
            $"/v1.0/servicePrincipals?$filter=appId eq '{clientAppId}'&$select=id,appId", ct);

        if (spDoc == null)
        {
            _logger.LogDebug("Could not verify service principal (may not exist yet)");
            return true; // Best-effort check - will be verified during first interactive authentication
        }

        var spJson = JsonNode.Parse(spDoc.RootElement.GetRawText());
        var servicePrincipals = spJson?["value"]?.AsArray();

        if (servicePrincipals == null || servicePrincipals.Count == 0)
        {
            _logger.LogDebug("Service principal not created yet for this app");
            return true; // Best-effort check - will be verified during first interactive authentication
        }

        var sp = servicePrincipals[0]!.AsObject();
        var spObjectId = sp["id"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(spObjectId))
        {
            _logger.LogDebug("Service principal object ID not found");
            return true; // Best-effort check
        }

        // Check OAuth2 permission grants
        using var grantsDoc = await _graphApiService.GraphGetAsync(tenantId,
            $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{spObjectId}'", ct);

        if (grantsDoc == null)
        {
            _logger.LogDebug("Could not verify admin consent status");
            return true; // Best-effort check
        }

        var grantsJson = JsonNode.Parse(grantsDoc.RootElement.GetRawText());
        var grants = grantsJson?["value"]?.AsArray();

        if (grants == null || grants.Count == 0)
        {
            return false; // No grants found - admin consent missing
        }

        // Check if there's a grant for Microsoft Graph with required scopes
        var hasGraphGrant = grants
            .Select(grant => grant?.AsObject())
            .Select(grantObj => grantObj?["scope"]?.GetValue<string>())
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Any(scope =>
            {
                var grantedScopes = scope!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var foundPermissions = AuthenticationConstants.RequiredClientAppPermissions
                    .Intersect(grantedScopes, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (foundPermissions.Count > 0)
                {
                    _logger.LogDebug("Admin consent verified for {Count} permissions", foundPermissions.Count);
                    return true;
                }
                return false;
            });

        return hasGraphGrant;
    }

    #endregion

    #region Helper Types

    private record ClientAppInfo(string ObjectId, string DisplayName, JsonArray? RequiredResourceAccess);

    #endregion
}
