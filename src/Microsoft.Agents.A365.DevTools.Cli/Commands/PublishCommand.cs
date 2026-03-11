// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// Publish command – updates manifest.json ids based on the generated agent blueprint id
/// and packages the manifest files into a zip ready for manual upload.
/// </summary>
public class PublishCommand
{
    /// <summary>
    /// Gets the project directory from config, with fallback to current directory.
    /// Ensures absolute path resolution for portability.
    /// </summary>
    /// <param name="config">Configuration containing deploymentProjectPath</param>
    /// <param name="logger">Logger for warnings</param>
    /// <returns>Absolute path to project directory</returns>
    private static string GetProjectDirectory(Agent365Config config, ILogger logger)
    {
        var projectPath = config.DeploymentProjectPath;

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            logger.LogWarning("deploymentProjectPath not configured, using current directory. Set this in a365.config.json for portability.");
            return Environment.CurrentDirectory;
        }

        // Resolve to absolute path (handles both relative and absolute paths)
        try
        {
            var absolutePath = Path.IsPathRooted(projectPath)
                ? projectPath
                : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, projectPath));

            if (!Directory.Exists(absolutePath))
            {
                logger.LogWarning("Configured deploymentProjectPath does not exist: {Path}. Using current directory.", absolutePath);
                return Environment.CurrentDirectory;
            }

            return absolutePath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve deploymentProjectPath: {Path}. Using current directory.", projectPath);
            return Environment.CurrentDirectory;
        }
    }

    public static Command CreateCommand(
        ILogger<PublishCommand> logger,
        IConfigService configService,
        ManifestTemplateService manifestTemplateService)
    {
        var command = new Command("publish", "Update manifest.json IDs and create a manifest package for upload to the Microsoft 365 Admin Center");

        var dryRunOption = new Option<bool>("--dry-run", "Show changes without writing file or calling APIs");

        command.AddOption(dryRunOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);

            var isNormalExit = false;

            try
            {
                // Load configuration using ConfigService
                var config = await configService.LoadAsync();

                // Extract required values from config
                var agentBlueprintDisplayName = config.AgentBlueprintDisplayName;
                var blueprintId = config.AgentBlueprintId;

                if (string.IsNullOrWhiteSpace(blueprintId))
                {
                    logger.LogError("agentBlueprintId missing in configuration. Run 'a365 setup all' first.");
                    return;
                }

                // Use deploymentProjectPath from config for portability
                var baseDir = GetProjectDirectory(config, logger);
                var manifestDir = Path.Combine(baseDir, "manifest");
                var manifestPath = Path.Combine(manifestDir, "manifest.json");
                var agenticUserManifestTemplatePath = Path.Combine(manifestDir, "agenticUserTemplateManifest.json");

                logger.LogDebug("Using project directory: {BaseDir}", baseDir);
                logger.LogDebug("Using manifest directory: {ManifestDir}", manifestDir);
                logger.LogDebug("Using blueprint ID: {BlueprintId}", blueprintId);

                // If manifest directory doesn't exist, extract templates from embedded resources
                if (!Directory.Exists(manifestDir))
                {
                    logger.LogInformation("Manifest directory not found. Extracting templates from embedded resources...");
                    Directory.CreateDirectory(manifestDir);

                    if (!manifestTemplateService.ExtractTemplates(manifestDir))
                    {
                        logger.LogError("Failed to extract manifest templates from embedded resources");
                        return;
                    }

                    logger.LogInformation("Successfully extracted manifest templates to {ManifestDir}", manifestDir);
                    logger.LogInformation("Please customize the manifest files before publishing");
                }

                // Ensure agenticUserTemplateManifest.json exists in the manifest directory.
                // It may be missing if the manifest directory was created by a previous partial run
                // or an older CLI version that did not include this file.
                if (!File.Exists(agenticUserManifestTemplatePath))
                {
                    logger.LogInformation("agenticUserTemplateManifest.json not found. Extracting from embedded resources...");
                    if (!manifestTemplateService.EnsureTemplateFile(manifestDir, "agenticUserTemplateManifest.json"))
                    {
                        logger.LogError("Failed to extract agenticUserTemplateManifest.json from embedded resources");
                        return;
                    }
                }

                if (!File.Exists(manifestPath))
                {
                    logger.LogError("Manifest file not found at {Path}", manifestPath);
                    logger.LogError("Expected location based on deploymentProjectPath: {ProjectPath}", baseDir);
                    return;
                }

                string updatedManifest = await UpdateManifestFileAsync(logger, agentBlueprintDisplayName, blueprintId, manifestPath);

                string updatedAgenticUserManifestTemplate = await UpdateAgenticUserManifestTemplateFileAsync(logger, agentBlueprintDisplayName, blueprintId, agenticUserManifestTemplatePath);

                if (dryRun)
                {
                    logger.LogInformation("DRY RUN: Updated manifest (not saved):\n{Json}", updatedManifest);
                    logger.LogInformation("DRY RUN: Updated agentic user manifest template (not saved):\n{Json}", updatedAgenticUserManifestTemplate);
                    logger.LogInformation("DRY RUN: Skipping zipping");
                    isNormalExit = true;
                    return;
                }

                await File.WriteAllTextAsync(manifestPath, updatedManifest);
                logger.LogInformation("Manifest updated successfully with agentBlueprintId {Id}", blueprintId);

                await File.WriteAllTextAsync(agenticUserManifestTemplatePath, updatedAgenticUserManifestTemplate);
                logger.LogInformation("Agentic user manifest template updated successfully with agentBlueprintId {Id}", blueprintId);

                logger.LogDebug("Manifest files written to disk");

                // Interactive pause for user customization
                logger.LogInformation("");
                logger.LogInformation("=== MANIFEST UPDATED ===");
                Console.WriteLine($"Location: {manifestPath}");
                logger.LogInformation("");
                logger.LogInformation("");
                logger.LogInformation("=== CUSTOMIZE YOUR AGENT MANIFEST ===");
                logger.LogInformation("");
                logger.LogInformation("Please customize these fields before publishing:");
                logger.LogInformation("");
                logger.LogInformation("  Version ('version')");
                logger.LogInformation("    - Increment for republishing (e.g., 1.0.0 to 1.0.1)");
                logger.LogInformation("    - REQUIRED: Must be higher than previously published version");
                logger.LogInformation("");
                logger.LogInformation("  Agent Name ('name.short' and 'name.full')");
                logger.LogInformation("    - Make it descriptive and user-friendly");
                logger.LogInformation("    - Currently: {Name}", agentBlueprintDisplayName);
                logger.LogInformation("    - IMPORTANT: 'name.short' must be 30 characters or less");
                logger.LogInformation("");
                logger.LogInformation("  Descriptions ('description.short' and 'description.full')");
                logger.LogInformation("    - Short: 1-2 sentences");
                logger.LogInformation("    - Full: Detailed capabilities");
                logger.LogInformation("");
                logger.LogInformation("  Developer Info ('developer.name', 'developer.websiteUrl', 'developer.privacyUrl')");
                logger.LogInformation("    - Should reflect your organization details");
                logger.LogInformation("");
                logger.LogInformation("  Icons");
                logger.LogInformation("    - Replace 'color.png' and 'outline.png' with your custom branding");
                logger.LogInformation("");

                // Ask if user wants to open the file now (skip when stdin is not a terminal)
                if (!Console.IsInputRedirected)
                {
                    Console.Write("Open manifest in your default editor now? (Y/n): ");
                    var openResponse = Console.ReadLine()?.Trim().ToLowerInvariant();

                    if (openResponse != "n" && openResponse != "no")
                    {
                        FileHelper.TryOpenFileInDefaultEditor(manifestPath, logger);
                    }

                    Console.Write("Press Enter when you have finished editing the manifest to continue: ");
                    Console.Out.Flush();
                    Console.ReadLine();
                }

                logger.LogInformation("Creating manifest package...");
                logger.LogInformation("");

                // Create manifest.zip including the required files
                var zipPath = Path.Combine(manifestDir, "manifest.zip");
                if (File.Exists(zipPath))
                {
                    try { File.Delete(zipPath); } catch { /* ignore */ }
                }

                // Collect all known manifest files that exist; order is deterministic
                string[] candidateNames = ["manifest.json", "agenticUserTemplateManifest.json", "color.png", "outline.png", "logo.png", "icon.png"];
                var expectedFiles = candidateNames
                    .Select(name => Path.Combine(manifestDir, name))
                    .Where(File.Exists)
                    .ToList();

                if (expectedFiles.Count == 0)
                {
                    logger.LogError("No manifest files found to zip in {Dir}", manifestDir);
                    return;
                }

                using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    foreach (var file in expectedFiles)
                    {
                        var entryName = Path.GetFileName(file);
                        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                        await using var entryStream = entry.Open();
                        await using var src = File.OpenRead(file);
                        await src.CopyToAsync(entryStream);
                        logger.LogInformation("Added {File} to manifest.zip", entryName);
                    }
                }
                logger.LogInformation("Created archive {ZipPath}", zipPath);

                // Print manual upload instructions
                logger.LogInformation("");
                logger.LogInformation("=== NEXT STEP: UPLOAD YOUR AGENT ===");
                logger.LogInformation("");
                logger.LogInformation("Your manifest package is ready at:");
                Console.WriteLine($"  {zipPath}");
                logger.LogInformation("");
                logger.LogInformation("To publish your agent to Microsoft 365:");
                logger.LogInformation("  1. Go to the Microsoft 365 Admin Center (https://admin.microsoft.com)");
                logger.LogInformation("  2. Navigate to Agents > All agents");
                logger.LogInformation("  3. Click 'Upload custom agent' and upload the manifest.zip file");
                logger.LogInformation("");
                logger.LogInformation("For detailed upload instructions, see:");
                logger.LogInformation("  https://learn.microsoft.com/en-us/copilot/microsoft-365/agent-essentials/agent-lifecycle/agent-upload-agents");
                logger.LogInformation("");

                isNormalExit = true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Publish command failed: {Message}", ex.Message);
            }
            finally
            {
                if (!isNormalExit)
                {
                    context.ExitCode = 1;
                }
            }
        });

        return command;
    }

    private static async Task<string> UpdateManifestFileAsync(ILogger<PublishCommand> logger, string? agentBlueprintDisplayName, string blueprintId, string manifestPath)
    {
        // Load manifest as mutable JsonNode
        var manifestText = await File.ReadAllTextAsync(manifestPath);
        var node = JsonNode.Parse(manifestText) ?? new JsonObject();

        // Update top-level id
        node["id"] = blueprintId;

        // Update name.short and name.full if agentBlueprintDisplayName is available
        if (!string.IsNullOrWhiteSpace(agentBlueprintDisplayName))
        {
            if (node["name"] is not JsonObject nameObj)
            {
                nameObj = new JsonObject();
                node["name"] = nameObj;
            }
            else
            {
                nameObj = (JsonObject)node["name"]!;
            }

            nameObj["short"] = agentBlueprintDisplayName;
            nameObj["full"] = agentBlueprintDisplayName;
            logger.LogInformation("Updated manifest name to: {Name}", agentBlueprintDisplayName);
        }

        // bots[0].botId
        if (node["bots"] is JsonArray bots && bots.Count > 0 && bots[0] is JsonObject botObj)
        {
            botObj["botId"] = blueprintId;
        }

        // webApplicationInfo.id + resource
        if (node["webApplicationInfo"] is JsonObject webInfo)
        {
            webInfo["id"] = blueprintId;
            webInfo["resource"] = $"api://{blueprintId}";
        }

        // copilotAgents.customEngineAgents[0].id
        if (node["copilotAgents"] is JsonObject ca && ca["customEngineAgents"] is JsonArray cea && cea.Count > 0 && cea[0] is JsonObject ceObj)
        {
            ceObj["id"] = blueprintId;
        }

        var updated = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return updated;
    }

    private static async Task<string> UpdateAgenticUserManifestTemplateFileAsync(ILogger<PublishCommand> logger, string? agentBlueprintDisplayName, string blueprintId, string agenticUserManifestTemplateFilePath)
    {
        // Load manifest as mutable JsonNode
        var agenticUserManifestTemplateFileContents = await File.ReadAllTextAsync(agenticUserManifestTemplateFilePath);
        var node = JsonNode.Parse(agenticUserManifestTemplateFileContents) ?? new JsonObject();

        // Update top-level id
        node["agentIdentityBlueprintId"] = blueprintId;

        var updated = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return updated;
    }

}
