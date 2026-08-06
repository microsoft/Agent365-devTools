// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Requirements;

public class LocalRuntimeRequirementCheckTests : IDisposable
{
    private readonly ILogger _logger;
    private readonly PlatformDetector _platformDetector;
    private readonly IProcessService _processService;
    private readonly string _tempDir;
    private readonly List<IDisposable> _disposables = new();

    public LocalRuntimeRequirementCheckTests()
    {
        _logger = Substitute.For<ILogger>();
        _platformDetector = new PlatformDetector(Substitute.For<ILogger<PlatformDetector>>());
        _processService = Substitute.For<IProcessService>();
        _tempDir = Path.Combine(Path.GetTempPath(), $"a365-runtime-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private LocalRuntimeRequirementCheck CreateCheck(HttpMessageHandler? handler = null)
    {
        var httpClient = handler is not null ? new HttpClient(handler) : new HttpClient();
        _disposables.Add(httpClient);
        var check = new LocalRuntimeRequirementCheck(_platformDetector, _processService, httpClient);
        _disposables.Add(check);
        return check;
    }

    [Fact]
    public void Check_HasExpectedMetadata()
    {
        var check = CreateCheck();
        check.Name.Should().Be("Local Runtime");
        check.Category.Should().Be("Code Health");
        check.Description.Should().Contain("health endpoint");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CheckAsync_WhenDeploymentProjectPathIsEmpty_FallsBackToCwd(string? path)
    {
        // Arrange - when deploymentProjectPath is empty, falls back to CWD
        var check = CreateCheck();
        var config = new Agent365Config { DeploymentProjectPath = path ?? string.Empty };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert - should not crash; CWD may or may not have a recognized project
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task CheckAsync_WhenDeploymentProjectPathIsInvalid_ReturnsFailure()
    {
        // Arrange
        var check = CreateCheck();
        var config = new Agent365Config { DeploymentProjectPath = "path\0with\0nulls" };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert - Path.GetFullPath throws, caught by ExecuteCheckWithLoggingAsync
        result.Passed.Should().BeFalse(because: "an invalid path format should be reported as a failure");
    }

    [Fact]
    public async Task CheckAsync_WhenDirectoryDoesNotExist_ReturnsFailure()
    {
        // Arrange
        var check = CreateCheck();
        var config = new Agent365Config { DeploymentProjectPath = Path.Combine(_tempDir, "nonexistent") };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: "a non-existent directory cannot run an app");
        result.ErrorMessage.Should().Contain("does not exist");
    }

    [Fact]
    public async Task CheckAsync_WhenPlatformIsUnknown_ReturnsWarning()
    {
        // Arrange - empty directory, PlatformDetector returns Unknown
        var check = CreateCheck();
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeTrue(because: "unknown platform is a non-blocking warning");
        result.IsWarning.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_WhenProcessFailsToStart_ReturnsFailure()
    {
        // Arrange - create a .csproj so platform is detected as DotNet
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns((Process?)null);
        var check = CreateCheck();
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: "a process that fails to start cannot serve health requests");
        result.ErrorMessage.Should().Contain("Failed to start");
    }

    [Fact]
    public async Task CheckAsync_WhenHealthEndpointResponds200_ReturnsSuccess()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new FakeHttpHandler(HttpStatusCode.OK);
        var check = CreateCheck(handler);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeTrue(because: "a health endpoint returning 200 means the app is running");
        result.Details.Should().Contain("200");
    }

    [Fact]
    public async Task CheckAsync_WhenProcessExitsEarly_ReturnsFailure()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var fakeProcess = CreateFakeProcess(exitImmediately: true, exitCode: 1);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new FakeHttpHandler(HttpStatusCode.OK);
        var check = CreateCheck(handler);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: "an early exit means the app crashed before responding");
        result.ErrorMessage.Should().Contain("exited early");
    }

    [Fact]
    public void ResolvePort_WithLaunchSettings_ReturnsConfiguredPort()
    {
        var propsDir = Directory.CreateDirectory(Path.Combine(_tempDir, "Properties"));
        File.WriteAllText(Path.Combine(propsDir.FullName, "launchSettings.json"), """
            {
              "profiles": {
                "MyApp": {
                  "applicationUrl": "http://localhost:3978"
                }
              }
            }
            """);

        var port = LocalRuntimeRequirementCheck.ResolvePort(_tempDir, ProjectPlatform.DotNet);
        port.Should().Be(3978, because: "port should be read from launchSettings.json applicationUrl");
    }

