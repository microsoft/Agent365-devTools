// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Helpers;

/// <summary>
/// Unit tests for DotNetProjectHelper
/// </summary>
public class DotNetProjectHelperTests : IDisposable
{
    [Fact]
    public void DetectTargetRuntimeVersion_Net8_Returns_8_0()
    {
        // Arrange
        var csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
        <PropertyGroup>
        <TargetFramework>net8.0</TargetFramework>
        </PropertyGroup>
        </Project>
        """;

        var path = WriteTempProjectFile(csproj);

        // Act
        var version = DotNetProjectHelper.DetectTargetRuntimeVersion(path, NullLogger.Instance);

        // Assert
        Assert.Equal("8.0", version);
    }

    [Fact]
    public void DetectTargetRuntimeVersion_Net9_Returns_9_0()
    {
        // Arrange
        var csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
        <PropertyGroup>
        <TargetFramework>net9.0</TargetFramework>
        </PropertyGroup>
        </Project>
        """;

        var path = WriteTempProjectFile(csproj);

        // Act
        var version = DotNetProjectHelper.DetectTargetRuntimeVersion(path, NullLogger.Instance);

        // Assert
        Assert.Equal("9.0", version);
    }

    [Fact]
    public void DetectTargetRuntimeVersion_MultipleTfms_Returns_First_ByDefault()
    {
        // Arrange
        // Current helper behavior: picks the first TFM (net8.0)
        var csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
        <PropertyGroup>
        <TargetFrameworks>net8.0;net9.0</TargetFrameworks>
        </PropertyGroup>
        </Project>
        """;

        var path = WriteTempProjectFile(csproj);

        // Act
        var version = DotNetProjectHelper.DetectTargetRuntimeVersion(path, NullLogger.Instance);

        // Assert
        Assert.Equal("8.0", version);
    }

    [Fact]
    public void DetectTargetRuntimeVersion_MissingTargetFramework_Returns_Null()
    {
        // Arrange
        var csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
        <PropertyGroup>
        <Nullable>enable</Nullable>
        </PropertyGroup>
        </Project>
        """;

        var path = WriteTempProjectFile(csproj);

        // Act
        var version = DotNetProjectHelper.DetectTargetRuntimeVersion(path, NullLogger.Instance);

        // Assert
        Assert.Null(version);
    }

    private static string WriteTempProjectFile(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "A365_CLI_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var file = Path.Combine(dir, "TestProject.csproj");
        File.WriteAllText(file, content);

        return file;
    }

    public void Dispose()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "A365_CLI_Tests");
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
