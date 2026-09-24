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
        IConfirmationProvider? confirmation = null) =>
        NetworkCommand.CreateCommand(
            NullLogger.Instance,
            vnet ?? Substitute.For<IVNetLinkService>(),
            azure ?? SignedInAzureCli(),
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

        var vnet = command.Subcommands.Should().ContainSingle().Subject;
        vnet.Name.Should().Be("vnet");
        vnet.Subcommands.Select(c => c.Name).Should().BeEquivalentTo("link", "unlink", "status");
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
}
