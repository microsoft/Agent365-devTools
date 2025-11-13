// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class PlatformDetectorTests
{
    private readonly ILogger<PlatformDetector> _logger;
    private readonly PlatformDetector _detector;

    public PlatformDetectorTests()
    {
        _logger = Substitute.For<ILogger<PlatformDetector>>();
        _detector = new PlatformDetector(_logger);
    }

    [Fact]
    public void Detect_WithCsProjFile_ReturnsDotNet()
    {
        // Arrange
        var tempDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(tempDir, "test.csproj"), "<Project></Project>");

        // Act
        var result = _detector.Detect(tempDir);

        // Assert
        result.Should().Be(ProjectPlatform.DotNet);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public void Detect_WithFsProjFile_ReturnsDotNet()
    {
        // Arrange
        var tempDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(tempDir, "test.fsproj"), "<Project></Project>");

        // Act
        var result = _detector.Detect(tempDir);

        // Assert
        result.Should().Be(ProjectPlatform.DotNet);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public void Detect_WithPackageJson_ReturnsNodeJs()
    {
        // Arrange
        var tempDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(tempDir, "package.json"), "{}");

        // Act
        var result = _detector.Detect(tempDir);

        // Assert
        result.Should().Be(ProjectPlatform.NodeJs);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public void Detect_WithRequirementsTxt_ReturnsPython()
    {
        // Arrange
        var tempDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(tempDir, "requirements.txt"), "flask==2.0.0");

        // Act
        var result = _detector.Detect(tempDir);

        // Assert
        result.Should().Be(ProjectPlatform.Python);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public void Detect_WithPythonFiles_ReturnsPython()
    {
        // Arrange
        var tempDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(tempDir, "app.py"), "print('hello')");

        // Act
        var result = _detector.Detect(tempDir);

        // Assert
        result.Should().Be(ProjectPlatform.Python);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public void Detect_WithEmptyDirectory_ReturnsUnknown()
    {
        // Arrange
        var tempDir = CreateTempDirectory();

        // Act
        var result = _detector.Detect(tempDir);

        // Assert
        result.Should().Be(ProjectPlatform.Unknown);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public void Detect_WithNonExistentDirectory_ReturnsUnknown()
    {
        // Act
        var result = _detector.Detect("C:\\NonExistent\\Path\\12345");

        // Assert
        result.Should().Be(ProjectPlatform.Unknown);
    }

    [Fact]
    public void Detect_PrioritizesDotNetOverNodeJs()
    {
        // Arrange - both .csproj and package.json exist
        var tempDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(tempDir, "test.csproj"), "<Project></Project>");
        File.WriteAllText(Path.Combine(tempDir, "package.json"), "{}");

        // Act
        var result = _detector.Detect(tempDir);

        // Assert - .NET should be detected first
        result.Should().Be(ProjectPlatform.DotNet);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public void Detect_PrioritizesNodeJsOverPython()
    {
        // Arrange - both package.json and requirements.txt exist
        var tempDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(tempDir, "package.json"), "{}");
        File.WriteAllText(Path.Combine(tempDir, "requirements.txt"), "flask");

        // Act
        var result = _detector.Detect(tempDir);

        // Assert - Node.js should be detected first
        result.Should().Be(ProjectPlatform.NodeJs);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    private string CreateTempDirectory()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"a365test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempPath);
        return tempPath;
    }
}
