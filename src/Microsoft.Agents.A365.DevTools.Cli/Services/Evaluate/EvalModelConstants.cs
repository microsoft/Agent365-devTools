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

    private const string DefaultCopilotModel = "claude-haiku-4.5";
    private const string DefaultClaudeModel = "haiku";

    /// <summary>Copilot model ID; overridable via <see cref="CopilotModelEnvVar"/>.</summary>
    public static string CopilotModel => FromEnvOrDefault(CopilotModelEnvVar, DefaultCopilotModel);

    /// <summary>Claude model ID/alias; overridable via <see cref="ClaudeModelEnvVar"/>.</summary>
    public static string ClaudeModel => FromEnvOrDefault(ClaudeModelEnvVar, DefaultClaudeModel);

    private static string FromEnvOrDefault(string envVar, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
