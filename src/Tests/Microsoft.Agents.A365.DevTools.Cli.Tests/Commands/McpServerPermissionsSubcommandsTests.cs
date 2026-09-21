// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Invocation tests for the develop-mcp subcommands that report and grant MCP server permissions.
/// These pin the CLI contract: which inputs are rejected, which exit codes are produced, and which
/// grants are issued.
/// </summary>
public class McpServerPermissionsSubcommandsTests
{
    private const string TenantId = "00000000-0000-0000-0000-0000000000aa";
    private const string BlueprintId = "33333333-3333-3333-3333-333333333333";
    private const string AgentSpId = "44444444-4444-4444-4444-444444444444";
    private const string ByoAppId = "11111111-1111-1111-1111-111111111111";
    private const string ByoSpObjectId = "22222222-2222-2222-2222-222222222222";
    private const string ServerName = "Foo";

    private readonly ILogger _logger = NullLogger.Instance;
    private readonly McpServerPermissionService _permissionService;

    public McpServerPermissionsSubcommandsTests()
    {
        var graph = Substitute.For<GraphApiService>();
        var blueprintService = Substitute.For<AgentBlueprintService>(
            Substitute.For<ILogger<AgentBlueprintService>>(), graph);
        _permissionService = Substitute.For<McpServerPermissionService>(
            graph, blueprintService, Substitute.For<ILogger<McpServerPermissionService>>());
    }

    private static McpServerResource Resource() =>
        new(ServerName, McpConstants.BuildByoAppDisplayName(ServerName), ByoAppId, ByoSpObjectId);

    private void SetupResolvedResource() =>
        _permissionService.ResolveServerResourceAsync(TenantId, ServerName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<McpServerResource?>(Resource()));

    private void SetupInstances(params AgentInstancePermissionStatus[] statuses) =>
        _permissionService.GetAgentInstanceStatusesAsync(TenantId, BlueprintId, ByoSpObjectId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentInstancePermissionStatus>>(statuses));

    private Command ListCommand() =>
        McpServerPermissionsSubcommands.CreateListAgentInstancesSubcommand(_logger, _permissionService);

    private Command GrantCommand() =>
        McpServerPermissionsSubcommands.CreateGrantPermissionsSubcommand(_logger, _permissionService);

    [Fact]
    public void ListAgentInstances_HasExpectedName()
    {
        ListCommand().Name.Should().Be("list-agent-instances");
    }

    [Fact]
    public void GrantPermissions_HasExpectedName()
    {
        GrantCommand().Name.Should().Be("grant-mcpserver-permissions");
    }

