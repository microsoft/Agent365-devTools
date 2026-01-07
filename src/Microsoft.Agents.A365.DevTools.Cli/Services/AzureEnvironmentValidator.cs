// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Validates Azure CLI environment and provides recommendations for optimal performance.
/// </summary>
public interface IAzureEnvironmentValidator
{
    /// <summary>
    /// Validates Azure CLI environment and warns about performance issues.
    /// </summary>
    /// <returns>True if validation passes (warnings don't fail validation)</returns>
    Task<bool> ValidateEnvironmentAsync();
}

public class AzureEnvironmentValidator : IAzureEnvironmentValidator
{
    private readonly CommandExecutor _executor;
    private readonly ILogger<AzureEnvironmentValidator> _logger;

    public AzureEnvironmentValidator(CommandExecutor executor, ILogger<AzureEnvironmentValidator> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ValidateEnvironmentAsync()
    {
        try
        {
            await ValidateAzureCliArchitectureAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate Azure CLI environment");
            return true; // Don't fail setup for validation issues
        }
    }

    private async Task ValidateAzureCliArchitectureAsync()
    {
        // Only check on Windows 64-bit systems
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !Environment.Is64BitOperatingSystem)
        {
            return;
        }

        var result = await _executor.ExecuteAsync("az", "--version");
        if (result.ExitCode != 0)
        {
            _logger.LogWarning("Could not determine Azure CLI version for environment validation");
            return;
        }

        // Check if Azure CLI is using 32-bit Python on 64-bit Windows
        if (result.StandardOutput.Contains("32 bit", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Azure CLI is using 32-bit Python on 64-bit Windows (performance may be affected)");
            _logger.LogWarning("For optimal performance, consider reinstalling Azure CLI with 64-bit Python");
            _logger.LogWarning("For more information, see: https://learn.microsoft.com/cli/azure/install-azure-cli-windows");
        }
        else if (result.StandardOutput.Contains("64 bit", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Azure CLI is using 64-bit Python (optimal)");
        }
    }
}