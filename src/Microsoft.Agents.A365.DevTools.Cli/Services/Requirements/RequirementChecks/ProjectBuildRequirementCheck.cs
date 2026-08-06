// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Validation;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Validates that the user's project builds locally with warnings treated as errors.
/// Uses PlatformDetector to determine the project type and runs the appropriate build command.
/// </summary>
public class ProjectBuildRequirementCheck : RequirementCheck
{
    private readonly PlatformDetector _platformDetector;
    private readonly CommandExecutor _commandExecutor;

    // Resolved uv command path — set during dependency install, used by build command.
    private string? _resolvedUvCommand;

    public ProjectBuildRequirementCheck(PlatformDetector platformDetector, CommandExecutor commandExecutor)
    {
        _platformDetector = platformDetector ?? throw new ArgumentNullException(nameof(platformDetector));
        _commandExecutor = commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    }

    /// <inheritdoc />
    public override string Name => "Project Build";

    /// <inheritdoc />
    public override string Description => "Validates that the project builds locally with warnings treated as errors";

    /// <inheritdoc />
    public override string Category => "Code Health";

    /// <inheritdoc />
    public override async Task<RequirementCheckResult> CheckAsync(
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteCheckWithLoggingAsync(config, logger, CheckImplementationAsync, cancellationToken);
    }

    private async Task<RequirementCheckResult> CheckImplementationAsync(
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var projectPath = ResolveProjectPath(config);

        if (!Directory.Exists(projectPath))
        {
            return RequirementCheckResult.Failure(
                $"Project path does not exist: {projectPath}",
                "Ensure the project directory exists, or set deploymentProjectPath in a365.config.json");
        }

        var platform = _platformDetector.Detect(projectPath);

        if (platform == ProjectPlatform.Unknown)
        {
            return RequirementCheckResult.Warning(
                "Could not detect project platform, skipping build validation",
                details: $"No .NET, Node.js, or Python project detected in {projectPath}");
        }

        var buildLogFile = GetBuildLogPath(platform);

        // Install dependencies before building (Python: uv/pip, Node.js: npm install)
        var (depFailure, depOutput) = await InstallDependenciesAsync(platform, projectPath, logger, cancellationToken);

        // Write dependency install output to the build log (even on success)
        if (depOutput is not null)
        {
            WriteBuildLog(buildLogFile, depOutput);
        }

        if (depFailure is not null)
        {
            if (depOutput is null)
            {
                WriteBuildLog(buildLogFile, null, depFailure.ErrorMessage);
            }

            depFailure.Metadata = new RequirementCheckMetadata
            {
                Platform = platform.ToString(),
                BuildLogFile = buildLogFile
            };
            return depFailure;
        }

        var (command, arguments) = GetBuildCommand(platform, buildLogFile, projectPath);

        logger.LogDebug("Running build check: {Command} {Arguments} in {Path}", command, arguments, projectPath);

        var result = await _commandExecutor.ExecuteAsync(
            command,
            arguments,
            workingDirectory: projectPath,
            captureOutput: true,
            suppressErrorLogging: true,
            cancellationToken: cancellationToken);

        // For non-.NET platforms, append build output to the log file.
        // .NET uses MSBuild's built-in file logger (-fl) so this is already handled.
        if (platform != ProjectPlatform.DotNet)
        {
            WriteBuildLog(buildLogFile, result, append: depOutput is not null);
        }

        if (result.Success)
        {
            return new RequirementCheckResult
            {
                Passed = true,
                Details = $"{platform} project builds with warnings as errors",
                Metadata = new RequirementCheckMetadata
                {
                    Platform = platform.ToString(),
                    ExitCode = result.ExitCode,
                    BuildLogFile = buildLogFile,
                    ResolvedUvCommand = _resolvedUvCommand
                }
            };
        }

        var errorSummary = ExtractBuildErrorSummary(result, platform);

        return new RequirementCheckResult
        {
            Passed = false,
            ErrorMessage = $"Project build failed ({platform}):\n{errorSummary}",
            ResolutionGuidance = GetResolutionGuidance(platform),
            Metadata = new RequirementCheckMetadata
            {
                Platform = platform.ToString(),
                ExitCode = result.ExitCode,
                BuildLogFile = buildLogFile,
                ResolvedUvCommand = _resolvedUvCommand
            }
        };
    }

    private (string Command, string Arguments) GetBuildCommand(ProjectPlatform platform, string buildLogFile, string projectPath)
    {
        return platform switch
        {
            ProjectPlatform.DotNet => ("dotnet",
                $"build --no-restore /p:TreatWarningsAsErrors=true -fl \"-flp:logfile={buildLogFile};verbosity=normal\""),
            ProjectPlatform.NodeJs => ("npm", "run build"),
            ProjectPlatform.Python => DetectPythonInstallCommand(projectPath) is ("uv", _)
                ? (_resolvedUvCommand ?? "uv", "run python -m compileall -q .")
                : ("python", "-m compileall -q ."),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unsupported platform")
        };
    }

