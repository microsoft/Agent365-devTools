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
    // SDK validation retry configuration
    private const int MaxSdkValidationAttempts = 3;
    private const int InitialRetryDelayMs = 500;
    private const int MaxRetryDelayMs = 5000; // Cap exponential backoff at 5 seconds

    /// <summary>
    /// Requirement checks for setup infrastructure: Azure auth, Frontier Preview, PowerShell modules, and infrastructure config.
    /// </summary>
    internal static List<IRequirementCheck> GetChecks(AzureAuthValidator auth)
    {
        var checks = SetupCommand.GetBaseChecks(auth);
        checks.Add(new InfrastructureRequirementCheck());
        return checks;
    }

    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        AzureAuthValidator authValidator,
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

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var config = context.ParseResult.GetValueForOption(configOption)!;
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var ct = context.GetCancellationToken();

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
                    var detectedRuntime = await GetLinuxFxVersionForPlatformAsync(detectedPlatform, dryRunConfig.DeploymentProjectPath, executor, logger);
                    logger.LogInformation("  - Detected Platform: {Platform}", detectedPlatform);
                    logger.LogInformation("  - Runtime: {Runtime}", detectedRuntime);
                }
                
                return;
            }

            // Load configuration - ConfigService automatically finds generated config in same directory
            var setupConfig = await configService.LoadAsync(config.FullName);
            if (setupConfig.NeedDeployment)
            {
                await RequirementsSubcommand.RunChecksOrExitAsync(
                    GetChecks(authValidator), setupConfig, logger, ct);
            }
            else
            {
                logger.LogDebug("NeedDeployment=false - skipping Azure subscription validation.");
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
                ct);

            logger.LogInformation("");
            logger.LogInformation("Next steps: Run 'a365 setup blueprint' to create the agent blueprint");

        });

        return command;
    }

    #region Public Static Methods (Reusable by A365SetupRunner)

    public static async Task<(bool success, bool anyAlreadyExisted)> CreateInfrastructureImplementationAsync(
        ILogger logger,
        string configPath,
        string generatedConfigPath,
        CommandExecutor commandExecutor,
        PlatformDetector platformDetector,
        bool needDeployment,
        bool skipInfrastructure,
        CancellationToken cancellationToken,
        ArmApiService? armApiService = null,
        GraphApiService? graphApiService = null)
    {
        if (!File.Exists(configPath))
        {
            logger.LogError("Config file not found at {Path}", configPath);
            return (false, false);
        }

        JsonObject cfg;
        try
        {
            cfg = JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken))!.AsObject();
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to parse config JSON: {Path} — {Message}", configPath, ex.Message);
            logger.LogDebug(ex, "Config JSON parse exception details");
            return (false, false);
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
            // Azure hosting scenario - need full infra details
            if (new[] { subscriptionId, resourceGroup, planName, webAppName, location }.Any(string.IsNullOrWhiteSpace))
            {
                logger.LogError(
                    "Config missing required properties for Azure hosting. " +
                    "Need subscriptionId, resourceGroup, appServicePlanName, webAppName, location.");
                return (false, false);
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
        logger.LogInformation("");

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
                return (false, false);
            }
        }
        else
        {
            logger.LogInformation("==> Skipping Azure management authentication (--skipInfrastructure or External hosting)");
            logger.LogInformation("");
        }

        var (principalId, anyAlreadyExisted) = await CreateInfrastructureAsync(
            commandExecutor,
            subscriptionId,
            tenantId,
            resourceGroup,
            location,
            planName,
            planSku,
            webAppName,
            generatedConfigPath,
            deploymentProjectPath,
            platform,
            logger,
            needDeployment,
            skipInfra,
            externalHosting,
            cancellationToken,
            armApiService,
            graphApiService);

        return (true, anyAlreadyExisted);
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
        logger.LogInformation("");

        // Use cached login hint from AzCliHelper (populated by requirements check).
        // Falls back to spawning 'az account show' only on first call in this process.
        var loginHint = await AzCliHelper.ResolveLoginHintAsync();
        if (loginHint == null)
        {
            logger.LogInformation("Azure CLI not authenticated. Initiating login with management scope...");
            logger.LogInformation("A browser window will open for authentication. Please check your taskbar or browser if you don't see it.");

            var loginResult = await executor.ExecuteAsync("az", $"login --tenant {tenantId}", cancellationToken: cancellationToken);

            if (!loginResult.Success)
            {
                logger.LogError("Azure CLI login failed. Please run manually: az login --scope https://management.core.windows.net//.default");
                return false;
            }

            logger.LogInformation("Azure CLI login successful!");
            AzCliHelper.InvalidateLoginHintCache();
            await Task.Delay(2000, cancellationToken);
        }
        else
        {
            logger.LogDebug("Azure CLI already authenticated as {LoginHint}", loginHint);
        }
        
        // Verify we have the management scope (token is cached at process level by AzCliHelper).
        logger.LogDebug("Verifying access to Azure management resources...");
        var managementToken = await AzCliHelper.AcquireAzCliTokenAsync(ArmApiService.ArmResource, tenantId);

        if (string.IsNullOrWhiteSpace(managementToken))
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
            AzCliHelper.InvalidateAzCliTokenCache();
            await Task.Delay(2000, cancellationToken);

            var retryToken = await AzCliHelper.AcquireAzCliTokenAsync(ArmApiService.ArmResource, tenantId);
            if (string.IsNullOrWhiteSpace(retryToken))
            {
                logger.LogWarning("Still unable to acquire management scope token after re-authentication.");
                logger.LogWarning("Continuing anyway - you may encounter permission errors later.");
            }
            else
            {
                logger.LogDebug("Management scope token acquired successfully!");
            }
        }
        else
        {
            logger.LogDebug("Management scope verified successfully");
        }
        return true;
    }

    /// <summary>
    /// Phase 1: Create Azure infrastructure (Resource Group, App Service Plan, Web App, Managed Identity)
    /// Equivalent to A365SetupRunner Phase 1 (lines 223-334)
    /// Returns the Managed Identity Principal ID (or null if not assigned)
    /// and whether any infrastructure already existed (for idempotent summary reporting)
    /// </summary>
    public static async Task<(string? principalId, bool anyAlreadyExisted)> CreateInfrastructureAsync(
        CommandExecutor executor,
        string subscriptionId,
        string tenantId,
        string resourceGroup,
        string location,
        string planName,
        string? planSku,
        string webAppName,
        string generatedConfigPath,
        string deploymentProjectPath,
        Models.ProjectPlatform platform,
        ILogger logger,
        bool needDeployment,
        bool skipInfra,
        bool externalHosting,
        CancellationToken cancellationToken = default,
        ArmApiService? armApiService = null,
        GraphApiService? graphApiService = null)
    {
        bool anyAlreadyExisted = false;
        string? principalId = null;
        JsonObject generatedConfig = new JsonObject();

        if (skipInfra)
        {
            var modeMessage = "External hosting (non-Azure)";

            logger.LogInformation("==> Skipping Azure infrastructure ({Mode})", modeMessage);
            logger.LogInformation("");
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
            return (principalId, false); // Skip infra means nothing was created/modified
        }
        else
        {
            logger.LogInformation("==> Deploying App Service + enabling Managed Identity");
            logger.LogInformation("");

            // Resource group
            // Use ArmApiService for a direct HTTP check (~0.5s) instead of az subprocess (~15-20s).
            // Falls back to az CLI if ARM token is unavailable.
            bool rgExistsResult;
            var rgExistsArm = armApiService != null
                ? await armApiService.ResourceGroupExistsAsync(subscriptionId, resourceGroup, tenantId, cancellationToken)
                : null;
            if (rgExistsArm.HasValue)
            {
                rgExistsResult = rgExistsArm.Value;
            }
            else
            {
                var rgExists = await executor.ExecuteAsync("az", $"group exists -n {resourceGroup} --subscription {subscriptionId}", captureOutput: true);
                rgExistsResult = rgExists.Success && rgExists.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            if (rgExistsResult)
            {
                logger.LogInformation("Resource group already exists: {RG} (skipping creation)", resourceGroup);
                anyAlreadyExisted = true;
            }
            else
            {
                logger.LogInformation("Creating resource group {RG}", resourceGroup);
                await AzWarnAsync(executor, logger, $"group create -n {resourceGroup} -l {location} --subscription {subscriptionId}", "Create resource group");
            }

            // App Service plan
            bool planAlreadyExisted = await EnsureAppServicePlanExistsAsync(executor, logger, resourceGroup, planName, planSku, location, subscriptionId, cancellationToken: cancellationToken, armApiService: armApiService, tenantId: tenantId);
            if (planAlreadyExisted)
            {
                anyAlreadyExisted = true;
            }

            // Web App
            // Use ArmApiService for a direct HTTP check (~0.5s) instead of az subprocess (~15-20s).
            bool webAppExists;
            var webAppExistsArm = armApiService != null
                ? await armApiService.WebAppExistsAsync(subscriptionId, resourceGroup, webAppName, tenantId, cancellationToken)
                : null;
            if (webAppExistsArm.HasValue)
            {
                webAppExists = webAppExistsArm.Value;
            }
            else
            {
                var webShow = await executor.ExecuteAsync("az", $"webapp show -g {resourceGroup} -n {webAppName} --subscription {subscriptionId}", captureOutput: true, suppressErrorLogging: true);
                webAppExists = webShow.Success;
            }

            if (!webAppExists)
            {
                var runtime = await GetLinuxFxVersionForPlatformAsync(platform, deploymentProjectPath, executor, logger, cancellationToken);
                logger.LogInformation("Creating web app {App} with runtime {Runtime}", webAppName, runtime);
                var createResult = await executor.ExecuteAsync("az", $"webapp create -g {resourceGroup} -p {planName} -n {webAppName} --runtime \"{runtime}\" --subscription {subscriptionId}", captureOutput: true, suppressErrorLogging: true);
                if (!createResult.Success)
                {
                    // Check for specific error conditions
                    if (createResult.StandardError.Contains("AuthorizationFailed", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new AzureResourceException("WebApp", webAppName, createResult.StandardError, true);
                    }
                    else if (createResult.StandardError.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                             createResult.StandardError.Contains("app names must be globally unique", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new AzureResourceException(
                            ErrorCodes.AzureWebAppNameTaken,
                            "WebApp",
                            webAppName,
                            $"Web app name '{webAppName}' is already taken (web app names must be globally unique across all Azure).",
                            new List<string>
                            {
                                "Web app names must be globally unique across all Azure subscriptions",
                                "Update the 'webAppName' in your a365.config.json to a different value",
                                "Consider adding a unique suffix like your organization name or random characters"
                            });
                    }
                    else
                    {
                        logger.LogError("Web app creation failed: {Err}", createResult.StandardError);
                        throw new AzureResourceException("WebApp", webAppName, createResult.StandardError);
                    }
                }

                // Use RetryHelper to verify the web app was created with exponential backoff
                var retryHelper = new RetryHelper(logger);
                logger.LogInformation("Verifying web app creation...");
                var webAppCreated = await retryHelper.ExecuteWithRetryAsync(
                    async ct =>
                    {
                        var verifyResult = await executor.ExecuteAsync("az", $"webapp show -g {resourceGroup} -n {webAppName} --subscription {subscriptionId}", captureOutput: true, suppressErrorLogging: true);
                        return verifyResult.Success;
                    },
                    result => !result,
                    maxRetries: 8,
                    baseDelaySeconds: 5,
                    cancellationToken);

                if (!webAppCreated)
                {
                    logger.LogError("ERROR: Web app creation verification failed. The web app '{App}' cannot be found after retries.", webAppName);
                    throw new AzureResourceException("WebApp", webAppName, "Web app creation succeeded but verification failed after retries. The resource may still be propagating.");
                }

                logger.LogInformation("Web app created and verified successfully: {App}", webAppName);
            }
            else
            {
                anyAlreadyExisted = true;
                var linuxFxVersion = await GetLinuxFxVersionForPlatformAsync(platform, deploymentProjectPath, executor, logger, cancellationToken);
                logger.LogInformation("Web app already exists: {App} (skipping creation)", webAppName);
                logger.LogInformation("Configuring web app to use {Platform} runtime ({LinuxFxVersion})...", platform, linuxFxVersion);
                await AzWarnAsync(executor, logger, $"webapp config set -g {resourceGroup} -n {webAppName} --linux-fx-version \"{linuxFxVersion}\" --subscription {subscriptionId}", "Configure runtime");
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

                        // Use RetryHelper to verify MSI propagation to Azure AD with exponential backoff.
                        // Graph SP lookup (~200ms) replaces 'az ad sp show' (~30s) per retry attempt.
                        var retryHelper = new RetryHelper(logger);
                        logger.LogInformation("Verifying managed identity propagation in Azure AD...");
                        var msiPropagated = await retryHelper.ExecuteWithRetryAsync(
                            async ct =>
                            {
                                if (graphApiService != null)
                                    return await graphApiService.ServicePrincipalExistsAsync(tenantId, principalId, ct);
                                var verifyMsi = await executor.ExecuteAsync("az", $"ad sp show --id {principalId}", captureOutput: true, suppressErrorLogging: true);
                                return verifyMsi.Success;
                            },
                            result => !result,
                            maxRetries: 10,
                            baseDelaySeconds: 5,
                            cancellationToken);

                        if (msiPropagated)
                        {
                            logger.LogInformation("Managed identity service principal verified in Azure AD");
                        }
                        else
                        {
                            logger.LogWarning("Managed identity service principal not yet visible in Azure AD after retries. This may cause issues in blueprint creation.");
                        }
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

            // Assign current user as Website Contributor for the WebApp
            // This enables access to diagnostic logs and log stream
            logger.LogInformation("Assigning current user as Website Contributor for the web app...");
            try
            {
                // Get the current signed-in user's object ID.
                // Graph /v1.0/me (~200ms) replaces 'az ad signed-in-user show' (~30s).
                string? userObjectId = null;
                if (graphApiService != null)
                    userObjectId = await graphApiService.GetCurrentUserObjectIdAsync(tenantId, cancellationToken);
                if (string.IsNullOrWhiteSpace(userObjectId))
                {
                    var userResult = await executor.ExecuteAsync("az", "ad signed-in-user show --query id -o tsv", captureOutput: true, suppressErrorLogging: true);
                    if (userResult.Success && !string.IsNullOrWhiteSpace(userResult.StandardOutput))
                        userObjectId = userResult.StandardOutput.Trim();
                }

                if (!string.IsNullOrWhiteSpace(userObjectId))
                {

                    // Validate that userObjectId is a valid GUID to prevent command injection
                    if (!Guid.TryParse(userObjectId, out _))
                    {
                        logger.LogWarning("Retrieved user object ID is not a valid GUID: {UserId}", userObjectId);
                        return (principalId, anyAlreadyExisted);
                    }

                    logger.LogDebug("Current user object ID: {UserId}", userObjectId);

                    var webAppScope = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Web/sites/{webAppName}";

                    // Before attempting assignment, check whether the user already has sufficient
                    // access via inheritance (Owner or Contributor at subscription/RG level both
                    // supersede Website Contributor and include log access).
                    // ARM role assignments API (~300ms) replaces 'az role assignment list --include-inherited' (~35s).
                    string? existingRole = null;
                    if (armApiService != null)
                        existingRole = await armApiService.GetSufficientWebAppRoleAsync(subscriptionId, resourceGroup, webAppName, userObjectId, tenantId, cancellationToken);

                    if (existingRole == null)
                    {
                        // ARM call failed — fall back to az CLI
                        var existingRoleResult = await executor.ExecuteAsync("az",
                            $"role assignment list --assignee {userObjectId} --scope {webAppScope} --include-inherited" +
                            " --query \"[?roleDefinitionName=='Owner' || roleDefinitionName=='Contributor' || roleDefinitionName=='Website Contributor'].roleDefinitionName | [0]\"" +
                            " -o tsv",
                            captureOutput: true,
                            suppressErrorLogging: true);
                        existingRole = existingRoleResult.Success ? existingRoleResult.StandardOutput.Trim() : string.Empty;
                    }

                    if (!string.IsNullOrWhiteSpace(existingRole))
                    {
                        logger.LogInformation("User already has '{Role}' access on the web app — log access confirmed, skipping Website Contributor assignment",
                            existingRole);
                    }
                    else
                    {
                        // Attempt assignment. If it fails (e.g. no roleAssignments/write permission),
                        // log a single warning with remediation guidance — no further verification needed.
                        var roleAssignResult = await executor.ExecuteAsync("az",
                            $"role assignment create --role \"Website Contributor\" --assignee-object-id {userObjectId} --scope {webAppScope} --assignee-principal-type User",
                            captureOutput: true,
                            suppressErrorLogging: true);

                        if (roleAssignResult.Success)
                        {
                            logger.LogInformation("Successfully assigned Website Contributor role to current user");
                        }
                        else
                        {
                            logger.LogWarning("Could not assign Website Contributor role to user. Diagnostic logs may not be accessible.");
                            logger.LogWarning("You may need to manually assign the role via Azure Portal:");
                            logger.LogWarning("  1. Go to Azure Portal -> Your Web App -> Access control (IAM)");
                            logger.LogWarning("  2. Add role assignment -> Website Contributor");
                            logger.LogDebug("Role assignment error detail: {Error}", roleAssignResult.StandardError.Trim());
                        }
                    }
                }
                else
                {
                    logger.LogDebug("Could not retrieve current user object ID. User may be using a service principal or not logged in with az login.");
                }
            }
            catch (Exception ex)
            {
                // Don't fail the entire setup if role assignment fails
                logger.LogWarning(ex, "Failed to assign Website Contributor role to user. Diagnostic logs may not be accessible.");
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
        }
        
        return (principalId, anyAlreadyExisted);
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
                var logFilePath = ConfigService.GetCommandLogPath(CommandNames.Setup);
                ExceptionHandler.HandleAgent365Exception(exception, logFilePath: logFilePath);
            }
            else
            {
                logger.LogWarning("az {Description} returned non-success (exit code {Code}). Error: {Err}",
                    description, result.ExitCode, Short(result.StandardError));
            }
        }
    }

    /// <summary>
    /// Ensures the App Service plan exists or creates it if missing.
    /// Returns true if plan already existed, false if newly created.
    /// </summary>
    internal static async Task<bool> EnsureAppServicePlanExistsAsync(
        CommandExecutor executor,
        ILogger logger,
        string resourceGroup,
        string planName,
        string? planSku,
        string location,
        string subscriptionId,
        int maxRetries = 5,
        int baseDelaySeconds = 3,
        CancellationToken cancellationToken = default,
        ArmApiService? armApiService = null,
        string tenantId = "")
    {
        // Use ArmApiService for a direct HTTP check (~0.5s) instead of az subprocess (~15-20s).
        bool planExists;
        var planExistsArm = armApiService != null
            ? await armApiService.AppServicePlanExistsAsync(subscriptionId, resourceGroup, planName, tenantId, cancellationToken)
            : null;
        if (planExistsArm.HasValue)
        {
            planExists = planExistsArm.Value;
        }
        else
        {
            var planShow = await executor.ExecuteAsync("az", $"appservice plan show -g {resourceGroup} -n {planName} --subscription {subscriptionId}", captureOutput: true, suppressErrorLogging: true);
            planExists = planShow.Success;
        }

        if (planExists)
        {
            logger.LogInformation("App Service plan already exists: {Plan} (skipping creation)", planName);
            return true; // Already existed
        }
        else
        {
            logger.LogInformation("Creating App Service plan {Plan} in location {Location}", planName, location);
            
            // Execute creation command directly and check result immediately
            var createResult = await executor.ExecuteAsync(
                "az", 
                $"appservice plan create -g {resourceGroup} -n {planName} --sku {planSku} --location {location} --is-linux --subscription {subscriptionId}", 
                captureOutput: true, 
                suppressErrorLogging: true);

            if (!createResult.Success)
            {
                // Log detailed error information for diagnosis
                logger.LogError("ERROR: App Service plan creation failed for '{Plan}'", planName);
                logger.LogError("Exit code: {Code}", createResult.ExitCode);
                
                if (!string.IsNullOrWhiteSpace(createResult.StandardError))
                {
                    // Strip non-actionable Python / az-CLI diagnostic lines (UserWarning,
                    // Readonly attribute warnings) so they don't surface as ERRORs for the user.
                    var cleanedError = string.Join(
                        Environment.NewLine,
                        createResult.StandardError
                            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                            .Where(l => !IsNonActionableStderrLine(l)));
                    if (!string.IsNullOrWhiteSpace(cleanedError))
                        logger.LogError("Error output: {Error}", cleanedError);
                }
                
                if (!string.IsNullOrWhiteSpace(createResult.StandardOutput))
                {
                    logger.LogError("Standard output: {Output}", createResult.StandardOutput);
                }

                // Check for specific error conditions and throw appropriate exception
                if ((createResult.StandardError?.Contains("AuthorizationFailed", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (createResult.StandardError?.Contains("authorization", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    throw new AzureAppServicePlanException(
                        planName,
                        location,
                        planSku ?? "Unknown",
                        AppServicePlanErrorType.AuthorizationFailed,
                        createResult.StandardError);
                }
                else if ((createResult.StandardError?.Contains("QuotaExceeded", StringComparison.OrdinalIgnoreCase) ?? false) ||
                         (createResult.StandardError?.Contains("quota", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    throw new AzureAppServicePlanException(
                        planName,
                        location,
                        planSku ?? "Unknown",
                        AppServicePlanErrorType.QuotaExceeded,
                        createResult.StandardError);
                }
                else if ((createResult.StandardError?.Contains("InvalidSku", StringComparison.OrdinalIgnoreCase) ?? false) ||
                         (createResult.StandardError?.Contains("SkuNotAvailable", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    throw new AzureAppServicePlanException(
                        planName,
                        location,
                        planSku ?? "Unknown",
                        AppServicePlanErrorType.SkuNotAvailable,
                        createResult.StandardError);
                }
                else
                {
                    throw new AzureAppServicePlanException(
                        planName,
                        location,
                        planSku ?? "Unknown",
                        AppServicePlanErrorType.Other,
                        $"Azure CLI command failed with exit code {createResult.ExitCode}. Error: {Short(createResult.StandardError)}");
                }
            }

            logger.LogInformation("App Service plan creation command completed successfully");

            // Add small delay to allow Azure resource propagation
            if (baseDelaySeconds > 0)
            {
                logger.LogInformation("Waiting for Azure resource propagation...");
                await Task.Delay(TimeSpan.FromSeconds(baseDelaySeconds), cancellationToken);
            }

            // Use RetryHelper to verify the plan was created successfully with exponential backoff.
            // baseDelaySeconds controls both the propagation wait above and the inter-retry interval
            // here — tests pass 0 to eliminate all waits; production uses the default of 3.
            var retryHelper = new RetryHelper(logger);
            logger.LogInformation("Verifying App Service plan creation...");
            var planCreated = await retryHelper.ExecuteWithRetryAsync(
                async ct =>
                {
                    var verifyPlan = await executor.ExecuteAsync("az", $"appservice plan show -g {resourceGroup} -n {planName} --subscription {subscriptionId}", captureOutput: true, suppressErrorLogging: true);
                    return verifyPlan.Success;
                },
                result => !result,
                maxRetries,
                baseDelaySeconds,
                cancellationToken);

            if (!planCreated)
            {
                logger.LogError("ERROR: App Service plan creation verification failed after {Retries} retries. The plan '{Plan}' does not exist.", maxRetries, planName);
                logger.LogError("The creation command succeeded, but the plan cannot be found. This may indicate an Azure propagation delay or regional issue.");
                logger.LogError("Please check the Azure Portal to verify if the plan exists. If it does, you may need to wait a few minutes and retry.");
                throw new AzureAppServicePlanException(
                    planName,
                    location,
                    planSku ?? "Unknown",
                    AppServicePlanErrorType.VerificationTimeout,
                    $"Verification failed after {maxRetries} attempts. The plan may still be propagating in Azure.");
            }
            logger.LogInformation("App Service plan created and verified successfully: {Plan}", planName);
            return false; // Newly created
        }
    }

    /// <summary>
    /// Get the Azure Web App Linux FX Version string based on the detected platform
    /// (from A365SetupRunner GetLinuxFxVersionForPlatform method)
    /// </summary>
    public static async Task<string> GetLinuxFxVersionForPlatformAsync(
        Models.ProjectPlatform platform, 
        string? deploymentProjectPath, 
        CommandExecutor executor, 
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var dotnetVersion = await ResolveDotNetRuntimeVersionAsync(platform, deploymentProjectPath, executor, logger, cancellationToken);
        if (!string.IsNullOrWhiteSpace(dotnetVersion))
        {
            return $"DOTNETCORE|{dotnetVersion}";
        }

        return platform switch
        {
            Models.ProjectPlatform.Python => "PYTHON|3.11",
            Models.ProjectPlatform.NodeJs => "NODE|20-lts",
            Models.ProjectPlatform.DotNet => "DOTNETCORE|8.0",
            _ => "DOTNETCORE|8.0" // Default fallback
        };
    }

    private static async Task<string?> ResolveDotNetRuntimeVersionAsync(
        Models.ProjectPlatform platform,
        string? deploymentProjectPath,
        CommandExecutor executor,
        ILogger logger,
        CancellationToken cancellationToken = default,
        int? retryDelayMsOverride = null)
    {
        if (platform != Models.ProjectPlatform.DotNet ||
            string.IsNullOrWhiteSpace(deploymentProjectPath))
        {
            return null;
        }

        var csproj = Directory
            .GetFiles(deploymentProjectPath, "*.csproj", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (csproj == null)
        {
            logger.LogWarning("No .csproj file found in deploymentProjectPath: {Path}", deploymentProjectPath);
            return null;
        }

        var version = DotNetProjectHelper.DetectTargetRuntimeVersion(csproj, logger);
        if (string.IsNullOrWhiteSpace(version))
        {
            logger.LogWarning("Unable to detect TargetFramework version from {Project}", csproj);
            return null;
        }

        // Validate local SDK with retry logic (exponential backoff) to handle intermittent process spawn failures
        string? installedVersion = null;
        
        try
        {
            for (int attempt = 1; attempt <= MaxSdkValidationAttempts; attempt++)
            {
                var sdkResult = await executor.ExecuteAsync("dotnet", "--version", captureOutput: true, cancellationToken: cancellationToken);
                
                if (sdkResult.Success && !string.IsNullOrWhiteSpace(sdkResult.StandardOutput))
                {
                    installedVersion = sdkResult.StandardOutput.Trim();
                    break; // Success!
                }
                
                if (attempt < MaxSdkValidationAttempts)
                {
                    // Exponential backoff with cap: 500ms, 1000ms, 2000ms (capped at MaxRetryDelayMs)
                    var delayMs = retryDelayMsOverride
                        ?? Math.Min(InitialRetryDelayMs * (1 << (attempt - 1)), MaxRetryDelayMs);
                    logger.LogWarning(
                        "dotnet --version check failed (attempt {Attempt}/{MaxAttempts}). Retrying in {DelayMs}ms...",
                        attempt, MaxSdkValidationAttempts, delayMs);
                    await Task.Delay(delayMs, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation(".NET SDK validation cancelled by user");
            throw; // Re-throw to propagate cancellation
        }

        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            throw new DotNetSdkVersionMismatchException(
                requiredVersion: version,
                installedVersion: installedVersion,
                projectFilePath: csproj);
        }

        // Parse installed SDK version (e.g., "9.0.308" -> major: 9)
        // Validate format: must have at least major.minor (e.g., "9.0")
        var installedParts = installedVersion.Split('.');
        if (installedParts.Length < 2 ||
            !int.TryParse(installedParts[0], out var installedMajor))
        {
            logger.LogWarning("Unable to parse installed SDK version: {Version}. Expected format: major.minor.patch (e.g., 9.0.308)", installedVersion);
            // Continue anyway - dotnet build will fail if truly incompatible
            return version;
        }

        // Parse target framework version (e.g., "8.0" -> major: 8)
        // Validate format: must have at least major.minor (e.g., "8.0")
        var targetParts = version.Split('.');
        if (targetParts.Length < 2 ||
            !int.TryParse(targetParts[0], out var targetMajor))
        {
            logger.LogWarning("Unable to parse target framework version: {Version}. Expected format: major.minor (e.g., net8.0)", version);
            return version;
        }

        // Check if installed SDK can build the target framework
        // .NET SDK supports building projects targeting the same or lower major version
        // E.g., .NET 9 SDK can build .NET 8, 7, 6 projects (forward compatibility)
        // Minor versions are not relevant for SDK compatibility
        if (installedMajor < targetMajor)
        {
            // Installed SDK is older than target framework - this is a real problem
            throw new DotNetSdkVersionMismatchException(
                requiredVersion: version,
                installedVersion: installedVersion,
                projectFilePath: csproj);
        }

        // Installed SDK is same or newer - this is fine!
        if (installedMajor > targetMajor)
        {
            logger.LogInformation(
                ".NET {InstalledVersion} SDK detected (project targets .NET {TargetVersion}) - forward compatibility enabled",
                installedVersion,
                version);
        }

        return version; // e.g. "8.0", "9.0"
    }

    private static string Short(string? text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : (text.Length <= 180 ? text.Trim() : text[..177] + "...");

    /// <summary>
    /// Returns true for non-actionable stderr lines from the Python interpreter bundled
    /// inside the Azure CLI (UserWarning, Readonly attribute warnings). These appear on
    /// stderr even during successful invocations and must not surface as user-facing ERRORs.
    /// </summary>
    private static bool IsNonActionableStderrLine(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.StartsWith("UserWarning:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (trimmed.StartsWith("WARNING: Readonly attribute name will be ignored", StringComparison.OrdinalIgnoreCase))
            return true;
        // Python file/line references that accompany UserWarning (e.g. "  warnings.warn(...)")
        if (trimmed.StartsWith("warnings.warn(", StringComparison.Ordinal))
            return true;
        return false;
    }

    #endregion
}
