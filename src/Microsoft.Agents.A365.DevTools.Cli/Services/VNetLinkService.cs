// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Calls the Agent 365 platform's /agents/vnet endpoints.
///
/// The platform resolves the tenant's Power Platform environment itself, which is why this
/// replaces the final Enable-SubnetInjection step of the Microsoft.PowerPlatform.EnterprisePolicies
/// module: that cmdlet needs an environment id Agent 365 does not publish.
///
/// The ARM read that turns a policy ARM id into the systemId the Business App Platform requires
/// happens here, in the CLI, under the admin's own Azure session. The platform therefore needs no
/// delegated ARM access of its own.
/// </summary>
public class VNetLinkService : IVNetLinkService
{
    private const string LinkPath = "/agents/vnet/link";
    private const string UnlinkPath = "/agents/vnet/unlink";
    private const string StatusPath = "/agents/vnet/status";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger<VNetLinkService> _logger;
    private readonly IAuthenticationService _authService;
    private readonly ArmApiService _armApiService;
    private readonly string _environment;
    private readonly HttpMessageHandler? _handler;
    private readonly Func<Task<string?>> _loginHintResolver;

    public VNetLinkService(
        ILogger<VNetLinkService> logger,
        IAuthenticationService authService,
        ArmApiService armApiService,
        string environment = "prod",
        HttpMessageHandler? handler = null,
        Func<Task<string?>>? loginHintResolver = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _armApiService = armApiService ?? throw new ArgumentNullException(nameof(armApiService));
        _environment = environment ?? "prod";
        _handler = handler;
        _loginHintResolver = loginHintResolver ?? AzCliHelper.ResolveLoginHintAsync;
    }

    /// <inheritdoc />
    public async Task<VNetStatusResponse?> LinkAsync(
        string policyArmId,
        bool swap,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(policyArmId))
            throw new ArgumentException("Policy ARM id is required.", nameof(policyArmId));

        _logger.LogInformation("Reading enterprise policy from Azure...");
        var policySystemId = await _armApiService.GetEnterprisePolicySystemIdAsync(policyArmId, tenantId, cancellationToken);
        if (string.IsNullOrWhiteSpace(policySystemId))
        {
            _logger.LogError("Could not resolve the policy's systemId, so there is nothing to send to Agent 365.");
            return null;
        }

        var request = new VNetLinkRequest
        {
            PolicySystemId = policySystemId,
            PolicyArmId = policyArmId,
            Swap = swap,
        };

