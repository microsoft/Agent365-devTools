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

    private static Command CreateCommand(IVNetLinkService? vnet = null, IAzureCliService? azure = null) =>
        NetworkCommand.CreateCommand(
            NullLogger.Instance,
            vnet ?? Substitute.For<IVNetLinkService>(),
            azure ?? Substitute.For<IAzureCliService>());

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
}
