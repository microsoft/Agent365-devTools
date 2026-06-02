// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;

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
        IClientAppValidator clientAppValidator,
        CommandExecutor executor,
        GraphApiService graphApiService,
        IEnumerable<IRequirementCheck>? requirementChecksOverride = null)
    {
        var command = new Command("requirements",
            "Validate prerequisites for Agent 365 setup\n" +
            "Runs modular requirement checks and provides guidance for any issues found\n\n" +
            "This command will:\n" +
            "  - Check all prerequisites needed for Agent 365 setup\n" +
            "  - Report any issues with detailed resolution guidance\n" +
            "  - Continue checking all requirements even if some fail\n" +
            "  - Provide a summary of all checks at the end\n\n");

        var categoryOption = new Option<string?>(
            ["--category"],
            description: "Run checks for a specific category only (e.g., 'Azure', 'Authentication', 'PowerShell', 'Tenant Enrollment')");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging");

        command.AddOption(categoryOption);
        command.AddOption(verboseOption);

        command.SetHandler(async (InvocationContext context) =>
        {
            var category = context.ParseResult.GetValueForOption(categoryOption);
            var ct = context.GetCancellationToken();

            logger.LogInformation("Agent 365 Requirements Check");
            logger.LogInformation(new string('-', 28));
            logger.LogInformation("Validating prerequisites for setup...");

            try
            {
                // If an override is supplied (tests), run it against an empty config and return the result.
                if (requirementChecksOverride is not null)
                {
                    var overrideChecks = requirementChecksOverride.ToList();
                    var overridePassed = await RunRequirementChecksAsync(overrideChecks, new Agent365Config(), logger, category, ct);
                    if (!overridePassed)
                    {
                        context.ExitCode = 1;
                    }
                    return;
                }

                var allPassed = true;

                // Pre-filter by category so we can emit a single warning if nothing matches,
                // and skip the Entra bootstrap entirely when only system checks match.
                var systemChecks = FilterByCategory(GetSystemRequirementChecks(), category);
                var configChecks = FilterByCategory(GetConfigRequirementChecks(authValidator, clientAppValidator), category);

                if (systemChecks.Count == 0 && configChecks.Count == 0 && !string.IsNullOrWhiteSpace(category))
                {
                    logger.LogWarning("No requirement checks found for category '{Category}'", category);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(category))
                    logger.LogInformation("Running checks for category: {Category}", category);

                if (systemChecks.Count > 0)
                {
                    var systemPassed = await RunRequirementChecksAsync(systemChecks, new Agent365Config(), logger, ct: ct);
                    allPassed = allPassed && systemPassed;
                }

                // Skip the Entra bootstrap entirely when the category matches only system checks.
                if (configChecks.Count > 0)
                {
                    var configForChecks = await ResolveConfigForChecksAsync(configService, executor, graphApiService, logger, ct);
                    if (configForChecks is null)
                    {
                        // Informational message already logged by ResolveConfigForChecksAsync — skip config checks.
                        if (!allPassed) context.ExitCode = 1;
                        return;
                    }

                    var configPassed = await RunRequirementChecksAsync(configChecks, configForChecks, logger, ct: ct);
                    allPassed = allPassed && configPassed;
                }

                if (!allPassed)
                {
                    context.ExitCode = 1;
                }
            }
            catch (Exception ex)
            {
                logger.LogError("Requirements check failed: {Message}", ex.Message);
                logger.LogDebug(ex, "Requirements check failed exception details");
                context.ExitCode = 1;
            }
        });

        return command;
    }

    /// <summary>
    /// Resolves the <see cref="Agent365Config"/> used by config-dependent requirement checks.
    /// If <c>a365.config.json</c> is present, it is loaded and returned as-is. Otherwise a
    /// minimal bootstrap config is synthesized from the current Azure CLI context plus a
    /// well-known-name lookup for the Agent 365 CLI client app. Returns <c>null</c> when
    /// the tenant cannot be determined or the well-known client app cannot be resolved —
    /// in which case config-dependent checks should be skipped.
    /// </summary>
    private static async Task<Agent365Config?> ResolveConfigForChecksAsync(
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        ILogger logger,
        CancellationToken ct)
    {
        var localConfigPath = Path.Combine(Directory.GetCurrentDirectory(), ConfigConstants.DefaultConfigFileName);
        if (File.Exists(localConfigPath))
        {
            return await configService.LoadAsync(localConfigPath);
        }

        // No config file — try to synthesize a minimal config from the Azure CLI context.
        var tenantId = await SetupHelpers.ResolveBootstrapTenantIdAsync(null, executor, logger);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            logger.LogInformation("Run 'az login' first to validate Azure prerequisites.");
            return null;
        }

        logger.LogDebug("No a365.config.json found. Resolving client app from Entra...");
        var clientAppId = await SetupHelpers.ResolveBootstrapClientAppIdAsync(tenantId, graphApiService, logger, ct);
        if (string.IsNullOrWhiteSpace(clientAppId))
        {
            logger.LogInformation("Agent 365 CLI app not found in tenant — client app validation skipped.");
            return null;
        }

        return new Agent365Config
        {
            TenantId = tenantId,
            ClientAppId = clientAppId,
        };
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

        // Execute all checks (grouped by category but headers not shown). Each check logs its
        // own Pass/Warn/Fail line; indent them one level under the "Checking requirements..." header.
        using (logger.Indent())
        {
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

            logger.LogInformation("Requirements: {Passed} passed, {Warning} warnings, {Failed} failed",
                passedChecks, warningChecks, failedChecks);
        }

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

            // Client app configuration validation (checks all required Graph permissions incl. UpdateAuthProperties.All)
            new ClientAppRequirementCheck(clientAppValidator),
        };
    }

    private static List<IRequirementCheck> FilterByCategory(List<IRequirementCheck> checks, string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return checks;
        return checks.Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
