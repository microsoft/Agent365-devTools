// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Helpers;

/// <summary>
/// Unit tests for LoggerFactoryHelper
/// </summary>
[Collection("ConsoleOutput")]
public class LoggerFactoryHelperTests : IDisposable
{
    private readonly TextWriter _originalOut;
    private readonly StringWriter _testWriter;

    public LoggerFactoryHelperTests()
    {
        _originalOut = Console.Out;
        _testWriter = new StringWriter();
        Console.SetOut(_testWriter);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _testWriter.Dispose();
    }

    [Fact]
    public void CreateCleanLoggerFactory_ReturnsNonNullFactory()
    {
        // Act
        var factory = LoggerFactoryHelper.CreateCleanLoggerFactory();

        // Assert
        factory.Should().NotBeNull();
    }

    [Fact]
    public void CreateCleanLoggerFactory_MultipleCalls_CreateIndependentFactories()
    {
        // Act
        var factory1 = LoggerFactoryHelper.CreateCleanLoggerFactory();
        var factory2 = LoggerFactoryHelper.CreateCleanLoggerFactory();

        // Assert
        factory1.Should().NotBeNull();
        factory2.Should().NotBeNull();
        factory1.Should().NotBeSameAs(factory2);
    }

    [Fact]
    public void CreateCleanLoggerFactory_WithDifferentLevels_RespectMinimumLevel()
    {
        // Arrange
        var factoryInfo = LoggerFactoryHelper.CreateCleanLoggerFactory(LogLevel.Information);
        var factoryDebug = LoggerFactoryHelper.CreateCleanLoggerFactory(LogLevel.Debug);

        var loggerInfo = factoryInfo.CreateLogger("Test");
        var loggerDebug = factoryDebug.CreateLogger("Test");

        // Act & Assert
        loggerInfo.IsEnabled(LogLevel.Debug).Should().BeFalse();
        loggerDebug.IsEnabled(LogLevel.Debug).Should().BeTrue();
    }

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public void CreateCleanLoggerFactory_SupportsAllLogLevels(LogLevel minimumLevel)
    {
        // Act
        var factory = LoggerFactoryHelper.CreateCleanLoggerFactory(minimumLevel);
        var logger = factory.CreateLogger("Test");

        // Assert
        factory.Should().NotBeNull();
        logger.Should().NotBeNull();
        logger.IsEnabled(minimumLevel).Should().BeTrue();
    }

    [Fact]
    public void CreateCleanLoggerFactory_DisposeDoesNotThrow()
    {
        // Arrange
        var factory = LoggerFactoryHelper.CreateCleanLoggerFactory();

        // Act
        Action dispose = () => factory.Dispose();

        // Assert
        dispose.Should().NotThrow();
    }
}
