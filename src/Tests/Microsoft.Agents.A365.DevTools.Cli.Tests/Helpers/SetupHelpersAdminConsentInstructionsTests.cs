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
    private const string AgentIdentityId = "ai-app-id-456";

    // Captures formatted log messages for content assertions.
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
    public void LogNonDwAdminConsentInstructions_WithAgentIdentityAppId_EmitsOptionAStep7()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId, AgentIdentityId);

        logger.AllOutput.Should().Contain("7. Grant Application permissions to the agent identity",
            because: "step 7 must appear when agentIdentityAppId is provided");
        logger.AllOutput.Should().Contain(AgentIdentityId,
            because: "agent identity app ID must appear in step 7");
    }

    [Fact]
    public void LogNonDwAdminConsentInstructions_WithoutAgentIdentityAppId_OmitsStep7()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId);

        logger.AllOutput.Should().NotContain("7. Grant Application permissions",
            because: "step 7 must be omitted when no agent identity ID is given");
    }

    [Fact]
    public void LogNonDwAdminConsentInstructions_WithAgentIdentityAppId_EmitsAgentIdentityAppRoleAssignmentInOptionB()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId, AgentIdentityId);

        logger.AllOutput.Should().Contain("$ai = Get-MgServicePrincipal",
            because: "Option B must declare the $ai variable when agent identity is provided");
        logger.AllOutput.Should().Contain("-ServicePrincipalId $ai.Id -PrincipalId $ai.Id",
            because: "app role must be assigned to the agent identity SP");
        logger.AllOutput.Should().Contain(AgentIdentityId,
            because: "agent identity app ID must appear in the $ai lookup");
    }

    [Fact]
    public void LogNonDwAdminConsentInstructions_WithoutAgentIdentityAppId_OmitsAgentIdentityGrantInOptionB()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId);

        logger.AllOutput.Should().NotContain("$ai",
            because: "no $ai variable or assignment should appear when agent identity is absent");
    }

    [Fact]
    public void LogNonDwAdminConsentInstructions_AlwaysEmitsBlueprintAppRoleAssignmentInOptionB()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId);

        logger.AllOutput.Should().Contain("-ServicePrincipalId $bp.Id -PrincipalId $bp.Id",
            because: "blueprint app role assignment must always be emitted regardless of agent identity");
    }

    [Fact]
    public void LogNonDwAdminConsentInstructions_AlwaysEmitsDelegatedGrantForBlueprint()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId);

        logger.AllOutput.Should().Contain("oauth2PermissionGrants",
            because: "delegated oauth2 grant must always be emitted for blueprint");
        logger.AllOutput.Should().Contain("clientId = $bp.Id",
            because: "oauth2 grant clientId must reference the blueprint SP");
    }

    [Fact]
    public void LogNonDwAdminConsentInstructions_OptionAStep5_GroupsPermissionTypesForSameScope()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId);

        // Observability API has both Application and Delegated for the same scope.
        // Step 5 must list it once, not twice.
        var obsLines = logger.Messages
            .Where(m => m.Contains("Observability API") && m.Contains(ConfigConstants.ObservabilityApiOtelWriteScope))
            .ToList();
        obsLines.Should().HaveCount(1,
            because: "Observability API scope must appear once in step 5, grouped for both permission types");
        obsLines[0].Should().Contain("Application",
            because: "Application permission type must be listed");
        obsLines[0].Should().Contain("Delegated",
            because: "Delegated permission type must be listed");
    }

    [Fact]
    public void LogNonDwAdminConsentInstructions_DelegatedBlock_UsesSeparateIdVariable()
    {
        var logger = new CapturingLogger();

        SetupHelpers.LogNonDwAdminConsentInstructions(logger, BlueprintId);

        // The delegated block must use an Id-suffixed variable (e.g. $observabilityId)
        // so it does not overwrite the full SP object set in the app role assignment block.
        logger.AllOutput.Should().Contain("$observabilityId",
            because: "delegated block must use an Id-suffixed variable to avoid overwriting the SP object");
        logger.AllOutput.Should().Contain("$powerplatformId",
            because: "Power Platform API delegated entry must also use an Id-suffixed variable");
        logger.AllOutput.Should().Contain("resourceId = $observabilityId",
            because: "Invoke-MgGraphRequest body must reference the Id-suffixed variable");
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
    public void NonDwAdminConsentSpecs_PowerPlatformApi_IsDelegatedOnly()
    {
        var specs = SetupHelpers.NonDwAdminConsentSpecs;

        specs.Should().NotContain(s => s.ResourceName == "Power Platform API" && s.PermissionType == "Application",
            because: "Power Platform API does not require application permissions in the non-DW flow");
        specs.Should().Contain(s => s.ResourceName == "Power Platform API" && s.PermissionType == "Delegated",
            because: "Power Platform API ConnectivityConnections.Read is a delegated scope");
    }
}
