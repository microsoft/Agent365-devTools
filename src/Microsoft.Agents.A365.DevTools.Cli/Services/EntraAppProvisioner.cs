// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Creates the Entra app registrations needed by publish and register flows. Both flows produce
/// the same two app shapes (a confidential proxy app with a secret, and a public-client app with
/// canonical redirect URIs); register additionally produces a second proxy app for the remote
/// MCP server. This provisioner centralizes the creation steps and their error-handling so the
/// command executors can compose them without duplicating the per-step null checks and logging.
/// </summary>
internal class EntraAppProvisioner
{
    private static readonly string[] PublicClientCanonicalRedirectUris =
    [
        "http://localhost:8080/callback",
        "https://vscode.dev/redirect",
        "http://localhost",
    ];

    private static readonly string[] ProdConsentRedirectUris =
    [
        "https://admin.cloud.microsoft/?ref=tools/consent",
    ];

    internal static string[] GetConsentRedirectUris(string environment)
    {
        var envKey = environment?.ToUpperInvariant() ?? "PROD";
        var customUris = Environment.GetEnvironmentVariable($"A365_CONSENT_REDIRECT_URIS_{envKey}");
        if (!string.IsNullOrWhiteSpace(customUris))
        {
            return customUris.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return [.. ProdConsentRedirectUris];
    }

    private readonly ILogger _logger;
    private readonly GraphApiService _graphApiService;
    private readonly RetryHelper _retryHelper;

    internal EntraAppProvisioner(ILogger logger, GraphApiService graphApiService, RetryHelper retryHelper)
    {
        _logger = logger;
        _graphApiService = graphApiService;
        _retryHelper = retryHelper;
    }

    /// <summary>
    /// Result of creating a confidential proxy app (A365 Proxy or Remote Proxy). All fields are
    /// guaranteed non-empty on success; a null return indicates failure with the cause already
    /// logged.
    /// </summary>
    internal sealed record ProxyAppResult(string ClientId, string Secret, string ObjectId, string AppName);

    /// <summary>
    /// Result of creating the Public Clients app. Failure here is non-fatal: the caller still
    /// receives the resolved <c>AppName</c> and a warning is appended to the supplied list, but
    /// <c>ClientId</c>/<c>ObjectId</c> may be null if the app or redirect-URI setup failed.
    /// </summary>
    internal sealed record PublicClientsAppResult(string? ClientId, string? ObjectId, string AppName);

    /// <summary>
    /// Creates a confidential proxy Entra app named <c>{serverName}-{suffix}</c>, adds a client
    /// secret, and validates that the returned client ID is non-empty. Used for both the A365
    /// Proxy (publish + register) and the Remote Proxy (register, when auth is EntraOAuth).
    /// </summary>
    /// <param name="lifetimeMonths">Optional secret lifetime in months, passed through to Graph's
    /// <c>addPassword</c> call. When null, Graph applies its default (~2 years). Register surfaces
    /// this via <c>--secret-lifetime-months</c> so users in tenants whose <c>appManagementPolicies</c>
    /// cap credential lifetimes below 2 years can opt into a shorter expiry; publish leaves it null.</param>
    internal virtual async Task<ProxyAppResult?> CreateProxyAppAsync(
        string serverName,
        string tenantId,
        string suffix,
        string roleDisplay,
        string? serviceTreeId,
        int? lifetimeMonths = null,
        CancellationToken ct = default)
    {
        var appName = $"{serverName}-{suffix}";

        _logger.LogDebug("Creating Entra application for {Role}...", roleDisplay);
        var app = await _graphApiService.CreateEntraAppAsync(tenantId, appName, serviceTreeId: serviceTreeId, ct);
        if (app == null)
        {
            _logger.LogError("Failed to create Entra application '{AppName}'. Ensure you have Application.ReadWrite.All permission in the target tenant. Run with -v for details.", appName);
            return null;
        }
        _logger.LogInformation("Created Entra app '{AppName}' (clientId: {ClientId})", appName, app.Value.ClientId);

        var secret = await _graphApiService.AddAppPasswordAsync(tenantId, app.Value.ObjectId, lifetimeMonths: lifetimeMonths, ct: ct);
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogError("Failed to create secret for '{AppName}'. Run with -v for details.", appName);
            await TryDeleteOrphanedAppAsync(tenantId, app.Value.ObjectId, appName, "secret-creation failed", ct);
            return null;
        }

        if (string.IsNullOrWhiteSpace(app.Value.ClientId))
        {
            _logger.LogError("{Role} Entra application was created but returned an empty client ID", roleDisplay);
            await TryDeleteOrphanedAppAsync(tenantId, app.Value.ObjectId, appName, "empty client ID returned", ct);
            return null;
        }

        _logger.LogDebug("Created {Role} app: {ClientId}", roleDisplay, app.Value.ClientId);
        return new ProxyAppResult(app.Value.ClientId, secret, app.Value.ObjectId, appName);
    }

