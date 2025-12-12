// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Helper class for detecting and resolving Azure tenant IDs
/// </summary>
public static class TenantDetectionHelper
{
    /// <summary>
    /// Detects tenant ID from configuration or Azure CLI context
    /// </summary>
    /// <param name="config">Optional configuration containing tenant ID</param>
    /// <param name="logger">Logger for output messages</param>
    /// <returns>Detected tenant ID or null if not found</returns>
    public static async Task<string?> DetectTenantIdAsync(Agent365Config? config, ILogger logger)
    {
        // First, try to get tenant ID from config
        if (config != null && !string.IsNullOrWhiteSpace(config.TenantId))
        {
            return config.TenantId;
        }

        // When config is not available or tenant ID is missing, try to detect from Azure CLI
        logger.LogInformation("No tenant ID in config. Attempting to detect from Azure CLI context...");

        try
        {
            var executor = new CommandExecutor(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CommandExecutor>.Instance);

            var result = await executor.ExecuteAsync(
                "az",
                "account show --query tenantId -o tsv",
                captureOutput: true,
                suppressErrorLogging: true);

            if (result.Success && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                var tenantId = result.StandardOutput.Trim();
                logger.LogInformation("Detected tenant ID from Azure CLI: {TenantId}", tenantId);
                return tenantId;
            }
            else
            {
                logger.LogWarning("Could not detect tenant ID from Azure CLI.");
                logger.LogWarning("You may need to run 'az login' first.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to detect tenant ID from Azure CLI: {Message}", ex.Message);
        }

        // Log guidance for users
        logger.LogInformation("");
        logger.LogInformation("For best results, either:");
        logger.LogInformation("  1. Run 'az login' to set Azure CLI context");
        logger.LogInformation("  2. Create a config file with: a365 config init");
        logger.LogInformation("");

        return null;
    }
}
