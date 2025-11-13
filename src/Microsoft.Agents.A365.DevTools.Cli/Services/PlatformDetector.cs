// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Detects the project platform based on project structure
/// </summary>
public class PlatformDetector
{
    private readonly ILogger<PlatformDetector> _logger;

    public PlatformDetector(ILogger<PlatformDetector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Detect project platform from project directory
    /// Detection priority: .NET -> Node.js -> Python -> Unknown
    /// </summary>
    public Models.ProjectPlatform Detect(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
        {
            _logger.LogError("Project path does not exist: {Path}", projectPath);
            return Models.ProjectPlatform.Unknown;
        }

        _logger.LogInformation("Detecting platform in: {Path}", projectPath);

        // Check for .NET project files
        var dotnetFiles = Directory.GetFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(projectPath, "*.fsproj", SearchOption.TopDirectoryOnly))
            .Concat(Directory.GetFiles(projectPath, "*.vbproj", SearchOption.TopDirectoryOnly))
            .ToArray();

        if (dotnetFiles.Length > 0)
        {
            _logger.LogInformation("Detected .NET project (found {Count} project file(s))", dotnetFiles.Length);
            return Models.ProjectPlatform.DotNet;
        }

        // Check for Node.js
        var packageJsonPath = Path.Combine(projectPath, "package.json");
        var jsFiles = Directory.EnumerateFiles(projectPath, "*.js").Any();
        var tsFiles = Directory.EnumerateFiles(projectPath, "*.ts").Any();

        if (File.Exists(packageJsonPath) || jsFiles || tsFiles)
        {
            _logger.LogInformation("Detected Node.js project");
            return Models.ProjectPlatform.NodeJs;
        }

        // Check for Python
        var requirementsPath = Path.Combine(projectPath, "requirements.txt");
        var setupPyPath = Path.Combine(projectPath, "setup.py");
        var pyprojectPath = Path.Combine(projectPath, "pyproject.toml");
        var pythonFiles = Directory.GetFiles(projectPath, "*.py", SearchOption.TopDirectoryOnly);

        if (File.Exists(requirementsPath) || File.Exists(setupPyPath) || File.Exists(pyprojectPath) || pythonFiles.Length > 0)
        {
            _logger.LogInformation("Detected Python project");
            return Models.ProjectPlatform.Python;
        }

        _logger.LogWarning("Could not detect project platform in: {Path}", projectPath);
        return Models.ProjectPlatform.Unknown;
    }
}
