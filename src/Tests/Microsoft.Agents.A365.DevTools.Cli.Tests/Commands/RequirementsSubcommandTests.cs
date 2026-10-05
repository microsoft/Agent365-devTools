// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Agents.A365.DevTools.Cli.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.CommandLine;
using System.CommandLine.Parsing;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for RequirementsSubcommand with custom test requirement checks.
/// These tests validate that the subcommand correctly processes passing and failing checks.
/// </summary>
public class RequirementsSubcommandTests
{
    private readonly ILogger _mockLogger;
    private readonly IConfigService _mockConfigService;

    public RequirementsSubcommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockConfigService = Substitute.For<IConfigService>();
    }

    #region Test Requirement Check Tests

    [Fact]
    public async Task AlwaysPassRequirementCheck_ShouldAlwaysReturnSuccess()
    {
        // Arrange
        var check = new AlwaysPassRequirementCheck();
        var config = new Agent365Config();

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeTrue();
        result.ErrorMessage.Should().BeNullOrEmpty();
        result.Details.Should().Contain("always passes");
    }

    [Fact]
    public async Task AlwaysFailRequirementCheck_ShouldAlwaysReturnFailure()
    {
        // Arrange
        var check = new AlwaysFailRequirementCheck();
        var config = new Agent365Config();

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("always fails");
        result.ResolutionGuidance.Should().NotBeNullOrEmpty();
        result.Details.Should().Contain("Test failure details");
    }

    [Fact]
    public void AlwaysPassRequirementCheck_ShouldHaveCorrectMetadata()
    {
        // Arrange
        var check = new AlwaysPassRequirementCheck();

        // Act & Assert
        check.Name.Should().Be("Test Always Pass Check");
        check.Description.Should().Be("Test requirement check that always passes");
        check.Category.Should().Be("Test");
    }

    [Fact]
    public void AlwaysFailRequirementCheck_ShouldHaveCorrectMetadata()
    {
        // Arrange
        var check = new AlwaysFailRequirementCheck();

        // Act & Assert
        check.Name.Should().Be("Test Always Fail Check");
        check.Description.Should().Be("Test requirement check that always fails");
        check.Category.Should().Be("Test");
    }

    #endregion

    #region RequirementCheckResult Tests

    [Fact]
    public void RequirementCheckResult_Success_ShouldCreateSuccessResult()
    {
        // Act
        var result = RequirementCheckResult.Success("Test details");

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeTrue();
        result.Details.Should().Be("Test details");
        result.ErrorMessage.Should().BeNullOrEmpty();
        result.ResolutionGuidance.Should().BeNullOrEmpty();
    }

    [Fact]
    public void RequirementCheckResult_Failure_ShouldCreateFailureResult()
    {
        // Act
        var result = RequirementCheckResult.Failure(
            "Test error",
            "Test resolution",
            "Test details");

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().Be("Test error");
        result.ResolutionGuidance.Should().Be("Test resolution");
        result.Details.Should().Be("Test details");
    }

    [Fact]
    public void RequirementCheckResult_SuccessWithoutDetails_ShouldHaveNullDetails()
    {
        // Act
        var result = RequirementCheckResult.Success();

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeTrue();
        result.Details.Should().BeNull();
    }

    [Fact]
    public void RequirementCheckResult_FailureWithoutDetails_ShouldHaveNullDetails()
    {
        // Act
        var result = RequirementCheckResult.Failure(
            "Test error",
            "Test resolution");

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse();
        result.Details.Should().BeNull();
    }

    #endregion

    #region GetRequirementChecks Composition Tests

    [Fact]
    public void GetRequirementChecks_ContainsAllExpectedCheckTypes()
    {
        // GetRequirementChecks is now derived from GetSystemRequirementChecks + GetConfigRequirementChecks.
        // This test guards against a check being accidentally added to one sub-list but not propagated.
        var mockExecutor = Substitute.ForPartsOf<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        var mockAuthValidator = Substitute.ForPartsOf<AzureAuthValidator>(NullLogger<AzureAuthValidator>.Instance, mockExecutor);
        var mockValidator = Substitute.For<IClientAppValidator>();

        var checks = RequirementsSubcommand.GetRequirementChecks(mockAuthValidator, mockValidator);

        checks.Should().HaveCount(4, "system (2) + config (2) checks; LocationRequirementCheck removed because the Location config property was deleted when Azure App Service deploy/infra provisioning was removed");
        checks.Should().ContainSingle(c => c is FrontierPreviewRequirementCheck);
        checks.Should().ContainSingle(c => c is PowerShellModulesRequirementCheck);
        checks.Should().ContainSingle(c => c is AzureAuthRequirementCheck);
        checks.Should().ContainSingle(c => c is ClientAppRequirementCheck);
    }

    [Fact]
    public void GetRequirementChecks_SystemChecksRunBeforeConfigChecks()
    {
        // GetRequirementChecks returns system checks (FrontierPreview, PowerShellModules)
        // before config checks (AzureAuth, Location, ClientApp).
        var mockExecutor = Substitute.ForPartsOf<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        var mockAuthValidator = Substitute.ForPartsOf<AzureAuthValidator>(NullLogger<AzureAuthValidator>.Instance, mockExecutor);
        var mockValidator = Substitute.For<IClientAppValidator>();

        var all = RequirementsSubcommand.GetRequirementChecks(mockAuthValidator, mockValidator);

        // System checks come first
        var types = all.Select(c => c.GetType()).ToList();
        types.IndexOf(typeof(FrontierPreviewRequirementCheck))
            .Should().BeLessThan(types.IndexOf(typeof(AzureAuthRequirementCheck)),
                "system checks should run before config checks");
        types.IndexOf(typeof(PowerShellModulesRequirementCheck))
            .Should().BeLessThan(types.IndexOf(typeof(AzureAuthRequirementCheck)),
                "system checks should run before config checks");
    }

    #endregion

    #region Multiple Check Execution Tests

    [Fact]
    public async Task MultipleChecks_AllPass_ShouldReturnTrue()
    {
        // Arrange
        var checks = new List<IRequirementCheck>
        {
            new AlwaysPassRequirementCheck(),
            new AlwaysPassRequirementCheck()
        };
        var config = new Agent365Config();

        // Act
        var results = new List<RequirementCheckResult>();
        foreach (var check in checks)
        {
            var result = await check.CheckAsync(config, _mockLogger);
            results.Add(result);
        }

        var allPassed = results.All(r => r.Passed);

        // Assert
        allPassed.Should().BeTrue();
        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task MultipleChecks_SomeFail_ShouldReturnFalse()
    {
        // Arrange
        var checks = new List<IRequirementCheck>
        {
            new AlwaysPassRequirementCheck(),
            new AlwaysFailRequirementCheck(),
            new AlwaysPassRequirementCheck()
        };
        var config = new Agent365Config();

        // Act
        var results = new List<RequirementCheckResult>();
        foreach (var check in checks)
        {
            var result = await check.CheckAsync(config, _mockLogger);
            results.Add(result);
        }

        var allPassed = results.All(r => r.Passed);
        var passedCount = results.Count(r => r.Passed);
        var failedCount = results.Count(r => !r.Passed);

        // Assert
        allPassed.Should().BeFalse();
        passedCount.Should().Be(2);
        failedCount.Should().Be(1);
        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task MultipleChecks_AllFail_ShouldReturnFalse()
    {
        // Arrange
        var checks = new List<IRequirementCheck>
        {
            new AlwaysFailRequirementCheck(),
            new AlwaysFailRequirementCheck()
        };
        var config = new Agent365Config();

        // Act
        var results = new List<RequirementCheckResult>();
        foreach (var check in checks)
        {
            var result = await check.CheckAsync(config, _mockLogger);
            results.Add(result);
        }

        var allPassed = results.All(r => r.Passed);
        var failedCount = results.Count(r => !r.Passed);

        // Assert
        allPassed.Should().BeFalse();
        failedCount.Should().Be(2);
        results.Should().HaveCount(2);
    }

    #endregion

    #region ClientAppRequirementCheck First-Party Detection Tests

    [Fact]
    public async Task ClientAppRequirementCheck_WithCustomAppId_DelegatesToValidatorUnchanged()
    {
        var mockValidator = Substitute.For<IClientAppValidator>();
        var check = new ClientAppRequirementCheck(mockValidator);
        var config = new Agent365Config
        {
            ClientAppId = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
            TenantId = "12345678-1234-1234-1234-123456789012"
        };

        await check.CheckAsync(config, _mockLogger);

        await mockValidator.Received(1).EnsureValidClientAppAsync(
            config.ClientAppId, config.TenantId, ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClientAppRequirementCheck_WithWellKnownClientAppId_DelegatesTheWellKnownIdToValidator()
    {
        // First-party semantics are derived from the resolved client app ID alone — there is no
        // caller-supplied override — so the check must pass the ID through untouched and let the
        // validator apply the non-mutating first-party path.
        var mockValidator = Substitute.For<IClientAppValidator>();
        var check = new ClientAppRequirementCheck(mockValidator);
        var config = new Agent365Config
        {
            ClientAppId = AuthenticationConstants.WellKnownClientAppId,
            TenantId = "12345678-1234-1234-1234-123456789012"
        };

        await check.CheckAsync(config, _mockLogger);

        await mockValidator.Received(1).EnsureValidClientAppAsync(
            AuthenticationConstants.WellKnownClientAppId, config.TenantId, ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Command_DoesNotExposeAFirstPartyOption()
    {
        // First-party validation is selected by the resolved client app ID, never by a flag: a flag
        // could force weak first-party validation onto a tenant-owned custom app.
        var mockConfigService = Substitute.For<IConfigService>();
        var mockExecutor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        var mockAuthValidator = Substitute.ForPartsOf<AzureAuthValidator>(NullLogger<AzureAuthValidator>.Instance, mockExecutor);
        var mockClientAppValidator = Substitute.For<IClientAppValidator>();
        var mockGraphApiService = Substitute.ForPartsOf<GraphApiService>(
            Substitute.For<ILogger<GraphApiService>>(), mockExecutor, (Func<Task<string?>>)(() => Task.FromResult<string?>(null)));

        var command = RequirementsSubcommand.CreateCommand(
            _mockLogger, mockConfigService, mockAuthValidator, mockClientAppValidator, mockExecutor, mockGraphApiService);

        command.Options.Should().NotContain(
            option => option.Aliases.Contains("--first-party"),
            because: "the CLI contract must not offer a flag that applies first-party validation to a tenant-owned custom app");

        command.Parse("--first-party").Errors.Should().NotBeEmpty(
            because: "an unrecognized option must be rejected rather than silently ignored");
    }

    #endregion
}
