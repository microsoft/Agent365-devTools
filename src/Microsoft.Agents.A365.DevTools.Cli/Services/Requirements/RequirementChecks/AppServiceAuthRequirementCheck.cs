// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Validates that the Azure App Service deployment token is valid and not expired or revoked.
/// Probes the App Service token scope explicitly, which is not covered by AzureAuthRequirementCheck.
/// Catches stale/revoked grants (AADSTS50173) before build and upload begin.
/// </summary>
public class AppServiceAuthRequirementCheck : RequirementCheck
{
    private readonly AzureAuthValidator _auth;

    public AppServiceAuthRequirementCheck(AzureAuthValidator auth)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
    }

    /// <inheritdoc />
    public override string Name => "App Service Authentication";

    /// <inheritdoc />
    public override string Description => "Validates that the Azure App Service deployment token is valid and not expired or revoked";

    /// <inheritdoc />
    public override string Category => "Azure";

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
        var success = await _auth.GetAppServiceTokenAsync(cancellationToken);
        return success
            ? RequirementCheckResult.Success()
            : RequirementCheckResult.Failure(
                "Azure App Service token is expired or revoked",
                "Run 'az logout' then 'az login --tenant <tenantId>' and retry");
    }
}
