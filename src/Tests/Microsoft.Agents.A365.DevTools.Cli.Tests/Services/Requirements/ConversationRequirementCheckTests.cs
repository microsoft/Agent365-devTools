// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Agents.A365.DevTools.Validation;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Requirements;

public class ConversationRequirementCheckTests : IDisposable
{
    private readonly ILogger _logger;
    private readonly PlatformDetector _platformDetector;
    private readonly IProcessService _processService;
    private readonly string _tempDir;

    public ConversationRequirementCheckTests()
    {
        _logger = Substitute.For<ILogger>();
        _platformDetector = new PlatformDetector(Substitute.For<ILogger<PlatformDetector>>());
        _processService = Substitute.For<IProcessService>();
        _tempDir = Path.Combine(Path.GetTempPath(), $"a365-conversation-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private ConversationRequirementCheck CreateCheck(
        HttpMessageHandler? handler = null,
        IBotCallbackReceiver? callbackReceiver = null,
        bool launchPlayground = false)
    {
        var httpClient = handler is not null ? new HttpClient(handler) : new HttpClient();
        return new ConversationRequirementCheck(
            _platformDetector, _processService, httpClient, callbackReceiver, launchPlayground);
    }

    [Fact]
    public void Check_HasExpectedMetadata()
    {
        var check = CreateCheck();
        check.Name.Should().Be("Conversation");
        check.Category.Should().Be("Code Health");
        check.Description.Should().Contain("/api/messages");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CheckAsync_WhenDeploymentProjectPathIsEmpty_FallsBackToCwd(string? path)
    {
        var check = CreateCheck();
        var config = new Agent365Config { DeploymentProjectPath = path ?? string.Empty };

        var result = await check.CheckAsync(config, _logger);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task CheckAsync_WhenDirectoryDoesNotExist_ReturnsFailure()
    {
        var check = CreateCheck();
        var config = new Agent365Config { DeploymentProjectPath = Path.Combine(_tempDir, "nonexistent") };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "a non-existent directory cannot run an app");
        result.ErrorMessage.Should().Contain("does not exist");
    }

    [Fact]
    public async Task CheckAsync_WhenPlatformIsUnknown_ReturnsWarning()
    {
        var check = CreateCheck();
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(because: "unknown platform is a non-blocking warning");
        result.IsWarning.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_WhenProcessFailsToStart_ReturnsFailure()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns((Process?)null);
        var check = CreateCheck();
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "a process that fails to start cannot handle conversations");
        result.ErrorMessage.Should().Contain("Failed to start");
    }

    [Fact]
    public async Task CheckAsync_WhenAllTurnsSucceed_ReturnsSuccess()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK,
            responseBody: "{\"type\":\"message\",\"text\":\"Hello!\"}");
        var fakeReceiver = new FakeBotCallbackReceiver(
            new BotCallbackResponse("Hello!", "message"));
        var check = CreateCheck(handler, fakeReceiver);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(because: "all conversation turns returned 200");
        result.Details.Should().Contain("3/3 turns succeeded");
        result.Metadata.Should().NotBeNull();
        var meta = (RequirementCheckMetadata)result.Metadata!;
        meta.Turns.Should().HaveCount(3);
        meta.Turns!.Should().OnlyContain(t => t.Ok, because: "every turn should report success");
    }

    [Fact]
    public async Task CheckAsync_WhenTurnReturnsAuthFailure_ReturnsFailureWithGuidance()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.Unauthorized);
        var check = CreateCheck(handler);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "auth failures should block the conversation tier");
        ((RequirementCheckMetadata)result.Metadata!).Turns!.Should().Contain(t => t.Error != null && t.Error.Contains("Auth rejected"),
            because: "auth failures should report targeted guidance");
    }

    [Fact]
    public async Task CheckAsync_WhenTurnReturns500_ReturnsFailure()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.InternalServerError);
        var check = CreateCheck(handler);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "server errors should fail the conversation tier");
        ((RequirementCheckMetadata)result.Metadata!).Turns!.Should().Contain(t => !t.Ok);
    }

    [Fact]
    public async Task CheckAsync_WhenProcessExitsDuringConversation_ReturnsFailure()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        // Health returns OK, then process exits during message turn
        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK,
            killProcessOnMessage: fakeProcess);
        var check = CreateCheck(handler);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "the process crashed during conversation");
        result.ErrorMessage.Should().Contain("exited during conversation");
    }

    [Fact]
    public async Task CheckAsync_WhenHealthTimesOut_ReturnsFailure()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        // Health never returns success
        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.ServiceUnavailable,
            messagesStatusCode: HttpStatusCode.OK);
        var check = CreateCheck(handler);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "the app never became ready");
        result.ErrorMessage.Should().Contain("did not respond");
    }

    [Fact]
    public async Task CheckAsync_SuccessfulTurns_IncludeLatencyAndSnippet()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK,
            responseBody: "{\"type\":\"message\",\"text\":\"I can help you\"}");
        var fakeReceiver = new FakeBotCallbackReceiver(
            new BotCallbackResponse("I can help you", "message"));
        var check = CreateCheck(handler, fakeReceiver);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue();
        var turn = ((RequirementCheckMetadata)result.Metadata!).Turns!.First();
        turn.LatencyMs.Should().NotBeNull(because: "latency should always be captured");
        turn.StatusCode.Should().Be(200);
        turn.ResponseSnippet.Should().Contain("I can help you");
    }

    [Fact]
    public async Task CheckAsync_NodeJsProject_UsesNpmStart()
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), "{}");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK);
        var check = CreateCheck(handler);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        await check.CheckAsync(config, _logger);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _processService.Received(1).Start(Arg.Is<ProcessStartInfo>(p =>
                p.FileName == "cmd.exe" && p.Arguments == "/c npm start"));
        }
        else
        {
            _processService.Received(1).Start(Arg.Is<ProcessStartInfo>(p =>
                p.FileName == "npm" && p.Arguments == "start"));
        }
    }

    [Fact]
    public async Task CheckAsync_ContinuesAllTurnsEvenOnFailure()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        // First turn fails, rest succeed — all 3 should be in the report
        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK,
            failFirstTurn: true);
        var check = CreateCheck(handler);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "at least one turn failed");
        ((RequirementCheckMetadata)result.Metadata!).Turns.Should().HaveCount(3,
            because: "all turns should be attempted even if one fails for complete reporting");
    }

    [Fact]
    public async Task CheckAsync_WhenCallbackReceiverProvided_TracksAgentResponded()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK,
            responseBody: "{\"type\":\"message\",\"text\":\"Hello!\"}");
        var fakeReceiver = new FakeBotCallbackReceiver(
            new BotCallbackResponse("I can help you with that!", "message"));
        var check = CreateCheck(handler, fakeReceiver);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(because: "all turns succeeded with agent responses");
        var meta = (RequirementCheckMetadata)result.Metadata!;
        meta.Turns.Should().OnlyContain(
            t => t.AgentResponded == true,
            because: "callback receiver reported agent responses for every turn");
        meta.Turns.Should().OnlyContain(
            t => t.AgentResponseText == "I can help you with that!",
            because: "agent response text should be captured from callback");
    }

    [Fact]
    public async Task CheckAsync_WhenAgentDoesNotRespond_ReturnsFailure()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK);
        var fakeReceiver = new FakeBotCallbackReceiver(response: null);
        var check = CreateCheck(handler, fakeReceiver);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(
            because: "in non-playground mode, agent must respond for turn to pass");
        var meta = (RequirementCheckMetadata)result.Metadata!;
        meta.Turns.Should().OnlyContain(
            t => t.AgentResponded == false,
            because: "callback receiver returned no response");
        meta.Turns.Should().OnlyContain(
            t => t.Ok == false && t.Error!.Contains("did not respond"),
            because: "each turn should report agent did not respond");
    }

    [Fact]
    public async Task CheckAsync_WhenNoCallbackReceiverInjected_AutoCreatesReceiverAndFailsWithoutResponse()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK);
        // No callback receiver injected — code auto-creates one internally.
        // Since the mock handler does not call back, agentResponded will be false → failure.
        var check = CreateCheck(handler, callbackReceiver: null);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(
            because: "auto-created receiver gets no callback from mock handler so turns fail");
        ((RequirementCheckMetadata)result.Metadata!).Turns.Should().OnlyContain(
            t => t.AgentResponded == false,
            because: "auto-created receiver gets no callback from mock handler so agentResponded is false");
    }

    [Fact]
    public async Task CheckAsync_WhenAgentReturnsErrorResponse_ReturnsFailure()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK);
        var fakeReceiver = new FakeBotCallbackReceiver(
            new BotCallbackResponse("An internal server error occurred while processing your request", "message"));
        var check = CreateCheck(handler, fakeReceiver);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(
            because: "agent responded with an error message");
        ((RequirementCheckMetadata)result.Metadata!).Turns.Should().OnlyContain(
            t => t.Ok == false && t.Error!.Contains("error response"),
            because: "each turn should report agent returned an error");
    }

    [Fact]
    public async Task CheckAsync_InPlaygroundMode_PassesEvenWithoutAgentResponse()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        var fakePlayground = CreateFakeProcess(exitImmediately: true);
        _processService.Start(Arg.Any<ProcessStartInfo>())
            .Returns(fakeProcess, fakePlayground);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK);
        var fakeReceiver = new FakeBotCallbackReceiver(response: null);
        var check = CreateCheck(handler, fakeReceiver, launchPlayground: true);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(
            because: "in playground mode, missing agent response does not fail the turn");
        ((RequirementCheckMetadata)result.Metadata!).Turns.Should().OnlyContain(
            t => t.Ok == true,
            because: "playground mode is lenient about agent responses");
    }

    [Fact]
    public async Task CheckAsync_DetailsSummaryIncludesAgentResponseCount()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK);
        var fakeReceiver = new FakeBotCallbackReceiver(
            new BotCallbackResponse("Hi there", "message"));
        var check = CreateCheck(handler, fakeReceiver);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Details.Should().Contain("agent responses received",
            because: "details should summarize how many agent responses were captured");
    }

    [Fact]
    public async Task CheckAsync_WhenAuthFailure_AgentRespondedIsFalse()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.Unauthorized);
        var fakeReceiver = new FakeBotCallbackReceiver(
            new BotCallbackResponse("should not appear", "message"));
        var check = CreateCheck(handler, fakeReceiver);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse();
        ((RequirementCheckMetadata)result.Metadata!).Turns.Should().OnlyContain(
            t => t.AgentResponded == false,
            because: "auth failures should report agent did not respond, not attempt callback wait");
    }

    [Fact]
    public async Task CheckAsync_ServiceUrlPointsToCallbackReceiver()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        string? capturedServiceUrl = null;
        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK,
            captureServiceUrl: url => capturedServiceUrl = url);
        var fakeReceiver = new FakeBotCallbackReceiver(response: null);
        var check = CreateCheck(handler, fakeReceiver);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        await check.CheckAsync(config, _logger);

        capturedServiceUrl.Should().Be(fakeReceiver.ServiceUrl,
            because: "activities should use the callback receiver's URL so the bot sends responses there");
    }

    [Fact]
    public async Task CheckAsync_WhenPlaygroundEnabled_LaunchesAgentsPlayground()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeAgentProcess = CreateFakeProcess(exitImmediately: false);
        var fakePlaygroundProcess = CreateFakeProcess(exitImmediately: true);

        // First Start call returns the agent process, second returns the playground process
        _processService.Start(Arg.Any<ProcessStartInfo>())
            .Returns(fakeAgentProcess, fakePlaygroundProcess);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK);
        var fakeReceiver = new FakeBotCallbackReceiver(
            new BotCallbackResponse("Hello!", "message"));
        var check = CreateCheck(handler, fakeReceiver, launchPlayground: true);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue();
        ((RequirementCheckMetadata)result.Metadata!).PlaygroundLaunched.Should().BeTrue(
            because: "playground was requested and started successfully");
        _processService.Received(2).Start(Arg.Any<ProcessStartInfo>());
        _processService.Received(1).Start(Arg.Is<ProcessStartInfo>(p =>
            p.FileName == "agentsplayground" && p.Arguments.Contains("-c \"emulator\"")));
    }

    [Fact]
    public async Task CheckAsync_WhenPlaygroundDisabled_DoesNotLaunchPlayground()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK);
        var fakeReceiver = new FakeBotCallbackReceiver(
            new BotCallbackResponse("Hello!", "message"));
        var check = CreateCheck(handler, fakeReceiver, launchPlayground: false);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue();
        ((RequirementCheckMetadata)result.Metadata!).PlaygroundLaunched.Should().BeNull(
            because: "playground was not requested");
        // Only one Start call for the agent process
        _processService.Received(1).Start(Arg.Any<ProcessStartInfo>());
    }

    [Fact]
    public async Task CheckAsync_WhenPlaygroundFailsToStart_ReturnsSuccessWithoutPlayground()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeAgentProcess = CreateFakeProcess(exitImmediately: false);

        // Agent process starts, playground fails to start
        _processService.Start(Arg.Any<ProcessStartInfo>())
            .Returns(fakeAgentProcess, (Process?)null);

        var handler = new ConversationHttpHandler(
            healthStatusCode: HttpStatusCode.OK,
            messagesStatusCode: HttpStatusCode.OK);
        var fakeReceiver = new FakeBotCallbackReceiver(
            new BotCallbackResponse("Hello!", "message"));
        var check = CreateCheck(handler, fakeReceiver, launchPlayground: true);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(
            because: "playground failure should not block conversation validation");
        ((RequirementCheckMetadata)result.Metadata!).PlaygroundLaunched.Should().BeNull(
            because: "playground failed to start so it should not be reported as launched");
    }

    /// <summary>
    /// Creates a fake Process for testing.
    /// </summary>
    private static Process CreateFakeProcess(bool exitImmediately, int exitCode = 0)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows()
                ? (exitImmediately ? "/c exit 1" : "/c ping -n 60 127.0.0.1 >nul")
                : (exitImmediately ? $"-c \"exit {exitCode}\"" : "-c \"sleep 60\""),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo)!;

        if (exitImmediately)
        {
            process.WaitForExit(5000);
        }

        return process;
    }

    /// <summary>
    /// Fake callback receiver for testing. Returns a configurable response.
    /// </summary>
    private sealed class FakeBotCallbackReceiver : IBotCallbackReceiver
    {
        private readonly BotCallbackResponse? _response;

        public FakeBotCallbackReceiver(BotCallbackResponse? response = null)
        {
            _response = response;
        }

        public string ServiceUrl => "http://localhost:39999";

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<BotCallbackResponse?> WaitForResponseAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_response);
        }

        public void ClearResponses() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// HTTP handler that simulates both health and /api/messages endpoints.
    /// </summary>
    private sealed class ConversationHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _healthStatusCode;
        private readonly HttpStatusCode _messagesStatusCode;
        private readonly string? _responseBody;
        private readonly Process? _killProcessOnMessage;
        private readonly bool _failFirstTurn;
        private readonly Action<string>? _captureServiceUrl;
        private int _messageCount;

        public ConversationHttpHandler(
            HttpStatusCode healthStatusCode,
            HttpStatusCode messagesStatusCode,
            string? responseBody = null,
            Process? killProcessOnMessage = null,
            bool failFirstTurn = false,
            Action<string>? captureServiceUrl = null)
        {
            _healthStatusCode = healthStatusCode;
            _messagesStatusCode = messagesStatusCode;
            _responseBody = responseBody;
            _killProcessOnMessage = killProcessOnMessage;
            _failFirstTurn = failFirstTurn;
            _captureServiceUrl = captureServiceUrl;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Contains("/api/health"))
            {
                return new HttpResponseMessage(_healthStatusCode);
            }

            if (path.Contains("/api/messages"))
            {
                _messageCount++;

                // Capture serviceUrl from the activity body if requested
                if (_captureServiceUrl is not null && request.Content is not null)
                {
                    var body = await request.Content.ReadAsStringAsync(cancellationToken);
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("serviceUrl", out var prop))
                        {
                            _captureServiceUrl(prop.GetString()!);
                        }
                    }
                    catch
                    {
                        // Best effort
                    }
                }

                if (_killProcessOnMessage is not null)
                {
                    try
                    {
                        if (!_killProcessOnMessage.HasExited)
                        {
                            _killProcessOnMessage.Kill(entireProcessTree: true);
                            _killProcessOnMessage.WaitForExit(5000);
                        }
                    }
                    catch
                    {
                        // Best effort
                    }
                }

                if (_failFirstTurn && _messageCount == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                var response = new HttpResponseMessage(_messagesStatusCode);
                if (_responseBody is not null)
                {
                    response.Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json");
                }
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public void BuildToolInvocationPrompt_NoManifest_ReturnsFallback()
    {
        var prompt = ConversationRequirementCheck.BuildToolInvocationPrompt(_tempDir, _logger);
        prompt.Should().Be(ConversationRequirementCheck.FallbackToolPrompt,
            because: "no ToolingManifest.json means we fall back to the default prompt");
    }

    [Fact]
    public void BuildToolInvocationPrompt_EmptyServers_ReturnsFallback()
    {
        var manifest = new { mcpServers = Array.Empty<object>() };
        File.WriteAllText(
            Path.Combine(_tempDir, "ToolingManifest.json"),
            JsonSerializer.Serialize(manifest));

        var prompt = ConversationRequirementCheck.BuildToolInvocationPrompt(_tempDir, _logger);
        prompt.Should().Be(ConversationRequirementCheck.FallbackToolPrompt,
            because: "an empty mcpServers array means no tools to invoke");
    }

    [Fact]
    public void BuildToolInvocationPrompt_InvalidJson_ReturnsFallback()
    {
        File.WriteAllText(
            Path.Combine(_tempDir, "ToolingManifest.json"),
            "{ not valid json }}}");

        var prompt = ConversationRequirementCheck.BuildToolInvocationPrompt(_tempDir, _logger);
        prompt.Should().Be(ConversationRequirementCheck.FallbackToolPrompt,
            because: "malformed JSON should not crash the check");
    }

    [Fact]
    public void BuildToolInvocationPrompt_WithKnownTool_ReturnsNaturalQuestion()
    {
        var manifest = new
        {
            mcpServers = new[]
            {
                new { mcpServerName = "Mail", url = "https://example.com/mail" }
            }
        };
        File.WriteAllText(
            Path.Combine(_tempDir, "ToolingManifest.json"),
            JsonSerializer.Serialize(manifest));

        var prompt = ConversationRequirementCheck.BuildToolInvocationPrompt(_tempDir, _logger);
        prompt.Should().Be("Get me my recent emails",
            because: "Mail is a known tool with a mapped natural-language question");
    }

    [Fact]
    public void BuildToolInvocationPrompt_MultipleTools_UsesFirstTool()
    {
        var manifest = new
        {
            mcpServers = new[]
            {
                new { mcpServerName = "Calendar", url = "https://example.com/cal" },
                new { mcpServerName = "Mail", url = "https://example.com/mail" }
            }
        };
        File.WriteAllText(
            Path.Combine(_tempDir, "ToolingManifest.json"),
            JsonSerializer.Serialize(manifest));

        var prompt = ConversationRequirementCheck.BuildToolInvocationPrompt(_tempDir, _logger);
        prompt.Should().Be("What meetings do I have today?",
            because: "Calendar is first and is a known tool");
    }

    [Fact]
    public void BuildToolInvocationPrompt_UnknownToolWithDescription_UsesDescription()
    {
        var manifest = new
        {
            mcpServers = new[]
            {
                new { mcpServerName = "CustomCRM", url = "https://example.com/crm", description = "Manage customer relationships." }
            }
        };
        File.WriteAllText(
            Path.Combine(_tempDir, "ToolingManifest.json"),
            JsonSerializer.Serialize(manifest));

        var prompt = ConversationRequirementCheck.BuildToolInvocationPrompt(_tempDir, _logger);
        prompt.Should().Be("Help me with Manage customer relationships",
            because: "unknown tools with a description fall back to description-based prompt");
    }

    [Fact]
    public void BuildToolInvocationPrompt_UnknownToolNoDescription_UsesToolName()
    {
        var manifest = new
        {
            mcpServers = new[]
            {
                new { mcpServerName = "CustomCRM", url = "https://example.com/crm" }
            }
        };
        File.WriteAllText(
            Path.Combine(_tempDir, "ToolingManifest.json"),
            JsonSerializer.Serialize(manifest));

        var prompt = ConversationRequirementCheck.BuildToolInvocationPrompt(_tempDir, _logger);
        prompt.Should().Be("Help me with CustomCRM",
            because: "unknown tools without a description fall back to name-based prompt");
    }

    [Fact]
    public void BuildToolInvocationPrompt_KnownToolCaseInsensitive_ReturnsNaturalQuestion()
    {
        var manifest = new
        {
            mcpServers = new[]
            {
                new { mcpServerName = "SHAREPOINT", url = "https://example.com/sp" }
            }
        };
        File.WriteAllText(
            Path.Combine(_tempDir, "ToolingManifest.json"),
            JsonSerializer.Serialize(manifest));

        var prompt = ConversationRequirementCheck.BuildToolInvocationPrompt(_tempDir, _logger);
        prompt.Should().Be("Get me my recent SharePoint files",
            because: "tool name matching should be case-insensitive");
    }

    [Fact]
    public void BuildToolInvocationPrompt_ContainsKnownKeyword_ReturnsNaturalQuestion()
    {
        var manifest = new
        {
            mcpServers = new[]
            {
                new { mcpServerName = "M365SharePoint", url = "https://example.com/sp" }
            }
        };
        File.WriteAllText(
            Path.Combine(_tempDir, "ToolingManifest.json"),
            JsonSerializer.Serialize(manifest));

        var prompt = ConversationRequirementCheck.BuildToolInvocationPrompt(_tempDir, _logger);
        prompt.Should().Be("Get me my recent SharePoint files",
            because: "tool name containing a known keyword should match via contains");
    }

    [Fact]
    public void BuildConversationPrompts_NoManifest_ReturnsDefaults()
    {
        var prompts = ConversationRequirementCheck.BuildConversationPrompts(_tempDir, _logger);
        prompts.Should().HaveCount(3);
        prompts[0].Should().Be("Hello");
        prompts[1].Should().Be("What can you do?",
            because: "without a manifest the fallback prompt is used");
        prompts[2].Should().Be("Thanks");
    }

    [Fact]
    public void BuildConversationPrompts_WithManifest_ReplacesMiddleTurn()
    {
        var manifest = new
        {
            mcpServers = new[]
            {
                new { mcpServerName = "Mail", url = "https://example.com/mail" }
            }
        };
        File.WriteAllText(
            Path.Combine(_tempDir, "ToolingManifest.json"),
            JsonSerializer.Serialize(manifest));

        var prompts = ConversationRequirementCheck.BuildConversationPrompts(_tempDir, _logger);
        prompts.Should().HaveCount(3);
        prompts[0].Should().Be("Hello");
        prompts[1].Should().Be("Get me my recent emails",
            because: "the middle turn should be a natural question that triggers the Mail tool");
        prompts[2].Should().Be("Thanks");
    }
}
