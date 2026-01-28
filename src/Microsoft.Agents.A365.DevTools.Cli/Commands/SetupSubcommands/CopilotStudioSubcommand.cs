// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// CopilotStudio permissions subcommand - Configures Power Platform CopilotStudio.Copilots.Invoke permission
/// Required Permissions: Global Administrator (for admin consent)
/// </summary>
internal static class CopilotStudioSubcommand
{
    /// <summary>
    /// Validates CopilotStudio permissions prerequisites without performing any actions.
    /// </summary>
    public static Task<List<string>> ValidateAsync(
        Agent365Config config,
        CancellationToken cancellationToken = default)
    {
        // Reuse the blueprint validation logic
        return ValidationHelper.ValidateBlueprintAsync(config, cancellationToken);
    }

    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService)
    {
        var command = new Command("copilotstudio",
            "Configure Power Platform CopilotStudio.Copilots.Invoke permission\n" +
            "Minimum required permissions: Global Administrator\n\n" +
            "Prerequisites: Blueprint (run 'a365 setup blueprint' first)");

        var configOption = new Option<FileInfo>(
            ["--config", "-c"],
            getDefaultValue: () => new FileInfo("a365.config.json"),
            description: "Configuration file path");

        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Show detailed output");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Show what would be done without executing");

        command.AddOption(configOption);
        command.AddOption(verboseOption);
        command.AddOption(dryRunOption);

        command.SetHandler(async (config, verbose, dryRun) =>
        {
            var setupConfig = await configService.LoadAsync(config.FullName);

            if (string.IsNullOrWhiteSpace(setupConfig.AgentBlueprintId))
            {
                logger.LogError("Blueprint ID not found. Run 'a365 setup blueprint' first.");
                Environment.Exit(1);
            }

            // Configure GraphApiService with custom client app ID if available
            if (!string.IsNullOrWhiteSpace(setupConfig.ClientAppId))
            {
                graphApiService.CustomClientAppId = setupConfig.ClientAppId;
            }

            if (dryRun)
            {
                logger.LogInformation("DRY RUN: Configure CopilotStudio Permissions");
                logger.LogInformation("Would configure Power Platform API permissions:");
                logger.LogInformation("  - Blueprint: {BlueprintId}", setupConfig.AgentBlueprintId);
                logger.LogInformation("  - Resource: Power Platform API ({ResourceAppId})", MosConstants.PowerPlatformApiResourceAppId);
                logger.LogInformation("  - Scopes: CopilotStudio.Copilots.Invoke");
                return;
            }

            await ConfigureAsync(
                config.FullName,
                logger,
                configService,
                executor,
                setupConfig,
                graphApiService,
                blueprintService,
                false);

        }, configOption, verboseOption, dryRunOption);

        return command;
    }

    /// <summary>
    /// Configures CopilotStudio permissions (OAuth2 grants and inheritable permissions).
    /// Public method that can be called by AllSubcommand.
    /// </summary>
    public static async Task<bool> ConfigureAsync(
        string configPath,
        ILogger logger,
        IConfigService configService,
        CommandExecutor executor,
        Models.Agent365Config setupConfig,
        GraphApiService graphService,
        AgentBlueprintService blueprintService,
        bool iSetupAll,
        SetupResults? setupResults = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("");
        logger.LogInformation("Configuring CopilotStudio permissions...");
        logger.LogInformation("");

        try
        {
            // Configure Power Platform API permissions for CopilotStudio
            // Note: Power Platform API is a first-party Microsoft service
            // We skip addToRequiredResourceAccess because the scopes may not be published there.
            await SetupHelpers.EnsureResourcePermissionsAsync(
                graphService,
                blueprintService,
                setupConfig,
                MosConstants.PowerPlatformApiResourceAppId,
                "Power Platform API (CopilotStudio)",
                new[] { MosConstants.PermissionNames.PowerPlatformCopilotStudioInvoke },
                logger,
                addToRequiredResourceAccess: false,
                setInheritablePermissions: true,
                setupResults,
                cancellationToken);

            // write changes to generated config
            await configService.SaveStateAsync(setupConfig);

            logger.LogInformation("");
            logger.LogInformation("CopilotStudio permissions configured successfully");
            logger.LogInformation("");
            if (!iSetupAll)
            {
                logger.LogInformation("Your agent blueprint can now invoke Copilot Studio copilots.");
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to configure CopilotStudio permissions: {Message}", ex.Message);
            if (iSetupAll)
            {
                throw;
            }
            return false;
        }
    }
}