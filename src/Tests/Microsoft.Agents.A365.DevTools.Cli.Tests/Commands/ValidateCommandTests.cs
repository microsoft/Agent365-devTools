// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.CommandLine;
using System.Text.Json;
using Xunit;
using Microsoft.Agents.A365.DevTools.Validation;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

[CollectionDefinition("ValidateCommandTests", DisableParallelization = true)]
public class ValidateCommandTestCollection { }

[Collection("ValidateCommandTests")]
public class ValidateCommandTests : IDisposable
{
    private readonly ILogger<ValidateCommand> _logger;
    private readonly IConfigService _configService;
    private readonly string _reportPath;

    public ValidateCommandTests()
    {
        _logger = Substitute.For<ILogger<ValidateCommand>>();
        _configService = Substitute.For<IConfigService>();
        _reportPath = Path.Combine(Directory.GetCurrentDirectory(), ValidateCommand.ReportFileName);
    }

    public void Dispose()
    {
        if (File.Exists(_reportPath))
        {
            File.Delete(_reportPath);
        }
    }

    [Fact]
    public void ValidateCommand_IsRegisteredWithExpectedNameAndDescription()
    {
        // Act
        var command = ValidateCommand.CreateCommand(_logger, _configService, requirementChecksOverride: new List<IRequirementCheck>());

        // Assert
        command.Name.Should().Be("validate");
        command.Description.Should().Contain("Validate the local Agent 365 CLI configuration");
    }

    [Fact]
    public async Task ValidateCommand_WithValidConfigAndPassingChecks_ReturnsExitCode0()
    {
        // Arrange
        _configService.ConfigExistsAsync(Arg.Any<string>()).Returns(true);
        _configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(new Agent365Config
        {
            TenantId = "12345678-1234-1234-1234-123456789012",
            ClientAppId = "87654321-4321-4321-4321-210987654321",
            AgentIdentityDisplayName = "Test Agent"
        });

        var root = new RootCommand();
        root.AddCommand(ValidateCommand.CreateCommand(_logger, _configService, requirementChecksOverride: [new AlwaysPassRequirementCheck()]));

        // Act
        var exitCode = await root.InvokeAsync("validate");

        // Assert
        exitCode.Should().Be(0, because: "a valid config and passing validation checks should succeed");
    }

    [Fact]
    public async Task ValidateCommand_WithMissingConfig_ReturnsExitCode1()
    {
        // Arrange
        _configService.ConfigExistsAsync(Arg.Any<string>()).Returns(false);

        var root = new RootCommand();
        root.AddCommand(ValidateCommand.CreateCommand(_logger, _configService, requirementChecksOverride: [new AlwaysPassRequirementCheck()]));

        // Act
        var exitCode = await root.InvokeAsync("validate");

        // Assert
        exitCode.Should().Be(1, because: "missing config should fail immediately since setup must be run first");
    }

    [Fact]
    public async Task ValidateCommand_WithMissingConfig_WritesReportWithStructuralBlocker()
    {
        // Arrange
        _configService.ConfigExistsAsync(Arg.Any<string>()).Returns(false);

        var root = new RootCommand();
        root.AddCommand(ValidateCommand.CreateCommand(_logger, _configService, requirementChecksOverride: [new AlwaysPassRequirementCheck()]));

        // Act
        await root.InvokeAsync("validate");

        // Assert
        File.Exists(_reportPath).Should().BeTrue(because: "report should always be written even on failure");
        var report = JsonSerializer.Deserialize<ValidateReport>(await File.ReadAllTextAsync(_reportPath));
        report.Should().NotBeNull();
        report!.Summary.Ok.Should().BeFalse();
        report.Summary.Blocker.Should().Be("structural");
        report.Tiers.Structural.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateCommand_WithValidConfig_WritesReportWithSummaryOk()
    {
        // Arrange
        _configService.ConfigExistsAsync(Arg.Any<string>()).Returns(true);
        _configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(new Agent365Config
        {
            TenantId = "12345678-1234-1234-1234-123456789012",
            ClientAppId = "87654321-4321-4321-4321-210987654321",
            AgentIdentityDisplayName = "Test Agent"
        });

        var root = new RootCommand();
        root.AddCommand(ValidateCommand.CreateCommand(_logger, _configService, requirementChecksOverride: [new AlwaysPassRequirementCheck()]));

        // Act
        await root.InvokeAsync("validate");

        // Assert
        File.Exists(_reportPath).Should().BeTrue(because: "report should be written on success");
        var report = JsonSerializer.Deserialize<ValidateReport>(await File.ReadAllTextAsync(_reportPath));
        report.Should().NotBeNull();
        report!.Summary.Ok.Should().BeTrue();
        report.Summary.Blocker.Should().BeNull();
    }

    [Fact]
    public async Task ValidateCommand_WithFailingCheck_ReturnsExitCode1()
    {
        // Arrange
        _configService.ConfigExistsAsync(Arg.Any<string>()).Returns(true);
        _configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(new Agent365Config
        {
            TenantId = "12345678-1234-1234-1234-123456789012",
            ClientAppId = "87654321-4321-4321-4321-210987654321",
            AgentIdentityDisplayName = "Test Agent"
        });

        var root = new RootCommand();
        root.AddCommand(ValidateCommand.CreateCommand(_logger, _configService, requirementChecksOverride: [new AlwaysFailRequirementCheck()]));

        // Act
        var exitCode = await root.InvokeAsync("validate");

        // Assert
        exitCode.Should().Be(1, because: "failing validation checks should return exit code 1");
    }

    [Fact]
    public async Task ValidateCommand_Report_HasSkippedUnimplementedTiers()
    {
        // Arrange
        _configService.ConfigExistsAsync(Arg.Any<string>()).Returns(true);
        _configService.LoadAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(new Agent365Config
        {
            TenantId = "12345678-1234-1234-1234-123456789012",
            ClientAppId = "87654321-4321-4321-4321-210987654321",
            AgentIdentityDisplayName = "Test Agent"
        });

        var root = new RootCommand();
        root.AddCommand(ValidateCommand.CreateCommand(_logger, _configService, requirementChecksOverride: [new AlwaysPassRequirementCheck()]));

        // Act
        await root.InvokeAsync("validate");

        // Assert
        var report = JsonSerializer.Deserialize<ValidateReport>(await File.ReadAllTextAsync(_reportPath));
        report!.Tiers.Conversation.Skipped.Should().BeTrue();
        report.Tiers.Telemetry.Skipped.Should().BeTrue();
        report.Tiers.Blueprint.Skipped.Should().BeTrue();
    }
}