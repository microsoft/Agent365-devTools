// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands.SetupSubcommands;

/// <summary>
/// Regression tests for ServiceTree support on blueprint creation.
/// </summary>
public class BlueprintServiceTreeOptionTests
{
    [Fact]
    public void BlueprintCreationOptions_DefaultsServiceTreeIdToNull()
    {
        var options = new BlueprintCreationOptions();

        options.ServiceTreeId.Should().BeNull(
            because: "tenants that do not enforce ServiceTree registration must keep the existing " +
                     "manifest shape, so serviceManagementReference is only sent when requested");
    }

    [Fact]
    public void BlueprintCreationOptions_PreservesServiceTreeId()
    {
        var options = new BlueprintCreationOptions(ServiceTreeId: "00000000-0000-0000-0000-000000000000");

        options.ServiceTreeId.Should().Be("00000000-0000-0000-0000-000000000000",
            because: "the value is written to the blueprint's serviceManagementReference, which Entra " +
                     "requires in tenants that enforce ServiceTree registration");
    }

    [Fact]
    public void BlueprintCreationOptions_ServiceTreeIdIsIndependentOfDeferConsent()
    {
        var options = new BlueprintCreationOptions(DeferConsent: true, ServiceTreeId: "svc-tree-1");

        options.DeferConsent.Should().BeTrue();
        options.ServiceTreeId.Should().Be("svc-tree-1",
            because: "setup all defers consent and must still be able to supply a ServiceTree ID");
    }

    [Fact]
    public void SetupContext_TrimsServiceTreeId()
    {
        var context = CreateSetupContext("  svc-tree-1  ");

        context.ServiceTreeId.Should().Be("svc-tree-1",
            because: "serviceManagementReference is sent verbatim to Entra, so surrounding " +
                     "whitespace from the command line must not reach the application manifest");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetupContext_TreatsMissingOrBlankServiceTreeIdAsNull(string? value)
    {
        var context = CreateSetupContext(value);

        context.ServiceTreeId.Should().BeNull(
            because: "tenants that do not enforce ServiceTree registration must keep the existing " +
                     "manifest shape, so serviceManagementReference is only sent when requested");
    }

    [Fact]
    public void SetupAllCommand_ExposesServiceTreeIdOption()
    {
        var command = CreateSetupAllCommand();

        command.Options.Select(o => o.Name).Should().Contain(
            "service-tree-id",
            because: "'setup all' creates the blueprint app registration through the same path as " +
                     "'setup blueprint', so it fails with ServiceTreeValueMissing in tenants that " +
                     "enforce ServiceTree unless the ID can be supplied to the orchestrated run too");
    }

    private static SetupContext CreateSetupContext(string? serviceTreeId)
    {
        var graphApiService = CreateGraphApiService();

        return new SetupContext(
            config: new Cli.Models.Agent365Config(),
            results: new SetupResults(),
            logger: NullLogger.Instance,
            configFile: new FileInfo("a365.config.json"),
            generatedConfigPath: "a365.generated.config.json",
            correlationId: "test-correlation-id",
            skipInfrastructure: false,
            skipRequirements: false,
            cancellationToken: CancellationToken.None,
            configService: Substitute.For<IConfigService>(),
            executor: CreateExecutor(),
            backendConfigurator: Substitute.For<ITeamsGraphBackendConfigurator>(),
            authValidator: new AzureAuthValidator(NullLogger<AzureAuthValidator>.Instance, CreateExecutor()),
            platformDetector: new PlatformDetector(NullLogger<PlatformDetector>.Instance),
            graphApiService: graphApiService,
            blueprintService: Substitute.ForPartsOf<AgentBlueprintService>(
                Substitute.For<ILogger<AgentBlueprintService>>(), graphApiService),
            blueprintLookupService: new BlueprintLookupService(
                NullLogger<BlueprintLookupService>.Instance, graphApiService),
            federatedCredentialService: Substitute.ForPartsOf<FederatedCredentialService>(
                Substitute.For<ILogger<FederatedCredentialService>>(), graphApiService),
            clientAppValidator: Substitute.For<IClientAppValidator>(),
            serviceTreeId: serviceTreeId);
    }

    private static System.CommandLine.Command CreateSetupAllCommand()
    {
        var graphApiService = CreateGraphApiService();

        return AllSubcommand.CreateCommand(
            logger: NullLogger.Instance,
            configService: Substitute.For<IConfigService>(),
            executor: CreateExecutor(),
            backendConfigurator: Substitute.For<ITeamsGraphBackendConfigurator>(),
            authValidator: new AzureAuthValidator(NullLogger<AzureAuthValidator>.Instance, CreateExecutor()),
            platformDetector: new PlatformDetector(NullLogger<PlatformDetector>.Instance),
            graphApiService: graphApiService,
            blueprintService: Substitute.ForPartsOf<AgentBlueprintService>(
                Substitute.For<ILogger<AgentBlueprintService>>(), graphApiService),
            clientAppValidator: Substitute.For<IClientAppValidator>(),
            blueprintLookupService: new BlueprintLookupService(
                NullLogger<BlueprintLookupService>.Instance, graphApiService),
            federatedCredentialService: Substitute.ForPartsOf<FederatedCredentialService>(
                Substitute.For<ILogger<FederatedCredentialService>>(), graphApiService));
    }

    private static CommandExecutor CreateExecutor() =>
        Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

    private static GraphApiService CreateGraphApiService() =>
        Substitute.ForPartsOf<GraphApiService>(
            Substitute.For<ILogger<GraphApiService>>(),
            CreateExecutor(),
            (Func<Task<string?>>)(() => Task.FromResult<string?>(null)));
}
