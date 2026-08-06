// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Validation;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Validates that the user's agent app starts locally and responds on a health endpoint.
/// Spawns the app process, polls /api/health, captures stdout/stderr, then stops the process.
/// </summary>
public class LocalRuntimeRequirementCheck : RequirementCheck, IDisposable
{
    private readonly PlatformDetector _platformDetector;
    private readonly IProcessService _processService;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string? _resolvedUvCommand;

    /// <summary>
    /// Default port used when no port can be inferred from configuration.
    /// </summary>
    internal const int DefaultPort = 5000;

    /// <summary>
    /// Default health endpoint path to probe.
    /// </summary>
    internal const string DefaultHealthPath = "/api/health";

    /// <summary>
    /// Maximum time to wait for the app to start and respond.
    /// </summary>
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Interval between health endpoint polls.
    /// </summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Maximum number of stdout/stderr lines to capture for diagnostics.
    /// </summary>
    internal const int MaxOutputLines = 50;

    public LocalRuntimeRequirementCheck(
        PlatformDetector platformDetector,
        IProcessService processService,
        HttpClient? httpClient = null,
        string? resolvedUvCommand = null)
    {
        _platformDetector = platformDetector ?? throw new ArgumentNullException(nameof(platformDetector));
        _processService = processService ?? throw new ArgumentNullException(nameof(processService));
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _resolvedUvCommand = resolvedUvCommand;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <inheritdoc />
    public override string Name => "Local Runtime";

    /// <inheritdoc />
    public override string Description => "Validates that the agent app starts locally and responds on a health endpoint";

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
                "Could not detect project platform, skipping local runtime validation",
                details: $"No .NET, Node.js, or Python project detected in {projectPath}");
        }

        var port = ResolvePort(projectPath, platform);
        var healthUrl = $"http://localhost:{port}{DefaultHealthPath}";

        logger.LogDebug(
            "Starting local runtime check: platform={Platform}, port={Port}, healthUrl={HealthUrl}, projectPath={ProjectPath}",
            platform, port, healthUrl, projectPath);

        var startInfo = BuildProcessStartInfo(platform, projectPath, port);
        return await SpawnAndProbeAsync(startInfo, healthUrl, platform, port, logger, cancellationToken);
    }

    /// <summary>
    /// Resolves the local port from the agent's project launch settings.
    /// Checks launchSettings.json (for .NET) or .env files (for Node.js/Python).
    /// Falls back to the default port when no setting is found.
    /// </summary>
    internal static int ResolvePort(string? projectPath, ProjectPlatform platform = ProjectPlatform.Unknown)
    {
        if (!string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath))
        {
            var portFromSettings = platform switch
            {
                ProjectPlatform.DotNet => ResolvePortFromLaunchSettings(projectPath),
                ProjectPlatform.NodeJs => ResolvePortFromEnvFile(projectPath),
                ProjectPlatform.Python => ResolvePortFromEnvFile(projectPath),
                _ => ResolvePortFromLaunchSettings(projectPath) ?? ResolvePortFromEnvFile(projectPath)
            };

            if (portFromSettings.HasValue)
            {
                return portFromSettings.Value;
            }
        }

        return DefaultPort;
    }

    /// <summary>
    /// Reads the port from Properties/launchSettings.json (first profile's applicationUrl).
    /// </summary>
    internal static int? ResolvePortFromLaunchSettings(string projectPath)
    {
        var launchSettingsPath = Path.Combine(projectPath, "Properties", "launchSettings.json");
        if (!File.Exists(launchSettingsPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(launchSettingsPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("profiles", out var profiles))
            {
                return null;
            }

            foreach (var profile in profiles.EnumerateObject())
            {
                if (profile.Value.TryGetProperty("applicationUrl", out var urlProp))
                {
                    var urls = urlProp.GetString();
                    if (string.IsNullOrWhiteSpace(urls))
                    {
                        continue;
                    }

                    // applicationUrl can be semicolon-separated; prefer HTTP for local validation
                    foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && !uri.IsDefaultPort)
                        {
                            return uri.Port;
                        }
                    }
                }
            }
        }
        catch
        {
            // Malformed launchSettings — fall through
        }

        return null;
    }

    /// <summary>
    /// Reads the PORT variable from a .env file in the project directory.
    /// </summary>
    internal static int? ResolvePortFromEnvFile(string projectPath)
    {
        var envPath = Path.Combine(projectPath, ".env");
        if (!File.Exists(envPath))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadLines(envPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("PORT=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed.Substring("PORT=".Length).Trim().Trim('"', '\'');
                    if (int.TryParse(value, out var port) && port > 0 && port <= 65535)
                    {
                        return port;
                    }
                }
            }
        }
        catch
        {
            // Malformed .env — fall through
        }

        return null;
    }

    private ProcessStartInfo BuildProcessStartInfo(ProjectPlatform platform, string projectPath, int port)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        switch (platform)
        {
            case ProjectPlatform.DotNet:
                startInfo.FileName = "dotnet";
                startInfo.Arguments = "run --no-build";
                startInfo.EnvironmentVariables["ASPNETCORE_URLS"] = $"http://localhost:{port}";
                break;

            case ProjectPlatform.NodeJs:
                WrapForWindows(startInfo, "npm", "start");
                startInfo.EnvironmentVariables["PORT"] = port.ToString();
                break;

            case ProjectPlatform.Python:
                var entryPoint = ResolvePythonEntryPoint(projectPath);
                var usesUv = ProjectBuildRequirementCheck.DetectPythonInstallCommand(projectPath) is ("uv", _);
                if (usesUv)
                {
                    startInfo.FileName = _resolvedUvCommand ?? "uv";
                    startInfo.Arguments = $"run python {entryPoint}";
                }
                else
                {
                    startInfo.FileName = "python";
                    startInfo.Arguments = entryPoint;
                }
                startInfo.EnvironmentVariables["PORT"] = port.ToString();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unsupported platform");
        }

        return startInfo;
    }

    /// <summary>
    /// On Windows, wraps batch-file commands (npm, npx, node) with cmd.exe /c
    /// so they can be started with UseShellExecute=false.
    /// </summary>
    internal static void WrapForWindows(ProcessStartInfo startInfo, string command, string arguments)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && IsBatchCommand(command))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/c {command} {arguments}";
        }
        else
        {
            startInfo.FileName = command;
            startInfo.Arguments = arguments;
        }
    }

    private static bool IsBatchCommand(string command)
    {
        var name = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();
        return name is "npm" or "npx" or "node";
    }

    private async Task<RequirementCheckResult> SpawnAndProbeAsync(
        ProcessStartInfo startInfo,
        string healthUrl,
        ProjectPlatform platform,
        int port,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var outputLines = new BoundedLineBuffer(MaxOutputLines);
        var errorLines = new BoundedLineBuffer(MaxOutputLines);
        Process? process = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var bootLogFile = ConfigService.GetCommandLogPath("validate.boot");

        try
        {
            process = _processService.Start(startInfo);
            if (process is null)
            {
                return RequirementCheckResult.Failure(
                    $"Failed to start {platform} process",
                    GetRunGuidance(platform));
            }

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null) outputLines.Add(args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null) errorLines.Add(args.Data);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(StartupTimeout);

            while (!timeoutCts.Token.IsCancellationRequested)
            {
                if (process.HasExited)
                {
                    var exitOutput = GetCapturedOutput(outputLines, errorLines);
                    WriteBootLog(bootLogFile, outputLines, errorLines);
                    return new RequirementCheckResult
                    {
                        Passed = false,
                        ErrorMessage = $"App exited early with code {process.ExitCode} before health endpoint responded:\n{exitOutput}",
                        ResolutionGuidance = GetRunGuidance(platform),
                        Metadata = new RequirementCheckMetadata
                        {
                            Platform = platform.ToString(),
                            ExitCode = process.ExitCode,
                            BootLogFile = bootLogFile
                        }
                    };
                }

                try
                {
                    using var response = await _httpClient.GetAsync(healthUrl, timeoutCts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        stopwatch.Stop();
                        logger.LogDebug("Health endpoint returned {StatusCode}", (int)response.StatusCode);
                        WriteBootLog(bootLogFile, outputLines, errorLines);
                        return new RequirementCheckResult
                        {
                            Passed = true,
                            Details = $"{platform} app running on port {port}, health endpoint returned HTTP {(int)response.StatusCode}",
                            Metadata = new RequirementCheckMetadata
                            {
                                Port = port,
                                BootMs = stopwatch.ElapsedMilliseconds,
                                Platform = platform.ToString(),
                                BootLogFile = bootLogFile
                            }
                        };
                    }

                    logger.LogDebug("Health endpoint returned non-success status {StatusCode}", (int)response.StatusCode);
                }
                catch (HttpRequestException)
                {
                    // App not ready yet, will retry
                }
                catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // Timeout — fall through to failure below
                    break;
                }

                try
                {
                    await Task.Delay(PollInterval, timeoutCts.Token);
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            var timeoutOutput = GetCapturedOutput(outputLines, errorLines);
            WriteBootLog(bootLogFile, outputLines, errorLines);
            return new RequirementCheckResult
            {
                Passed = false,
                ErrorMessage = $"App did not respond on {healthUrl} within {(int)StartupTimeout.TotalSeconds} seconds:\n{timeoutOutput}",
                ResolutionGuidance = GetRunGuidance(platform),
                Metadata = new RequirementCheckMetadata
                {
                    Platform = platform.ToString(),
                    BootLogFile = bootLogFile
                }
            };
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to kill process during cleanup");
                }

                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Writes captured stdout/stderr to a boot log file for post-mortem inspection.
    /// </summary>
    private static void WriteBootLog(string logPath, BoundedLineBuffer outputLines, BoundedLineBuffer errorLines)
    {
        try
        {
            var dir = Path.GetDirectoryName(logPath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            using var writer = new StreamWriter(logPath, append: false);
            var stdout = outputLines.GetLines();
            var stderr = errorLines.GetLines();

            if (stdout.Length > 0)
            {
                writer.WriteLine("[stdout]");
                foreach (var line in stdout)
                    writer.WriteLine(line);
            }

            if (stderr.Length > 0)
            {
                writer.WriteLine("[stderr]");
                foreach (var line in stderr)
                    writer.WriteLine(line);
            }

            if (stdout.Length == 0 && stderr.Length == 0)
            {
                writer.WriteLine("(no output captured)");
            }
        }
        catch
        {
            // Best-effort: don't fail the check if log writing fails.
        }
    }

    private static string GetCapturedOutput(BoundedLineBuffer outputLines, BoundedLineBuffer errorLines)
    {
        var sb = new StringBuilder();
        var stdout = outputLines.GetLines();
        var stderr = errorLines.GetLines();

        if (stdout.Length > 0)
        {
            sb.AppendLine("  [stdout]");
            foreach (var line in stdout)
                sb.AppendLine($"    {line}");
        }

        if (stderr.Length > 0)
        {
            sb.AppendLine("  [stderr]");
            foreach (var line in stderr)
                sb.AppendLine($"    {line}");
        }

        if (sb.Length == 0)
        {
            sb.Append("  (no output captured)");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Resolves the Python entry point by inspecting the project:
    /// 1. Procfile (web: python &lt;file&gt;) — explicit, highest priority
    /// 2. Scan top-level .py files for if __name__ == "__main__" guard
    /// 3. Among matches, prefer well-known names (app.py, main.py)
    /// 4. Falls back to app.py if nothing is found.
    /// </summary>
    internal static string ResolvePythonEntryPoint(string projectPath)
    {
        // Check Procfile for explicit command
        var procfilePath = Path.Combine(projectPath, "Procfile");
        if (File.Exists(procfilePath))
        {
            var entryFromProcfile = ParseProcfileEntryPoint(procfilePath);
            if (entryFromProcfile is not null)
            {
                return entryFromProcfile;
            }
        }

        // Scan top-level .py files for entry point guard
        var pyFiles = Directory.GetFiles(projectPath, "*.py", SearchOption.TopDirectoryOnly);
        var filesWithMain = new List<string>();

        foreach (var pyFile in pyFiles)
        {
            if (HasMainGuard(pyFile))
            {
                filesWithMain.Add(Path.GetFileName(pyFile));
            }
        }

        if (filesWithMain.Count == 1)
        {
            return filesWithMain[0];
        }

        if (filesWithMain.Count > 1)
        {
            // Prefer well-known entry point names among matches
            string[] preferred = ["app.py", "main.py", "__main__.py", "bot.py", "server.py"];
            foreach (var name in preferred)
            {
                if (filesWithMain.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    return name;
                }
            }

            // Return the first match alphabetically
            filesWithMain.Sort(StringComparer.OrdinalIgnoreCase);
            return filesWithMain[0];
        }

        // No __main__ guard found — check if well-known files exist at all
        string[] fallbackCandidates = ["app.py", "main.py", "__main__.py", "bot.py", "server.py"];
        foreach (var candidate in fallbackCandidates)
        {
            if (File.Exists(Path.Combine(projectPath, candidate)))
            {
                return candidate;
            }
        }

        // Default fallback
        return "app.py";
    }

    /// <summary>
    /// Checks whether a Python file contains an if __name__ == "__main__" guard,
    /// indicating it is designed to be run directly.
    /// </summary>
    internal static bool HasMainGuard(string filePath)
    {
        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                var trimmed = line.TrimStart();
                // Match: if __name__ == "__main__" or if __name__ == '__main__'
                if (trimmed.StartsWith("if", StringComparison.Ordinal) &&
                    trimmed.Contains("__name__") &&
                    trimmed.Contains("__main__"))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Best-effort: unreadable files are skipped
        }

        return false;
    }

    /// <summary>
    /// Parses a Procfile for the web process entry point.
    /// Expects format: web: python &lt;file.py&gt; [args...]
    /// </summary>
    internal static string? ParseProcfileEntryPoint(string procfilePath)
    {
        try
        {
            var lines = File.ReadAllLines(procfilePath);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("web:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Extract the command after "web:"
                var command = trimmed["web:".Length..].Trim();

                // Match patterns like "python app.py", "python -m module", "python3 main.py"
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                // Skip the python executable (python, python3, etc.)
                if (parts[0].StartsWith("python", StringComparison.OrdinalIgnoreCase))
                {
                    // Return everything after the python command as arguments
                    return string.Join(' ', parts[1..]);
                }

                // If it's gunicorn/uvicorn, return the full command using -m approach
                // e.g., "gunicorn app:app" -> we can't directly use this with "python"
                // So skip and fall through to file detection
            }
        }
        catch
        {
            // Best-effort: fall through to file detection
        }

        return null;
    }

    private static string GetRunGuidance(ProjectPlatform platform)
    {
        return platform switch
        {
            ProjectPlatform.DotNet => "Try running the app manually:\n" +
                "  dotnet run\n" +
                "Verify it starts and exposes /api/health.",
            ProjectPlatform.NodeJs => "Try running the app manually:\n" +
                "  npm start\n" +
                "Verify it starts and exposes /api/health.",
            ProjectPlatform.Python => "Try running the app manually:\n" +
                "  python app.py\n" +
                "Verify it starts and exposes /api/health.",
            _ => "Try running the app manually and verify it exposes /api/health."
        };
    }

    /// <summary>
    /// Thread-safe bounded buffer that keeps the last N lines.
    /// </summary>
    internal sealed class BoundedLineBuffer
    {
        private readonly Queue<string> _lines;
        private readonly int _maxLines;
        private readonly object _lock = new();

        public BoundedLineBuffer(int maxLines)
        {
            _maxLines = maxLines;
            _lines = new Queue<string>(maxLines);
        }

        public void Add(string line)
        {
            lock (_lock)
            {
                if (_lines.Count >= _maxLines)
                {
                    _lines.Dequeue();
                }
                _lines.Enqueue(line);
            }
        }

        public string[] GetLines()
        {
            lock (_lock)
            {
                return _lines.ToArray();
            }
        }
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
