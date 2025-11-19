// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Node.js platform builder
/// </summary>
public class NodeBuilder : IPlatformBuilder
{
    private readonly ILogger<NodeBuilder> _logger;
    private readonly CommandExecutor _executor;

    public NodeBuilder(ILogger<NodeBuilder> logger, CommandExecutor executor)
    {
        _logger = logger;
        _executor = executor;
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
        
        var packageJsonPath = Path.Combine(projectDir, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            throw new FileNotFoundException("package.json not found in project directory");
        }

        // Install dependencies
        _logger.LogInformation("Installing dependencies...");
        var installResult = await ExecuteWithOutputAsync("npm", "ci", projectDir, verbose);
        if (!installResult.Success)
        {
            _logger.LogWarning("npm ci failed, trying npm install...");
            installResult = await ExecuteWithOutputAsync("npm", "install", projectDir, verbose);
            if (!installResult.Success)
            {
                throw new Exception($"npm install failed: {installResult.StandardError}");
            }
        }

        // Check if build script exists
        var packageJson = await File.ReadAllTextAsync(packageJsonPath);
        var hasBuildScript = packageJson.Contains("\"build\":");

        if (hasBuildScript)
        {
            _logger.LogInformation("Running build script...");
            var buildResult = await ExecuteWithOutputAsync("npm", "run build", projectDir, verbose);
            if (!buildResult.Success)
            {
                throw new Exception($"npm run build failed: {buildResult.StandardError}");
            }
        }
        else
        {
            _logger.LogInformation("No build script found, skipping build step");
        }

        // Prepare publish directory
        var publishPath = Path.Combine(projectDir, outputPath);
        if (Directory.Exists(publishPath))
        {
            Directory.Delete(publishPath, recursive: true);
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

        // Copy server files (.js files in root)
        foreach (var jsFile in Directory.GetFiles(projectDir, "*.js"))
        {
            File.Copy(jsFile, Path.Combine(publishPath, Path.GetFileName(jsFile)));
        }
        foreach (var tsFile in Directory.GetFiles(projectDir, "*.ts"))
        {
            File.Copy(tsFile, Path.Combine(publishPath, Path.GetFileName(tsFile)));
        }

        // Step 4.5: Create .deployment file to force Oryx build
        await CreateDeploymentFile(publishPath);

        return publishPath;
    }

    public async Task<OryxManifest> CreateManifestAsync(string projectDir, string publishPath)
    {
        _logger.LogInformation("Creating Oryx manifest for Node.js...");
        
        var packageJsonPath = Path.Combine(projectDir, "package.json");
        var packageJson = await File.ReadAllTextAsync(packageJsonPath);
        
        // Parse package.json to detect start command and version
        using var doc = JsonDocument.Parse(packageJson);
        var root = doc.RootElement;

        // Detect Node version
        var nodeVersion = "20"; // Default
        if (root.TryGetProperty("engines", out var engines) && 
            engines.TryGetProperty("node", out var nodeVersionProp))
        {
            var versionString = nodeVersionProp.GetString() ?? "18";
            // Extract major version (e.g., "18.x" -> "18")
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
            startCommand = startScript.GetString() ?? startCommand;
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
            // Look for common entry point files
            var commonEntryPoints = new[] { "server.js", "app.js", "index.js", "main.js" };
            foreach (var entryPoint in commonEntryPoints)
            {
                if (File.Exists(Path.Combine(publishPath, entryPoint)))
                {
                    startCommand = $"node {entryPoint}";
                    _logger.LogInformation("Detected entry point: {Command}", startCommand);
                    break;
                }
            }
        }

        return new OryxManifest
        {
            Platform = "nodejs",
            Version = nodeVersion,
            Command = startCommand,
            BuildCommand = "npm run build",
            BuildRequired = true,
        };
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

    private async Task<CommandResult> ExecuteWithOutputAsync(string command, string arguments, string workingDirectory, bool verbose)
    {
        var result = await _executor.ExecuteAsync(command, arguments, workingDirectory);
        
        if (verbose || !result.Success)
        {
            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                _logger.LogInformation("Output:\n{Output}", result.StandardOutput);
            }
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                _logger.LogWarning("Warnings/Errors:\n{Error}", result.StandardError);
            }
        }
        
        return result;
    }
}
