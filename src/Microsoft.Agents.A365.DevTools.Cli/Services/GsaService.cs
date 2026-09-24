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
/// Calls the Agent 365 platform's /agents/gsa endpoints.
///
/// The setting lives on the tenant's Power Platform environment, whose id Agent 365 does not
/// publish. The platform resolves that environment itself, so these calls carry no environment
/// identifier at all.
/// </summary>
public class GsaService : IGsaService
{
    private const string EnablePath = "/agents/gsa/enable";
    private const string DisablePath = "/agents/gsa/disable";
    private const string StatusPath = "/agents/gsa/status";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger<GsaService> _logger;
    private readonly IAuthenticationService _authService;
    private readonly string _environment;
    private readonly HttpMessageHandler? _handler;

    public GsaService(
        ILogger<GsaService> logger,
        IAuthenticationService authService,
        string environment = "prod",
        HttpMessageHandler? handler = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _environment = environment ?? "prod";
        _handler = handler;
    }

    /// <inheritdoc />
    public async Task<GsaStatusResponse?> SetAsync(
        AzureAccountInfo account,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var path = enabled ? EnablePath : DisablePath;
        var operationName = enabled ? "enable Global Secure Access" : "disable Global Secure Access";

        _logger.LogInformation(
            "{Action} Global Secure Access on your Agent 365 environment...",
            enabled ? "Enabling" : "Disabling");

        return await SendAsync(account, HttpMethod.Post, path, operationName, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GsaStatusResponse?> GetStatusAsync(
        AzureAccountInfo account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        return await SendAsync(
            account, HttpMethod.Get, StatusPath, "read Global Secure Access status", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GsaStatusResponse?> WaitForStatusAsync(
        AzureAccountInfo account,
        string expectedStatus,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (string.IsNullOrWhiteSpace(expectedStatus))
            throw new ArgumentException("Expected status is required.", nameof(expectedStatus));

        // Wall clock, not summed sleeps: each status call costs real time, and a caller who asked
        // for five minutes should not wait eight because the service was slow.
        //
        // The stopwatch alone only bounds the gap between completed polls. A poll that starts just
        // inside the ceiling can still run to the HttpClient's own timeout, overshooting by
        // minutes, so the ceiling is also armed on the token every request is made with.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        GsaStatusResponse? last = null;

        while (true)
        {
            try
            {
                last = await GetStatusAsync(account, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The ceiling elapsed mid-request. That is a timeout, not a failure: report the
                // last known state, exactly as the pre-sleep check below does.
                _logger.LogInformation(
                    "Stopped waiting after {Elapsed:0}s. The change is still being applied.",
                    stopwatch.Elapsed.TotalSeconds);
                return last;
            }

            if (last == null || string.Equals(last.Status, expectedStatus, StringComparison.OrdinalIgnoreCase))
                return last;

            if (stopwatch.Elapsed + PollInterval >= timeout)
                return last;

            _logger.LogInformation("Still applying... ({Elapsed:0}s elapsed)", stopwatch.Elapsed.TotalSeconds);

            // The pre-sleep check above guarantees this delay finishes inside the ceiling, so it
            // waits on the caller's token only -- the timeout can't fire here.
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private async Task<GsaStatusResponse?> SendAsync(
        AzureAccountInfo account,
        HttpMethod method,
        string path,
        string operationName,
        CancellationToken cancellationToken)
    {
        var correlationId = HttpClientFactory.GenerateCorrelationId();
        var baseUrl = BuildBaseUrl();
        var url = $"{baseUrl}{path}";

        try
        {
            var audience = ConfigConstants.GetAgent365ToolsResourceAppId(_environment);

            // Authenticate against the tenant of the az login the caller resolved, not whichever
            // account the Windows broker happens to prefer. Without an explicit tenant the
            // authority is "common", and WAM silently returns the Windows account even when a login
            // hint names a different one — so a tenant-wide setting would be changed on the wrong
            // tenant. Passing the tenant also arms the mismatch self-heal in AuthenticationService.
            var authToken = await _authService.GetAccessTokenAsync(
                audience, account.TenantId, userId: account.User.Name, ct: cancellationToken);
            if (string.IsNullOrWhiteSpace(authToken))
            {
                _logger.LogError("Failed to acquire an Agent 365 access token.");
                return null;
            }

            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(
                authToken, correlationId: correlationId, handler: _handler);

            using var request = new HttpRequestMessage(method, url);

            // The platform derives everything it needs from the token, so enable and disable are
            // distinguished by route rather than by a body.
            if (method == HttpMethod.Post)
            {
                request.Content = new StringContent(string.Empty, Encoding.UTF8);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
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

            // 200 and 202 share a shape as far as the CLI is concerned: a status, plus a pending
            // flag when the change has not surfaced yet.
            return string.IsNullOrWhiteSpace(body)
                ? new GsaStatusResponse()
                : JsonSerializer.Deserialize<GsaStatusResponse>(body);
        }
        // Cancellation is the caller's business, or the wait ceiling firing on a linked token.
        // HttpClient's own timeout also surfaces as OperationCanceledException with no token
        // cancelled, and that is an ordinary request failure — it belongs in the catch below so it
        // is logged and reported, not thrown at whoever called enable, disable or status.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
            message = JsonSerializer.Deserialize<GsaErrorResponse>(body)?.Error;
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
                "and a client application consented for AgentTools.Gsa.Manage.All.");
        }

        if (statusCode == HttpStatusCode.Conflict)
        {
            // Retrying cannot fix this one, so say why rather than letting it look transient.
            _logger.LogError(
                "A Power Platform policy governs this setting. Change it through that policy instead.");
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
