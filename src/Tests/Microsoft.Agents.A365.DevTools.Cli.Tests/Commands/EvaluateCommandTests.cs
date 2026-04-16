// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for the evaluate subcommand under develop-mcp.
/// </summary>
public class EvaluateCommandTests
{
    private readonly ILogger _mockLogger;
    private readonly IAgent365ToolingService _mockToolingService;
    private readonly IEvaluationPipelineService _mockPipelineService;

    public EvaluateCommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockToolingService = Substitute.For<IAgent365ToolingService>();
        _mockPipelineService = Substitute.For<IEvaluationPipelineService>();
    }

    private Command GetEvaluateSubcommand()
    {
        var parent = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService, _mockPipelineService);
        return parent.Subcommands.First(sc => sc.Name == "evaluate");
    }

    // -----------------------------------------------------------------------
    // Command structure
    // -----------------------------------------------------------------------

    [Fact]
    public void EvaluateSubcommand_HasCorrectName()
    {
        var command = GetEvaluateSubcommand();

        command.Name.Should().Be("evaluate");
    }

    [Fact]
    public void EvaluateSubcommand_HasServerUrlArgument()
    {
        var command = GetEvaluateSubcommand();

        var argument = command.Arguments.FirstOrDefault(a => a.Name == "server-url");
        argument.Should().NotBeNull();
        argument!.ValueType.Should().Be(typeof(string));
    }

    [Fact]
    public void EvaluateSubcommand_HasOutputDirOption()
    {
        var command = GetEvaluateSubcommand();

        var option = command.Options.FirstOrDefault(o => o.Name == "output-dir");
        option.Should().NotBeNull();
        option!.Aliases.Should().Contain("--output-dir");
        option.Aliases.Should().Contain("-o");
    }

    [Fact]
    public void EvaluateSubcommand_HasEvalEngineOption()
    {
        var command = GetEvaluateSubcommand();

        var option = command.Options.FirstOrDefault(o => o.Name == "eval-engine");
        option.Should().NotBeNull();
        option!.Aliases.Should().Contain("--eval-engine");
    }

    [Fact]
    public void EvaluateSubcommand_HasAuthTokenOption()
    {
        var command = GetEvaluateSubcommand();

        var option = command.Options.FirstOrDefault(o => o.Name == "auth-token");
        option.Should().NotBeNull();
        option!.Aliases.Should().Contain("--auth-token");
    }

    [Fact]
    public void EvaluateSubcommand_OutputDirDefaultsToCurrentDirectory()
    {
        var command = GetEvaluateSubcommand();

        var option = command.Options.First(o => o.Name == "output-dir") as Option<string>;
        option.Should().NotBeNull();

        var parseResult = command.Parse("http://localhost:3000");
        var value = parseResult.GetValueForOption(option!);
        value.Should().Be(".");
    }

    [Fact]
    public void EvaluateSubcommand_EvalEngineDefaultsToAuto()
    {
        var command = GetEvaluateSubcommand();

        var option = command.Options.First(o => o.Name == "eval-engine") as Option<string>;
        option.Should().NotBeNull();

        var parseResult = command.Parse("http://localhost:3000");
        var value = parseResult.GetValueForOption(option!);
        value.Should().Be("auto");
    }
}
