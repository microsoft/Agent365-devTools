// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Globalization;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

public static class ConfigCommand
{
    public static Command CreateCommand(ILogger logger, string? configDir = null)
    {
        var directory = configDir ?? Services.ConfigService.GetGlobalConfigDirectory();
        var command = new Command("config", "Configure Azure subscription, resource settings, and deployment options\nfor a365 CLI commands");
        command.AddCommand(CreateInitSubcommand(logger, directory));
        command.AddCommand(CreateDisplaySubcommand(logger, directory));
        return command;
    }

    private static Command CreateInitSubcommand(ILogger logger, string configDir)
    {
        var cmd = new Command("init", "Initialize configuration settings for Azure resources, agent identity,\nand deployment options used by subsequent Agent 365 commands")
        {
            new Option<string?>(new[] { "-c", "--configfile" }, "Path to a config file to import"),
            new Option<bool>(new[] { "--global", "-g" }, "Create config in global directory (AppData) instead of current directory")
        };

        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFileOption = cmd.Options.OfType<Option<string?>>().First(opt => opt.HasAlias("-c"));
            var globalOption = cmd.Options.OfType<Option<bool>>().First(opt => opt.HasAlias("--global"));
            
            string? configFile = context.ParseResult.GetValueForOption(configFileOption);
            bool useGlobal = context.ParseResult.GetValueForOption(globalOption);
            
            // Create local config by default, unless --global flag is used
            string configPath = useGlobal 
                ? Path.Combine(configDir, "a365.config.json")
                : Path.Combine(Environment.CurrentDirectory, "a365.config.json");
            
            if (!useGlobal)
            {
                logger.LogInformation("Initializing local configuration...");
            }
            else
            {
                Directory.CreateDirectory(configDir);
                logger.LogInformation("Initializing global configuration...");
            }

            var configModelType = typeof(Models.Agent365Config);
            Models.Agent365Config config;

            if (!string.IsNullOrEmpty(configFile))
            {
                if (!File.Exists(configFile))
                {
                    logger.LogError($"Config file '{configFile}' not found.");
                    return;
                }
                var json = await File.ReadAllTextAsync(configFile);
                try
                {
                    config = JsonSerializer.Deserialize<Models.Agent365Config>(json) ?? new Models.Agent365Config();
                }
                catch (Exception ex)
                {
                    logger.LogError($"Failed to parse config file: {ex.Message}");
                    return;
                }
            }
            else
            {
                // Check for existing configuration to use as defaults
                Models.Agent365Config? existingConfig = null;
                var localConfigPath = Path.Combine(Environment.CurrentDirectory, "a365.config.json");
                var globalConfigPath = Path.Combine(configDir, "a365.config.json");
                bool hasExistingConfig = false;
                
                // Try to load existing config (local first, then global)
                if (File.Exists(localConfigPath))
                {
                    try
                    {
                        var existingJson = await File.ReadAllTextAsync(localConfigPath);
                        existingConfig = JsonSerializer.Deserialize<Models.Agent365Config>(existingJson);
                        hasExistingConfig = true;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"Could not parse existing local config: {ex.Message}");
                    }
                }
                else if (File.Exists(globalConfigPath))
                {
                    try
                    {
                        var existingJson = await File.ReadAllTextAsync(globalConfigPath);
                        existingConfig = JsonSerializer.Deserialize<Models.Agent365Config>(existingJson);
                        hasExistingConfig = true;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"Could not parse existing global config: {ex.Message}");
                    }
                }

                string PromptWithHelp(string prompt, string help, string? defaultValue = null, Func<string, (bool isValid, string error)>? validator = null)
                {
                    // Validate default value and fix if needed
                    if (defaultValue != null && validator != null)
                    {
                        var (isValidDefault, _) = validator(defaultValue);
                        if (!isValidDefault)
                        {
                            defaultValue = null; // Clear invalid default, force user to enter valid value
                        }
                    }
                    
                    // Section divider
                    Console.WriteLine("----------------------------------------------");
                    Console.WriteLine($" {prompt}");
                    Console.WriteLine("----------------------------------------------");
                    
                    // Multi-line description
                    Console.WriteLine($"Description : {help}");
                    Console.WriteLine();
                    
                    // Current value display
                    if (defaultValue != null)
                    {
                        Console.WriteLine($"Current Value: [{defaultValue}]");
                    }
                    Console.WriteLine();
                    
                    string input;
                    do
                    {
                        Console.Write("> ");
                        input = Console.ReadLine()?.Trim() ?? "";
                        
                        if (string.IsNullOrWhiteSpace(input) && defaultValue != null)
                        {
                            input = defaultValue;
                        }
                        
                        if (string.IsNullOrWhiteSpace(input))
                        {
                            Console.WriteLine("This field is required. Please provide a value.");
                            Console.Write("> ");
                            continue;
                        }
                        
                        if (validator != null)
                        {
                            var (isValid, error) = validator(input);
                            if (!isValid)
                            {
                                Console.WriteLine(error);
                                Console.Write("> ");
                                continue;
                            }
                        }
                        
                        break;
                    } while (true);
                    
                    return input;
                }