    private async Task SetWebConsentRedirectUrisAsync(
        string tenantId, string objectId, string appName, string roleDisplay,
        string? environment, CancellationToken ct, List<string>? warnings = null)
    {
        var resolvedEnvironment = environment ?? Environment.GetEnvironmentVariable("A365_ENVIRONMENT") ?? "prod";
        var consentUris = GetConsentRedirectUris(resolvedEnvironment);

        var success = await _retryHelper.ExecuteWithRetryAsync(
            async retryCt => await _graphApiService.UpdateAppRedirectUrisAsync(tenantId, objectId, consentUris, retryCt),
            result => !result,
            cancellationToken: ct);
        if (success)
        {
            _logger.LogDebug(
                "Set {RedirectUriCount} web redirect URIs on '{AppName}' ({ObjectId}): {RedirectUris}",
                consentUris.Length, appName, objectId, string.Join(", ", consentUris));
        }
        else
        {
            var msg = $"Failed to set web redirect URIs on {roleDisplay} app '{appName}' after retries.";
            _logger.LogWarning(msg);
            warnings?.Add(msg);
        }
    }

    /// <summary>
    /// Best-effort compensating delete for an Entra app that was successfully created but failed
    /// a follow-up step (secret creation, post-create validation). Without this, partial failures
    /// in <see cref="CreateProxyAppAsync"/> leak orphan app registrations into the user's tenant
    /// on a likely failure mode (Graph throttling, permission gaps). Delete failures are surfaced
    /// as warnings — the caller has already returned null with a clear error, so we don't want a
    /// secondary cleanup error to drown the root cause; the user can clean up manually using the
    /// objectId we log.
    /// </summary>
    private async Task TryDeleteOrphanedAppAsync(string tenantId, string objectId, string appName, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objectId))
        {
            return;
        }

        try
        {
            var deleted = await _graphApiService.DeleteEntraAppAsync(tenantId, objectId, ct);
            if (deleted)
            {
                _logger.LogInformation(
                    "Rolled back orphan Entra app '{AppName}' (objectId {ObjectId}) after {Reason}.",
                    appName, objectId, reason);
            }
            else
            {
                _logger.LogWarning(
                    "Could not roll back Entra app '{AppName}' (objectId {ObjectId}) after {Reason}. Delete it manually in the Azure portal.",
                    appName, objectId, reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Exception rolling back Entra app '{AppName}' (objectId {ObjectId}) after {Reason}. Delete it manually in the Azure portal.",
                appName, objectId, reason);
        }
    }

    /// <summary>
    /// Creates the Public Clients Entra app named <c>{serverName}-PublicClients</c> and sets its
    /// public-client redirect URIs (broker plugin + canonical localhost/vscode URIs). Best-effort:
    /// any failure is converted to a warning rather than failing the caller; the returned record
    /// carries null IDs to signal partial success while still surfacing the app name.
    /// </summary>
    internal virtual async Task<PublicClientsAppResult> CreatePublicClientsAppAsync(
        string serverName,
        string tenantId,
        string? serviceTreeId,
        List<string> warnings,
        string? environment = null,
        CancellationToken ct = default)
    {
        var appName = $"{serverName}-PublicClients";

        _logger.LogDebug("Creating Entra application for Public Clients...");
        var app = await _graphApiService.CreateEntraAppAsync(tenantId, appName, serviceTreeId: serviceTreeId, ct);
        if (app == null)
        {
            var msg = "Failed to create Public Clients Entra app. Continuing without it.";
            _logger.LogWarning(msg);
            warnings.Add(msg);
            return new PublicClientsAppResult(null, null, appName);
        }

        var clientId = app.Value.ClientId;
        var objectId = app.Value.ObjectId;
        _logger.LogInformation("Created Entra app '{AppName}' (clientId: {ClientId})", appName, clientId);

        var brokerRedirectUri = $"ms-appx-web://Microsoft.AAD.BrokerPlugin/{clientId}";
        var publicClientUris = new[] { brokerRedirectUri }.Concat(PublicClientCanonicalRedirectUris).ToArray();

        try
        {
            var success = await _retryHelper.ExecuteWithRetryAsync(
                async retryCt => await _graphApiService.UpdateAppPublicClientRedirectUrisAsync(tenantId, objectId, publicClientUris, retryCt),
                result => !result,
                cancellationToken: ct);
            if (!success)
            {
                var msg = $"Failed to set publicClient redirect URIs on Public Clients app '{appName}' after retries.";
                _logger.LogError(msg);
                warnings.Add(msg);
            }
            else
            {
                _logger.LogDebug(
                    "Set {RedirectUriCount} publicClient redirect URIs on '{AppName}' ({ObjectId}): {RedirectUris}",
                    publicClientUris.Length,
                    appName,
                    objectId,
                    string.Join(", ", publicClientUris));
            }

            await SetWebConsentRedirectUrisAsync(tenantId, objectId, appName, "Public Clients", environment, ct, warnings);
        }
        catch (Exception ex)
        {
            // Caller cancellation (Ctrl+C) should terminate the CLI promptly rather than be demoted
            // to a warning and continue. Rethrow so cancellation propagates, matching the handling
            // in the command executors and setup flows.
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            var msg = $"Failed to set redirect URIs on Public Clients app: {ex.Message}";
            _logger.LogError(msg);
            warnings.Add(msg);
        }

        return new PublicClientsAppResult(clientId, objectId, appName);
    }
}
