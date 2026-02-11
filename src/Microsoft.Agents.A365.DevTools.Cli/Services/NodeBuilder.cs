// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Node.js platform builder
/// </summary>
public class NodeBuilder : IPlatformBuilder
{
    private readonly ILogger<NodeBuilder> _logger;
    private readonly CommandExecutor _executor;
    private readonly BuilderHelper _helper;

    public NodeBuilder(ILogger<NodeBuilder> logger, CommandExecutor executor)
    {
        _logger = logger;
        _executor = executor;
        _helper = new BuilderHelper(logger, executor);
    }

    public async Task<bool> ValidateEnvironmentAsync()
    {
        _logger.LogInformation("Validating Node.js environment...");
        
        var nodeResult = await _executor.ExecuteAsync("node", "--version", captureOutput: true);
        if (!nodeResult.Success)
        {
            _logger.LogError("Node.js not found. Please install Node.js from https://nodejs.org/");
            return false;
        }

        var npmResult = await _executor.ExecuteAsync("npm", "--version", captureOutput: true);
        if (!npmResult.Success)
        {
            _logger.LogError("npm not found. Please install Node.js which includes npm.");
            return false;
        }

        _logger.LogInformation("Node.js version: {Version}", nodeResult.StandardOutput.Trim());
        _logger.LogInformation("npm version: {Version}", npmResult.StandardOutput.Trim());
        return true;
    }

    public async Task CleanAsync(string projectDir)
    {
        _logger.LogInformation("Cleaning Node.js project...");

        // Remove node_modules if it exists
        var nodeModulesPath = Path.Combine(projectDir, "node_modules");
        if (Directory.Exists(nodeModulesPath))
        {
            _logger.LogInformation("Removing node_modules directory...");
            Directory.Delete(nodeModulesPath, recursive: true);
        }

        await Task.CompletedTask;
    }

    public async Task<string> BuildAsync(string projectDir, string outputPath, bool verbose)
    {
        _logger.LogInformation("Building Node.js project...");

        // Clean up old publish directory for fresh start
        var publishPath = Path.Combine(projectDir, outputPath);
        if (Directory.Exists(publishPath))
        {
            _logger.LogInformation("Removing old publish directory...");
            Directory.Delete(publishPath, recursive: true);
        }

        var packageJsonPath = Path.Combine(projectDir, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            throw new NodeProjectNotFoundException(projectDir);
        }

        // Install dependencies
        _logger.LogInformation("Installing dependencies...");
        var installResult = await _helper.ExecuteWithOutputAsync("npm", "ci", projectDir, verbose);
        if (!installResult.Success)
        {
            _logger.LogWarning("npm ci failed, trying npm install...");
            installResult = await _helper.ExecuteWithOutputAsync("npm", "install", projectDir, verbose);
            if (!installResult.Success)
            {
                throw new NodeDependencyInstallException(projectDir, installResult.StandardError);
            }
        }

        // Check if build script exists
        var packageJson = await File.ReadAllTextAsync(packageJsonPath);
        var hasBuildScript = packageJson.Contains("\"build\":");

        if (hasBuildScript)
        {
            _logger.LogInformation("Running build script...");
            var buildResult = await _helper.ExecuteWithOutputAsync("npm", "run build", projectDir, verbose);
            if (!buildResult.Success)
            {
                throw new NodeBuildFailedException(projectDir, buildResult.StandardError);
            }
        }
        else
        {
            _logger.LogInformation("No build script found, skipping build step");
        }

        Directory.CreateDirectory(publishPath);

        // Copy necessary files to publish directory
        _logger.LogInformation("Preparing deployment package...");

        // Copy package.json and package-lock.json
        File.Copy(packageJsonPath, Path.Combine(publishPath, "package.json"));
        var packageLockPath = Path.Combine(projectDir, "package-lock.json");
        if (File.Exists(packageLockPath))
        {
            File.Copy(packageLockPath, Path.Combine(publishPath, "package-lock.json"));
        }

        // Copy ts build config
        var tsConfigPath = Path.Combine(projectDir, "tsconfig.json");
        if (File.Exists(tsConfigPath))
        {
            File.Copy(tsConfigPath, Path.Combine(publishPath, "tsconfig.json"));
        }

        // Copy ToolingManifest if exists
        var toolingManifestPath = Path.Combine(projectDir, "ToolingManifest.json");
        if (File.Exists(toolingManifestPath))
        {
            File.Copy(toolingManifestPath, Path.Combine(publishPath, "ToolingManifest.json"));
        }

        // Copy source files (src, lib, etc.)
        var srcDir = Path.Combine(projectDir, "src");
        if (Directory.Exists(srcDir))
        {
            CopyDirectory(srcDir, Path.Combine(publishPath, "src"));
        }

        // Copy server/entry files (.js/.ts files in root)
        foreach (var jsFile in Directory.GetFiles(projectDir, "*.js"))
        {
            File.Copy(jsFile, Path.Combine(publishPath, Path.GetFileName(jsFile)));
        }
        foreach (var tsFile in Directory.GetFiles(projectDir, "*.ts"))
        {
            File.Copy(tsFile, Path.Combine(publishPath, Path.GetFileName(tsFile)));
        }

        var distDir = Path.Combine(projectDir, "dist");
        if (Directory.Exists(distDir))
        {
            _logger.LogInformation("Found dist folder, copying to publish output...");
            CopyDirectory(distDir, Path.Combine(publishPath, "dist"));
        }
        else
        {
            _logger.LogInformation("No dist folder found in project; relying on Oryx build (if configured) to produce runtime output.");
        }

        // Create .deployment file to force Oryx build during Azure deployment
        await CreateDeploymentFile(publishPath);

        return publishPath;
    }

