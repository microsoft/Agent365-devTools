// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

/// <summary>
/// Unit tests for SetupHelpers.BuildAdminConsentUrls and PopulateAdminConsentUrls.
/// </summary>
public class SetupHelpersConsentUrlTests
{
    private const string TenantId = "tenant-id-123";
    private const string BlueprintClientId = "blueprint-app-id-456";

    [Fact]
    public void BuildAdminConsentUrls_WithGraphAndMcpScopes_ReturnsUrlForEachResource()
    {
        var graphScopes = new[] { "Mail.Send", "Chat.ReadWrite" };
        var mcpScopes = new[] { "McpServers.Mail.All" };

        var urls = SetupHelpers.BuildAdminConsentUrls(TenantId, BlueprintClientId, graphScopes, mcpScopes);

        urls.Should().HaveCount(5);
        urls.Select(u => u.ResourceName).Should().Contain(new[]
        {
            "Microsoft Graph",
            "Agent 365 Tools",
            "Messaging Bot API",
            "Observability API",
            "Power Platform API"
        });
    }

    [Fact]
    public void BuildAdminConsentUrls_UrlsContainTenantAndClientId()
    {
        var urls = SetupHelpers.BuildAdminConsentUrls(
            TenantId, BlueprintClientId,
            new[] { "Mail.Send" },
            new[] { "McpServers.Mail.All" });

        foreach (var (_, url) in urls)
        {
            url.Should().Contain(TenantId);
            url.Should().Contain(BlueprintClientId);
        }
    }

    [Fact]
    public void BuildAdminConsentUrls_MessagingBotApi_UsesCorrectScopeConstant()
    {
        var urls = SetupHelpers.BuildAdminConsentUrls(TenantId, BlueprintClientId, new[] { "Mail.Send" }, new[] { "scope" });
        var botUrl = urls.First(u => u.ResourceName == "Messaging Bot API").ConsentUrl;

        // Scope should contain the constant value, URL-encoded under the identifier URI
        botUrl.Should().Contain(Uri.EscapeDataString($"{ConfigConstants.MessagingBotApiIdentifierUri}/{ConfigConstants.MessagingBotApiAdminConsentScope}"));
    }

    [Fact]
    public void BuildAdminConsentUrls_ObservabilityApi_UsesCorrectScopeConstant()
    {
        var urls = SetupHelpers.BuildAdminConsentUrls(TenantId, BlueprintClientId, new[] { "Mail.Send" }, new[] { "scope" });
        var obsUrl = urls.First(u => u.ResourceName == "Observability API").ConsentUrl;

        obsUrl.Should().Contain(Uri.EscapeDataString($"{ConfigConstants.ObservabilityApiIdentifierUri}/{ConfigConstants.ObservabilityApiAdminConsentScope}"));
    }

    [Fact]
    public void BuildAdminConsentUrls_PowerPlatformApi_UsesCorrectScopeConstant()
    {
        var urls = SetupHelpers.BuildAdminConsentUrls(TenantId, BlueprintClientId, new[] { "Mail.Send" }, new[] { "scope" });
        var ppUrl = urls.First(u => u.ResourceName == "Power Platform API").ConsentUrl;

        ppUrl.Should().Contain(Uri.EscapeDataString($"{PowerPlatformConstants.PowerPlatformApiIdentifierUri}/{PowerPlatformConstants.PermissionNames.ConnectivityConnectionsRead}"));
    }

    [Fact]
    public void BuildAdminConsentUrls_UrlsDoNotContainRawAmpersand_InScopeParam()
    {
        // Ensure '&' in the URL is only used as a query-string separator, not inside
        // the scope parameter (which would break browser-based consent flow).
        var urls = SetupHelpers.BuildAdminConsentUrls(
            TenantId, BlueprintClientId,
            new[] { "Mail.Send", "Chat.ReadWrite" },
            new[] { "McpServers.Mail.All" });

        foreach (var (_, url) in urls)
        {
            // Extract just the scope= value
            var scopeValue = url.Split("&scope=", 2)[1].Split('&')[0];
            scopeValue.Should().NotContain("&",
                because: "scopes must be joined with %20, not raw ampersands");
        }
    }

    [Fact]
    public void BuildAdminConsentUrls_EmptyGraphScopes_OmitsGraphEntry()
    {
        var urls = SetupHelpers.BuildAdminConsentUrls(TenantId, BlueprintClientId, Array.Empty<string>(), new[] { "scope" });

        urls.Should().NotContain(u => u.ResourceName == "Microsoft Graph");
    }

    [Fact]
    public void BuildAdminConsentUrls_EmptyMcpScopes_OmitsMcpEntry()
    {
        var urls = SetupHelpers.BuildAdminConsentUrls(TenantId, BlueprintClientId, new[] { "Mail.Send" }, Array.Empty<string>());

        urls.Should().NotContain(u => u.ResourceName == "Agent 365 Tools");
    }

    [Fact]
    public void PopulateAdminConsentUrls_UpsertsConsentUrlIntoResourceConsents()
    {
        var config = new Agent365Config
        {
            TenantId = TenantId,
            AgentBlueprintId = BlueprintClientId,
        };
        var mcpScopes = new[] { "McpServers.Mail.All" };

        var names = SetupHelpers.PopulateAdminConsentUrls(config, McpConstants.Agent365ToolsProdAppId, mcpScopes);

        names.Should().NotBeEmpty();
        config.ResourceConsents.Should().NotBeEmpty();
        config.ResourceConsents.Should().OnlyContain(rc => !string.IsNullOrWhiteSpace(rc.ConsentUrl));
    }

    [Fact]
    public void PopulateAdminConsentUrls_ReturnsResourceNamesForAllPopulatedUrls()
    {
        var config = new Agent365Config
        {
            TenantId = TenantId,
            AgentBlueprintId = BlueprintClientId,
        };

        var names = SetupHelpers.PopulateAdminConsentUrls(config, McpConstants.Agent365ToolsProdAppId, new[] { "scope" });

        names.Should().BeEquivalentTo(config.ResourceConsents.Select(rc => rc.ResourceName));
    }

    [Fact]
    public void PopulateAdminConsentUrls_WhenConsentAlreadyExists_UpdatesUrl()
    {
        var config = new Agent365Config
        {
            TenantId = TenantId,
            AgentBlueprintId = BlueprintClientId,
        };
        config.ResourceConsents.Add(new ResourceConsent
        {
            ResourceName = "Messaging Bot API",
            ResourceAppId = ConfigConstants.MessagingBotApiAppId,
            ConsentUrl = "https://old-url"
        });

        SetupHelpers.PopulateAdminConsentUrls(config, McpConstants.Agent365ToolsProdAppId, new[] { "scope" });

        var botConsent = config.ResourceConsents.First(rc => rc.ResourceName == "Messaging Bot API");
        botConsent.ConsentUrl.Should().NotBe("https://old-url",
            because: "existing entry should be updated with the freshly built URL");
    }
}
