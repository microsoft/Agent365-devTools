// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.CommandLine;
using System.CommandLine.Parsing;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for the network command tree, its handlers and its result reporting.
/// Handlers are driven through InvokeAsync against substituted services so that tenant
/// resolution, confirmation and exit codes are covered, not just option parsing.
/// </summary>
public class NetworkCommandTests
{
    private const string OperationId = "op-abc";
    private const string TenantId = "tid";

    private const string PolicyArmId =
        "/subscriptions/8d1e5b21-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.PowerPlatform/enterprisePolicies/p";

    private static Command CreateCommand(
        IVNetLinkService? vnet = null,
        IAzureCliService? azure = null,
        IConfirmationProvider? confirmation = null,
        IGsaService? gsa = null) =>
        NetworkCommand.CreateCommand(
            NullLogger.Instance,
            vnet ?? Substitute.For<IVNetLinkService>(),
            azure ?? SignedInAzureCli(),
            gsa ?? Substitute.For<IGsaService>(),
            confirmation ?? Confirming(true));

    private static IAzureCliService SignedInAzureCli(string? tenantId = TenantId)
    {
        var azure = Substitute.For<IAzureCliService>();
        azure.GetCurrentAccountAsync().Returns(
            Task.FromResult<AzureAccountInfo?>(tenantId == null ? null : new AzureAccountInfo { TenantId = tenantId }));
        return azure;
    }

    private static IConfirmationProvider Confirming(bool answer)
    {
        var confirmation = Substitute.For<IConfirmationProvider>();
        confirmation.ConfirmAsync(Arg.Any<string>()).Returns(Task.FromResult(answer));
        return confirmation;
    }

    // ──────────────────────────── Command tree shape ────────────────────────────

    [Fact]
    public void CreateCommand_ExposesTheVnetSubcommandTree()
    {
        var command = CreateCommand();

        command.Name.Should().Be("network");

        var vnet = command.Subcommands.Single(c => c.Name == "vnet");
        vnet.Subcommands.Select(c => c.Name).Should().BeEquivalentTo("link", "unlink", "status");
    }

    [Fact]
    public void CreateCommand_ExposesTheGsaSubcommandTree()
    {
        var command = CreateCommand();

        command.Subcommands.Select(c => c.Name).Should().BeEquivalentTo("vnet", "gsa");

        var gsa = command.Subcommands.Single(c => c.Name == "gsa");
        gsa.Subcommands.Select(c => c.Name).Should().BeEquivalentTo("enable", "disable", "status");
    }

    [Fact]
    public void LinkSubcommand_RequiresPolicyArmIdAndOffersTheDocumentedOptions()
    {
        var link = CreateCommand().Subcommands[0].Subcommands.Single(c => c.Name == "link");

        link.Options.Select(o => o.Name).Should()
            .BeEquivalentTo("policy-arm-id", "swap", "tenant-id", "wait", "yes", "verbose");
        link.Options.Single(o => o.Name == "policy-arm-id").IsRequired.Should().BeTrue();
        link.Options.Single(o => o.Name == "swap").IsRequired.Should().BeFalse();
    }

    [Fact]
    public void UnlinkSubcommand_TakesNoPolicyBecauseThePlatformStoredIt()
    {
        var unlink = CreateCommand().Subcommands[0].Subcommands.Single(c => c.Name == "unlink");

        unlink.Options.Select(o => o.Name).Should().BeEquivalentTo("wait", "tenant-id", "yes", "verbose");
    }

    [Fact]
    public void StatusSubcommand_AcceptsAnOperationHandle()
    {
        var status = CreateCommand().Subcommands[0].Subcommands.Single(c => c.Name == "status");

        status.Options.Select(o => o.Name).Should().BeEquivalentTo("operation-id", "tenant-id", "verbose");
    }

    [Fact]
    public void LinkSubcommand_ParsesItsOptions()
    {
        var command = CreateCommand();

        var parsed = command.Parse("vnet link --policy-arm-id /p/1 --swap --tenant-id tid --wait");

        parsed.Errors.Should().BeEmpty();
    }

    [Fact]
    public void LinkSubcommand_WithoutPolicyArmId_FailsToParse()
    {
        var command = CreateCommand();

        var parsed = command.Parse("vnet link");

        parsed.Errors.Should().NotBeEmpty(because: "--policy-arm-id is required");
    }

    // ──────────────────────────── Handler invocation ────────────────────────────

    [Fact]
    public async Task LinkHandler_ResolvesTheTenantFromAzLoginAndCallsTheService()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.LinkAsync(PolicyArmId, false, TenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(new VNetStatusResponse { Status = "Linked" }));
        var command = CreateCommand(vnet);

