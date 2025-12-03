// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// .NET platform builder
/// </summary>
public class DotNetBuilder : IPlatformBuilder
{
    private readonly ILogger<DotNetBuilder> _logger;
    private readonly CommandExecutor _executor;
    private readonly BuilderHelper _helper;

    public DotNetBuilder(ILogger<DotNetBuilder> logger, CommandExecutor executor)
    {
        _logger = logger;
        _executor = executor;
        _helper = new BuilderHelper(logger, executor);
    }

    public async Task<bool> ValidateEnvironmentAsync()
    {
        _logger.LogInformation("Validating .NET environment...");
        
        var result = await _executor.ExecuteAsync("dotnet", "--version", captureOutput: true);
        if (!result.Success)
        {
            _logger.LogError(".NET SDK not found. Please install .NET SDK from https://dotnet.microsoft.com/download");
            return false;
        }

        _logger.LogInformation(".NET SDK version: {Version}", result.StandardOutput.Trim());
        return true;
    }

    public async Task CleanAsync(string projectDir)
    {
        _logger.LogInformation("Cleaning .NET project...");
        
        var projectFile = ResolveProjectFile(projectDir);
        if (projectFile == null)
        {
            _logger.LogWarning("No .NET project file found in {Dir} - skipping clean (this is expected for publish folders)", projectDir);
            return;
        }

        var result = await _executor.ExecuteAsync("dotnet", $"clean \"{projectFile}\"", projectDir);
        if (!result.Success)
        {
            _logger.LogWarning("dotnet clean failed: {Error} - continuing anyway", result.StandardError);
        }
    }

    public async Task<string> BuildAsync(string projectDir, string outputPath, bool verbose)
    {
        _logger.LogInformation("Building .NET project...");
        
        var projectFile = ResolveProjectFile(projectDir);
        if (projectFile == null)
        {
            throw new FileNotFoundException("No .NET project file found in directory");
        }

        // Restore
        _logger.LogInformation("Restoring NuGet packages...");
        var restoreResult = await _executor.ExecuteAsync("dotnet", $"restore \"{projectFile}\"", projectDir);
        if (!restoreResult.Success)
        {
            throw new Exception($"dotnet restore failed: {restoreResult.StandardError}");
        }

        // Remove old publish directory
        var publishPath = Path.Combine(projectDir, outputPath);
        if (Directory.Exists(publishPath))
        {
            Directory.Delete(publishPath, recursive: true);
        }

        // Publish
        _logger.LogInformation("Publishing .NET application...");
        var publishArgs = $"publish \"{projectFile}\" -c Release -o \"{outputPath}\" --self-contained false --verbosity minimal";
        var publishResult = await _helper.ExecuteWithOutputAsync("dotnet", publishArgs, projectDir, verbose);
        
        if (!publishResult.Success)
        {
            _logger.LogError("dotnet publish failed with exit code {ExitCode}", publishResult.ExitCode);
            throw new Exception("dotnet publish failed - see output above for details");
        }

        if (!Directory.Exists(publishPath))
        {
            throw new DirectoryNotFoundException($"Expected publish output path not found: {publishPath}");
        }

        return publishPath;
    }

    public async Task<OryxManifest> CreateManifestAsync(string projectDir, string publishPath)
    {
        _logger.LogInformation("Creating Oryx manifest for .NET...");
        
        // Find entry point DLL
        var depsFiles = Directory.GetFiles(publishPath, "*.deps.json");
        if (depsFiles.Length == 0)
        {
            throw new FileNotFoundException("No .deps.json file found. Cannot determine entry point.");
        }

        var entryDll = Path.GetFileNameWithoutExtension(depsFiles[0]) + ".dll";
        _logger.LogInformation("Detected entry point: {Dll}", entryDll);

        // Detect .NET version
        var dotnetVersion = "8.0"; // Default
        var projectFile = ResolveProjectFile(projectDir);
        if (projectFile != null)
        {
            var projectFilePath = Path.Combine(projectDir, projectFile);
            var projectContent = await File.ReadAllTextAsync(projectFilePath);
            var tfmMatch = System.Text.RegularExpressions.Regex.Match(
                projectContent, 
                @"<TargetFramework>net(\d+\.\d+)</TargetFramework>");
            
            if (tfmMatch.Success)
            {
                dotnetVersion = tfmMatch.Groups[1].Value;
                _logger.LogInformation("Detected .NET version: {Version}", dotnetVersion);
            }
        }

        return new OryxManifest
        {
            Platform = "dotnet",
            Version = dotnetVersion,
            Command = $"dotnet {entryDll}"
        };
    }

    public async Task<bool> ConvertEnvToAzureAppSettingsAsync(string projectDir, string resourceGroup, string webAppName, bool verbose)
    {
        // Not needed for dotnet projects.
        return await Task.FromResult(true);
    }

    private string? ResolveProjectFile(string projectDir)
    {
        var csprojFiles = Directory.GetFiles(projectDir, "*.csproj", SearchOption.TopDirectoryOnly);
        var fsprojFiles = Directory.GetFiles(projectDir, "*.fsproj", SearchOption.TopDirectoryOnly);
        var vbprojFiles = Directory.GetFiles(projectDir, "*.vbproj", SearchOption.TopDirectoryOnly);

        var allProjectFiles = csprojFiles.Concat(fsprojFiles).Concat(vbprojFiles).ToArray();

        if (allProjectFiles.Length == 0)
        {
            _logger.LogError("No .NET project file found in {Dir}", projectDir);
            return null;
        }

        if (allProjectFiles.Length > 1)
        {
            _logger.LogWarning("Multiple project files found. Using: {File}", 
                Path.GetFileName(allProjectFiles[0]));
        }

        return Path.GetFileName(allProjectFiles[0]);
    }
}
