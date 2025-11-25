// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Infrastructure subcommand - Creates Azure infrastructure (Resource Group, App Service Plan, Web App, MSI)
/// Required Permissions: Azure Subscription Contributor/Owner
/// COMPLETE REPLICATION of A365SetupRunner Phase 0 and Phase 1 functionality
/// </summary>
public static class InfrastructureSubcommand
{
    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        IAzureValidator azureValidator,
        AzureWebAppCreator webAppCreator,
        PlatformDetector platformDetector,
        CommandExecutor executor)
    {
        var command = new Command("infrastructure", 
            "Create Azure infrastructure\n" +
            "Minimum required permissions: Azure Subscription Contributor or Owner\n");

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
            if (dryRun)
            {
                var dryRunConfig = await configService.LoadAsync(config.FullName);

                logger.LogInformation("DRY RUN: Create Azure Infrastructure");
                logger.LogInformation("Would create the following resources:");
                logger.LogInformation("  - Resource Group: {ResourceGroup}", dryRunConfig.ResourceGroup);
                logger.LogInformation("  - Location: {Location}", dryRunConfig.Location);
                logger.LogInformation("  - App Service Plan: {PlanName} (SKU: {Sku})",
                    dryRunConfig.AppServicePlanName, dryRunConfig.AppServicePlanSku);
                logger.LogInformation("  - Web App: {WebAppName}", dryRunConfig.WebAppName);
                logger.LogInformation("  - Managed Service Identity: Enabled");
                
                // Detect platform (even in dry-run for informational purposes)
                if (!string.IsNullOrWhiteSpace(dryRunConfig.DeploymentProjectPath))
                {
                    var detectedPlatform = platformDetector.Detect(dryRunConfig.DeploymentProjectPath);
                    var detectedRuntime = GetRuntimeForPlatform(detectedPlatform);
                    logger.LogInformation("  - Detected Platform: {Platform}", detectedPlatform);
                    logger.LogInformation("  - Runtime: {Runtime}", detectedRuntime);
                }
                
                return;
            }

            // Load configuration - ConfigService automatically finds generated config in same directory
            var setupConfig = await configService.LoadAsync(config.FullName);
            if (setupConfig.NeedDeployment)
            {
                // Validate Azure CLI authentication, subscription, and environment
                if (!await azureValidator.ValidateAllAsync(setupConfig.SubscriptionId))
                {
                    Environment.Exit(1);
                }
            }
            else
            {
                logger.LogInformation("NeedDeployment=false – skipping Azure subscription validation.");
            }

            var generatedConfigPath = Path.Combine(
                   config.DirectoryName ?? Environment.CurrentDirectory,
                   "a365.generated.config.json");

            await CreateInfrastructureImplementationAsync(
                logger,
                config.FullName,
                generatedConfigPath,
                executor,
                platformDetector,
                setupConfig.NeedDeployment,
                false,
                CancellationToken.None);

            logger.LogInformation("");
            logger.LogInformation("Next steps: Run 'a365 setup blueprint' to create the agent blueprint");

        }, configOption, verboseOption, dryRunOption);

        return command;
    }

    #region Public Static Methods (Reusable by A365SetupRunner)

    public static async Task<bool> CreateInfrastructureImplementationAsync(
        ILogger logger,
        string configPath,
        string generatedConfigPath,
        CommandExecutor commandExecutor,
        PlatformDetector platformDetector,
        bool needDeployment,
        bool skipInfrastructure,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            logger.LogError("Config file not found at {Path}", configPath);
            return false;
        }

        JsonObject cfg;
        try
        {
            cfg = JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken))!.AsObject();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse config JSON: {Path}", configPath);
            return false;
        }

        string Get(string name) => cfg.TryGetPropertyValue(name, out var node) && node is JsonValue jv && jv.TryGetValue(out string? s) ? s ?? string.Empty : string.Empty;

        var subscriptionId = Get("subscriptionId");
        var tenantId = Get("tenantId");
        var resourceGroup = Get("resourceGroup");
        var planName = Get("appServicePlanName");
        var webAppName = Get("webAppName");
        var location = Get("location");
        var planSku = Get("appServicePlanSku");
        if (string.IsNullOrWhiteSpace(planSku)) planSku = ConfigConstants.DefaultAppServicePlanSku;

        var deploymentProjectPath = Get("deploymentProjectPath");

        var skipInfra = skipInfrastructure || !needDeployment;
        var externalHosting = !needDeployment && !skipInfrastructure;

        if (!skipInfra)
        {
            // Azure hosting scenario – need full infra details
            if (new[] { subscriptionId, resourceGroup, planName, webAppName, location }.Any(string.IsNullOrWhiteSpace))
            {
                logger.LogError(
                    "Config missing required properties for Azure hosting. " +
                    "Need subscriptionId, resourceGroup, appServicePlanName, webAppName, location.");
                return false;
            }
        }
        else
        {
            // Non-Azure hosting or --blueprint: no infra required
            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                logger.LogWarning(
                    "subscriptionId is not set. This is acceptable for blueprint-only or External hosting mode " +
                    "as Azure infrastructure will not be provisioned.");
            }
        }

        // Detect project platform for appropriate runtime configuration
        var platform = Models.ProjectPlatform.DotNet; // Default fallback
        if (!string.IsNullOrWhiteSpace(deploymentProjectPath))
        {
            platform = platformDetector.Detect(deploymentProjectPath);
            logger.LogInformation("Detected project platform: {Platform}", platform);
        }
        else
        {
            logger.LogWarning("No deploymentProjectPath specified, defaulting to .NET runtime");
        }

        logger.LogInformation("Agent 365 Setup Infrastructure - Starting...");
        logger.LogInformation("Subscription: {Sub}", subscriptionId);
        logger.LogInformation("Resource Group: {RG}", resourceGroup);
        logger.LogInformation("App Service Plan: {Plan}", planName);
        logger.LogInformation("Web App: {App}", webAppName);
        logger.LogInformation("Location: {Loc}", location);
        logger.LogInformation("");

        if (!skipInfra)
        {
            bool isValidated = await ValidateAzureCliAuthenticationAsync(
            commandExecutor,
            tenantId,
            logger,
            cancellationToken);

            if (!isValidated)
            {
                return false;
            }
        }
        else
        {
            logger.LogInformation("==> Skipping Azure management authentication (--skipInfrastructure or External hosting)");
        }

        await CreateInfrastructureAsync(
            commandExecutor,
            subscriptionId,
            tenantId,
            resourceGroup,
            location,
            planName,
            planSku,
            webAppName,
            generatedConfigPath,
            platform,
            logger,
            needDeployment,
            skipInfra,
            externalHosting,
            cancellationToken);

        return true;
    }

    /// <summary>
    /// Phase 0: Validate Azure CLI authentication and acquire management scope token
    /// </summary>
    public static async Task<bool> ValidateAzureCliAuthenticationAsync(
        CommandExecutor executor,
        string tenantId,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("==> Verifying Azure CLI authentication");
        
        // Check if logged in
        var accountCheck = await executor.ExecuteAsync("az", "account show", captureOutput: true, suppressErrorLogging: true, cancellationToken: cancellationToken);
        if (!accountCheck.Success)
        {
            logger.LogInformation("Azure CLI not authenticated. Initiating login with management scope...");
            logger.LogInformation("A browser window will open for authentication.");
            
            var loginResult = await executor.ExecuteAsync("az", $"login --tenant {tenantId}", cancellationToken: cancellationToken);
            
            if (!loginResult.Success)
            {
                logger.LogError("Azure CLI login failed. Please run manually: az login --scope https://management.core.windows.net//.default");
                return false;
            }
            
            logger.LogInformation("Azure CLI login successful!");
            await Task.Delay(2000, cancellationToken);
        }
        else
        {
            logger.LogInformation("Azure CLI already authenticated");
        }
        
        // Verify we have the management scope
        logger.LogInformation("Verifying access to Azure management resources...");
        var tokenCheck = await executor.ExecuteAsync(
            "az", 
            "account get-access-token --resource https://management.core.windows.net/ --query accessToken -o tsv", 
            captureOutput: true, 
            suppressErrorLogging: true,
            cancellationToken: cancellationToken);
            
        if (!tokenCheck.Success)
        {
            logger.LogWarning("Unable to acquire management scope token. Attempting re-authentication...");
            logger.LogInformation("A browser window will open for authentication.");
            
            var loginResult = await executor.ExecuteAsync("az", $"login --tenant {tenantId}", cancellationToken: cancellationToken);
            
            if (!loginResult.Success)
            {
                logger.LogError("Azure CLI login with management scope failed. Please run manually: az login --scope https://management.core.windows.net//.default");
                return false;
            }
            
            logger.LogInformation("Azure CLI re-authentication successful!");
            await Task.Delay(2000, cancellationToken);
            
            var retryTokenCheck = await executor.ExecuteAsync(
                "az", 
                "account get-access-token --resource https://management.core.windows.net/ --query accessToken -o tsv", 
                captureOutput: true, 
                suppressErrorLogging: true,
                cancellationToken: cancellationToken);
                
            if (!retryTokenCheck.Success)
            {
                logger.LogWarning("Still unable to acquire management scope token after re-authentication.");
                logger.LogWarning("Continuing anyway - you may encounter permission errors later.");
            }
            else
            {
                logger.LogInformation("Management scope token acquired successfully!");
            }
        }
        else
        {
            logger.LogInformation("Management scope verified successfully");
        }
        
        logger.LogInformation("");
        return true;
    }

    /// <summary>
    /// Phase 1: Create Azure infrastructure (Resource Group, App Service Plan, Web App, Managed Identity)
    /// Equivalent to A365SetupRunner Phase 1 (lines 223-334)
    /// Returns the Managed Identity Principal ID (or null if not assigned)
    /// </summary>
    public static async Task CreateInfrastructureAsync(
        CommandExecutor executor,
        string subscriptionId,
        string tenantId,
        string resourceGroup,
        string location,
        string planName,
        string? planSku,
        string webAppName,
        string generatedConfigPath,
        Models.ProjectPlatform platform,
        ILogger logger,
        bool needDeployment,
        bool skipInfra,
        bool externalHosting,
        CancellationToken cancellationToken = default)
    {
        string? principalId = null;
        JsonObject generatedConfig = new JsonObject();

        if (skipInfra)
        {
            var modeMessage = "External hosting (non-Azure)";

            logger.LogInformation("==> Skipping Azure infrastructure ({Mode})", modeMessage);
            logger.LogInformation("Loading existing configuration...");

            // Load existing generated config if available
            if (File.Exists(generatedConfigPath))
            {
                try
                {
                    generatedConfig = JsonNode.Parse(await File.ReadAllTextAsync(generatedConfigPath, cancellationToken))?.AsObject() ?? new JsonObject();

                    if (generatedConfig.TryGetPropertyValue("managedIdentityPrincipalId", out var existingPrincipalId))
                    {
                        // Only reuse MSI in blueprint-only mode
                        principalId = existingPrincipalId?.GetValue<string>();
                        logger.LogInformation("Found existing Managed Identity Principal ID: {Id}", principalId ?? "(none)");
                    }
                    else if (externalHosting)
                    {
                        logger.LogInformation("External hosting selected - Managed Identity will NOT be used.");

                        // Make sure we don't create FIC later
                        principalId = null;
                    }

                    logger.LogInformation("Existing configuration loaded successfully");
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Could not load existing config: {Message}. Starting fresh.", ex.Message);
                }
            }
            else
            {
                logger.LogInformation("No existing configuration found - blueprint will be created without managed identity");
            }

            logger.LogInformation("");
        }
        else
        {
            logger.LogInformation("==> Deploying App Service + enabling Managed Identity");

            // Set subscription context
            try
            {
                await executor.ExecuteAsync("az", $"account set --subscription {subscriptionId}");
            }
            catch (Exception)
            {
                logger.LogWarning("Failed to set az subscription context explicitly");
            }

            // Resource group
            var rgExists = await executor.ExecuteAsync("az", $"group exists -n {resourceGroup} --subscription {subscriptionId}", captureOutput: true);
            if (rgExists.Success && rgExists.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Resource group already exists: {RG} (skipping creation)", resourceGroup);
            }
            else
            {
                logger.LogInformation("Creating resource group {RG}", resourceGroup);
                await AzWarnAsync(executor, logger, $"group create -n {resourceGroup} -l {location} --subscription {subscriptionId}", "Create resource group");
            }

            // App Service plan
            await EnsureAppServicePlanExistsAsync(executor, logger, resourceGroup, planName, planSku, subscriptionId);

            // Web App
            var webShow = await executor.ExecuteAsync("az", $"webapp show -g {resourceGroup} -n {webAppName} --subscription {subscriptionId}", captureOutput: true, suppressErrorLogging: true);
            if (!webShow.Success)
            {
                var runtime = GetRuntimeForPlatform(platform);
                logger.LogInformation("Creating web app {App} with runtime {Runtime}", webAppName, runtime);
                var createResult = await executor.ExecuteAsync("az", $"webapp create -g {resourceGroup} -p {planName} -n {webAppName} --runtime \"{runtime}\" --subscription {subscriptionId}", captureOutput: true, suppressErrorLogging: true);
                if (!createResult.Success)
                {
                    if (createResult.StandardError.Contains("AuthorizationFailed", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new AzureResourceException("WebApp", webAppName, createResult.StandardError, true);
                    }
                    else
                    {
                        logger.LogError("ERROR: Web app creation failed: {Err}", createResult.StandardError);
                        throw new InvalidOperationException($"Failed to create web app '{webAppName}'. Setup cannot continue.");
                    }
                }
            }
            else
            {
                var linuxFxVersion = GetLinuxFxVersionForPlatform(platform);
                logger.LogInformation("Web app already exists: {App} (skipping creation)", webAppName);
                logger.LogInformation("Configuring web app to use {Platform} runtime ({LinuxFxVersion})...", platform, linuxFxVersion);
                await AzWarnAsync(executor, logger, $"webapp config set -g {resourceGroup} -n {webAppName} --linux-fx-version \"{linuxFxVersion}\" --subscription {subscriptionId}", "Configure runtime");
            }

            // Verify web app
            var verifyResult = await executor.ExecuteAsync("az", $"webapp show -g {resourceGroup} -n {webAppName} --subscription {subscriptionId}", captureOutput: true, suppressErrorLogging: true);
            if (!verifyResult.Success)
            {
                logger.LogWarning("WARNING: Unable to verify web app via az webapp show.");
            }
            else
            {
                logger.LogInformation("Verified web app presence.");
            }

            // Managed Identity
            logger.LogInformation("Assigning (or confirming) system-assigned managed identity");
            var identity = await executor.ExecuteAsync("az", $"webapp identity assign -g {resourceGroup} -n {webAppName} --subscription {subscriptionId}");
            if (identity.Success)
            {
                try
                {
                    var json = JsonDocument.Parse(identity.StandardOutput);
                    principalId = json.RootElement.GetProperty("principalId").GetString();
                    if (!string.IsNullOrEmpty(principalId))
                    {
                        logger.LogInformation("Managed Identity principalId: {Id}", principalId);
                    }
                }
                catch
                {
                    // ignore parse error
                }
            }
            else if (identity.StandardError.Contains("already has a managed identity", StringComparison.OrdinalIgnoreCase) ||
                     identity.StandardError.Contains("Conflict", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Managed identity already assigned (ignoring conflict).");
            }
            else
            {
                logger.LogWarning("WARNING: identity assign returned error: {Err}", identity.StandardError.Trim());
            }

            // Load or create generated config
            if (File.Exists(generatedConfigPath))
            {
                try
                {
                    generatedConfig = JsonNode.Parse(await File.ReadAllTextAsync(generatedConfigPath, cancellationToken))?.AsObject() ?? new JsonObject();
                }
                catch
                {
                    logger.LogWarning("Could not parse existing generated config, starting fresh");
                }
            }

            if (!string.IsNullOrWhiteSpace(principalId))
            {
                generatedConfig["managedIdentityPrincipalId"] = principalId;
                await File.WriteAllTextAsync(generatedConfigPath, generatedConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
                logger.LogInformation("Generated config updated with MSI principalId: {Id}", principalId);
            }

            logger.LogInformation("Waiting 10 seconds to ensure Service Principal is fully propagated...");
            await Task.Delay(10000, cancellationToken);
        }
    }

    /// <summary>
    /// Save Managed Identity Principal ID to a365.generated.config.json
    /// Equivalent to A365SetupRunner logic (lines 321-332)
    /// </summary>
    public static async Task SaveManagedIdentityToConfigAsync(
        string principalId,
        string generatedConfigPath,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Load or create generated config
        JsonObject generatedConfig = new JsonObject();
        if (File.Exists(generatedConfigPath))
        {
            try
            {
                generatedConfig = JsonNode.Parse(await File.ReadAllTextAsync(generatedConfigPath, cancellationToken))?.AsObject() ?? new JsonObject();
            }
            catch
            {
                logger.LogWarning("Could not parse existing generated config, starting fresh");
            }
        }

        generatedConfig["managedIdentityPrincipalId"] = principalId;
        await File.WriteAllTextAsync(generatedConfigPath, 
            generatedConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), 
            cancellationToken);
        
        logger.LogInformation("Generated config updated with MSI principalId: {Id}", principalId);
    }

    #endregion

    #region Private Helper Methods

    private static async Task AzWarnAsync(CommandExecutor executor, ILogger logger,  string args, string description)
    {
        var result = await executor.ExecuteAsync("az", args, suppressErrorLogging: true);
        if (!result.Success)
        {
            if (result.StandardError.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("{Description} already exists (skipping creation)", description);
            }
            else if (result.StandardError.Contains("AuthorizationFailed", StringComparison.OrdinalIgnoreCase))
            {
                var exception = new AzureResourceException(description, string.Empty, result.StandardError, true);
                ExceptionHandler.HandleAgent365Exception(exception);
            }
            else
            {
                logger.LogWarning("az {Description} returned non-success (exit code {Code}). Error: {Err}",
                    description, result.ExitCode, Short(result.StandardError));
            }
        }
    }

    /// <summary>
    /// Ensures that an App Service Plan exists, creating it if necessary and verifying its existence.
    /// </summary>
    internal static async Task EnsureAppServicePlanExistsAsync(CommandExecutor executor, ILogger logger, string resourceGroup, string planName, string? planSku, string subscriptionId)
    {
        var planShow = await executor.ExecuteAsync("az", $"appservice plan show -g {resourceGroup} -n {planName} --subscription {subscriptionId}", captureOutput: true, suppressErrorLogging: true);
        if (planShow.Success)
        {
            logger.LogInformation("App Service plan already exists: {Plan} (skipping creation)", planName);
        }
        else
        {
            logger.LogInformation("Creating App Service plan {Plan}", planName);
            await AzWarnAsync(executor, logger, $"appservice plan create -g {resourceGroup} -n {planName} --sku {planSku} --is-linux --subscription {subscriptionId}", "Create App Service plan");

            // Verify the plan was created successfully
            var verifyPlan = await executor.ExecuteAsync("az", $"appservice plan show -g {resourceGroup} -n {planName} --subscription {subscriptionId}", captureOutput: true, suppressErrorLogging: true);
            if (!verifyPlan.Success)
            {
                logger.LogError("ERROR: App Service plan creation failed. The plan '{Plan}' does not exist.", planName);
                throw new InvalidOperationException($"Failed to create App Service plan '{planName}'. This may be due to quota limits or insufficient permissions. Setup cannot continue.");
            }
            logger.LogInformation("App Service plan created successfully: {Plan}", planName);
        }
    }

    /// <summary>
    /// Get the Azure Web App runtime string based on the detected platform
    /// (from A365SetupRunner GetRuntimeForPlatform method)
    /// </summary>
    private static string GetRuntimeForPlatform(Models.ProjectPlatform platform)
    {
        return platform switch
        {
            Models.ProjectPlatform.Python => "PYTHON:3.11",
            Models.ProjectPlatform.NodeJs => "NODE:18-lts", 
            Models.ProjectPlatform.DotNet => "DOTNETCORE:8.0",
            _ => "DOTNETCORE:8.0" // Default fallback
        };
    }

    /// <summary>
    /// Get the Azure Web App Linux FX Version string based on the detected platform
    /// (from A365SetupRunner GetLinuxFxVersionForPlatform method)
    /// </summary>
    private static string GetLinuxFxVersionForPlatform(Models.ProjectPlatform platform)
    {
        return platform switch
        {
            Models.ProjectPlatform.Python => "PYTHON|3.11",
            Models.ProjectPlatform.NodeJs => "NODE|18-lts",
            Models.ProjectPlatform.DotNet => "DOTNETCORE|8.0",
            _ => "DOTNETCORE|8.0" // Default fallback
        };
    }

    private static string Short(string? text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : (text.Length <= 180 ? text.Trim() : text[..177] + "...");

    #endregion
}