    [Fact]
    public async Task ListAgentInstances_NonGuidBlueprintId_ExitsWithOne()
    {
        var exitCode = await ListCommand().InvokeAsync(
            ["--agent-blueprint-id", "not-a-guid", "--mcp-server-name", ServerName, "--tenant-id", TenantId]);

        exitCode.Should().Be(1,
            because: "the blueprint ID is interpolated into a Graph OData filter, so a non-GUID must be rejected before any request is made");
        await _permissionService.DidNotReceive().ResolveServerResourceAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAgentInstances_NonGuidTenantId_ExitsWithOne()
    {
        var exitCode = await ListCommand().InvokeAsync(
            ["--agent-blueprint-id", BlueprintId, "--mcp-server-name", ServerName, "--tenant-id", "nope"]);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task ListAgentInstances_WhitespaceServerName_ExitsWithOne()
    {
        var exitCode = await ListCommand().InvokeAsync(
            ["--agent-blueprint-id", BlueprintId, "--mcp-server-name", "  ", "--tenant-id", TenantId]);

        exitCode.Should().Be(1,
            because: "an explicitly empty server name would resolve the application ' - BYO' rather than failing clearly");
    }

    [Fact]
    public async Task ListAgentInstances_UnresolvableServer_ExitsWithOne()
    {
        _permissionService.ResolveServerResourceAsync(TenantId, ServerName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<McpServerResource?>(null));

        var exitCode = await ListCommand().InvokeAsync(
            ["--agent-blueprint-id", BlueprintId, "--mcp-server-name", ServerName, "--tenant-id", TenantId]);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task ListAgentInstances_AllInstancesHaveScope_ExitsWithZeroAndGrantsNothing()
    {
        SetupResolvedResource();
        SetupInstances(new AgentInstancePermissionStatus(AgentSpId, "Agent", HasScope: true));

        var exitCode = await ListCommand().InvokeAsync(
            ["--agent-blueprint-id", BlueprintId, "--mcp-server-name", ServerName, "--tenant-id", TenantId]);

        exitCode.Should().Be(0);
        await _permissionService.DidNotReceive().GrantServerScopeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAgentInstances_WithoutYes_DoesNotGrantWhenSelectionIsSkipped()
    {
        SetupResolvedResource();
        SetupInstances(new AgentInstancePermissionStatus(AgentSpId, "Agent", HasScope: false));
        // Covers both paths: redirected input prints the equivalent commands, a terminal prompts and gets an empty answer.
        ConsoleHelper.ReadLineOverrideForTests.Value = () => string.Empty;
        try
        {
            var exitCode = await ListCommand().InvokeAsync(
                ["--agent-blueprint-id", BlueprintId, "--mcp-server-name", ServerName, "--tenant-id", TenantId]);

            exitCode.Should().Be(0,
                because: "listing is a read-only report; declining the prompt is not a failure");
            await _permissionService.DidNotReceive().GrantServerScopeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            ConsoleHelper.ReadLineOverrideForTests.Value = null;
        }
    }

    [Fact]
    public async Task ListAgentInstances_WithYes_GrantsOnlyInstancesMissingTheScope()
    {
        SetupResolvedResource();
        SetupInstances(
            new AgentInstancePermissionStatus(AgentSpId, "Missing", HasScope: false),
            new AgentInstancePermissionStatus("sp-has-scope", "Granted", HasScope: true));
        _permissionService.GrantServerScopeAsync(TenantId, AgentSpId, ByoSpObjectId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var exitCode = await ListCommand().InvokeAsync(
            ["--agent-blueprint-id", BlueprintId, "--mcp-server-name", ServerName, "--tenant-id", TenantId, "--yes"]);

        exitCode.Should().Be(0);
        await _permissionService.Received(1).GrantServerScopeAsync(
            TenantId, AgentSpId, ByoSpObjectId, Arg.Any<CancellationToken>());
        await _permissionService.DidNotReceive().GrantServerScopeAsync(
            TenantId, "sp-has-scope", ByoSpObjectId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAgentInstances_WithYes_FailedGrantExitsWithOne()
    {
        SetupResolvedResource();
        SetupInstances(new AgentInstancePermissionStatus(AgentSpId, "Missing", HasScope: false));
        _permissionService.GrantServerScopeAsync(TenantId, AgentSpId, ByoSpObjectId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var exitCode = await ListCommand().InvokeAsync(
            ["--agent-blueprint-id", BlueprintId, "--mcp-server-name", ServerName, "--tenant-id", TenantId, "--yes"]);

        exitCode.Should().Be(1,
            because: "a failed grant must surface as a non-zero exit code so automation does not treat the agent as provisioned");
    }

    [Fact]
    public async Task ListAgentInstances_DeviceCodeFlag_RoutesSignInThroughDeviceCode()
    {
        SetupResolvedResource();
        SetupInstances(new AgentInstancePermissionStatus(AgentSpId, "Agent", HasScope: true));

        await ListCommand().InvokeAsync(
            ["--agent-blueprint-id", BlueprintId, "--mcp-server-name", ServerName, "--tenant-id", TenantId, "--device-code"]);

        _permissionService.Received().UseDeviceCodeAuthentication = true;
    }

    [Fact]
    public async Task GrantPermissions_WithoutDeviceCodeFlag_UsesDefaultInteractiveSignIn()
    {
        SetupResolvedResource();
        _permissionService.GrantServerScopeAsync(TenantId, AgentSpId, ByoSpObjectId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        await GrantCommand().InvokeAsync(
            ["--agent-serviceprincipal-id", AgentSpId, "--mcp-server-name", ServerName, "--tenant-id", TenantId]);

        _permissionService.Received().UseDeviceCodeAuthentication = false;
    }

    [Fact]
    public async Task GrantPermissions_NonGuidAgentServicePrincipalId_ExitsWithOne()
    {
        var exitCode = await GrantCommand().InvokeAsync(
            ["--agent-serviceprincipal-id", "not-a-guid", "--mcp-server-name", ServerName, "--tenant-id", TenantId]);

        exitCode.Should().Be(1);
        await _permissionService.DidNotReceive().GrantServerScopeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GrantPermissions_UnresolvableServer_ExitsWithOne()
    {
        _permissionService.ResolveServerResourceAsync(TenantId, ServerName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<McpServerResource?>(null));

        var exitCode = await GrantCommand().InvokeAsync(
            ["--agent-serviceprincipal-id", AgentSpId, "--mcp-server-name", ServerName, "--tenant-id", TenantId]);

        exitCode.Should().Be(1);
        await _permissionService.DidNotReceive().GrantServerScopeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GrantPermissions_Success_ExitsWithZeroAndGrantsAgainstResolvedResource()
    {
        SetupResolvedResource();
        _permissionService.GrantServerScopeAsync(TenantId, AgentSpId, ByoSpObjectId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var exitCode = await GrantCommand().InvokeAsync(
            ["--agent-serviceprincipal-id", AgentSpId, "--mcp-server-name", ServerName, "--tenant-id", TenantId]);

        exitCode.Should().Be(0);
        await _permissionService.Received(1).GrantServerScopeAsync(
            TenantId, AgentSpId, ByoSpObjectId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GrantPermissions_GraphRejectsGrant_ExitsWithOne()
    {
        SetupResolvedResource();
        _permissionService.GrantServerScopeAsync(TenantId, AgentSpId, ByoSpObjectId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var exitCode = await GrantCommand().InvokeAsync(
            ["--agent-serviceprincipal-id", AgentSpId, "--mcp-server-name", ServerName, "--tenant-id", TenantId]);

        exitCode.Should().Be(1);
    }
}
