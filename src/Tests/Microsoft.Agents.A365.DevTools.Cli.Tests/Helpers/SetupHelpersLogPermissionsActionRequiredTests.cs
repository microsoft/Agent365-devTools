// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

/// <summary>
/// Tests for <see cref="SetupHelpers.LogPermissionsActionRequired"/>, which surfaces the same
/// copy-paste PowerShell action block that <c>setup all</c> produces from individual
/// <c>setup permissions *</c> subcommands when the caller is not a Global Administrator.
/// </summary>
public class SetupHelpersLogPermissionsActionRequiredTests
{
    private const string TenantId = "tenant-abc-123";
    private const string BlueprintId = "blueprint-def-456";
    private static readonly string BotApiAppId = ConfigConstants.MessagingBotApiAppId;

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _messages = [];
        public string AllOutput => string.Join("\n", _messages);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _messages.Add(formatter(state, exception));
    }

    private static SetupResults BuildResults() => new()
    {
        BlueprintId = BlueprintId,
        TenantId = TenantId,
    };

    [Fact]
    public void BotLikeSpecs_NoConsentUrl_NoAppRoles_EmitsGenericFallback()
    {
        var logger = new CapturingLogger();
        var specs = new List<ResourcePermissionSpec>
        {
            new(BotApiAppId, "Bot API", ["BotApi.Scope"], SetInheritable: true),
            new(ConfigConstants.ObservabilityApiAppId, "Observability API", [ConfigConstants.ObservabilityApiOtelWriteScope], SetInheritable: true),
        };

        SetupHelpers.LogPermissionsActionRequired(logger, BuildResults(), specs, adminConsentUrl: null);

        logger.AllOutput.Should().NotContain("Invoke-MgGraphRequest",
            because: "the orchestrator no longer POSTs to /oauth2PermissionGrants — all delegated scopes go through /v2.0/adminconsent");
        logger.AllOutput.Should().NotContain("oauth2PermissionGrants",
            because: "the legacy PowerShell snippet that targeted that endpoint has been removed");
        logger.AllOutput.Should().Contain("Ask a tenant administrator",
            because: "with no consent URL and no S2S app roles, the helper has nothing actionable to print and must fall back to the generic message");
    }

    [Fact]
    public void EmbedsBlueprintAndTenantContext()
    {
        var logger = new CapturingLogger();
        var specs = new List<ResourcePermissionSpec>
        {
            new(AuthenticationConstants.MicrosoftGraphResourceAppId, "Microsoft Graph", ["User.Read"], SetInheritable: true),
        };
        const string consentUrl = "https://login.microsoftonline.com/tenant/v2.0/adminconsent?client_id=blueprint";

        SetupHelpers.LogPermissionsActionRequired(logger, BuildResults(), specs, consentUrl);

        logger.AllOutput.Should().Contain(BlueprintId,
            because: "the admin must know which blueprint app ID the consent applies to");
        logger.AllOutput.Should().Contain(TenantId,
            because: "the admin must know which tenant to grant the consent in");
    }

    [Fact]
    public void GraphScopes_PresentsConsentUrl()
    {
        var logger = new CapturingLogger();
        var specs = new List<ResourcePermissionSpec>
        {
            new(AuthenticationConstants.MicrosoftGraphResourceAppId, "Microsoft Graph", ["User.Read"], SetInheritable: true),
        };
        const string consentUrl = "https://login.microsoftonline.com/tenant/adminconsent?client_id=blueprint";

        SetupHelpers.LogPermissionsActionRequired(logger, BuildResults(), specs, consentUrl);

        logger.AllOutput.Should().Contain(consentUrl,
            because: "Graph delegated scopes are granted via /adminconsent; the URL must be surfaced verbatim");
        logger.AllOutput.Should().NotContain("Invoke-MgGraphRequest",
            because: "Graph-only specs are covered by the consent URL — no per-resource PowerShell is needed");
    }

    [Fact]
    public void GraphPlusNonGraphSpecs_EmitsOnlyConsentUrl_NoOauth2PowerShell()
    {
        var logger = new CapturingLogger();
        var specs = new List<ResourcePermissionSpec>
        {
            new(AuthenticationConstants.MicrosoftGraphResourceAppId, "Microsoft Graph", ["User.Read"], SetInheritable: true),
            new(BotApiAppId, "Bot API", ["BotApi.Scope"], SetInheritable: true),
        };
        const string consentUrl = "https://login.microsoftonline.com/tenant/v2.0/adminconsent?client_id=blueprint";

        SetupHelpers.LogPermissionsActionRequired(logger, BuildResults(), specs, consentUrl);

        logger.AllOutput.Should().Contain(consentUrl,
            because: "the unified consent URL covers Graph and non-Graph delegated scopes in a single browser prompt");
        logger.AllOutput.Should().NotContain("Invoke-MgGraphRequest",
            because: "non-Graph delegated grants now go through /v2.0/adminconsent — the legacy per-resource PowerShell snippet has been removed");
        logger.AllOutput.Should().NotContain("oauth2PermissionGrants",
            because: "the helper must not direct admins at an endpoint the CLI no longer uses");
    }

    [Fact]
    public void NoActions_EmitsGenericFallback()
    {
        var logger = new CapturingLogger();
        var specs = new List<ResourcePermissionSpec>();

        SetupHelpers.LogPermissionsActionRequired(logger, BuildResults(), specs, adminConsentUrl: null);

        logger.AllOutput.Should().Contain("Ask a tenant administrator",
            because: "with nothing actionable to print, the helper must fall back to the legacy generic message");
        logger.AllOutput.Should().NotContain("Action Required",
            because: "the structured block must only appear when at least one concrete action is available");
    }

    [Fact]
    public void AppRoleSpecs_EmitNewMgServicePrincipalAppRoleAssignment()
    {
        var logger = new CapturingLogger();
        var specs = new List<ResourcePermissionSpec>
        {
            new(
                ConfigConstants.ObservabilityApiAppId,
                "Observability API",
                Scopes: [],
                SetInheritable: true,
                AppRoleScopes: [ConfigConstants.ObservabilityApiOtelWriteScope]),
        };

        SetupHelpers.LogPermissionsActionRequired(logger, BuildResults(), specs, adminConsentUrl: null);

        logger.AllOutput.Should().Contain("New-MgServicePrincipalAppRoleAssignment",
            because: "S2S app roles must be assigned via New-MgServicePrincipalAppRoleAssignment");
        logger.AllOutput.Should().Contain(ConfigConstants.ObservabilityApiOtelWriteScope,
            because: "the role value must be embedded so the admin grants the correct app role");
    }
}
