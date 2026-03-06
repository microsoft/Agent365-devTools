// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Validates Azure infrastructure configuration fields required for Azure-hosted deployments.
/// Skips all checks when NeedDeployment is false (external messaging endpoint scenario).
/// No external service calls — pure configuration validation.
/// </summary>
public class InfrastructureRequirementCheck : RequirementCheck
{
    /// <inheritdoc />
    public override string Name => "Infrastructure Configuration";

    /// <inheritdoc />
    public override string Description => "Validates Azure infrastructure configuration fields (subscription, resource group, app service plan, web app, location, SKU)";

    /// <inheritdoc />
    public override string Category => "Configuration";

    /// <inheritdoc />
    public override Task<RequirementCheckResult> CheckAsync(
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!config.NeedDeployment)
            return Task.FromResult(RequirementCheckResult.Success());

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.SubscriptionId))
            errors.Add("subscriptionId is required for Azure hosting");

        if (string.IsNullOrWhiteSpace(config.ResourceGroup))
            errors.Add("resourceGroup is required for Azure hosting");

        if (string.IsNullOrWhiteSpace(config.AppServicePlanName))
            errors.Add("appServicePlanName is required for Azure hosting");

        if (string.IsNullOrWhiteSpace(config.WebAppName))
            errors.Add("webAppName is required for Azure hosting");

        if (string.IsNullOrWhiteSpace(config.Location))
            errors.Add("location is required for Azure hosting");

        var sku = string.IsNullOrWhiteSpace(config.AppServicePlanSku)
            ? ConfigConstants.DefaultAppServicePlanSku
            : config.AppServicePlanSku;

        if (!IsValidAppServicePlanSku(sku))
            errors.Add($"Invalid appServicePlanSku '{sku}'. Valid SKUs: F1 (Free), B1/B2/B3 (Basic), S1/S2/S3 (Standard), P1V2/P2V2/P3V2 (Premium V2), P1V3/P2V3/P3V3 (Premium V3)");

        if (errors.Count > 0)
        {
            return Task.FromResult(RequirementCheckResult.Failure(
                string.Join("; ", errors),
                "Update the missing or invalid fields in a365.config.json and run again"));
        }

        return Task.FromResult(RequirementCheckResult.Success());
    }

    private static bool IsValidAppServicePlanSku(string sku)
    {
        if (string.IsNullOrWhiteSpace(sku))
            return false;

        var validSkus = new[]
        {
            // Free tier
            "F1",
            // Basic tier
            "B1", "B2", "B3",
            // Standard tier
            "S1", "S2", "S3",
            // Premium V2
            "P1V2", "P2V2", "P3V2",
            // Premium V3
            "P1V3", "P2V3", "P3V3",
            // Isolated (less common)
            "I1", "I2", "I3",
            "I1V2", "I2V2", "I3V2"
        };

        return validSkus.Contains(sku, StringComparer.OrdinalIgnoreCase);
    }
}
