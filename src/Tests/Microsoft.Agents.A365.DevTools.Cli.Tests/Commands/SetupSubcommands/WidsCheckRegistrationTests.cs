// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands.SetupSubcommands;

/// <summary>
/// Verifies that <see cref="WidsOptionalClaimRequirementCheck"/> is registered in the right
/// requirement-check lists, and crucially that it appears AFTER <see cref="ClientAppRequirementCheck"/>
/// for DW and non-DW-non-bootstrap flows, and is OMITTED from the non-DW bootstrap flow.
///
/// <para>
/// Why the ordering and conditional inclusion matter:
/// </para>
/// <list type="bullet">
///   <item><description>
///     The client-app probe must run first because the wids probe inspects the same client app's
///     <c>optionalClaims.accessToken</c> via Graph. If the client app cannot be located, the wids
///     check would emit a confusing secondary failure on top of the real root cause.
///   </description></item>
///   <item><description>
///     During the non-DW bootstrap pass (<c>setup all --agent-name &lt;name&gt;</c> without an existing
///     <c>a365.config.json</c>), the client app is being created on the fly — it does not yet exist in
///     the tenant. Including the wids probe would 404 and fail the requirements pass for a check that
///     cannot possibly succeed yet.
///   </description></item>
/// </list>
/// </summary>
public class WidsCheckRegistrationTests
{
    private static AzureAuthValidator CreateAuthValidator()
    {
        var executor = new CommandExecutor(NullLogger<CommandExecutor>.Instance);
        return Substitute.For<AzureAuthValidator>(
            NullLogger<AzureAuthValidator>.Instance, executor);
    }

    private static IClientAppValidator CreateClientAppValidator()
        => Substitute.For<IClientAppValidator>();

    [Fact]
    public void GetChecks_DwSetupAll_IncludesWidsOptionalClaimCheck_AfterClientApp()
    {
        // Arrange
        var auth = CreateAuthValidator();
        var clientAppValidator = CreateClientAppValidator();

        // Act
        List<IRequirementCheck> checks = AllSubcommand.GetChecks(auth, clientAppValidator);

        // Assert
        var clientAppIndex = checks.FindIndex(c => c is ClientAppRequirementCheck);
        var widsIndex = checks.FindIndex(c => c is WidsOptionalClaimRequirementCheck);

        clientAppIndex.Should().BeGreaterOrEqualTo(0,
            because: "the DW setup-all requirements pass must probe the client app before any " +
                     "downstream checks that depend on it being resolvable in Entra");

        widsIndex.Should().BeGreaterOrEqualTo(0,
            because: "without the wids optional claim, Global Administrator role detection in " +
                     "the batch permissions orchestrator silently returns Unknown and Phase 2b " +
                     "AllPrincipals OAuth2 grants are skipped — leaving the blueprint with " +
                     "inheritablePermissions.kind=allAllowed but zero granted scopes/roles");

        widsIndex.Should().BeGreaterThan(clientAppIndex,
            because: "the wids probe reads optionalClaims on the same client app the ClientApp " +
                     "check resolves; if the client app probe fails first the wids probe would " +
                     "emit a confusing secondary failure that masks the real root cause");
    }

    [Fact]
    public void GetNonDwChecks_NonBootstrap_IncludesWidsOptionalClaimCheck_AfterClientApp()
    {
        // Arrange
        var auth = CreateAuthValidator();
        var clientAppValidator = CreateClientAppValidator();

        // Act
        List<IRequirementCheck> checks = AllSubcommand.GetNonDwChecks(
            auth, clientAppValidator, isBootstrap: false);

        // Assert
        var clientAppIndex = checks.FindIndex(c => c is ClientAppRequirementCheck);
        var widsIndex = checks.FindIndex(c => c is WidsOptionalClaimRequirementCheck);

        clientAppIndex.Should().BeGreaterOrEqualTo(0,
            because: "non-bootstrap non-DW setup uses an existing a365.config.json that names a " +
                     "client app, so the client-app probe must validate it before downstream checks");

        widsIndex.Should().BeGreaterOrEqualTo(0,
            because: "non-bootstrap non-DW setup also performs blueprint-level AllPrincipals OAuth2 " +
                     "grants that depend on Global Administrator role detection via the access " +
                     "token's wids claim — the absence of wids silently breaks those grants");

        widsIndex.Should().BeGreaterThan(clientAppIndex,
            because: "the wids probe inspects the same client app that ClientAppRequirementCheck " +
                     "resolves, so the client-app probe must run first");
    }

    [Fact]
    public void GetNonDwChecks_Bootstrap_ExcludesWidsOptionalClaimCheck()
    {
        // Arrange
        var auth = CreateAuthValidator();
        var clientAppValidator = CreateClientAppValidator();

        // Act
        List<IRequirementCheck> checks = AllSubcommand.GetNonDwChecks(
            auth, clientAppValidator, isBootstrap: true);

        // Assert
        checks.Should().NotContain(c => c is WidsOptionalClaimRequirementCheck,
            because: "during the bootstrap pass (setup all --agent-name <name> with no static " +
                     "config), the custom client app is being resolved/created dynamically and " +
                     "is not guaranteed to exist yet in the tenant — including the wids probe " +
                     "would 404 and fail a requirements pass for a check that cannot possibly " +
                     "succeed before the client app exists");

        checks.Should().NotContain(c => c is ClientAppRequirementCheck,
            because: "the ClientApp probe also depends on a static a365.config.json that does not " +
                     "yet exist during bootstrap, so both client-app-dependent checks are deferred " +
                     "to a subsequent (non-bootstrap) run of setup");
    }

    [Fact]
    public void GetChecks_DwSetupAll_RegistersWidsCheckExactlyOnce()
    {
        // Arrange
        var auth = CreateAuthValidator();
        var clientAppValidator = CreateClientAppValidator();

        // Act
        List<IRequirementCheck> checks = AllSubcommand.GetChecks(auth, clientAppValidator);

        // Assert
        checks.Count(c => c is WidsOptionalClaimRequirementCheck).Should().Be(1,
            because: "duplicate registration would double-probe Graph for the same optionalClaims " +
                     "value and emit the same failure twice in the requirements summary");
    }

    [Fact]
    public void GetChecks_BlueprintSubcommand_IncludesWidsOptionalClaimCheck_AfterClientApp()
    {
        // The setup blueprint standalone subcommand also runs the batch permissions orchestrator
        // (when invoked without --endpoint-only), so it has the same wids-claim dependency as
        // setup all. Verify it registers the check in the same order.

        // Arrange
        var auth = CreateAuthValidator();
        var clientAppValidator = CreateClientAppValidator();

        // Act
        List<IRequirementCheck> checks = BlueprintSubcommand.GetChecks(auth, clientAppValidator);

        // Assert
        var clientAppIndex = checks.FindIndex(c => c is ClientAppRequirementCheck);
        var widsIndex = checks.FindIndex(c => c is WidsOptionalClaimRequirementCheck);

        clientAppIndex.Should().BeGreaterOrEqualTo(0,
            because: "setup blueprint resolves the client app to write inheritable permissions " +
                     "against the blueprint SP, so the client-app probe must run first");

        widsIndex.Should().BeGreaterOrEqualTo(0,
            because: "setup blueprint performs the same Phase 2b AllPrincipals grants as setup " +
                     "all and therefore has the same wids-claim dependency for GA role detection");

        widsIndex.Should().BeGreaterThan(clientAppIndex,
            because: "client-app resolvability is a precondition for inspecting its optionalClaims, " +
                     "so the wids probe must always follow the client-app probe");
    }
}
