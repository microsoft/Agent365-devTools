// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Ensures MOS service principals exist in the tenant, creating and configuring them if absent.
/// Wraps PublishHelpers.EnsureMosPrerequisitesAsync so it runs before the interactive manifest
/// editing pause, preventing wasted work if MOS prerequisites are not configured.
/// </summary>
public class MosPrerequisitesRequirementCheck : RequirementCheck
{
    private readonly GraphApiService _graphApiService;
    private readonly AgentBlueprintService _blueprintService;

    public MosPrerequisitesRequirementCheck(GraphApiService graphApiService, AgentBlueprintService blueprintService)
    {
        _graphApiService = graphApiService ?? throw new ArgumentNullException(nameof(graphApiService));
        _blueprintService = blueprintService ?? throw new ArgumentNullException(nameof(blueprintService));
    }

    /// <inheritdoc />
    public override string Name => "MOS Prerequisites";

    /// <inheritdoc />
    public override string Description => "Ensures MOS service principals exist in tenant, creating and configuring them if absent";

    /// <inheritdoc />
    public override string Category => "MOS";

    /// <inheritdoc />
    public override async Task<RequirementCheckResult> CheckAsync(
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteCheckWithLoggingAsync(config, logger, CheckImplementationAsync, cancellationToken);
    }

    private async Task<RequirementCheckResult> CheckImplementationAsync(
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var ok = await PublishHelpers.EnsureMosPrerequisitesAsync(
                _graphApiService, _blueprintService, config, logger, cancellationToken);

            return ok
                ? RequirementCheckResult.Success()
                : RequirementCheckResult.Failure(
                    "MOS service principals not configured",
                    "Run 'a365 setup all' to configure MOS prerequisites");
        }
        catch (SetupValidationException ex)
        {
            // EnsureMosPrerequisitesAsync throws SetupValidationException for unrecoverable
            // failures (e.g., insufficient privileges). Convert to Failure so the check
            // framework returns [FAIL] with guidance rather than an unhandled exception.
            var resolution = ex.MitigationSteps.Count > 0
                ? string.Join("\n", ex.MitigationSteps)
                : "Run 'a365 setup all' to configure MOS prerequisites";
            return RequirementCheckResult.Failure(ex.Message, resolution);
        }
    }
}
