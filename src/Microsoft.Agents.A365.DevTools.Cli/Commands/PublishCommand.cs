// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Net.Http.Headers;
using System.IO.Compression;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// Publish command – updates manifest.json ids based on the generated agent blueprint id.
/// Native C# implementation - no PowerShell dependencies.
/// </summary>
public class PublishCommand
{
    // MOS Titles service URLs
    private const string MosTitlesUrlProd = "https://titles.prod.mos.microsoft.com";
    
    /// <summary>
    /// Gets the appropriate MOS Titles URL based on environment variable override or defaults to production.
    /// Set MOS_TITLES_URL environment variable to override the default production URL.
    /// </summary>
    /// <param name="tenantId">Tenant ID (not used, kept for backward compatibility)</param>
    /// <returns>MOS Titles base URL from environment variable or production default</returns>
    private static string GetMosTitlesUrl(string? tenantId)
    {
        // Check for environment variable override
        var envUrl = Environment.GetEnvironmentVariable("MOS_TITLES_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            return envUrl;
        }
        
        return MosTitlesUrlProd;
    }

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
        GraphApiService graphApiService,
        ManifestTemplateService manifestTemplateService)
    {
        var command = new Command("publish", "Update manifest.json IDs and publish package; configure federated identity and app role assignments");

        var dryRunOption = new Option<bool>("--dry-run", "Show changes without writing file or calling APIs");
        var skipGraphOption = new Option<bool>("--skip-graph", "Skip Graph federated identity and role assignment steps");
        var mosEnvOption = new Option<string>("--mos-env", () => "prod", "MOS environment identifier (e.g. prod, dev) - use MOS_TITLES_URL environment variable for custom URLs");
        var mosPersonalTokenOption = new Option<string?>("--mos-token", () => Environment.GetEnvironmentVariable("MOS_PERSONAL_TOKEN"), "Override MOS token (personal token) - bypass script & cache");
        command.AddOption(dryRunOption);
        command.AddOption(skipGraphOption);
        command.AddOption(mosEnvOption);
        command.AddOption(mosPersonalTokenOption);

        command.SetHandler(async (bool dryRun, bool skipGraph, string mosEnv, string? mosPersonalToken) =>
        {
            try
            {
                // Load configuration using ConfigService
                var config = await configService.LoadAsync();

                // Extract required values from config
                var tenantId = config.TenantId;
                var agentBlueprintDisplayName = config.AgentBlueprintDisplayName;
                var blueprintId = config.AgentBlueprintId;

                if (string.IsNullOrWhiteSpace(blueprintId))
                {
                    logger.LogError("agentBlueprintId missing in configuration. Run 'a365 setup' first.");
                    return;
                }

                // Use deploymentProjectPath from config for portability
                var baseDir = GetProjectDirectory(config, logger);
                var manifestDir = Path.Combine(baseDir, "manifest");
                var manifestPath = Path.Combine(manifestDir, "manifest.json");
                var agenticUserManifestTemplatePath = Path.Combine(manifestDir, "agenticUserTemplateManifest.json");

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

                if (!File.Exists(manifestPath))
                {
                    logger.LogError("Manifest file not found at {Path}", manifestPath);
                    logger.LogError("Expected location based on deploymentProjectPath: {ProjectPath}", baseDir);
                    return;
                }

                // Determine MOS Titles URL based on tenant
                var mosTitlesBaseUrl = GetMosTitlesUrl(tenantId);
                logger.LogInformation("Using MOS Titles URL: {Url} (Tenant: {TenantId})", mosTitlesBaseUrl, tenantId ?? "unknown");

                // Warn if tenantId is missing
                if (string.IsNullOrWhiteSpace(tenantId))
                {
                    logger.LogWarning("tenantId missing in configuration; using default production MOS URL. Graph operations will be skipped.");
                }

                string updatedManifest = await UpdateManifestFileAsync(logger, agentBlueprintDisplayName, blueprintId, manifestPath);

                string updatedAgenticUserManifestTemplate = await UpdateAgenticUserManifestTemplateFileAsync(logger, agentBlueprintDisplayName, blueprintId, agenticUserManifestTemplatePath);

                if (dryRun)
                {
                    logger.LogInformation("DRY RUN: Updated manifest (not saved):\n{Json}", updatedManifest);
                    logger.LogInformation("DRY RUN: Updated agentic user manifest template (not saved):\n{Json}", updatedAgenticUserManifestTemplate);
                    logger.LogInformation("DRY RUN: Skipping zipping & API calls");
                    return;
                }

                await File.WriteAllTextAsync(manifestPath, updatedManifest);
                logger.LogInformation("Manifest updatedManifest successfully with agentBlueprintId {Id}", blueprintId);

                await File.WriteAllTextAsync(agenticUserManifestTemplatePath, updatedAgenticUserManifestTemplate);
                logger.LogInformation("Manifest agentic user manifest template successfully with agentBlueprintId {Id}", blueprintId);

                // Interactive pause for user customization
                logger.LogInformation("");
                logger.LogInformation("=== CUSTOMIZE YOUR AGENT MANIFEST ===");
                logger.LogInformation("");
                logger.LogInformation("Your manifest has been updated at: {ManifestPath}", manifestPath);
                logger.LogInformation("");
                logger.LogInformation("Please customize these fields before publishing:");
                logger.LogInformation("    Version ('version'): Increment for republishing (e.g., 1.0.0 to 1.0.1)");
                logger.LogInformation("    REQUIRED: Must be higher than previously published version");
                logger.LogInformation("    Agent Name ('name.short' and 'name.full'): Make it descriptive and user-friendly");
                logger.LogInformation("    Currently: {Name}", agentBlueprintDisplayName);
                logger.LogInformation("    IMPORTANT: 'name.short' must be 30 characters or less");
                logger.LogInformation("    Descriptions ('description.short' and 'description.full'): Explain what your agent does");
                logger.LogInformation("    Short: 1-2 sentences, Full: Detailed capabilities");
                logger.LogInformation("    Developer Info ('developer.name', 'developer.websiteUrl', 'developer.privacyUrl')");
                logger.LogInformation("    Should reflect your organization details");
                logger.LogInformation("    Icons: Replace 'color.png' and 'outline.png' with your custom branding");
                logger.LogInformation("");
                logger.LogInformation("When you're done customizing, type 'continue' (or 'c') and press Enter to proceed:");

                // Wait for user confirmation
                string? userInput;
                do
                {
                    Console.Write("> ");
                    userInput = Console.ReadLine()?.Trim().ToLowerInvariant();
                } while (userInput != "continue" && userInput != "c");

                logger.LogInformation("Continuing with publish process...");
                logger.LogInformation("");

                // Step 1: Create manifest.zip including the four required files
                var zipPath = Path.Combine(manifestDir, "manifest.zip");
                if (File.Exists(zipPath))
                {
                    try { File.Delete(zipPath); } catch { /* ignore */ }
                }

                // Identify up to 4 files (manifest.json + icons + any additional up to 4 total)
                var expectedFiles = new List<string>();
                string[] candidateNames = ["manifest.json", "color.png", "outline.png", "logo.png", "icon.png"];
                foreach (var name in candidateNames)
                {
                    var p = Path.Combine(manifestDir, name);
                    if (File.Exists(p)) expectedFiles.Add(p);
                    if (expectedFiles.Count == 4) break;
                }
                // If still fewer than 4, add any other files to reach 4 (non recursive)
                if (expectedFiles.Count < 4)
                {
                    foreach (var f in Directory.EnumerateFiles(manifestDir).Where(f => !expectedFiles.Contains(f)))
                    {
                        expectedFiles.Add(f);
                        if (expectedFiles.Count == 4) break;
                    }
                }

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

                // Acquire MOS token using native C# service
                var mosTokenService = new MosTokenService(
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<MosTokenService>());

                var mosToken = await mosTokenService.AcquireTokenAsync(mosEnv, mosPersonalToken);
                if (string.IsNullOrWhiteSpace(mosToken))
                {
                    logger.LogError("Unable to acquire MOS token. Aborting publish.");
                    return;
                }

                using var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mosToken);
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"Agent365Publish/{Assembly.GetExecutingAssembly().GetName().Version}");

                // Step 2: POST packages (multipart form) - using tenant-specific URL
                logger.LogInformation("Uploading package to Titles service...");
                var packagesUrl = $"{mosTitlesBaseUrl}/admin/v1/tenants/packages";
                using var form = new MultipartFormDataContent();
                await using (var zipFs = File.OpenRead(zipPath))
                {
                    var fileContent = new StreamContent(zipFs);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                    form.Add(fileContent, "package", Path.GetFileName(zipPath));

                    HttpResponseMessage uploadResp;
                    try
                    {
                        uploadResp = await http.PostAsync(packagesUrl, form);
                    }
                    catch (HttpRequestException ex)
                    {
                        logger.LogError("Network error during package upload: {Message}", ex.Message);
                        logger.LogInformation("The manifest package is available at: {ZipPath}", zipPath);
                        logger.LogInformation("You can manually upload it at: {Url}", packagesUrl);
                        logger.LogInformation("When network connectivity is restored, you can retry the publish command.");
                        return;
                    }
                    catch (TaskCanceledException ex)
                    {
                        logger.LogError("Upload request timed out: {Message}", ex.Message);
                        logger.LogInformation("The manifest package is available at: {ZipPath}", zipPath);
                        logger.LogInformation("You can manually upload it at: {Url}", packagesUrl);
                        logger.LogInformation("When network connectivity is restored, you can retry the publish command.");
                        return;
                    }

                    var uploadBody = await uploadResp.Content.ReadAsStringAsync();
                    logger.LogInformation("Titles upload HTTP {StatusCode}. Raw body length={Length} bytes", (int)uploadResp.StatusCode, uploadBody?.Length ?? 0);
                    if (!uploadResp.IsSuccessStatusCode)
                    {
                        logger.LogError("Package upload failed ({Status}). Body:\n{Body}", uploadResp.StatusCode, uploadBody);
                        return;
                    }

                    JsonDocument? uploadJson = null;
                    try
                    {
                        if (string.IsNullOrWhiteSpace(uploadBody))
                        {
                            logger.LogError("Upload response body is null or empty. Cannot parse JSON.");
                            return;
                        }
                        uploadJson = JsonDocument.Parse(uploadBody);
                    }
                    catch (Exception jex)
                    {
                        logger.LogError(jex, "Failed to parse upload response JSON. Body was:\n{Body}", uploadBody);
                        return;
                    }
                    // Extract operationId (required)
                    if (!uploadJson.RootElement.TryGetProperty("operationId", out var opIdEl))
                    {
                        var propertyNames = string.Join(
                            ", ",
                            uploadJson.RootElement.EnumerateObject().Select(p => p.Name));
                        logger.LogError("operationId missing in upload response. Present properties: [{Props}] Raw body:\n{Body}", propertyNames, uploadBody);
                        return;
                    }
                    var operationId = opIdEl.GetString();
                    if (string.IsNullOrWhiteSpace(operationId))
                    {
                        logger.LogError("operationId property empty/null. Raw body:\n{Body}", uploadBody);
                        return;
                    }
                    // Extract titleId only from titlePreview block
                    string? titleId = null;
                    if (uploadJson.RootElement.TryGetProperty("titlePreview", out var previewEl) &&
                        previewEl.ValueKind == JsonValueKind.Object &&
                        previewEl.TryGetProperty("titleId", out var previewTitleIdEl))
                    {
                        titleId = previewTitleIdEl.GetString();
                    }
                    if (string.IsNullOrWhiteSpace(titleId))
                    {
                        logger.LogError("titleId not found under titlePreview.titleId. Raw body:\n{Body}", uploadBody);
                        return;
                    }

                    logger.LogInformation("Upload succeeded. operationId={Op} titleId={Title}", operationId, titleId);

                    // POST titles with operationId - using tenant-specific URL
                    var titlesUrl = $"{mosTitlesBaseUrl}/admin/v1/tenants/packages/titles";
                    var titlePayload = JsonSerializer.Serialize(new { operationId });

                    HttpResponseMessage titlesResp;
                    try
                    {
                        using (var content = new StringContent(titlePayload, System.Text.Encoding.UTF8, "application/json"))
                        {
                            titlesResp = await http.PostAsync(titlesUrl, content);
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        logger.LogError("Network error during title creation: {Message}", ex.Message);
                        logger.LogInformation("Package was uploaded successfully (operationId={Op}), but title creation failed.", operationId);
                        return;
                    }
                    catch (TaskCanceledException ex)
                    {
                        logger.LogError("Title creation request timed out: {Message}", ex.Message);
                        logger.LogInformation("Package was uploaded successfully (operationId={Op}), but title creation failed.", operationId);
                        return;
                    }

                    var titlesBody = await titlesResp.Content.ReadAsStringAsync();
                    if (!titlesResp.IsSuccessStatusCode)
                    {
                        logger.LogError("Titles creation failed ({Status}). Payload sent={Payload}. Body:\n{Body}", titlesResp.StatusCode, titlePayload, titlesBody);
                        return;
                    }
                    logger.LogInformation("Title creation initiated. Response body length={Length} bytes", titlesBody?.Length ?? 0);

                    // Wait 10 seconds before allowing all users to ensure title is fully created
                    logger.LogInformation("Configuring title access for all users with retry and exponential backoff...");
                    var allowUrl = $"{mosTitlesBaseUrl}/admin/v1/tenants/titles/{titleId}/allowed";
                    var allowedPayload = JsonSerializer.Serialize(new
                    {
                        EntityCollection = new
                        {
                            ForAllUsers = true,
                            Entities = Array.Empty<object>()
                        }
                    });

                    // Use custom retry helper
                    var retryHelper = new RetryHelper(logger);

                    var allowResult = await retryHelper.ExecuteWithRetryAsync(
                        async ct =>
                        {
                            using var content = new StringContent(allowedPayload, System.Text.Encoding.UTF8, "application/json");
                            var resp = await http.PostAsync(allowUrl, content, ct);
                            var body = await resp.Content.ReadAsStringAsync(ct);
                            return (resp, body);
                        },
                        result =>
                        {
                            var (resp, body) = result;

                            if (resp.IsSuccessStatusCode)
                            {
                                return false;
                            }

                            if ((int)resp.StatusCode == 404 && body.Contains("Title Not Found", StringComparison.OrdinalIgnoreCase))
                            {
                                logger.LogWarning("Title not found yet (HTTP 404). Will retry...");
                                return true;
                            }

                            return false;
                        },
                        maxRetries: 5,
                        baseDelaySeconds: 10,
                        CancellationToken.None);

                    var (allowResp, allowBody) = allowResult;
                    if (!allowResp.IsSuccessStatusCode)
                    {
                        logger.LogError("Allow users failed ({Status}). URL={Url} Payload={Payload} Body:\n{Body}", allowResp.StatusCode, allowUrl, allowedPayload, allowBody);
                        return;
                    }
                    logger.LogInformation("Title access configured for all users. Allow response length={Length} bytes", allowBody?.Length ?? 0);
                    logger.LogDebug("Allow users response body:\n{Body}", allowBody);
                }

                // ================= Graph API Operations =================
                if (skipGraph)
                {
                    logger.LogInformation("--skip-graph specified; skipping federated identity credential and role assignment.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(tenantId))
                {
                    logger.LogWarning("tenantId unavailable; skipping Graph operations.");
                    return;
                }

                logger.LogInformation("Executing Graph API operations...");
                logger.LogInformation("TenantId: {TenantId}, BlueprintId: {BlueprintId}", tenantId, blueprintId);

                var graphSuccess = await graphApiService.ExecutePublishGraphStepsAsync(
                    tenantId,
                    blueprintId,
                    blueprintId, // Using blueprintId as manifestId
                    CancellationToken.None);

                if (!graphSuccess)
                {
                    logger.LogError("Graph API operations failed");
                    return;
                }

                logger.LogInformation("Publish completed successfully!");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Publish command failed: {Message}", ex.Message);
            }
        }, dryRunOption, skipGraphOption, mosEnvOption, mosPersonalTokenOption);

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

