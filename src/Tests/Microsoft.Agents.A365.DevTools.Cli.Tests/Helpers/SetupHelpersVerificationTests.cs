// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

/// <summary>
/// Unit tests for SetupHelpers.DisplayVerificationInfoAsync.
/// Specifically guards against regressions in JSON property casing
/// and the "no URLs found → no header" behaviour.
/// </summary>
public class SetupHelpersVerificationTests : IDisposable
{
    private readonly ILogger _mockLogger;
    private readonly List<string> _logMessages;
    private readonly string _tempDir;

    public SetupHelpersVerificationTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _logMessages = new List<string>();
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _mockLogger.When(x => x.Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>()))
            .Do(callInfo =>
            {
                var state = callInfo.ArgAt<object>(2);
                if (state != null)
                    _logMessages.Add(state.ToString() ?? string.Empty);
            });
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// Verifies that camelCase JSON property names are read correctly.
    /// This is a regression test: the original code used PascalCase lookups
    /// (e.g. "AppServiceName") which silently produced no output against the
    /// actual camelCase JSON written by the CLI (e.g. "appServiceName").
    /// </summary>
    [Fact]
    public async Task DisplayVerificationInfoAsync_WithCamelCaseJson_EmitsAllThreeUrls()
    {
        // Arrange
        var generatedConfig = new
        {
            appServiceName = "my-web-app",
            resourceGroup = "my-rg",
            subscriptionId = "sub-123",
            agentBlueprintId = "blueprint-abc"
        };

        await WriteGeneratedConfigAsync(generatedConfig);
        var configFile = new FileInfo(Path.Combine(_tempDir, "a365.config.json"));

        // Act
        await SetupHelpers.DisplayVerificationInfoAsync(configFile, _mockLogger);

        // Assert — all three URL strings must appear in logged output
        _logMessages.Should().Contain(m => m.Contains("my-web-app.azurewebsites.net"),
            because: "appServiceName should produce an azurewebsites.net URL");
        _logMessages.Should().Contain(m => m.Contains("my-rg"),
            because: "resourceGroup should appear in the Azure portal resource group URL");
        _logMessages.Should().Contain(m => m.Contains("sub-123"),
            because: "subscriptionId should appear in the Azure portal resource group URL");
        _logMessages.Should().Contain(m => m.Contains("blueprint-abc"),
            because: "agentBlueprintId should appear in the Entra app registration URL");
        _logMessages.Should().Contain(m => m.Contains("Verification URLs:"),
            because: "header must be emitted when at least one URL is available");
    }

    /// <summary>
    /// Verifies that the "Verification URLs:" header is NOT emitted when the
    /// generated config contains none of the expected properties.
    /// Previously the header was always logged before the property checks,
    /// resulting in an empty section in the output.
    /// </summary>
    [Fact]
    public async Task DisplayVerificationInfoAsync_WithNoRelevantProperties_DoesNotEmitHeader()
    {
        // Arrange — valid JSON but none of the three expected properties
        await WriteGeneratedConfigAsync(new { tenantId = "tenant-only" });
        var configFile = new FileInfo(Path.Combine(_tempDir, "a365.config.json"));

        // Act
        await SetupHelpers.DisplayVerificationInfoAsync(configFile, _mockLogger);

        // Assert
        _logMessages.Should().NotContain(m => m.Contains("Verification URLs:"),
            because: "header must be suppressed when no URLs can be built");
    }

    private async Task WriteGeneratedConfigAsync(object content)
    {
        var path = Path.Combine(_tempDir, "a365.generated.config.json");
        var json = JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(path, json);
    }
}
