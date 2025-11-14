// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO.Compression;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Multi-platform service for application deployment to Azure App Service
/// Supports .NET, Node.js, and Python applications
/// </summary>
public class DeploymentService
{
    private readonly ILogger<DeploymentService> _logger;
    private readonly CommandExecutor _executor;
    private readonly PlatformDetector _platformDetector;
    private readonly Dictionary<ProjectPlatform, IPlatformBuilder> _builders;

    public DeploymentService(
        ILogger<DeploymentService> logger, 
        CommandExecutor executor,
        PlatformDetector platformDetector,
        ILogger<DotNetBuilder> dotnetLogger,
        ILogger<NodeBuilder> nodeLogger,
        ILogger<PythonBuilder> pythonLogger)
    {
        _logger = logger;
        _executor = executor;
        _platformDetector = platformDetector;
        
        // Initialize platform builders
        _builders = new Dictionary<ProjectPlatform, IPlatformBuilder>
        {
            { ProjectPlatform.DotNet, new DotNetBuilder(dotnetLogger, executor) },
            { ProjectPlatform.NodeJs, new NodeBuilder(nodeLogger, executor) },
            { ProjectPlatform.Python, new PythonBuilder(pythonLogger, executor) }
        };
    }

    /// <summary>
    /// Deploy application to Azure App Service with automatic platform detection
    /// Supports .NET, Node.js, and Python platforms
    /// </summary>
    public async Task<bool> DeployAsync(DeploymentConfiguration config, bool verbose, bool inspect = false, bool restart = false)
    {
        if (restart)
        {
            _logger.LogInformation("Starting deployment from existing publish folder (--restart mode)...");
        }
        else
        {
            _logger.LogInformation("Starting multi-platform deployment...");
        }

        // Resolve and validate project directory
        var projectDir = Path.GetFullPath(config.ProjectPath);
        if (!Directory.Exists(projectDir))
        {
            throw new DirectoryNotFoundException($"Project directory not found: {projectDir}");
        }

        // Determine publish path
        var publishPath = Path.Combine(projectDir, config.PublishOutputPath);

        if (restart)
        {
            // Validate publish folder exists
            if (!Directory.Exists(publishPath))
            {
                throw new DirectoryNotFoundException(
                    $"Publish folder not found: {publishPath}. " +
                    $"Cannot use --restart without an existing publish folder. " +
                    $"Run 'a365 deploy' without --restart first to build the project.");
            }

            _logger.LogInformation("Using existing publish folder: {PublishPath}", publishPath);
            _logger.LogInformation("Skipping build steps (platform detection, environment validation, build, manifest creation)");
            _logger.LogInformation("");
        }
        else
        {
            // 1. Detect platform
            var platform = config.Platform ?? _platformDetector.Detect(projectDir);
            if (platform == ProjectPlatform.Unknown)
            {
                throw new NotSupportedException($"Could not detect project platform in {projectDir}. " +
                    "Ensure the directory contains .NET project files (.csproj), Node.js files (package.json), " +
                    "or Python files (requirements.txt, *.py).");
            }

            _logger.LogInformation("Detected platform: {Platform}", platform);

            // 2. Get appropriate builder
            if (!_builders.TryGetValue(platform, out var builder))
            {
                throw new NotSupportedException($"Platform {platform} is not yet supported for deployment");
            }

            // 3. Validate environment
            _logger.LogInformation("[1/7] Validating {Platform} environment...", platform);
            if (!await builder.ValidateEnvironmentAsync())
            {
                throw new Exception($"Environment validation failed for {platform}");
            }

            // 4. Build application (BuildAsync will handle cleaning the publish directory)
            _logger.LogInformation("[2/7] Building {Platform} application...", platform);
            publishPath = await builder.BuildAsync(projectDir, config.PublishOutputPath, verbose);
            _logger.LogInformation("Build output: {Path}", publishPath);

            // 5. Create Oryx manifest
            _logger.LogInformation("[3/7] Creating Oryx manifest...");
            var manifest = await builder.CreateManifestAsync(projectDir, publishPath);
            var manifestPath = Path.Combine(publishPath, "oryx-manifest.toml");
            await manifest.WriteToFileAsync(manifestPath);
            _logger.LogInformation("Manifest command: {Command}", manifest.Command);

            // 6. Convert .env to Azure App Settings (for Python projects)
            if (platform == ProjectPlatform.Python && builder is PythonBuilder pythonBuilder)
            {
                _logger.LogInformation("[4/7] Converting .env to Azure App Settings...");
                var envResult = await pythonBuilder.ConvertEnvToAzureAppSettingsAsync(projectDir, config.ResourceGroup, config.AppName, verbose);
                if (!envResult)
                {
                    _logger.LogWarning("Failed to convert environment variables, but continuing with deployment");
                }

                // Set startup command for Python apps
                _logger.LogInformation("[6/7] Setting Python startup command...");
                var startupResult = await pythonBuilder.SetStartupCommandAsync(projectDir, config.ResourceGroup, config.AppName, verbose);
                if (!startupResult)
                {
                    _logger.LogWarning("Failed to set startup command, but continuing with deployment");
                }

                // Add delay to allow Azure configuration to stabilize before deployment
                // This prevents "SCM container restart" conflicts
                _logger.LogInformation("Waiting for Azure configuration to stabilize...");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            await builder.CleanAsync(publishPath);
        }

        // 6. Create deployment ZIP
        var zipPath = await CreateDeploymentPackageAsync(projectDir, publishPath, config.DeploymentZip);

        // 6.5. Optional inspection pause (only if --inspect flag is used)
        if (inspect)
        {
            await OfferPublishInspectionAsync(publishPath, zipPath);
        }

        // 7. Deploy to Azure
        await DeployToAzureAsync(config, projectDir, zipPath);
        
        return true;
    }

    /// <summary>
    /// Deploy the ZIP package to Azure Web App
    /// </summary>
    private async Task DeployToAzureAsync(DeploymentConfiguration config, string projectDir, string zipPath)
    {
        _logger.LogInformation("[7/7] Deploying to Azure Web App...");
        _logger.LogInformation("  Resource Group: {ResourceGroup}", config.ResourceGroup);
        _logger.LogInformation("  App Name: {AppName}", config.AppName);
        _logger.LogInformation("");
        _logger.LogInformation("Deployment typically takes 2-5 minutes to complete");
        _logger.LogDebug("Using async deployment to avoid Azure SCM gateway timeout (4-5 minute limit)");
        _logger.LogInformation("Monitor progress: https://{AppName}.scm.azurewebsites.net/api/deployments/latest", config.AppName);
        _logger.LogInformation("");
        
        // Use async deployment to avoid Azure SCM gateway timeout
        // Azure App Service SCM/Kudu has a hard-coded 4-5 minute gateway timeout
        // The --async flag uploads the package and returns immediately while deployment continues in background
        // This prevents 504 Gateway Timeout errors for long-running Python/Oryx builds
        var deployArgs = $"webapp deploy --resource-group {config.ResourceGroup} --name {config.AppName} --src-path \"{zipPath}\" --type zip --async true";
        _logger.LogInformation("Uploading deployment package...");
        
        var deployResult = await _executor.ExecuteWithStreamingAsync("az", deployArgs, projectDir, "[Azure] ");
        
        if (!deployResult.Success)
        {
            _logger.LogError("Deployment upload failed with exit code {ExitCode}", deployResult.ExitCode);
            if (!string.IsNullOrWhiteSpace(deployResult.StandardError))
            {
                _logger.LogError("Deployment error: {Error}", deployResult.StandardError);
            }
            throw new Exception($"Azure deployment failed: {deployResult.StandardError}");
        }

        _logger.LogInformation("");
        _logger.LogInformation("Deployment package uploaded successfully!");
        _logger.LogInformation("");
        _logger.LogInformation("Deployment is continuing in the background on Azure");
        _logger.LogInformation("Application will be available in 2-5 minutes");
        _logger.LogInformation("");
        _logger.LogInformation("Monitor deployment status:");
        _logger.LogInformation("  • Web: https://{AppName}.scm.azurewebsites.net/api/deployments/latest", config.AppName);
        _logger.LogInformation("  • CLI: az webapp log tail --name {AppName} --resource-group {ResourceGroup}", config.AppName, config.ResourceGroup);
        _logger.LogInformation("");
    }

    /// <summary>
    /// Creates a deployment package (ZIP file) from the publish directory
    /// Uses fast compression and includes detailed logging and error handling
    /// </summary>
    private async Task<string> CreateDeploymentPackageAsync(string projectDir, string publishPath, string deploymentZipName)
    {
        var zipPath = Path.Combine(projectDir, deploymentZipName);
        _logger.LogInformation("[6/7] Creating deployment package: {ZipPath}", zipPath);

        // Delete old zip if exists with retry logic
        if (File.Exists(zipPath))
        {
            _logger.LogDebug("Removing existing deployment package...");
            try
            {
                File.Delete(zipPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not delete existing package, using timestamped name: {Error}", ex.Message);
                var directory = Path.GetDirectoryName(zipPath) ?? projectDir;
                var nameWithoutExt = Path.GetFileNameWithoutExtension(zipPath);
                var extension = Path.GetExtension(zipPath);
                zipPath = Path.Combine(directory, $"{nameWithoutExt}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}");
                _logger.LogInformation("Using alternative filename: {ZipPath}", zipPath);
            }
        }

        // Count files before compression
        var allFiles = Directory.GetFiles(publishPath, "*", SearchOption.AllDirectories);
        _logger.LogInformation("Compressing {FileCount} files from {PublishPath}...", allFiles.Length, publishPath);

        // Use faster compression and add timing
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ZipFile.CreateFromDirectory(publishPath, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
        stopwatch.Stop();

        var zipInfo = new FileInfo(zipPath);
        _logger.LogInformation("Package created in {ElapsedSeconds:F1}s - Size: {SizeMB:F2} MB",
            stopwatch.Elapsed.TotalSeconds, Math.Round(zipInfo.Length / 1024.0 / 1024.0, 2));

        await Task.CompletedTask;
        return zipPath;
    }

    /// <summary>
    /// Offers user option to inspect publish folder and ZIP contents before deployment.
    /// Only called when --inspect flag is used in deploy command.
    /// </summary>
    private async Task OfferPublishInspectionAsync(string publishPath, string zipPath)
    {
        _logger.LogInformation("");
        _logger.LogInformation("=== DEPLOYMENT PACKAGE READY ===");
        _logger.LogInformation("Publish folder: {PublishPath}", publishPath);
        _logger.LogInformation("Deployment ZIP: {ZipPath}", zipPath);

        var zipInfo = new FileInfo(zipPath);
        _logger.LogInformation("ZIP size: {SizeMB:F2} MB", Math.Round(zipInfo.Length / 1024.0 / 1024.0, 2));

        _logger.LogInformation("");
        _logger.LogInformation("Key files to verify:");
        _logger.LogInformation("  - .deployment (should contain: SCM_DO_BUILD_DURING_DEPLOYMENT=true)");
        _logger.LogInformation("  - requirements.txt (should have: --find-links=dist)");
        _logger.LogInformation("  - dist/*.whl (local Microsoft Agent 365 packages)");
        _logger.LogInformation("");

        Console.Write("Proceed with deployment? [Y/n]: ");
        var response = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (response == "n" || response == "no")
        {
            _logger.LogInformation("Deployment cancelled by user");
            Environment.Exit(0);
        }

        _logger.LogInformation("Continuing with deployment...");
        _logger.LogInformation("");

        await Task.CompletedTask;
    }
}