                // Generate sensible defaults based on user environment or existing config
                var userName = Environment.UserName.ToLowerInvariant();
                var timestamp = DateTime.Now.ToString("MMdd");
                
                Console.WriteLine();
                Console.WriteLine("----------------------------------------------");
                Console.WriteLine(" Agent 365 CLI - Configuration Setup");
                Console.WriteLine("----------------------------------------------");
                Console.WriteLine();
                
                if (hasExistingConfig)
                {
                    Console.WriteLine("A configuration file already exists in this directory.");
                    Console.WriteLine("Press **Enter** to keep a current value, or type a new one to update it.");
                }
                else
                {
                    Console.WriteLine("Setting up your Agent 365 CLI configuration.");
                    Console.WriteLine("Please provide the required configuration details below.");
                }
                Console.WriteLine();

                config = new Models.Agent365Config
                {
                    TenantId = PromptWithHelp(
                        "Azure Tenant ID",
                        "Your Azure Active Directory tenant identifier (GUID format).\n              You can find this in the Azure Portal under:\n              Azure Active Directory > Overview > Tenant ID",
                        existingConfig?.TenantId,
                        input => Guid.TryParse(input, out _) ? (true, "") : (false, "Must be a valid GUID format (e.g., 12345678-1234-1234-1234-123456789abc)")
                    ),
                    
                    SubscriptionId = PromptWithHelp(
                        "Azure Subscription ID", 
                        "The Azure subscription where resources will be created.\n              You can find this in the Azure Portal under:\n              Subscriptions > [Your Subscription] > Overview > Subscription ID",
                        existingConfig?.SubscriptionId,
                        input => Guid.TryParse(input, out _) ? (true, "") : (false, "Must be a valid GUID format")
                    ),
                    
                    ResourceGroup = PromptWithHelp(
                        "Resource Group Name",
                        "Azure resource group name for organizing related resources.\n              Must be 1-90 characters, alphanumeric, periods, underscores, hyphens and parenthesis.",
                        existingConfig?.ResourceGroup ?? $"{userName}-agent365-rg"
                    ),
                    
                    Location = PromptWithHelp(
                        "Azure Location",
                        "Azure region where resources will be deployed.\n              Common options: eastus, westus2, centralus, westeurope, eastasia\n              You can find all regions in the Azure Portal under:\n              Create a resource > [Any service] > Basics > Region dropdown",
                        existingConfig?.Location ?? "eastus",
                        input => !string.IsNullOrWhiteSpace(input) ? (true, "") : (false, "Location cannot be empty")
                    ),
                    
                    AppServicePlanName = PromptWithHelp(
                        "App Service Plan Name",
                        "Name for the Azure App Service Plan that will host your agent web app.\n              This defines the compute resources (CPU, memory) for your application.\n              A new plan will be created if it doesn't exist.",
                        existingConfig?.AppServicePlanName ?? $"{userName}-agent365-plan"
                    ),
                    
                    WebAppName = PromptWithHelp(
                        "Web App Name",
                        "Globally unique name for your Azure Web App.\n              This will be part of your agent's URL: https://<name>.azurewebsites.net\n              Must be unique across all Azure Web Apps worldwide.\n              Only alphanumeric characters and hyphens allowed (no underscores).\n              Cannot start or end with a hyphen. Maximum 60 characters.",
                        existingConfig?.WebAppName ?? $"{userName}-agent365-{timestamp}",
                        input => {
                            // Azure Web App naming rules:
                            // - 2-60 characters
                            // - Only alphanumeric and hyphens (NO underscores)
                            // - Cannot start or end with hyphen
                            // - Must be globally unique
                            
                            if (input.Length < 2 || input.Length > 60) 
                                return (false, "Must be between 2-60 characters");
                            
                            if (!System.Text.RegularExpressions.Regex.IsMatch(input, @"^[a-zA-Z0-9][a-zA-Z0-9-]*[a-zA-Z0-9]$")) 
                                return (false, "Only alphanumeric characters and hyphens allowed (no underscores). Cannot start or end with a hyphen.");
                            
                            if (input.Contains("_"))
                                return (false, "Underscores are not allowed in Azure Web App names. Use hyphens (-) instead.");
                            
                            return (true, "");
                        }
                    ),
                    
                    AgentIdentityDisplayName = PromptWithHelp(
                        "Agent Identity Display Name",
                        "Human-readable name for your agent identity.\n              This will appear in Azure Active Directory and admin interfaces.\n              Use a descriptive name to easily identify this agent.",
                        existingConfig?.AgentIdentityDisplayName ?? $"{CultureInfo.CurrentCulture.TextInfo.ToTitleCase(userName)}'s Agent 365 Instance {timestamp}"
                    ),
                    
                    AgentUserPrincipalName = PromptWithHelp(
                        "Agent User Principal Name (UPN)",
                        "Email-like identifier for the agentic user in Azure AD.\n              Format: <username>@<domain>.onmicrosoft.com or @<verified-domain>\n              Example: demo.agent@contoso.onmicrosoft.com\n              This must be unique in your tenant.",
                        existingConfig?.AgentUserPrincipalName ?? $"agent.{userName}@yourdomain.onmicrosoft.com",
                        input => {
                            // Basic email format validation
                            if (!input.Contains("@") || !input.Contains("."))
                                return (false, "Must be a valid email-like format (e.g., user@domain.onmicrosoft.com)");
                            
                            var parts = input.Split('@');
                            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                                return (false, "Invalid UPN format. Use: username@domain");
                            
                            return (true, "");
                        }
                    ),
                    
                    AgentUserDisplayName = PromptWithHelp(
                        "Agent User Display Name",
                        "Human-readable name for the agentic user.\n              This will appear in Teams, Outlook, and other Microsoft 365 apps.\n              Example: 'Demo Agent' or 'Support Bot'",
                        existingConfig?.AgentUserDisplayName ?? $"{CultureInfo.CurrentCulture.TextInfo.ToTitleCase(userName)}'s Agent User"
                    ),
                    
                    DeploymentProjectPath = PromptWithHelp(
                        "Deployment Project Path",
                        "Path to your agent project directory for deployment.\n              This should contain your agent's source code and configuration files.\n              The directory must exist and be accessible.\n              You can use relative paths (e.g., ./my-agent) or absolute paths.",
                        existingConfig?.DeploymentProjectPath ?? Environment.CurrentDirectory,
                        input => {
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
                    )
                    // AgentIdentityScopes and AgentApplicationScopes are read-only properties that return hardcoded defaults
                };
                
                Console.WriteLine();
                Console.WriteLine("Configuration setup completed successfully!");
            }

