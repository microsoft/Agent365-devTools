// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class PythonBuilderTests : IDisposable
{
    private readonly ILogger<PythonBuilder> _logger;
    private readonly CommandExecutor _mockExecutor;
    private readonly PythonBuilder _builder;
    private readonly List<string> _tempDirectories;

    public PythonBuilderTests()
    {
        _logger = Substitute.For<ILogger<PythonBuilder>>();
        var executorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(executorLogger);
        _builder = new PythonBuilder(_logger, _mockExecutor);
        _tempDirectories = new List<string>();
    }

    [Fact]
    public async Task BuildAsync_WithPyprojectToml_CreatesEditableRequirementsTxt()
    {
        // Arrange
        var projectDir = CreateTempDirectory();
        var publishDir = "publish";
        var publishPath = Path.Combine(projectDir, publishDir);

        // Create source files
        File.WriteAllText(Path.Combine(projectDir, "pyproject.toml"), "[project]\nname = \"test-project\"");
        File.WriteAllText(Path.Combine(projectDir, "requirements.txt"), "flask==2.0.0\nrequests==2.31.0");
        File.WriteAllText(Path.Combine(projectDir, "app.py"), "print('hello')");

        // Mock Python and pip executables
        MockPythonEnvironment();

        // Act
        await _builder.BuildAsync(projectDir, publishDir, verbose: false);

        // Assert
        var requirementsTxt = Path.Combine(publishPath, "requirements.txt");
        File.Exists(requirementsTxt).Should().BeTrue();

        var content = await File.ReadAllTextAsync(requirementsTxt);
        content.Should().Contain("--find-links dist");
        content.Should().Contain("--pre");
        content.Should().Contain("-e .");
        content.Should().NotContain("flask==2.0.0", "editable install should replace original requirements.txt");
    }

    [Fact]
    public async Task BuildAsync_WithSetupPy_CreatesEditableRequirementsTxt()
    {
        // Arrange
        var projectDir = CreateTempDirectory();
        var publishDir = "publish";
        var publishPath = Path.Combine(projectDir, publishDir);

        // Create source files
        File.WriteAllText(Path.Combine(projectDir, "setup.py"), "from setuptools import setup\nsetup(name='test')");
        File.WriteAllText(Path.Combine(projectDir, "requirements.txt"), "django==3.2.0\ncelery==5.2.0");
        File.WriteAllText(Path.Combine(projectDir, "app.py"), "print('hello')");

        // Mock Python and pip executables
        MockPythonEnvironment();

        // Act
        await _builder.BuildAsync(projectDir, publishDir, verbose: false);

        // Assert
        var requirementsTxt = Path.Combine(publishPath, "requirements.txt");
        File.Exists(requirementsTxt).Should().BeTrue();

        var content = await File.ReadAllTextAsync(requirementsTxt);
        content.Should().Contain("--find-links dist");
        content.Should().Contain("--pre");
        content.Should().Contain("-e .");
        content.Should().NotContain("django==3.2.0", "editable install should replace original requirements.txt");
    }

    [Fact]
    public async Task BuildAsync_WithOnlyRequirementsTxt_PreservesOriginalRequirements()
    {
        // Arrange
        var projectDir = CreateTempDirectory();
        var publishDir = "publish";
        var publishPath = Path.Combine(projectDir, publishDir);

        // Create source files - NO pyproject.toml or setup.py
        var originalRequirements = "flask==2.0.0\nrequests==2.31.0\ngunicorn==20.1.0";
        File.WriteAllText(Path.Combine(projectDir, "requirements.txt"), originalRequirements);
        File.WriteAllText(Path.Combine(projectDir, "app.py"), "print('hello')");

        // Mock Python and pip executables
        MockPythonEnvironment();

        // Act
        await _builder.BuildAsync(projectDir, publishDir, verbose: false);

        // Assert
        var requirementsTxt = Path.Combine(publishPath, "requirements.txt");
        File.Exists(requirementsTxt).Should().BeTrue();

        var content = await File.ReadAllTextAsync(requirementsTxt);
        content.Should().Be(originalRequirements, "should preserve original dependencies exactly");
        content.Should().NotContain("-e .", "should not use editable install when no package metadata");
        content.Should().Contain("flask==2.0.0");
        content.Should().Contain("requests==2.31.0");
        content.Should().Contain("gunicorn==20.1.0");
    }

    [Fact]
    public async Task BuildAsync_WithBothPyprojectTomlAndRequirements_PrefersPyprojectToml()
    {
        // Arrange
        var projectDir = CreateTempDirectory();
        var publishDir = "publish";
        var publishPath = Path.Combine(projectDir, publishDir);

        // Create source files with both pyproject.toml and requirements.txt
        File.WriteAllText(Path.Combine(projectDir, "pyproject.toml"), "[project]\nname = \"test-project\"");
        File.WriteAllText(Path.Combine(projectDir, "requirements.txt"), "fastapi==0.95.0");
        File.WriteAllText(Path.Combine(projectDir, "app.py"), "print('hello')");

        // Mock Python and pip executables
        MockPythonEnvironment();

        // Act
        await _builder.BuildAsync(projectDir, publishDir, verbose: false);

        // Assert
        var requirementsTxt = Path.Combine(publishPath, "requirements.txt");
        File.Exists(requirementsTxt).Should().BeTrue();

        var content = await File.ReadAllTextAsync(requirementsTxt);
        content.Should().Contain("-e .", "should use editable install when pyproject.toml exists");
        content.Should().NotContain("fastapi==0.95.0", "should not copy requirements.txt when pyproject.toml exists");
    }

    [Fact]
    public async Task BuildAsync_WithoutRequirementsTxtAndNoPyprojectToml_CreatesMinimalRequirementsTxt()
    {
        // Arrange
        var projectDir = CreateTempDirectory();
        var publishDir = "publish";
        var publishPath = Path.Combine(projectDir, publishDir);

        // Create source files - NO requirements.txt, NO pyproject.toml, NO setup.py
        File.WriteAllText(Path.Combine(projectDir, "app.py"), "print('hello')");

        // Mock Python and pip executables
        MockPythonEnvironment();

        // Act
        await _builder.BuildAsync(projectDir, publishDir, verbose: false);

        // Assert
        var requirementsTxt = Path.Combine(publishPath, "requirements.txt");
        File.Exists(requirementsTxt).Should().BeTrue();

        var content = await File.ReadAllTextAsync(requirementsTxt);
        content.Should().Contain("# Auto-generated", "should create minimal file with comment");
    }

    private void MockPythonEnvironment()
    {
        // Mock python --version command
        _mockExecutor.ExecuteAsync(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("--version")),
            Arg.Any<string>(),
            Arg.Any<bool>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 0,
                StandardOutput = "Python 3.11.0",
                StandardError = string.Empty
            }));

        // Mock pip --version command
        _mockExecutor.ExecuteAsync(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("pip --version")),
            Arg.Any<string>(),
            Arg.Any<bool>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 0,
                StandardOutput = "pip 23.0.1",
                StandardError = string.Empty
            }));

        // Mock python -m py_compile command (syntax checking)
        _mockExecutor.ExecuteAsync(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("py_compile")),
            Arg.Any<string>(),
            Arg.Any<bool>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 0,
                StandardOutput = string.Empty,
                StandardError = string.Empty
            }));

        // Mock uv build command (may not exist)
        _mockExecutor.ExecuteAsync(
            Arg.Is<string>(s => s == "uv"),
            Arg.Is<string>(s => s.Contains("build")),
            Arg.Any<string>(),
            Arg.Any<bool>())
            .Returns(Task.FromResult(new CommandResult
            {
                ExitCode = 1,
                StandardOutput = string.Empty,
                StandardError = "uv not found"
            }));
    }

    private string CreateTempDirectory()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"a365test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempPath);
        _tempDirectories.Add(tempPath);
        return tempPath;
    }

    public void Dispose()
    {
        foreach (var tempDir in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
