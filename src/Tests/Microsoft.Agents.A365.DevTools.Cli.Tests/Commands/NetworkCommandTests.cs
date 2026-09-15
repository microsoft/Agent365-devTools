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
/// Unit tests for the network command tree and its result reporting.
/// The subcommand handlers themselves are exercised through ReportAsync, which holds the
/// wait-and-exit-code logic; the handlers around it only parse options.
/// </summary>
public class NetworkCommandTests
{
    private const string OperationId = "op-abc";

    private static Command CreateCommand(
        IVNetLinkService? vnet = null,
        IAzureCliService? azure = null,
        IGsaService? gsa = null) =>
        NetworkCommand.CreateCommand(
            NullLogger.Instance,
            vnet ?? Substitute.For<IVNetLinkService>(),
            azure ?? Substitute.For<IAzureCliService>(),
            gsa ?? Substitute.For<IGsaService>());

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
            .BeEquivalentTo("policy-arm-id", "swap", "tenant-id", "wait", "verbose");
        link.Options.Single(o => o.Name == "policy-arm-id").IsRequired.Should().BeTrue();
        link.Options.Single(o => o.Name == "swap").IsRequired.Should().BeFalse();
    }

    [Fact]
    public void UnlinkSubcommand_TakesNoPolicyBecauseThePlatformStoredIt()
    {
        var unlink = CreateCommand().Subcommands[0].Subcommands.Single(c => c.Name == "unlink");

        unlink.Options.Select(o => o.Name).Should().BeEquivalentTo("wait", "verbose");
    }

    [Fact]
    public void StatusSubcommand_AcceptsAnOperationHandle()
    {
        var status = CreateCommand().Subcommands[0].Subcommands.Single(c => c.Name == "status");

        status.Options.Select(o => o.Name).Should().BeEquivalentTo("operation-id", "verbose");
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

    // ───────────────────────────────── ReportAsync ──────────────────────────────

    [Fact]
    public async Task ReportAsync_WhenResultNull_ReturnsFailure()
    {
        var vnet = Substitute.For<IVNetLinkService>();

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result: null, wait: true, "Link", CancellationToken.None);

        exitCode.Should().Be(1);
        await vnet.DidNotReceive().WaitForCompletionAsync(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportAsync_WhenSettled_ReturnsSuccessWithoutWaiting()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        var result = new VNetStatusResponse { Status = "Linked" };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: true, "Link", CancellationToken.None);

        exitCode.Should().Be(0);
        await vnet.DidNotReceive().WaitForCompletionAsync(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportAsync_WhenFailed_ReturnsFailure()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        var result = new VNetStatusResponse { Status = "Failed", Reason = "Region mismatch." };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: false, "Link", CancellationToken.None);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task ReportAsync_WhenRunningAndNotWaiting_ReturnsSuccessAndLeavesTheHandle()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        var result = new VNetStatusResponse { Status = "Running", OperationId = OperationId };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: false, "Link", CancellationToken.None);

        exitCode.Should().Be(0, because: "an accepted operation is not itself a failure");
        await vnet.DidNotReceive().WaitForCompletionAsync(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportAsync_WhenRunningAndWaiting_PollsThenReportsTheSettledStatus()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.WaitForCompletionAsync(OperationId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(new VNetStatusResponse { Status = "Linked" }));
        var result = new VNetStatusResponse { Status = "Running", OperationId = OperationId };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: true, "Link", CancellationToken.None);

        exitCode.Should().Be(0);
        await vnet.Received(1).WaitForCompletionAsync(
            OperationId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportAsync_WhenWaitSettlesAsFailed_ReturnsFailure()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.WaitForCompletionAsync(OperationId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(
                new VNetStatusResponse { Status = "Failed", Reason = "Upstream rejected the link." }));
        var result = new VNetStatusResponse { Status = "Running", OperationId = OperationId };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: true, "Link", CancellationToken.None);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task ReportAsync_WhenWaitCannotReadStatus_ReturnsFailure()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        vnet.WaitForCompletionAsync(OperationId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<VNetStatusResponse?>(null));
        var result = new VNetStatusResponse { Status = "Running", OperationId = OperationId };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: true, "Link", CancellationToken.None);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task ReportAsync_WhenRunningWithoutAHandle_DoesNotWait()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        var result = new VNetStatusResponse { Status = "Running", OperationId = null };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: true, "Link", CancellationToken.None);

        exitCode.Should().Be(0);
        await vnet.DidNotReceive().WaitForCompletionAsync(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportAsync_WhenUnlinkSettles_ReturnsSuccess()
    {
        var vnet = Substitute.For<IVNetLinkService>();
        var result = new VNetStatusResponse { Status = "NotLinked" };

        var exitCode = await NetworkCommand.ReportAsync(
            NullLogger.Instance, vnet, result, wait: true, "Unlink", CancellationToken.None);

        exitCode.Should().Be(0);
    }

    // ─────────────────────────── GSA subcommand shape ───────────────────────────

    [Theory]
    [InlineData("enable")]
    [InlineData("disable")]
    public void GsaSetSubcommands_OfferWaitAndVerboseOnly(string name)
    {
        var gsa = CreateCommand().Subcommands.Single(c => c.Name == "gsa");

        var subcommand = gsa.Subcommands.Single(c => c.Name == name);

        subcommand.Options.Select(o => o.Name).Should().BeEquivalentTo("wait", "verbose");
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