    /// <summary>
    /// Returns the path for the build log file.
    /// </summary>
    private static string GetBuildLogPath(ProjectPlatform platform)
    {
        var suffix = platform switch
        {
            ProjectPlatform.DotNet => "build",
            ProjectPlatform.NodeJs => "build.npm",
            ProjectPlatform.Python => "build.python",
            _ => "build"
        };

        return ConfigService.GetCommandLogPath($"validate.{suffix}");
    }

    private static string GetResolutionGuidance(ProjectPlatform platform)
    {
        return platform switch
        {
            ProjectPlatform.DotNet => "Fix the build errors and warnings in your project.\n" +
                "Run 'dotnet build /p:TreatWarningsAsErrors=true' locally to see the full output.",
            ProjectPlatform.NodeJs => "Fix the build errors in your project.\n" +
                "Run 'npm run build' locally to see the full output.",
            ProjectPlatform.Python => "Fix the syntax errors in your Python files.\n" +
                "Run 'python -m compileall -q .' locally to see the full output.",
            _ => "Fix the build errors in your project and try again."
        };
    }

    /// <summary>
    /// Extracts a concise summary from build output, limiting to the most relevant lines.
    /// </summary>
    private static string ExtractBuildErrorSummary(CommandResult result, ProjectPlatform platform)
    {
        // Combine both streams — many tools write warnings (not errors) to stderr,
        // so preferring stderr alone can surface a deprecation warning instead of the real error.
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            parts.Add(result.StandardOutput);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            parts.Add(result.StandardError);
        }

        var output = string.Join("\n", parts);

