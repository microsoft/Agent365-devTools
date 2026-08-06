// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Validation;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Validates that the agent can hold a multi-turn conversation by spawning the agent locally,
/// waiting for readiness via /api/health, then POSTing Bot Framework Activity messages to /api/messages.
/// </summary>
public class ConversationRequirementCheck : RequirementCheck, IDisposable
{
    private readonly PlatformDetector _platformDetector;
    private readonly IProcessService _processService;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IBotCallbackReceiver? _callbackReceiver;
    private readonly bool _launchPlayground;
    private readonly string? _resolvedUvCommand;

    /// <summary>
    /// Maximum time to wait for the app to start and respond on the health endpoint.
    /// </summary>
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum time to wait for a single conversation turn response.
    /// </summary>
    internal static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Maximum time to wait for the agent to respond via the serviceUrl callback after a successful POST.
    /// </summary>
    internal static readonly TimeSpan ResponseWaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Interval between health endpoint polls during startup.
    /// </summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Delay after health endpoint is ready before sending messages.
    /// Agents often need additional time to initialize their message pipeline after the HTTP server is up.
    /// </summary>
    internal static readonly TimeSpan PostHealthWarmupDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum number of retries for a conversation turn that fails with a transient error.
    /// </summary>
    internal const int MaxTurnRetries = 2;

    /// <summary>
    /// Delay between retry attempts for a failed conversation turn.
    /// </summary>
    internal static readonly TimeSpan TurnRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum number of stdout/stderr lines to capture for diagnostics.
    /// </summary>
    internal const int MaxOutputLines = 50;

    /// <summary>
    /// Default port used when no port can be inferred from configuration.
    /// </summary>
    internal const int DefaultPort = 5000;

    /// <summary>
    /// Default multi-turn conversation prompts used for validation.
    /// The middle turn is replaced with a tool-specific prompt when ToolingManifest.json is available.
    /// </summary>
    internal static readonly string[] DefaultConversationPrompts = new[]
    {
        "Hello",
        "What can you do?",
        "Thanks"
    };

    /// <summary>
    /// Fallback prompt when no tools are discovered from ToolingManifest.json.
    /// </summary>
    internal const string FallbackToolPrompt = "What can you do?";

