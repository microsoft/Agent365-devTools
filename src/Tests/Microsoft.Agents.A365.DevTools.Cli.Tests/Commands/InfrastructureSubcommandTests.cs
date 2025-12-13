// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class InfrastructureSubcommandTests
{
    private readonly ILogger _logger;
    private readonly CommandExecutor _commandExecutor;

    public InfrastructureSubcommandTests()
    {
        _logger = Substitute.For<ILogger>();
        _commandExecutor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
    }

    [Fact]
    public async Task EnsureAppServicePlanExists_WhenQuotaLimitExceeded_ThrowsInvalidOperationException()
    {
        // Arrange
        var subscriptionId = "test-sub-id";
        var resourceGroup = "test-rg";
        var planName = "test-plan";
        var planSku = "B1";

        // Mock app service plan doesn't exist (initial check)
        _commandExecutor.ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan show") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true)
            .Returns(new CommandResult { ExitCode = 1, StandardError = "Plan not found" });

        // Mock app service plan creation fails with quota error
        _commandExecutor.ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan create") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true)
            .Returns(new CommandResult
            {
                ExitCode = 1,
                StandardError = "ERROR: Operation cannot be completed without additional quota.\n\nAdditional details - Location:\n\nCurrent Limit (Basic VMs): 0\n\nCurrent Usage: 0\n\nAmount required for this deployment (Basic VMs): 1"
            });

        // Act & Assert - The method should throw immediately because creation fails
        var exception = await Assert.ThrowsAsync<AzureAppServicePlanException>(
            async () => await InfrastructureSubcommand.EnsureAppServicePlanExistsAsync(
                _commandExecutor, _logger, resourceGroup, planName, planSku, "eastus", subscriptionId,
                maxRetries: 2, baseDelaySeconds: 1));

        exception.ErrorType.Should().Be(AppServicePlanErrorType.QuotaExceeded);
        exception.PlanName.Should().Be(planName);
    }

    [Fact]
    public async Task EnsureAppServicePlanExists_WhenPlanAlreadyExists_SkipsCreation()
    {
        // Arrange
        var subscriptionId = "test-sub-id";
        var resourceGroup = "test-rg";
        var planName = "existing-plan";
        var planSku = "B1";

        // Mock app service plan already exists
        _commandExecutor.ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan show") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true)
            .Returns(new CommandResult
            {
                ExitCode = 0,
                StandardOutput = "{\"name\": \"existing-plan\", \"sku\": {\"name\": \"B1\"}}"
            });

        // Act
        await InfrastructureSubcommand.EnsureAppServicePlanExistsAsync(
            _commandExecutor, _logger, resourceGroup, planName, planSku, "eastus", subscriptionId,
            maxRetries: 2, baseDelaySeconds: 1);

        // Assert - Verify creation command was never called
        await _commandExecutor.DidNotReceive().ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan create")),
            captureOutput: true,
            suppressErrorLogging: true);
    }

    [Fact]
    public async Task EnsureAppServicePlanExists_WhenCreationSucceeds_VerifiesExistence()
    {
        // Arrange
        var subscriptionId = "test-sub-id";
        var resourceGroup = "test-rg";
        var planName = "new-plan";
        var planSku = "B1";

        // Mock app service plan doesn't exist initially, then exists after creation
        var planShowCallCount = 0;
        _commandExecutor.ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan show") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true)
            .Returns(callInfo =>
            {
                planShowCallCount++;
                // First call: plan doesn't exist, second call (after creation): plan exists
                return planShowCallCount == 1
                    ? new CommandResult { ExitCode = 1, StandardError = "Plan not found" }
                    : new CommandResult { ExitCode = 0, StandardOutput = "{\"name\": \"new-plan\"}" };
            });

        // Mock app service plan creation succeeds
        _commandExecutor.ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan create") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true)
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = "Plan created" });

        // Act
        await InfrastructureSubcommand.EnsureAppServicePlanExistsAsync(
            _commandExecutor, _logger, resourceGroup, planName, planSku, "eastus", subscriptionId,
            maxRetries: 2, baseDelaySeconds: 1);

        // Assert - Verify the plan creation was called
        await _commandExecutor.Received(1).ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan create") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true);

        // Verify the plan was checked twice (before creation and verification after)
        await _commandExecutor.Received(2).ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan show") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true);
    }

    [Fact]
    public async Task EnsureAppServicePlanExists_WhenCreationFailsSilently_ThrowsInvalidOperationException()
    {
        // Arrange - Tests the scenario where Azure CLI returns success but the plan doesn't actually exist
        var subscriptionId = "test-sub-id";
        var resourceGroup = "test-rg";
        var planName = "failed-plan";
        var planSku = "B1";

        // Mock app service plan doesn't exist before and after creation attempt
        _commandExecutor.ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan show") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true)
            .Returns(new CommandResult { ExitCode = 1, StandardError = "Plan not found" });

        // Mock plan creation appears to succeed but doesn't actually create the plan
        _commandExecutor.ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan create") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true)
            .Returns(new CommandResult { ExitCode = 0, StandardOutput = "" });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<AzureAppServicePlanException>(
            async () => await InfrastructureSubcommand.EnsureAppServicePlanExistsAsync(
                _commandExecutor, _logger, resourceGroup, planName, planSku, "eastus", subscriptionId,
                maxRetries: 2, baseDelaySeconds: 1));

        exception.ErrorType.Should().Be(AppServicePlanErrorType.VerificationTimeout);
        exception.PlanName.Should().Be(planName);
    }

    [Fact]
    public async Task EnsureAppServicePlanExists_WhenPermissionDenied_ThrowsInvalidOperationException()
    {
        // Arrange
        var subscriptionId = "test-sub-id";
        var resourceGroup = "test-rg";
        var planName = "test-plan";
        var planSku = "B1";

        // Mock app service plan doesn't exist
        _commandExecutor.ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan show") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true)
            .Returns(new CommandResult { ExitCode = 1, StandardError = "Plan not found" });

        // Mock app service plan creation fails with permission error
        _commandExecutor.ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan create") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true)
            .Returns(new CommandResult
            {
                ExitCode = 1,
                StandardError = "ERROR: The client does not have authorization to perform action"
            });

        // Act & Assert - The method should throw immediately because creation fails
        var exception = await Assert.ThrowsAsync<AzureAppServicePlanException>(
            async () => await InfrastructureSubcommand.EnsureAppServicePlanExistsAsync(
                _commandExecutor, _logger, resourceGroup, planName, planSku, "eastus", subscriptionId,
                maxRetries: 2, baseDelaySeconds: 1));

        exception.ErrorType.Should().Be(AppServicePlanErrorType.AuthorizationFailed);
        exception.PlanName.Should().Be(planName);
    }

    [Fact]
    public async Task EnsureAppServicePlanExists_WithRetry_WhenPlanPropagatesSlowly_EventuallySucceeds()
    {
        // Arrange
        var subscriptionId = "test-sub-id";
        var resourceGroup = "test-rg";
        var planName = "slow-plan";
        var planSku = "B1";

        // Mock app service plan doesn't exist initially
        _commandExecutor.ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan show") && s.Contains(planName) && !s.Contains("create")),
            captureOutput: true,
            suppressErrorLogging: true)
            .Returns(
                new CommandResult { ExitCode = 1, StandardError = "Plan not found" },
                new CommandResult { ExitCode = 1, StandardError = "Plan not found" },
                new CommandResult { ExitCode = 0, StandardOutput = "{\"name\": \"slow-plan\"}" });

        // Mock app service plan creation succeeds
        _commandExecutor.ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan create") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true)
            .Returns(new CommandResult { ExitCode = 0 });

        // Act
        await InfrastructureSubcommand.EnsureAppServicePlanExistsAsync(
            _commandExecutor, _logger, resourceGroup, planName, planSku, "eastus", subscriptionId,
            maxRetries: 2, baseDelaySeconds: 1);

        // Assert - Verify show was called multiple times (initial check + retries)
        await _commandExecutor.Received(3).ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan show") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true);
    }

    [Fact]
    public async Task EnsureAppServicePlanExists_WithRetry_WhenPlanNeverAppears_ThrowsAfterRetries()
    {
        // Arrange
        var subscriptionId = "test-sub-id";
        var resourceGroup = "test-rg";
        var planName = "missing-plan";
        var planSku = "B1";

        // Mock app service plan never appears even after creation
        _commandExecutor.ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan show") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true)
            .Returns(new CommandResult { ExitCode = 1, StandardError = "Plan not found" });

        // Mock app service plan creation succeeds
        _commandExecutor.ExecuteAsync("az",
            Arg.Is<string>(s => s.Contains("appservice plan create") && s.Contains(planName)),
            captureOutput: true,
            suppressErrorLogging: true)
            .Returns(new CommandResult { ExitCode = 0 });

        // Act & Assert - Use minimal retries for test performance
        var exception = await Assert.ThrowsAsync<AzureAppServicePlanException>(
            async () => await InfrastructureSubcommand.EnsureAppServicePlanExistsAsync(
                _commandExecutor, 
                _logger, 
                resourceGroup, 
                planName, 
                planSku, 
                "eastus",
                subscriptionId,
                maxRetries: 2,
                baseDelaySeconds: 1));

        exception.ErrorType.Should().Be(AppServicePlanErrorType.VerificationTimeout);
        exception.PlanName.Should().Be(planName);
    }
}
