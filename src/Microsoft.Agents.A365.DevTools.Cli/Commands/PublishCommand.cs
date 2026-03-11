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
/// Publish command – updates manifest.json IDs based on the agent blueprint ID
/// and packages the manifest files into a zip ready for manual upload.
/// </summary>
public class PublishCommand
{
    /// <summary>
    /// Gets the project directory from config, with fallback to current directory.
    /// </summary>
    private static string GetProjectDirectory(Agent365Config config, ILogger logger)
    {
        var projectPath = config.DeploymentProjectPath;

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            logger.LogWarning("deploymentProjectPath not configured, using current directory. Set this in a365.config.json for portability.");
            return Environment.CurrentDirectory;
        }

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
        var command = new Command("publish", "Update manifest IDs and create a package for upload to Microsoft 365 Admin Center");

        var dryRunOption = new Option<bool>("--dry-run", "Show changes without writing files");

        command.AddOption(dryRunOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);

            var isNormalExit = false;

            try
            {
                var config = await configService.LoadAsync();
                var blueprintId = config.AgentBlueprintId;
                var displayName = config.AgentBlueprintDisplayName;

                if (string.IsNullOrWhiteSpace(blueprintId))
                {
                    logger.LogError("agentBlueprintId missing in configuration. Run 'a365 setup all' first.");
                    return;
                }

                var baseDir = GetProjectDirectory(config, logger);
                var manifestDir = Path.Combine(baseDir, "manifest");
                var manifestPath = Path.Combine(manifestDir, "manifest.json");
                var agenticUserManifestPath = Path.Combine(manifestDir, "agenticUserTemplateManifest.json");

                logger.LogDebug("Project directory: {BaseDir}", baseDir);
                logger.LogDebug("Blueprint ID: {BlueprintId}", blueprintId);

                if (!Directory.Exists(manifestDir))
                {
                    logger.LogInformation("Extracting manifest templates...");
                    Directory.CreateDirectory(manifestDir);

                    if (!manifestTemplateService.ExtractTemplates(manifestDir))
                    {
                        logger.LogError("Failed to extract manifest templates from embedded resources.");
                        return;
                    }
                }

                if (!File.Exists(agenticUserManifestPath))
                {
                    if (!manifestTemplateService.EnsureTemplateFile(manifestDir, "agenticUserTemplateManifest.json"))
                    {
                        logger.LogError("Failed to extract agenticUserTemplateManifest.json from embedded resources.");
                        return;
                    }
                }

                if (!File.Exists(manifestPath))
                {
                    logger.LogError("Manifest not found: {Path}", manifestPath);
                    return;
                }

                var updatedManifest = await UpdateManifestFileAsync(displayName, blueprintId, manifestPath);
                var updatedAgenticUserManifest = await UpdateAgenticUserManifestTemplateFileAsync(blueprintId, agenticUserManifestPath);

                if (dryRun)
                {
                    logger.LogInformation("DRY RUN: manifest.json (not saved):\n{Json}", updatedManifest);
                    logger.LogInformation("DRY RUN: agenticUserTemplateManifest.json (not saved):\n{Json}", updatedAgenticUserManifest);
                    isNormalExit = true;
                    return;
                }

                await File.WriteAllTextAsync(manifestPath, updatedManifest);
                await File.WriteAllTextAsync(agenticUserManifestPath, updatedAgenticUserManifest);

                logger.LogInformation("Manifest updated: {Path}", manifestPath);
                logger.LogInformation("");
                logger.LogInformation("Customize before packaging:");
                logger.LogInformation("  version              - increment for republishing (e.g., 1.0.1), must be higher than previous");

                if (!string.IsNullOrWhiteSpace(displayName) && displayName.Length > 30)
                    logger.LogWarning("  name.short           - EXCEEDS 30 chars ({Length}), currently: \"{Name}\" -- shorten before packaging", displayName.Length, displayName);
                else
                    logger.LogInformation("  name.short           - 30 chars max, currently: \"{Name}\"", displayName);

                logger.LogInformation("  name.full            - displayed in Microsoft 365");
                logger.LogInformation("  description.short    - 1-2 sentences");
                logger.LogInformation("  description.full     - detailed capabilities");
                logger.LogInformation("  developer.*          - name, websiteUrl, privacyUrl");
                logger.LogInformation("  icons                - replace color.png and outline.png with your branding");
                logger.LogInformation("");

                if (!Console.IsInputRedirected)
                {
                    Console.Write("Open manifest in your default editor now? (Y/n): ");
                    var openResponse = Console.ReadLine()?.Trim().ToLowerInvariant();
                    if (openResponse != "n" && openResponse != "no")
                        FileHelper.TryOpenFileInDefaultEditor(manifestPath, logger);

                    Console.Write("Press Enter when you have finished editing the manifest to continue: ");
                    Console.Out.Flush();
                    Console.ReadLine();
                    Console.WriteLine();
                }

                var zipPath = Path.Combine(manifestDir, "manifest.zip");
                if (File.Exists(zipPath))
                {
                    try { File.Delete(zipPath); } catch { /* ignore */ }
                }

                string[] candidateNames = ["manifest.json", "agenticUserTemplateManifest.json", "color.png", "outline.png", "logo.png", "icon.png"];
                var filesToZip = candidateNames
                    .Select(name => Path.Combine(manifestDir, name))
                    .Where(File.Exists)
                    .ToList();

                if (filesToZip.Count == 0)
                {
                    logger.LogError("No manifest files found in {Dir}", manifestDir);
                    return;
                }

                using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    foreach (var file in filesToZip)
                    {
                        logger.LogDebug("Adding {File} to manifest.zip", Path.GetFileName(file));
                        var entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                        await using var entryStream = entry.Open();
                        await using var src = File.OpenRead(file);
                        await src.CopyToAsync(entryStream);
                    }
                }

                logger.LogInformation("Package created: {ZipPath}", zipPath);
                logger.LogInformation("");
                logger.LogInformation("To publish: https://admin.microsoft.com > Agents > All agents > Upload custom agent");
                logger.LogInformation("For details: https://learn.microsoft.com/en-us/copilot/microsoft-365/agent-essentials/agent-lifecycle/agent-upload-agents");

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

    private static async Task<string> UpdateManifestFileAsync(string? displayName, string blueprintId, string manifestPath)
    {
        var manifestText = await File.ReadAllTextAsync(manifestPath);
        var node = JsonNode.Parse(manifestText) ?? new JsonObject();

        node["id"] = blueprintId;

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            if (node["name"] is not JsonObject nameObj)
            {
                nameObj = new JsonObject();
                node["name"] = nameObj;
            }

            nameObj["short"] = displayName;
            nameObj["full"] = displayName;
        }

        if (node["bots"] is JsonArray bots && bots.Count > 0 && bots[0] is JsonObject botObj)
            botObj["botId"] = blueprintId;

        if (node["webApplicationInfo"] is JsonObject webInfo)
        {
            webInfo["id"] = blueprintId;
            webInfo["resource"] = $"api://{blueprintId}";
        }

        if (node["copilotAgents"] is JsonObject ca && ca["customEngineAgents"] is JsonArray cea && cea.Count > 0 && cea[0] is JsonObject ceObj)
            ceObj["id"] = blueprintId;

        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task<string> UpdateAgenticUserManifestTemplateFileAsync(string blueprintId, string agenticUserManifestPath)
    {
        var contents = await File.ReadAllTextAsync(agenticUserManifestPath);
        var node = JsonNode.Parse(contents) ?? new JsonObject();
        node["agentIdentityBlueprintId"] = blueprintId;
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
