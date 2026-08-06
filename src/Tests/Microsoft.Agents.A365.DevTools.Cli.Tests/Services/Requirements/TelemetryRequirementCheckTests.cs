// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Requirements;

public class TelemetryRequirementCheckTests : IDisposable
{
    private readonly ILogger _logger = NullLoggerFactory.Instance.CreateLogger("test");
    private readonly Agent365Config _config = new();
    private readonly List<string> _tempFiles = new();

    private string CreateTempLogFile(string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"telemetry-test-{Guid.NewGuid()}.log");
        File.WriteAllLines(path, lines);
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Helper to build a console exporter span block with Agent365Sdk scope.
    /// </summary>
    private static string[] MakeAgent365Span(string operationName) => new[]
    {
        "  traceId: '59ea028f0ee6a6cbb3b0e3c96ee96fa7',",
        "  instrumentationScope: {",
        "    name: 'Agent365Sdk',",
        "  },",
        $"    'gen_ai.operation.name': '{operationName}',"
    };

    /// <summary>
    /// Helper to build a fully-compliant span block with scope version and parentId.
    /// </summary>
    private static string[] MakeFullAgent365Span(string operationName, bool withParent = false) => withParent
        ? new[]
        {
            "  traceId: '59ea028f0ee6a6cbb3b0e3c96ee96fa7',",
            "  parentId: 'abc123def456',",
            "  instrumentationScope: {",
            "    name: 'Agent365Sdk',",
            "    version: '1.0.0',",
            "  },",
            $"    'gen_ai.operation.name': '{operationName}',"
        }
        : new[]
        {
            "  traceId: '59ea028f0ee6a6cbb3b0e3c96ee96fa7',",
            "  instrumentationScope: {",
            "    name: 'Agent365Sdk',",
            "    version: '1.0.0',",
            "  },",
            $"    'gen_ai.operation.name': '{operationName}',"
        };

    /// <summary>
    /// Resource lines that satisfy OTel semantic convention checks.
    /// </summary>
    private static readonly string[] ResourceLines = new[]
    {
        "  resource: {",
        "    'telemetry.sdk.name': 'opentelemetry',",
        "    'telemetry.sdk.version': '1.25.0',",
        "    'service.name': 'my-agent',",
        "  },"
    };

