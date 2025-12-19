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
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Identity.Client;

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
        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            description: "Enable verbose logging");
        
        command.AddOption(dryRunOption);
        command.AddOption(skipGraphOption);
        command.AddOption(mosEnvOption);
        command.AddOption(mosPersonalTokenOption);
        command.AddOption(verboseOption);

        command.SetHandler(async (bool dryRun, bool skipGraph, string mosEnv, string? mosPersonalToken, bool verbose) =>
        {
            try
            {
                // Load configuration using ConfigService
                var config = await configService.LoadAsync();
                logger.LogDebug("Configuration loaded successfully");

                // Extract required values from config
                var tenantId = config.TenantId;
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
                
                // Ask if user wants to open the file now
                Console.Write("Open manifest in your default editor now? (Y/n): ");
                var openResponse = Console.ReadLine()?.Trim().ToLowerInvariant();
                
                if (openResponse != "n" && openResponse != "no")
                {
                    FileHelper.TryOpenFileInDefaultEditor(manifestPath, logger);
                }
                
                Console.Write("Press Enter when you have finished editing the manifest to continue with publish: ");
                Console.Out.Flush();
                Console.ReadLine();

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

                // Ensure MOS prerequisites are configured (service principals + permissions)
                try
                {
                    logger.LogInformation("");
                    logger.LogDebug("Checking MOS prerequisites (service principals and permissions)...");
                    var mosPrereqsConfigured = await PublishHelpers.EnsureMosPrerequisitesAsync(
                        graphApiService, config, logger);
                    
                    if (!mosPrereqsConfigured)
                    {
                        logger.LogError("Failed to configure MOS prerequisites. Aborting publish.");
                        return;
                    }
                    logger.LogInformation("");
                }
                catch (SetupValidationException ex)
                {
                    logger.LogError("MOS prerequisites configuration failed: {Message}", ex.Message);
                    logger.LogInformation("");
                    logger.LogInformation("To manually create MOS service principals, run:");
                    logger.LogInformation("  az ad sp create --id 6ec511af-06dc-4fe2-b493-63a37bc397b1");
                    logger.LogInformation("  az ad sp create --id 8578e004-a5c6-46e7-913e-12f58912df43");
                    logger.LogInformation("  az ad sp create --id e8be65d6-d430-4289-a665-51bf2a194bda");
                    logger.LogInformation("");
                    return;
                }

                // Acquire MOS token using native C# service
                logger.LogDebug("Acquiring MOS authentication token for environment: {Environment}", mosEnv);
                var cleanLoggerFactory = LoggerFactoryHelper.CreateCleanLoggerFactory();
                var mosTokenService = new MosTokenService(
                    cleanLoggerFactory.CreateLogger<MosTokenService>(),
                    configService);

                string? mosToken = null;
                try
                {
                    mosToken = await mosTokenService.AcquireTokenAsync(mosEnv, mosPersonalToken);
                    logger.LogDebug("MOS token acquired successfully");
                }
                catch (MsalServiceException ex) when (ex.ErrorCode == "invalid_client" && 
                    ex.Message.Contains("AADSTS650052"))
                {
                    logger.LogError("MOS token acquisition failed: Missing service principal or admin consent (Error: {ErrorCode})", ex.ErrorCode);
                    logger.LogInformation("");
                    logger.LogInformation("The MOS service principals exist, but admin consent may not be granted.");
                    logger.LogInformation("Grant admin consent at:");
                    logger.LogInformation("  {PortalUrl}",
                        MosConstants.GetApiPermissionsPortalUrl(config.ClientAppId));
                    logger.LogInformation("");
                    logger.LogInformation("Or authenticate interactively and consent when prompted.");
                    logger.LogInformation("");
                    return;
                }
                catch (MsalServiceException ex) when (ex.ErrorCode == "unauthorized_client" && 
                    ex.Message.Contains("AADSTS50194"))
                {
                    logger.LogError("MOS token acquisition failed: Single-tenant app cannot use /common endpoint (Error: {ErrorCode})", ex.ErrorCode);
                    logger.LogInformation("");
                    logger.LogInformation("AADSTS50194: The application is configured as single-tenant but is trying to use the /common authority.");
                    logger.LogInformation("This should be automatically handled by using tenant-specific authority URLs.");
                    logger.LogInformation("");
                    logger.LogInformation("If this error persists:");
                    logger.LogInformation("1. Verify your app registration is configured correctly in Azure Portal");
                    logger.LogInformation("2. Check that tenantId in a365.config.json matches your app's home tenant");
                    logger.LogInformation("3. Ensure the app's 'Supported account types' setting matches your use case");
                    logger.LogInformation("");
                    return;
                }
                catch (MsalServiceException ex) when (ex.ErrorCode == "invalid_grant")
                {
                    logger.LogError("MOS token acquisition failed: Invalid or expired credentials (Error: {ErrorCode})", ex.ErrorCode);
                    logger.LogInformation("");
                    logger.LogInformation("The authentication failed due to invalid credentials or expired tokens.");
                    logger.LogInformation("Try clearing the token cache and re-authenticating:");
                    logger.LogInformation("  - Delete: ~/.a365/mos-token-cache.json");
                    logger.LogInformation("  - Run: a365 publish");
                    logger.LogInformation("");
                    return;
                }
                catch (MsalServiceException ex)
                {
                    // Log all MSAL-specific errors with full context for debugging
                    logger.LogError("MOS token acquisition failed with MSAL error");
                    logger.LogError("Error Code: {ErrorCode}", ex.ErrorCode);
                    logger.LogError("Error Message: {Message}", ex.Message);
                    logger.LogDebug("Stack Trace: {StackTrace}", ex.StackTrace);
                    
                    logger.LogInformation("");
                    logger.LogInformation("Authentication failed. Common issues:");
                    logger.LogInformation("1. Missing admin consent - Grant at:");
                    logger.LogInformation("   {PortalUrl}",
                        MosConstants.GetApiPermissionsPortalUrl(config.ClientAppId));
                    logger.LogInformation("2. Insufficient permissions - Verify required API permissions are configured");
                    logger.LogInformation("3. Tenant configuration - Ensure app registration matches your tenant setup");
                    logger.LogInformation("");
                    logger.LogInformation("For detailed troubleshooting, search for error code: {ErrorCode}", ex.ErrorCode);
                    logger.LogInformation("");
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to acquire MOS token: {Message}", ex.Message);
                    return;
                }

                if (string.IsNullOrWhiteSpace(mosToken))
                {
                    logger.LogError("Unable to acquire MOS token. Aborting publish.");
                    return;
                }

                using var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mosToken);
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"Agent365Publish/{Assembly.GetExecutingAssembly().GetName().Version}");

                // Log token info for debugging (first/last chars only for security)
                if (mosToken.Length >= 20)
                {
                    var prefixLen = Math.Min(10, mosToken.Length / 2);
                    var suffixLen = Math.Min(10, mosToken.Length / 2);
                    logger.LogDebug("Using MOS token: {TokenStart}...{TokenEnd} (length: {Length})", 
                        mosToken[..prefixLen], mosToken[^suffixLen..], mosToken.Length);
                }

                // Step 2: POST packages (multipart form) - using tenant-specific URL
                logger.LogInformation("Uploading package to Titles service...");
                var packagesUrl = $"{mosTitlesBaseUrl}/admin/v1/tenants/packages";
                logger.LogDebug("Upload URL: {Url}", packagesUrl);
                logger.LogDebug("Package file: {ZipPath}", zipPath);
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
                        
                        // Log response headers for additional diagnostic info
                        logger.LogDebug("Response headers:");
                        foreach (var header in uploadResp.Headers)
                        {
                            logger.LogDebug("  {HeaderName}: {HeaderValue}", header.Key, string.Join(", ", header.Value));
                        }
                        foreach (var header in uploadResp.Content.Headers)
                        {
                            logger.LogDebug("  {HeaderName}: {HeaderValue}", header.Key, string.Join(", ", header.Value));
                        }
                        
                        // Provide helpful troubleshooting info for 401
                        if (uploadResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        {
                            logger.LogError("");
                            logger.LogError("TROUBLESHOOTING 401 UNAUTHORIZED:");
                            logger.LogError("1. Verify MOS API permissions are configured correctly");
                            logger.LogError("   - Required permission: Title.ReadWrite.All");
                            logger.LogError("   - Admin consent must be granted");
                            logger.LogError("2. Check that the token contains the correct scopes");
                            logger.LogError("   - Run 'a365 publish -v' to see token scopes in debug logs");
                            logger.LogError("3. Ensure you're signed in with the correct account");
                            logger.LogError("   - Run 'az account show' to verify current account");
                            logger.LogError("4. Try clearing the MOS token cache and re-authenticating:");
                            logger.LogError("   - Delete: .mos-token-cache.json");
                            logger.LogError("   - Run: a365 publish");
                            logger.LogError("");
                        }
                        
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

                    logger.LogDebug("Proceeding to title creation step...");

                    // POST titles with operationId - using tenant-specific URL
                    var titlesUrl = $"{mosTitlesBaseUrl}/admin/v1/tenants/packages/titles";
                    logger.LogDebug("Title creation URL: {Url}", titlesUrl);
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

                logger.LogDebug("Configuring Graph API permissions (federated identity and role assignments)...");
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
        }, dryRunOption, skipGraphOption, mosEnvOption, mosPersonalTokenOption, verboseOption);

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

