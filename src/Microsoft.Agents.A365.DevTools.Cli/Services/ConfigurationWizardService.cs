// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for simplifying Agent 365 configuration initialization with smart defaults and Azure CLI integration
/// </summary>
public interface IConfigurationWizardService
{
    /// <summary>
    /// Runs an interactive configuration wizard that minimizes user input by leveraging Azure CLI and smart defaults
    /// </summary>
    /// <param name="existingConfig">Existing configuration to use for defaults, if any</param>
    /// <returns>Configured Agent365Config instance</returns>
    Task<Agent365Config?> RunWizardAsync(Agent365Config? existingConfig = null);
}

public class ConfigurationWizardService : IConfigurationWizardService
{
    private readonly IAzureCliService _azureCliService;
    private readonly PlatformDetector _platformDetector;
    private readonly ILogger<ConfigurationWizardService> _logger;

    public ConfigurationWizardService(
        IAzureCliService azureCliService,
        PlatformDetector platformDetector,
        ILogger<ConfigurationWizardService> logger)
    {
        _azureCliService = azureCliService;
        _platformDetector = platformDetector;
        _logger = logger;
    }

    public async Task<Agent365Config?> RunWizardAsync(Agent365Config? existingConfig = null)
    {
        try
        {
            if (existingConfig != null)
            {
                _logger.LogDebug("Using existing configuration with deploymentProjectPath: {Path}", existingConfig.DeploymentProjectPath ?? "(null)");
                Console.WriteLine("Found existing configuration. Default values will be used where available.");
                Console.WriteLine("Press Enter to keep a current value, or type a new one to update it.");
                Console.WriteLine();
            }

            // Step 1: Verify Azure CLI login
            if (!await VerifyAzureLoginAsync())
            {
                _logger.LogError("Configuration wizard cancelled: Azure CLI authentication required");
                return null;
            }

            // Step 2: Get Azure account info
            var accountInfo = await _azureCliService.GetCurrentAccountAsync();
            if (accountInfo == null)
            {
                _logger.LogError("Failed to retrieve Azure account information. Please run 'az login' first");
                return null;
            }

            Console.WriteLine($"Subscription ID: {accountInfo.Id} ({accountInfo.Name})");
            Console.WriteLine($"Tenant ID: {accountInfo.TenantId}");
            Console.WriteLine();
            Console.WriteLine("NOTE: Defaulted from current Azure account. To use a different Azure subscription, run 'az login' and then 'az account set --subscription <subscription-id>' before running this command.");
            Console.WriteLine();

            // Step 3: Get unique agent name
            var agentName = PromptForAgentName(existingConfig);
            if (string.IsNullOrWhiteSpace(agentName))
            {
                _logger.LogError("Agent name is required. Configuration cancelled");
                return null;
            }

            var derivedNames = GenerateDerivedNames(agentName);

            // Step 4: Validate deployment project path
            var deploymentPath = await PromptForDeploymentPathAsync(existingConfig);
            if (string.IsNullOrWhiteSpace(deploymentPath))
            {
                _logger.LogError("Configuration wizard cancelled: Deployment project path not provided or invalid");
                return null;
            }

            // Step 5: Select Resource Group
            var resourceGroup = await PromptForResourceGroupAsync(existingConfig);
            if (string.IsNullOrWhiteSpace(resourceGroup))
            {
                _logger.LogError("Configuration wizard cancelled: Resource group not selected");
                return null;
            }

            // Step 6: Select App Service Plan
            var appServicePlan = await PromptForAppServicePlanAsync(existingConfig, resourceGroup);
            if (string.IsNullOrWhiteSpace(appServicePlan))
            {
                _logger.LogError("Configuration wizard cancelled: App Service Plan not selected");
                return null;
            }

            // Step 7: Get manager email (required for agent creation)
            var managerEmail = PromptForManagerEmail(existingConfig);
            if (string.IsNullOrWhiteSpace(managerEmail))
            {
                _logger.LogError("Configuration wizard cancelled: Manager email not provided");
                return null;
            }

            // Step 8: Get location (with smart default from account or existing config)
            var location = await PromptForLocationAsync(existingConfig, accountInfo);

            // Step 9: Show configuration summary and allow override
            Console.WriteLine();
            Console.WriteLine("=================================================================");
            Console.WriteLine(" Configuration Summary");
            Console.WriteLine("=================================================================");
            Console.WriteLine($"Agent Name             : {agentName}");
            Console.WriteLine($"Web App Name           : {derivedNames.WebAppName}");
            Console.WriteLine($"Agent Identity Name    : {derivedNames.AgentIdentityDisplayName}");
            Console.WriteLine($"Agent Blueprint Name   : {derivedNames.AgentBlueprintDisplayName}");
            Console.WriteLine($"Agent UPN              : {derivedNames.AgentUserPrincipalName}");
            Console.WriteLine($"Agent Display Name     : {derivedNames.AgentUserDisplayName}");
            Console.WriteLine($"Manager Email          : {managerEmail}");
            Console.WriteLine($"Deployment Path        : {deploymentPath}");
            Console.WriteLine($"Resource Group         : {resourceGroup}");
            Console.WriteLine($"App Service Plan       : {appServicePlan}");
            Console.WriteLine($"Location               : {location}");
            Console.WriteLine($"Subscription           : {accountInfo.Name} ({accountInfo.Id})");
            Console.WriteLine($"Tenant                 : {accountInfo.TenantId}");
            Console.WriteLine();

            // Step 10: Allow customization of derived names
            var customizedNames = PromptForNameCustomization(derivedNames);

            // Step 11: Final confirmation to save configuration
            Console.Write("Save this configuration? (Y/n): ");
            var saveResponse = Console.ReadLine()?.Trim().ToLowerInvariant();
            
            if (saveResponse == "n" || saveResponse == "no")
            {
                Console.WriteLine("Configuration cancelled.");
                _logger.LogInformation("Configuration wizard cancelled by user");
                return null;
            }

            // Step 12: Build final configuration
            var config = new Agent365Config
            {
                TenantId = accountInfo.TenantId,
                SubscriptionId = accountInfo.Id,
                ResourceGroup = resourceGroup,
                Location = location,
                Environment = existingConfig?.Environment ?? "prod", // Default to prod, not asking for this
                AppServicePlanName = appServicePlan,
                AppServicePlanSku = existingConfig?.AppServicePlanSku ?? "B1", // Default to B1, not asking
                WebAppName = customizedNames.WebAppName,
                AgentIdentityDisplayName = customizedNames.AgentIdentityDisplayName,
                AgentBlueprintDisplayName = customizedNames.AgentBlueprintDisplayName,
                AgentUserPrincipalName = customizedNames.AgentUserPrincipalName,
                AgentUserDisplayName = customizedNames.AgentUserDisplayName,
                ManagerEmail = managerEmail,
                AgentUserUsageLocation = GetUsageLocationFromAccount(accountInfo),
                DeploymentProjectPath = deploymentPath,
                AgentDescription = $"{agentName} - Agent 365 Agent"
            };

            _logger.LogInformation("Configuration wizard completed successfully");
            return config;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Configuration wizard failed: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<bool> VerifyAzureLoginAsync()
    {
        if (!await _azureCliService.IsLoggedInAsync())
        {
            _logger.LogError("You are not logged in to Azure CLI. Please run 'az login' and select your subscription, then try again");
            return false;
        }

        return true;
    }

    private string PromptForAgentName(Agent365Config? existingConfig)
    {
        string defaultName;
        if (existingConfig != null)
        {
            defaultName = ExtractAgentNameFromConfig(existingConfig);
        }
        else
        {
            // Generate alphanumeric-only default
            var username = System.Text.RegularExpressions.Regex.Replace(Environment.UserName, @"[^a-zA-Z0-9]", "");
            defaultName = $"{username}agent{DateTime.Now:MMdd}";
        }

        return PromptWithDefault(
            "Agent name",
            defaultName,
            ValidateAgentName
        );
    }

    private string ExtractAgentNameFromConfig(Agent365Config config)
    {
        // Try to extract a reasonable agent name from existing config
        if (!string.IsNullOrEmpty(config.WebAppName))
        {
            // Remove common suffixes and clean up
            var name = config.WebAppName;
            name = System.Text.RegularExpressions.Regex.Replace(name, @"(webapp|app|web|agent|bot)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            name = System.Text.RegularExpressions.Regex.Replace(name, @"[-_]", ""); // Remove all hyphens and underscores
            name = System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9]", ""); // Remove any remaining non-alphanumeric
            if (!string.IsNullOrWhiteSpace(name) && name.Length > 2 && char.IsLetter(name[0]))
            {
                return name;
            }
        }

        return $"agent{DateTime.Now:MMdd}";
    }

    private async Task<string> PromptForDeploymentPathAsync(Agent365Config? existingConfig)
    {
        var defaultPath = existingConfig?.DeploymentProjectPath ?? Environment.CurrentDirectory;

        await Task.CompletedTask; // Satisfy async requirement
        var path = PromptWithDefault(
            "Deployment project path",
            defaultPath,
            ValidateDeploymentPath
        );

        // Additional validation using PlatformDetector
        if (!string.IsNullOrWhiteSpace(path))
        {
            var platform = _platformDetector.Detect(path);
            if (platform == ProjectPlatform.Unknown)
            {
                Console.WriteLine("WARNING: Could not detect a supported project type (.NET, Node.js, or Python) in the specified directory.");
                Console.Write("Continue anyway? (y/N): ");
                var response = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (response != "y" && response != "yes")
                {
                    _logger.LogError("Deployment path must contain a valid project. Configuration cancelled");
                    return string.Empty;
                }
            }
            else
            {
                Console.WriteLine($"Detected {platform} project");
            }
        }

        return path;
    }

    private async Task<string> PromptForResourceGroupAsync(Agent365Config? existingConfig)
    {
        Console.WriteLine();
        Console.WriteLine("Loading resource groups from Azure...");
        
        var resourceGroups = await _azureCliService.ListResourceGroupsAsync();
        if (!resourceGroups.Any())
        {
            Console.WriteLine("WARNING: No resource groups found. You may need to create one first.");
            return PromptWithDefault(
                "Resource group name",
                existingConfig?.ResourceGroup ?? $"{Environment.UserName}-agent365-rg",
                input => !string.IsNullOrWhiteSpace(input) ? (true, "") : (false, "Resource group name cannot be empty")
            );
        }

        Console.WriteLine();
        Console.WriteLine("Available Resource Groups:");
        for (int i = 0; i < resourceGroups.Count; i++)
        {
            Console.WriteLine($"{i + 1:D2}. {resourceGroups[i].Name} ({resourceGroups[i].Location})");
        }
        Console.WriteLine();

        var defaultIndex = existingConfig?.ResourceGroup != null ? 
            resourceGroups.FindIndex(rg => rg.Name.Equals(existingConfig.ResourceGroup, StringComparison.OrdinalIgnoreCase)) + 1 : 
            1;

        while (true)
        {
            Console.Write($"Select resource group [1-{resourceGroups.Count}] (default: {Math.Max(1, defaultIndex)}): ");
            var input = Console.ReadLine()?.Trim();
            
            if (string.IsNullOrWhiteSpace(input))
            {
                input = Math.Max(1, defaultIndex).ToString();
            }

            if (int.TryParse(input, out int index) && index >= 1 && index <= resourceGroups.Count)
            {
                return resourceGroups[index - 1].Name;
            }

            Console.WriteLine($"Please enter a number between 1 and {resourceGroups.Count}");
        }
    }

    private async Task<string> PromptForAppServicePlanAsync(Agent365Config? existingConfig, string resourceGroup)
    {
        Console.WriteLine();
        Console.WriteLine("Loading app service plans from Azure...");
        
        var allPlans = await _azureCliService.ListAppServicePlansAsync();
        var plansInRg = allPlans.Where(p => p.ResourceGroup.Equals(resourceGroup, StringComparison.OrdinalIgnoreCase)).ToList();
        
        Console.WriteLine();
        if (plansInRg.Any())
        {
            Console.WriteLine($"App Service Plans in {resourceGroup}:");
            for (int i = 0; i < plansInRg.Count; i++)
            {
                Console.WriteLine($"{i + 1:D2}. {plansInRg[i].Name} ({plansInRg[i].Sku}, {plansInRg[i].Location})");
            }
            Console.WriteLine($"{plansInRg.Count + 1:D2}. Create new app service plan");
            Console.WriteLine();

            var defaultIndex = existingConfig?.AppServicePlanName != null ? 
                plansInRg.FindIndex(p => p.Name.Equals(existingConfig.AppServicePlanName, StringComparison.OrdinalIgnoreCase)) + 1 : 
                plansInRg.Count + 1; // Default to creating new

            while (true)
            {
                Console.Write($"Select option [1-{plansInRg.Count + 1}] (default: {Math.Max(1, defaultIndex)}): ");
                var input = Console.ReadLine()?.Trim();
                
                if (string.IsNullOrWhiteSpace(input))
                {
                    input = Math.Max(1, defaultIndex).ToString();
                }

                if (int.TryParse(input, out int index))
                {
                    if (index >= 1 && index <= plansInRg.Count)
                    {
                        return plansInRg[index - 1].Name;
                    }
                    else if (index == plansInRg.Count + 1)
                    {
                        // Create new plan name
                        return $"{Environment.UserName}-agent365-plan";
                    }
                }

                Console.WriteLine($"Please enter a number between 1 and {plansInRg.Count + 1}");
            }
        }
        else
        {
            Console.WriteLine($"No existing app service plans found in {resourceGroup}. A new plan will be created.");
            return existingConfig?.AppServicePlanName ?? $"{Environment.UserName}-agent365-plan";
        }
    }

    private string PromptForManagerEmail(Agent365Config? existingConfig)
    {
        return PromptWithDefault(
            "Manager email",
            existingConfig?.ManagerEmail ?? "",
            ValidateEmail
        );
    }

    private async Task<string> PromptForLocationAsync(Agent365Config? existingConfig, AzureAccountInfo accountInfo)
    {
        // Try to get a smart default location
        var defaultLocation = existingConfig?.Location;
        
        if (string.IsNullOrEmpty(defaultLocation))
        {
            // Try to get from resource group or common defaults
            defaultLocation = "eastus"; // Conservative default
        }

        await Task.CompletedTask; // Satisfy async requirement
        return PromptWithDefault(
            "Azure location",
            defaultLocation,
            input => !string.IsNullOrWhiteSpace(input) ? (true, "") : (false, "Location cannot be empty")
        );
    }

    private ConfigDerivedNames GenerateDerivedNames(string agentName)
    {
        var cleanName = System.Text.RegularExpressions.Regex.Replace(agentName, @"[^a-zA-Z0-9]", "").ToLowerInvariant();
        var timestamp = DateTime.Now.ToString("MMddHHmm");

        return new ConfigDerivedNames
        {
            WebAppName = $"{cleanName}-webapp-{timestamp}",
            AgentIdentityDisplayName = $"{agentName} Identity",
            AgentBlueprintDisplayName = $"{agentName} Blueprint",
            AgentUserPrincipalName = $"agent.{cleanName}.{timestamp}@yourdomain.onmicrosoft.com",
            AgentUserDisplayName = $"{agentName} Agent User"
        };
    }

    private ConfigDerivedNames PromptForNameCustomization(ConfigDerivedNames defaultNames)
    {
        Console.Write("Would you like to customize the generated names? (y/N): ");
        var response = Console.ReadLine()?.Trim().ToLowerInvariant();
        
        if (response != "y" && response != "yes")
        {
            return defaultNames;
        }

        Console.WriteLine();
        Console.WriteLine("Customizing generated names (press Enter to keep default):");
        
        return new ConfigDerivedNames
        {
            WebAppName = PromptWithDefault("Web app name", defaultNames.WebAppName, ValidateWebAppName),
            AgentIdentityDisplayName = PromptWithDefault("Agent identity name", defaultNames.AgentIdentityDisplayName),
            AgentBlueprintDisplayName = PromptWithDefault("Agent blueprint name", defaultNames.AgentBlueprintDisplayName),
            AgentUserPrincipalName = PromptWithDefault("Agent UPN", defaultNames.AgentUserPrincipalName, ValidateEmail),
            AgentUserDisplayName = PromptWithDefault("Agent display name", defaultNames.AgentUserDisplayName)
        };
    }

    private string PromptWithDefault(
        string prompt, 
        string defaultValue = "", 
        Func<string, (bool isValid, string error)>? validator = null)
    {
        // Azure CLI style: "Prompt [default]: "
        while (true)
        {
            if (!string.IsNullOrEmpty(defaultValue))
            {
                Console.Write($"{prompt} [{defaultValue}]: ");
            }
            else
            {
                Console.Write($"{prompt}: ");
            }
            
            var input = Console.ReadLine()?.Trim() ?? "";
            
            if (string.IsNullOrWhiteSpace(input) && !string.IsNullOrEmpty(defaultValue))
            {
                input = defaultValue;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("ERROR: This field is required.");
                continue;
            }

            if (validator != null)
            {
                var (isValid, error) = validator(input);
                if (!isValid)
                {
                    Console.WriteLine($"ERROR: {error}");
                    continue;
                }
            }

            return input;
        }
    }

    private static (bool isValid, string error) ValidateAgentName(string input)
    {
        if (input.Length < 2 || input.Length > 50)
            return (false, "Agent name must be between 2-50 characters");
        
        if (!System.Text.RegularExpressions.Regex.IsMatch(input, @"^[a-zA-Z][a-zA-Z0-9]*$"))
            return (false, "Agent name must start with a letter and contain only letters and numbers (no special characters for cross-platform compatibility)");
        
        return (true, "");
    }

    private (bool isValid, string error) ValidateDeploymentPath(string input)
    {
        try
        {
            var fullPath = Path.GetFullPath(input);
            if (!Directory.Exists(fullPath))
                return (false, $"Directory does not exist: {fullPath}");
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"Invalid path: {ex.Message}");
        }
    }

    private static (bool isValid, string error) ValidateWebAppName(string input)
    {
        if (input.Length < 2 || input.Length > 60)
            return (false, "Must be between 2-60 characters");

        if (!System.Text.RegularExpressions.Regex.IsMatch(input, @"^[a-zA-Z0-9][a-zA-Z0-9\-]*[a-zA-Z0-9]$"))
            return (false, "Only alphanumeric characters and hyphens allowed. Cannot start or end with a hyphen.");

        if (input.Contains("_"))
            return (false, "Underscores are not allowed in Azure Web App names. Use hyphens (-) instead.");

        return (true, "");
    }

    private static (bool isValid, string error) ValidateEmail(string input)
    {
        if (!input.Contains("@") || !input.Contains("."))
            return (false, "Must be a valid email format");

        var parts = input.Split('@');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            return (false, "Invalid email format. Use: username@domain");

        return (true, "");
    }

    private string GetUsageLocationFromAccount(AzureAccountInfo accountInfo)
    {
        // Default to US for now - could be enhanced to detect from account location
        return "US";
    }
}