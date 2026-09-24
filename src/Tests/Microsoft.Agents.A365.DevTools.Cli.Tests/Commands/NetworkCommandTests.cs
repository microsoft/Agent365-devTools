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
    private const string TenantId = "tid";

    private static Command CreateCommand(
        IAzureCliService? azure = null,
        IConfirmationProvider? confirmation = null,
        IGsaService? gsa = null) =>
        NetworkCommand.CreateCommand(
            NullLogger.Instance,
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

    private static AzureAccountInfo Account(string tenantId = TenantId) =>
        new() { TenantId = tenantId };

    private static IConfirmationProvider Confirming(bool answer)
    {
        var confirmation = Substitute.For<IConfirmationProvider>();
        confirmation.ConfirmAsync(Arg.Any<string>()).Returns(Task.FromResult(answer));
        return confirmation;
    }

    // ──────────────────────────── Command tree shape ────────────────────────────

    [Fact]
    public void CreateCommand_ExposesTheGsaSubcommandTree()
    {
        var command = CreateCommand();

        command.Name.Should().Be("network");
        command.Subcommands.Select(c => c.Name).Should().BeEquivalentTo("gsa");

        var gsa = command.Subcommands.Single(c => c.Name == "gsa");
        gsa.Subcommands.Select(c => c.Name).Should().BeEquivalentTo("enable", "disable", "status");
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
        gsa.SetAsync(Arg.Any<AzureAccountInfo>(), enabled, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(
                new GsaStatusResponse { Status = enabled ? "Enabled" : "Disabled" }));
        var confirmation = Confirming(true);
        var command = CreateCommand(confirmation: confirmation, gsa: gsa);

        var exitCode = await command.InvokeAsync($"gsa {verb}");

        exitCode.Should().Be(0);
        await confirmation.Received(1).ConfirmAsync(Arg.Any<string>());
        await gsa.Received(1).SetAsync(Arg.Any<AzureAccountInfo>(), enabled, Arg.Any<CancellationToken>());
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
        await gsa.DidNotReceive().SetAsync(Arg.Any<AzureAccountInfo>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GsaSetHandler_WithYes_SkipsThePrompt()
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.SetAsync(Arg.Any<AzureAccountInfo>(), true, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(new GsaStatusResponse { Status = "Enabled" }));
        var confirmation = Confirming(false);
        var command = CreateCommand(confirmation: confirmation, gsa: gsa);

        var exitCode = await command.InvokeAsync("gsa enable --yes");

        exitCode.Should().Be(0);
        await confirmation.DidNotReceive().ConfirmAsync(Arg.Any<string>());
        await gsa.Received(1).SetAsync(Arg.Any<AzureAccountInfo>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GsaSetHandler_WhenNoTenantCanBeResolved_FailsWithoutCallingTheService()
    {
        var gsa = Substitute.For<IGsaService>();
        var command = CreateCommand(azure: SignedInAzureCli(tenantId: null), gsa: gsa);

        var exitCode = await command.InvokeAsync("gsa enable");

        exitCode.Should().Be(1);
        await gsa.DidNotReceive().SetAsync(Arg.Any<AzureAccountInfo>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GsaSetHandler_ReadsTheAzAccountOnceAndActsOnTheTenantItConfirmed()
    {
        // az account show reads mutable local state, so a second read for authentication could
        // confirm one tenant and change another. The account is resolved once and passed down.
        const string tenantId = "44444444-4444-4444-4444-444444444444";
        var azure = SignedInAzureCli(tenantId);
        var gsa = Substitute.For<IGsaService>();
        gsa.SetAsync(Arg.Any<AzureAccountInfo>(), true, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(new GsaStatusResponse { Status = "Enabled" }));
        var confirmation = Confirming(true);
        var command = CreateCommand(azure: azure, confirmation: confirmation, gsa: gsa);

        var exitCode = await command.InvokeAsync("gsa enable");

        exitCode.Should().Be(0);
        await azure.Received(1).GetCurrentAccountAsync();
        await confirmation.Received(1).ConfirmAsync(Arg.Is<string>(m => m.Contains(tenantId)));
        await gsa.Received(1).SetAsync(
            Arg.Is<AzureAccountInfo>(a => a.TenantId == tenantId), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GsaStatusHandler_WhenNoTenantCanBeResolved_FailsWithoutCallingTheService()
    {
        var gsa = Substitute.For<IGsaService>();
        var command = CreateCommand(azure: SignedInAzureCli(tenantId: null), gsa: gsa);

        var exitCode = await command.InvokeAsync("gsa status");

        exitCode.Should().Be(1);
        await gsa.DidNotReceive().GetStatusAsync(
            Arg.Any<AzureAccountInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GsaSetHandler_WhenTheServiceFails_ReturnsFailure()
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.SetAsync(Arg.Any<AzureAccountInfo>(), true, Arg.Any<CancellationToken>()).Returns(Task.FromResult<GsaStatusResponse?>(null));
        var command = CreateCommand(gsa: gsa);

        var exitCode = await command.InvokeAsync("gsa enable --yes");

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task GsaSetHandler_WithWait_PollsForTheRequestedValue()
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.SetAsync(Arg.Any<AzureAccountInfo>(), true, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(
                new GsaStatusResponse { Status = "Disabled", Pending = true }));
        gsa.WaitForStatusAsync(Arg.Any<AzureAccountInfo>(), "Enabled", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(new GsaStatusResponse { Status = "Enabled" }));
        var command = CreateCommand(gsa: gsa);

        var exitCode = await command.InvokeAsync("gsa enable --yes --wait");

        exitCode.Should().Be(0);
        await gsa.Received(1).WaitForStatusAsync(Arg.Any<AzureAccountInfo>(), "Enabled", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GsaStatusHandler_ReadsStatusWithoutPrompting()
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.GetStatusAsync(Arg.Any<AzureAccountInfo>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(new GsaStatusResponse { Status = "Enabled" }));
        var confirmation = Confirming(false);
        var command = CreateCommand(confirmation: confirmation, gsa: gsa);

        var exitCode = await command.InvokeAsync("gsa status");

        exitCode.Should().Be(0);
        await confirmation.DidNotReceive().ConfirmAsync(Arg.Any<string>());
        await gsa.Received(1).GetStatusAsync(Arg.Any<AzureAccountInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GsaStatusHandler_WhenStatusUnreadable_ReturnsFailure()
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.GetStatusAsync(Arg.Any<AzureAccountInfo>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<GsaStatusResponse?>(null));
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
            NullLogger.Instance, gsa, Account(), result: null, wait: true, enabled: true, CancellationToken.None);

        exitCode.Should().Be(1);
        await gsa.DidNotReceive().WaitForStatusAsync(
            Arg.Any<AzureAccountInfo>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportGsaAsync_WhenSettled_ReturnsSuccessWithoutWaiting()
    {
        var gsa = Substitute.For<IGsaService>();
        var result = new GsaStatusResponse { Status = "Enabled", Pending = false };

        var exitCode = await NetworkCommand.ReportGsaAsync(
            NullLogger.Instance, gsa, Account(), result, wait: true, enabled: true, CancellationToken.None);

        exitCode.Should().Be(0);
        await gsa.DidNotReceive().WaitForStatusAsync(
            Arg.Any<AzureAccountInfo>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportGsaAsync_WhenPendingAndNotWaiting_ReturnsSuccess()
    {
        var gsa = Substitute.For<IGsaService>();
        var result = new GsaStatusResponse { Status = "Disabled", Pending = true };

        var exitCode = await NetworkCommand.ReportGsaAsync(
            NullLogger.Instance, gsa, Account(), result, wait: false, enabled: true, CancellationToken.None);

        exitCode.Should().Be(0, because: "an accepted change that has not surfaced yet is not a failure");
        await gsa.DidNotReceive().WaitForStatusAsync(
            Arg.Any<AzureAccountInfo>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, "Enabled")]
    [InlineData(false, "Disabled")]
    public async Task ReportGsaAsync_WhenPendingAndWaiting_PollsForTheRequestedStatus(
        bool enabled, string expectedStatus)
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.WaitForStatusAsync(Arg.Any<AzureAccountInfo>(), expectedStatus, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(
                new GsaStatusResponse { Status = expectedStatus, Pending = false }));
        var result = new GsaStatusResponse { Status = "NotConfigured", Pending = true };

        var exitCode = await NetworkCommand.ReportGsaAsync(
            NullLogger.Instance, gsa, Account(), result, wait: true, enabled, CancellationToken.None);

        exitCode.Should().Be(0);
        await gsa.Received(1).WaitForStatusAsync(
            Arg.Any<AzureAccountInfo>(), expectedStatus, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportGsaAsync_WhenWaitCannotReadStatus_ReturnsFailure()
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.WaitForStatusAsync(Arg.Any<AzureAccountInfo>(), "Enabled", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(null));
        var result = new GsaStatusResponse { Status = "Disabled", Pending = true };

        var exitCode = await NetworkCommand.ReportGsaAsync(
            NullLogger.Instance, gsa, Account(), result, wait: true, enabled: true, CancellationToken.None);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task ReportGsaAsync_WhenStillPendingAfterWaiting_ReturnsSuccess()
    {
        var gsa = Substitute.For<IGsaService>();
        gsa.WaitForStatusAsync(Arg.Any<AzureAccountInfo>(), "Enabled", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GsaStatusResponse?>(
                new GsaStatusResponse { Status = "Disabled", Pending = true }));
        var result = new GsaStatusResponse { Status = "Disabled", Pending = true };

        var exitCode = await NetworkCommand.ReportGsaAsync(
            NullLogger.Instance, gsa, Account(), result, wait: true, enabled: true, CancellationToken.None);

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
            NullLogger.Instance, gsa, Account(), result, wait: false, enabled: false, CancellationToken.None);

        exitCode.Should().Be(0);
    }
}
