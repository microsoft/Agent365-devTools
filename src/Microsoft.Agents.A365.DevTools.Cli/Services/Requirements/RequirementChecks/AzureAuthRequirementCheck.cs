// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Validates Azure CLI authentication and active subscription.
/// Delegates entirely to AzureAuthValidator which handles all user-facing logging.
/// </summary>
public class AzureAuthRequirementCheck : RequirementCheck
{
    private readonly AzureAuthValidator _authValidator;

    public AzureAuthRequirementCheck(AzureAuthValidator authValidator)
    {
        _authValidator = authValidator ?? throw new ArgumentNullException(nameof(authValidator));
    }

    /// <inheritdoc />
    public override string Name => "Azure Authentication";

    /// <inheritdoc />
    public override string Description => "Validates Azure CLI authentication and active subscription";

    /// <inheritdoc />
    public override string Category => "Azure";

    /// <inheritdoc />
    public override async Task<RequirementCheckResult> CheckAsync(
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // AzureAuthValidator logs all detailed user-facing messages internally.
        // This adapter converts the bool result into a RequirementCheckResult.
        var authenticated = await _authValidator.ValidateAuthenticationAsync(config.SubscriptionId);

        if (!authenticated)
        {
            return RequirementCheckResult.Failure(
                "Azure CLI authentication failed or the active subscription does not match the configured subscriptionId",
                "Run 'az login' to authenticate, then 'az account set --subscription <id>' to select the correct subscription");
        }

        return RequirementCheckResult.Success();
    }
}
