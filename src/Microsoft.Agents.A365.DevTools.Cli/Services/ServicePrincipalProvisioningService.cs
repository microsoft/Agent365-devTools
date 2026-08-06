// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Outcome of an Agent 365 CLI service principal provisioning attempt.
/// </summary>
public enum ServicePrincipalProvisioningStatus
{
    /// <summary>Provisioning was not attempted.</summary>
    Skipped,

    /// <summary>The service principal already existed in the tenant.</summary>
    AlreadyProvisioned,

    /// <summary>The service principal was provisioned by this call.</summary>
    Provisioned,

    /// <summary>Provisioning was attempted and did not succeed.</summary>
    Failed,
}

/// <summary>
/// Result of an Agent 365 CLI service principal provisioning attempt.
/// </summary>
/// <param name="Status">Outcome of the attempt.</param>
/// <param name="ServicePrincipalObjectId">Object ID of the service principal, when known.</param>
/// <param name="Detail">Human-readable detail for diagnostics.</param>
public sealed record ServicePrincipalProvisioningResult(
    ServicePrincipalProvisioningStatus Status,
    string? ServicePrincipalObjectId,
    string? Detail);

/// <summary>
/// Ensures the Agent 365 CLI service principal exists in the target tenant.
/// </summary>
public interface IServicePrincipalProvisioningService
{
    /// <summary>
    /// Ensures the Agent 365 CLI service principal exists in <paramref name="tenantId"/>.
    /// Runs at most once per tenant for the lifetime of the process and never throws.
    /// </summary>
    /// <param name="tenantId">Target tenant ID.</param>
    /// <param name="userId">Optional login hint for token acquisition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The provisioning result.</returns>
    Task<ServicePrincipalProvisioningResult> EnsureProvisionedAsync(
        string? tenantId,
        string? userId = null,
        CancellationToken ct = default);
}

/// <summary>
/// Requests Agent 365 CLI service principal provisioning from the Agent 365 service.
/// </summary>
/// <remarks>
/// The CLI is a public client, so its service principal is not created automatically on first
/// sign-in. The Agent 365 service performs the provisioning on the caller's behalf.
/// </remarks>
public sealed class ServicePrincipalProvisioningService : IServicePrincipalProvisioningService
{
    /// <summary>
    /// Set to "true" or "1" to skip the provisioning call entirely.
    /// </summary>
    public const string DisableEnvironmentVariable = "A365_DISABLE_SP_PROVISIONING";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<ServicePrincipalProvisioningService> _logger;
    private readonly IAuthenticationService _authService;
    private readonly HttpMessageHandler? _handler;
    private readonly RetryHelper _retryHelper;
    private readonly IConfigService? _configService;

    private readonly ConcurrentDictionary<string, Task<ServicePrincipalProvisioningResult>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="ServicePrincipalProvisioningService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="authService">Authentication service used to acquire the delegated token.</param>
    /// <param name="configService">Optional config service used to resolve the environment.</param>
    /// <param name="handler">Optional handler override, used by tests.</param>
    /// <param name="retryHelper">Optional retry helper override, used by tests.</param>
    public ServicePrincipalProvisioningService(
        ILogger<ServicePrincipalProvisioningService> logger,
        IAuthenticationService authService,
        IConfigService? configService = null,
        HttpMessageHandler? handler = null,
        RetryHelper? retryHelper = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _configService = configService;
        _handler = handler;
        _retryHelper = retryHelper ?? new RetryHelper(logger);
    }

    /// <inheritdoc/>
    public Task<ServicePrincipalProvisioningResult> EnsureProvisionedAsync(
        string? tenantId,
        string? userId = null,
        CancellationToken ct = default)
    {
        if (IsDisabled())
        {
            _logger.LogDebug(
                "Agent 365 CLI service principal provisioning disabled by {Variable}.",
                DisableEnvironmentVariable);

            return Task.FromResult(new ServicePrincipalProvisioningResult(
                ServicePrincipalProvisioningStatus.Skipped, null, "Disabled"));
        }

        // The tenant ID reaches a request URL, so only accept a well-formed GUID.
        if (!Guid.TryParse(tenantId, out var parsedTenantId) || parsedTenantId == Guid.Empty)
        {
            _logger.LogDebug("Skipping service principal provisioning: no valid tenant ID available.");

            return Task.FromResult(new ServicePrincipalProvisioningResult(
                ServicePrincipalProvisioningStatus.Skipped, null, "NoTenantId"));
        }

        var key = parsedTenantId.ToString();

        // One attempt per tenant per process; concurrent callers share the same attempt.
        return _inFlight.GetOrAdd(key, _ => ProvisionAsync(parsedTenantId, userId, ct));
    }

