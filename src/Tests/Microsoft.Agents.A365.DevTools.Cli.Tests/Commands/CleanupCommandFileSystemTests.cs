// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Tests.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.CommandLine;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Filesystem-level tests for <c>a365 cleanup</c> — verifies that <c>a365.config.json</c> is
/// deleted with a backup after a fully-successful cleanup, and is preserved when cleanup fails.
/// These tests manipulate <see cref="Environment.CurrentDirectory"/> and must run serially.
/// </summary>
[Collection("ConfigTests")]
public class CleanupCommandFileSystemTests
{
    private readonly ILogger<CleanupCommand> _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly ITeamsGraphBackendConfigurator _mockBackendConfigurator;
    private readonly CommandExecutor _mockExecutor;
    private readonly GraphApiService _graphApiService;
    private readonly FederatedCredentialService _federatedCredentialService;
    private readonly IConfirmationProvider _mockConfirmationProvider;
    private readonly AzureAuthValidator _mockAuthValidator;

    public CleanupCommandFileSystemTests()
    {
        _mockLogger = Substitute.For<ILogger<CleanupCommand>>();
        _mockConfigService = Substitute.For<IConfigService>();

        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.For<CommandExecutor>(mockExecutorLogger);
        _mockExecutor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult
            {
                ExitCode = 0,
                StandardOutput = string.Empty,
                StandardError = string.Empty
            }));

        _mockBackendConfigurator = Substitute.For<ITeamsGraphBackendConfigurator>();

        var mockTokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
        mockTokenProvider.GetMgGraphAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("test-token");

        var mockGraphLogger = Substitute.For<ILogger<GraphApiService>>();
        _graphApiService = new GraphApiService(
            mockGraphLogger, _mockExecutor, Substitute.For<IAuthenticationService>(),
            new TestHttpMessageHandler(), mockTokenProvider,
            loginHintResolver: () => Task.FromResult<string?>(null));

        var mockFicLogger = Substitute.For<ILogger<FederatedCredentialService>>();
        _federatedCredentialService = new FederatedCredentialService(mockFicLogger, _graphApiService);

        _mockConfirmationProvider = Substitute.For<IConfirmationProvider>();
        _mockConfirmationProvider.ConfirmAsync(Arg.Any<string>()).Returns(true);
        _mockConfirmationProvider.ConfirmWithTypedResponseAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        _mockAuthValidator = Substitute.For<AzureAuthValidator>(NullLogger<AzureAuthValidator>.Instance, _mockExecutor);
    }

    /// <summary>
    /// Verifies that a successful full cleanup deletes <c>a365.config.json</c> and creates a
    /// timestamped backup in the same directory.
    /// </summary>
    [Fact]
    public async Task ExecuteAllCleanup_OnSuccess_DeletesStaticConfigWithBackup()
    {
        // Arrange — minimal config with no resource IDs so no Graph/Blueprint service calls are
        // made; cleanupSucceeded = true with no failures.
        var config = new Agent365Config { TenantId = "test-tenant-id" };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);

        var command = CleanupCommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockBackendConfigurator,
            _mockExecutor, BuildAgentBlueprintService(), _mockConfirmationProvider,
            _federatedCredentialService, _mockAuthValidator);

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var configFilePath = Path.Combine(tempDir, "a365.config.json");
        await File.WriteAllTextAsync(configFilePath, "{}");

        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = tempDir;

            // Act
            var result = await command.InvokeAsync(new[] { "cleanup" });

            // Assert
            result.Should().Be(0, because: "full cleanup should exit 0");
            File.Exists(configFilePath).Should().BeFalse(
                because: "a365.config.json must be deleted after a fully-successful cleanup");
            Directory.GetFiles(tempDir, "a365.config.backup-*.json")
                .Should().HaveCountGreaterOrEqualTo(1,
                    because: "a timestamped backup must be created alongside the deletion");
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that when cleanup fails (e.g., agent registration cannot be deleted because no
    /// <c>GraphApiService</c> is available), <c>a365.config.json</c> is preserved so the user
    /// can retry after fixing the issue.
    /// </summary>
    [Fact]
    public async Task ExecuteAllCleanup_OnPartialFailure_PreservesStaticConfig()
    {
        // Arrange — config with an AgentRegistrationId but no graphApiService; the registration
        // deletion is skipped with hasFailures = true, keeping cleanupSucceeded = false.
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            AgentRegistrationId = "reg-id-that-cannot-be-deleted"
        };
        _mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(config);

        var command = CleanupCommand.CreateCommand(
            _mockLogger, _mockConfigService, _mockBackendConfigurator,
            _mockExecutor, BuildAgentBlueprintService(), _mockConfirmationProvider,
            _federatedCredentialService, _mockAuthValidator,
            graphApiService: null);  // null triggers hasFailures = true for the registration step

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var configFilePath = Path.Combine(tempDir, "a365.config.json");
        await File.WriteAllTextAsync(configFilePath, "{}");

        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = tempDir;

            // Act
            var result = await command.InvokeAsync(new[] { "cleanup" });

            // Assert
            result.Should().Be(0, because: "cleanup exits 0 even when partial failures occur");
            File.Exists(configFilePath).Should().BeTrue(
                because: "a365.config.json must be preserved when cleanup does not fully succeed");
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private AgentBlueprintService BuildAgentBlueprintService()
    {
        var logger = Substitute.For<ILogger<AgentBlueprintService>>();
        var svc = Substitute.ForPartsOf<AgentBlueprintService>(logger, _graphApiService);

        svc.GetAgentInstancesForBlueprintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AgentInstanceInfo>)Array.Empty<AgentInstanceInfo>());
        svc.DeleteAgentUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        svc.DeleteAgentIdentityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        svc.DeleteAgentBlueprintAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);

        return svc;
    }
}