    [Fact]
    public void ResolvePort_WithEnvFile_ReturnsConfiguredPort()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".env"), "PORT=8080\n");

        var port = LocalRuntimeRequirementCheck.ResolvePort(_tempDir, ProjectPlatform.NodeJs);
        port.Should().Be(8080, because: "port should be read from .env PORT variable");
    }

    [Fact]
    public void ResolvePort_WithNoSettings_ReturnsDefault()
    {
        var port = LocalRuntimeRequirementCheck.ResolvePort(_tempDir, ProjectPlatform.DotNet);
        port.Should().Be(5000, because: "default port is used when no launch settings are found");
    }

    [Fact]
    public void ResolvePort_WithNullPath_ReturnsDefault()
    {
        var port = LocalRuntimeRequirementCheck.ResolvePort(null);
        port.Should().Be(5000, because: "default port is used when project path is null");
    }

    [Fact]
    public async Task CheckAsync_NodeJsProject_UsesNpmStart()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), "{}");
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new FakeHttpHandler(HttpStatusCode.OK);
        var check = CreateCheck(handler);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeTrue(because: "Node.js project with health endpoint responding should pass");
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
    public async Task CheckAsync_DotNetProject_SetsAspNetCoreUrls()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project />");
        var propsDir = Directory.CreateDirectory(Path.Combine(_tempDir, "Properties"));
        File.WriteAllText(Path.Combine(propsDir.FullName, "launchSettings.json"), """
            {
              "profiles": {
                "MyApp": {
                  "applicationUrl": "http://localhost:3978"
                }
              }
            }
            """);
        var fakeProcess = CreateFakeProcess(exitImmediately: false);
        _processService.Start(Arg.Any<ProcessStartInfo>()).Returns(fakeProcess);

        var handler = new FakeHttpHandler(HttpStatusCode.OK);
        var check = CreateCheck(handler);
        var config = new Agent365Config
        {
            DeploymentProjectPath = _tempDir
        };

        // Act
        await check.CheckAsync(config, _logger);

        // Assert
        _processService.Received(1).Start(Arg.Is<ProcessStartInfo>(p =>
            p.FileName == "dotnet" &&
            p.EnvironmentVariables["ASPNETCORE_URLS"] == "http://localhost:3978"));
    }

    /// <summary>
    /// Creates a fake Process for testing. When exitImmediately is true, the process
    /// appears to have already exited.
    /// </summary>
    private static Process CreateFakeProcess(bool exitImmediately, int exitCode = 0)
    {
        // Start a real but trivial process we can control
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

    #region Python Entry Point Detection

    [Fact]
    public void ResolvePythonEntryPoint_WhenSingleFileHasMainGuard_ReturnsThatFile()
    {
        File.WriteAllText(Path.Combine(_tempDir, "bot_runner.py"),
            "import bot\nif __name__ == \"__main__\":\n    bot.run()");

        var result = LocalRuntimeRequirementCheck.ResolvePythonEntryPoint(_tempDir);

        result.Should().Be("bot_runner.py");
    }

    [Fact]
    public void ResolvePythonEntryPoint_WhenMultipleFilesHaveMainGuard_PrefersWellKnownName()
    {
        File.WriteAllText(Path.Combine(_tempDir, "app.py"),
            "if __name__ == '__main__':\n    pass");
        File.WriteAllText(Path.Combine(_tempDir, "helper.py"),
            "if __name__ == '__main__':\n    pass");

        var result = LocalRuntimeRequirementCheck.ResolvePythonEntryPoint(_tempDir);

        result.Should().Be("app.py",
            because: "app.py is a preferred entry point name when multiple files have __main__ guards");
    }

    [Fact]
    public void ResolvePythonEntryPoint_WhenNoMainGuard_FallsBackToExistingWellKnownFile()
    {
        File.WriteAllText(Path.Combine(_tempDir, "main.py"), "# no guard");
        File.WriteAllText(Path.Combine(_tempDir, "utils.py"), "# utility");

        var result = LocalRuntimeRequirementCheck.ResolvePythonEntryPoint(_tempDir);

        result.Should().Be("main.py",
            because: "main.py exists and is a known entry point name even without a __main__ guard");
    }

    [Fact]
    public void ResolvePythonEntryPoint_WhenNoPyFiles_FallsBackToAppPy()
    {
        var result = LocalRuntimeRequirementCheck.ResolvePythonEntryPoint(_tempDir);

        result.Should().Be("app.py",
            because: "app.py is the ultimate default when nothing else is found");
    }

    [Fact]
    public void ResolvePythonEntryPoint_ProcfileTakesPriority_OverMainGuard()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Procfile"), "web: python serve.py --host 0.0.0.0");
        File.WriteAllText(Path.Combine(_tempDir, "app.py"),
            "if __name__ == '__main__':\n    pass");

        var result = LocalRuntimeRequirementCheck.ResolvePythonEntryPoint(_tempDir);

        result.Should().Be("serve.py --host 0.0.0.0",
            because: "Procfile is the explicit user-declared entry point and takes highest priority");
    }

    [Fact]
    public void ResolvePythonEntryPoint_WhenProcfileHasGunicorn_FallsToCodeScan()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Procfile"), "web: gunicorn app:app");
        File.WriteAllText(Path.Combine(_tempDir, "bot.py"),
            "if __name__ == '__main__':\n    run()");

        var result = LocalRuntimeRequirementCheck.ResolvePythonEntryPoint(_tempDir);

        result.Should().Be("bot.py",
            because: "gunicorn is not a python command so Procfile is skipped and code scanning finds bot.py");
    }

    [Fact]
    public void ResolvePythonEntryPoint_WhenProcfileHasDashM_ReturnsModuleArgs()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Procfile"), "web: python -m uvicorn app:app");

        var result = LocalRuntimeRequirementCheck.ResolvePythonEntryPoint(_tempDir);

        result.Should().Be("-m uvicorn app:app");
    }

    [Fact]
    public void HasMainGuard_WithDoubleQuotes_ReturnsTrue()
    {
        var file = Path.Combine(_tempDir, "test.py");
        File.WriteAllText(file, "import os\nif __name__ == \"__main__\":\n    main()");

        LocalRuntimeRequirementCheck.HasMainGuard(file).Should().BeTrue();
    }

    [Fact]
    public void HasMainGuard_WithSingleQuotes_ReturnsTrue()
    {
        var file = Path.Combine(_tempDir, "test.py");
        File.WriteAllText(file, "if __name__ == '__main__':\n    main()");

        LocalRuntimeRequirementCheck.HasMainGuard(file).Should().BeTrue();
    }

    [Fact]
    public void HasMainGuard_WithoutGuard_ReturnsFalse()
    {
        var file = Path.Combine(_tempDir, "test.py");
        File.WriteAllText(file, "def main():\n    pass\n\nmain()");

        LocalRuntimeRequirementCheck.HasMainGuard(file).Should().BeFalse();
    }

    [Fact]
    public void HasMainGuard_WithIndentation_ReturnsTrue()
    {
        var file = Path.Combine(_tempDir, "test.py");
        File.WriteAllText(file, "# entry\n    if __name__ == '__main__':\n        run()");

        LocalRuntimeRequirementCheck.HasMainGuard(file).Should().BeTrue();
    }

    [Fact]
    public void ParseProcfileEntryPoint_WhenEmpty_ReturnsNull()
    {
        var procfile = Path.Combine(_tempDir, "Procfile");
        File.WriteAllText(procfile, "");

        LocalRuntimeRequirementCheck.ParseProcfileEntryPoint(procfile).Should().BeNull();
    }

    [Fact]
    public void ParseProcfileEntryPoint_WhenNoWebProcess_ReturnsNull()
    {
        var procfile = Path.Combine(_tempDir, "Procfile");
        File.WriteAllText(procfile, "worker: python worker.py");

        LocalRuntimeRequirementCheck.ParseProcfileEntryPoint(procfile).Should().BeNull();
    }

    #endregion

    /// <summary>
    /// Fake HTTP handler that returns a configurable status code.
    /// </summary>
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public FakeHttpHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }
}
