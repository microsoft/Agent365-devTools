// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for managing manifest templates embedded in the CLI binary.
/// Handles extraction, customization, and packaging of manifest files.
/// </summary>
public class ManifestTemplateService
{
    private readonly ILogger<ManifestTemplateService> _logger;
    private const string ResourcePrefix = "Microsoft.Agents.A365.DevTools.Cli.Templates.";

    public ManifestTemplateService(ILogger<ManifestTemplateService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Extracts embedded manifest templates to a working directory.
    /// </summary>
    /// <param name="workingDirectory">Directory to extract templates to</param>
    /// <returns>True if extraction succeeded</returns>
    public bool ExtractTemplates(string workingDirectory)
    {
        try
        {
            if (!Directory.Exists(workingDirectory))
            {
                Directory.CreateDirectory(workingDirectory);
            }

            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = new[]
            {
                "manifest.json",
                "agenticUserTemplateManifest.json",
                "color.png",
                "outline.png"
            };

            foreach (var resourceName in resourceNames)
            {
                var fullResourceName = $"{ResourcePrefix}{resourceName}";
                using var stream = assembly.GetManifestResourceStream(fullResourceName);
                
                if (stream == null)
                {
                    _logger.LogError("Embedded resource not found: {Resource}", fullResourceName);
                    return false;
                }

                var targetPath = Path.Combine(workingDirectory, resourceName);
                using var fileStream = File.Create(targetPath);
                stream.CopyTo(fileStream);
                
                _logger.LogDebug("Extracted template: {File}", resourceName);
            }

            _logger.LogInformation("Extracted manifest templates to {Directory}", workingDirectory);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract manifest templates");
            return false;
        }
    }

    /// <summary>
    /// Updates manifest files with agent-specific identifiers.
    /// </summary>
    /// <param name="workingDirectory">Directory containing extracted templates</param>
    /// <param name="blueprintId">Agent blueprint ID to inject</param>
    /// <param name="agentDisplayName">Display name for the agent (optional)</param>
    /// <returns>True if update succeeded</returns>
    public async Task<bool> UpdateManifestIdentifiersAsync(
        string workingDirectory,
        string blueprintId,
        string? agentDisplayName = null)
    {
        try
        {
            // Update manifest.json
            var manifestPath = Path.Combine(workingDirectory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                _logger.LogError("Manifest file not found at {Path}", manifestPath);
                return false;
            }

            var manifestText = await File.ReadAllTextAsync(manifestPath);
            var manifestNode = JsonNode.Parse(manifestText) ?? new JsonObject();

            // Update top-level id
            manifestNode["id"] = blueprintId;

            // Update name if provided
            if (!string.IsNullOrWhiteSpace(agentDisplayName))
            {
                if (manifestNode["name"] is not JsonObject nameObj)
                {
                    nameObj = new JsonObject();
                    manifestNode["name"] = nameObj;
                }
                else
                {
                    nameObj = (JsonObject)manifestNode["name"]!;
                }

                nameObj["short"] = agentDisplayName;
                nameObj["full"] = agentDisplayName;
                _logger.LogInformation("Updated manifest name to: {Name}", agentDisplayName);
            }

            // Update bots[0].botId
            if (manifestNode["bots"] is JsonArray bots && bots.Count > 0 && bots[0] is JsonObject botObj)
            {
                botObj["botId"] = blueprintId;
            }

            // Update copilotAgents.customEngineAgents[0].id
            if (manifestNode["copilotAgents"] is JsonObject ca && 
                ca["customEngineAgents"] is JsonArray cea && 
                cea.Count > 0 && 
                cea[0] is JsonObject ceObj)
            {
                ceObj["id"] = blueprintId;
            }

            var updatedManifest = manifestNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(manifestPath, updatedManifest);
            _logger.LogInformation("Updated manifest.json with blueprint ID: {Id}", blueprintId);

            // Update agenticUserTemplateManifest.json
            var templateManifestPath = Path.Combine(workingDirectory, "agenticUserTemplateManifest.json");
            if (!File.Exists(templateManifestPath))
            {
                _logger.LogError("Template manifest file not found at {Path}", templateManifestPath);
                return false;
            }

            var templateText = await File.ReadAllTextAsync(templateManifestPath);
            var templateNode = JsonNode.Parse(templateText) ?? new JsonObject();

            // Update agentIdentityBlueprintId (this replaces the old webApplicationInfo.id logic)
            templateNode["agentIdentityBlueprintId"] = blueprintId;

            var updatedTemplate = templateNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(templateManifestPath, updatedTemplate);
            _logger.LogInformation("Updated agenticUserTemplateManifest.json with agentIdentityBlueprintId: {Id}", blueprintId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update manifest identifiers");
            return false;
        }
    }

    /// <summary>
    /// Creates a zip archive containing all manifest files.
    /// </summary>
    /// <param name="workingDirectory">Directory containing manifest files</param>
    /// <param name="outputZipPath">Path where zip file should be created</param>
    /// <returns>True if zip creation succeeded</returns>
    public async Task<bool> CreateManifestZipAsync(string workingDirectory, string outputZipPath)
    {
        try
        {
            if (File.Exists(outputZipPath))
            {
                File.Delete(outputZipPath);
            }

            var filesToZip = new[]
            {
                "manifest.json",
                "agenticUserTemplateManifest.json",
                "color.png",
                "outline.png"
            };

            using var zipStream = new FileStream(outputZipPath, FileMode.Create, FileAccess.ReadWrite);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

            foreach (var fileName in filesToZip)
            {
                var filePath = Path.Combine(workingDirectory, fileName);
                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("Skipping missing file: {File}", fileName);
                    continue;
                }

                var entry = archive.CreateEntry(fileName, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var fileStream = File.OpenRead(filePath);
                await fileStream.CopyToAsync(entryStream);
                
                _logger.LogInformation("Added {File} to manifest.zip", fileName);
            }

            _logger.LogInformation("Created manifest archive: {ZipPath}", outputZipPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create manifest zip");
            return false;
        }
    }

    /// <summary>
    /// Validates that all required embedded resources are present in the assembly.
    /// </summary>
    /// <returns>True if all resources are present</returns>
    public bool ValidateEmbeddedResources()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var requiredResources = new[]
        {
            $"{ResourcePrefix}manifest.json",
            $"{ResourcePrefix}agenticUserTemplateManifest.json",
            $"{ResourcePrefix}color.png",
            $"{ResourcePrefix}outline.png"
        };

        var allResources = assembly.GetManifestResourceNames();
        var missingResources = requiredResources.Where(r => !allResources.Contains(r)).ToList();

        if (missingResources.Any())
        {
            _logger.LogError("Missing embedded resources: {Resources}", string.Join(", ", missingResources));
            return false;
        }

        _logger.LogDebug("All required embedded resources validated");
        return true;
    }

    /// <summary>
    /// Checks if a manifest directory exists and validates its format.
    /// </summary>
    /// <param name="projectPath">Project root path</param>
    /// <param name="manifestDirectory">Output parameter with manifest directory path if found</param>
    /// <returns>True if valid manifest directory exists</returns>
    public bool TryGetExistingManifestDirectory(string projectPath, out string? manifestDirectory)
    {
        manifestDirectory = null;

        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
        {
            return false;
        }

        var manifestDir = Path.Combine(projectPath, "manifest");
        if (!Directory.Exists(manifestDir))
        {
            return false;
        }

        manifestDirectory = manifestDir;
        return true;
    }

    /// <summary>
    /// Validates that existing manifest files have required structure for updates.
    /// </summary>
    /// <param name="manifestDirectory">Directory containing manifest files</param>
    /// <returns>True if manifest is compatible with CLI updates</returns>
    public async Task<bool> ValidateManifestFormatAsync(string manifestDirectory)
    {
        try
        {
            var manifestPath = Path.Combine(manifestDirectory, "manifest.json");
            var templatePath = Path.Combine(manifestDirectory, "agenticUserTemplateManifest.json");

            // Check manifest.json exists
            if (!File.Exists(manifestPath))
            {
                _logger.LogError("manifest.json not found in {Directory}", manifestDirectory);
                return false;
            }

            // Validate manifest.json structure
            var manifestText = await File.ReadAllTextAsync(manifestPath);
            var manifestDoc = JsonDocument.Parse(manifestText);
            var root = manifestDoc.RootElement;

            // Check for required top-level properties
            if (!root.TryGetProperty("id", out _))
            {
                _logger.LogError("manifest.json missing required 'id' property");
                return false;
            }

            // Check agenticUserTemplateManifest.json if it exists
            if (File.Exists(templatePath))
            {
                var templateText = await File.ReadAllTextAsync(templatePath);
                var templateDoc = JsonDocument.Parse(templateText);
                var templateRoot = templateDoc.RootElement;

                if (!templateRoot.TryGetProperty("agentIdentityBlueprintId", out _))
                {
                    _logger.LogError("agenticUserTemplateManifest.json missing required 'agentIdentityBlueprintId' property");
                    return false;
                }
            }
            else
            {
                _logger.LogWarning("agenticUserTemplateManifest.json not found. Will be created from template.");
            }

            _logger.LogInformation("Manifest format validation passed");
            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Invalid JSON format in manifest files");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate manifest format");
            return false;
        }
    }

    /// <summary>
    /// Copies existing manifest files to working directory, supplementing with templates as needed.
    /// </summary>
    /// <param name="sourceManifestDirectory">Source manifest directory</param>
    /// <param name="workingDirectory">Destination working directory</param>
    /// <returns>True if copy succeeded</returns>
    public async Task<bool> CopyAndSupplementManifestAsync(string sourceManifestDirectory, string workingDirectory)
    {
        try
        {
            if (!Directory.Exists(workingDirectory))
            {
                Directory.CreateDirectory(workingDirectory);
            }

            var assembly = Assembly.GetExecutingAssembly();

            // Copy or extract each required file
            var files = new[]
            {
                "manifest.json",
                "agenticUserTemplateManifest.json",
                "color.png",
                "outline.png"
            };

            foreach (var fileName in files)
            {
                var sourcePath = Path.Combine(sourceManifestDirectory, fileName);
                var destPath = Path.Combine(workingDirectory, fileName);

                if (File.Exists(sourcePath))
                {
                    // Copy existing file
                    File.Copy(sourcePath, destPath, overwrite: true);
                    _logger.LogInformation("Copied existing file: {File}", fileName);
                }
                else
                {
                    // Extract from embedded resources
                    var fullResourceName = $"{ResourcePrefix}{fileName}";
                    using var stream = assembly.GetManifestResourceStream(fullResourceName);

                    if (stream == null)
                    {
                        _logger.LogError("Embedded resource not found: {Resource}", fullResourceName);
                        return false;
                    }

                    using var fileStream = File.Create(destPath);
                    await stream.CopyToAsync(fileStream);
                    _logger.LogInformation("Created from template: {File}", fileName);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy and supplement manifest files");
            return false;
        }
    }
}