        _logger.LogInformation("Linking the policy to your Agent 365 environment...");
        return await SendAsync(HttpMethod.Post, LinkPath, request, "link virtual network", tenantId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<VNetStatusResponse?> UnlinkAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Removing the virtual network link from your Agent 365 environment...");
        return await SendAsync(HttpMethod.Post, UnlinkPath, payload: null, "unlink virtual network", tenantId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<VNetStatusResponse?> GetStatusAsync(
        string tenantId,
        string? operationId = null,
        CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(operationId)
            ? StatusPath
            : $"{StatusPath}?operationId={Uri.EscapeDataString(operationId)}";

        return await SendAsync(HttpMethod.Get, path, payload: null, "read virtual network status", tenantId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<VNetStatusResponse?> WaitForCompletionAsync(
        string tenantId,
        string operationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("Operation id is required.", nameof(operationId));

        // Wall clock, not summed sleeps: each status call costs real time, and a caller who asked
        // for five minutes should not wait eight because the service was slow.
        //
        // The stopwatch alone only bounds the gap between completed polls. A poll that starts just
        // inside the ceiling can still run to the HttpClient's own timeout, overshooting by minutes,
        // so the ceiling is also armed on the token every request is made with.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        VNetStatusResponse? last = null;

        while (true)
        {
            try
            {
                last = await GetStatusAsync(tenantId, operationId, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The ceiling elapsed mid-request. That is a timeout, not a failure: report the
                // last known state, exactly as the pre-sleep check below does.
                _logger.LogInformation(
                    "Stopped waiting after {Elapsed:0}s. The operation is still running.",
                    stopwatch.Elapsed.TotalSeconds);
                return last;
            }

            if (last == null || !IsRunning(last.Status))
                return last;

            if (stopwatch.Elapsed + PollInterval >= timeout)
                return last;

            _logger.LogInformation("Still running... ({Elapsed:0}s elapsed)", stopwatch.Elapsed.TotalSeconds);
            // The pre-sleep check above guarantees this delay finishes inside the ceiling, so it
            // waits on the caller's token only -- the timeout can't fire here.
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// True when the reported status means the operation has not settled yet.
    ///
    /// The platform reports a queued operation as NotStarted, which is as unsettled as Running:
    /// treating it as terminal would make --wait return before the work had begun.
    /// </summary>
    public static bool IsRunning(string? status) =>
        string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "NotStarted", StringComparison.OrdinalIgnoreCase);

    private async Task<VNetStatusResponse?> SendAsync(
        HttpMethod method,
        string path,
        object? payload,
        string operationName,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var correlationId = HttpClientFactory.GenerateCorrelationId();
        var baseUrl = BuildBaseUrl();
        var url = $"{baseUrl}{path}";

        try
        {
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);
            var loginHint = await _loginHintResolver();

            // The tenant matters as much here as on the ARM read: without it MSAL falls back to
            // the common authority with only a login hint, so on a machine with several cached
            // accounts the platform call can land in a different tenant than the policy read.
            var authToken = await _authService.GetAccessTokenAsync(audience, tenantId, userId: loginHint, ct: cancellationToken);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire an Agent 365 access token.");
                return null;
            }

            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(
                authToken, correlationId: correlationId, handler: _handler);

            using var request = new HttpRequestMessage(method, url);
            if (payload != null)
            {
                var json = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(json, Encoding.UTF8);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                _logger.LogDebug("Request payload: {Payload}", json);
            }

            _logger.LogDebug("{Method} {Url} (CorrelationId: {CorrelationId})", method, url, correlationId);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("Response {StatusCode}: {Body}", response.StatusCode, body);

            if (!response.IsSuccessStatusCode)
            {
                LogFailure(response.StatusCode, body, operationName, correlationId);
                return null;
            }

            // 200 and 202 share a shape as far as the CLI is concerned: a status, and an
            // operationId when there is more to wait for.
            return string.IsNullOrWhiteSpace(body)
                ? new VNetStatusResponse()
                : JsonSerializer.Deserialize<VNetStatusResponse>(body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (NetworkHelper.IsConnectionResetByProxy(ex))
                _logger.LogWarning(NetworkHelper.ConnectionResetWarning);
            else
                _logger.LogError(ex, "Failed to {Operation}. Correlation ID: {CorrelationId}", operationName, correlationId);
            return null;
        }
    }

    private void LogFailure(HttpStatusCode statusCode, string body, string operationName, string correlationId)
    {
        string? message = null;
        try
        {
            message = JsonSerializer.Deserialize<VNetErrorResponse>(body)?.Error;
        }
        catch (JsonException)
        {
            // The platform always sends a typed error body, so a non-JSON body means something
            // upstream of it answered. The status code is then the only usable signal.
        }

        _logger.LogError(
            "Failed to {Operation}. Status: {StatusCode}. {Message}",
            operationName,
            statusCode,
            message ?? "No error detail was returned.");

        if (statusCode == HttpStatusCode.Forbidden)
        {
            _logger.LogError(
                "This command requires the Global Administrator or Power Platform Administrator role, " +
                "and a client application consented for AgentTools.VNet.Manage.All.");
        }

        _logger.LogError("Correlation ID: {CorrelationId}", correlationId);
    }

    private string BuildBaseUrl()
    {
        var discoverUrl = ConfigConstants.GetDiscoverEndpointUrl(_environment);
        var uri = new Uri(discoverUrl);
        return $"{uri.Scheme}://{uri.Authority}";
    }
}
