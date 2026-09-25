// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public sealed class A365CreateInstanceRunnerTests : IDisposable
{
    private readonly string _testDirectory;

    public A365CreateInstanceRunnerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"a365-create-instance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public async Task RunAsync_WhenExistingGrantReadFails_LogsErrorReturnsFalseAndDoesNotWriteGrants()
    {
        var configPath = Path.Combine(_testDirectory, "a365.config.json");
        var generatedConfigPath = Path.Combine(_testDirectory, "a365.generated.config.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "tenantId": "11111111-1111-1111-1111-111111111111",
              "environment": "prod"
            }
            """);
        await File.WriteAllTextAsync(
            generatedConfigPath,
            """
            {
              "agentBlueprintId": "22222222-2222-2222-2222-222222222222",
              "agentBlueprintClientSecret": "test-secret",
              "AgenticAppId": "33333333-3333-3333-3333-333333333333",
              "AgenticUserId": "44444444-4444-4444-4444-444444444444"
            }
            """);

        var logger = Substitute.For<ILogger<A365CreateInstanceRunner>>();
        var executor = Substitute.For<CommandExecutor>(NullLogger<CommandExecutor>.Instance);
        var graph = Substitute.ForPartsOf<GraphApiService>(
            NullLogger<GraphApiService>.Instance,
            executor,
            (Func<Task<string?>>?)(() => Task.FromResult<string?>(null)));
        graph.LookupServicePrincipalByAppIdAsync(
                "11111111-1111-1111-1111-111111111111",
                "33333333-3333-3333-3333-333333333333",
                Arg.Any<CancellationToken>(),
                Arg.Any<IEnumerable<string>?>())
            .Returns("agent-sp-object-id");
        graph.GetOauth2PermissionGrantsAsync(
                "11111111-1111-1111-1111-111111111111",
                "agent-sp-object-id",
                Arg.Any<CancellationToken>())
            .Returns<Task<List<(string resourceId, string scope, string consentType)>>>(_ =>
                throw new InvalidOperationException("Microsoft Graph could not read OAuth2 permission grants: HTTP 403 Forbidden."));

        var runner = new A365CreateInstanceRunner(logger, executor, graph);

        var succeeded = await runner.RunAsync(
            configPath,
            generatedConfigPath,
            step: "identity");

        succeeded.Should().BeFalse(
            because: "grant creation cannot safely continue when the idempotency pre-read is unreadable");
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("no grant changes were attempted", StringComparison.Ordinal)),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
        await graph.DidNotReceive().EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<bool>());
        await graph.DidNotReceive().CreateOrUpdateOauth2PermissionGrantAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>());
    }

    [Theory]
    [InlineData("\"graphBaseUrl\": \"https://graph.example/us\"", "Graph base URL")]
    [InlineData("\"authorityHost\": \"http://login.example.com\"", "Authority host")]
    public async Task RunAsync_WhenConfiguredCloudEndpointIsInvalid_LogsErrorAndReturnsFalse(
        string invalidEndpointProperty,
        string expectedSettingName)
    {
        var configPath = Path.Combine(_testDirectory, "a365.config.json");
        var generatedConfigPath = Path.Combine(_testDirectory, "a365.generated.config.json");
        await File.WriteAllTextAsync(
            configPath,
            $$"""
            {
              "tenantId": "11111111-1111-1111-1111-111111111111",
              "environment": "prod",
              {{invalidEndpointProperty}}
            }
            """);
        await File.WriteAllTextAsync(
            generatedConfigPath,
            """
            {
              "agentBlueprintId": "22222222-2222-2222-2222-222222222222",
              "agentBlueprintClientSecret": "test-secret"
            }
            """);

        var logger = Substitute.For<ILogger<A365CreateInstanceRunner>>();
        var executor = Substitute.For<CommandExecutor>(NullLogger<CommandExecutor>.Instance);
        var graph = Substitute.For<GraphApiService>(NullLogger<GraphApiService>.Instance, executor);
        var runner = new A365CreateInstanceRunner(logger, executor, graph);

        var succeeded = await runner.RunAsync(
            configPath,
            generatedConfigPath,
            step: "licenses");

        succeeded.Should().BeFalse(
            because: "invalid sovereign-cloud endpoint configuration is a user-fixable input error, not an unhandled exception path");
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains(expectedSettingName, StringComparison.Ordinal)),
            Arg.Is<ArgumentException>(ex => ex.Message.Contains(expectedSettingName, StringComparison.Ordinal)),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Theory]
    [InlineData("all")]
    [InlineData("identity")]
    [InlineData("licenses")]
    public async Task RunAsync_WhenGovernmentEnvironmentIsAmbiguous_LogsGuidanceWithoutChangingState(string step)
    {
        var configPath = Path.Combine(_testDirectory, "a365.config.json");
        var generatedConfigPath = Path.Combine(_testDirectory, "a365.generated.config.json");
        await File.WriteAllTextAsync(configPath, """
            {
              "tenantId": "11111111-1111-1111-1111-111111111111",
              "environment": "AzureUSGovernment"
            }
            """);
        const string generatedConfig = """
            {
              "agentBlueprintId": "22222222-2222-2222-2222-222222222222",
              "agentBlueprintClientSecret": "test-secret"
            }
            """;
        await File.WriteAllTextAsync(generatedConfigPath, generatedConfig);
        var logger = Substitute.For<ILogger<A365CreateInstanceRunner>>();
        var executor = Substitute.For<CommandExecutor>(NullLogger<CommandExecutor>.Instance);
        var graph = Substitute.For<GraphApiService>(NullLogger<GraphApiService>.Instance, executor);
        var runner = new A365CreateInstanceRunner(logger, executor, graph);

        var succeeded = await runner.RunAsync(configPath, generatedConfigPath, step: step);

        succeeded.Should().BeFalse(
            because: "an ambiguous government environment must return a configuration failure rather than throw");
        (await File.ReadAllTextAsync(generatedConfigPath)).Should().Be(generatedConfig,
            because: "cloud validation must finish before generated state is saved");
        graph.ReceivedCalls().Should().BeEmpty(
            because: "an ambiguous cloud must not configure Graph or start resource operations");
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("Set the environment to gcc, gcc-high, or dod.")),
            Arg.Any<ArgumentException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
