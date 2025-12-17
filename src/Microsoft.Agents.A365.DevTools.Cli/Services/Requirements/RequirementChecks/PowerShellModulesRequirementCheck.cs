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
            return RequirementCheckResult.Failure(
                errorMessage: "PowerShell is not available on this system",
                resolutionGuidance: "Install PowerShell 7+ from https://docs.microsoft.com/powershell/scripting/install/installing-powershell",
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

        // Return results
        if (missingModules.Count == 0)
        {
            return RequirementCheckResult.Success(
                details: $"All required PowerShell modules are installed: {string.Join(", ", installedModules.Select(m => m.Name))}"
            );
        }

        var missingModuleNames = string.Join(", ", missingModules.Select(m => m.Name));
        var installCommands = GenerateInstallationInstructions(missingModules);

        return RequirementCheckResult.Failure(
            errorMessage: $"Missing required PowerShell modules: {missingModuleNames}",
            resolutionGuidance: installCommands,
            details: $"These modules are required for Microsoft Graph operations, app registration, and Azure authentication. " +
                    $"Missing: {missingModuleNames}. " +
                    $"Installed: {string.Join(", ", installedModules.Select(m => m.Name))}"
        );
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
            return (false, null);
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

        // PowerShell 7+ command
        var moduleNames = string.Join(",", missingModules.Select(m => $"'{m.Name}'"));
        instructions.Add($"  pwsh -Command \"Install-Module -Name '{moduleNames}' -Scope CurrentUser -Force\"");
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