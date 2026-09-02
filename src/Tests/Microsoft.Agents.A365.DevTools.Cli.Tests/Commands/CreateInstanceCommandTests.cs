// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Tests for CreateInstanceCommand functionality
/// </summary>
[Collection("ConfigTests")]
public class CreateInstanceCommandTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string AgentBlueprintId = "22222222-2222-2222-2222-222222222222";
    private const string AgenticAppId = "33333333-3333-3333-3333-333333333333";
    private const string AgenticUserId = "44444444-4444-4444-4444-444444444444";

    private readonly ILogger<CreateInstanceCommand> _mockLogger;
    private readonly ConfigService _mockConfigService;
    private readonly CommandExecutor _mockExecutor;
    private readonly GraphApiService _mockGraphApiService;

    public CreateInstanceCommandTests()
    {
        _mockLogger = Substitute.For<ILogger<CreateInstanceCommand>>();

        // Use NullLogger instead of console logger to avoid I/O bottleneck
        _mockConfigService = Substitute.ForPartsOf<ConfigService>(NullLogger<ConfigService>.Instance);
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(NullLogger<CommandExecutor>.Instance);
        _mockGraphApiService = Substitute.ForPartsOf<GraphApiService>(NullLogger<GraphApiService>.Instance, _mockExecutor);
    }

    [Fact]
    public void CreateInstanceCommand_Should_Have_Identity_Subcommand()
    {
        // Arrange
        var command = CreateInstanceCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService);

        // Act
        var identitySubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "identity");

        // Assert - Subcommand should be registered
        Assert.NotNull(identitySubcommand);
    }

    [Fact]
    public void CreateInstanceCommand_Should_Have_Licenses_Subcommand()
    {
        // Arrange
        var command = CreateInstanceCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService);

        // Act
        var licensesSubcommand = command.Subcommands.FirstOrDefault(c => c.Name == "licenses");

        // Assert - Subcommand should be registered
        Assert.NotNull(licensesSubcommand);
    }

    [Fact]
    public void CreateInstanceCommand_Should_Have_Handler_For_Complete_Instance_Creation()
    {
        // Arrange
        var command = CreateInstanceCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService);

        // Act & Assert - Main command should have handler for running all steps
        Assert.NotNull(command.Handler);
    }

    [Fact]
    public void CreateInstanceCommand_Should_Be_Named_CreateInstance()
    {
        // Arrange
        var command = CreateInstanceCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService);

        // Act - Command should be created successfully
        // Assert - Command is named "create-instance" for use as "a365 create-instance"
        Assert.NotNull(command);
        Assert.Equal("create-instance", command.Name);
    }

    [Fact]
    public async Task CreateInstance_WhenConfigFileNotFound_ShouldReturnExitCode2()
    {
        // Arrange — ConfigFileNotFoundException.ExitCode is 2 (configuration error).
        // Scripts checking $LASTEXITCODE must distinguish missing config (2) from general errors (1).
        var mockConfigService = Substitute.For<IConfigService>();
        mockConfigService.LoadAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromException<Agent365Config>(new ConfigFileNotFoundException()));

        var command = CreateInstanceCommand.CreateCommand(
            _mockLogger, mockConfigService, _mockExecutor, _mockGraphApiService);

        // Act
        var result = await command.InvokeAsync(new[] { "create-instance" });

        // Assert
        Assert.Equal(2, result);
    }

    [Theory]
    [InlineData("", "Instance creation failed")]
    [InlineData("identity", "Identity creation failed")]
    public async Task CreateInstance_WhenGrantPreReadFails_ExitsNonzeroShowsErrorAndDoesNotMutate(
        string subcommand,
        string expectedCommandError)
    {
        var originalDirectory = Environment.CurrentDirectory;
        var testDirectory = Path.Combine(Path.GetTempPath(), $"create-instance-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(testDirectory, "a365.config.json"),
                $$"""
                {
                  "tenantId": "{{TenantId}}",
                  "environment": "prod"
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(testDirectory, "a365.generated.config.json"),
                $$"""
                {
                  "agentBlueprintId": "{{AgentBlueprintId}}",
                  "agentBlueprintClientSecret": "test-secret",
                  "AgenticAppId": "{{AgenticAppId}}",
                  "AgenticUserId": "{{AgenticUserId}}"
                }
                """);
            Environment.CurrentDirectory = testDirectory;

            var resolver = Substitute.For<IBootstrapConfigResolver>();
            resolver.ResolveAsync(
                    Arg.Any<string?>(),
                    Arg.Any<string?>(),
                    Arg.Any<FileInfo>(),
                    Arg.Is(false),
                    Arg.Any<CancellationToken>())
                .Returns(new Agent365Config
                {
                    TenantId = TenantId,
                    AgentBlueprintId = AgentBlueprintId,
                    AgenticAppId = AgenticAppId,
                    AgenticUserId = AgenticUserId
                });
            _mockGraphApiService.LookupServicePrincipalByAppIdAsync(
                    TenantId,
                    AgenticAppId,
                    Arg.Any<CancellationToken>(),
                    Arg.Any<IEnumerable<string>?>())
                .Returns("agent-sp-object-id");
            _mockGraphApiService.GetOauth2PermissionGrantsAsync(
                    TenantId,
                    "agent-sp-object-id",
                    Arg.Any<CancellationToken>())
                .Returns<Task<List<(string resourceId, string scope, string consentType)>>>(_ =>
                    throw new InvalidOperationException("connection reset"));

            var command = CreateInstanceCommand.CreateCommand(
                _mockLogger,
                _mockConfigService,
                _mockExecutor,
                _mockGraphApiService,
                resolver);
            var parser = new CommandLineBuilder(command).UseDefaults().Build();

            var exitCode = await parser.InvokeAsync(subcommand, new TestConsole());

            exitCode.Should().NotBe(0,
                because: "a failed idempotency pre-read must fail both the root all-steps handler and the identity handler");
            LoggerReceivedContaining(LogLevel.Error, "A365CreateInstanceRunner failed").Should().BeTrue(
                because: "the command must visibly report that instance execution stopped");
            LoggerReceivedContaining(LogLevel.Error, expectedCommandError).Should().BeTrue(
                because: "each materially distinct command handler must surface its own operation-level failure");
            await _mockGraphApiService.DidNotReceive().EnsureServicePrincipalForAppIdAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<bool>());
            await _mockGraphApiService.DidNotReceive().CreateOrUpdateOauth2PermissionGrantAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IEnumerable<string>?>());
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private bool LoggerReceivedContaining(LogLevel level, string fragment)
    {
        return _mockLogger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(call => call.GetArguments())
            .Where(args => args.Length >= 3 && args[0] is LogLevel loggedLevel && loggedLevel == level)
            .Select(args => args[2]?.ToString() ?? string.Empty)
            .Any(message => message.Contains(fragment, StringComparison.Ordinal));
    }
}
