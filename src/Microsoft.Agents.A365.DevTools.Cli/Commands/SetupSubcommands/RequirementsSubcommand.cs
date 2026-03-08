// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
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
        IConfigService configService,
        AzureAuthValidator authValidator,
        IClientAppValidator clientAppValidator)
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
            logger.LogInformation(new string('-', 28));
            logger.LogInformation("Validating prerequisites for setup...");

            try
            {
                // Load configuration
                var setupConfig = await configService.LoadAsync(config.FullName);
                var requirementChecks = GetRequirementChecks(authValidator, clientAppValidator);
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
            Console.WriteLine();
        }

        // Group checks by category for organized output (categories not printed yet)
        var checksByCategory = requirementChecks.GroupBy(c => c.Category).ToList();

        var totalChecks = requirementChecks.Count;
        var passedChecks = 0;
        var warningChecks = 0;
        var failedChecks = 0;

        logger.LogInformation("Checking requirements...");

        // Execute all checks (grouped by category but headers not shown)
        foreach (var categoryGroup in checksByCategory)
        {
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
            }
        }

        Console.WriteLine();
        logger.LogInformation("Requirements: {Passed} passed, {Warning} warnings, {Failed} failed",
            passedChecks, warningChecks, failedChecks);

        return failedChecks == 0;
    }

    /// <summary>
    /// Runs checks with formatted [PASS]/[FAIL] output and exits if any fail.
    /// Use this instead of RunRequirementChecksAsync when failure should abort the command.
    /// </summary>
    public static async Task RunChecksOrExitAsync(
        List<IRequirementCheck> checks,
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var passed = await RunRequirementChecksAsync(checks, config, logger, category: null, cancellationToken);
        if (!passed)
        {
            logger.LogError("Operation cannot proceed due to failed requirement checks above. Please fix the issues and retry.");
            ExceptionHandler.ExitWithCleanup(1);
        }
    }

    /// <summary>
    /// Gets all available requirement checks.
    /// Derived from the union of system and config checks to keep a single source of truth.
    /// </summary>
    public static List<IRequirementCheck> GetRequirementChecks(AzureAuthValidator authValidator, IClientAppValidator clientAppValidator)
    {
        return GetSystemRequirementChecks()
            .Concat(GetConfigRequirementChecks(authValidator, clientAppValidator))
            .ToList();
    }

    /// <summary>
    /// Gets system-level requirement checks that do not depend on configuration.
    /// These can be run before the configuration wizard to surface blockers early.
    /// </summary>
    private static List<IRequirementCheck> GetSystemRequirementChecks()
    {
        return new List<IRequirementCheck>
        {
            // Frontier Preview Program enrollment check
            new FrontierPreviewRequirementCheck(),

            // PowerShell modules required for Microsoft Graph operations
            new PowerShellModulesRequirementCheck(),
        };
    }

    /// <summary>
    /// Gets configuration-dependent requirement checks that must run after the configuration is loaded.
    /// </summary>
    private static List<IRequirementCheck> GetConfigRequirementChecks(AzureAuthValidator authValidator, IClientAppValidator clientAppValidator)
    {
        return new List<IRequirementCheck>
        {
            // Azure CLI authentication — required before any Azure operation
            new AzureAuthRequirementCheck(authValidator),

            // Location configuration — required for endpoint registration
            new LocationRequirementCheck(),

            // Client app configuration validation
            new ClientAppRequirementCheck(clientAppValidator),
        };
    }
}
