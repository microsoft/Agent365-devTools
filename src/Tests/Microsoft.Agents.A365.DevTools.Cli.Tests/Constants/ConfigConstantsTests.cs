// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Constants;

[Collection("ConfigTests")]
public class ConfigConstantsTests
{
    [Theory]
    [InlineData("gcc-high", "GCC_HIGH")]
    [InlineData("Gcc High", "GCC_HIGH")]
    [InlineData("gcch", "GCCH")]
    [InlineData("", "PROD")]
    public void NormalizeEnvironmentKey_ProducesEnvironmentVariableSuffix(
        string environment,
        string expected)
    {
        ConfigConstants.NormalizeEnvironmentKey(environment).Should().Be(expected);
    }

    [Fact]
    public void EnvironmentScopedOverrides_UseNormalizedCloudName()
    {
        const string appId = "11111111-2222-3333-4444-555555555555";
        const string discoverEndpoint = "https://tools.example/discover";

        WithEnvironmentVariable("A365_MCP_APP_ID_GCC_HIGH", appId, () =>
            ConfigConstants.GetAgent365ToolsResourceAppId("gcc-high").Should().Be(appId));
        WithEnvironmentVariable("A365_DISCOVER_ENDPOINT_GCC_HIGH", discoverEndpoint, () =>
            ConfigConstants.GetDiscoverEndpointUrl("gcc-high").Should().Be(discoverEndpoint));
    }

    [Fact]
    public void GraphBaseUrl_UsesScopedOverrideThenConfigThenDefault()
    {
        const string key = "A365_GRAPH_BASE_URL_GCC_HIGH";

        WithEnvironmentVariable(key, "https://scoped.example/", () =>
            ConfigConstants.GetGraphBaseUrl("gcc-high", "https://config.example")
                .Should().Be("https://scoped.example"));

        WithEnvironmentVariable(key, null, () =>
            ConfigConstants.GetGraphBaseUrl("gcc-high", "https://config.example/")
                .Should().Be("https://config.example"));

    }

    [Fact]
    public void AuthorityHost_UsesScopedOverrideThenConfigThenDefault()
    {
        const string key = "A365_AUTHORITY_HOST_GCC_HIGH";

        WithEnvironmentVariable(key, "https://login.scoped.example/", () =>
            ConfigConstants.GetAuthorityHost("gcc-high", "https://login.config.example")
                .Should().Be("https://login.scoped.example"));

        WithEnvironmentVariable(key, null, () =>
            ConfigConstants.GetAuthorityHost("gcc-high", "https://login.config.example/")
                .Should().Be("https://login.config.example"));

    }

    [Theory]
    [InlineData("prod", ConfigConstants.ObservabilityApiAppId)]
    [InlineData("commercial", ConfigConstants.ObservabilityApiAppId)]
    [InlineData("gcc", ConfigConstants.GccObservabilityApiAppId)]
    [InlineData("GCC Moderate", ConfigConstants.GccObservabilityApiAppId)]
    [InlineData("gcc-moderate", ConfigConstants.GccObservabilityApiAppId)]
    [InlineData("gcc-high", ConfigConstants.GccHighObservabilityApiAppId)]
    [InlineData("GCC High", ConfigConstants.GccHighObservabilityApiAppId)]
    [InlineData("dod", ConfigConstants.DodObservabilityApiAppId)]
    public void GetObservabilityApiAppId_ReturnsCloudSpecificResource(
        string environment,
        string expected)
    {
        ConfigConstants.GetObservabilityApiAppId(environment).Should().Be(expected,
            because: "Observability tokens and permission grants must target the resource deployed in the selected cloud");
    }

    [Fact]
    public void GetObservabilityApiIdentifierUri_UsesResolvedCloudResource()
    {
        ConfigConstants.GetObservabilityApiIdentifierUri("gcc")
            .Should().Be($"api://{ConfigConstants.GccObservabilityApiAppId}",
                because: "the admin-consent scope prefix must match the GCC Observability application ID");
    }

    [Fact]
    public void GetObservabilityApiAppId_WithAmbiguousAzureGovernmentCloud_Throws()
    {
        var act = () => ConfigConstants.GetObservabilityApiAppId("AzureUSGovernment");

        act.Should().Throw<ArgumentException>(
            because: "the Azure CLI government cloud name cannot distinguish GCC Moderate, GCC High, and DoD resources");
    }

    [Theory]
    [InlineData("http://graph.example")]
    [InlineData("https://user@graph.example")]
    [InlineData("https://graph.example/path")]
    [InlineData("https://graph.example?query=value")]
    [InlineData("https://graph.example#fragment")]
    public void GraphBaseUrl_RejectsValuesThatAreNotHttpsOrigins(string value)
    {
        WithEnvironmentVariable("A365_GRAPH_BASE_URL_GCC_HIGH", null, () =>
            FluentActions.Invoking(() => ConfigConstants.GetGraphBaseUrl("gcc-high", value))
                .Should().Throw<ArgumentException>());
    }

    private static void WithEnvironmentVariable(string name, string? value, Action assertion)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            assertion();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
