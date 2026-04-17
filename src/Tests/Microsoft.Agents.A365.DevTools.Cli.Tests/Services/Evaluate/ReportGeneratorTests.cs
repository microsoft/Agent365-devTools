// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

/// <summary>
/// Tests for the ReportGenerator service which produces JSON and HTML report files.
/// </summary>
public class ReportGeneratorTests : IDisposable
{
    private readonly ReportGenerator _generator;
    private readonly string _tempDir;

    public ReportGeneratorTests()
    {
        _generator = new ReportGenerator(NullLogger<ReportGenerator>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"eval_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Creates a minimal SchemaEvalResult for testing report generation.
    /// </summary>
    private static SchemaEvalResult CreateMinimalResult(string serverName = "test-server")
    {
        return new SchemaEvalResult
        {
            ServerName = serverName,
            ServerUrl = "http://localhost:3000",
            EvaluatedAt = DateTime.UtcNow,
            OverallScore = 75.5f,
            Maturity = new MaturityLevel
            {
                Level = 2,
                Label = "Consistent",
                Description = "Test maturity description",
                NextLevelRequirements = ["Requirement 1"],
            },
            ToolCount = 1,
            ToolResults =
            [
                new ToolEvalResult
                {
                    ToolName = "test_tool",
                    ToolDescription = "A test tool",
                    ParamCount = 1,
                    Score = 80f,
                    CategoryScores = new Dictionary<string, float>
                    {
                        ["tool_name"] = 100f,
                        ["tool_description"] = 66.7f,
                        ["schema_structure"] = 100f,
                        ["param_name"] = 100f,
                        ["param_description"] = 50f,
                    },
                    Checks = [],
                    ActionItems = [],
                    IssuesDetected = [],
                },
            ],
            ToolsetResult = new ToolsetEvalResult
            {
                Score = 100f,
                Checks = [],
                ActionItems = [],
            },
            AllActionItems = [],
            CategoryAverages = new Dictionary<string, float>
            {
                ["tool_name"] = 100f,
                ["tool_description"] = 66.7f,
            },
            ActionItemsByPriority = new Dictionary<string, int>
            {
                ["P0"] = 0,
                ["P1"] = 1,
                ["P2"] = 0,
                ["P3"] = 0,
            },
            IssueSummary = [],
            EvalEngine = "None",
        };
    }

    // -----------------------------------------------------------------------
    // JSON report generation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GenerateAsync_CreatesJsonReportFile()
    {
        var result = CreateMinimalResult();

        await _generator.GenerateAsync(result, _tempDir, openInBrowser: false);

        var jsonPath = Path.Combine(_tempDir, "test-server_eval_report.json");
        File.Exists(jsonPath).Should().BeTrue("JSON report file should be created");
    }

    [Fact]
    public async Task GenerateAsync_JsonReportContainsValidJson()
    {
        var result = CreateMinimalResult();

        await _generator.GenerateAsync(result, _tempDir, openInBrowser: false);

        var jsonPath = Path.Combine(_tempDir, "test-server_eval_report.json");
        var content = await File.ReadAllTextAsync(jsonPath);
        content.Should().Contain("\"server_name\"");
        content.Should().Contain("\"overall_score\"");
        content.Should().Contain("test-server");
    }

    // -----------------------------------------------------------------------
    // HTML report generation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GenerateAsync_CreatesHtmlReportFile()
    {
        var result = CreateMinimalResult();

        await _generator.GenerateAsync(result, _tempDir, openInBrowser: false);

        var htmlPath = Path.Combine(_tempDir, "test-server_eval_report.html");
        File.Exists(htmlPath).Should().BeTrue("HTML report file should be created");
    }

    [Fact]
    public async Task GenerateAsync_HtmlReportContainsReportData()
    {
        var result = CreateMinimalResult();

        await _generator.GenerateAsync(result, _tempDir, openInBrowser: false);

        var htmlPath = Path.Combine(_tempDir, "test-server_eval_report.html");
        var content = await File.ReadAllTextAsync(htmlPath);

        // The template placeholder {{REPORT_DATA}} should have been replaced
        // with actual JSON data
        content.Should().NotContain("{{REPORT_DATA}}",
            "the placeholder should be replaced with actual report data");

        // The injected data should contain the server name from the result
        content.Should().Contain("test-server");
    }

    [Fact]
    public async Task GenerateAsync_HtmlReportIsValidHtml()
    {
        var result = CreateMinimalResult();

        await _generator.GenerateAsync(result, _tempDir, openInBrowser: false);

        var htmlPath = Path.Combine(_tempDir, "test-server_eval_report.html");
        var content = await File.ReadAllTextAsync(htmlPath);

        content.Should().Contain("<html", "output should be valid HTML");
    }

    // -----------------------------------------------------------------------
    // Output directory handling
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GenerateAsync_CreatesOutputDirectoryIfNotExists()
    {
        var result = CreateMinimalResult();
        var newDir = Path.Combine(_tempDir, "nested", "output");

        await _generator.GenerateAsync(result, newDir, openInBrowser: false);

        Directory.Exists(newDir).Should().BeTrue();
        File.Exists(Path.Combine(newDir, "test-server_eval_report.json")).Should().BeTrue();
        File.Exists(Path.Combine(newDir, "test-server_eval_report.html")).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Server name sanitization
    // -----------------------------------------------------------------------

    [Fact]
    public void SanitizeFileName_ReplacesSpecialCharactersWithUnderscores()
    {
        var result = ReportGenerator.SanitizeFileName("my.server:8080/api");

        result.Should().Be("my_server_8080_api");
    }

    [Fact]
    public void SanitizeFileName_PreservesHyphens()
    {
        var result = ReportGenerator.SanitizeFileName("my-server-name");

        result.Should().Be("my-server-name");
    }

    [Fact]
    public void SanitizeFileName_PreservesAlphanumerics()
    {
        var result = ReportGenerator.SanitizeFileName("server123");

        result.Should().Be("server123");
    }

    [Fact]
    public void SanitizeFileName_EmptyOrWhitespace_ReturnsDefault()
    {
        ReportGenerator.SanitizeFileName("").Should().Be("server");
        ReportGenerator.SanitizeFileName("  ").Should().Be("server");
    }

    [Fact]
    public void SanitizeFileName_NullInput_ReturnsDefault()
    {
        ReportGenerator.SanitizeFileName(null!).Should().Be("server");
    }

    [Fact]
    public async Task GenerateAsync_SanitizedServerNameUsedForFilenames()
    {
        var result = CreateMinimalResult("my.server:8080");

        await _generator.GenerateAsync(result, _tempDir, openInBrowser: false);

        // Dots and colons get sanitized to underscores
        var expectedPrefix = "my_server_8080";
        File.Exists(Path.Combine(_tempDir, $"{expectedPrefix}_eval_report.json")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, $"{expectedPrefix}_eval_report.html")).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Null argument validation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GenerateAsync_NullResult_ThrowsArgumentNullException()
    {
        var act = () => _generator.GenerateAsync(null!, _tempDir);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GenerateAsync_NullOutputDir_ThrowsArgumentException()
    {
        var result = CreateMinimalResult();

        var act = () => _generator.GenerateAsync(result, null!);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GenerateAsync_WhitespaceOutputDir_ThrowsArgumentException()
    {
        var result = CreateMinimalResult();

        var act = () => _generator.GenerateAsync(result, "   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
