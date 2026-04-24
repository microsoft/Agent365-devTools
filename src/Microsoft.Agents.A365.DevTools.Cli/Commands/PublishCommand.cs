// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;
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
        ManifestTemplateService manifestTemplateService,
        GraphApiService? graphApiService = null,
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("publish", "Update manifest IDs and create a package for upload to Microsoft 365 Admin Center");

        var agentNameOption = new Option<string?>(
            ["--agent-name", "-n"],
            description: "Agent base name. When provided, no config file is required.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection. Use with --agent-name.");

        var dryRunOption = new Option<bool>("--dry-run", "Show changes without writing files or creating the zip");

        var ownIdentityOption = new Option<bool?>(
            "--ownaccess",
            description: "true = own-identity agent: setup provisions blueprint and permissions only;\n" +
                        "      run 'a365 create-instance' separately to create the agent identity SP and Entra user.\n" +
                        "false = blueprint-only agent: setup auto-creates agent identity SP; no Entra user (default)\n" +
                        "Overrides the aiTeammate field in a365.config.json");

        var useBlueprintOption = new Option<bool>(
            "--use-blueprint",
            description: "Use the blueprint-based non-DW flow (calls Agent Instance Graph API, no manifest).\n" +
                        "Only meaningful with --ownaccess false");

        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(dryRunOption);
        command.AddOption(ownIdentityOption);
        command.AddOption(useBlueprintOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFile = new FileInfo("a365.config.json");
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var ownIdentityFlag = context.ParseResult.GetValueForOption(ownIdentityOption);
            var useBlueprintFlag = context.ParseResult.GetValueForOption(useBlueprintOption);
            var ct = context.GetCancellationToken();

            var isNormalExit = false;

            try
            {
                Agent365Config config;
                if (resolver != null)
                {
                    var resolved = await resolver.ResolveAsync(agentName, tenantIdFlag, configFile, isCleanupMode: false, ct);
                    if (resolved is null) { context.ExitCode = 1; return; }
                    config = resolved;
                }
                else
                {
                    config = await configService.LoadAsync(configFile.FullName);
                }

                // Effective agent type: CLI flag > config value > default (own-identity agent)
                var isBlueprintAgent =
                    ownIdentityFlag == false ||
                    (!ownIdentityFlag.HasValue && config.IsNonAiTeammate);

                if (isBlueprintAgent)
                {
                    var isBlueprint = useBlueprintFlag || (isBlueprintAgent && config.UseBlueprint == true);

                    if (dryRun)
                    {
                        if (isBlueprint)
                            PrintNonDwBlueprintDryRunPlan(config, logger);
                        else
                            PrintNonDwDryRunPlan(config, logger);
                        isNormalExit = true;
                        return;
                    }

                    if (isBlueprint)
                    {
                        isNormalExit = await PublishBlueprintNonDwAsync(config, graphApiService, configService, logger, context, ct: context.GetCancellationToken());
                        return;
                    }

                    // App-based non-DW Phase B not yet implemented — team feedback on dry-run output first.
                    logger.LogError(
                        "App-based non-DW publish (Phase B) is not yet implemented. " +
                        "Run with --dry-run to preview the manifest substitution plan.");
                    context.ExitCode = 1;
                    return;
                }

                // --- Own-identity agent (default) path ---
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

                if (string.IsNullOrWhiteSpace(displayName))
                    logger.LogWarning("  name.short           - not set; edit manifest.json to provide a short name (30 chars max) before packaging");
                else if (displayName.Length > 30)
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
                    if (Console.ReadLine() is null)
                        throw new OperationCanceledException();
                    Console.WriteLine();
                }

                var zipPath = Path.Combine(manifestDir, "manifest.zip");

                if (!await manifestTemplateService.CreateManifestZipAsync(manifestDir, zipPath))
                {
                    logger.LogError("Failed to create manifest package in {Dir}", manifestDir);
                    return;
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

    /// <summary>
    /// Registers the agent instance via POST /beta/agentRegistry/agentInstances and saves
    /// the returned instance ID to the generated config. Returns true on success.
    /// </summary>
    private static async Task<bool> PublishBlueprintNonDwAsync(
        Agent365Config config,
        GraphApiService? graphApiService,
        IConfigService configService,
        ILogger logger,
        System.CommandLine.Invocation.InvocationContext context,
        CancellationToken ct)
    {
        if (graphApiService == null)
        {
            logger.LogError("GraphApiService is not available. This is a configuration error.");
            context.ExitCode = 1;
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.TenantId))
        {
            logger.LogError("tenantId is required for blueprint non-DW publish. Set it in a365.config.json.");
            context.ExitCode = 1;
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.AgentIdentityDisplayName))
        {
            logger.LogError("agentIdentityDisplayName is required. Set it in a365.config.json.");
            context.ExitCode = 1;
            return false;
        }

        logger.LogInformation("Registering agent instance...");
        logger.LogInformation("  POST /beta/agentRegistry/agentInstances");
        logger.LogInformation("  displayName            : {DisplayName}", config.AgentIdentityDisplayName);
        if (!string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            logger.LogInformation("  agentIdentityBlueprintId: {BlueprintId}", config.AgentBlueprintId);

        var instanceId = await graphApiService.RegisterAgentInstanceAsync(
            config.TenantId,
            config.AgentIdentityDisplayName,
            config.AgentBlueprintId,
            ct);

        if (string.IsNullOrWhiteSpace(instanceId))
        {
            logger.LogError("Agent instance registration failed.");
            context.ExitCode = 1;
            return false;
        }

        logger.LogInformation("Agent instance registered: {InstanceId}", instanceId);

        config.AgentInstanceId = instanceId;
        await configService.SaveStateAsync(config);
        logger.LogInformation("Saved agentInstanceId to generated config.");

        return true;
    }

    private static void PrintNonDwBlueprintDryRunPlan(Models.Agent365Config config, ILogger logger)
    {
        var blueprintId = !string.IsNullOrWhiteSpace(config.AgentBlueprintId)
            ? config.AgentBlueprintId
            : "<agentBlueprintId — run setup first>";

        logger.LogInformation("Non-DW Blueprint Publish Plan (dry run — no API calls will be made)");
        logger.LogInformation("");
        logger.LogInformation("  Agent Instance Registration");
        logger.LogInformation("    Call Agent Instance Graph API");
        logger.LogInformation("    Blueprint ID                 {BlueprintId}", blueprintId);
        logger.LogInformation("    Tenant                       {TenantId}", config.TenantId);
        logger.LogInformation("");
        logger.LogInformation("  No manifest or zip created for blueprint-based agents.");
        logger.LogInformation("");
        logger.LogInformation("Run without --dry-run to register the agent instance.");
    }

    private static void PrintNonDwDryRunPlan(Models.Agent365Config config, ILogger logger)
    {
        var clientAppId = !string.IsNullOrWhiteSpace(config.ClientAppId)
            ? config.ClientAppId
            : "<clientAppId — run setup first>";

        var webAppDomain = "<host>.azurewebsites.net";

        logger.LogInformation("Non-DW Publish Plan (dry run — no files will be written)");
        logger.LogInformation("");
        logger.LogInformation("  Source of truth   : ClientAppId = {ClientAppId}", clientAppId);
        logger.LogInformation("");
        logger.LogInformation("  Fields to substitute:");
        logger.LogInformation("    id                                     -> {ClientAppId}", clientAppId);
        logger.LogInformation("    bots[0].botId                          -> {ClientAppId}", clientAppId);
        logger.LogInformation("    copilotAgents.customEngineAgents[0].id -> {ClientAppId}", clientAppId);
        logger.LogInformation("    validDomains[1]                        -> {Domain}", webAppDomain);
        logger.LogInformation("    webApplicationInfo.id                  -> {ClientAppId}", clientAppId);
        logger.LogInformation("    webApplicationInfo.resource            -> api://botid-{ClientAppId}", clientAppId);
        logger.LogInformation("");
        logger.LogInformation("  Zip contents:");
        logger.LogInformation("    manifest.json");
        logger.LogInformation("    color.png");
        logger.LogInformation("    outline.png");
        logger.LogInformation("");
        logger.LogInformation("Run without --dry-run to write the manifest files and create the zip.");
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
