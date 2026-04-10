// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for the EvaluateCommand structure and helper methods.
/// </summary>
public class EvaluateCommandTests
{
    private readonly ILogger _mockLogger;
    private readonly ISchemaDiscoveryService _mockDiscoveryService;
    private readonly IChecklistGenerator _mockChecklistGenerator;
    private readonly IChecklistEvaluator _mockChecklistEvaluator;
    private readonly IEvaluationAnalyzer _mockEvaluationAnalyzer;
    private readonly IReportGenerator _mockReportGenerator;

    public EvaluateCommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockDiscoveryService = Substitute.For<ISchemaDiscoveryService>();
        _mockChecklistGenerator = Substitute.For<IChecklistGenerator>();
        _mockChecklistEvaluator = Substitute.For<IChecklistEvaluator>();
        _mockEvaluationAnalyzer = Substitute.For<IEvaluationAnalyzer>();
        _mockReportGenerator = Substitute.For<IReportGenerator>();
    }

    private Command CreateCommand()
    {
        return EvaluateCommand.CreateCommand(
            _mockLogger,
            _mockDiscoveryService,
            _mockChecklistGenerator,
            _mockChecklistEvaluator,
            _mockEvaluationAnalyzer,
            _mockReportGenerator);
    }

    // -----------------------------------------------------------------------
    // Command structure
    // -----------------------------------------------------------------------

    [Fact]
    public void CreateCommand_HasCorrectName()
    {
        var command = CreateCommand();

        command.Name.Should().Be("evaluate");
    }

    [Fact]
    public void CreateCommand_HasServerUrlArgument()
    {
        var command = CreateCommand();

        var argument = command.Arguments.FirstOrDefault(a => a.Name == "server-url");
        argument.Should().NotBeNull();
        argument!.ValueType.Should().Be(typeof(string));
    }

    [Fact]
    public void CreateCommand_HasOutputDirOption()
    {
        var command = CreateCommand();

        var option = command.Options.FirstOrDefault(o => o.Name == "output-dir");
        option.Should().NotBeNull();
        option!.Aliases.Should().Contain("--output-dir");
        option.Aliases.Should().Contain("-o");
    }

    [Fact]
    public void CreateCommand_HasEvalEngineOption()
    {
        var command = CreateCommand();

        var option = command.Options.FirstOrDefault(o => o.Name == "eval-engine");
        option.Should().NotBeNull();
        option!.Aliases.Should().Contain("--eval-engine");
    }

    [Fact]
    public void CreateCommand_HasVerboseOption()
    {
        var command = CreateCommand();

        var option = command.Options.FirstOrDefault(o => o.Name == "verbose");
        option.Should().NotBeNull();
        option!.Aliases.Should().Contain("--verbose");
        option.Aliases.Should().Contain("-v");
    }

    [Fact]
    public void CreateCommand_OutputDirDefaultsToCurrentDirectory()
    {
        var command = CreateCommand();

        var option = command.Options.First(o => o.Name == "output-dir") as Option<string>;
        option.Should().NotBeNull();

        // Parse with no --output-dir specified to verify the default
        var parseResult = command.Parse("http://localhost:3000");
        var value = parseResult.GetValueForOption(option!);
        value.Should().Be(".");
    }

    [Fact]
    public void CreateCommand_EvalEngineDefaultsToAuto()
    {
        var command = CreateCommand();

        var option = command.Options.First(o => o.Name == "eval-engine") as Option<string>;
        option.Should().NotBeNull();

        var parseResult = command.Parse("http://localhost:3000");
        var value = parseResult.GetValueForOption(option!);
        value.Should().Be("auto");
    }

    // -----------------------------------------------------------------------
    // ParseEvalEngine
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("auto", EvalEngine.Auto)]
    [InlineData("AUTO", EvalEngine.Auto)]
    [InlineData("github-copilot", EvalEngine.GithubCopilot)]
    [InlineData("GITHUB-COPILOT", EvalEngine.GithubCopilot)]
    [InlineData("claude-code", EvalEngine.ClaudeCode)]
    [InlineData("Claude-Code", EvalEngine.ClaudeCode)]
    [InlineData("none", EvalEngine.None)]
    [InlineData("NONE", EvalEngine.None)]
    public void ParseEvalEngine_ValidValues_ReturnsCorrectEnum(string input, EvalEngine expected)
    {
        var result = EvaluateCommand.ParseEvalEngine(input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("openai")]
    [InlineData("")]
    public void ParseEvalEngine_InvalidValues_ThrowsEvaluationException(string input)
    {
        var act = () => EvaluateCommand.ParseEvalEngine(input);

        act.Should().Throw<EvaluationException>();
    }

    // -----------------------------------------------------------------------
    // DeriveServerName
    // -----------------------------------------------------------------------

    [Fact]
    public void DeriveServerName_StandardUrl_ReturnsHostWithDotsReplaced()
    {
        var result = EvaluateCommand.DeriveServerName("http://my.server.com/mcp");

        result.Should().Be("my-server-com");
    }

    [Fact]
    public void DeriveServerName_UrlWithNonStandardPort_IncludesPort()
    {
        var result = EvaluateCommand.DeriveServerName("http://localhost:3000/mcp");

        result.Should().Be("localhost-3000");
    }

    [Fact]
    public void DeriveServerName_UrlWithDefaultPort_ExcludesPort()
    {
        var result = EvaluateCommand.DeriveServerName("http://example.com/mcp");

        result.Should().Be("example-com");
    }

    [Fact]
    public void DeriveServerName_InvalidUri_ReturnsSanitizedFallback()
    {
        // The fallback replaces :// / : . with hyphens and trims trailing hyphens.
        // "not a valid uri" has no such characters, so it passes through unchanged.
        var result = EvaluateCommand.DeriveServerName("not a valid uri");

        result.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DeriveServerName_InvalidUriWithSpecialChars_ReplacesSpecialChars()
    {
        var result = EvaluateCommand.DeriveServerName("fake://host.name:1234/path");

        result.Should().NotContain("://");
        result.Should().NotContain("/");
    }

    [Fact]
    public void DeriveServerName_EmptyString_ReturnsUnknownServer()
    {
        var result = EvaluateCommand.DeriveServerName("");

        result.Should().Be("unknown-server");
    }
}
