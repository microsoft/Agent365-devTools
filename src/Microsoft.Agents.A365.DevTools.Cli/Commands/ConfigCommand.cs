// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Globalization;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

public static class ConfigCommand
{
    private const int ConsentsTableWidth = 120;
    public static Command CreateCommand(ILogger logger, string? configDir = null, IConfigurationWizardService? wizardService = null)
    {
        var directory = configDir ?? Services.ConfigService.GetGlobalConfigDirectory();
        var command = new Command("config", "Configure Azure subscription, resource settings, and deployment options\nfor a365 CLI commands");
        
        // Always add init command - it supports both wizard and direct import (-c option)
        command.AddCommand(CreateInitSubcommand(logger, directory, wizardService));
        command.AddCommand(CreateDisplaySubcommand(logger, directory));
        
        return command;
    }

    private static Command CreateInitSubcommand(ILogger logger, string configDir, IConfigurationWizardService? wizardService)
    {
        var cmd = new Command("init", "Interactive wizard to configure Agent 365 with Azure CLI integration and smart defaults")
        {
            new Option<string?>(new[] { "-c", "--configfile" }, "Path to an existing config file to import"),
            new Option<bool>(new[] { "--global", "-g" }, "Create config in global directory (AppData) instead of current directory")
        };

        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFileOption = cmd.Options.OfType<Option<string?>>().First(opt => opt.HasAlias("-c"));
            var globalOption = cmd.Options.OfType<Option<bool>>().First(opt => opt.HasAlias("--global"));
            
            string? configFile = context.ParseResult.GetValueForOption(configFileOption);
            bool useGlobal = context.ParseResult.GetValueForOption(globalOption);
            
            // Determine config path
            string configPath = useGlobal 
                ? Path.Combine(configDir, "a365.config.json")
                : Path.Combine(Environment.CurrentDirectory, "a365.config.json");
            
            if (useGlobal)
            {
                Directory.CreateDirectory(configDir);
            }

            // If config file is specified, import it directly
            if (!string.IsNullOrEmpty(configFile))
            {
                if (!File.Exists(configFile))
                {
                    logger.LogError($"Config file '{configFile}' not found.");
                    return;
                }
                
                try
                {
                    var json = await File.ReadAllTextAsync(configFile);
                    var importedConfig = JsonSerializer.Deserialize<Agent365Config>(json);
                    
                    if (importedConfig == null)
                    {
                        logger.LogError("Failed to parse config file.");
                        return;
                    }

                    // Validate imported config
                    var errors = importedConfig.Validate();
                    if (errors.Count > 0)
                    {
                        logger.LogError("Imported configuration is invalid:");
                        foreach (var err in errors)
                        {
                            logger.LogError($"  {err}");
                        }
                        return;
                    }

                    // CRITICAL: Only serialize static properties when saving to a365.config.json
                    // This prevents dynamic properties (e.g., agentBlueprintId, managedIdentityPrincipalId) 
                    // from being written to the static config file
                    var staticConfig = importedConfig.GetStaticConfig();
                    var outputJson = JsonSerializer.Serialize(staticConfig, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(configPath, outputJson);
                    
                    // Also save to global if saving locally
                    if (!useGlobal)
                    {
                        var globalConfigPath = Path.Combine(configDir, "a365.config.json");
                        Directory.CreateDirectory(configDir);
                        await File.WriteAllTextAsync(globalConfigPath, outputJson);
                    }
                    
                    logger.LogInformation($"\nConfiguration imported to: {configPath}");
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError($"Failed to import config file: {ex.Message}");
                    return;
                }
            }

            // Load existing config if it exists
            Agent365Config? existingConfig = null;
            if (File.Exists(configPath))
            {
                try
                {
                    var existingJson = await File.ReadAllTextAsync(configPath);
                    existingConfig = JsonSerializer.Deserialize<Agent365Config>(existingJson);
                    logger.LogDebug($"Loaded existing configuration from: {configPath}");
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Could not load existing config from {configPath}: {ex.Message}");
                }
            }

            // If no config file specified, run wizard
            if (wizardService == null)
            {
                logger.LogError("Wizard service not available. Use -c option to import a config file, or run from full CLI.");
                context.ExitCode = 1;
                return;
            }

            try
            {
                // Run the wizard with existing config
                var config = await wizardService.RunWizardAsync(existingConfig);
                
                if (config != null)
                {
                    // CRITICAL: Only serialize static properties (init-only) to a365.config.json
                    // Dynamic properties (get/set) should only be in a365.generated.config.json
                    var staticConfig = config.GetStaticConfig();
                    var json = JsonSerializer.Serialize(staticConfig, new JsonSerializerOptions { WriteIndented = true });
                    
                    // Save to primary location (local or global based on flag)
                    await File.WriteAllTextAsync(configPath, json);
                    
                    // Also save to global config directory for reuse
                    if (!useGlobal)
                    {
                        var globalConfigPath = Path.Combine(configDir, "a365.config.json");
                        Directory.CreateDirectory(configDir);
                        await File.WriteAllTextAsync(globalConfigPath, json);
                    }
                    
                    logger.LogInformation($"\nConfiguration saved to: {configPath}");
                    logger.LogInformation("\nYou can now run:");
                    logger.LogInformation("  a365 setup      - Create Azure resources");
                    logger.LogInformation("  a365 deploy     - Deploy your agent");
                }
                else
                {
                    // Wizard returned null - could be user cancellation or error
                    // Error details already logged by the wizard service
                    logger.LogDebug("Configuration wizard returned null");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to complete configuration: {Message}", ex.Message);
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
                    
                    // Post-process: Replace escaped backslashes
                    displayJson = System.Text.RegularExpressions.Regex.Replace(displayJson, @"\\\\", @"\");
                    
                    Console.WriteLine(displayJson);

                    // Display resource consents table only when showing full config (not with -g flag)
                    // When -g is used, resourceConsents are already in the JSON output
                    if (!showGenerated && config.ResourceConsents != null && config.ResourceConsents.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Resource Consents:");
                        Console.WriteLine(new string('-', ConsentsTableWidth));
                        Console.WriteLine($"{"Resource Name",-30} {"App ID",-40} {"Consented",-12} {"Timestamp",-25}");
                        Console.WriteLine(new string('-', ConsentsTableWidth));
                        
                        foreach (var consent in config.ResourceConsents.OrderBy(c => c.ResourceName))
                        {
                            var timestamp = consent.ConsentTimestamp?.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "N/A";
                            var consented = consent.ConsentGranted ? "Yes" : "No";
                            Console.WriteLine($"{consent.ResourceName,-30} {consent.ResourceAppId,-40} {consented,-12} {timestamp,-25}");
                            
                            if (consent.Scopes != null && consent.Scopes.Count > 0)
                            {
                                Console.WriteLine($"  Scopes: {string.Join(", ", consent.Scopes)}");
                            }
                        }
                        Console.WriteLine(new string('-', ConsentsTableWidth));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to display configuration: {Message}", ex.Message);
            }
        }, generatedOption, allOption);

        return cmd;
    }
}
