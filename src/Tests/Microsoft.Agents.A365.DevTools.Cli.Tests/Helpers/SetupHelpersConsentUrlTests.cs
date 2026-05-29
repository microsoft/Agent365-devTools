// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

/// <summary>
/// Unit tests for SetupHelpers.BuildAdminConsentUrls, PopulateAdminConsentUrls,
/// and BuildCombinedConsentUrl.
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

        botUrl.Should().Contain(Uri.EscapeDataString($"{ConfigConstants.MessagingBotApiIdentifierUri}/{ConfigConstants.MessagingBotApiAdminConsentScope}"),
            because: "scope URIs are Uri.EscapeDataString-encoded in the query string — required by AAD for adminconsent");
    }

    [Fact]
    public void BuildAdminConsentUrls_ObservabilityApi_UsesCorrectScopeConstant()
    {
        var urls = SetupHelpers.BuildAdminConsentUrls(TenantId, BlueprintClientId, new[] { "Mail.Send" }, new[] { "scope" });
        var obsUrl = urls.First(u => u.ResourceName == "Observability API").ConsentUrl;

        obsUrl.Should().Contain(Uri.EscapeDataString($"{ConfigConstants.ObservabilityApiIdentifierUri}/{ConfigConstants.ObservabilityApiOtelWriteScope}"),
            because: "OtelWrite is the published delegated scope on the Observability API used for admin consent");
    }

    [Fact]
    public void BuildAdminConsentUrls_PowerPlatformApi_UsesCorrectScopeConstant()
    {
        var urls = SetupHelpers.BuildAdminConsentUrls(TenantId, BlueprintClientId, new[] { "Mail.Send" }, new[] { "scope" });
        var ppUrl = urls.First(u => u.ResourceName == "Power Platform API").ConsentUrl;

        ppUrl.Should().Contain(Uri.EscapeDataString($"{PowerPlatformConstants.PowerPlatformApiIdentifierUri}/{PowerPlatformConstants.PermissionNames.ConnectivityConnectionsRead}"),
            because: "scope URIs are Uri.EscapeDataString-encoded in the query string — required by AAD for adminconsent");
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

        var names = SetupHelpers.PopulateAdminConsentUrls(config, McpConstants.WorkIQToolsProdAppId, mcpScopes);

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

        var names = SetupHelpers.PopulateAdminConsentUrls(config, McpConstants.WorkIQToolsProdAppId, new[] { "scope" });

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

        SetupHelpers.PopulateAdminConsentUrls(config, McpConstants.WorkIQToolsProdAppId, new[] { "scope" });

        var botConsent = config.ResourceConsents.First(rc => rc.ResourceName == "Messaging Bot API");
        botConsent.ConsentUrl.Should().NotBe("https://old-url",
            because: "existing entry should be updated with the freshly built URL");
    }

    // ── BuildCombinedConsentUrl ────────────────────────────────────────────────

    [Fact]
    public void BuildCombinedConsentUrl_ReturnsCorrectBaseUrlStructure()
    {
        var url = SetupHelpers.BuildCombinedConsentUrl(
            TenantId, BlueprintClientId,
            new[] { "Mail.Send" }, new[] { "McpServers.Mail.All" });

        url.Should().StartWith($"https://login.microsoftonline.com/{TenantId}/v2.0/adminconsent");
        url.Should().Contain($"client_id={BlueprintClientId}");
        url.Should().Contain($"redirect_uri={Uri.EscapeDataString(AuthenticationConstants.BlueprintConsentRedirectUri)}",
            because: "redirect_uri must be registered on the blueprint app — AADSTS500113 is returned if absent or unregistered");
    }

    [Fact]
    public void BuildCombinedConsentUrl_IncludesAllGraphScopes()
    {
        var url = SetupHelpers.BuildCombinedConsentUrl(
            TenantId, BlueprintClientId,
            new[] { "Mail.ReadWrite", "Mail.Send", "Chat.ReadWrite" }, Array.Empty<string>());

        url.Should().Contain(Uri.EscapeDataString($"{AuthenticationConstants.MicrosoftGraphResourceUri}/Mail.ReadWrite"),
            because: "scope URIs are Uri.EscapeDataString-encoded in the query string — required by AAD for adminconsent");
        url.Should().Contain(Uri.EscapeDataString($"{AuthenticationConstants.MicrosoftGraphResourceUri}/Mail.Send"));
        url.Should().Contain(Uri.EscapeDataString($"{AuthenticationConstants.MicrosoftGraphResourceUri}/Chat.ReadWrite"));
    }

    [Fact]
    public void BuildCombinedConsentUrl_IncludesAllMcpScopes()
    {
        var url = SetupHelpers.BuildCombinedConsentUrl(
            TenantId, BlueprintClientId,
            Array.Empty<string>(), new[] { "McpServers.Mail.All", "McpServersMetadata.Read.All" });

        url.Should().Contain(Uri.EscapeDataString($"{McpConstants.Agent365ToolsIdentifierUri}/McpServers.Mail.All"),
            because: "scope URIs are Uri.EscapeDataString-encoded in the query string — required by AAD for adminconsent");
        url.Should().Contain(Uri.EscapeDataString($"{McpConstants.Agent365ToolsIdentifierUri}/McpServersMetadata.Read.All"));
    }

    [Fact]
    public void BuildCombinedConsentUrl_AlwaysIncludesAllThreeFixedResources()
    {
        // Even with empty graph and MCP scopes, the three fixed resources must be present
        var url = SetupHelpers.BuildCombinedConsentUrl(
            TenantId, BlueprintClientId,
            Array.Empty<string>(), Array.Empty<string>());

        url.Should().Contain(Uri.EscapeDataString($"{ConfigConstants.MessagingBotApiIdentifierUri}/{ConfigConstants.MessagingBotApiAdminConsentScope}"),
            because: "scope URIs are Uri.EscapeDataString-encoded in the query string — required by AAD for adminconsent");
        url.Should().Contain(Uri.EscapeDataString($"{ConfigConstants.ObservabilityApiIdentifierUri}/{ConfigConstants.ObservabilityApiOtelWriteScope}"),
            because: "OtelWrite is the published delegated scope on the Observability API used for admin consent");
        url.Should().Contain(Uri.EscapeDataString($"{PowerPlatformConstants.PowerPlatformApiIdentifierUri}/{PowerPlatformConstants.PermissionNames.ConnectivityConnectionsRead}"));
    }

    [Fact]
    public void BuildCombinedConsentUrl_ScopesJoinedWithEncodedSpaceNotAmpersand()
    {
        var url = SetupHelpers.BuildCombinedConsentUrl(
            TenantId, BlueprintClientId,
            new[] { "Mail.Send", "Chat.ReadWrite" }, new[] { "McpServers.Mail.All" });

        // Extract the scope parameter value. BuildCombinedConsentUrl places scope before
        // redirect_uri, so splitting on "&scope=" then stopping at the next "&" is stable.
        var scopeParam = url.Split("&scope=", 2)[1].Split('&')[0];

        scopeParam.Should().NotContain("&",
            because: "scopes must be separated by %20, not raw ampersands");
        scopeParam.Should().Contain("%20",
            because: "multiple scopes must be space-separated using %20");
    }

    // ── isM365 gating ─────────────────────────────────────────────────────────
    //
    // Messaging Bot is the only resource gated on isM365 because non-M365 tenants
    // typically lack the Messaging Bot resource SP, and the /v2.0/adminconsent
    // endpoint returns AADSTS650053 when any requested scope is unknown. Graph,
    // MCP (Agent 365 Tools), Observability, and Power Platform SPs are provisioned
    // during blueprint permission stamping, so they must always appear in the
    // consent URLs whenever they have scopes.

    [Fact]
    public void BuildAdminConsentUrls_NonM365_ExcludesMessagingBotButKeepsAllOthers()
    {
        var urls = SetupHelpers.BuildAdminConsentUrls(
            TenantId, BlueprintClientId,
            new[] { "Mail.Send" }, new[] { "McpServers.Mail.All" },
            isM365: false);

        urls.Select(u => u.ResourceName).Should().BeEquivalentTo(new[]
        {
            "Microsoft Graph",
            "Agent 365 Tools",
            "Observability API",
            "Power Platform API",
        }, because: "non-M365 tenants lack the Messaging Bot resource SP — Bot would cause AADSTS650053 if included");
    }

    [Fact]
    public void BuildCombinedConsentUrl_NonM365_ExcludesBotScopeButKeepsGraphMcpObsPp()
    {
        var url = SetupHelpers.BuildCombinedConsentUrl(
            TenantId, BlueprintClientId,
            new[] { "Mail.Send" }, new[] { "McpServers.Mail.All" },
            isM365: false);

        url.Should().Contain(Uri.EscapeDataString($"{AuthenticationConstants.MicrosoftGraphResourceUri}/Mail.Send"),
            because: "Graph scopes are stamped on the blueprint for all agents — non-admin users need the combined URL to cover them");
        url.Should().Contain(Uri.EscapeDataString($"{McpConstants.Agent365ToolsIdentifierUri}/McpServers.Mail.All"),
            because: "MCP scopes are stamped on the blueprint when a ToolingManifest.json is present — non-admin users need the combined URL to cover them");
        url.Should().Contain(Uri.EscapeDataString($"{ConfigConstants.ObservabilityApiIdentifierUri}/{ConfigConstants.ObservabilityApiOtelWriteScope}"));
        url.Should().Contain(Uri.EscapeDataString($"{PowerPlatformConstants.PowerPlatformApiIdentifierUri}/{PowerPlatformConstants.PermissionNames.ConnectivityConnectionsRead}"));

        url.Should().NotContain(Uri.EscapeDataString(ConfigConstants.MessagingBotApiIdentifierUri),
            because: "Messaging Bot must be omitted from the combined URL for non-M365 agents to avoid AADSTS650053 when the Bot resource SP is absent from the tenant");
    }

    [Fact]
    public void PopulateAdminConsentUrls_NonM365_ResourceConsentsExcludeMessagingBot()
    {
        var config = new Agent365Config
        {
            TenantId = TenantId,
            AgentBlueprintId = BlueprintClientId,
        };

        var names = SetupHelpers.PopulateAdminConsentUrls(
            config, McpConstants.WorkIQToolsProdAppId, new[] { "McpServers.Mail.All" },
            isM365: false);

        names.Should().NotContain("Messaging Bot API",
            because: "non-M365 agents must not advertise a Messaging Bot consent URL — the resource SP is typically absent and the URL would return AADSTS650053");
        config.ResourceConsents.Should().NotContain(
            rc => rc.ResourceAppId == ConfigConstants.MessagingBotApiAppId,
            because: "no Messaging Bot consent URL is generated for non-M365 agents, so no resourceConsents entry should be persisted");
    }

    // ── V2 per-server audience routing (issue #429) ──────────────────────────
    //
    // V2 manifest entries declare a per-server audience (a unique Entra appId) and the
    // generic scope "Tools.ListInvoke.All". Each per-server SP exposes that scope on its
    // OWN identifierUri; WorkIQ Tools (the V1 shared resource) does NOT publish it.
    //
    // Pre-fix behavior collapsed every "Agent 365 Tools"-named spec onto the shared
    // WorkIQ URI (https://agent365.svc.cloud.microsoft), producing a URL that asked Entra
    // for Tools.ListInvoke.All on WorkIQ — which fails with AADSTS650053. The URL builders
    // must route per-server audiences through the bare appId GUID so each scope lands on
    // its actual SP. api://{appId} is NOT used — per-server SPs have identifierUris null
    // and the bare GUID is what's in servicePrincipalNames (api:// triggers AADSTS500011).

    private const string PerServerAudienceMail = "16b1878d-62c7-4009-aa25-68989d63bbad";
    private const string PerServerAudienceCalendar = "910333d2-47e9-43ca-981f-6df2f4531ef4";
    private const string V2Scope = "Tools.ListInvoke.All";

    [Fact]
    public void BuildCombinedConsentUrl_V2PerServerAudiences_EmitsBareAppIdResourcePerAudienceNotWorkIqUri()
    {
        var scopesByAudience = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [PerServerAudienceMail] = new[] { V2Scope },
            [PerServerAudienceCalendar] = new[] { V2Scope },
            // WorkIQ Tools still carries its V1-compat seed scope; it must keep the canonical URI.
            [McpConstants.WorkIQToolsProdAppId] = new[] { "McpServersMetadata.Read.All" },
        };

        var url = SetupHelpers.BuildCombinedConsentUrl(
            TenantId, BlueprintClientId,
            graphScopes: Array.Empty<string>(),
            mcpScopes: Array.Empty<string>(),
            isM365: false,
            mcpScopesByAudience: scopesByAudience);

        // Per-server V2 SPs (e.g. Work IQ Mail MCP, appId 16b1878d-...) have identifierUris
        // unset; their only registered resource identifier is the bare appId GUID in
        // servicePrincipalNames. The previous "api://{appId}" form caused AADSTS500011
        // ("resource principal not found"); the bare appId form is the canonical fallback
        // Entra accepts for SPs without a published Application ID URI.
        url.Should().Contain(Uri.EscapeDataString($"{PerServerAudienceMail}/{V2Scope}"),
            because: "V2 per-server SPs publish only the bare appId GUID as their resource identifier; api://{appId} produces AADSTS500011 because that URI is not registered on the SP (issue #429)");
        url.Should().NotContain(Uri.EscapeDataString($"api://{PerServerAudienceMail}/{V2Scope}"),
            because: "the api:// prefix must NOT be emitted for per-server audiences — that was the AADSTS500011 regression");
        url.Should().Contain(Uri.EscapeDataString($"{PerServerAudienceCalendar}/{V2Scope}"),
            because: "every V2 audience routes through its own appId; collapsing them would produce a single URL fragment that Entra rejects");
        url.Should().Contain(Uri.EscapeDataString($"{McpConstants.Agent365ToolsIdentifierUri}/McpServersMetadata.Read.All"),
            because: "the WorkIQ Tools (V1-shared) audience still uses the canonical https URI — the V2 fix must not regress V1 routing");
    }

    [Fact]
    public void BuildAdminConsentUrls_V2PerServerAudiences_OneUrlPerAudience()
    {
        var scopesByAudience = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [PerServerAudienceMail] = new[] { V2Scope },
            [PerServerAudienceCalendar] = new[] { V2Scope },
        };

        var urls = SetupHelpers.BuildAdminConsentUrls(
            TenantId, BlueprintClientId,
            graphScopes: new[] { "Mail.Send" },
            mcpScopes: Array.Empty<string>(),
            isM365: false,
            mcpScopesByAudience: scopesByAudience);

        urls.Should().Contain(u => u.ConsentUrl.Contains(Uri.EscapeDataString($"{PerServerAudienceMail}/{V2Scope}")),
            because: "the per-resource URL list must surface a URL the operator can hand off for the Mail MCP audience — collapsing it onto WorkIQ would point the operator at an SP that does not publish the scope");
        urls.Should().Contain(u => u.ConsentUrl.Contains(Uri.EscapeDataString($"{PerServerAudienceCalendar}/{V2Scope}")),
            because: "every per-server audience needs its own per-resource handoff URL");
    }

    [Fact]
    public void PopulateAdminConsentUrls_V2PerServerAudiences_AddsResourceConsentPerAudience()
    {
        var config = new Agent365Config
        {
            TenantId = TenantId,
            AgentBlueprintId = BlueprintClientId,
        };

        var scopesByAudience = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [PerServerAudienceMail] = new[] { V2Scope },
            [PerServerAudienceCalendar] = new[] { V2Scope },
        };

        var names = SetupHelpers.PopulateAdminConsentUrls(
            config, McpConstants.WorkIQToolsProdAppId,
            mcpScopes: Array.Empty<string>(),
            isM365: false,
            mcpScopesByAudience: scopesByAudience);

        config.ResourceConsents.Should().Contain(rc => rc.ResourceAppId == PerServerAudienceMail,
            because: "each V2 per-server audience needs its own ResourceConsent entry so query-entra and the setup summary surface the right SP for the operator to verify");
        config.ResourceConsents.Should().Contain(rc => rc.ResourceAppId == PerServerAudienceCalendar);
        names.Should().Contain(n => n.Contains(PerServerAudienceMail) || n.Contains("Mail", StringComparison.OrdinalIgnoreCase));
    }
}
