// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Requirements;

public class ToolingManifestRequirementCheckTests : IDisposable
{
    private readonly ILogger _logger;
    private readonly ToolingManifestRequirementCheck _check;
    private readonly string _tempDir;

    public ToolingManifestRequirementCheckTests()
    {
        _logger = Substitute.For<ILogger>();
        _check = new ToolingManifestRequirementCheck();
        _tempDir = Path.Combine(Path.GetTempPath(), $"a365-manifest-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Check_HasExpectedMetadata()
    {
        _check.Name.Should().Be("Tooling Manifest");
        _check.Category.Should().Be("Configuration");
        _check.Description.Should().Contain("ToolingManifest.json");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CheckAsync_WhenDeploymentProjectPathIsEmpty_FallsBackToCwd(string? path)
    {
        // Arrange - when deploymentProjectPath is empty, falls back to CWD
        // CWD likely has no ToolingManifest.json, so we expect a warning about missing file
        var config = new Agent365Config { DeploymentProjectPath = path ?? string.Empty };

        // Act
        var result = await _check.CheckAsync(config, _logger);

        // Assert - should not fail, should either warn (no manifest found) or pass (if manifest exists in CWD)
        result.Passed.Should().BeTrue(because: "missing manifest in CWD is a non-blocking warning");
    }

    [Fact]
    public async Task CheckAsync_WhenManifestFileIsMissing_ReturnsPass()
    {
        // Arrange - use a directory that exists but has no manifest
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await _check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeTrue(because: "manifest is optional, check passes when file is absent");
        result.IsWarning.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_WhenManifestContainsInvalidJson_ReturnsFailure()
    {
        // Arrange
        var manifestPath = Path.Combine(_tempDir, McpConstants.ToolingManifestFileName);
        await File.WriteAllTextAsync(manifestPath, "{ not valid json }");
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await _check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: "invalid JSON is a parse error that must be fixed");
        result.ErrorMessage.Should().Contain("invalid JSON");
    }

    [Fact]
    public async Task CheckAsync_WhenManifestIsJsonNull_ReturnsFailure()
    {
        // Arrange
        var manifestPath = Path.Combine(_tempDir, McpConstants.ToolingManifestFileName);
        await File.WriteAllTextAsync(manifestPath, "null");
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await _check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: "a null manifest is not a valid ToolingManifest");
        result.ErrorMessage.Should().Contain("mcpServers must be an array");
    }

    [Fact]
    public async Task CheckAsync_WhenMcpServersIsNull_ReturnsFailure()
    {
        // Arrange
        var manifestPath = Path.Combine(_tempDir, McpConstants.ToolingManifestFileName);
        await File.WriteAllTextAsync(manifestPath, """{ "mcpServers": null }""");
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await _check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: "mcpServers null is not a valid manifest");
        result.ErrorMessage.Should().Contain("mcpServers must be an array");
    }

    [Fact]
    public async Task CheckAsync_WhenManifestHasNoServers_ReturnsFailure()
    {
        // Arrange
        var manifestPath = Path.Combine(_tempDir, McpConstants.ToolingManifestFileName);
        await File.WriteAllTextAsync(manifestPath, """{ "mcpServers": [] }""");
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await _check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: "an empty mcpServers array is invalid per ToolingManifest validation rules");
        result.ErrorMessage.Should().Contain("validation error");
    }

    [Fact]
    public async Task CheckAsync_WhenManifestHasDuplicateServerNames_ReturnsFailure()
    {
        // Arrange
        var manifestPath = Path.Combine(_tempDir, McpConstants.ToolingManifestFileName);
        var content = """
        {
            "mcpServers": [
                { "mcpServerName": "MCP_MailTools", "url": "https://example.com/mail" },
                { "mcpServerName": "MCP_MailTools", "url": "https://example.com/mail2" }
            ]
        }
        """;
        await File.WriteAllTextAsync(manifestPath, content);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await _check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: "duplicate server names are not allowed");
        result.ErrorMessage.Should().Contain("Duplicate");
    }

    [Fact]
    public async Task CheckAsync_WhenManifestHasServerMissingUrl_ReturnsFailure()
    {
        // Arrange
        var manifestPath = Path.Combine(_tempDir, McpConstants.ToolingManifestFileName);
        var content = """
        {
            "mcpServers": [
                { "mcpServerName": "MCP_MailTools" }
            ]
        }
        """;
        await File.WriteAllTextAsync(manifestPath, content);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await _check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: "MCP server entries require a url field");
        result.ErrorMessage.Should().Contain("invalid");
    }

    [Fact]
    public async Task CheckAsync_WhenManifestIsValid_ReturnsSuccess()
    {
        // Arrange
        var manifestPath = Path.Combine(_tempDir, McpConstants.ToolingManifestFileName);
        var content = """
        {
            "mcpServers": [
                { "mcpServerName": "MCP_MailTools", "url": "https://example.com/mail" },
                { "mcpServerName": "MCP_CalendarTools", "url": "https://example.com/calendar" }
            ]
        }
        """;
        await File.WriteAllTextAsync(manifestPath, content);
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await _check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeTrue(because: "a valid manifest with proper server entries should pass validation");
        result.IsWarning.Should().BeFalse();
        result.Details.Should().Contain("2 MCP server(s) configured");
    }

    [Fact]
    public async Task CheckAsync_WhenDeploymentProjectPathIsInvalid_ReturnsFailure()
    {
        // Arrange - use characters that are invalid in a path
        var config = new Agent365Config { DeploymentProjectPath = "path\0with\0nulls" };

        // Act
        var result = await _check.CheckAsync(config, _logger);

        // Assert - Path.GetFullPath throws, caught by ExecuteCheckWithLoggingAsync
        result.Passed.Should().BeFalse(because: "an invalid path format should be reported as a failure");
    }
}
