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
        if (!Guid.TryParse(clientAppId, out var parsedClientAppId))
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
            // Never run tenant-owned mutation logic against Microsoft's application registration.
            if (AuthenticationConstants.IsWellKnownFirstPartyClientApp(clientAppId))
            {
                await EnsureValidFirstPartyClientAppAsync(parsedClientAppId.ToString("D"), tenantId, ct);
                return;
            }

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
            var widsClaimMissing = await IsWidsOptionalClaimMissingAsync(clientAppId, tenantId, ct);

            // Check whether the existing consent grant is per-user (Principal) rather than tenant-wide (AllPrincipals).
            // A Principal grant only covers the specific admin who first consented; other users (e.g. developers
            // running blueprint creation) see "Need admin approval" even though permissions are technically granted.
            bool needsConsentUpgrade = await HasPrincipalOnlyConsentGrantAsync(clientAppId, tenantId, ct);

            // Determine what mutations are needed
            bool hasMissingPermissions = missingPermissions.Count > 0;
            bool hasMissingRedirectUris = missingRedirectUris.Count > 0;
            bool needsPublicClientEnabled = publicClientNeedsEnabling;
            bool needsWidsClaim = widsClaimMissing;
            bool hasPendingMutations = hasMissingPermissions || hasMissingRedirectUris || needsPublicClientEnabled || needsConsentUpgrade || needsWidsClaim;

            // Prompt the user before making any changes (unless skipConfirmation or no confirmation provider)
            bool applyFixes = true;
            // True when admin status was confirmed by successfully PATCHing the wids optional claim
            // (the Unknown-path probe). We then short-circuit the wids fix later — already applied.
            bool widsAlreadyApplied = false;
            if (hasPendingMutations && _confirmationProvider != null && !skipConfirmation)
            {
                // Determine the signed-in user's directory role via the wids claim on the MSAL token.
                // Branches:
                //   HasRole          → existing prompt + apply-fixes path.
                //   DoesNotHaveRole  → existing non-admin guidance.
                //   Unknown          → wids isn't on the token yet. Skip the prompt and use the wids
                //                      PATCH itself as the admin probe (an admin succeeds with 2xx;
                //                      a non-admin fails with 403/Authorization_RequestDenied).
                //                      Anything else surfaces as the real error.
                var roleCheck = await _graphApiService.IsCurrentUserAdminAsync(tenantId, ct);

                if (roleCheck == Models.RoleCheckResult.Unknown && needsWidsClaim)
                {
                    _logger.LogInformation("Cannot determine admin role from the access token (the 'wids' optional claim is missing). Attempting the wids fix as an admin probe — a successful PATCH confirms admin authority; a 403 reveals non-admin.");
                    var probe = await TryProbeAdminViaWidsPatchAsync(clientAppId, tenantId, ct);
                    if (probe == ProbeResult.Admin)
                    {
                        widsAlreadyApplied = true;
                        // The PATCH that just succeeded invalidates the cached access token (it
                        // lacks the new wids claim). Cache clear is performed inside the patch helper
                        // (see EnsureWidsOptionalClaimAsync). Subsequent calls re-acquire silently
                        // via WAM/refresh-token and receive tokens that carry wids.
                    }
                    else
                    {
                        // Both NotAdmin and Inconclusive fall through to the existing non-admin guidance.
                        // Inconclusive means a transient error during the probe — converting that to a
                        // hard ClientAppValidationException turns a network blip into a worse failure
                        // than the baseline Unknown path, with no actionable hint for the operator.
                        // Routing both to the non-admin branch surfaces the standard 3-option guidance
                        // (run as GA / portal / consent URL), which is correct for either case.
                        roleCheck = Models.RoleCheckResult.DoesNotHaveRole;
                    }
                }

                bool isAdmin = roleCheck == Models.RoleCheckResult.HasRole || widsAlreadyApplied;
                if (!isAdmin)
                {
                    _logger.LogDebug("User does not have admin privileges to modify app registration — skipping auto-provision prompt");
                    var missingDetails = new List<string>();
                    if (hasMissingPermissions)
                        missingDetails.Add($"Missing permissions: {string.Join(", ", missingPermissions)}");
                    if (hasMissingRedirectUris)
                        missingDetails.Add($"Missing redirect URIs: {string.Join(", ", missingRedirectUris)}");
                    if (needsPublicClientEnabled)
                        missingDetails.Add("Public client flows ('Allow public client flows') must be enabled");
                    if (needsConsentUpgrade)
                        missingDetails.Add("OAuth2 consent grant must be upgraded from per-user (Principal) to tenant-wide (AllPrincipals)");
                    if (needsWidsClaim)
                        missingDetails.Add("'wids' optional claim missing on access tokens — without it, role detection always returns Unknown and the AllPrincipals grant phase silently skips, leaving the agent blueprint with no permissions granted on its service principal");
                    var consentUrl = ClientAppValidationException.BuildAdminConsentUrl(clientAppId, tenantId, _graphApiService.AuthorityHost);
                    var steps = new List<string>
                    {
                        "Next Steps — Global Administrator action required:",
                        "  Option 1 (recommended) — Have a Global Administrator run the CLI:",
                        "    a365 setup requirements",
                        "    (This is the only path that fixes the 'wids' optional claim. The consent URL below does NOT add 'wids'.)"
                    };
                    if (needsWidsClaim)
                    {
                        steps.Add("  Option 2 — Have a Global Administrator add 'wids' manually in the Azure portal:");
                        steps.Add($"    App registrations > <Agent 365 CLI app> > Token configuration > Add optional claim > Access > wids");
                    }
                    if (consentUrl != null && (hasMissingPermissions || needsConsentUpgrade))
                    {
                        steps.Add("  Option 3 — Share this consent URL with your Global Administrator (covers permissions/consent grant only, NOT 'wids'):");
                        steps.Add($"    {consentUrl}");
                    }
                    throw new ClientAppValidationException(
                        issueDescription: "Client app configuration requires a Global Administrator",
                        errorDetails: missingDetails,
                        mitigationSteps: steps);
                }

                _logger.LogInformation("The following changes will be applied to app registration ({AppId}):", clientAppId);
                _logger.LogInformation("");
                if (hasMissingPermissions)
                {
                    _logger.LogInformation("  - Add permissions and grant admin consent for all users:");
                    foreach (var perm in missingPermissions)
                        _logger.LogInformation("      {Permission}", perm);
                }
                if (needsConsentUpgrade)
                {
                    _logger.LogInformation("  - Upgrade consent grant from per-user to tenant-wide (AllPrincipals)");
                    _logger.LogInformation("    This allows all users in the tenant to use the CLI without individual consent prompts.");
                    _logger.LogInformation("    (Required for multi-user workflows: admin runs setup, developer runs blueprint creation)");
                }
                if (hasMissingRedirectUris)
                {
                    _logger.LogInformation("  - Add redirect URIs:");
                    foreach (var uri in missingRedirectUris)
                        _logger.LogInformation("      {Uri}", uri);
                }
                if (needsPublicClientEnabled)
                    _logger.LogInformation("  - Enable 'Allow public client flows' (required for device code fallback)");
                if (needsWidsClaim)
                {
                    _logger.LogInformation("  - Add 'wids' optional claim to access tokens");
                    _logger.LogInformation("    (Without this, the CLI cannot detect Global Administrator role and silently skips AllPrincipals OAuth2 grants on the blueprint SP — agents created from the blueprint inherit no permissions.)");
                }
                _logger.LogInformation("For more information: https://learn.microsoft.com/en-us/microsoft-agent-365/developer/custom-client-app-registration");
                _logger.LogInformation("");

                // Skip the prompt when the Unknown-probe path already proved admin authority by
                // landing the wids PATCH (per H8 of the design). The probe IS the confirmation:
                // an admin's PATCH succeeded; a non-admin would have already been routed to the
                // non-admin error above. Prompting again would be redundant and confuses the
                // workflow (the mutation is already partially applied).
                if (widsAlreadyApplied)
                {
                    applyFixes = true;
                }
                else
                {
                    applyFixes = await _confirmationProvider.ConfirmAsync("Do you want to proceed? (y/N): ");
                }
                if (!applyFixes)
                {
                    _logger.LogInformation("App registration was not modified. Re-run and accept the prompt, or configure manually.");

                    var details = new List<string>();
                    if (hasMissingPermissions)
                        details.Add($"Missing permissions: {string.Join(", ", missingPermissions)}");
                    if (hasMissingRedirectUris)
                        details.Add($"Missing redirect URIs: {string.Join(", ", missingRedirectUris)}");
                    if (needsPublicClientEnabled)
                        details.Add("Public client flows ('Allow public client flows') must be enabled");
                    throw ClientAppValidationException.ValidationFailed(
                        "App registration changes were declined — manual configuration required",
                        details,
                        clientAppId);
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

            // Step 3.7: Upgrade consent grant from per-user to tenant-wide if needed.
            // Must run before ValidateAdminConsentAsync so the consentType check passes.
            if (applyFixes && needsConsentUpgrade)
                await UpgradeConsentGrantToAllPrincipalsAsync(clientAppId, tenantId, ct);

            // Step 4: Verify admin consent (requires AllPrincipals grant)
            if (!await ValidateAdminConsentAsync(clientAppId, tenantId, ct))
            {
                throw ClientAppValidationException.MissingAdminConsent(clientAppId, tenantId, _graphApiService.AuthorityHost);
            }

            // Step 5: Verify and fix redirect URIs
            if (applyFixes)
                await EnsureRedirectUrisAsync(clientAppId, tenantId, ct);

            // Step 6: Verify and fix public client flows (required for device code fallback)
            if (applyFixes)
                await EnsurePublicClientFlowsEnabledAsync(clientAppId, tenantId, ct);

            // Step 7: Verify and fix the 'wids' optional claim on access tokens.
            // Required so the CLI can read the signed-in user's directory roles from the token
            // (instead of returning Unknown, which causes the orchestrator to silently skip the
            // AllPrincipals OAuth2 grant phase — leaving the blueprint with inheritable=allAllowed
            // configured but no permissions actually granted on the blueprint SP).
            // Skipped when widsAlreadyApplied — the Unknown-probe path above already PATCHed it.
            if (applyFixes && needsWidsClaim && !widsAlreadyApplied)
                await EnsureWidsOptionalClaimAsync(clientAppId, tenantId, ct);

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
    /// Validates first-party service-principal presence and delegated token scopes without mutation.
    /// </summary>
    private async Task EnsureValidFirstPartyClientAppAsync(string clientAppId, string tenantId, CancellationToken ct)
    {
        GraphApiService.ServicePrincipalLookupResult lookup;
        try
        {
            _graphApiService.CustomClientAppId = clientAppId;
            lookup = await _graphApiService.LookupServicePrincipalByAppIdWithResponseAsync(
                tenantId, clientAppId, ct, GraphAuthenticationMode.ResolvedClientApp);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ClientAppValidationException.FirstPartyServicePrincipalLookupFailed(
                clientAppId, tenantId, ex.Message);
        }

        if (lookup is null || !lookup.IsSuccess)
        {
            throw ClientAppValidationException.FirstPartyServicePrincipalLookupFailed(
                clientAppId,
                tenantId,
                lookup?.FailureReason ?? "Service-principal lookup returned no result.");
        }

        if (string.IsNullOrWhiteSpace(lookup.ServicePrincipalId))
        {
            throw ClientAppValidationException.FirstPartyServicePrincipalNotFound(clientAppId, tenantId);
        }

        _logger.LogDebug(
            "First-party service principal {SpId} found for {ClientAppId} — no app-registration mutations will be attempted.",
            lookup.ServicePrincipalId, clientAppId);

        // Keep the registration scope separate because Entra can reject a combined request before
        // returning a token whose scp claim identifies the unavailable permission.
        await ValidateFirstPartyTokenScopesAsync(
            clientAppId,
            tenantId,
            AuthenticationConstants.BlueprintOperationScopes,
            ct);
        await ValidateFirstPartyTokenScopesAsync(
            clientAppId,
            tenantId,
            [AuthenticationConstants.AgentRegistrationReadWriteAllScope],
            ct);

        _logger.LogDebug(
            "First-party client app validation successful for {ClientAppId} — all required scopes present on delegated tokens.",
            clientAppId);
    }

    private async Task ValidateFirstPartyTokenScopesAsync(
        string clientAppId,
        string tenantId,
        IReadOnlyCollection<string> requiredScopes,
        CancellationToken ct)
    {
        string? token;
        try
        {
            token = await _graphApiService.GetClientAppAccessTokenAsync(tenantId, clientAppId, requiredScopes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ClientAppValidationException.FirstPartyAuthorizationFailed(
                clientAppId, requiredScopes, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw ClientAppValidationException.FirstPartyAuthorizationFailed(
                clientAppId, requiredScopes, "Token acquisition returned no result.");
        }

        if (!JwtHelper.TryDecodeSpaceDelimitedClaim(
                token,
                "scp",
                out var grantedScopes,
                out var decodeFailureReason))
        {
            throw ClientAppValidationException.FirstPartyAuthorizationFailed(
                clientAppId, requiredScopes, decodeFailureReason);
        }

        var missingScopes = requiredScopes.Where(s => !grantedScopes.Contains(s)).ToList();
        if (missingScopes.Count > 0)
        {
            throw ClientAppValidationException.FirstPartyMissingPermissions(clientAppId, missingScopes);
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

        if (AuthenticationConstants.IsWellKnownFirstPartyClientApp(clientAppId))
        {
            _logger.LogDebug("Skipping redirect URI mutation for the first-party Agent 365 CLI application.");
            return;
        }

        try
        {
            _logger.LogDebug("Checking redirect URIs for client app {ClientAppId}", clientAppId);

            using var appDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/applications?$filter=appId eq '{clientAppId}'&$select=id,publicClient",
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);

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
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);

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
    /// Ensures the client app's <c>optionalClaims.accessToken</c> includes the <c>wids</c> claim.
    /// Preserves any existing optional claims and appends <c>wids</c> when absent. Without this
    /// claim on the access token, role detection (<see cref="GraphApiService.IsCurrentUserAdminAsync"/>)
    /// always returns <c>Unknown</c> and the AllPrincipals grant phase of the permissions orchestrator
    /// is silently skipped — the symptom the user sees is "blueprint has inheritable=allAllowed but
    /// no permissions granted on the blueprint SP".
    /// </summary>
    private async Task EnsureWidsOptionalClaimAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug("Checking 'wids' optional claim for client app {ClientAppId}", clientAppId);

            var (hasWids, objectId, existingClaims) = await ReadWidsOptionalClaimStateAsync(clientAppId, tenantId, ct);

            if (string.IsNullOrWhiteSpace(objectId))
            {
                _logger.LogWarning("Could not get application object ID for 'wids' optional claim update");
                return;
            }

            if (hasWids)
            {
                _logger.LogDebug("'wids' optional claim is already present on accessToken");
                return;
            }

            // Build the merged accessToken claims array: any existing claims + wids. PATCH replaces
            // the entire optionalClaims object, so preserve idToken / saml2Token entries too.
            var existingAccessTokenClaims = existingClaims?["accessToken"]?.AsArray();
            var mergedAccessToken = new JsonArray();
            if (existingAccessTokenClaims != null)
            {
                foreach (var claimNode in existingAccessTokenClaims)
                {
                    if (claimNode == null) continue;
                    mergedAccessToken.Add(JsonNode.Parse(claimNode.ToJsonString())!);
                }
            }
            mergedAccessToken.Add(new JsonObject
            {
                ["name"] = "wids",
                ["essential"] = false,
                ["additionalProperties"] = new JsonArray()
            });

            var idTokenClaims = existingClaims?["idToken"]?.AsArray();
            var saml2TokenClaims = existingClaims?["saml2Token"]?.AsArray();

            var patchPayload = new JsonObject
            {
                ["optionalClaims"] = new JsonObject
                {
                    ["accessToken"] = mergedAccessToken,
                    ["idToken"] = idTokenClaims != null
                        ? JsonNode.Parse(idTokenClaims.ToJsonString())!
                        : new JsonArray(),
                    ["saml2Token"] = saml2TokenClaims != null
                        ? JsonNode.Parse(saml2TokenClaims.ToJsonString())!
                        : new JsonArray()
                }
            };

            _logger.LogInformation(
                "Adding 'wids' optional claim to the access token on app registration " +
                "(required so the CLI can detect Global Administrator role and apply tenant-wide consent grants).");
            _logger.LogInformation("Re-run 'a365 setup requirements' at any time to re-verify this setting.");

            var patchSuccess = await _graphApiService.GraphPatchAsync(tenantId,
                $"/v1.0/applications/{objectId}",
                patchPayload,
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);

            if (!patchSuccess)
            {
                _logger.LogWarning("Failed to add 'wids' optional claim. Role detection will return Unknown and AllPrincipals grants may be silently skipped. " +
                    "Add it manually via Azure portal: App registrations > {ClientAppId} > Token configuration > Add optional claim > Access > wids.", clientAppId);
                return;
            }

            _logger.LogInformation("Successfully added 'wids' optional claim to app registration. New tokens issued for this client will include the claim.");

            // The cached access token still lacks 'wids'. Clear the persistent MSAL token cache so
            // the next acquisition (silent via WAM / refresh-token) issues a fresh token that
            // carries the new claim. Subsequent role checks in this same process will then return
            // HasRole instead of Unknown.
            await _graphApiService.ClearTokenCacheAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error ensuring 'wids' optional claim is configured (non-fatal)");
        }
    }

    /// <summary>
    /// Outcome of the wids-PATCH admin probe used when the wids optional claim isn't yet on the
    /// access token (role detection returns Unknown). The probe IS the wids fix — on success the
    /// app is mutated; on 403 we learn the caller isn't admin without changing anything.
    /// </summary>
    private enum ProbeResult { Admin, NotAdmin, Inconclusive }

    /// <summary>
    /// Attempts to add the 'wids' optional claim and reports admin authority based on the result.
    /// Used only when <see cref="GraphApiService.IsCurrentUserAdminAsync"/> returns Unknown — i.e.
    /// the token can't tell us the role because wids isn't configured yet. A successful PATCH
    /// implies admin (Application.ReadWrite-scope-on-app via directory role); a 403 with
    /// <c>Authorization_RequestDenied</c> implies non-admin; anything else is inconclusive.
    /// </summary>
    private async Task<ProbeResult> TryProbeAdminViaWidsPatchAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct)
    {
        try
        {
            await EnsureWidsOptionalClaimAsync(clientAppId, tenantId, ct);

            // EnsureWidsOptionalClaimAsync logs but doesn't propagate the HTTP outcome.
            // Re-read the app's optionalClaims to determine whether the PATCH actually landed.
            var stillMissing = await IsWidsOptionalClaimMissingAsync(clientAppId, tenantId, ct);
            if (!stillMissing)
            {
                _logger.LogInformation("Admin authority confirmed via wids PATCH probe (claim now present on app).");
                return ProbeResult.Admin;
            }

            // Wids still missing after the attempt — most commonly Authorization_RequestDenied
            // (caller lacks directory-role write authority on the app). Treat as non-admin so the
            // existing non-admin error surface runs with full guidance.
            _logger.LogInformation("Admin authority NOT confirmed: wids PATCH did not land. Treating caller as non-admin.");
            return ProbeResult.NotAdmin;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "wids PATCH probe failed with an unexpected error — admin status is inconclusive: {Message}", ex.Message);
            return ProbeResult.Inconclusive;
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
                $"/v1.0/applications?$filter=appId eq '{clientAppId}'&$select=id,isFallbackPublicClient",
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);

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
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);

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
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);

            if (!patchSuccess)
            {
                _logger.LogWarning("Failed to update app registration with missing permissions");
                return false;
            }

            _logger.LogInformation("Added {Count} permission(s) to app registration: {Permissions}",
                missingPermissions.Count, string.Join(", ", missingPermissions));

            // Best-effort: also extend the existing oauth2PermissionGrant so consent takes effect immediately
            await TryExtendConsentGrantScopesAsync(clientAppId, missingPermissions, tenantId, ct);

            // Tokens issued before the permission update cannot carry the newly consented scopes.
            await _graphApiService.ClearTokenCacheAsync();

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
                $"/v1.0/servicePrincipals?$filter=appId eq '{clientAppId}'&$select=id",
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);

            if (spDoc == null) return;

            var spJson = JsonNode.Parse(spDoc.RootElement.GetRawText());
            var spObjectId = spJson?["value"]?.AsArray().FirstOrDefault()?.AsObject()["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(spObjectId)) return;

            // Find the oauth2PermissionGrant that targets Microsoft Graph
            using var grantsDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{spObjectId}'",
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);

            if (grantsDoc == null) return;

            var grantsJson = JsonNode.Parse(grantsDoc.RootElement.GetRawText());
            var grants = grantsJson?["value"]?.AsArray();
            if (grants == null) return;

            // Look up the Microsoft Graph service principal ID to match against resourceId
            string? graphSpObjectId = null;
            using var graphSpDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/servicePrincipals?$filter=appId eq '{AuthenticationConstants.MicrosoftGraphResourceAppId}'&$select=id",
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);

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
                    new JsonObject
                    {
                        ["scope"] = updatedScope,
                        ["consentType"] = "AllPrincipals",
                        ["principalId"] = null
                    },
                    ct,
                    scopes: null,
                    authenticationMode: GraphAuthenticationMode.Ambient);

                if (patchSuccess)
                {
                    _logger.LogInformation("Extending admin consent grant with {Count} new permission(s): {Scopes}.",
                        scopesToAdd.Count, string.Join(", ", scopesToAdd));
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
    /// missing from a custom app's grant; first-party authorization is token-based and returns empty.
    /// </summary>
    public async Task<List<string>> GetUnconsentedRequiredPermissionsAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct = default)
    {
        if (AuthenticationConstants.IsWellKnownFirstPartyClientApp(clientAppId))
        {
            return [];
        }

        var consented = await GetConsentedPermissionsAsync(clientAppId, tenantId, ct);
        return AuthenticationConstants.RequiredClientAppPermissions
            .Where(p => !consented.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Extends a custom client app's oauth2PermissionGrant to include the specified permissions.
    /// Call after the user has confirmed they want to grant admin consent.
    /// </summary>
    public Task GrantConsentForPermissionsAsync(
        string clientAppId,
        List<string> permissions,
        string tenantId,
        CancellationToken ct = default)
    {
        if (AuthenticationConstants.IsWellKnownFirstPartyClientApp(clientAppId))
        {
            throw new InvalidOperationException(
                "Tenant-local consent grants cannot be modified for the first-party Agent 365 CLI application.");
        }

        return TryExtendConsentGrantScopesAsync(clientAppId, permissions, tenantId, ct);
    }

    /// <inheritdoc />
    public async Task<bool> HasWidsAccessTokenOptionalClaimAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var (hasWids, _, _) = await ReadWidsOptionalClaimStateAsync(clientAppId, tenantId, ct);
        return hasWids;
    }

    /// <inheritdoc />
    public async Task<bool?> HasWidsClaimOnIssuedAccessTokenAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        string? token;
        try
        {
            // User.Read matches the scope set used by the role check, so the token is served from
            // the provider cache instead of triggering a second interactive sign-in.
            token = await _graphApiService.GetClientAppAccessTokenAsync(
                tenantId, clientAppId, [AuthenticationConstants.UserReadScope], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not acquire an access token for {ClientAppId} to inspect the 'wids' claim.", clientAppId);
            return null;
        }

        return JwtHelper.ClaimExists(token, "wids");
    }

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
                $"/v1.0/applications?$filter=appId eq '{clientAppId}'&$select=id,publicClient",
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);

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
                $"/v1.0/applications?$filter=appId eq '{clientAppId}'&$select=id,isFallbackPublicClient",
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);

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

    /// <summary>
    /// Single source of truth for reading the client app's optionalClaims state — used by every
    /// wids-related callsite. Returns <c>HasWids</c> (whether <c>wids</c> appears under
    /// <c>optionalClaims.accessToken</c>), <c>ObjectId</c> (the application object ID, needed for
    /// PATCH callers), and the raw <c>OptionalClaims</c> JsonObject (also for PATCH callers that
    /// need to preserve <c>idToken</c>/<c>saml2Token</c> entries). On any failure all three return
    /// values are null/false — callers decide how to surface that (most fail-closed and assume wids
    /// is missing so the standard guidance fires).
    /// </summary>
    private async Task<(bool HasWids, string? ObjectId, JsonObject? OptionalClaims)> ReadWidsOptionalClaimStateAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct)
    {
        try
        {
            using var appDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/applications?$filter=appId eq '{clientAppId}'&$select=id,optionalClaims",
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);

            if (appDoc == null) return (false, null, null);

            var response = JsonNode.Parse(appDoc.RootElement.GetRawText());
            var apps = response?["value"]?.AsArray();
            if (apps == null || apps.Count == 0) return (false, null, null);

            var app = apps[0]!.AsObject();
            var objectId = app["id"]?.GetValue<string>();
            var optionalClaims = app["optionalClaims"]?.AsObject();
            var accessTokenClaims = optionalClaims?["accessToken"]?.AsArray();

            if (accessTokenClaims == null)
                return (false, objectId, optionalClaims);

            foreach (var claimNode in accessTokenClaims)
            {
                var name = claimNode?["name"]?.GetValue<string>();
                if (string.Equals(name, "wids", StringComparison.OrdinalIgnoreCase))
                    return (true, objectId, optionalClaims);
            }
            return (false, objectId, optionalClaims);
        }
        catch (Exception ex)
        {
            // Fail closed: callers assume wids is missing so the standard guidance fires.
            _logger.LogDebug("ReadWidsOptionalClaimStateAsync failed: {Message}", ex.Message);
            return (false, null, null);
        }
    }

    /// <summary>
    /// Read-only check: returns true when the client app's <c>optionalClaims.accessToken</c> does
    /// not include the <c>wids</c> claim. Thin wrapper over <see cref="ReadWidsOptionalClaimStateAsync"/>.
    /// </summary>
    private async Task<bool> IsWidsOptionalClaimMissingAsync(
        string clientAppId,
        string tenantId,
        CancellationToken ct)
    {
        var (hasWids, _, _) = await ReadWidsOptionalClaimStateAsync(clientAppId, tenantId, ct);
        return !hasWids;
    }

    /// <summary>
    /// Returns true if the client app has only per-user (consentType: "Principal") consent grants
    /// and no tenant-wide (AllPrincipals) grant covering required permissions.
    /// When true, users other than the consenting admin see "Need admin approval" during interactive auth.
    /// </summary>
    private async Task<bool> HasPrincipalOnlyConsentGrantAsync(string clientAppId, string tenantId, CancellationToken ct)
    {
        try
        {
            using var spDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/servicePrincipals?$filter=appId eq '{clientAppId}'&$select=id",
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);
            if (spDoc == null) return false;

            var spJson = JsonNode.Parse(spDoc.RootElement.GetRawText());
            var spObjectId = spJson?["value"]?.AsArray().FirstOrDefault()?.AsObject()["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(spObjectId)) return false;

            using var grantsDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{spObjectId}'",
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);
            if (grantsDoc == null) return false;

            var grantsJson = JsonNode.Parse(grantsDoc.RootElement.GetRawText());
            var grants = grantsJson?["value"]?.AsArray();
            if (grants == null || grants.Count == 0) return false;

            bool hasAllPrincipals = false;
            bool hasPrincipal = false;

            foreach (var grantNode in grants)
            {
                var grantObj = grantNode?.AsObject();
                var consentType = grantObj?["consentType"]?.GetValue<string>();
                var scope = grantObj?["scope"]?.GetValue<string>() ?? string.Empty;

                // Only consider grants that cover required CLI permissions
                bool isRelevantGrant = AuthenticationConstants.RequiredClientAppPermissions
                    .Any(p => scope.Contains(p, StringComparison.OrdinalIgnoreCase));
                if (!isRelevantGrant) continue;

                if (string.Equals(consentType, "AllPrincipals", StringComparison.OrdinalIgnoreCase))
                    hasAllPrincipals = true;
                else if (string.Equals(consentType, "Principal", StringComparison.OrdinalIgnoreCase))
                    hasPrincipal = true;
            }

            // Upgrade needed only when there's a Principal grant covering CLI permissions but no AllPrincipals grant
            return hasPrincipal && !hasAllPrincipals;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("HasPrincipalOnlyConsentGrantAsync failed (non-fatal): {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Upgrades all per-user (consentType: "Principal") oauth2PermissionGrants that cover required
    /// CLI permissions to tenant-wide (consentType: "AllPrincipals", principalId: null).
    /// This ensures that any user in the tenant can authenticate without seeing "Need admin approval".
    /// </summary>
    private async Task UpgradeConsentGrantToAllPrincipalsAsync(string clientAppId, string tenantId, CancellationToken ct)
    {
        try
        {
            using var spDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/servicePrincipals?$filter=appId eq '{clientAppId}'&$select=id",
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);
            if (spDoc == null) return;

            var spJson = JsonNode.Parse(spDoc.RootElement.GetRawText());
            var spObjectId = spJson?["value"]?.AsArray().FirstOrDefault()?.AsObject()["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(spObjectId)) return;

            using var grantsDoc = await _graphApiService.GraphGetAsync(tenantId,
                $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{spObjectId}'",
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);
            if (grantsDoc == null) return;

            var grantsJson = JsonNode.Parse(grantsDoc.RootElement.GetRawText());
            var grants = grantsJson?["value"]?.AsArray();
            if (grants == null) return;

            foreach (var grantNode in grants)
            {
                var grant = grantNode?.AsObject();
                if (grant == null) continue;

                var grantId = grant["id"]?.GetValue<string>();
                var consentType = grant["consentType"]?.GetValue<string>();
                var scope = grant["scope"]?.GetValue<string>() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(grantId)) continue;

                // Only upgrade Principal grants that cover required CLI permissions
                if (!string.Equals(consentType, "Principal", StringComparison.OrdinalIgnoreCase)) continue;

                bool isRelevantGrant = AuthenticationConstants.RequiredClientAppPermissions
                    .Any(p => scope.Contains(p, StringComparison.OrdinalIgnoreCase));
                if (!isRelevantGrant) continue;

                _logger.LogInformation("Upgrading consent grant from per-user to tenant-wide (AllPrincipals)...");

                var patchSuccess = await _graphApiService.GraphPatchAsync(tenantId,
                    $"/v1.0/oauth2PermissionGrants/{grantId}",
                    new JsonObject
                    {
                        ["consentType"] = "AllPrincipals",
                        ["principalId"] = null,
                        ["scope"] = scope
                    },
                    ct,
                    scopes: null,
                    authenticationMode: GraphAuthenticationMode.Ambient);

                if (patchSuccess)
                    _logger.LogInformation("Consent grant upgraded to AllPrincipals — all tenant users can now authenticate without individual consent prompts.");
                else
                    _logger.LogWarning("Failed to upgrade consent grant to AllPrincipals (may require Global Administrator role).");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error upgrading consent grant (non-fatal): {Message}", ex.Message);
        }
    }

    #region Private Helper Methods

    private async Task<ClientAppInfo?> GetClientAppInfoAsync(string clientAppId, string tenantId, CancellationToken ct)
    {
        _logger.LogDebug("Checking if client app exists in tenant...");

        const string path = "/v1.0/applications?$filter=appId eq '{0}'&$select=id,appId,displayName,requiredResourceAccess";
        var graphResponse = await _graphApiService.GraphGetWithResponseAsync(tenantId,
            string.Format(path, clientAppId),
            ct: ct,
            authenticationMode: GraphAuthenticationMode.Ambient);

        if (graphResponse == null || !graphResponse.IsSuccess)
        {
            // Only retry on 401 — a stale token due to CAE revocation. Transient errors (503,
            // network failure) surface the real error to the caller rather than masking it as
            // "token revoked". StatusCode 0 means token acquisition itself failed.
            if (graphResponse?.StatusCode != 401)
            {
                _logger.LogDebug("Graph app query failed with {StatusCode} — not retrying", graphResponse?.StatusCode);
                throw ClientAppValidationException.ValidationFailed(
                    "Unable to verify the client app registration",
                    [$"Microsoft Graph application lookup failed: HTTP {graphResponse?.StatusCode ?? 0} {graphResponse?.ReasonPhrase ?? "Unknown"}."],
                    clientAppId);
            }

            _logger.LogDebug("Graph app query returned 401 — retrying with fresh token (possible CAE revocation)");
            graphResponse = await _graphApiService.GraphGetWithResponseAsync(tenantId,
                string.Format(path, clientAppId),
                forceRefresh: true,
                ct: ct,
                authenticationMode: GraphAuthenticationMode.Ambient);

            if (!graphResponse.IsSuccess)
            {
                if (graphResponse.StatusCode == 401)
                    throw ClientAppValidationException.TokenRevoked(clientAppId);

                throw ClientAppValidationException.ValidationFailed(
                    "Unable to verify the client app registration",
                    [$"Microsoft Graph application lookup failed after token refresh: HTTP {graphResponse.StatusCode} {graphResponse.ReasonPhrase}."],
                    clientAppId);
            }
        }

        using var doc = graphResponse.Json;
        if (doc is null)
        {
            throw ClientAppValidationException.ValidationFailed(
                "Unable to verify the client app registration",
                ["Microsoft Graph application lookup returned an empty response body."],
                clientAppId);
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("value", out var appsElement) ||
            appsElement.ValueKind != JsonValueKind.Array)
        {
            throw ClientAppValidationException.ValidationFailed(
                "Unable to verify the client app registration",
                ["Microsoft Graph application lookup returned an invalid response."],
                clientAppId);
        }

        if (appsElement.GetArrayLength() == 0) return null;

        var firstApp = appsElement[0];
        if (firstApp.ValueKind != JsonValueKind.Object ||
            !firstApp.TryGetProperty("id", out var objectIdElement) ||
            objectIdElement.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(objectIdElement.GetString(), out var objectId) ||
            !firstApp.TryGetProperty("appId", out var appIdElement) ||
            appIdElement.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(appIdElement.GetString(), out var returnedAppId) ||
            !Guid.TryParse(clientAppId, out var expectedAppId) ||
            returnedAppId != expectedAppId)
        {
            throw ClientAppValidationException.ValidationFailed(
                "Unable to verify the client app registration",
                ["Microsoft Graph application lookup returned an invalid application record."],
                clientAppId);
        }

        var app = JsonNode.Parse(firstApp.GetRawText())!.AsObject();
        return new ClientAppInfo(
            objectId.ToString("D"),
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
                // GUID not in v1.0 oauth2PermissionScopes (e.g. preview scopes like AgentIdentity.Create.All).
                // Add to missing so EnsurePermissionsConfiguredAsync -> TryExtendConsentGrantScopesAsync
                // patches the consent grant by scope name (no GUID required). The step-3.5 consent
                // fallback will remove this entry if already granted.
                _logger.LogDebug("Could not resolve permission GUID for {PermissionName} — will verify via consent grants", permissionName);
                missingPermissions.Add(permissionName);
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
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);

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
                $"/v1.0/servicePrincipals?$filter=appId eq '{clientAppId}'&$select=id",
                ct,
                scopes: null,
                authenticationMode: GraphAuthenticationMode.Ambient);

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

            // Get oauth2PermissionGrants. When the caller lacks DelegatedPermissionGrant.Read.All
            // the GET returns 403 permanently — fail-open and assume all required permissions
            // are consented rather than reporting an empty set (which would trigger a false
            // "permissions not consented" prompt for non-admin developers who can never read
            // the grants table by design).
            var grantsResp = await _graphApiService.GraphGetWithResponseAsync(tenantId,
                $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{spObjectId}'",
                ct: ct,
                authenticationMode: GraphAuthenticationMode.Ambient);
            using var grantsDoc = grantsResp.Json;

            if (grantsResp.StatusCode == 403)
            {
                _logger.LogDebug("Cannot read oauth2PermissionGrants (caller lacks DelegatedPermissionGrant.Read.All). Treating all required permissions as consented to avoid false prompts. Real consent failures will surface from downstream operations.");
                foreach (var p in AuthenticationConstants.RequiredClientAppPermissions)
                    consentedPermissions.Add(p);
                return consentedPermissions;
            }

            if (grantsDoc == null)
            {
                _logger.LogDebug("Could not query oauth2PermissionGrants (status: {Status})", grantsResp.StatusCode);
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
            $"/v1.0/servicePrincipals?$filter=appId eq '{clientAppId}'&$select=id,appId",
            ct,
            scopes: null,
            authenticationMode: GraphAuthenticationMode.Ambient);

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

        // Check OAuth2 permission grants. Use GraphGetWithResponseAsync so we can distinguish
        // "caller lacks DelegatedPermissionGrant.Read.All" (403) from other failure modes
        // (token acquisition, network, 5xx) — the user-facing message differs and lumping them
        // together would either misattribute the cause or hide real failures.
        var grantsResp = await _graphApiService.GraphGetWithResponseAsync(tenantId,
            $"/v1.0/oauth2PermissionGrants?$filter=clientId eq '{spObjectId}'",
            ct: ct,
            authenticationMode: GraphAuthenticationMode.Ambient);
        using var grantsDoc = grantsResp.Json;

        if (grantsResp.StatusCode == 403)
        {
            // The grants-read 403 only signals the caller lacks DelegatedPermissionGrant.Read.All —
            // it tells us nothing about whether tenant-wide consent is actually granted. Don't
            // emit a user-visible warning here: it would be a false positive on every developer
            // run (developers don't have that scope by design). Real consent failures surface
            // with actionable errors from the operations that need them.
            _logger.LogDebug("Skipping tenant-wide consent verification — caller lacks DelegatedPermissionGrant.Read.All. Downstream operations will surface any actual consent issues.");
            return true;
        }

        if (grantsDoc == null)
        {
            // Best-effort skip on transient/auth/network failures other than 403. Treat as
            // "cannot verify, assume consented" — the same operation will retry with its own
            // error handling. Logging both the status and reason helps diagnose real failures.
            _logger.LogDebug(
                "Skipping tenant-wide consent verification — grants read returned no data (status: {Status} {Reason}). Downstream operations will surface any actual consent issues.",
                grantsResp.StatusCode, grantsResp.ReasonPhrase);
            return true;
        }

        var grantsJson = JsonNode.Parse(grantsDoc.RootElement.GetRawText());
        var grants = grantsJson?["value"]?.AsArray();

        if (grants == null || grants.Count == 0)
        {
            return false; // No grants found - admin consent missing
        }

        // Require a tenant-wide (AllPrincipals) grant. A per-user (Principal) grant only covers the
        // specific admin who consented; other users see "Need admin approval" during interactive auth.
        // Graph may split permissions across multiple grants (e.g. one per resource SP), so accumulate
        // consented scopes across all AllPrincipals grants before comparing.
        var consentedScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grant in grants)
        {
            var grantObj = grant?.AsObject();
            if (!string.Equals(
                grantObj?["consentType"]?.GetValue<string>(),
                "AllPrincipals",
                StringComparison.OrdinalIgnoreCase))
                continue;

            var scope = grantObj?["scope"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(scope)) continue;

            foreach (var s in scope!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                consentedScopes.Add(s);
        }

        var foundPermissions = AuthenticationConstants.RequiredClientAppPermissions
            .Intersect(consentedScopes, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool hasAllPrincipalsGraphGrant;
        if (foundPermissions.Count == AuthenticationConstants.RequiredClientAppPermissions.Length)
        {
            _logger.LogDebug("Admin consent (AllPrincipals) verified for all {Count} required permissions", foundPermissions.Count);
            hasAllPrincipalsGraphGrant = true;
        }
        else
        {
            if (foundPermissions.Count > 0)
            {
                var missingPermissions = AuthenticationConstants.RequiredClientAppPermissions
                    .Except(foundPermissions, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _logger.LogDebug(
                    "Admin consent grants found but missing {MissingCount} permission(s): {Missing}",
                    missingPermissions.Count,
                    string.Join(", ", missingPermissions));
            }
            hasAllPrincipalsGraphGrant = false;
        }

        if (!hasAllPrincipalsGraphGrant)
        {
            // Check if there's a Principal-only grant — surface a more specific actionable message
            bool hasPrincipalGrant = grants
                .Select(g => g?.AsObject())
                .Any(g => string.Equals(g?["consentType"]?.GetValue<string>(), "Principal", StringComparison.OrdinalIgnoreCase));

            if (hasPrincipalGrant)
            {
                _logger.LogWarning("Consent grant is per-user only (consentType: Principal). Tenant-wide (AllPrincipals) consent is required.");
            }
            else
            {
                _logger.LogWarning("No admin consent grant found for the required permissions.");
            }

            // Print the admin consent URL so the user (or their admin) can fix this immediately
            var consentUrl = ClientAppValidationException.BuildAdminConsentUrl(clientAppId, tenantId, _graphApiService.AuthorityHost);
            if (consentUrl != null)
            {
                _logger.LogInformation("To grant tenant-wide admin consent, share this URL with a Global Administrator:");
                _logger.LogInformation("  {ConsentUrl}", consentUrl);
                _logger.LogInformation("After consent is granted, re-run 'a365 setup requirements' to verify.");
                _logger.LogInformation("");
            }
        }

        return hasAllPrincipalsGraphGrant;
    }

    #endregion

    #region Helper Types

    private record ClientAppInfo(string ObjectId, string DisplayName, JsonArray? RequiredResourceAccess);

    #endregion
}
