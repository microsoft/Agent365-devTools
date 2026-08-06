// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// Typed metadata for structured validation report output.
/// Attached to RequirementCheckResult.Metadata by validate-specific checks.
/// </summary>
public sealed class RequirementCheckMetadata
{
    /// <summary>Port the app is running on (boot tier).</summary>
    public int? Port { get; init; }

    /// <summary>Time in milliseconds for the app to respond (boot tier).</summary>
    public long? BootMs { get; init; }

    /// <summary>Build or runtime log output (build/boot tier).</summary>
    public string? Log { get; init; }

    /// <summary>Process exit code (build tier).</summary>
    public int? ExitCode { get; init; }

    /// <summary>Detected platform name (build/boot tier).</summary>
    public string? Platform { get; init; }

    /// <summary>Conversation turn results (conversation tier).</summary>
    public List<ConversationTurnMetadata>? Turns { get; init; }

    /// <summary>Whether AgentsPlayground was launched for interactive testing.</summary>
    public bool? PlaygroundLaunched { get; init; }

    /// <summary>
    /// Path to the agent's captured console output log file.
    /// Written during the conversation step; used by telemetry check and referenced in the report.
    /// </summary>
    public string? AgentConsoleLogPath { get; init; }

    /// <summary>
    /// Path to the MSBuild file log written during project build validation.
    /// </summary>
    public string? BuildLogFile { get; init; }

    /// <summary>
    /// Path to the boot log file written during local runtime validation.
    /// </summary>
    public string? BootLogFile { get; init; }

    /// <summary>
    /// Path to the conversation log file written during conversation validation.
    /// Contains HTTP request/response details for each turn.
    /// </summary>
    public string? ConversationLogFile { get; init; }

    /// <summary>
    /// Resolved path to the uv command, set during build dependency install.
    /// Used by the boot step to run Python agents in uv-managed projects.
    /// </summary>
    public string? ResolvedUvCommand { get; init; }

    /// <summary>Whether the blueprint application exists in Entra ID.</summary>
    public bool? AppExists { get; init; }

    /// <summary>Whether a service principal exists for the blueprint.</summary>
    public bool? ServicePrincipalExists { get; init; }

    /// <summary>Whether the agent registration exists (null if not configured).</summary>
    public bool? RegistrationExists { get; init; }

    /// <summary>Resource permission results from comparing config vs Entra.</summary>
    public List<BlueprintResourcePermission>? ResourcePermissions { get; set; }

    /// <summary>
    /// Path to the persisted pre-conversation MAC metrics baseline file.
    /// </summary>
    public string? MacMetricsBaselineFile { get; init; }

    /// <summary>
    /// Flattened numeric metrics captured before conversation.
    /// </summary>
    public Dictionary<string, double>? MacBaselineMetrics { get; init; }

    /// <summary>
    /// Flattened numeric metrics captured after conversation.
    /// </summary>
    public Dictionary<string, double>? MacCurrentMetrics { get; init; }

    /// <summary>
    /// Per-metric comparison outcome between baseline and post-conversation snapshots.
    /// </summary>
    public List<MacMetricComparisonMetadata>? MacMetricComparisons { get; init; }

    /// <summary>
    /// Whether conversation simulation completion was verified before MAC comparison.
    /// </summary>
    public bool? ConversationStepVerified { get; init; }
}
