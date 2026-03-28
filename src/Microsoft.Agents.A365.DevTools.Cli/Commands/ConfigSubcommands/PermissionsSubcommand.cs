// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.ConfigSubcommands;

public static class ConfigPermissionsSubcommand
{
    public static Command CreateCommand(ILogger logger, string configDir)
    {
        var cmd = new Command("permissions", "Manage custom blueprint permissions in a365.config.json");

        var resourceAppIdOption = new Option<string?>("--resource-app-id", "Resource application ID (GUID) for custom blueprint permission");
        var scopesOption = new Option<string?>("--scopes", "Comma-separated list of scopes for the custom blueprint permission");
        var resetOption = new Option<bool>("--reset", "Clear all custom blueprint permissions");
        var forceOption = new Option<bool>("--force", "Skip confirmation prompts when updating existing permissions");

        cmd.AddOption(resourceAppIdOption);
        cmd.AddOption(scopesOption);
        cmd.AddOption(resetOption);
        cmd.AddOption(forceOption);

        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            string? resourceAppId = context.ParseResult.GetValueForOption(resourceAppIdOption);
            string? scopes = context.ParseResult.GetValueForOption(scopesOption);
            bool reset = context.ParseResult.GetValueForOption(resetOption);
            bool force = context.ParseResult.GetValueForOption(forceOption);

            // Resolve config path: current directory first, then global fallback
            var localConfigPath = Path.Combine(Environment.CurrentDirectory, "a365.config.json");
            var globalConfigPath = Path.Combine(configDir, "a365.config.json");
            var configPath = File.Exists(localConfigPath) ? localConfigPath : globalConfigPath;

            if (!File.Exists(configPath))
            {
                logger.LogError("Configuration file not found. Run 'a365 config init' first to create a base configuration.");
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

                bool? permissionAdded = null; // true = added, false = updated; null = reset (not add/update)

                // Handle --reset flag
                if (reset)
                {
                    Console.WriteLine("Clearing all custom blueprint permissions...");
                    permissions.Clear();
                }
                // Handle add/update with --resource-app-id and --scopes
                else if (!string.IsNullOrWhiteSpace(resourceAppId) && !string.IsNullOrWhiteSpace(scopes))
                {
                    // Validate resourceAppId format
                    if (!Guid.TryParse(resourceAppId, out _))
                    {
                        logger.LogError("ERROR: Invalid resource-app-id '{ResourceAppId}'. Must be a valid GUID format.", resourceAppId);
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

                    // Validate the new permission before add/update
                    var validation = new CustomResourcePermission
                    {
                        ResourceAppId = resourceAppId,
                        Scopes = scopesList
                    };
                    var (isValid, errors) = validation.Validate();
                    if (!isValid)
                    {
                        logger.LogError("ERROR: Invalid permission:");
                        foreach (var error in errors)
                            logger.LogError("  {Error}", error);
                        context.ExitCode = 1;
                        return;
                    }

                    // Show confirmation prompt when updating an existing entry (unless --force)
                    var existing = permissions.FirstOrDefault(
                        p => p.ResourceAppId.Equals(resourceAppId, StringComparison.OrdinalIgnoreCase));

                    if (existing != null && !force)
                    {
                        Console.WriteLine($"\nResource {resourceAppId} already exists with scopes:");
                        Console.WriteLine($"  {string.Join(", ", existing.Scopes)}");
                        Console.WriteLine();
                        Console.Write("Do you want to overwrite with new scopes? (y/N): ");
                        var response = Console.ReadLine()?.Trim().ToLowerInvariant();
                        if (response != "y" && response != "yes")
                        {
                            Console.WriteLine("No changes made.");
                            return;
                        }
                    }

                    permissionAdded = CustomResourcePermission.AddOrUpdate(permissions, resourceAppId, scopesList);
                }
                // Show current permissions if no parameters provided
                else if (string.IsNullOrWhiteSpace(resourceAppId) && string.IsNullOrWhiteSpace(scopes))
                {
                    if (permissions.Count == 0)
                    {
                        Console.WriteLine("\nNo custom blueprint permissions configured.");
                        Console.WriteLine("\nTo add permissions, use:");
                        Console.WriteLine("  a365 config permissions --resource-app-id <guid> --scopes <scope1,scope2>");
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
                    logger.LogError("ERROR: Both --resource-app-id and --scopes are required to add/update a permission.");
                    logger.LogError("Usage:");
                    logger.LogError("  a365 config permissions --resource-app-id <guid> --scopes <scope1,scope2>");
                    logger.LogError("  a365 config permissions --reset");
                    context.ExitCode = 1;
                    return;
                }

                // Save updated config (static properties only)
                var updatedConfig = currentConfig.WithCustomBlueprintPermissions(
                    permissions.Count > 0 ? permissions : null);

                var staticConfig = updatedConfig.GetStaticConfig();
                var json = JsonSerializer.Serialize(staticConfig, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(configPath, json);

                if (permissionAdded.HasValue)
                    Console.WriteLine(permissionAdded.Value ? "\nPermission added successfully." : "\nPermission updated successfully.");
                else if (reset)
                    Console.WriteLine("\nCustom blueprint permissions cleared.");

                Console.WriteLine($"\nConfiguration saved to: {configPath}");

                // Show next step hint if blueprint exists
                var generatedConfigPath = Path.Combine(
                    Path.GetDirectoryName(configPath)!, "a365.generated.config.json");
                if (File.Exists(generatedConfigPath) && permissions.Count > 0)
                {
                    try
                    {
                        var generatedJson = await File.ReadAllTextAsync(generatedConfigPath);
                        var generatedConfig = JsonSerializer.Deserialize<Agent365Config>(generatedJson);
                        if (!string.IsNullOrWhiteSpace(generatedConfig?.AgentBlueprintId))
                            Console.WriteLine("\nNext step: Run 'a365 setup permissions custom' to apply these permissions to your blueprint.");
                    }
                    catch
                    {
                        // Ignore errors reading generated config
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to update custom permissions: {Message}", ex.Message);
                context.ExitCode = 1;
            }
        });

        return cmd;
    }
}
