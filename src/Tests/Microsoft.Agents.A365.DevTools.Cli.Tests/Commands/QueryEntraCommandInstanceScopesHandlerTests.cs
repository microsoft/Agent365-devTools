// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using CommandExecutionResult = Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Handler-level tests for <c>a365 query-entra instance-scopes</c>.
/// They pin the Azure CLI service-principal lookup and unreadable-grants failure contract.
/// </summary>
public class QueryEntraCommandInstanceScopesHandlerTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string AgentIdentityAppId = "22222222-2222-2222-2222-222222222222";
    private const string AgentIdentityServicePrincipalObjectId = "33333333-3333-3333-3333-333333333333";

    private readonly ILogger<QueryEntraCommand> _mockLogger;
    private readonly IConfigService _mockConfigService;
    private readonly CommandExecutor _mockExecutor;
    private readonly GraphApiService _mockGraphApiService;
    private readonly AgentBlueprintService _mockBlueprintService;
    private readonly IBootstrapConfigResolver _mockResolver;

    public QueryEntraCommandInstanceScopesHandlerTests()
    {
        _mockLogger = Substitute.For<ILogger<QueryEntraCommand>>();
        _mockConfigService = Substitute.For<IConfigService>();
        _mockExecutor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        _mockGraphApiService = Substitute.For<GraphApiService>(
            Substitute.For<ILogger<GraphApiService>>(),
            _mockExecutor);
        _mockBlueprintService = Substitute.ForPartsOf<AgentBlueprintService>(
            Substitute.For<ILogger<AgentBlueprintService>>(),
            _mockGraphApiService);
        _mockResolver = Substitute.For<IBootstrapConfigResolver>();

        _mockResolver.ResolveAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<FileInfo>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(new Agent365Config
            {
                TenantId = TenantId,
                AgenticAppId = AgentIdentityAppId,
                AgentUserPrincipalName = "agent@example.com"
            });
    }

    private Command BuildRootCommand() =>
        QueryEntraCommand.CreateCommand(
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService,
            _mockBlueprintService,
            _mockResolver);

    private bool LoggerReceivedContaining(LogLevel level, string fragment)
    {
        var calls = _mockLogger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(c => c.GetArguments())
            .Where(args => args.Length >= 3 && args[0] is LogLevel lvl && lvl == level)
            .Select(args => args[2]?.ToString() ?? string.Empty);
        return calls.Any(s => s.Contains(fragment, StringComparison.Ordinal));
    }

    private static CommandExecutionResult SuccessfulCommand(string standardOutput) =>
        new()
        {
            ExitCode = 0,
            StandardOutput = standardOutput
        };

    private static CommandExecutionResult FailedCommand(string standardError) =>
        new()
        {
            ExitCode = 1,
            StandardError = standardError
        };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstanceScopesSubcommand_UsesAzureCliLookup_AndServicePrincipalObjectIdForOauth2GrantFilter(bool hasGrants)
    {
        _mockExecutor.ExecuteAsync(
                "az",
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var args = ci.ArgAt<string>(1);
                if (args.StartsWith("ad sp list", StringComparison.Ordinal))
                    return Task.FromResult(SuccessfulCommand($$"""[{"objectId":"{{AgentIdentityServicePrincipalObjectId}}","appId":"{{AgentIdentityAppId}}","displayName":"Agent Identity"}]"""));
                if (args.Contains("/oauth2PermissionGrants?", StringComparison.Ordinal))
                    return Task.FromResult(SuccessfulCommand(hasGrants
                        ? """{"value":[{"scope":"User.Read","resourceId":"44444444-4444-4444-4444-444444444444"}]}"""
                        : """{"value": []}"""));
                return Task.FromResult(SuccessfulCommand(
                    """{"displayName":"Microsoft Graph","appId":"00000003-0000-0000-c000-000000000000"}"""));
            });

        var parser = new CommandLineBuilder(BuildRootCommand()).Build();
        var exitCode = await parser.InvokeAsync("instance-scopes --agent-name test-agent", new TestConsole());

        exitCode.Should().Be(0,
            because: "both populated and empty grants lists are successful diagnostic reads");
        LoggerReceivedContaining(LogLevel.Information, "No direct OAuth2 permission grants found").Should().Be(!hasGrants,
            because: "the query reports only direct grants, not permissions inherited from the blueprint");
        LoggerReceivedContaining(LogLevel.Information, "admin consent has not been granted").Should().BeFalse(
            because: "an empty direct-grant response cannot establish that inherited consent is absent");
        if (!hasGrants)
        {
            LoggerReceivedContaining(LogLevel.Information, "a365 query-entra inheritance").Should().BeTrue(
                because: "users must inspect inheritance before attempting unnecessary direct consent");
        }
        if (hasGrants)
        {
            LoggerReceivedContaining(LogLevel.Information, "User.Read").Should().BeTrue(
                because: "the diagnostic must display the scopes returned for the service principal");
        }
        await _mockExecutor.Received().ExecuteAsync(
            "az",
            Arg.Is<string>(s =>
                s.Contains("ad sp list", StringComparison.Ordinal) &&
                s.Contains("""[].{objectId:id,appId:appId,displayName:displayName}""", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
        await _mockExecutor.Received().ExecuteAsync(
            "az",
            Arg.Is<string>(s =>
                s.Contains($"oauth2PermissionGrants?$filter=clientId eq '{AgentIdentityServicePrincipalObjectId}'", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
        await _mockGraphApiService.DidNotReceiveWithAnyArgs().LookupServicePrincipalByAppIdWithResponseAsync(
            default!,
            default!,
            default,
            default);
        await _mockGraphApiService.DidNotReceiveWithAnyArgs().GetServicePrincipalDisplayNameByAppIdAsync(
            default!,
            default!,
            default,
            default);
    }

    [Fact]
    public async Task InstanceScopesSubcommand_WhenServicePrincipalLookupReturnsEmptyArray_WarnsAndExitsOne()
    {
        _mockExecutor.ExecuteAsync(
                "az",
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SuccessfulCommand("[]")));

        var parser = new CommandLineBuilder(BuildRootCommand()).Build();
        var exitCode = await parser.InvokeAsync("instance-scopes --agent-name test-agent", new TestConsole());

        exitCode.Should().Be(1,
            because: "without a tenant service principal the command cannot inspect tenant-wide delegated grants for the instance");
        LoggerReceivedContaining(LogLevel.Warning, "No service principal found for this application").Should().BeTrue();
        await AssertNoGrantQueryAsync();
    }

    [Theory]
    [InlineData("""[{"appId":"22222222-2222-2222-2222-222222222222","displayName":"Agent Identity"}]""")]
    [InlineData("""[{"objectId":null,"appId":"22222222-2222-2222-2222-222222222222","displayName":"Agent Identity"}]""")]
    [InlineData("""[{"objectId":123,"appId":"22222222-2222-2222-2222-222222222222","displayName":"Agent Identity"}]""")]
    [InlineData("""[{"objectId":" ","appId":"22222222-2222-2222-2222-222222222222","displayName":"Agent Identity"}]""")]
    public async Task InstanceScopesSubcommand_WhenServicePrincipalLookupResponseHasMissingOrInvalidObjectId_LogsErrorAndExitsOne(
        string lookupJson)
    {
        _mockExecutor.ExecuteAsync(
                "az",
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SuccessfulCommand(lookupJson)));

        var parser = new CommandLineBuilder(BuildRootCommand()).Build();
        var exitCode = await parser.InvokeAsync("instance-scopes --agent-name test-agent", new TestConsole());

        exitCode.Should().Be(1,
            because: "the oauth2PermissionGrants clientId filter requires a non-empty service-principal object ID");
        LoggerReceivedContaining(LogLevel.Error, "Expected a non-empty string objectId").Should().BeTrue(
            because: "malformed lookup data must stay visible instead of being treated as missing consent");
        await AssertNoGrantQueryAsync();
    }

    [Fact]
    public async Task InstanceScopesSubcommand_WhenServicePrincipalLookupFails_LogsErrorAndExitsOne()
    {
        _mockExecutor.ExecuteAsync(
                "az",
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(FailedCommand("HTTP 403 Forbidden.")));

        var parser = new CommandLineBuilder(BuildRootCommand()).Build();
        var exitCode = await parser.InvokeAsync("instance-scopes --agent-name test-agent", new TestConsole());

        exitCode.Should().Be(1,
            because: "the command cannot continue when the service-principal lookup itself failed");
        LoggerReceivedContaining(LogLevel.Error, "HTTP 403 Forbidden").Should().BeTrue(
            because: "lookup failures must be surfaced as permission or transport errors, not misreported as 'no grants found'");
        await AssertNoGrantQueryAsync();
    }

    [Fact]
    public async Task InstanceScopesSubcommand_WhenGrantsCallFails_LogsUnreadableGuidanceAndExitsOne()
    {
        _mockExecutor.ExecuteAsync(
                "az",
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var args = ci.ArgAt<string>(1);
                return Task.FromResult(
                    args.StartsWith("ad sp list", StringComparison.Ordinal)
                        ? SuccessfulCommand($$"""[{"objectId":"{{AgentIdentityServicePrincipalObjectId}}","appId":"{{AgentIdentityAppId}}","displayName":"Agent Identity"}]""")
                        : FailedCommand("HTTP 403 Forbidden."));
            });

        var parser = new CommandLineBuilder(BuildRootCommand()).Build();
        var exitCode = await parser.InvokeAsync("instance-scopes --agent-name test-agent", new TestConsole());

        exitCode.Should().Be(1,
            because: "an unreadable grants table is not evidence that consent is absent");
        LoggerReceivedContaining(LogLevel.Information, "Cannot read tenant-wide OAuth2 permission grants").Should().BeTrue(
            because: "the operator needs the administrative-read guidance instead of a false no-consent result");
        LoggerReceivedContaining(LogLevel.Information, "No direct OAuth2 permission grants found").Should().BeFalse(
            because: "failed grant enumeration must not be misreported as a successful empty read");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"unexpected":[]}""")]
    [InlineData("""{"value":{}}""")]
    [InlineData("""{"value":null}""")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task InstanceScopesSubcommand_WhenGrantsResponseIsMalformed_LogsUnreadableGuidanceAndExitsOne(
        string grantsJson)
    {
        _mockExecutor.ExecuteAsync(
                "az",
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var args = ci.ArgAt<string>(1);
                return Task.FromResult(
                    args.StartsWith("ad sp list", StringComparison.Ordinal)
                        ? SuccessfulCommand($$"""[{"objectId":"{{AgentIdentityServicePrincipalObjectId}}","appId":"{{AgentIdentityAppId}}","displayName":"Agent Identity"}]""")
                        : SuccessfulCommand(grantsJson));
            });

        var parser = new CommandLineBuilder(BuildRootCommand()).Build();
        var exitCode = await parser.InvokeAsync("instance-scopes --agent-name test-agent", new TestConsole());

        exitCode.Should().Be(1,
            because: "a malformed grants payload is unreadable and must fail the diagnostic instead of claiming consent is absent");
        LoggerReceivedContaining(LogLevel.Information, "Cannot read tenant-wide OAuth2 permission grants").Should().BeTrue(
            because: "malformed responses still require the unreadable-grants guidance");
        LoggerReceivedContaining(LogLevel.Information, "No direct OAuth2 permission grants found").Should().BeFalse(
            because: "a malformed payload is not equivalent to a successful empty array");
    }

    private async Task AssertNoGrantQueryAsync() =>
        await _mockExecutor.DidNotReceive().ExecuteAsync(
            "az",
            Arg.Is<string>(s => s.Contains("/oauth2PermissionGrants?", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
}