            // Validate config
            var errors = config.Validate();
            if (errors.Count > 0)
            {
                logger.LogError("Configuration is invalid:");
                Console.WriteLine("Configuration is invalid:");
                foreach (var err in errors)
                {
                    logger.LogError("  " + err);
                    Console.WriteLine("  " + err);
                }
                logger.LogError("Aborted. Please fix the above errors and try again.");
                Console.WriteLine("Aborted. Please fix the above errors and try again.");
                return;
            }

            // Re-validate before writing as a defensive check
            var finalErrors = config.Validate();
            if (finalErrors.Count > 0)
            {
                logger.LogError("Configuration validation failed before writing. Aborting write.");
                return;
            }

            if (File.Exists(configPath))
            {
                Console.Write($"Config file already exists at {configPath}. Overwrite? (y/N): ");
                var answer = Console.ReadLine();
                if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Aborted by user. Config not overwritten.");
                    return;
                }
            }

            // Serialize only static properties (init-only) to a365.config.json
            var staticConfig = new
            {
                tenantId = config.TenantId,
                subscriptionId = config.SubscriptionId,
                resourceGroup = config.ResourceGroup,
                location = config.Location,
                appServicePlanName = config.AppServicePlanName,
                appServicePlanSku = config.AppServicePlanSku,
                webAppName = config.WebAppName,
                agentIdentityDisplayName = config.AgentIdentityDisplayName,
                agentBlueprintDisplayName = config.AgentBlueprintDisplayName,
                agentUserPrincipalName = config.AgentUserPrincipalName,
                agentUserDisplayName = config.AgentUserDisplayName,
                managerEmail = config.ManagerEmail,
                agentUserUsageLocation = config.AgentUserUsageLocation,
                // agentIdentityScopes and agentApplicationScopes are hardcoded - not persisted to config file
                deploymentProjectPath = config.DeploymentProjectPath,
                agentDescription = config.AgentDescription,
                // enableTeamsChannel, enableEmailChannel, enableGraphApiRegistration are hardcoded - not persisted to config file
                mcpDefaultServers = config.McpDefaultServers
            };

            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var configJson = JsonSerializer.Serialize(staticConfig, options);
            await File.WriteAllTextAsync(configPath, configJson);
            logger.LogInformation($"Config written to {configPath}");

            // If imported from file, display the config
            if (!string.IsNullOrEmpty(configFile))
            {
                var displayCmd = CreateDisplaySubcommand(logger, configDir);
                await displayCmd.InvokeAsync("");
            }
        });

        return cmd;
    }

    private static Command CreateDisplaySubcommand(ILogger logger, string configDir)
    {
        var cmd = new Command("display", "Display current configuration settings including Azure subscription,\nresource names, and deployment parameters");

        var generatedOption = new Option<bool>(
            new[] { "--generated", "-g" },
            description: "Display generated configuration (a365.generated.config.json)");

        var allOption = new Option<bool>(
            new[] { "--all", "-a" },
            description: "Display both static and generated configuration");

        cmd.AddOption(generatedOption);
        cmd.AddOption(allOption);

        cmd.SetHandler(async (bool showGenerated, bool showAll) =>
        {
            try
            {
                // Use ConfigService to load config (triggers sync to %LocalAppData%)
                var configService = new Services.ConfigService(logger as Microsoft.Extensions.Logging.ILogger<Services.ConfigService>);
                var config = await configService.LoadAsync();

                // JSON serialization options for display
                var displayOptions = new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                // Determine what to show based on options
                bool displayStatic = !showGenerated || showAll;
                bool displayGenerated = showGenerated || showAll;

                if (displayStatic)
                {
                    if (showAll)
                    {
                        Console.WriteLine("=== Static Configuration (a365.config.json) ===");
                        var configPath = Services.ConfigService.GetConfigFilePath();
                        if (configPath != null)
                        {
                            Console.WriteLine($"Location: {configPath}");
                        }
                    }
                    
                    // Use the model's method to get only static configuration fields
                    var staticConfig = config.GetStaticConfig();
                    var displayJson = JsonSerializer.Serialize(staticConfig, displayOptions);
                    
                    // Post-process: Replace escaped backslashes with single backslashes for better readability
                    displayJson = System.Text.RegularExpressions.Regex.Replace(displayJson, @"\\\\", @"\");
                    
                    Console.WriteLine(displayJson);
                    
                    if (showAll && displayGenerated)
                    {
                        Console.WriteLine();
                    }
                }

                if (displayGenerated)
                {
                    if (showAll)
                    {
                        Console.WriteLine("=== Generated Configuration (a365.generated.config.json) ===");
                        var generatedPath = Services.ConfigService.GetGeneratedConfigFilePath();
                        if (generatedPath != null)
                        {
                            Console.WriteLine($"Location: {generatedPath}");
                        }
                    }
                    
                    // Use the model's method to get only generated configuration fields
                    var generatedConfig = config.GetGeneratedConfig();
                    var displayJson = JsonSerializer.Serialize(generatedConfig, displayOptions);
                    
                    // Post-process: Replace escaped backslashes with single backslashes for better readability
                    displayJson = System.Text.RegularExpressions.Regex.Replace(displayJson, @"\\\\", @"\");
                    
                    Console.WriteLine(displayJson);
                }
            }
            catch (FileNotFoundException ex)
            {
                logger.LogError("Configuration file not found: {Message}", ex.Message);
                logger.LogError("Run 'a365 config init' to create a configuration.");
            }
            catch (JsonException ex)
            {
                logger.LogError("Failed to parse configuration: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to display configuration: {Message}", ex.Message);
            }
        }, generatedOption, allOption);

        return cmd;
    }
}
