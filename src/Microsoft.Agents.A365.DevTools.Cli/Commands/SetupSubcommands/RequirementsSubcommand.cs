// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Requirements subcommand - Validates prerequisites for Agent 365 setup
/// Executes modular requirement checks and provides guidance for resolution
/// </summary>
internal static class RequirementsSubcommand
{
    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService)
    {
        var command = new Command("requirements", 
            "Validate prerequisites for Agent 365 setup\n" +
            "Runs modular requirement checks and provides guidance for any issues found\n\n" +
            "This command will:\n" +
            "  - Check all prerequisites needed for Agent 365 setup\n" +
            "  - Report any issues with detailed resolution guidance\n" +
            "  - Continue checking all requirements even if some fail\n" +
            "  - Provide a summary of all checks at the end\n\n");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Show detailed output for all checks");

        var categoryOption = new Option<string?>(
            ["--category"],
            description: "Run checks for a specific category only (e.g., 'Azure', 'Authentication', 'Configuration')");

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(categoryOption);

        command.SetHandler(async (config, verbose, category) =>
        {
            logger.LogInformation("Agent 365 Requirements Check");
            logger.LogInformation("Validating prerequisites for setup...");
            logger.LogInformation("");

            try
            {
                // Load configuration
                var setupConfig = await configService.LoadAsync(config.FullName);
                var requirementChecks = GetRequirementChecks();
                await RunRequirementChecksAsync(requirementChecks, setupConfig, logger, category);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Requirements check failed: {Message}", ex.Message);
            }
        }, configOption, verboseOption, categoryOption);

        return command;
    }

    public static async Task<bool> RunRequirementChecksAsync(
        List<IRequirementCheck> requirementChecks,
        Agent365Config setupConfig,
        ILogger logger,
        string? category = null,
        CancellationToken ct = default)
    {
        // Filter by category if specified
        if (!string.IsNullOrWhiteSpace(category))
        {
            requirementChecks = requirementChecks
                .Where(check => string.Equals(check.Category, category, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (requirementChecks.Count == 0)
            {
                logger.LogWarning("No requirement checks found for category '{Category}'", category);
                return true;
            }

            logger.LogInformation("Running checks for category: {Category}", category);
            logger.LogInformation("");
        }

        // Group checks by category for organized output
        var checksByCategory = requirementChecks.GroupBy(c => c.Category).ToList();

        var totalChecks = requirementChecks.Count;
        var passedChecks = 0;
        var warningChecks = 0;
        var failedChecks = 0;

        // Execute all checks
        foreach (var categoryGroup in checksByCategory)
        {
            logger.LogInformation("Category: {Category}", categoryGroup.Key);
            logger.LogInformation(new string('-', 50));

            foreach (var check in categoryGroup)
            {
                var result = await check.CheckAsync(setupConfig, logger, ct);

                if (result.Passed)
                {
                    if (result.IsWarning)
                    {
                        warningChecks++;
                    }
                    else
                    {
                        passedChecks++;
                    }
                }
                else
                {
                    failedChecks++;
                }

                // Add spacing between checks for readability
                logger.LogInformation("");
            }
        }

        // Display summary
        logger.LogInformation("Requirements Check Summary");
        logger.LogInformation(new string('=', 50));
        logger.LogInformation("Total checks: {Total}", totalChecks);
        logger.LogInformation("Passed: {Passed}", passedChecks);
        logger.LogInformation("Warning: {Warning}", warningChecks);
        logger.LogInformation("Failed: {Failed}", failedChecks);
        logger.LogInformation("");

        if (failedChecks > 0)
        {
            logger.LogError("Some requirements failed. Please address the issues above before running setup.");
            logger.LogInformation("Use the resolution guidance provided for each failed check.");
        }
        else if (warningChecks > 0)
        {
            logger.LogWarning("All automated checks passed, but {WarningCount} requirement(s) require manual verification.", warningChecks);
            logger.LogInformation("Please review the warnings above and ensure all requirements are met before running setup.");
        }
        else
        {
            logger.LogInformation("All requirements passed! You're ready to run Agent 365 setup.");
        }

        return failedChecks == 0;
    }

    /// <summary>
    /// Gets all available requirement checks
    /// </summary>
    public static List<IRequirementCheck> GetRequirementChecks()
    {
        return new List<IRequirementCheck>
        {
            // Frontier Preview Program enrollment check
            new FrontierPreviewRequirementCheck(),
            
            // PowerShell modules required for Microsoft Graph operations
            new PowerShellModulesRequirementCheck(),
            
            // Additional checks can be added here
        };
    }
}