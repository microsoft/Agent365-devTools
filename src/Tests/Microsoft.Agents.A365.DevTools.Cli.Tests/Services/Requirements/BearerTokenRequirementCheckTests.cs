// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Requirements;

public class BearerTokenRequirementCheckTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ILogger _logger = Substitute.For<ILogger>();

    public BearerTokenRequirementCheckTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"a365-bearer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static PlatformDetector CreateDetector() =>
        new(Substitute.For<ILogger<PlatformDetector>>());

    [Fact]
    public async Task CheckAsync_DotNet_WithBearerToken_ReturnsSuccess()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir, "Program.cs"), "");
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var propsDir = Directory.CreateDirectory(Path.Combine(_tempDir, "Properties"));
        File.WriteAllText(Path.Combine(propsDir.FullName, "launchSettings.json"), $$"""
            {
              "profiles": {
                "MyApp": {
                  "environmentVariables": {
                    "{{AuthenticationConstants.BearerTokenEnvironmentVariable}}": "eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9"
                  }
                }
              }
            }
            """);

        var check = new BearerTokenRequirementCheck(CreateDetector());
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeTrue(because: "bearer token is present in launchSettings.json");
        result.Details.Should().Contain("launchSettings.json");
    }

    [Fact]
    public async Task CheckAsync_DotNet_WithoutBearerToken_ReturnsFailure()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir, "Program.cs"), "");
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var propsDir = Directory.CreateDirectory(Path.Combine(_tempDir, "Properties"));
        File.WriteAllText(Path.Combine(propsDir.FullName, "launchSettings.json"), """
            {
              "profiles": {
                "MyApp": {
                  "environmentVariables": {
                    "ASPNETCORE_ENVIRONMENT": "Development"
                  }
                }
              }
            }
            """);

        var check = new BearerTokenRequirementCheck(CreateDetector());
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: "BEARER_TOKEN is not set in launchSettings.json");
        result.ErrorMessage.Should().Contain("BEARER_TOKEN");
        result.ResolutionGuidance.Should().Contain("a365 develop get-token");
    }

    [Fact]
    public async Task CheckAsync_DotNet_NoLaunchSettings_ReturnsFailure()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir, "Program.cs"), "");
        File.WriteAllText(Path.Combine(_tempDir, "test.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var check = new BearerTokenRequirementCheck(CreateDetector());
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: "launchSettings.json does not exist");
        result.ErrorMessage.Should().Contain("launchSettings.json");
    }

    [Fact]
    public async Task CheckAsync_NodeJs_WithBearerToken_ReturnsSuccess()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_tempDir, ".env"),
            $"{AuthenticationConstants.BearerTokenEnvironmentVariable}=eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9\n");

        var check = new BearerTokenRequirementCheck(CreateDetector());
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeTrue(because: "bearer token is present in .env file");
        result.Details.Should().Contain(".env");
    }

    [Fact]
    public async Task CheckAsync_NodeJs_WithoutBearerToken_ReturnsFailure()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_tempDir, ".env"), "PORT=3978\n");

        var check = new BearerTokenRequirementCheck(CreateDetector());
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: "BEARER_TOKEN is not in .env file");
        result.ResolutionGuidance.Should().Contain("a365 develop get-token");
    }

    [Fact]
    public async Task CheckAsync_NodeJs_NoEnvFile_ReturnsFailure()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), "{}");

        var check = new BearerTokenRequirementCheck(CreateDetector());
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: ".env file does not exist");
        result.ErrorMessage.Should().Contain(".env");
    }

    [Fact]
    public async Task CheckAsync_Python_WithBearerToken_ReturnsSuccess()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir, "requirements.txt"), "");
        File.WriteAllText(Path.Combine(_tempDir, ".env"),
            $"{AuthenticationConstants.BearerTokenEnvironmentVariable}=\"eyJtoken\"\n");

        var check = new BearerTokenRequirementCheck(CreateDetector());
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeTrue(because: "bearer token is present in .env file for Python");
    }

    [Fact]
    public async Task CheckAsync_EmptyTokenValue_ReturnsFailure()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_tempDir, ".env"),
            $"{AuthenticationConstants.BearerTokenEnvironmentVariable}=\n");

        var check = new BearerTokenRequirementCheck(CreateDetector());
        var config = new Agent365Config { DeploymentProjectPath = _tempDir };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: "empty token value should not be accepted");
    }

    [Fact]
    public async Task CheckAsync_ProjectPathDoesNotExist_ReturnsFailure()
    {
        // Arrange
        var check = new BearerTokenRequirementCheck(CreateDetector());
        var config = new Agent365Config { DeploymentProjectPath = Path.Combine(_tempDir, "nonexistent") };

        // Act
        var result = await check.CheckAsync(config, _logger);

        // Assert
        result.Passed.Should().BeFalse(because: "project path does not exist");
    }

    [Fact]
    public void CheckLaunchSettings_EmptyBearerToken_ReturnsFailure()
    {
        var propsDir = Directory.CreateDirectory(Path.Combine(_tempDir, "Properties"));
        File.WriteAllText(Path.Combine(propsDir.FullName, "launchSettings.json"), """
            {
              "profiles": {
                "MyApp": {
                  "environmentVariables": {
                    "BEARER_TOKEN": ""
                  }
                }
              }
            }
            """);

        var result = BearerTokenRequirementCheck.CheckLaunchSettings(
            _tempDir, AuthenticationConstants.BearerTokenEnvironmentVariable);

        result.Passed.Should().BeFalse(because: "empty token value should not pass");
    }
}
