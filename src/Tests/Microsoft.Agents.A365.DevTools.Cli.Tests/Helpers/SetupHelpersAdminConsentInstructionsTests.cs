// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

/// <summary>
/// Unit tests for SetupHelpers.LogNonDwAdminConsentInstructions and NonDwAdminConsentSpecs.
/// </summary>
public class SetupHelpersAdminConsentInstructionsTests
{
    private const string BlueprintId = "bp-app-id-123";
    private const string TenantId = "tenant-id-789";

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages => _messages;
        public string AllOutput => string.Join("\n", _messages);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _messages.Add(formatter(state, exception));
    }

    [Fact]
    public void LogNonDwAdminConsentInstructions_OptionA_ContainsDirectLinkWithBlueprintId()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId);

        logger.AllOutput.Should().Contain(BlueprintId,
            because: "the direct Entra portal link must embed the blueprint app ID");
        logger.AllOutput.Should().Contain("entra.microsoft.com",
            because: "Option A must link to the Entra portal");
        logger.AllOutput.Should().Contain("CallAnAPI",
            because: "the deep link must target the API permissions blade");
    }

    [Fact]
    public void LogNonDwAdminConsentInstructions_OptionA_ShowsDelegatedPermissionsInStep2()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId);

        var obsLines = logger.Messages
            .Where(m => m.Contains("Observability API") && m.Contains(ConfigConstants.ObservabilityApiOtelWriteScope))
            .ToList();
        obsLines.Should().HaveCount(1,
            because: "Observability API delegated scope must appear exactly once");
        obsLines[0].Should().Contain("Delegated",
            because: "only delegated grants are needed for OBO — no Application permissions");
    }

    [Fact]
    public void LogNonDwAdminConsentInstructions_DoesNotEmitOptionBPowerShell()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId);

        logger.AllOutput.Should().NotContain("Option B",
            because: "OBO mode needs only Entra portal consent — no PowerShell required");
        logger.AllOutput.Should().NotContain("Connect-MgGraph",
            because: "no PowerShell commands should be emitted for OBO-only consent");
        logger.AllOutput.Should().NotContain("New-MgServicePrincipalAppRoleAssignment",
            because: "app role assignments are not needed for delegated OBO grants");
    }

    [Fact]
    public void LogNonDwAdminConsentInstructions_CopyPasteBlock_ContainsBlueprintId()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId);

        logger.AllOutput.Should().Contain($"Blueprint : {BlueprintId}",
            because: "the copy-paste block must include the blueprint ID for the admin");
    }

    [Fact]
    public void LogNonDwAdminConsentInstructions_CopyPasteBlock_ContainsTenantIdWhenProvided()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId, tenantId: TenantId);

        logger.AllOutput.Should().Contain($"Tenant    : {TenantId}",
            because: "the copy-paste block must include the tenant ID when provided");
    }

    [Fact]
    public void LogNonDwAdminConsentInstructions_CopyPasteBlock_OmitsTenantIdWhenAbsent()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId);

        logger.AllOutput.Should().NotContain("Tenant    :",
            because: "tenant ID line must be omitted when no tenantId is passed");
    }

    [Fact]
    public void LogNonDwAdminConsentInstructions_CopyPasteBlock_ContainsDirectLink()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId);

        logger.AllOutput.Should().Contain("Grant admin consent:",
            because: "the copy-paste block must include a direct consent link label");
        logger.AllOutput.Should().Contain(BlueprintId,
            because: "the copy-paste block link must include the blueprint ID");
    }

    // ── NonDwAdminConsentSpecs contract ───────────────────────────────────────

    [Fact]
    public void NonDwAdminConsentSpecs_ContainsBothPermissionTypesForObservabilityApi()
    {
        var specs = SetupHelpers.NonDwAdminConsentSpecs;

        specs.Should().Contain(s => s.ResourceName == "Observability API" && s.PermissionType == "Application",
            because: "S2S token acquisition requires an app role assignment (Application permission)");
        specs.Should().Contain(s => s.ResourceName == "Observability API" && s.PermissionType == "Delegated",
            because: "OBO flow requires a delegated oauth2 grant");
    }

    [Fact]
    public void GetNonDwAdminConsentSpecs_ForGcc_UsesGccObservabilityResource()
    {
        var specs = SetupHelpers.GetNonDwAdminConsentSpecs("gcc");

        specs.Where(spec => spec.ResourceName == "Observability API")
            .Should().OnlyContain(
                spec => spec.ResourceAppId == ConfigConstants.GccObservabilityApiAppId,
                because: "manual GCC consent instructions must target the GCC Observability resource");
    }

    [Fact]
    public void NonDwAdminConsentSpecs_PowerPlatformApi_IsDelegatedOnly()
    {
        var specs = SetupHelpers.NonDwAdminConsentSpecs;

        specs.Should().NotContain(s => s.ResourceName == "Power Platform API" && s.PermissionType == "Application",
            because: "Power Platform API does not require application permissions in the non-DW flow");
        specs.Should().Contain(s => s.ResourceName == "Power Platform API" && s.PermissionType == "Delegated",
            because: "Power Platform API ConnectivityConnections.Read is a delegated scope");
    }
}
