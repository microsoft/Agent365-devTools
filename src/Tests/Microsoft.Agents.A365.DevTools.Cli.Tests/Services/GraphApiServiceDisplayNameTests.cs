// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Branch coverage for <see cref="GraphApiService.GetServicePrincipalDisplayNameByAppIdAsync"/>.
/// This method is the display-only resolver used by <c>query-entra inheritance</c> to render
/// readable service-principal names. The contract callers depend on:
///   - When the SP exists, return its displayName.
///   - When the SP is absent (empty value array), the underlying request returns null, or
///     the displayName property itself is missing, return null so the caller can fall back
///     to rendering the raw appId GUID instead of crashing or printing a confusing string.
/// </summary>
public class GraphApiServiceDisplayNameTests
{
    // Builds a partially-mocked GraphApiService whose virtual GraphGetAsync can be stubbed.
    // The no-op loginHintResolver prevents the real az subprocess from being spawned, matching
    // the pattern used by AgentBlueprintServiceTests.BuildServiceWithMockedGraph.
    private static GraphApiService BuildMockedGraph()
    {
        return Substitute.ForPartsOf<GraphApiService>(
            Substitute.For<ILogger<GraphApiService>>(),
            new CommandExecutor(Substitute.For<ILogger<CommandExecutor>>()),
            (Func<Task<string?>>?)(() => Task.FromResult<string?>(null)));
    }

    private static JsonDocument JsonDoc(string json) => JsonDocument.Parse(json);

    [Fact]
    public async Task GetServicePrincipalDisplayNameByAppIdAsync_SpFound_ReturnsDisplayName()
    {
        // Arrange
        var graph = BuildMockedGraph();
        graph.GraphGetAsync(
                "tenant-id",
                Arg.Is<string>(s => s.Contains("/v1.0/servicePrincipals?$filter=appId eq 'app-id'", StringComparison.Ordinal)
                                    && s.Contains("$select=displayName", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>(),
                Arg.Any<IEnumerable<string>?>())
            .Returns(JsonDoc(@"{ ""value"": [ { ""displayName"": ""Microsoft Graph"" } ] }"));

        // Act
        var result = await graph.GetServicePrincipalDisplayNameByAppIdAsync("tenant-id", "app-id");

        // Assert
        result.Should().Be("Microsoft Graph",
            because: "when Graph returns a service principal with a displayName the resolver must surface that name so operators see a readable resource identity instead of a raw appId GUID");
    }

    [Fact]
    public async Task GetServicePrincipalDisplayNameByAppIdAsync_SpNotInTenant_ReturnsNull()
    {
        // Arrange — Graph returned a well-formed envelope but no SP matched the filter.
        // This is the common case when an inheritance entry references an appId whose SP
        // has not been provisioned in the current tenant.
        var graph = BuildMockedGraph();
        graph.GraphGetAsync(
                "tenant-id",
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IEnumerable<string>?>())
            .Returns(JsonDoc(@"{ ""value"": [] }"));

        // Act
        var result = await graph.GetServicePrincipalDisplayNameByAppIdAsync("tenant-id", "missing-app-id");

        // Assert
        result.Should().BeNull(
            because: "an empty value array means the SP is not provisioned in this tenant; returning null lets the caller fall back to printing the raw appId rather than misreporting the resource");
    }

    [Fact]
    public async Task GetServicePrincipalDisplayNameByAppIdAsync_DisplayNameAbsent_ReturnsNull()
    {
        // Arrange — Graph returned a matching SP entry but the displayName property is
        // missing (e.g. due to an unexpected $select projection or a partial response).
        var graph = BuildMockedGraph();
        graph.GraphGetAsync(
                "tenant-id",
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IEnumerable<string>?>())
            .Returns(JsonDoc(@"{ ""value"": [ { } ] }"));

        // Act
        var result = await graph.GetServicePrincipalDisplayNameByAppIdAsync("tenant-id", "app-id");

        // Assert
        result.Should().BeNull(
            because: "a malformed entry without a displayName must not throw and must not surface an empty string — the caller relies on null to trigger the appId fallback");
    }

    [Fact]
    public async Task GetServicePrincipalDisplayNameByAppIdAsync_GraphReturnsNull_ReturnsNull()
    {
        // Arrange — GraphGetAsync itself returned null. This happens on transient errors,
        // 404s, or auth failures; the display-only resolver must swallow it because
        // query-entra inheritance is a read-only diagnostic and must not abort on lookup
        // failure for a single resource row.
        var graph = BuildMockedGraph();
        graph.GraphGetAsync(
                "tenant-id",
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IEnumerable<string>?>())
            .Returns((JsonDocument?)null);

        // Act
        var result = await graph.GetServicePrincipalDisplayNameByAppIdAsync("tenant-id", "app-id");

        // Assert
        result.Should().BeNull(
            because: "a null GraphGetAsync result represents a transient or auth failure; the display-only path must degrade to null so the diagnostic command can still render the rest of the report");
    }
}