    public async Task<OryxManifest> CreateManifestAsync(string projectDir, string publishPath)
    {
        _logger.LogInformation("Creating Oryx manifest for Node.js...");
        
        var packageJsonPath = Path.Combine(projectDir, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            throw new NodeProjectNotFoundException(projectDir);
        }

        var packageJson = await File.ReadAllTextAsync(packageJsonPath);
        
        // Parse package.json to detect start command and version
        using var doc = JsonDocument.Parse(packageJson);
        var root = doc.RootElement;

        // Detect Node version
        var nodeVersion = "20"; // Default
        if (root.TryGetProperty("engines", out var engines) && 
            engines.TryGetProperty("node", out var nodeVersionProp))
        {
            var versionString = nodeVersionProp.GetString() ?? "20";
            var match = System.Text.RegularExpressions.Regex.Match(versionString, @"(\d+)");
            if (match.Success)
            {
                nodeVersion = match.Groups[1].Value;
            }
        }

        // Detect start command
        var startCommand = "node server.js"; // Default
        
        if (root.TryGetProperty("scripts", out var scripts) && 
            scripts.TryGetProperty("start", out var startScript))
        {
            var scriptValue = startScript.GetString();
            if (!string.IsNullOrWhiteSpace(scriptValue))
            {
                // Use the script literally, same as package.json
                startCommand = scriptValue;
            }

            _logger.LogInformation("Detected start command from package.json: {Command}", startCommand);
        }
        else if (root.TryGetProperty("main", out var mainProp))
        {
            var mainFile = mainProp.GetString() ?? "server.js";
            startCommand = $"node {mainFile}";
            _logger.LogInformation("Detected start command from main property: {Command}", startCommand);
        }
        else
        {
            // Look for common entry point files under publish path
            var commonEntryPoints = new[] { "server.js", "app.js", "index.js", "main.js" };
            foreach (var entryPoint in commonEntryPoints)
            {
                if (File.Exists(Path.Combine(publishPath, entryPoint)))
                {
                    startCommand = $"node {entryPoint}";
                    _logger.LogInformation("Detected entry point in publish folder: {Command}", startCommand);
                    break;
                }
            }
        }

        // Detect build command – strictly Node
        string buildCommand = "";
        bool buildRequired = false;

        if (root.TryGetProperty("scripts", out var scriptsForBuild) &&
            scriptsForBuild.TryGetProperty("build", out var buildScript))
        {
            var buildValue = buildScript.GetString();
            if (!string.IsNullOrWhiteSpace(buildValue))
            {
                // We always call through npm so it picks up the script from package.json
                buildCommand = "npm run build";
                buildRequired = true;
                _logger.LogInformation("Detected build script; using Oryx build command: {Command}", buildCommand);
            }
        }
        else
        {
            _logger.LogInformation("No build script found; Oryx will only run npm install.");
        }
        
        return new OryxManifest
        {
            Platform = "nodejs",
            Version = nodeVersion,
            Command = startCommand,
            BuildCommand = buildCommand,
            BuildRequired = buildRequired
        };
    }
    
    public async Task<bool> ConvertEnvToAzureAppSettingsAsync(string projectDir, string resourceGroup, string webAppName, bool verbose)
    {
        return await _helper.ConvertEnvToAzureAppSettingsIfExistsAsync(projectDir, resourceGroup, webAppName, verbose);
    }

    private async Task CreateDeploymentFile(string publishPath)
    {
        var deploymentPath = Path.Combine(publishPath, ".deployment");
        var content = "[config]\nSCM_DO_BUILD_DURING_DEPLOYMENT=true\n";

        await File.WriteAllTextAsync(deploymentPath, content);
        _logger.LogInformation("Created .deployment file to force Oryx build");
    }

    private void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSubDir);
        }
    }
}
