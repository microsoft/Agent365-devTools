// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Responses;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

// The OpenAI Responses API surface is annotated [Experimental("OPENAI001")] in the
// 2.x SDK. Suppress for this file; the API shape used here is small and stable.
#pragma warning disable OPENAI001

/// <summary>
/// Scores semantic checks with a user-provided Azure OpenAI deployment via the OpenAI
/// Responses API, authenticated with Entra ID (<see cref="DefaultAzureCredential"/>).
///
/// This engine scores each check INDEPENDENTLY (<see cref="ScoresPerCheck"/> = true): one
/// model call per assertion, with the full tool schema passed as context and temperature 0.
/// Per-check calls keep every response tiny (no truncation on large tools) and low-variance;
/// the evaluator fans them out concurrently. The coding-agent launchers (Copilot, Claude)
/// instead edit a whole-tool file via <see cref="LaunchAsync"/>, which this engine does not use.
///
/// Configuration is environment-driven (no secrets on the command line):
///   <c>A365_EVAL_AZURE_OPENAI_ENDPOINT</c>        (required) e.g. https://foo.services.ai.azure.com/openai/v1
///   <c>A365_EVAL_AZURE_OPENAI_DEPLOYMENT</c>      (required) e.g. gpt-4.1
///   <c>A365_EVAL_AZURE_OPENAI_MAX_CONCURRENCY</c> (optional) parallel check calls, default 100
/// The Entra ID scope (https://ai.azure.com/.default) and per-call retry count (3) are fixed.
///
/// Explicit-only: <see cref="AutoDetectable"/> is false, so <c>--eval-engine auto</c>
/// never selects it. A plain evaluate run never sends content to a model endpoint or
/// spends tokens unless the user opts in with <c>--eval-engine azure-openai</c>.
/// </summary>
internal sealed class AzureOpenAiLauncher : ICodingAgentLauncher
{
    private readonly ILogger<AzureOpenAiLauncher> _logger;

    // DefaultAzureCredential probes several credential sources on construction; build it
    // once (the launcher is a DI singleton) and reuse across all check calls.
    private DefaultAzureCredential? _credential;

    // The ResponsesClient is thread-safe for concurrent calls; build it once and share it
    // across every per-check scoring call (avoids thousands of client instances at high dop).
    private ResponsesClient? _client;
    private readonly object _clientLock = new();

