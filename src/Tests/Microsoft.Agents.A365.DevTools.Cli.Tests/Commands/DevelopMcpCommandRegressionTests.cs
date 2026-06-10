// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using NSubstitute;
using FluentAssertions;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Core regression tests for the MCP commands focusing on critical scenarios
/// These tests ensure key functionality works and prevent regressions from architectural changes
/// </summary>
public class DevelopMcpCommandRegressionTests
{
    private readonly ILogger _mockLogger;
    private readonly IAgent365ToolingService _mockToolingService;
    private readonly Command _command;

    public DevelopMcpCommandRegressionTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockToolingService = Substitute.For<IAgent365ToolingService>();
        _command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);
    }

    [Fact]
    public async Task DryRunMode_NeverCallsActualServices()
    {
        // This test ensures dry-run mode is properly implemented across all commands
        // and prevents accidental service calls during dry runs

        // Arrange & Act - Test all dry run scenarios  
        var dryRunCommands = new[]
        {
            new[] { "list-environments", "--dry-run" },
            new[] { "list-servers", "-e", "test-env", "--dry-run" },
            new[] { "publish", "-e", "test-env", "-s", "test-server", "--dry-run" },
            new[] { "unpublish", "-e", "test-env", "-s", "test-server", "--dry-run" }
        };

        foreach (var commandArgs in dryRunCommands)
        {
            var result = await _command.InvokeAsync(commandArgs);
            result.Should().Be(0, $"Command {string.Join(" ", commandArgs)} should succeed");
        }

        // Verify no service methods were called
        await _mockToolingService.DidNotReceive().ListEnvironmentsAsync();
        await _mockToolingService.DidNotReceive().ListServersAsync(Arg.Any<string>());
        await _mockToolingService.DidNotReceive().PublishServerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishMcpServerRequest>());
        await _mockToolingService.DidNotReceive().UnpublishServerAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData("list-servers", "-e", "test-env")]
    [InlineData("list-servers", "--environment-id", "test-env")]
    [InlineData("publish", "-e", "test-env", "-s", "test-server")]
    [InlineData("publish", "--environment-id", "test-env", "--server-name", "test-server")]
    [InlineData("unpublish", "-e", "test-env", "-s", "test-server")]
    public async Task AzureCliStyleParameters_AreAcceptedCorrectly(string command, params string[] args)
    {
        // This test ensures we maintain Azure CLI compatibility with named options
        // Regression test: Prevents reverting back to positional arguments

        // Arrange  
        _mockToolingService.ListServersAsync(Arg.Any<string>()).Returns(new DataverseMcpServersResponse());
        _mockToolingService.PublishServerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishMcpServerRequest>())
            .Returns(new PublishMcpServerResponse { Status = "Success" });
        _mockToolingService.UnpublishServerAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var fullCommand = new List<string> { command };
        fullCommand.AddRange(args);
        fullCommand.Add("--dry-run"); // Use dry run to avoid actual service calls

        // Act
        var result = await _command.InvokeAsync(fullCommand.ToArray());

        // Assert
        result.Should().Be(0, $"Azure CLI style command should be accepted: {string.Join(" ", fullCommand)}");
    }

    [Fact]
    public async Task PublishCommand_AcceptsAllNamedParameters_InDryRun()
    {
        // Verifies the publish CLI parses every documented flag without error. Dry-run
        // short-circuits before any platform call, so this is a pure CLI parsing test —
        // it does NOT verify that the parsed values flow into PublishServerAsync.
        // That contract is covered by PublishCommand_ForwardsParsedParametersToToolingService
        // (which mocks Graph + tenant detection so the non-dry-run path can be exercised).

        // Arrange
        var testEnvId = "test-environment-123";
        var testServerName = "msdyn_TestServer";
        var testAlias = "test-alias";
        var testDisplayName = "Test Server Display Name";

        // Act — dry-run short-circuits the Graph + platform calls so this stays a pure CLI parsing test.
        var result = await _command.InvokeAsync(new[]
        {
            "publish",
            "--environment-id", testEnvId,
            "--server-name", testServerName,
            "--alias", testAlias,
            "--display-name", testDisplayName,
            "--dry-run",
        });

        // Assert — successful parse + dispatch, no service calls.
        result.Should().Be(
            0,
            because: "dry-run should never trigger a non-zero exit code when all flags parse cleanly.");
        await _mockToolingService.DidNotReceive().PublishServerAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishMcpServerRequest>());
    }

    /// <summary>
    /// Strengthened contract test: verifies that every parsed publish flag flows into the actual
    /// <see cref="IAgent365ToolingService.PublishServerAsync"/> call. Exercises the non-dry-run
    /// path by (a) using <c>--yes</c> to bypass the interactive confirmation prompt, (b) mocking
    /// <see cref="GraphApiService"/> so Entra app creation succeeds without a real tenant, and
    /// (c) subclassing <see cref="PublishCommandExecutor"/> to stub out tenant auto-detection so
    /// the executor doesn't shell out to Azure CLI in CI. Catches breakage of the CLI-to-service
    /// contract (alias/display-name/publisher-name mapping, request shaping, and selecting the
    /// correct service method) that the dry-run-only test above can't catch.
    /// </summary>
    [Fact]
    public async Task PublishCommand_ForwardsParsedParametersToToolingService()
    {
        // Arrange
        const string TestTenantId = "test-tenant-99999";
        const string TestEnvironmentId = "test-env-forward";
        const string TestServerName = "msdyn_TestServer";
        const string TestAlias = "test-alias-forward";
        const string TestDisplayName = "Test Display Forward";
        const string TestPublisherName = "Contoso Forward";
        const string TestPublicClientsObjectId = "public-clients-object-id";
        const string TestPublicClientsClientId = "public-clients-client-id";

        var logger = Substitute.For<ILogger>();
        var toolingService = Substitute.For<IAgent365ToolingService>();
        var graphApiService = Substitute.For<GraphApiService>();

        // Mock Graph so CreateEntraAppsAsync → factory.CreatePublicClientsAppAsync succeeds.
        graphApiService.CreateEntraAppAsync(
                TestTenantId, Arg.Any<string>(), serviceTreeId: Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string ObjectId, string ClientId)?>(
                (TestPublicClientsObjectId, TestPublicClientsClientId)));
        graphApiService.UpdateAppPublicClientRedirectUrisAsync(
                TestTenantId, TestPublicClientsObjectId, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Mock Graph for ConfigureEntraAppsAsync → required-resource-access grant on Public Clients.
        graphApiService.GetOAuth2PermissionScopeIdAsync(
                TestTenantId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(Guid.NewGuid()));
        graphApiService.AddRequiredResourceAccessAsync(
                TestTenantId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Capture what gets forwarded to PublishServerAsync.
        string? capturedEnvId = null;
        string? capturedServerName = null;
        PublishMcpServerRequest? capturedRequest = null;
        toolingService.PublishServerAsync(
                Arg.Do<string>(e => capturedEnvId = e),
                Arg.Do<string>(s => capturedServerName = s),
                Arg.Do<PublishMcpServerRequest>(r => capturedRequest = r),
                Arg.Any<CancellationToken>())
            .Returns(new PublishMcpServerResponse
            {
                Status = "Success",
                McpServerAppId = Guid.NewGuid().ToString(),
                McpServerScope = "Tools.ListInvoke.All",
            });

        var executor = new TestablePublishCommandExecutor(
            logger, toolingService, graphApiService, TestTenantId);

        var args = new RawPublishArgs(
            EnvironmentId: TestEnvironmentId,
            ServerName: TestServerName,
            Alias: TestAlias,
            DisplayName: TestDisplayName,
            PublisherName: TestPublisherName,
            Yes: true,
            DryRun: false);

        // Act
        var result = await executor.ExecuteAsync(args);

        // Assert
        result.Should().BeTrue(
            because: "the happy path with all dependencies mocked must return true so the publish " +
                     "handler exits 0.");
        capturedRequest.Should().NotBeNull(
            because: "PublishServerAsync must be invoked once the prompt is skipped, the tenant " +
                     "is detected, and the Public Clients app is created.");
        capturedEnvId.Should().Be(
            TestEnvironmentId,
            because: "--environment-id must flow unchanged into PublishServerAsync's first positional arg.");
        capturedServerName.Should().Be(
            TestServerName,
            because: "--server-name must flow unchanged into PublishServerAsync's second positional arg.");
        capturedRequest!.Alias.Should().Be(
            TestAlias,
            because: "--alias must populate request.Alias; this is the platform's `name` for the published row.");
        capturedRequest.DisplayName.Should().Be(
            TestDisplayName,
            because: "--display-name must populate request.DisplayName; the platform's v2 validator " +
                     "requires it and surfaces it in MOS.");
        capturedRequest.PublisherName.Should().Be(
            TestPublisherName,
            because: "--publisher-name must populate request.PublisherName so the MOS manifest's " +
                     "developer field is set for custom servers (the platform rejects empty values " +
                     "for non-1p servers).");
        capturedRequest.PublicClientsAppId.Should().Be(
            TestPublicClientsClientId,
            because: "the just-created Public Clients Entra app's clientId must be carried to the " +
                     "platform so it can be echoed back and the CLI can grant the PPMI scope on it " +
                     "post-publish.");
    }

    /// <summary>
    /// Regression test for the empty-publisher case: an explicit <c>--publisher-name ""</c> (empty /
    /// whitespace) must be treated as "no publisher" without triggering the interactive prompt, so
    /// non-interactive automation can't hang. Exercises the non-dry-run path with <c>--yes</c>; the
    /// proof the prompt was skipped is that the executor reaches PublishServerAsync at all (a real
    /// prompt would block on Console.ReadLine), and the forwarded request carries a null publisher.
    /// </summary>
    [Fact]
    public async Task PublishCommand_ExplicitEmptyPublisherName_SkipsPromptAndForwardsNull()
    {
        // Arrange
        const string TestTenantId = "test-tenant-99999";
        const string TestEnvironmentId = "test-env-empty-pub";
        const string TestServerName = "msdyn_TestServer";
        const string TestAlias = "test-alias-empty-pub";
        const string TestDisplayName = "Test Display Empty Pub";
        const string TestPublicClientsObjectId = "public-clients-object-id";
        const string TestPublicClientsClientId = "public-clients-client-id";

        var logger = Substitute.For<ILogger>();
        var toolingService = Substitute.For<IAgent365ToolingService>();
        var graphApiService = Substitute.For<GraphApiService>();

        graphApiService.CreateEntraAppAsync(
                TestTenantId, Arg.Any<string>(), serviceTreeId: Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string ObjectId, string ClientId)?>(
                (TestPublicClientsObjectId, TestPublicClientsClientId)));
        graphApiService.UpdateAppPublicClientRedirectUrisAsync(
                TestTenantId, TestPublicClientsObjectId, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        graphApiService.GetOAuth2PermissionScopeIdAsync(
                TestTenantId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(Guid.NewGuid()));
        graphApiService.AddRequiredResourceAccessAsync(
                TestTenantId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        PublishMcpServerRequest? capturedRequest = null;
        toolingService.PublishServerAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<PublishMcpServerRequest>(r => capturedRequest = r),
                Arg.Any<CancellationToken>())
            .Returns(new PublishMcpServerResponse
            {
                Status = "Success",
                McpServerAppId = Guid.NewGuid().ToString(),
                McpServerScope = "Tools.ListInvoke.All",
            });

        var executor = new TestablePublishCommandExecutor(
            logger, toolingService, graphApiService, TestTenantId);

        var args = new RawPublishArgs(
            EnvironmentId: TestEnvironmentId,
            ServerName: TestServerName,
            Alias: TestAlias,
            DisplayName: TestDisplayName,
            PublisherName: string.Empty, // explicit empty, e.g. --publisher-name "" from a script
            Yes: true,
            DryRun: false);

        // Act
        var result = await executor.ExecuteAsync(args);

        // Assert
        result.Should().BeTrue(
            because: "an explicit empty publisher is valid (treated as no publisher), so the happy " +
                     "path with all dependencies mocked must return true.");
        capturedRequest.Should().NotBeNull(
            because: "the executor must proceed to PublishServerAsync without prompting; an explicit " +
                     "empty --publisher-name must not be treated as 'missing' and block on the prompt.");
        capturedRequest!.PublisherName.Should().BeNull(
            because: "an explicit empty/whitespace --publisher-name is normalized to null ('no " +
                     "publisher'); the platform decides whether that's acceptable for the server type.");
    }

    /// <summary>
    /// Test-only subclass of <see cref="PublishCommandExecutor"/> that stubs out
    /// <see cref="PublishCommandExecutor.DetectTenantIdAsync"/> with a known value, so the
    /// strengthened contract test doesn't need to shell out to <c>az account show</c> in CI.
    /// </summary>
    private sealed class TestablePublishCommandExecutor : PublishCommandExecutor
    {
        private readonly string? _tenantId;

        public TestablePublishCommandExecutor(
            ILogger logger,
            IAgent365ToolingService toolingService,
            GraphApiService? graphApiService,
            string? tenantId)
            // Zero-delay retry so the non-dry-run path doesn't spend ~45s in exponential backoff when a
            // best-effort Graph step is left unmocked (mirrors GraphApiService/ArmApiService test injection).
            : base(logger, toolingService, graphApiService, new RetryHelper(logger, maxRetries: 1, baseDelaySeconds: 0))
        {
            _tenantId = tenantId;
        }

        protected override Task<string?> DetectTenantIdAsync() => Task.FromResult(_tenantId);
    }

    [Fact]
    public async Task ServiceIntegration_UnpublishCommand_PassesCorrectParameters()
    {
        // Core functionality test: Ensures unpublish command integration works correctly
        
        // Arrange
        var testEnvId = "test-environment-456";
        var testServerName = "msdyn_TestServer";

        _mockToolingService.UnpublishServerAsync(testEnvId, testServerName).Returns(true);

        // Act
        var result = await _command.InvokeAsync(new[] 
        { 
            "unpublish", 
            "-e", testEnvId,
            "-s", testServerName
        });

        // Assert
        result.Should().Be(0);
        await _mockToolingService.Received(1).UnpublishServerAsync(testEnvId, testServerName);
    }

    [Fact]
    public void CommandStructure_HasNoPositionalArguments()
    {
        // Critical regression test: Ensures we don't accidentally revert to positional arguments
        // This was a key architectural decision to follow Azure CLI patterns
        
        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);

        // Assert
        foreach (var subcommand in command.Subcommands)
        {
            subcommand.Arguments.Should().BeEmpty(
                $"Subcommand '{subcommand.Name}' must not have positional arguments - Azure CLI compliance requires named options only");
        }
    }

    [Fact]
    public void CommandStructure_AllSubcommandsHaveConsistentOptions()
    {
        // Regression test: Ensures consistent option patterns across all commands

        // Act
        var command = DevelopMcpCommand.CreateCommand(_mockLogger, _mockToolingService);

        // Assert
        foreach (var subcommand in command.Subcommands)
        {
            var options = subcommand.Options.ToList();

            options.Should().Contain(o => o.Name == "dry-run",
                $"Subcommand '{subcommand.Name}' should have --dry-run option");
        }
    }
}