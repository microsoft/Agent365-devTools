// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Requirement check that validates necessary PowerShell modules used in setup and deploy commands are installed
/// Checks for Microsoft Graph modules and provides installation instructions if missing
/// </summary>
public class PowerShellModulesRequirementCheck : RequirementCheck
{
    /// <inheritdoc />
    public override string Name => "PowerShell Modules";

    /// <inheritdoc />
    public override string Description => "Validates that Powershell 7+ and required PowerShell modules are installed for setup and deployment operations";

    /// <inheritdoc />
    public override string Category => "PowerShell";

    /// <summary>
    /// Required PowerShell modules for Agent 365 operations
    /// </summary>
    private static readonly RequiredModule[] RequiredModules =
    {
        new("Microsoft.Graph.Authentication", "Microsoft Graph authentication module for token management"),
        new("Microsoft.Graph.Applications", "Microsoft Graph applications module for app registration operations")
    };

    /// <inheritdoc />
    public override async Task<RequirementCheckResult> CheckAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken = default)
    {
        return await ExecuteCheckWithLoggingAsync(config, logger, CheckImplementationAsync, cancellationToken);
    }

    /// <summary>
    /// The actual implementation of the PowerShell modules requirement check
    /// </summary>
    private async Task<RequirementCheckResult> CheckImplementationAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Checking if PowerShell is available on this system...");

        // Check if PowerShell is available
        var powerShellAvailable = await CheckPowerShellAvailabilityAsync(logger, cancellationToken);
        if (!powerShellAvailable)
        {
            bool isWsl = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"))
                         || await IsWslEnvironmentAsync(cancellationToken);

            var resolution = isWsl
                ? "Install PowerShell 7+ in your WSL distribution.\n" +
                  "Installation steps vary by Linux distribution. Follow the official guidance for your distro:\n" +
                  "  https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell-on-linux"
                : "Install PowerShell 7+ from https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell";

            return RequirementCheckResult.Failure(
                errorMessage: "PowerShell is not available on this system",
                resolutionGuidance: resolution,
                details: "PowerShell is required for Microsoft Graph operations and Azure authentication"
            );
        }

        logger.LogInformation("Checking PowerShell modules...");
        var missingModules = new List<RequiredModule>();
        var installedModules = new List<RequiredModule>();

        // Check each required module
        foreach (var module in RequiredModules)
        {
            logger.LogDebug("Checking module: {ModuleName}", module.Name);
            
            var isInstalled = await CheckModuleInstalledAsync(module.Name, logger, cancellationToken);
            if (isInstalled)
            {
                installedModules.Add(module);
                logger.LogDebug("Module {ModuleName} is installed", module.Name);
            }
            else
            {
                missingModules.Add(module);
                logger.LogDebug("Module {ModuleName} is missing", module.Name);
            }
        }

        // All modules present - done
        if (missingModules.Count == 0)
        {
            return RequirementCheckResult.Success(
                details: $"All required PowerShell modules are installed: {string.Join(", ", installedModules.Select(m => m.Name))}"
            );
        }

        // Attempt auto-install for missing modules
        logger.LogInformation("Attempting to auto-install missing PowerShell modules...");
        var autoInstalled = new List<RequiredModule>();
        var stillMissing = new List<RequiredModule>();

        foreach (var module in missingModules)
        {
            logger.LogInformation("Installing {ModuleName}...", module.Name);
            var installSuccess = await InstallModuleAsync(module.Name, logger, cancellationToken);

            if (installSuccess)
            {
                var verified = await CheckModuleInstalledAsync(module.Name, logger, cancellationToken);
                if (verified)
                {
                    autoInstalled.Add(module);
                    logger.LogInformation("Successfully installed {ModuleName}", module.Name);
                }
                else
                {
                    stillMissing.Add(module);
                    logger.LogWarning("Install succeeded but {ModuleName} not found in module path after install", module.Name);
                }
            }
            else
            {
                stillMissing.Add(module);
            }
        }

        if (stillMissing.Count == 0)
        {
            var autoInstalledNames = string.Join(", ", autoInstalled.Select(m => m.Name));
            var alreadyInstalled = installedModules.Count > 0
                ? $" Previously installed: {string.Join(", ", installedModules.Select(m => m.Name))}."
                : string.Empty;
            return RequirementCheckResult.Success(
                details: $"Auto-installed missing modules: {autoInstalledNames}.{alreadyInstalled}"
            );
        }

        // Some modules could not be auto-installed
        var stillMissingNames = string.Join(", ", stillMissing.Select(m => m.Name));
        var installCommands = GenerateInstallationInstructions(stillMissing);
        var autoInstalledNote = autoInstalled.Count > 0
            ? $"Auto-installed: {string.Join(", ", autoInstalled.Select(m => m.Name))}. "
            : string.Empty;

        return RequirementCheckResult.Failure(
            errorMessage: $"Missing required PowerShell modules (auto-install failed): {stillMissingNames}",
            resolutionGuidance: $"{autoInstalledNote}{installCommands}",
            details: $"These modules are required for Microsoft Graph operations. " +
                     $"Auto-install was attempted but failed for: {stillMissingNames}. " +
                     $"Installed: {string.Join(", ", installedModules.Concat(autoInstalled).Select(m => m.Name))}"
        );
    }

    /// <summary>
    /// Detects whether the process is running inside WSL (Windows Subsystem for Linux)
    /// by checking the WSL_DISTRO_NAME environment variable or /proc/version content.
    /// </summary>
    internal static async Task<bool> IsWslEnvironmentAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists("/proc/version"))
                return false;

            var procVersion = await File.ReadAllTextAsync("/proc/version", cancellationToken);
            return procVersion.Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if PowerShell is available on the system
    /// </summary>
    private async Task<bool> CheckPowerShellAvailabilityAsync(ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            // Check for PowerShell 7+ (pwsh)
            var result = await ExecutePowerShellCommandAsync("pwsh", "$PSVersionTable.PSVersion.Major", logger, cancellationToken);
            if (result.success && int.TryParse(result.output?.Trim(), out var major) && major >= 7)
            {
                logger.LogDebug("PowerShell availability check succeeded.");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug("PowerShell availability check failed: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Check if a specific PowerShell module is installed
    /// </summary>
    private async Task<bool> CheckModuleInstalledAsync(string moduleName, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var command = $"(Get-Module -ListAvailable -Name '{moduleName}' | Select-Object -First 1).Name";
            
            var result = await ExecutePowerShellCommandAsync("pwsh", command, logger, cancellationToken);
            if (!result.success || string.IsNullOrWhiteSpace(result.output))
            {
                return false;
            }

            // Check if the output contains the module name (case-insensitive)
            // Trim whitespace and check for exact match or partial match
            var output = result.output.Trim();
            return !string.IsNullOrWhiteSpace(output) && 
                   output.Contains(moduleName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Module check failed for {ModuleName}: {Error}", moduleName, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Attempts to install a PowerShell module using Install-Module with CurrentUser scope.
    /// Uses -Force and -AllowClobber to handle conflicts without requiring elevation.
    /// </summary>
    private async Task<bool> InstallModuleAsync(string moduleName, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var command = $"Install-Module -Name '{moduleName}' -Repository 'PSGallery' -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop";
            var result = await ExecutePowerShellCommandAsync("pwsh", command, logger, cancellationToken);
            if (!result.success)
            {
                logger.LogDebug("Auto-install failed for {ModuleName}: {Output}", moduleName, result.output);
            }
            return result.success;
        }
        catch (Exception ex)
        {
            logger.LogDebug("Auto-install threw exception for {ModuleName}: {Error}", moduleName, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Execute a PowerShell command and return the result
    /// </summary>
    private async Task<(bool success, string? output)> ExecutePowerShellCommandAsync(
        string executable, 
        string command, 
        ILogger logger, 
        CancellationToken cancellationToken)
    {
        try
        {
            var wrappedCommand = $"try {{ {command} }} catch {{ Write-Error $_.Exception.Message; exit 1 }}";
            var processStartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"-Command \"{wrappedCommand}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode == 0)
            {
                return (true, output);
            }
            
            logger.LogDebug("PowerShell command failed: {Error}", error);
            return (false, error);
        }
        catch (Exception ex)
        {
            logger.LogDebug("PowerShell execution failed: {Error}", ex.Message);
            return (false, null);
        }
    }

    /// <summary>
    /// Generate installation instructions for missing modules
    /// </summary>
    private static string GenerateInstallationInstructions(List<RequiredModule> missingModules)
    {
        var instructions = new List<string>
        {
            "Install the missing PowerShell modules using one of these methods:",
            "",
            "Method 1: Install all required modules at once"
        };

        // PowerShell 7+ command — pass quoted module names directly as an array literal (no outer quotes)
        var moduleNames = string.Join(", ", missingModules.Select(m => $"'{m.Name}'"));
        instructions.Add($"  pwsh -Command \"Install-Module -Name {moduleNames} -Scope CurrentUser -Force\"");
        instructions.Add("");

        // Individual module instructions
        instructions.Add("Method 2: Install modules individually");
        foreach (var module in missingModules)
        {
            instructions.Add($"  Install-Module -Name '{module.Name}' -Scope CurrentUser -Force");
        }

        instructions.Add("");
        instructions.Add("Notes:");
        instructions.Add("- Use -Scope CurrentUser to install without admin privileges");
        instructions.Add("- Use -Force to bypass confirmation prompts");
        instructions.Add("- Restart your terminal after installation");

        return string.Join(Environment.NewLine, instructions);
    }

    /// <summary>
    /// Represents a required PowerShell module
    /// </summary>
    private readonly record struct RequiredModule(string Name, string Description);
}