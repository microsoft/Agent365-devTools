// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class LogsCommandTests : IDisposable
{
    private readonly ILogger<LogsCommand> _logger = Substitute.For<ILogger<LogsCommand>>();
    private readonly ILogRedactionService _redactionService = Substitute.For<ILogRedactionService>();
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), $"a365-logs-test-{Guid.NewGuid():N}");
    private readonly List<string> _createdLogFiles = [];

    public LogsCommandTests()
    {
        Directory.CreateDirectory(_outputDir);
        _redactionService.Redact(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new LogRedactionResult("# redacted\n[INF] content", 0, 0, 0, 0));
    }

    [Fact]
    public async Task Export_InvalidCommandName_ExitsWithCode1()
    {
        var command = LogsCommand.CreateCommand(_logger, _redactionService);

        var result = await command.InvokeAsync(["export", "../../etc/passwd"]);

        result.Should().Be(1,
            because: "command names containing path separators must be rejected to prevent path traversal");
    }

    [Fact]
    public async Task Export_CommandNameWithUpperCase_ExitsWithCode1()
    {
        var command = LogsCommand.CreateCommand(_logger, _redactionService);

        var result = await command.InvokeAsync(["export", "Setup"]);

        result.Should().Be(1,
            because: "command names must match ^[a-z0-9-]+$ — uppercase is not allowed");
    }

    [Fact]
    public async Task Export_NonexistentOutputDirectory_ExitsWithCode1()
    {
        var command = LogsCommand.CreateCommand(_logger, _redactionService);

        var result = await command.InvokeAsync(["export", "--output", Path.Combine(Path.GetTempPath(), $"no-such-dir-{Guid.NewGuid():N}")]);

        result.Should().Be(1,
            because: "a nonexistent --output directory must be rejected with exit code 1");
    }

    [Fact]
    public async Task Export_WhitespaceOutputDirectory_ExitsWithCode1()
    {
        var command = LogsCommand.CreateCommand(_logger, _redactionService);

        var result = await command.InvokeAsync(["export", "--output", "   "]);

        result.Should().Be(1,
            because: "a whitespace-only --output value must be rejected rather than falling through to Directory.Exists with a blank path");
    }

    [Fact]
    public async Task Export_MissingLogFile_ExitsWithCode1()
    {
        // No log file created — ConfigService.GetCommandLogPath("nonexistent-cmd") will not exist.
        var command = LogsCommand.CreateCommand(_logger, _redactionService);

        var result = await command.InvokeAsync(["export", "nonexistent-cmd", "--output", _outputDir]);

        result.Should().Be(1,
            because: "an explicitly requested log that does not exist must exit 1 so callers can detect the failure");
    }

    [Fact]
    public async Task Export_SuccessfulExport_WritesRedactedFileWithExpectedName()
    {
        var logPath = ConfigService.GetCommandLogPath("test-cmd");
        await File.WriteAllTextAsync(logPath, "[INF] Test log content");
        _createdLogFiles.Add(logPath);

        var command = LogsCommand.CreateCommand(_logger, _redactionService);

        var result = await command.InvokeAsync(["export", "test-cmd", "--output", _outputDir]);

        result.Should().Be(0,
            because: "a successful export must exit 0");
        var expectedFile = Path.Combine(_outputDir, "a365.test-cmd.redacted.log");
        File.Exists(expectedFile).Should().BeTrue(
            because: "the exported file must be written as a365.{command}.redacted.log in the output directory");
    }

    [Fact]
    public async Task Export_RedactedFilesExcludedFromAutoDiscovery_NotReExported()
    {
        var logsDir = ConfigService.GetLogsDirectory();
        var realLog = Path.Combine(logsDir, "a365.auto-test.log");
        var redactedLog = Path.Combine(logsDir, "a365.auto-test.redacted.log");
        await File.WriteAllTextAsync(realLog, "[INF] Real log");
        await File.WriteAllTextAsync(redactedLog, "# already redacted");
        _createdLogFiles.Add(realLog);
        _createdLogFiles.Add(redactedLog);

        var command = LogsCommand.CreateCommand(_logger, _redactionService);
        await command.InvokeAsync(["export", "--output", _outputDir]);

        _redactionService.Received(1).Redact(Arg.Any<string>(), Arg.Is<string>(p => p.Contains("auto-test.log")));
        _redactionService.DidNotReceive().Redact(Arg.Any<string>(), Arg.Is<string>(p => p.Contains("redacted.log")));
    }

    public void Dispose()
    {
        foreach (var f in _createdLogFiles)
            try { File.Delete(f); } catch { }
        try { Directory.Delete(_outputDir, recursive: true); } catch { }
    }
}
