// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Constants;

/// <summary>
/// Pins the first-party Dataverse server classification that drives publish's "skip the A365 proxy
/// app" decision. The names mirror the MCP platform's OOBDataverseServerNamesToScopeMapping; matching
/// must be exact and case-insensitive, never a <c>msdyn_</c> prefix match, so a custom server that
/// merely starts with <c>msdyn_</c> is still treated as custom and gets its proxy app.
/// </summary>
public class FirstPartyMcpServersTests
{
    [Theory]
    [InlineData("msdyn_SalesMCPServer")]
    [InlineData("msdyn_ServiceMCPServer")]
    [InlineData("msdyn_ERPAnalyticsMCPServer")]
    [InlineData("msdyn_D365ContactCenterAdminMCPServer")]
    [InlineData("msdyn_ContactCenterMCPServer")]
    [InlineData("msdyn_DataverseMCPServer")]
    [InlineData("msdyn_DataversePreviewMCPServer")]
    [InlineData("msdyn_CIMCPServer")]
    [InlineData("msdyn_FnOMCPServer")]
    public void IsOobDataverseServer_ReturnsTrue_ForKnownOobNames(string serverName)
    {
        FirstPartyMcpServers.IsOobDataverseServer(serverName).Should().BeTrue(
            because: "publish must skip the A365 proxy app for servers the platform fronts with its own first-party app");
    }

    [Fact]
    public void IsOobDataverseServer_ReturnsTrue_ForNonMsdynBackwardsCompatName()
    {
        FirstPartyMcpServers.IsOobDataverseServer("MCP_DataverseMCPServer").Should().BeTrue(
            because: "the legacy backwards-compat Dataverse name has no msdyn_ prefix, so a prefix-only check would wrongly treat it as custom");
    }

    [Theory]
    [InlineData("msdyn_sAlESmcpSERVER")]
    [InlineData("MSDYN_DATAVERSEMCPSERVER")]
    public void IsOobDataverseServer_IsCaseInsensitive(string serverName)
    {
        FirstPartyMcpServers.IsOobDataverseServer(serverName).Should().BeTrue(
            because: "server-name casing from discovery is not guaranteed, so classification must be case-insensitive to match the platform");
    }

    [Theory]
    [InlineData("mcp_TestServer")]
    [InlineData("msdyn_SomethingCustomMCPServer")]
    [InlineData("msdyn_")]
    [InlineData("contoso_SalesMCPServer")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsOobDataverseServer_ReturnsFalse_ForCustomOrEmptyNames(string? serverName)
    {
        FirstPartyMcpServers.IsOobDataverseServer(serverName).Should().BeFalse(
            because: "only exact matches to the known first-party list skip the proxy app; a msdyn_-prefixed-but-unknown or empty name is custom and must get a proxy app");
    }
}