    /// <summary>
    /// Helper to build a span block from the @microsoft/agents-telemetry scope.
    /// With the operation-name-based filtering, these spans are now included
    /// if they have a recognized gen_ai.operation.name.
    /// </summary>
    private static string[] MakeAgentsTelemetrySpan(string operationName) => new[]
    {
        "  traceId: 'bbbb028f0ee6a6cbb3b0e3c96ee96fa7',",
        "  instrumentationScope: {",
        "    name: '@microsoft/agents-telemetry',",
        "  },",
        $"    'gen_ai.operation.name': '{operationName}',"
    };

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best-effort cleanup */ }
        }
    }

    // --- Metadata ---

    [Fact]
    public void Name_ReturnsTelemetry()
    {
        var check = new TelemetryRequirementCheck(null);
        check.Name.Should().Be("Telemetry");
    }

    [Fact]
    public void Category_ReturnsObservability()
    {
        var check = new TelemetryRequirementCheck(null);
        check.Category.Should().Be("Observability");
    }

    // --- No log file ---

    [Fact]
    public async Task CheckAsync_NullLogPath_ReturnsWarning()
    {
        var check = new TelemetryRequirementCheck(null);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeTrue(because: "no log file is a warning, not a failure");
        result.IsWarning.Should().BeTrue(because: "missing log file means telemetry status is unknown");
    }

    [Fact]
    public async Task CheckAsync_NonExistentLogPath_ReturnsWarning()
    {
        var check = new TelemetryRequirementCheck("/nonexistent/path.log");

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeTrue(because: "missing file is a warning, not a failure");
        result.IsWarning.Should().BeTrue();
    }

    // --- No span output ---

    [Fact]
    public async Task CheckAsync_NoSpanOutput_ReturnsFail()
    {
        var logPath = CreateTempLogFile(new[]
        {
            "info: Application started",
            "info: Listening on http://localhost:5000"
        });

        var check = new TelemetryRequirementCheck(logPath);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeFalse(because: "no console exporter span output detected");
        result.ErrorMessage.Should().Contain("No console exporter span output detected");
    }

    // --- Operation-name-based filtering ---

    [Fact]
    public async Task CheckAsync_SpansWithNoRecognizedOperations_ReturnsFail()
    {
        var logPath = CreateTempLogFile(new[]
        {
            "  traceId: 'abc',",
            "  instrumentationScope: {",
            "    name: 'SomeSdk',",
            "  },",
            "    'gen_ai.operation.name': 'unknown_op',"
        });

        var check = new TelemetryRequirementCheck(logPath);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeFalse(because: "no spans have a recognized gen_ai.operation.name");
        result.ErrorMessage.Should().Contain("No GenAI operation spans found");
    }

    [Fact]
    public async Task CheckAsync_AgentsTelemetryScope_IncludedWhenHasRecognizedOp()
    {
        var lines = new List<string>();
        lines.AddRange(MakeAgentsTelemetrySpan("invoke_agent"));
        lines.AddRange(MakeAgentsTelemetrySpan("chat"));
        lines.AddRange(MakeAgentsTelemetrySpan("execute_tool"));
        var logPath = CreateTempLogFile(lines.ToArray());

        var check = new TelemetryRequirementCheck(logPath);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeTrue(
            because: "spans from any scope are accepted if they have recognized gen_ai.operation.name values");
    }

    // --- All 3 GenAI spans from Agent365Sdk ---

    [Fact]
    public async Task CheckAsync_AllThreeSpansFromAgent365Sdk_ReturnsPass()
    {
        var lines = new List<string>();
        lines.AddRange(MakeAgent365Span("invoke_agent"));
        lines.AddRange(MakeAgent365Span("chat"));
        lines.AddRange(MakeAgent365Span("execute_tool"));
        var logPath = CreateTempLogFile(lines.ToArray());

        var check = new TelemetryRequirementCheck(logPath);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeTrue(because: "all 3 required GenAI spans are present from Agent365Sdk scope");
        result.Details.Should().Contain("All required GenAI operation spans detected");
    }

    [Fact]
    public async Task CheckAsync_MixedScopes_AllAccepted()
    {
        var logPath = CreateTempLogFile(new[]
        {
            "  traceId: 'abc',",
            "  instrumentationScope: {",
            "    name: 'CustomSdk',",
            "  },",
            "    'gen_ai.operation.name': 'invoke_agent',",
            "  traceId: 'def',",
            "  instrumentationScope: {",
            "    name: 'microsoft-otel-langchain',",
            "  },",
            "    'gen_ai.operation.name': 'chat',",
            "  traceId: 'ghi',",
            "  instrumentationScope: {",
            "    name: 'Agent365Sdk',",
            "  },",
            "    'gen_ai.operation.name': 'execute_tool',"
        });

        var check = new TelemetryRequirementCheck(logPath);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeTrue(because: "spans from any scope are accepted when they have recognized operations");
    }

    // --- Missing spans ---

    [Fact]
    public async Task CheckAsync_MissingChat_ReturnsFail()
    {
        var lines = new List<string>();
        lines.AddRange(MakeAgent365Span("invoke_agent"));
        lines.AddRange(MakeAgent365Span("execute_tool"));
        var logPath = CreateTempLogFile(lines.ToArray());

        var check = new TelemetryRequirementCheck(logPath);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeFalse(because: "chat operation is missing");
        result.ErrorMessage.Should().Contain("chat");
    }

    [Fact]
    public async Task CheckAsync_OnlyInvokeAgent_ReportsOtherTwoMissing()
    {
        var lines = new List<string>();
        lines.AddRange(MakeAgent365Span("invoke_agent"));
        var logPath = CreateTempLogFile(lines.ToArray());

        var check = new TelemetryRequirementCheck(logPath);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().Contain("chat");
        result.ErrorMessage.Should().Contain("execute_tool");
    }

    // --- SplitIntoSpanBlocks ---

    [Fact]
    public void SplitIntoSpanBlocks_SplitsOnTraceId()
    {
        var lines = new[]
        {
            "  traceId: 'aaa',",
            "  name: 'span1',",
            "  traceId: 'bbb',",
            "  name: 'span2',"
        };

        var blocks = TelemetryRequirementCheck.SplitIntoSpanBlocks(lines);

        blocks.Should().HaveCount(2);
        blocks[0].Should().Contain(l => l.Contains("span1"));
        blocks[1].Should().Contain(l => l.Contains("span2"));
    }

    [Fact]
    public void SplitIntoSpanBlocks_IgnoresLinesBeforeFirstTraceId()
    {
        var lines = new[]
        {
            "info: Application started",
            "info: some noise",
            "  traceId: 'aaa',",
            "  name: 'span1',"
        };

        var blocks = TelemetryRequirementCheck.SplitIntoSpanBlocks(lines);

        blocks.Should().HaveCount(1);
    }

    [Fact]
    public void SplitIntoSpanBlocks_EmptyInput_ReturnsEmpty()
    {
        var blocks = TelemetryRequirementCheck.SplitIntoSpanBlocks(Array.Empty<string>());
        blocks.Should().BeEmpty();
    }

    [Fact]
    public void SplitIntoSpanBlocks_IncludesLinesBeforeTraceId()
    {
        // instrumentationScope appears before traceId in real output
        var lines = new[]
        {
            "  instrumentationScope: {",
            "    name: 'Agent365Sdk',",
            "  },",
            "  traceId: 'aaa',",
            "  'gen_ai.operation.name': 'chat',"
        };

        var blocks = TelemetryRequirementCheck.SplitIntoSpanBlocks(lines);

        // SplitIntoSpanBlocks carries lines before the first traceId into the first block,
        // so instrumentationScope preceding traceId is preserved in block[0]
        blocks.Should().HaveCount(1);
    }

    // --- ExtractOperationNames ---

    [Fact]
    public void ExtractOperationNames_SingleQuoteFormat()
    {
        var block = new List<string> { "    'gen_ai.operation.name': 'chat'," };

        var result = TelemetryRequirementCheck.ExtractOperationNames(block);

        result.Should().ContainSingle().Which.Should().Be("chat");
    }

    [Fact]
    public void ExtractOperationNames_DoubleQuoteFormat()
    {
        var block = new List<string> { "    \"gen_ai.operation.name\": \"invoke_agent\"," };

        var result = TelemetryRequirementCheck.ExtractOperationNames(block);

        result.Should().ContainSingle().Which.Should().Be("invoke_agent");
    }

    [Fact]
    public void ExtractOperationNames_EqualsFormat()
    {
        var block = new List<string> { "gen_ai.operation.name=execute_tool" };

        var result = TelemetryRequirementCheck.ExtractOperationNames(block);

        result.Should().ContainSingle().Which.Should().Be("execute_tool");
    }

    [Fact]
    public void ExtractOperationNames_NoMatch_ReturnsEmpty()
    {
        var block = new List<string> { "  name: 'some-span',", "  duration: 123" };

        var result = TelemetryRequirementCheck.ExtractOperationNames(block);

        result.Should().BeEmpty();
    }

    // --- Real-world console exporter output ---

    [Fact]
    public async Task CheckAsync_RealWorldNodeConsoleExporter_ReturnsPass()
    {
        var logPath = CreateTempLogFile(new[]
        {
            "{",
            "  resource: {",
            "    attributes: {",
            "      'service.name': 'internal-docs-agent',",
            "      'telemetry.sdk.name': 'opentelemetry',",
            "    }",
            "  },",
            "  instrumentationScope: {",
            "    name: 'Agent365Sdk',",
            "    version: '1.0.0',",
            "  },",
            "  traceId: '59ea028f0ee6a6cbb3b0e3c96ee96fa7',",
            "  name: 'invoke_agent Agent',",
            "  attributes: {",
            "    'gen_ai.operation.name': 'invoke_agent',",
            "  },",
            "}",
            "{",
            "  instrumentationScope: {",
            "    name: 'Agent365Sdk',",
            "  },",
            "  traceId: '59ea028f0ee6a6cbb3b0e3c96ee96fa7',",
            "  name: 'chat gpt-4.1',",
            "  attributes: {",
            "    'gen_ai.operation.name': 'chat',",
            "    'gen_ai.request.model': 'gpt-4.1-2025-04-14',",
            "  },",
            "}",
            "{",
            "  instrumentationScope: {",
            "    name: 'Agent365Sdk',",
            "  },",
            "  traceId: '59ea028f0ee6a6cbb3b0e3c96ee96fa7',",
            "  name: 'execute_tool search_docs',",
            "  attributes: {",
            "    'gen_ai.operation.name': 'execute_tool',",
            "  },",
            "}"
        });

        var check = new TelemetryRequirementCheck(logPath);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeTrue(because: "real-world Node.js console exporter output with Agent365Sdk scope should pass");
        result.Details.Should().Contain("invoke_agent");
        result.Details.Should().Contain("chat");
        result.Details.Should().Contain("execute_tool");
    }

    // --- Parent link checks ---

    [Fact]
    public void GetChildSpansMissingParent_WithParentId_ReturnsEmpty()
    {
        var blocks = new List<List<string>>
        {
            new(MakeFullAgent365Span("chat", withParent: true)),
            new(MakeFullAgent365Span("execute_tool", withParent: true))
        };

        TelemetryRequirementCheck.GetChildSpansMissingParent(blocks).Should().BeEmpty();
    }

    [Fact]
    public void GetChildSpansMissingParent_MissingParent_ReturnsOperations()
    {
        var blocks = new List<List<string>>
        {
            new(MakeAgent365Span("chat")),
            new(MakeAgent365Span("execute_tool"))
        };

        var missing = TelemetryRequirementCheck.GetChildSpansMissingParent(blocks);
        missing.Should().Contain("chat");
        missing.Should().Contain("execute_tool");
    }

    [Fact]
    public void GetChildSpansMissingParent_InvokeAgentWithoutParent_IsIgnored()
    {
        var blocks = new List<List<string>>
        {
            new(MakeAgent365Span("invoke_agent"))
        };

        TelemetryRequirementCheck.GetChildSpansMissingParent(blocks)
            .Should().BeEmpty(because: "invoke_agent is a root span and does not need a parent");
    }

    [Theory]
    [InlineData("  parentId: 'abc123def456',", true, "JS/Node camelCase format")]
    [InlineData("  Activity.ParentSpanId:  63a4d021ceef33ed", true, ".NET console exporter format")]
    [InlineData("    \"parent_id\": \"0x9534d47ca25deef6\",", true, "Python JSON format with 0x prefix")]
    [InlineData("  parentSpanId: 'abc123',", true, "camelCase spanId variant")]
    [InlineData("  parent_id=abc123", true, "equals-sign separator")]
    [InlineData("  parentId: undefined", false, "undefined is not a valid hex span ID")]
    [InlineData("  parentId: ''", false, "empty value has no hex digits")]
    [InlineData("  parentId: null", false, "null is not a valid hex span ID")]
    [InlineData("  some unrelated line", false, "no parent key present")]
    public void ParentSpanPattern_MatchesExpectedFormats(string line, bool shouldMatch, string because)
    {
        TelemetryRequirementCheck.ParentSpanPattern.IsMatch(line)
            .Should().Be(shouldMatch, because: because);
    }

    // --- End-to-end: fully compliant spans return success ---

    [Fact]
    public async Task CheckAsync_FullyCompliantSpans_ReturnsSuccess()
    {
        var lines = new List<string>();
        lines.AddRange(ResourceLines);
        lines.Add("{");
        lines.AddRange(MakeFullAgent365Span("invoke_agent"));
        lines.Add("}");
        lines.Add("{");
        lines.AddRange(MakeFullAgent365Span("chat", withParent: true));
        lines.Add("}");
        lines.Add("{");
        lines.AddRange(MakeFullAgent365Span("execute_tool", withParent: true));
        lines.Add("}");

        var logPath = CreateTempLogFile(lines.ToArray());
        var check = new TelemetryRequirementCheck(logPath);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeTrue();
        result.IsWarning.Should().BeFalse(because: "fully compliant spans should not produce warnings");
    }

    [Fact]
    public async Task CheckAsync_ChildSpansMissingParent_ReturnsWarning()
    {
        var lines = new List<string>();
        lines.AddRange(ResourceLines);
        lines.Add("{");
        lines.AddRange(MakeFullAgent365Span("invoke_agent"));
        lines.Add("}");
        lines.Add("{");
        // chat without parentId
        lines.AddRange(MakeFullAgent365Span("chat", withParent: false));
        lines.Add("}");
        lines.Add("{");
        lines.AddRange(MakeFullAgent365Span("execute_tool", withParent: true));
        lines.Add("}");

        var logPath = CreateTempLogFile(lines.ToArray());
        var check = new TelemetryRequirementCheck(logPath);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeTrue(because: "missing parent is a warning not a failure");
        result.IsWarning.Should().BeTrue();
        result.Details.Should().Contain("parentId", because: "warning should mention missing parent links");
        result.Details.Should().Contain("chat");
    }

    [Fact]
    public async Task CheckAsync_AllSpansPresent_NoResourceAttributes_ReturnsSuccess()
    {
        var lines = new List<string>();
        lines.Add("{");
        lines.AddRange(MakeFullAgent365Span("invoke_agent"));
        lines.Add("}");
        lines.Add("{");
        lines.AddRange(MakeFullAgent365Span("chat", withParent: true));
        lines.Add("}");
        lines.Add("{");
        lines.AddRange(MakeFullAgent365Span("execute_tool", withParent: true));
        lines.Add("}");

        var logPath = CreateTempLogFile(lines.ToArray());
        var check = new TelemetryRequirementCheck(logPath);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeTrue(because: "all required spans are present with parent links");
        result.IsWarning.Should().BeFalse(because: "resource attributes are no longer checked");
    }

    // ── Python console exporter format tests ──

    /// <summary>
    /// Helper to build a Python console exporter span block (JSON with snake_case keys).
    /// </summary>
    private static string[] MakePythonSpan(string operationName, bool withParent = false) => withParent
        ? new[]
        {
            $"    \"name\": \"{operationName} gpt-5.4-mini\",",
            "    \"context\": {",
            $"        \"trace_id\": \"0xdd1ed405c6970ac9a12f716d10348920\",",
            "        \"span_id\": \"0xfb587c5909a6c691\",",
            "        \"trace_state\": \"[]\"",
            "    },",
            "    \"kind\": \"SpanKind.INTERNAL\",",
            $"    \"parent_id\": \"0x9534d47ca25deef6\",",
            "    \"attributes\": {",
            $"        \"gen_ai.operation.name\": \"{operationName}\",",
            "        \"gen_ai.request.model\": \"gpt-5.4-mini\"",
            "    },",
        }
        : new[]
        {
            $"    \"name\": \"{operationName} gpt-5.4-mini\",",
            "    \"context\": {",
            $"        \"trace_id\": \"0xdd1ed405c6970ac9a12f716d10348920\",",
            "        \"span_id\": \"0xfb587c5909a6c691\",",
            "        \"trace_state\": \"[]\"",
            "    },",
            "    \"kind\": \"SpanKind.INTERNAL\",",
            "    \"parent_id\": null,",
            "    \"attributes\": {",
            $"        \"gen_ai.operation.name\": \"{operationName}\",",
            "        \"gen_ai.request.model\": \"gpt-5.4-mini\"",
            "    },",
        };

    private static readonly string[] PythonResourceLines = new[]
    {
        "    \"resource\": {",
        "        \"attributes\": {",
        "            \"telemetry.sdk.language\": \"python\",",
        "            \"telemetry.sdk.name\": \"opentelemetry\",",
        "            \"telemetry.sdk.version\": \"1.40.0\",",
        "            \"service.name\": \"pirate-agent\"",
        "        }",
        "    }"
    };

    [Fact]
    public async Task Python_ConsoleExporter_AllOpsPresent_Passes()
    {
        var lines = new List<string>();
        lines.AddRange(PythonResourceLines);
        lines.Add("{");
        lines.AddRange(MakePythonSpan("invoke_agent"));
        lines.Add("}");
        lines.Add("{");
        lines.AddRange(MakePythonSpan("chat", withParent: true));
        lines.Add("}");
        lines.Add("{");
        lines.AddRange(MakePythonSpan("execute_tool", withParent: true));
        lines.Add("}");

        var logPath = CreateTempLogFile(lines.ToArray());
        var check = new TelemetryRequirementCheck(logPath);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeTrue(because: "all three required GenAI operations are present in Python format");
    }

    [Fact]
    public async Task Python_ConsoleExporter_MissingOps_Fails()
    {
        var lines = new List<string>();
        lines.Add("{");
        lines.AddRange(MakePythonSpan("chat", withParent: true));
        lines.Add("}");

        var logPath = CreateTempLogFile(lines.ToArray());
        var check = new TelemetryRequirementCheck(logPath);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeFalse(because: "invoke_agent and execute_tool operations are missing");
        result.ErrorMessage.Should().Contain("invoke_agent");
        result.ErrorMessage.Should().Contain("execute_tool");
    }

    [Fact]
    public async Task Python_ConsoleExporter_ChildSpan_WithoutParent_WarnsAboutMissingParent()
    {
        var lines = new List<string>();
        lines.AddRange(PythonResourceLines);
        lines.Add("{");
        lines.AddRange(MakePythonSpan("invoke_agent"));
        lines.Add("}");
        lines.Add("{");
        // chat span without parent_id (null)
        lines.AddRange(MakePythonSpan("chat"));
        lines.Add("}");
        lines.Add("{");
        lines.AddRange(MakePythonSpan("execute_tool", withParent: true));
        lines.Add("}");

        var logPath = CreateTempLogFile(lines.ToArray());
        var check = new TelemetryRequirementCheck(logPath);

        var result = await check.CheckAsync(_config, _logger);

        result.Passed.Should().BeTrue(because: "missing parent is a warning not a failure");
        result.IsWarning.Should().BeTrue(because: "chat span has null parent_id");
        result.Details.Should().Contain("chat", because: "chat span is missing parent link");
    }

    [Fact]
    public void SplitIntoSpanBlocks_PythonFormat_SplitsCorrectly()
    {
        var lines = new List<string>();
        lines.Add("{");
        lines.AddRange(MakePythonSpan("invoke_agent"));
        lines.Add("}");
        lines.Add("{");
        lines.AddRange(MakePythonSpan("chat", withParent: true));
        lines.Add("}");

        var blocks = TelemetryRequirementCheck.SplitIntoSpanBlocks(lines.ToArray());

        blocks.Should().HaveCount(2, because: "two Python span JSON blocks were provided");
    }
}