        var exitCode = await command.InvokeAsync($"vnet link --policy-arm-id {PolicyArmId}");

        exitCode.Should().Be(0);
        await vnet.Received(1).LinkAsync(PolicyArmId, false, TenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkHandler_PrefersAnExplicitTenantOverTheAzLoginTenant()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.LinkAsync(PolicyArmId, false, "other-tenant", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(new VNetStatusResponse { Status = "Linked" }));
        var command = CreateCommand(vnet);

        var exitCode = await command.InvokeAsync(
            $"vnet link --policy-arm-id {PolicyArmId} --tenant-id other-tenant");

        exitCode.Should().Be(0);
        await vnet.Received(1).LinkAsync(PolicyArmId, false, "other-tenant", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkHandler_WithoutSwap_DoesNotPrompt()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.LinkAsync(PolicyArmId, false, TenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(new VNetStatusResponse { Status = "Linked" }));
        var confirmation = Confirming(false);
        var command = CreateCommand(vnet, confirmation: confirmation);

        var exitCode = await command.InvokeAsync($"vnet link --policy-arm-id {PolicyArmId}");

        exitCode.Should().Be(0, because: "a conflicting link is reported, not replaced, without --swap");
        await confirmation.DidNotReceive().ConfirmAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task LinkHandler_WhenSwapDeclined_DoesNotCallTheService()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        var command = CreateCommand(vnet, confirmation: Confirming(false));

        var exitCode = await command.InvokeAsync($"vnet link --policy-arm-id {PolicyArmId} --swap");

        exitCode.Should().Be(1);
        await vnet.DidNotReceive().LinkAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkHandler_WhenSwapAndYes_SkipsThePromptAndCallsTheService()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.LinkAsync(PolicyArmId, true, TenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(new VNetStatusResponse { Status = "Linked" }));
        var confirmation = Confirming(false);
        var command = CreateCommand(vnet, confirmation: confirmation);

        var exitCode = await command.InvokeAsync($"vnet link --policy-arm-id {PolicyArmId} --swap --yes");

        exitCode.Should().Be(0);
        await confirmation.DidNotReceive().ConfirmAsync(Arg.Any<string>());
        await vnet.Received(1).LinkAsync(PolicyArmId, true, TenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkHandler_WhenTheServiceFails_ReturnsFailure()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.LinkAsync(PolicyArmId, false, TenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(null));
        var command = CreateCommand(vnet);

        var exitCode = await command.InvokeAsync($"vnet link --policy-arm-id {PolicyArmId}");

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task LinkHandler_WithWait_PollsUntilTheOperationSettles()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.LinkAsync(PolicyArmId, false, TenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(
                new VNetStatusResponse { Status = "Running", OperationId = OperationId }));
        vnet.WaitForCompletionAsync(TenantId, OperationId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(new VNetStatusResponse { Status = "Linked" }));
        var command = CreateCommand(vnet);

        var exitCode = await command.InvokeAsync($"vnet link --policy-arm-id {PolicyArmId} --wait");

        exitCode.Should().Be(0);
        await vnet.Received(1).WaitForCompletionAsync(
            TenantId, OperationId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkHandler_WhenNoTenantCanBeResolved_FailsWithoutCallingTheService()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        var command = CreateCommand(vnet, SignedInAzureCli(tenantId: null));

        var exitCode = await command.InvokeAsync($"vnet link --policy-arm-id {PolicyArmId}");

        exitCode.Should().Be(1);
        await vnet.DidNotReceive().LinkAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkHandler_WhenTenantIdSuppliedButBlank_FailsWithoutFallingBackToAzLogin()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        var azure = SignedInAzureCli();
        var command = CreateCommand(vnet, azure);

        var exitCode = await command.InvokeAsync(
            ["vnet", "link", "--policy-arm-id", PolicyArmId, "--tenant-id", "  "]);

        exitCode.Should().Be(1, because: "a blank tenant is a mistake, not a request for the default");
        await azure.DidNotReceive().GetCurrentAccountAsync();
        await vnet.DidNotReceive().LinkAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnlinkHandler_PromptsThenCallsTheService()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.UnlinkAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(new VNetStatusResponse { Status = "NotLinked" }));
        var confirmation = Confirming(true);
        var command = CreateCommand(vnet, confirmation: confirmation);

        var exitCode = await command.InvokeAsync("vnet unlink");

        exitCode.Should().Be(0);
        await confirmation.Received(1).ConfirmAsync(Arg.Any<string>());
        await vnet.Received(1).UnlinkAsync(TenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnlinkHandler_WhenDeclined_DoesNotCallTheService()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        var command = CreateCommand(vnet, confirmation: Confirming(false));

        var exitCode = await command.InvokeAsync("vnet unlink");

        exitCode.Should().Be(1);
        await vnet.DidNotReceive().UnlinkAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnlinkHandler_WithYes_SkipsThePrompt()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.UnlinkAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(new VNetStatusResponse { Status = "NotLinked" }));
        var confirmation = Confirming(false);
        var command = CreateCommand(vnet, confirmation: confirmation);

        var exitCode = await command.InvokeAsync("vnet unlink --yes");

        exitCode.Should().Be(0);
        await confirmation.DidNotReceive().ConfirmAsync(Arg.Any<string>());
        await vnet.Received(1).UnlinkAsync(TenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StatusHandler_ReadsTheCurrentLinkWithoutPrompting()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.GetStatusAsync(TenantId, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(
                new VNetStatusResponse { Status = "Linked", PolicyArmId = PolicyArmId }));
        var confirmation = Confirming(false);
        var command = CreateCommand(vnet, confirmation: confirmation);

        var exitCode = await command.InvokeAsync("vnet status");

        exitCode.Should().Be(0);
        await confirmation.DidNotReceive().ConfirmAsync(Arg.Any<string>());
        await vnet.Received(1).GetStatusAsync(TenantId, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StatusHandler_PassesTheOperationHandleThrough()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.GetStatusAsync(TenantId, OperationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(
                new VNetStatusResponse { Status = "Running", OperationId = OperationId }));
        var command = CreateCommand(vnet);

        var exitCode = await command.InvokeAsync($"vnet status --operation-id {OperationId}");

        exitCode.Should().Be(0);
        await vnet.Received(1).GetStatusAsync(TenantId, OperationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StatusHandler_WhenFailed_ReturnsFailure()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.GetStatusAsync(TenantId, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(
                new VNetStatusResponse { Status = "Failed", Reason = "Region mismatch." }));
        var command = CreateCommand(vnet);

        var exitCode = await command.InvokeAsync("vnet status");

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task StatusHandler_WhenStatusUnreadable_ReturnsFailure()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.GetStatusAsync(TenantId, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(null));
        var command = CreateCommand(vnet);

        var exitCode = await command.InvokeAsync("vnet status");

        exitCode.Should().Be(1);
    }

    // ───────────────────────────────── ReportAsync ──────────────────────────────

    [Fact]
    public async Task ReportAsync_WhenResultNull_ReturnsFailure()
    {
        var vnet = Substitute.For<IVNetLinkService>();

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result: null, wait: true, "Link", TenantId, CancellationToken.None);

        exitCode.Should().Be(1);
        await vnet.DidNotReceive().WaitForCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportAsync_WhenSettled_ReturnsSuccessWithoutWaiting()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        var result = new VNetStatusResponse { Status = "Linked" };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: true, "Link", TenantId, CancellationToken.None);

        exitCode.Should().Be(0);
        await vnet.DidNotReceive().WaitForCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportAsync_WhenFailed_ReturnsFailure()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        var result = new VNetStatusResponse { Status = "Failed", Reason = "Region mismatch." };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: false, "Link", TenantId, CancellationToken.None);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task ReportAsync_WhenRunningAndNotWaiting_ReturnsSuccessAndLeavesTheHandle()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        var result = new VNetStatusResponse { Status = "Running", OperationId = OperationId };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: false, "Link", TenantId, CancellationToken.None);

        exitCode.Should().Be(0, because: "an accepted operation is not itself a failure");
        await vnet.DidNotReceive().WaitForCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportAsync_WhenRunningAndWaiting_PollsThenReportsTheSettledStatus()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.WaitForCompletionAsync(TenantId, OperationId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(new VNetStatusResponse { Status = "Linked" }));
        var result = new VNetStatusResponse { Status = "Running", OperationId = OperationId };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: true, "Link", TenantId, CancellationToken.None);

        exitCode.Should().Be(0);
        await vnet.Received(1).WaitForCompletionAsync(TenantId, OperationId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportAsync_WhenWaitSettlesAsFailed_ReturnsFailure()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.WaitForCompletionAsync(TenantId, OperationId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(
                new VNetStatusResponse { Status = "Failed", Reason = "Upstream rejected the link." }));
        var result = new VNetStatusResponse { Status = "Running", OperationId = OperationId };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: true, "Link", TenantId, CancellationToken.None);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task ReportAsync_WhenWaitCannotReadStatus_ReturnsFailure()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.WaitForCompletionAsync(TenantId, OperationId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(null));
        var result = new VNetStatusResponse { Status = "Running", OperationId = OperationId };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: true, "Link", TenantId, CancellationToken.None);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task ReportAsync_WhenRunningWithoutAHandle_DoesNotWait()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        var result = new VNetStatusResponse { Status = "Running", OperationId = null };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: true, "Link", TenantId, CancellationToken.None);

        exitCode.Should().Be(0);
        await vnet.DidNotReceive().WaitForCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportAsync_WhenUnlinkSettles_ReturnsSuccess()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        var result = new VNetStatusResponse { Status = "NotLinked" };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: true, "Unlink", TenantId, CancellationToken.None);

        exitCode.Should().Be(0);
    }

    // ─────────────────────────── GSA subcommand shape ───────────────────────────

    [Theory]
    [InlineData("enable")]
    [InlineData("disable")]
    public void GsaSetSubcommands_OfferWaitYesAndVerboseOnly(string name)
    {
        var gsa = CreateCommand().Subcommands.Single(c => c.Name == "gsa");

        var subcommand = gsa.Subcommands.Single(c => c.Name == name);

        subcommand.Options.Select(o => o.Name).Should().BeEquivalentTo("wait", "yes", "verbose");
    }

    // ───────────────────────── GSA handler invocation ───────────────────────────

    [Theory]
    [InlineData("enable", true)]
    [InlineData("disable", false)]
    public async Task GsaSetHandler_PromptsThenAppliesTheRequestedValue(string verb, bool enabled)
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.SetAsync(enabled, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(
                new GsaStatusResponse { Status = enabled ? "Enabled" : "Disabled" }));
        var confirmation = Confirming(true);
        var command = CreateCommand(confirmation: confirmation, gsa: gsa);

        var exitCode = await command.InvokeAsync($"gsa {verb}");

        exitCode.Should().Be(0);
        await confirmation.Received(1).ConfirmAsync(Arg.Any<string>());
        await gsa.Received(1).SetAsync(enabled, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("disable")]
    public async Task GsaSetHandler_WhenDeclined_DoesNotCallTheService(string verb)
    {
        var gsa = Substitute.For<IGsaService>();
        var command = CreateCommand(confirmation: Confirming(false), gsa: gsa);

        var exitCode = await command.InvokeAsync($"gsa {verb}");

        exitCode.Should().Be(1);
        await gsa.DidNotReceive().SetAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GsaSetHandler_WithYes_SkipsThePrompt()
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.SetAsync(true, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(new GsaStatusResponse { Status = "Enabled" }));
        var confirmation = Confirming(false);
        var command = CreateCommand(confirmation: confirmation, gsa: gsa);

        var exitCode = await command.InvokeAsync("gsa enable --yes");

        exitCode.Should().Be(0);
        await confirmation.DidNotReceive().ConfirmAsync(Arg.Any<string>());
        await gsa.Received(1).SetAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GsaSetHandler_WhenNoTenantCanBeResolved_FailsWithoutCallingTheService()
    {
        var gsa = Substitute.For<IGsaService>();
        var command = CreateCommand(azure: SignedInAzureCli(tenantId: null), gsa: gsa);

        var exitCode = await command.InvokeAsync("gsa enable");

        exitCode.Should().Be(1);
        await gsa.DidNotReceive().SetAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GsaSetHandler_WhenTheServiceFails_ReturnsFailure()
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.SetAsync(true, Arg.Any<CancellationToken>()).Returns(Task.FromResult<GsaStatusResponse?>(null));
        var command = CreateCommand(gsa: gsa);

        var exitCode = await command.InvokeAsync("gsa enable --yes");

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task GsaSetHandler_WithWait_PollsForTheRequestedValue()
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.SetAsync(true, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(
                new GsaStatusResponse { Status = "Disabled", Pending = true }));
        gsa.WaitForStatusAsync("Enabled", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(new GsaStatusResponse { Status = "Enabled" }));
        var command = CreateCommand(gsa: gsa);

        var exitCode = await command.InvokeAsync("gsa enable --yes --wait");

        exitCode.Should().Be(0);
        await gsa.Received(1).WaitForStatusAsync("Enabled", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GsaStatusHandler_ReadsStatusWithoutPrompting()
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(new GsaStatusResponse { Status = "Enabled" }));
        var confirmation = Confirming(false);
        var command = CreateCommand(confirmation: confirmation, gsa: gsa);

        var exitCode = await command.InvokeAsync("gsa status");

        exitCode.Should().Be(0);
        await confirmation.DidNotReceive().ConfirmAsync(Arg.Any<string>());
        await gsa.Received(1).GetStatusAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GsaStatusHandler_WhenStatusUnreadable_ReturnsFailure()
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<GsaStatusResponse?>(null));
        var command = CreateCommand(gsa: gsa);

        var exitCode = await command.InvokeAsync("gsa status");

        exitCode.Should().Be(1);
    }

    [Fact]
    public void GsaStatusSubcommand_TakesNoOperationHandle()
    {
        var gsa = CreateCommand().Subcommands.Single(c => c.Name == "gsa");

        var status = gsa.Subcommands.Single(c => c.Name == "status");

        // GSA converges on re-read rather than issuing a handle, so there is nothing to look up.
        status.Options.Select(o => o.Name).Should().BeEquivalentTo(new[] { "verbose" });
    }

    [Fact]
    public void GsaEnableSubcommand_ParsesItsOptions()
    {
        var parsed = CreateCommand().Parse("gsa enable --wait");

        parsed.Errors.Should().BeEmpty();
    }

    // ──────────────────────────────── ReportGsaAsync ────────────────────────────

    [Fact]
    public async Task ReportGsaAsync_WhenResultNull_ReturnsFailure()
    {
        var gsa = Substitute.For<IGsaService>();

        var exitCode = await NetworkCommand.ReportGsaAsync(
            NullLogger.Instance, gsa, result: null, wait: true, enabled: true, CancellationToken.None);

        exitCode.Should().Be(1);
        await gsa.DidNotReceive().WaitForStatusAsync(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportGsaAsync_WhenSettled_ReturnsSuccessWithoutWaiting()
    {
        var gsa = Substitute.For<IGsaService>();
        var result = new GsaStatusResponse { Status = "Enabled", Pending = false };

        var exitCode = await NetworkCommand.ReportGsaAsync(
            NullLogger.Instance, gsa, result, wait: true, enabled: true, CancellationToken.None);

        exitCode.Should().Be(0);
        await gsa.DidNotReceive().WaitForStatusAsync(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportGsaAsync_WhenPendingAndNotWaiting_ReturnsSuccess()
    {
        var gsa = Substitute.For<IGsaService>();
        var result = new GsaStatusResponse { Status = "Disabled", Pending = true };

        var exitCode = await NetworkCommand.ReportGsaAsync(
            NullLogger.Instance, gsa, result, wait: false, enabled: true, CancellationToken.None);

        exitCode.Should().Be(0, because: "an accepted change that has not surfaced yet is not a failure");
        await gsa.DidNotReceive().WaitForStatusAsync(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, "Enabled")]
    [InlineData(false, "Disabled")]
    public async Task ReportGsaAsync_WhenPendingAndWaiting_PollsForTheRequestedStatus(
        bool enabled, string expectedStatus)
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.WaitForStatusAsync(expectedStatus, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(
                new GsaStatusResponse { Status = expectedStatus, Pending = false }));
        var result = new GsaStatusResponse { Status = "NotConfigured", Pending = true };

        var exitCode = await NetworkCommand.ReportGsaAsync(
            NullLogger.Instance, gsa, result, wait: true, enabled, CancellationToken.None);

        exitCode.Should().Be(0);
        await gsa.Received(1).WaitForStatusAsync(
            expectedStatus, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportGsaAsync_WhenWaitCannotReadStatus_ReturnsFailure()
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.WaitForStatusAsync("Enabled", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(null));
        var result = new GsaStatusResponse { Status = "Disabled", Pending = true };

        var exitCode = await NetworkCommand.ReportGsaAsync(
            NullLogger.Instance, gsa, result, wait: true, enabled: true, CancellationToken.None);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task ReportGsaAsync_WhenStillPendingAfterWaiting_ReturnsSuccess()
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.WaitForStatusAsync("Enabled", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(
                new GsaStatusResponse { Status = "Disabled", Pending = true }));
        var result = new GsaStatusResponse { Status = "Disabled", Pending = true };

        var exitCode = await NetworkCommand.ReportGsaAsync(
            NullLogger.Instance, gsa, result, wait: true, enabled: true, CancellationToken.None);

        exitCode.Should().Be(0, because: "the platform accepted the change; the environment is catching up");
    }

    [Fact]
    public async Task ReportGsaAsync_WithAReasonOnASettledResult_StillSucceeds()
    {
        var gsa = Substitute.For<IGsaService>();
        var result = new GsaStatusResponse
        {
            Status = "NotConfigured",
            Pending = false,
            Reason = "This tenant has no Agent 365 environment yet.",
        };

        var exitCode = await NetworkCommand.ReportGsaAsync(
            NullLogger.Instance, gsa, result, wait: false, enabled: false, CancellationToken.None);

        exitCode.Should().Be(0);
    }
}
