// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Single source of truth for the coding-agent model identifiers used during semantic
/// evaluation. Each model has a per-engine environment-variable override so a user can
/// move to a newer model without waiting for a CLI release.
///
/// GitHub Copilot requires an exact model ID (no aliases); Claude Code accepts an
/// alias — so the two values are deliberately separate, not a single shared constant.
/// </summary>
internal static class EvalModelConstants
{
    /// <summary>Environment variable that overrides the GitHub Copilot model.</summary>
    public const string CopilotModelEnvVar = "A365_EVAL_COPILOT_MODEL";

    /// <summary>Environment variable that overrides the Claude Code model.</summary>
    public const string ClaudeModelEnvVar = "A365_EVAL_CLAUDE_MODEL";

    /// <summary>Environment variable holding the Azure OpenAI endpoint, including the
    /// API path (e.g. https://my-resource.services.ai.azure.com/openai/v1).</summary>
    public const string AzureOpenAiEndpointEnvVar = "A365_EVAL_AZURE_OPENAI_ENDPOINT";

    /// <summary>Environment variable holding the Azure OpenAI deployment (model) name.</summary>
    public const string AzureOpenAiDeploymentEnvVar = "A365_EVAL_AZURE_OPENAI_DEPLOYMENT";

    private const string DefaultCopilotModel = "claude-haiku-4.5";
    private const string DefaultClaudeModel = "haiku";
    private const string DefaultAzureOpenAiScope = "https://ai.azure.com/.default";

    /// <summary>Copilot model ID; overridable via <see cref="CopilotModelEnvVar"/>.</summary>
    public static string CopilotModel => FromEnvOrDefault(CopilotModelEnvVar, DefaultCopilotModel);

    /// <summary>Claude model ID/alias; overridable via <see cref="ClaudeModelEnvVar"/>.</summary>
    public static string ClaudeModel => FromEnvOrDefault(ClaudeModelEnvVar, DefaultClaudeModel);

    /// <summary>Azure OpenAI endpoint, or null when unset. There is no default: it is
    /// resource-specific and must be supplied via <see cref="AzureOpenAiEndpointEnvVar"/>.</summary>
    public static string? AzureOpenAiEndpoint => NullIfBlank(Environment.GetEnvironmentVariable(AzureOpenAiEndpointEnvVar));

    /// <summary>Azure OpenAI deployment (model) name, or null when unset. Supplied via
    /// <see cref="AzureOpenAiDeploymentEnvVar"/>.</summary>
    public static string? AzureOpenAiDeployment => NullIfBlank(Environment.GetEnvironmentVariable(AzureOpenAiDeploymentEnvVar));

    /// <summary>Entra ID token scope for Azure OpenAI (fixed; not user-configurable).</summary>
    public static string AzureOpenAiScope => DefaultAzureOpenAiScope;

    /// <summary>Environment variable overriding how many checks the Azure OpenAI judge scores concurrently.</summary>
    public const string AzureOpenAiMaxConcurrencyEnvVar = "A365_EVAL_AZURE_OPENAI_MAX_CONCURRENCY";

    private const int DefaultAzureOpenAiMaxConcurrency = 100;

    /// <summary>Max concurrent Azure OpenAI scoring calls; overridable via
    /// <see cref="AzureOpenAiMaxConcurrencyEnvVar"/>. Clamped to [1, 4096]. Higher is faster but
    /// more likely to hit endpoint rate limits (429), which are retried with backoff per call.</summary>
    public static int AzureOpenAiMaxConcurrency
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(AzureOpenAiMaxConcurrencyEnvVar);
            return int.TryParse(raw, out var n) && n >= 1 ? Math.Min(n, 4096) : DefaultAzureOpenAiMaxConcurrency;
        }
    }

    private static string FromEnvOrDefault(string envVar, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string? NullIfBlank(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        // Strip a single pair of wrapping quotes that copy-paste from a portal or `SET VAR="..."` can leave.
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