    public AzureOpenAiLauncher(ILogger<AzureOpenAiLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public EvalEngine Engine => EvalEngine.AzureOpenAI;

    public string DisplayName => "Azure OpenAI";

    // Unused by this engine: it builds its own self-contained prompt from the checklist
    // content rather than a coding-agent prompt that names read/edit tools. Present only
    // to satisfy the interface.
    public SemanticCheckPrompts.AgentToolset Toolset => new(ReadToolName: "read", EditToolName: "edit");

    // No CLI binary backs this engine; it is never probed on PATH (IsAvailableAsync is
    // overridden) and is excluded from the auto-detection list.
    public string CliCommand => "azure-openai";

    public bool AutoDetectable => false;

    public string AvailabilityHint =>
        $"the {EvalModelConstants.AzureOpenAiEndpointEnvVar} and {EvalModelConstants.AzureOpenAiDeploymentEnvVar} environment variables";

    // Direct API: score many checks concurrently (vs. 1 for subprocess agents).
    public int MaxConcurrency => EvalModelConstants.AzureOpenAiMaxConcurrency;

    // This engine evaluates each check on its own (per-check), not a whole-tool file.
    public bool ScoresPerCheck => true;

    /// <summary>
    /// Available when the endpoint and deployment environment variables are set to a
    /// usable value. Credentials are not probed here (that would require a token call);
    /// an auth failure surfaces from <see cref="LaunchAsync"/> with actionable guidance.
    /// </summary>
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = EvalModelConstants.AzureOpenAiEndpoint;
        var deployment = EvalModelConstants.AzureOpenAiDeployment;

        if (endpoint is null || deployment is null)
        {
            _logger.LogDebug("Azure OpenAI engine unavailable: {EndpointVar} and/or {DeploymentVar} not set.",
                EvalModelConstants.AzureOpenAiEndpointEnvVar, EvalModelConstants.AzureOpenAiDeploymentEnvVar);
            return Task.FromResult(false);
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            _logger.LogWarning("Azure OpenAI endpoint '{Endpoint}' (from {EndpointVar}) is not a valid absolute URL.",
                endpoint, EvalModelConstants.AzureOpenAiEndpointEnvVar);
            return Task.FromResult(false);
        }

        // The server's tool names and descriptions are sent to this endpoint for scoring, so
        // require HTTPS to avoid transmitting them in plaintext. Real Azure OpenAI endpoints are
        // HTTPS, so this also rejects an obviously misconfigured (e.g. http://) value early.
        if (!string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Azure OpenAI endpoint '{Endpoint}' (from {EndpointVar}) must use HTTPS.",
                endpoint, EvalModelConstants.AzureOpenAiEndpointEnvVar);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    /// <remarks>Not used by this engine — it scores per check via <see cref="ScoreCheckAsync"/>.
    /// The evaluator routes per-check engines down a different path and never calls this.</remarks>
    public Task<bool> LaunchAsync(string prompt, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Azure OpenAI scores checks individually; the evaluator calls ScoreCheckAsync instead of LaunchAsync.");

    /// <inheritdoc />
    public async Task<CheckEvaluation?> ScoreCheckAsync(string context, string checkPrompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkPrompt);

        var endpoint = EvalModelConstants.AzureOpenAiEndpoint;
        var deployment = EvalModelConstants.AzureOpenAiDeployment;
        if (endpoint is null || deployment is null)
        {
            // IsAvailableAsync gates this; guard anyway so a direct call can't NRE.
            _logger.LogError("Azure OpenAI engine requires {EndpointVar} and {DeploymentVar}.",
                EvalModelConstants.AzureOpenAiEndpointEnvVar, EvalModelConstants.AzureOpenAiDeploymentEnvVar);
            return null;
        }

        try
        {
            var client = GetClient(endpoint, deployment);
            var prompt = SemanticCheckPrompts.BuildSingleCheckPrompt(context, checkPrompt);
            var options = new CreateResponseOptions(new[] { ResponseItem.CreateUserMessageItem(prompt) }, deployment)
            {
                // Deterministic scoring to minimize hallucination and variance across checks.
                Temperature = 0f,
            };

            ClientResult<ResponseResult> result = await client.CreateResponseAsync(options, cancellationToken);
            return ParseEvaluation(result.Value.GetOutputText());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AuthenticationFailedException ex)
        {
            _logger.LogError("Azure OpenAI authentication failed for scope {Scope}. Run 'az login' or verify your Entra credentials. {Message}",
                EvalModelConstants.AzureOpenAiScope, ex.Message);
            return null;
        }
        catch (ClientResultException ex)
        {
            // HTTP failure surfaced by the OpenAI SDK (e.g. 401 auth, 404 wrong deployment, 429 throttling after retries).
            _logger.LogWarning("Azure OpenAI request failed (HTTP {Status}): {Message}", ex.Status, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure OpenAI check scoring failed unexpectedly.");
            return null;
        }
    }

    /// <summary>
    /// Builds (once) and reuses a single <see cref="ResponsesClient"/>. The client is thread-safe
    /// for concurrent calls, so every check scoring shares it. Retry count is configurable.
    /// </summary>
    private ResponsesClient GetClient(string endpoint, string deployment)
    {
        if (_client is not null)
        {
            return _client;
        }

        lock (_clientLock)
        {
            if (_client is null)
            {
                _credential ??= new DefaultAzureCredential();
                var scope = EvalModelConstants.AzureOpenAiScope;
                // Bridge the Azure.Core credential to System.ClientModel's auth abstraction the
                // OpenAI SDK consumes, so no Azure.Identity version bump is required.
                var authPolicy = new BearerTokenPolicy(new AzureCredentialTokenProvider(_credential, scope), scope);
                _client = new ResponsesClient(deployment, authPolicy, new OpenAIClientOptions
                {
                    Endpoint = new Uri(endpoint),
                    // Retry 429/transient with exponential backoff honoring Retry-After. Fixed at 3;
                    // if checks still fail after that, re-running the command resumes only the
                    // unscored ones. The logging subclass surfaces each retry so throttling is visible.
                    RetryPolicy = new LoggingRetryPolicy(maxRetries: 3, _logger),
                });
            }

            return _client;
        }
    }

    /// <summary>
    /// Parses the model's per-check response into a <see cref="CheckEvaluation"/>, tolerating
    /// code fences and stray prose. Returns null when no usable <c>{score, reason}</c> is present.
    /// </summary>
    internal static CheckEvaluation? ParseEvaluation(string? output)
    {
        var json = ExtractJson(output);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("score", out var scoreEl))
            {
                return null;
            }

            bool score = scoreEl.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(scoreEl.GetString(), out var b) && b,
                _ => false,
            };

            var reason = root.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                ? reasonEl.GetString()!
                : "No reason provided.";

            return new CheckEvaluation(score, reason);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts a JSON object from raw model output: strips Markdown code fences and any
    /// surrounding prose, then verifies the candidate parses. Returns null when no valid
    /// JSON object can be recovered (the evaluator then retries the attempt).
    /// </summary>
    internal static string? ExtractJson(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var text = output.Trim();

        // Strip a leading ```json (or ```) fence and its closing ``` if present.
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
            {
                text = text[(firstNewline + 1)..];
            }

            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
            {
                text = text[..lastFence];
            }

            text = text.Trim();
        }

        // Narrow to the outermost { ... } in case the model added stray prose.
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        var candidate = text[start..(end + 1)];
        try
        {
            using var _ = JsonDocument.Parse(candidate, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            return candidate;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

// The classes below (AzureCredentialTokenProvider, LoggingRetryPolicy) use no experimental
// OpenAI APIs, so the OPENAI001 suppression should not extend to them.
#pragma warning restore OPENAI001

/// <summary>
/// Adapts an Azure.Core <see cref="TokenCredential"/> (e.g. <see cref="DefaultAzureCredential"/>)
/// to System.ClientModel's <see cref="AuthenticationTokenProvider"/>, which the OpenAI SDK's
/// <see cref="BearerTokenPolicy"/> consumes. This bridge lets the evaluate command authenticate
/// with Entra ID without bumping the repo's shared Azure.Identity version.
/// </summary>
internal sealed class AzureCredentialTokenProvider : AuthenticationTokenProvider
{
    private readonly TokenCredential _credential;
    private readonly string[] _scopes;

    // Single-flight token cache. Under high concurrency (hundreds/thousands of parallel
    // per-check calls) the underlying credential would otherwise be hit once per call —
    // and AzureCliCredential spawns an `az` process per acquisition, so the burst storms the
    // CLI and times out. We acquire one token, cache it until shortly before expiry, and
    // serialize refreshes through a gate so only a single acquisition is ever in flight.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);
    private volatile CachedToken? _cache;

    private sealed record CachedToken(string Value, DateTimeOffset ExpiresOn);

    public AzureCredentialTokenProvider(TokenCredential credential, string scope)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        _credential = credential;
        _scopes = new[] { scope };
    }

    public override GetTokenOptions CreateTokenOptions(IReadOnlyDictionary<string, object> properties) => new(properties);

    public override AuthenticationToken GetToken(GetTokenOptions options, CancellationToken cancellationToken)
        => GetTokenAsync(options, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AuthenticationToken> GetTokenAsync(GetTokenOptions options, CancellationToken cancellationToken)
    {
        var cached = _cache;
        if (IsFresh(cached))
        {
            return ToToken(cached!);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while we waited on the gate.
            cached = _cache;
            if (IsFresh(cached))
            {
                return ToToken(cached!);
            }

            AccessToken token = await _credential.GetTokenAsync(new TokenRequestContext(_scopes), cancellationToken).ConfigureAwait(false);
            var fresh = new CachedToken(token.Token, token.ExpiresOn);
            _cache = fresh;
            return ToToken(fresh);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsFresh(CachedToken? c) => c is not null && c.ExpiresOn - DateTimeOffset.UtcNow > RefreshSkew;

    private static AuthenticationToken ToToken(CachedToken c) => new(c.Value, "Bearer", c.ExpiresOn, refreshOn: null);
}

/// <summary>
/// A <see cref="ClientRetryPolicy"/> that logs whenever it retries a request, so rate-limit
/// (HTTP 429) and transient failures are visible instead of being silently absorbed. The
/// retry decision, exponential backoff, and Retry-After handling are inherited unchanged from
/// the base policy; this subclass only adds the log line on each retry.
/// </summary>
internal sealed class LoggingRetryPolicy : ClientRetryPolicy
{
    private readonly ILogger _logger;

    public LoggingRetryPolicy(int maxRetries, ILogger logger) : base(maxRetries)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    protected override bool ShouldRetry(PipelineMessage message, Exception? exception)
    {
        var shouldRetry = base.ShouldRetry(message, exception);
        if (shouldRetry)
        {
            LogRetry(message.Response?.Status ?? 0, exception);
        }

        return shouldRetry;
    }

    protected override async ValueTask<bool> ShouldRetryAsync(PipelineMessage message, Exception? exception)
    {
        var shouldRetry = await base.ShouldRetryAsync(message, exception).ConfigureAwait(false);
        if (shouldRetry)
        {
            LogRetry(message.Response?.Status ?? 0, exception);
        }

        return shouldRetry;
    }

    private void LogRetry(int status, Exception? exception)
    {
        if (status == 429)
        {
            _logger.LogWarning("        Azure OpenAI rate-limited (HTTP 429); backing off and retrying the call.");
        }
        else if (status == 503)
        {
            _logger.LogWarning("        Azure OpenAI unavailable (HTTP 503); backing off and retrying the call.");
        }
        else if (status >= 400)
        {
            _logger.LogDebug("        Azure OpenAI transient failure (HTTP {Status}); retrying the call.", status);
        }
        else
        {
            // No HTTP response captured (network error / timeout); the exception carries the detail.
            _logger.LogDebug(exception, "        Azure OpenAI transient error; retrying the call.");
        }
    }
}