    private static bool IsDisabled()
    {
        var value = Environment.GetEnvironmentVariable(DisableEnvironmentVariable);

        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.Ordinal);
    }

    private async Task<ServicePrincipalProvisioningResult> ProvisionAsync(
        Guid tenantId,
        string? userId,
        CancellationToken ct)
    {
        try
        {
            var environment = await ResolveEnvironmentAsync();
            var baseUrl = ConfigConstants.GetProvisioningBaseUrl(environment);
            var requestUrl = baseUrl + string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                ConfigConstants.Agent365CliProvisionPathFormat,
                tenantId);

            // The Power Platform API gateway fronts the service, so the token is issued for that
            // resource.
            var authToken = await _authService.GetAccessTokenAsync(
                PowerPlatformConstants.PowerPlatformApiIdentifierUri,
                tenantId.ToString(),
                userId: userId,
                ct: ct);

            var correlationId = HttpClientFactory.GenerateCorrelationId();

            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(
                authToken, correlationId: correlationId, handler: _handler);

            _logger.LogDebug(
                "Requesting Agent 365 CLI service principal provisioning for tenant {TenantId}.",
                tenantId);

            using var response = await _retryHelper.ExecuteWithRetryAsync(
                sendCt => httpClient.PostAsync(requestUrl, content: null, sendCt),
                cancellationToken: ct);

            return await InterpretResponseAsync(response, tenantId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Provisioning is best-effort; setup continues and surfaces its own errors.
            _logger.LogDebug(
                ex,
                "Agent 365 CLI service principal provisioning failed for tenant {TenantId}.",
                tenantId);

            return new ServicePrincipalProvisioningResult(
                ServicePrincipalProvisioningStatus.Failed, null, ex.Message);
        }
    }

    private async Task<string?> ResolveEnvironmentAsync()
    {
        if (_configService == null)
        {
            return null;
        }

        try
        {
            var config = await _configService.LoadAsync();
            return config?.Environment;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve environment for the provisioning endpoint; using default.");
            return null;
        }
    }

    private async Task<ServicePrincipalProvisioningResult> InterpretResponseAsync(
        HttpResponseMessage response,
        Guid tenantId,
        CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug(
                "Agent 365 CLI service principal provisioning returned {StatusCode} for tenant {TenantId}. {Body}",
                (int)response.StatusCode,
                tenantId,
                body);

            var detail = response.StatusCode == HttpStatusCode.Forbidden
                ? "Forbidden"
                : $"Http{(int)response.StatusCode}";

            return new ServicePrincipalProvisioningResult(
                ServicePrincipalProvisioningStatus.Failed, null, detail);
        }

        ProvisionResponseBody? payload = null;

        try
        {
            payload = JsonSerializer.Deserialize<ProvisionResponseBody>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Could not parse service principal provisioning response.");
        }

        var status = payload?.Status switch
        {
            "Provisioned" => ServicePrincipalProvisioningStatus.Provisioned,
            "AlreadyProvisioned" => ServicePrincipalProvisioningStatus.AlreadyProvisioned,
            "Disabled" => ServicePrincipalProvisioningStatus.Skipped,
            "Failed" => ServicePrincipalProvisioningStatus.Failed,
            _ => ServicePrincipalProvisioningStatus.Provisioned,
        };

        _logger.LogDebug(
            "Agent 365 CLI service principal provisioning for tenant {TenantId} returned {Status}.",
            tenantId,
            status);

        return new ServicePrincipalProvisioningResult(
            status, payload?.ServicePrincipalObjectId, payload?.Detail);
    }

    private sealed class ProvisionResponseBody
    {
        public string? Status { get; set; }

        public string? ApplicationId { get; set; }

        public string? ServicePrincipalObjectId { get; set; }

        public string? Detail { get; set; }
    }
}