        if (string.IsNullOrWhiteSpace(output))
        {
            return $"Build exited with code {result.ExitCode} (no output captured)";
        }

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (platform == ProjectPlatform.DotNet)
        {
            // For .NET, extract lines containing "error" or "warning" (MSBuild output)
            var diagnosticLines = lines
                .Where(l => l.Contains(": error ", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains(": warning ", StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Trim())
                .Take(10)
                .ToArray();

            if (diagnosticLines.Length > 0)
            {
                return string.Join("\n", diagnosticLines.Select(l => $"  {l}"));
            }
        }

        // Fallback: return last 10 meaningful lines
        var lastLines = lines
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .TakeLast(10)
            .ToArray();

        return string.Join("\n", lastLines.Select(l => $"  {l}"));
    }

    /// <summary>
    /// Writes captured build output to a log file for non-.NET platforms.
    /// </summary>
    private static void WriteBuildLog(string logPath, CommandResult? result, string? errorMessage = null, bool append = false)
    {
        try
        {
            var dir = Path.GetDirectoryName(logPath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            using var writer = new StreamWriter(logPath, append: append);

            if (errorMessage is not null)
            {
                writer.WriteLine(errorMessage);
            }

            if (result is not null)
            {
                if (!string.IsNullOrEmpty(result.StandardOutput))
                {
                    writer.WriteLine(result.StandardOutput);
                }

                if (!string.IsNullOrEmpty(result.StandardError))
                {
                    writer.WriteLine(result.StandardError);
                }
            }
        }
        catch
        {
            // Best-effort: don't fail the check if log writing fails.
        }
    }

    /// <summary>
    /// Installs dependencies for the detected platform before building.
    /// Returns null if no install is needed or deps installed successfully.
    /// Returns a failure result if installation fails.
    /// </summary>
    private async Task<(RequirementCheckResult? FailureResult, CommandResult? Output)> InstallDependenciesAsync(
        ProjectPlatform platform,
        string projectPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        return platform switch
        {
            ProjectPlatform.Python => await InstallPythonDependenciesAsync(projectPath, logger, cancellationToken),
            ProjectPlatform.NodeJs => await InstallNodeDependenciesAsync(projectPath, logger, cancellationToken),
            ProjectPlatform.DotNet => await RestoreDotNetDependenciesAsync(projectPath, logger, cancellationToken),
            _ => (null, null)
        };
    }

    /// <summary>
    /// Runs dotnet restore so that the subsequent --no-restore build has a valid assets file.
    /// </summary>
    private async Task<(RequirementCheckResult? FailureResult, CommandResult? Output)> RestoreDotNetDependenciesAsync(
        string projectPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("Running dotnet restore in {Path}", projectPath);

        var result = await _commandExecutor.ExecuteAsync(
            "dotnet", "restore",
            workingDirectory: projectPath,
            captureOutput: true,
            suppressErrorLogging: true,
            cancellationToken: cancellationToken);

        if (result.Success)
        {
            logger.LogDebug("dotnet restore completed successfully");
            return (null, result);
        }

        var summary = ExtractBuildErrorSummary(result, ProjectPlatform.DotNet);

        return (new RequirementCheckResult
        {
            Passed = false,
            ErrorMessage = $"Package restore failed (.NET):\n{summary}",
            ResolutionGuidance = "Run 'dotnet restore' manually and fix any dependency issues."
        }, null);
    }

    /// <summary>
    /// Runs npm install if a package.json exists and node_modules is missing.
    /// </summary>
    private async Task<(RequirementCheckResult? FailureResult, CommandResult? Output)> InstallNodeDependenciesAsync(
        string projectPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var packageJson = Path.Combine(projectPath, "package.json");
        if (!File.Exists(packageJson))
        {
            return (null, null);
        }

        var nodeModules = Path.Combine(projectPath, "node_modules");
        if (Directory.Exists(nodeModules))
        {
            logger.LogDebug("node_modules already exists, skipping npm install");
            return (null, null);
        }

        logger.LogDebug("Running npm install in {Path}", projectPath);

        var result = await _commandExecutor.ExecuteAsync(
            "npm", "install",
            workingDirectory: projectPath,
            captureOutput: true,
            suppressErrorLogging: true,
            cancellationToken: cancellationToken);

        if (result.Success)
        {
            logger.LogDebug("npm install completed successfully");
            return (null, result);
        }

        var summary = ExtractBuildErrorSummary(result, ProjectPlatform.NodeJs);

        return (new RequirementCheckResult
        {
            Passed = false,
            ErrorMessage = $"Failed to install Node.js dependencies (npm install):\n{summary}",
            ResolutionGuidance = "Run 'npm install' manually to see the full output."
        }, result);
    }

    /// <summary>
    /// Detects the Python package manager used by the project and installs dependencies.
    /// Detection order: uv (uv.lock or pyproject.toml) -> pip (requirements.txt).
    /// Returns null if no dependency file is found (no install needed) or deps installed successfully.
    /// Returns a failure result if installation fails.
    /// </summary>
    private async Task<(RequirementCheckResult? FailureResult, CommandResult? Output)> InstallPythonDependenciesAsync(
        string projectPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var (command, arguments) = DetectPythonInstallCommand(projectPath);

        if (command is null)
        {
            logger.LogDebug("No Python dependency file found, skipping dependency install");
            return (null, null);
        }

        // If uv is needed but not installed, install it and resolve its path
        if (command == "uv")
        {
            var uvCommand = await EnsureUvInstalledAsync(logger, cancellationToken);
            if (uvCommand is null)
            {
                return (new RequirementCheckResult
                {
                    Passed = false,
                    ErrorMessage = "uv is required but could not be installed",
                    ResolutionGuidance = "Install uv manually: pip install uv  (or see https://docs.astral.sh/uv/getting-started/installation/)"
                }, null);
            }

            // Use the resolved uv path for the install command
            command = uvCommand;
            _resolvedUvCommand = uvCommand;
        }

        logger.LogDebug("Installing Python dependencies: {Command} {Arguments}", command, arguments);

        var result = await _commandExecutor.ExecuteAsync(
            command,
            arguments!,
            workingDirectory: projectPath,
            captureOutput: true,
            suppressErrorLogging: true,
            cancellationToken: cancellationToken);

        if (result.Success)
        {
            logger.LogDebug("Python dependencies installed successfully");
            return (null, result);
        }

        var summary = ExtractBuildErrorSummary(result, ProjectPlatform.Python);

        return (new RequirementCheckResult
        {
            Passed = false,
            ErrorMessage = $"Failed to install Python dependencies ({command} {arguments}):\n{summary}",
            ResolutionGuidance = $"Run '{command} {arguments}' manually to see the full output."
        }, result);
    }

    /// <summary>
    /// Checks if uv is available on PATH. If not, attempts to install it via pip.
    /// Returns the resolved uv command (either "uv" if on PATH, or full path after pip install).
    /// Returns null if uv cannot be made available.
    /// </summary>
    private async Task<string?> EnsureUvInstalledAsync(ILogger logger, CancellationToken cancellationToken)
    {
        // Check if uv is already available on PATH
        var checkResult = await _commandExecutor.ExecuteAsync(
            "uv", "version",
            captureOutput: true,
            suppressErrorLogging: true,
            cancellationToken: cancellationToken);

        if (checkResult.Success)
        {
            logger.LogDebug("uv is available: {Version}", checkResult.StandardOutput.Trim());
            return "uv";
        }

        // Try to install uv via pip (use python -m pip to target the active interpreter)
        logger.LogDebug("uv not found, attempting to install via python -m pip");

        var installResult = await _commandExecutor.ExecuteAsync(
            "python", "-m pip install uv",
            captureOutput: true,
            suppressErrorLogging: true,
            cancellationToken: cancellationToken);

        if (!installResult.Success)
        {
            logger.LogWarning("Failed to install uv: {Error}",
                !string.IsNullOrWhiteSpace(installResult.StandardError)
                    ? installResult.StandardError.Trim()
                    : installResult.StandardOutput.Trim());
            return null;
        }

        logger.LogDebug("uv installed via pip, resolving path");

        // After pip install, uv may not be on PATH for the current process.
        // Resolve its location via pip show or python -c.
        var resolveResult = await _commandExecutor.ExecuteAsync(
            "python", "-c \"import shutil; p = shutil.which('uv'); print(p if p else '')\"",
            captureOutput: true,
            suppressErrorLogging: true,
            cancellationToken: cancellationToken);

        var uvPath = resolveResult.Success ? resolveResult.StandardOutput.Trim() : null;

        if (!string.IsNullOrWhiteSpace(uvPath) && File.Exists(uvPath))
        {
            logger.LogDebug("Resolved uv path: {Path}", uvPath);
            return uvPath;
        }

        // Fallback: try common Scripts directory
        var scriptsUv = ResolveUvFromPythonScripts();
        if (scriptsUv is not null)
        {
            logger.LogDebug("Found uv in Python Scripts: {Path}", scriptsUv);
            return scriptsUv;
        }

        // Last resort: try "uv" again (pip may have added it to PATH)
        var retryResult = await _commandExecutor.ExecuteAsync(
            "uv", "version",
            captureOutput: true,
            suppressErrorLogging: true,
            cancellationToken: cancellationToken);

        if (retryResult.Success)
        {
            return "uv";
        }

        logger.LogWarning("uv was installed via pip but could not be found on PATH");
        return null;
    }

    /// <summary>
    /// Attempts to find the uv executable in Python's Scripts directory.
    /// </summary>
    private static string? ResolveUvFromPythonScripts()
    {
        try
        {
            var pythonPath = Environment.GetEnvironmentVariable("VIRTUAL_ENV");
            if (!string.IsNullOrEmpty(pythonPath))
            {
                var uvInVenv = Path.Combine(pythonPath,
                    OperatingSystem.IsWindows() ? "Scripts" : "bin",
                    OperatingSystem.IsWindows() ? "uv.exe" : "uv");
                if (File.Exists(uvInVenv))
                    return uvInVenv;
            }

            // Check user-level pip install location
            var userBase = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userBase))
            {
                // Windows: %APPDATA%\Python\PythonXX\Scripts
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(appData))
                {
                    var pythonDirs = Directory.Exists(Path.Combine(appData, "Python"))
                        ? Directory.GetDirectories(Path.Combine(appData, "Python"), "Python*")
                        : [];

                    foreach (var pyDir in pythonDirs)
                    {
                        var candidate = Path.Combine(pyDir, "Scripts",
                            OperatingSystem.IsWindows() ? "uv.exe" : "uv");
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
        }
        catch
        {
            // Best-effort
        }

        return null;
    }

    /// <summary>
    /// Detects the appropriate install command for a Python project.
    /// Returns (null, null) if no dependency file is found.
    /// </summary>
    internal static (string? Command, string? Arguments) DetectPythonInstallCommand(string projectPath)
    {
        // uv: check for uv.lock or pyproject.toml (uv can work with pyproject.toml directly)
        if (File.Exists(Path.Combine(projectPath, "uv.lock")))
        {
            return ("uv", "sync");
        }

        if (File.Exists(Path.Combine(projectPath, "pyproject.toml")))
        {
            // pyproject.toml could be used by uv, pip, or poetry.
            // Check if uv is the tool by looking for [tool.uv] section
            if (HasUvConfig(Path.Combine(projectPath, "pyproject.toml")))
            {
                return ("uv", "sync");
            }

            // Generic pyproject.toml — use pip install
            return ("pip", "install -e .");
        }

        if (File.Exists(Path.Combine(projectPath, "requirements.txt")))
        {
            return ("pip", "install -r requirements.txt");
        }

        return (null, null);
    }

    /// <summary>
    /// Checks if a pyproject.toml contains a [tool.uv] section, indicating uv is the package manager.
    /// </summary>
    internal static bool HasUvConfig(string pyprojectPath)
    {
        try
        {
            foreach (var line in File.ReadLines(pyprojectPath))
            {
                if (line.TrimStart().StartsWith("[tool.uv", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Best-effort
        }

        return false;
    }

    /// <summary>
    /// Returns deploymentProjectPath if configured, otherwise falls back to the current directory.
    /// </summary>
    private static string ResolveProjectPath(Agent365Config config)
    {
        return string.IsNullOrWhiteSpace(config.DeploymentProjectPath)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(config.DeploymentProjectPath);
    }
}
