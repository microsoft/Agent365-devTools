// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Globalization;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

public static class ConfigCommand
{
    private const int ConsentsTableWidth = 120;
    public static Command CreateCommand(ILogger logger, string? configDir = null, IConfigurationWizardService? wizardService = null, IClientAppValidator? clientAppValidator = null)
    {
        var directory = configDir ?? Services.ConfigService.GetGlobalConfigDirectory();
        var command = new Command("config", "Configure Azure subscription, resource settings, and deployment options\nfor a365 CLI commands");

        // Always add init command - it supports both wizard and direct import (-c option)
        command.AddCommand(CreateInitSubcommand(logger, directory, wizardService, clientAppValidator));
        command.AddCommand(CreateDisplaySubcommand(logger, directory));

        return command;
    }

    private static Command CreateInitSubcommand(ILogger logger, string configDir, IConfigurationWizardService? wizardService, IClientAppValidator? clientAppValidator)
    {
        var cmd = new Command("init", "Interactive wizard to configure Agent 365 with Azure CLI integration and smart defaults")
        {
            new Option<string?>(new[] { "-c", "--configfile" }, "Path to an existing config file to import"),
            new Option<bool>(new[] { "--global", "-g" }, "Create config in global directory (AppData) instead of current directory"),
            new Option<bool>("--custom-blueprint-permissions", "Configure custom resource permissions for the agent blueprint"),
            new Option<string?>("--resourceAppId", "Resource application ID (GUID) for custom blueprint permission"),
            new Option<string?>("--scopes", "Comma-separated list of scopes for the custom blueprint permission"),
            new Option<bool>("--reset", "Clear all custom blueprint permissions (use with --custom-blueprint-permissions)"),
            new Option<bool>("--force", "Skip confirmation prompts when updating existing permissions")
        };

        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFileOption = cmd.Options.OfType<Option<string?>>().First(opt => opt.HasAlias("-c"));
            var globalOption = cmd.Options.OfType<Option<bool>>().First(opt => opt.HasAlias("--global"));
            var customPermissionsOption = cmd.Options.OfType<Option<bool>>().First(opt => opt.Name == "custom-blueprint-permissions");
            var resourceAppIdOption = cmd.Options.OfType<Option<string?>>().First(opt => opt.Name == "resourceAppId");
            var scopesOption = cmd.Options.OfType<Option<string?>>().First(opt => opt.Name == "scopes");
            var resetOption = cmd.Options.OfType<Option<bool>>().First(opt => opt.Name == "reset");
            var forceOption = cmd.Options.OfType<Option<bool>>().First(opt => opt.Name == "force");

            string? configFile = context.ParseResult.GetValueForOption(configFileOption);
            bool useGlobal = context.ParseResult.GetValueForOption(globalOption);
            bool customPermissions = context.ParseResult.GetValueForOption(customPermissionsOption);
            string? resourceAppId = context.ParseResult.GetValueForOption(resourceAppIdOption);
            string? scopes = context.ParseResult.GetValueForOption(scopesOption);
            bool reset = context.ParseResult.GetValueForOption(resetOption);
            bool force = context.ParseResult.GetValueForOption(forceOption);

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

                    // Validate client app if clientAppValidator is provided and clientAppId exists
                    if (clientAppValidator != null && !string.IsNullOrWhiteSpace(importedConfig.ClientAppId))
                    {
                        try
                        {
                            await clientAppValidator.EnsureValidClientAppAsync(
                                importedConfig.ClientAppId,
                                importedConfig.TenantId,
                                context.GetCancellationToken());
                        }
                        catch (ClientAppValidationException ex)
                        {
                            logger.LogError("");
                            logger.LogError(ErrorMessages.ClientAppValidationFailed);
                            logger.LogError($"  {ex.IssueDescription}");
                            foreach (var detail in ex.ErrorDetails)
                            {
                                logger.LogError($"  {detail}");
                            }
                            if (ex.MitigationSteps.Count > 0)
                            {
                                foreach (var step in ex.MitigationSteps)
                                {
                                    logger.LogError(step);
                                }
                            }
                            logger.LogError("");
                            return;
                        }
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

            // Handle custom blueprint permissions (parameter-based approach)
            if (customPermissions)
            {
                // Load existing config
                if (!File.Exists(configPath))
                {
                    logger.LogError($"Configuration file not found: {configPath}");
                    logger.LogError("Run 'a365 config init' first to create a base configuration.");
                    context.ExitCode = 1;
                    return;
                }

                try
                {
                    var existingJson = await File.ReadAllTextAsync(configPath);
                    var currentConfig = JsonSerializer.Deserialize<Agent365Config>(existingJson);

                    if (currentConfig == null)
                    {
                        logger.LogError("Failed to parse existing config file.");
                        context.ExitCode = 1;
                        return;
                    }

                    var permissions = currentConfig.CustomBlueprintPermissions != null
                        ? new List<CustomResourcePermission>(currentConfig.CustomBlueprintPermissions)
                        : new List<CustomResourcePermission>();

                    // Handle --reset flag
                    if (reset)
                    {
                        Console.WriteLine("Clearing all custom blueprint permissions...");
                        permissions.Clear();
                    }
                    // Handle add/update with --resourceAppId and --scopes
                    else if (!string.IsNullOrWhiteSpace(resourceAppId) && !string.IsNullOrWhiteSpace(scopes))
                    {
                        // Validate resourceAppId format
                        if (!Guid.TryParse(resourceAppId, out _))
                        {
                            logger.LogError($"ERROR: Invalid resourceAppId '{resourceAppId}'. Must be a valid GUID format.");
                            context.ExitCode = 1;
                            return;
                        }

                        // Validate scopes input before processing
                        if (string.IsNullOrWhiteSpace(scopes))
                        {
                            logger.LogError("ERROR: --scopes parameter cannot be empty.");
                            context.ExitCode = 1;
                            return;
                        }

                        // Parse and validate scopes
                        var scopesList = scopes
                            .Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .ToList();

                        // This check catches edge case of "  ,  ,  " input
                        if (scopesList.Count == 0)
                        {
                            logger.LogError("ERROR: At least one valid scope is required (all entries were empty).");
                            context.ExitCode = 1;
                            return;
                        }

                        // Check if resourceAppId already exists
                        var existing = permissions.FirstOrDefault(p =>
                            p.ResourceAppId.Equals(resourceAppId, StringComparison.OrdinalIgnoreCase));

                        if (existing != null)
                        {
                            // Show current scopes
                            Console.WriteLine($"\nResource {resourceAppId} already exists with scopes:");
                            Console.WriteLine($"  {string.Join(", ", existing.Scopes)}");
                            Console.WriteLine();

                            // Ask for confirmation unless --force is specified
                            if (!force)
                            {
                                Console.Write("Do you want to overwrite with new scopes? (y/N): ");
                                var response = Console.ReadLine()?.Trim().ToLowerInvariant();

                                if (response != "y" && response != "yes")
                                {
                                    Console.WriteLine("No changes made.");
                                    return;
                                }
                            }

                            // Update existing permission
                            existing.Scopes = scopesList;
                            Console.WriteLine("\nPermission updated successfully.");
                        }
                        else
                        {
                            // Add new permission (resource name will be auto-resolved during setup)
                            var newPermission = new CustomResourcePermission
                            {
                                ResourceAppId = resourceAppId,
                                ResourceName = null, // Will be auto-resolved during setup
                                Scopes = scopesList
                            };

                            // Validate the new permission
                            var (isValid, errors) = newPermission.Validate();
                            if (!isValid)
                            {
                                logger.LogError("ERROR: Invalid permission:");
                                foreach (var error in errors)
                                {
                                    logger.LogError($"  {error}");
                                }
                                context.ExitCode = 1;
                                return;
                            }

                            permissions.Add(newPermission);
                            Console.WriteLine("\nPermission added successfully.");
                        }
                    }
                    // Show current permissions if no parameters provided
                    else if (string.IsNullOrWhiteSpace(resourceAppId) && string.IsNullOrWhiteSpace(scopes))
                    {
                        if (permissions.Count == 0)
                        {
                            Console.WriteLine("\nNo custom blueprint permissions configured.");
                            Console.WriteLine("\nTo add permissions, use:");
                            Console.WriteLine("  a365 config init --custom-blueprint-permissions --resourceAppId <guid> --scopes <scope1,scope2>");
                            return;
                        }

                        Console.WriteLine("\nCurrent custom blueprint permissions:");
                        for (int i = 0; i < permissions.Count; i++)
                        {
                            var perm = permissions[i];
                            var displayName = string.IsNullOrWhiteSpace(perm.ResourceName)
                                ? perm.ResourceAppId
                                : $"{perm.ResourceName} ({perm.ResourceAppId})";
                            Console.WriteLine($"  {i + 1}. {displayName}");
                            Console.WriteLine($"     Scopes: {string.Join(", ", perm.Scopes)}");
                        }
                        return;
                    }
                    // Invalid parameter combination
                    else
                    {
                        logger.LogError("ERROR: Both --resourceAppId and --scopes are required to add/update a permission.");
                        logger.LogError("Usage:");
                        logger.LogError("  a365 config init --custom-blueprint-permissions --resourceAppId <guid> --scopes <scope1,scope2>");
                        logger.LogError("  a365 config init --custom-blueprint-permissions --reset");
                        context.ExitCode = 1;
                        return;
                    }

                    // Create new config with updated permissions using helper method
                    var updatedConfig = currentConfig.WithCustomBlueprintPermissions(
                        permissions.Count > 0 ? permissions : null);

                    // Validate the updated config
                    var configErrors = updatedConfig.Validate();
                    if (configErrors.Count > 0)
                    {
                        logger.LogError("Configuration validation failed:");
                        foreach (var err in configErrors)
                        {
                            logger.LogError($"  {err}");
                        }
                        context.ExitCode = 1;
                        return;
                    }

                    // Save updated config (static properties only)
                    var staticConfig = updatedConfig.GetStaticConfig();
                    var json = JsonSerializer.Serialize(staticConfig, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(configPath, json);

                    // Also save to global config directory
                    if (!useGlobal)
                    {
                        var globalConfigPath = Path.Combine(configDir, "a365.config.json");
                        Directory.CreateDirectory(configDir);
                        await File.WriteAllTextAsync(globalConfigPath, json);
                    }

                    Console.WriteLine($"\nConfiguration saved to: {configPath}");

                    // Check if blueprint exists (by checking generated config for agentBlueprintId)
                    var generatedConfigPath = useGlobal
                        ? Path.Combine(configDir, "a365.generated.config.json")
                        : Path.Combine(Environment.CurrentDirectory, "a365.generated.config.json");

                    bool blueprintExists = false;
                    if (File.Exists(generatedConfigPath))
                    {
                        try
                        {
                            var generatedJson = await File.ReadAllTextAsync(generatedConfigPath);
                            var generatedConfig = JsonSerializer.Deserialize<Agent365Config>(generatedJson);
                            blueprintExists = !string.IsNullOrWhiteSpace(generatedConfig?.AgentBlueprintId);
                        }
                        catch
                        {
                            // If we can't read generated config, assume blueprint doesn't exist
                            blueprintExists = false;
                        }
                    }

                    // Show context-aware next step message
                    if (blueprintExists && permissions.Count > 0)
                    {
                        Console.WriteLine("\nNext step: Run 'a365 setup permissions custom' to apply these permissions to your blueprint.");
                    }

                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to update custom permissions: {Message}", ex.Message);
                    context.ExitCode = 1;
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
                    logger.LogInformation("  a365 setup all      - Create Azure resources");
                    logger.LogInformation("  a365 deploy         - Deploy your agent");
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

                    // Use the model's method to get generated config with secrets decrypted for display
                    var generatedConfig = config.GetGeneratedConfigForDisplay(logger);
                    var displayJson = JsonSerializer.Serialize(generatedConfig, displayOptions);

                    // Post-process: Replace escaped backslashes
                    displayJson = System.Text.RegularExpressions.Regex.Replace(displayJson, @"\\\\", @"\");

                    Console.WriteLine(displayJson);

                    // Display resource consents table when showing generated config (default or -a)
                    // Skip table when using -g flag since resourceConsents are already in JSON output
                    if (displayGenerated && !showGenerated && config.ResourceConsents != null && config.ResourceConsents.Count > 0)
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
