// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Requirements;

/// <summary>
/// Tests for WidsOptionalClaimRequirementCheck. The requirement check is the user-facing surface
/// for the `wids` optional-claim detection introduced in this branch — when wids is missing on
/// the access token, role detection silently returns Unknown and the AllPrincipals OAuth2 grant
/// phase is skipped. The visible symptom is a blueprint with kind=allAllowed but no permissions
/// actually granted on the blueprint SP. These tests pin the requirement-check contract for each
/// branch so the user-facing remediation text and the success/failure routing don't regress.
/// </summary>
public class WidsOptionalClaimRequirementCheckTests
{
    private readonly ILogger _logger;
    private readonly IClientAppValidator _validator;

    private const string ValidClientAppId = "11111111-1111-1111-1111-111111111111";
    private const string ValidTenantId = "22222222-2222-2222-2222-222222222222";

    public WidsOptionalClaimRequirementCheckTests()
    {
        _logger = Substitute.For<ILogger>();
        _validator = Substitute.For<IClientAppValidator>();
    }

    [Fact]
    public async Task CheckAsync_MissingClientAppId_ReturnsFailureWithRunSetupBlueprintGuidance()
    {
        var check = new WidsOptionalClaimRequirementCheck(_validator);
        var config = new Agent365Config { TenantId = ValidTenantId };  // ClientAppId omitted

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(
            because: "the wids check cannot inspect optionalClaims without a clientAppId — the requirement must fail loudly rather than skipping silently");
        result.ErrorMessage.Should().Contain("clientAppId is not configured",
            because: "the operator must see exactly which config field is missing so they can fix it");
        result.ResolutionGuidance.Should().NotBeNullOrWhiteSpace(
            because: "every failure must give the operator a concrete next step (run `a365 setup blueprint` or edit a365.config.json)");
    }

    [Fact]
    public async Task CheckAsync_MissingTenantId_ReturnsFailureWithTenantIdGuidance()
    {
        var check = new WidsOptionalClaimRequirementCheck(_validator);
        var config = new Agent365Config { ClientAppId = ValidClientAppId };  // TenantId omitted

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().Contain("tenantId is not configured");
        result.ResolutionGuidance.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CheckAsync_WhenWidsPresent_ReturnsSuccess_AndDoesNotProbeBeyondOptionalClaims()
    {
        var check = new WidsOptionalClaimRequirementCheck(_validator);
        var config = new Agent365Config { ClientAppId = ValidClientAppId, TenantId = ValidTenantId };
        _validator.HasWidsAccessTokenOptionalClaimAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(
            because: "wids is on the access token so role detection will work — the check has nothing to do");
        result.Details.Should().Contain(ValidClientAppId,
            because: "the success details must identify which app was inspected so the operator can correlate with their app registration");
    }

    [Fact]
    public async Task CheckAsync_WhenWidsAbsent_ReturnsFailureWithPortalAndAzRestRemediation()
    {
        var check = new WidsOptionalClaimRequirementCheck(_validator);
        var config = new Agent365Config { ClientAppId = ValidClientAppId, TenantId = ValidTenantId };
        _validator.HasWidsAccessTokenOptionalClaimAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(
            because: "without wids the AllPrincipals OAuth2 grant phase silently skips and inheritance is empty — the requirement must fail so the operator knows the agent will not work");
        result.ErrorMessage.Should().Contain("'wids' optional claim",
            because: "operators searching logs or chat history for 'wids' must find this exact phrase to correlate with the docs");
        result.ResolutionGuidance.Should().Contain(ValidClientAppId,
            because: "the portal URL and az rest command must embed the actual appId so the operator can copy-paste");
        result.ResolutionGuidance.Should().Contain("Add optional claim",
            because: "the portal click-path must name the exact UI button so an operator unfamiliar with token configuration can find it");
        result.ResolutionGuidance.Should().Contain("az rest",
            because: "scriptable runs need the CLI command alongside the portal click-path");
    }

    [Fact]
    public async Task CheckAsync_WhenValidatorThrowsCancellation_PropagatesCancellation()
    {
        var check = new WidsOptionalClaimRequirementCheck(_validator);
        var config = new Agent365Config { ClientAppId = ValidClientAppId, TenantId = ValidTenantId };
        _validator.HasWidsAccessTokenOptionalClaimAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => check.CheckAsync(config, _logger, new System.Threading.CancellationToken(canceled: true)));
        // Contract: cancellation must propagate so the calling pipeline can abort the entire setup
        // — swallowing it into a Failure result would make Ctrl+C unresponsive.
    }

    [Fact]
    public async Task CheckAsync_WhenValidatorThrowsGenericException_ReturnsFailureWithExceptionDetails()
    {
        var check = new WidsOptionalClaimRequirementCheck(_validator);
        var config = new Agent365Config { ClientAppId = ValidClientAppId, TenantId = ValidTenantId };
        _validator.HasWidsAccessTokenOptionalClaimAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new System.Net.Http.HttpRequestException("transient network failure"));

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(
            because: "a Graph read failure means the check could not verify wids; the requirement must surface the failure rather than guess");
        result.ErrorMessage.Should().Contain("transient network failure",
            because: "the underlying exception message must reach the operator so they can diagnose (DNS, auth, connectivity)");
        result.ResolutionGuidance.Should().Contain("Application.Read.All",
            because: "the most common cause of a read failure here is that the CLI app does not have Application.Read.All consented yet — pointing the operator at the missing consent is the highest-value hint");
    }
}