    /// <summary>
    /// Maps well-known MCP server names to natural-language questions that would trigger tool usage.
    /// Keys are lowercase for case-insensitive matching.
    /// </summary>
    internal static readonly Dictionary<string, string> KnownToolPrompts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mail"] = "Get me my recent emails",
        ["calendar"] = "What meetings do I have today?",
        ["sharepoint"] = "Get me my recent SharePoint files",
        ["onedrive"] = "List my recent OneDrive files",
        ["teams"] = "Show me my recent Teams messages",
        ["planner"] = "What tasks are assigned to me?",
        ["todo"] = "Show me my to-do items",
        ["people"] = "Find my recent contacts",
        ["search"] = "Search for recent documents",
        ["files"] = "List my recent files",
    };

    /// <summary>
    /// Builds a natural-language prompt that would trigger the agent to invoke a configured tool.
    /// Looks up the first MCP server name in <see cref="KnownToolPrompts"/> for a matching question,
    /// falls back to a description-based prompt, then to a generic question.
    /// </summary>
    internal static string BuildToolInvocationPrompt(string projectPath, ILogger logger)
    {
        var manifestPath = Path.Combine(projectPath, McpConstants.ToolingManifestFileName);
        if (!File.Exists(manifestPath))
        {
            logger.LogDebug("No ToolingManifest.json found at {Path}, using default prompt", manifestPath);
            return FallbackToolPrompt;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<ToolingManifest>(json);

            if (manifest?.McpServers is null || manifest.McpServers.Length == 0)
            {
                logger.LogDebug("ToolingManifest.json has no MCP servers, using default prompt");
                return FallbackToolPrompt;
            }

            var firstTool = manifest.McpServers[0];
            var toolName = firstTool.McpServerName;

            if (string.IsNullOrWhiteSpace(toolName))
            {
                logger.LogDebug("First MCP server has no name, using default prompt");
                return FallbackToolPrompt;
            }

            // Check for a well-known tool keyword in the server name (e.g. "SharePoint" matches "M365SharePoint")
            var matchedPrompt = KnownToolPrompts
                .FirstOrDefault(kvp => toolName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase));

            if (matchedPrompt.Value is not null)
            {
                logger.LogDebug("Using known tool prompt for MCP server: {ToolName} (matched keyword: {Keyword})", toolName, matchedPrompt.Key);
                return matchedPrompt.Value;
            }

            // Fall back to a description-based prompt if available
            if (!string.IsNullOrWhiteSpace(firstTool.Description))
            {
                logger.LogDebug("Using description-based prompt for MCP server: {ToolName}", toolName);
                return $"Help me with {firstTool.Description.TrimEnd('.')}";
            }

            // Generic prompt referencing the tool name
            logger.LogDebug("Using generic prompt for MCP server: {ToolName}", toolName);
            return $"Help me with {toolName}";
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to parse ToolingManifest.json for tool discovery, using default prompt");
            return FallbackToolPrompt;
        }
    }

    /// <summary>
    /// Builds conversation prompts, replacing the middle turn with a tool invocation prompt
    /// when ToolingManifest.json is present and contains configured tools.
    /// </summary>
    internal static string[] BuildConversationPrompts(string projectPath, ILogger logger)
    {
        var toolPrompt = BuildToolInvocationPrompt(projectPath, logger);
        return new[]
        {
            DefaultConversationPrompts[0],
            toolPrompt,
            DefaultConversationPrompts[2]
        };
    }

    public ConversationRequirementCheck(
        PlatformDetector platformDetector,
        IProcessService processService,
        HttpClient? httpClient = null,
        IBotCallbackReceiver? callbackReceiver = null,
        bool launchPlayground = false,
        string? resolvedUvCommand = null)
    {
        _platformDetector = platformDetector ?? throw new ArgumentNullException(nameof(platformDetector));
        _processService = processService ?? throw new ArgumentNullException(nameof(processService));
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _callbackReceiver = callbackReceiver;
        _launchPlayground = launchPlayground;
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
    public override string Name => "Conversation";

    /// <inheritdoc />
    public override string Description => "Validates multi-turn conversation with the agent via /api/messages";

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
                "Could not detect project platform, skipping conversation validation",
                details: $"No .NET, Node.js, or Python project detected in {projectPath}");
        }

        var port = LocalRuntimeRequirementCheck.ResolvePort(projectPath, platform);
        var healthUrl = $"http://localhost:{port}{LocalRuntimeRequirementCheck.DefaultHealthPath}";
        var messagesUrl = $"http://localhost:{port}/api/messages";
        var conversationId = $"validate-{Guid.NewGuid():N}";

        logger.LogDebug(
            "Starting conversation check: platform={Platform}, port={Port}, projectPath={ProjectPath}",
            platform, port, projectPath);

        var startInfo = BuildProcessStartInfo(platform, projectPath, port);
        var prompts = BuildConversationPrompts(projectPath, logger);
        return await SpawnAndConverse(startInfo, healthUrl, messagesUrl, conversationId, platform, port, prompts, logger, cancellationToken);
    }

    private async Task<RequirementCheckResult> SpawnAndConverse(
        ProcessStartInfo startInfo,
        string healthUrl,
        string messagesUrl,
        string conversationId,
        ProjectPlatform platform,
        int port,
        string[] conversationPrompts,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var outputLines = new LocalRuntimeRequirementCheck.BoundedLineBuffer(MaxOutputLines);
        var errorLines = new LocalRuntimeRequirementCheck.BoundedLineBuffer(MaxOutputLines);
        string? agentConsoleLogPath = null;
        string? conversationLogPath = null;
        StreamWriter? consoleLogWriter = null;
        StreamWriter? conversationLogWriter = null;
        Process? process = null;
        IBotCallbackReceiver? receiver = _callbackReceiver;
        bool ownedReceiver = false;

        try
        {
            // Create conversation log file for diagnostics
            try
            {
                conversationLogPath = ConfigService.GetCommandLogPath("validate.conversation");
                conversationLogWriter = new StreamWriter(conversationLogPath, append: false, encoding: System.Text.Encoding.UTF8)
                {
                    AutoFlush = true
                };
                conversationLogWriter.WriteLine($"Conversation validation started at {DateTimeOffset.Now:O}");
                conversationLogWriter.WriteLine($"Platform: {platform}, Port: {port}");
                conversationLogWriter.WriteLine($"Command: {startInfo.FileName} {startInfo.Arguments}");
                conversationLogWriter.WriteLine(new string('-', 60));
            }
            catch
            {
                conversationLogPath = null;
            }

            // Start callback receiver for agent response tracking
            if (receiver is null)
            {
                try
                {
                    var httpReceiver = new HttpListenerBotCallbackReceiver();
                    await httpReceiver.StartAsync(cancellationToken);
                    receiver = httpReceiver;
                    ownedReceiver = true;
                    logger.LogDebug("Callback receiver started on {ServiceUrl}", receiver.ServiceUrl);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not start callback receiver, agent response tracking unavailable");
                }
            }

            process = _processService.Start(startInfo);
            if (process is null)
            {
                conversationLogWriter?.WriteLine("FAILED: Could not start process");
                return new RequirementCheckResult
                {
                    Passed = false,
                    ErrorMessage = $"Failed to start {platform} process",
                    ResolutionGuidance = GetRunGuidance(platform),
                    Metadata = new RequirementCheckMetadata
                    {
                        Platform = platform.ToString(),
                        ConversationLogFile = conversationLogPath
                    }
                };
            }

            // Create agent console log file for telemetry analysis
            try
            {
                agentConsoleLogPath = ConfigService.GetCommandLogPath("validate.agent-console");
                consoleLogWriter = new StreamWriter(agentConsoleLogPath, append: false, encoding: System.Text.Encoding.UTF8)
                {
                    AutoFlush = true
                };
                logger.LogDebug("Writing agent console output to {LogPath}", agentConsoleLogPath);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not create agent console log file, telemetry analysis will use in-memory buffer");
                agentConsoleLogPath = null;
            }

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    outputLines.Add(args.Data);
                    try { consoleLogWriter?.WriteLine(args.Data); } catch { /* best-effort */ }
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    errorLines.Add(args.Data);
                    try { consoleLogWriter?.WriteLine(args.Data); } catch { /* best-effort */ }
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Phase 1: Wait for health endpoint
            conversationLogWriter?.WriteLine("\n[Phase 1] Waiting for health endpoint...");
            var bootResult = await WaitForHealthAsync(process, healthUrl, platform, logger, cancellationToken);
            if (bootResult is not null)
            {
                conversationLogWriter?.WriteLine($"FAILED: {bootResult.ErrorMessage}");
                WriteAgentOutputToConversationLog(conversationLogWriter, outputLines, errorLines);
                bootResult.Metadata = new RequirementCheckMetadata
                {
                    Platform = platform.ToString(),
                    ConversationLogFile = conversationLogPath,
                    AgentConsoleLogPath = agentConsoleLogPath
                };
                return bootResult;
            }

            conversationLogWriter?.WriteLine("Health endpoint ready.");

            // Allow the agent message pipeline to finish initializing after the HTTP server is up.
            conversationLogWriter?.WriteLine($"Waiting {(int)PostHealthWarmupDelay.TotalSeconds}s for message pipeline warmup...");
            await Task.Delay(PostHealthWarmupDelay, cancellationToken);

            // Phase 2: Multi-turn conversation
            conversationLogWriter?.WriteLine($"\n[Phase 2] Starting {conversationPrompts.Length}-turn conversation...");
            var turns = new List<ConversationTurnData>();
            var allOk = true;

            for (int i = 0; i < conversationPrompts.Length; i++)
            {
                if (process.HasExited)
                {
                    var exitOutput = GetCapturedOutput(outputLines, errorLines);
                    conversationLogWriter?.WriteLine($"\nFAILED: Agent process exited during turn {i + 1}");
                    WriteAgentOutputToConversationLog(conversationLogWriter, outputLines, errorLines);
                    return new RequirementCheckResult
                    {
                        Passed = false,
                        ErrorMessage = $"Agent process exited during conversation (turn {i + 1}):\n{exitOutput}",
                        ResolutionGuidance = GetRunGuidance(platform),
                        Metadata = new RequirementCheckMetadata
                        {
                            Platform = platform.ToString(),
                            ConversationLogFile = conversationLogPath,
                            AgentConsoleLogPath = agentConsoleLogPath
                        }
                    };
                }

                var turnResult = await SendTurnWithRetryAsync(messagesUrl, conversationId, conversationPrompts[i], i, port, receiver, logger, conversationLogWriter, cancellationToken);
                turns.Add(turnResult);
                LogTurn(conversationLogWriter, i + 1, turnResult);

                if (!turnResult.Ok)
                {
                    allOk = false;
                    // Continue to remaining turns for a complete report
                }
            }

            var turnSummary = $"{turns.Count(t => t.Ok)}/{turns.Count} turns succeeded";
            var respondedCount = turns.Count(t => t.AgentResponded == true);
            var trackedCount = turns.Count(t => t.AgentResponded is not null);
            if (trackedCount > 0)
            {
                turnSummary += $", {respondedCount}/{trackedCount} agent responses received";
            }

            // Phase 3: Launch AgentsPlayground for interactive testing if requested
            bool playgroundLaunched = false;
            if (_launchPlayground && !process.HasExited)
            {
                playgroundLaunched = await LaunchPlaygroundAsync(messagesUrl, logger, cancellationToken);
            }

            conversationLogWriter?.WriteLine($"\n[Summary] {turnSummary}");
            conversationLogWriter?.WriteLine($"Result: {(allOk ? "PASSED" : "FAILED")}");
            WriteAgentOutputToConversationLog(conversationLogWriter, outputLines, errorLines);

            return new RequirementCheckResult
            {
                Passed = allOk,
                ErrorMessage = allOk ? null : $"Conversation validation failed: {turnSummary}",
                ResolutionGuidance = allOk ? null : GetConversationGuidance(turns, platform),
                Details = turnSummary,
                Metadata = new RequirementCheckMetadata
                {
                    Port = port,
                    Platform = platform.ToString(),
                    PlaygroundLaunched = playgroundLaunched ? true : null,
                    AgentConsoleLogPath = agentConsoleLogPath,
                    ConversationLogFile = conversationLogPath,
                    Turns = turns.Select(t => new ConversationTurnMetadata
                    {
                        Input = t.Input,
                        StatusCode = t.StatusCode,
                        ResponseSnippet = t.ResponseSnippet,
                        LatencyMs = t.LatencyMs,
                        Ok = t.Ok,
                        Error = t.Error,
                        AgentResponded = t.AgentResponded,
                        AgentResponseText = t.AgentResponseText
                    }).ToList()
                }
            };
        }
        finally
        {
            conversationLogWriter?.Dispose();
            consoleLogWriter?.Dispose();

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

            if (ownedReceiver && receiver is not null)
            {
                try
                {
                    await receiver.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to dispose callback receiver during cleanup");
                }
            }
        }
    }

    private async Task<RequirementCheckResult?> WaitForHealthAsync(
        Process process,
        string healthUrl,
        ProjectPlatform platform,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(StartupTimeout);

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                return RequirementCheckResult.Failure(
                    $"App exited with code {process.ExitCode} before health endpoint responded",
                    GetRunGuidance(platform));
            }

            try
            {
                using var response = await _httpClient.GetAsync(healthUrl, timeoutCts.Token);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogDebug("Health endpoint ready, starting conversation");
                    return null; // Ready
                }
            }
            catch (HttpRequestException)
            {
                // App not ready yet
            }
            catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
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

        return RequirementCheckResult.Failure(
            $"App did not respond on {healthUrl} within {(int)StartupTimeout.TotalSeconds} seconds",
            GetRunGuidance(platform));
    }

    /// <summary>
    /// Sends a conversation turn with retry logic for transient failures.
    /// Agents may not have their message pipeline fully ready even after the health endpoint responds.
    /// </summary>
    private async Task<ConversationTurnData> SendTurnWithRetryAsync(
        string messagesUrl,
        string conversationId,
        string text,
        int turnIndex,
        int port,
        IBotCallbackReceiver? callbackReceiver,
        ILogger logger,
        StreamWriter? conversationLogWriter,
        CancellationToken cancellationToken)
    {
        ConversationTurnData? lastResult = null;

        for (int attempt = 0; attempt <= MaxTurnRetries; attempt++)
        {
            lastResult = await SendTurnAsync(messagesUrl, conversationId, text, turnIndex, port, callbackReceiver, logger, cancellationToken);

            if (lastResult.Ok || !IsTransientFailure(lastResult))
            {
                return lastResult;
            }

            if (attempt < MaxTurnRetries)
            {
                logger.LogDebug(
                    "Turn {Turn} failed with transient error, retrying in {Delay}s (attempt {Attempt}/{Max}): {Error}",
                    turnIndex + 1, (int)TurnRetryDelay.TotalSeconds, attempt + 1, MaxTurnRetries, lastResult.Error);
                conversationLogWriter?.WriteLine(
                    $"  [Retry] Turn {turnIndex + 1} failed ({lastResult.Error}), retrying in {(int)TurnRetryDelay.TotalSeconds}s...");
                await Task.Delay(TurnRetryDelay, cancellationToken);
            }
        }

        return lastResult!;
    }

    /// <summary>
    /// Determines if a turn failure is transient and worth retrying.
    /// Connection failures and server errors (5xx) are transient.
    /// Auth failures (401/403) and client errors (4xx) are not.
    /// </summary>
    private static bool IsTransientFailure(ConversationTurnData turnResult)
    {
        if (turnResult.Error?.StartsWith("Connection failed", StringComparison.Ordinal) == true)
        {
            return true;
        }

        if (turnResult.Error?.StartsWith("Turn timed out", StringComparison.Ordinal) == true)
        {
            return true;
        }

        if (turnResult.StatusCode is >= 500 and < 600)
        {
            return true;
        }

        return false;
    }

    private async Task<ConversationTurnData> SendTurnAsync(
        string messagesUrl,
        string conversationId,
        string text,
        int turnIndex,
        int port,
        IBotCallbackReceiver? callbackReceiver,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // Clear previous responses before each turn
        callbackReceiver?.ClearResponses();

        var activity = new BotActivity
        {
            Type = "message",
            Id = Guid.NewGuid().ToString("N"),
            Text = text,
            From = new BotChannelAccount { Id = "validate-user", Name = "Validate" },
            Recipient = new BotChannelAccount { Id = "agent", Name = "Agent" },
            Conversation = new BotConversationAccount { Id = conversationId },
            ChannelId = "emulator",
            ServiceUrl = callbackReceiver?.ServiceUrl ?? $"http://localhost:{port}",
            Timestamp = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(activity, ActivitySerializerOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TurnTimeout);

            using var response = await _httpClient.PostAsync(messagesUrl, content, timeoutCts.Token);
            stopwatch.Stop();
            var latencyMs = stopwatch.ElapsedMilliseconds;
            var statusCode = (int)response.StatusCode;

            // Auth failure — distinct guidance
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ConversationTurnData
                {
                    Input = text,
                    StatusCode = statusCode,
                    LatencyMs = latencyMs,
                    Ok = false,
                    Error = $"Auth rejected (HTTP {statusCode}). Set channelId='emulator' bypass or requireAuth=false for local testing.",
                    AgentResponded = false
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ConversationTurnData
                {
                    Input = text,
                    StatusCode = statusCode,
                    LatencyMs = latencyMs,
                    Ok = false,
                    Error = $"HTTP {statusCode} from /api/messages",
                    AgentResponded = false
                };
            }

            // Try to read the HTTP response body (some bots return inline responses)
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var snippet = TruncateResponse(responseBody, 200);

            // Wait for agent to respond via the serviceUrl callback
            bool? agentResponded = null;
            string? agentResponseText = null;

            if (callbackReceiver is not null)
            {
                var botResponse = await callbackReceiver.WaitForResponseAsync(ResponseWaitTimeout, cancellationToken);
                agentResponded = botResponse is not null;
                agentResponseText = botResponse?.Text;

                if (agentResponseText is not null)
                {
                    agentResponseText = TruncateResponse(agentResponseText, 200);
                }
            }

            // In non-playground mode, require a valid agent response
            bool turnOk = true;
            string? turnError = null;

            if (!_launchPlayground && callbackReceiver is not null)
            {
                if (agentResponded != true)
                {
                    turnOk = false;
                    turnError = "Agent did not respond within timeout";
                }
                else if (IsErrorResponse(agentResponseText))
                {
                    turnOk = false;
                    turnError = $"Agent returned an error response: {agentResponseText}";
                }
            }

            logger.LogDebug(
                "Turn {Turn} ({Text}): HTTP {StatusCode}, latency {Latency}ms, agentResponded={AgentResponded}",
                turnIndex + 1, text, statusCode, latencyMs, agentResponded);

            return new ConversationTurnData
            {
                Input = text,
                StatusCode = statusCode,
                ResponseSnippet = string.IsNullOrWhiteSpace(responseBody) ? null : snippet,
                LatencyMs = latencyMs,
                Ok = turnOk,
                Error = turnError,
                AgentResponded = agentResponded,
                AgentResponseText = agentResponseText
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ConversationTurnData
            {
                Input = text,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                Ok = false,
                Error = $"Turn timed out after {(int)TurnTimeout.TotalSeconds} seconds",
                AgentResponded = false
            };
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return new ConversationTurnData
            {
                Input = text,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                Ok = false,
                Error = $"Connection failed: {ex.Message}",
                AgentResponded = false
            };
        }
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
                LocalRuntimeRequirementCheck.WrapForWindows(startInfo, "npm", "start");
                startInfo.EnvironmentVariables["PORT"] = port.ToString();
                break;

            case ProjectPlatform.Python:
                var entryPoint = LocalRuntimeRequirementCheck.ResolvePythonEntryPoint(projectPath);
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

    private static string GetRunGuidance(ProjectPlatform platform)
    {
        return platform switch
        {
            ProjectPlatform.DotNet => "Try running the app manually:\n" +
                "  dotnet run\n" +
                "Verify it starts and responds on /api/messages.",
            ProjectPlatform.NodeJs => "Try running the app manually:\n" +
                "  npm start\n" +
                "Verify it starts and responds on /api/messages.",
            ProjectPlatform.Python => "Try running the app manually:\n" +
                "  python <entry_point>.py  (or: uv run python <entry_point>.py)\n" +
                "Verify it starts and responds on /api/messages.",
            _ => "Try running the app manually and verify it responds on /api/messages."
        };
    }

    private static string GetConversationGuidance(List<ConversationTurnData> turns, ProjectPlatform platform)
    {
        var failedTurns = turns.Where(t => !t.Ok).ToList();
        var hasAuthFailure = failedTurns.Any(t => t.StatusCode is 401 or 403);

        if (hasAuthFailure)
        {
            return platform switch
            {
                ProjectPlatform.DotNet => "Local conversation validation requires auth bypass.\n" +
                    "  Ensure MapAgentApplicationEndpoints is called with requireAuth: false.",
                _ => "Local conversation validation requires auth bypass.\n" +
                    "  Use channelId 'emulator' or disable auth for local testing."
            };
        }

        return "Check the agent is handling /api/messages correctly.\n" +
            "  Try testing with AgentsPlayground:\n" +
            "  agentsplayground -e \"http://localhost:<port>/api/messages\" -c \"emulator\"";
    }

    /// <summary>
    /// Launches AgentsPlayground for interactive testing. Blocks until the user closes it.
    /// </summary>
    private async Task<bool> LaunchPlaygroundAsync(
        string messagesUrl,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Launching AgentsPlayground for interactive testing...");
        logger.LogInformation("  Endpoint: {MessagesUrl}", messagesUrl);
        logger.LogInformation("  Close the playground window or press Ctrl+C to continue validation.");

        var pgStartInfo = new ProcessStartInfo
        {
            FileName = "agentsplayground",
            Arguments = $"-e \"{messagesUrl}\" -c \"emulator\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        Process? playgroundProcess = null;
        try
        {
            playgroundProcess = _processService.Start(pgStartInfo);
            if (playgroundProcess is null)
            {
                logger.LogWarning(
                    "Could not start AgentsPlayground. Install with: npm install -g agentsplayground");
                return false;
            }

            playgroundProcess.BeginOutputReadLine();
            playgroundProcess.BeginErrorReadLine();

            await playgroundProcess.WaitForExitAsync(cancellationToken);
            logger.LogInformation("AgentsPlayground exited, continuing validation.");
            return true;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Playground session cancelled, continuing validation.");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not launch AgentsPlayground: {Message}", ex.Message);
            logger.LogWarning("  Install with: npm install -g agentsplayground");
            return false;
        }
        finally
        {
            if (playgroundProcess is not null)
            {
                try
                {
                    if (!playgroundProcess.HasExited)
                    {
                        playgroundProcess.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Best effort
                }

                playgroundProcess.Dispose();
            }
        }
    }

    private static string GetCapturedOutput(
        LocalRuntimeRequirementCheck.BoundedLineBuffer outputLines,
        LocalRuntimeRequirementCheck.BoundedLineBuffer errorLines)
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

    private static void LogTurn(StreamWriter? writer, int turnNumber, ConversationTurnData turn)
    {
        if (writer is null) return;

        try
        {
            writer.WriteLine($"\n  Turn {turnNumber}: \"{turn.Input}\"");
            writer.WriteLine($"    Status: HTTP {turn.StatusCode?.ToString() ?? "N/A"}, Latency: {turn.LatencyMs}ms, Ok: {turn.Ok}");

            if (turn.ResponseSnippet is not null)
            {
                writer.WriteLine($"    Response: {turn.ResponseSnippet}");
            }

            if (turn.AgentResponded is not null)
            {
                writer.WriteLine($"    Agent responded: {turn.AgentResponded}");
            }

            if (turn.AgentResponseText is not null)
            {
                writer.WriteLine($"    Agent response: {turn.AgentResponseText}");
            }

            if (turn.Error is not null)
            {
                writer.WriteLine($"    Error: {turn.Error}");
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static void WriteAgentOutputToConversationLog(
        StreamWriter? writer,
        LocalRuntimeRequirementCheck.BoundedLineBuffer outputLines,
        LocalRuntimeRequirementCheck.BoundedLineBuffer errorLines)
    {
        if (writer is null) return;

        try
        {
            var stdout = outputLines.GetLines();
            var stderr = errorLines.GetLines();

            if (stdout.Length > 0 || stderr.Length > 0)
            {
                writer.WriteLine($"\n{new string('-', 60)}");
                writer.WriteLine("[Agent console output]");
            }

            if (stdout.Length > 0)
            {
                foreach (var line in stdout)
                    writer.WriteLine(line);
            }

            if (stderr.Length > 0)
            {
                writer.WriteLine("[stderr]");
                foreach (var line in stderr)
                    writer.WriteLine(line);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static string TruncateResponse(string response, int maxLength)
    {
        if (string.IsNullOrEmpty(response)) return string.Empty;
        return response.Length <= maxLength
            ? response
            : response[..maxLength] + "...";
    }

    /// <summary>
    /// Detects error responses using structural signals (stack traces, HTTP error codes)
    /// rather than keyword matching, to avoid false positives on legitimate agent replies
    /// that mention words like "error" or "not found" in conversational context.
    /// </summary>
    private static bool IsErrorResponse(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        var lower = responseText.ToLowerInvariant();

        // Detect stack traces (e.g., "   at Namespace.Class.Method()")
        if (System.Text.RegularExpressions.Regex.IsMatch(responseText, @"^\s+at\s+\S+\.\S+\(", System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            return true;
        }

        // Detect unhandled exception headers (e.g., "System.InvalidOperationException:")
        if (System.Text.RegularExpressions.Regex.IsMatch(responseText, @"\b\w+Exception\s*:", System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            return true;
        }

        // Detect HTTP 4xx/5xx status codes
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"http\s*[45]\d{2}"))
        {
            return true;
        }

        // Detect "internal server error" as a specific, unambiguous signal
        if (lower.Contains("internal server error"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns deploymentProjectPath if configured, otherwise falls back to the current directory.
    /// </summary>
    internal static string ResolveProjectPath(Agent365Config config)
    {
        return string.IsNullOrWhiteSpace(config.DeploymentProjectPath)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(config.DeploymentProjectPath);
    }

    private static readonly JsonSerializerOptions ActivitySerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Internal data structure for tracking turn results during execution.
    /// </summary>
    internal sealed class ConversationTurnData
    {
        public string Input { get; init; } = string.Empty;
        public int? StatusCode { get; init; }
        public string? ResponseSnippet { get; init; }
        public long? LatencyMs { get; init; }
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public bool? AgentResponded { get; init; }
        public string? AgentResponseText { get; init; }
    }

    /// <summary>
    /// Minimal Bot Framework Activity model for local validation.
    /// </summary>
    private sealed class BotActivity
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "message";

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("from")]
        public BotChannelAccount? From { get; set; }

        [JsonPropertyName("recipient")]
        public BotChannelAccount? Recipient { get; set; }

        [JsonPropertyName("conversation")]
        public BotConversationAccount? Conversation { get; set; }

        [JsonPropertyName("channelId")]
        public string? ChannelId { get; set; }

        [JsonPropertyName("serviceUrl")]
        public string? ServiceUrl { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset? Timestamp { get; set; }
    }

    private sealed class BotChannelAccount
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class BotConversationAccount
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }
}
