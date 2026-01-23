// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
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

    [Fact]
    public async Task CreateInfrastructureAsync_WhenUserIdAvailable_AssignsWebsiteContributorRole()
    {
        // Arrange
        var subscriptionId = "test-sub-id";
        var tenantId = "test-tenant-id";
        var resourceGroup = "test-rg";
        var location = "eastus";
        var planName = "test-plan";
        var webAppName = "test-webapp";
        var generatedConfigPath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.json");
        var deploymentProjectPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
        var logger = Substitute.For<ILogger>();
        
        try
        {
            // Create temporary project directory
            Directory.CreateDirectory(deploymentProjectPath);
            
            // Setup mock CommandExecutor to return success for all operations
            _commandExecutor.ExecuteAsync("az", Arg.Any<string>(), captureOutput: true, suppressErrorLogging: Arg.Any<bool>())
                .Returns(callInfo =>
                {
                    var args = callInfo.ArgAt<string>(1);

                    // Resource group exists check
                    if (args.Contains("group exists"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "true" };

                    // App service plan show
                    if (args.Contains("appservice plan show"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"name\": \"test-plan\"}" };

                    // Web app show - succeeds after creation to avoid retry timeout
                    if (args.Contains("webapp show"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"name\": \"test-webapp\", \"state\": \"Running\"}" };

                    // Web app create
                    if (args.Contains("webapp create"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"name\": \"test-webapp\"}" };

                    // Managed identity assign
                    if (args.Contains("webapp identity assign"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"principalId\": \"test-principal-id\"}" };

                    // MSI verification
                    if (args.Contains("ad sp show"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"id\": \"test-principal-id\"}" };

                    // Get current user object ID - Use valid GUID format
                    if (args.Contains("ad signed-in-user show"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "12345678-1234-1234-1234-123456789abc" };

                    // Role assignment create
                    if (args.Contains("role assignment create"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"id\": \"test-role-assignment-id\"}" };

                    // Role assignment verification
                    if (args.Contains("role assignment list"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "Website Contributor" };

                    return new CommandResult { ExitCode = 0 };
                });

            // Act
            (string? principalId, bool anyAlreadyExisted) = await InfrastructureSubcommand.CreateInfrastructureAsync(
                _commandExecutor,
                subscriptionId,
                tenantId,
                resourceGroup,
                location,
                planName,
                "B1",
                webAppName,
                generatedConfigPath,
                deploymentProjectPath,
                ProjectPlatform.DotNet,
                logger,
                needDeployment: true,
                skipInfra: false,
                externalHosting: false,
                CancellationToken.None);

            // Assert - Verify role assignment command was called
            await _commandExecutor.Received().ExecuteAsync("az",
                Arg.Is<string>(s =>
                    s.Contains("role assignment create") &&
                    s.Contains("Website Contributor") &&
                    s.Contains("12345678-1234-1234-1234-123456789abc")),
                captureOutput: true,
                suppressErrorLogging: true);

            // Assert - Verify role assignment verification was called
            await _commandExecutor.Received().ExecuteAsync("az",
                Arg.Is<string>(s =>
                    s.Contains("role assignment list") &&
                    s.Contains("Website Contributor") &&
                    s.Contains("12345678-1234-1234-1234-123456789abc")),
                captureOutput: true,
                suppressErrorLogging: true);
        }
        finally
        {
            // Cleanup
            if (File.Exists(generatedConfigPath))
                File.Delete(generatedConfigPath);
            if (Directory.Exists(deploymentProjectPath))
                Directory.Delete(deploymentProjectPath, true);
        }
    }

    [Fact]
    public async Task CreateInfrastructureAsync_WhenUserIdUnavailable_ContinuesWithoutRoleAssignment()
    {
        // Arrange
        var subscriptionId = "test-sub-id";
        var tenantId = "test-tenant-id";
        var resourceGroup = "test-rg";
        var location = "eastus";
        var planName = "test-plan";
        var webAppName = "test-webapp";
        var generatedConfigPath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.json");
        var deploymentProjectPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
        var logger = Substitute.For<ILogger>();
        
        try
        {
            // Create temporary project directory
            Directory.CreateDirectory(deploymentProjectPath);
            
            // Setup mock CommandExecutor
            _commandExecutor.ExecuteAsync("az", Arg.Any<string>(), captureOutput: true, suppressErrorLogging: Arg.Any<bool>())
                .Returns(callInfo =>
                {
                    var args = callInfo.ArgAt<string>(1);

                    // Resource group exists check
                    if (args.Contains("group exists"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "true" };

                    // App service plan show
                    if (args.Contains("appservice plan show"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"name\": \"test-plan\"}" };

                    // Web app show - succeeds after creation to avoid retry timeout
                    if (args.Contains("webapp show"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"name\": \"test-webapp\", \"state\": \"Running\"}" };

                    // Web app create
                    if (args.Contains("webapp create"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"name\": \"test-webapp\"}" };

                    // Managed identity assign
                    if (args.Contains("webapp identity assign"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"principalId\": \"test-principal-id\"}" };

                    // MSI verification
                    if (args.Contains("ad sp show"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"id\": \"test-principal-id\"}" };

                    // Get current user object ID - fails (service principal scenario)
                    if (args.Contains("ad signed-in-user show"))
                        return new CommandResult { ExitCode = 1, StandardError = "Not logged in as user" };

                    return new CommandResult { ExitCode = 0 };
                });

            // Act - Should not throw, just log a debug message
            (string? principalId, bool anyAlreadyExisted) = await InfrastructureSubcommand.CreateInfrastructureAsync(
                _commandExecutor,
                subscriptionId,
                tenantId,
                resourceGroup,
                location,
                planName,
                "B1",
                webAppName,
                generatedConfigPath,
                deploymentProjectPath,
                ProjectPlatform.DotNet,
                logger,
                needDeployment: true,
                skipInfra: false,
                externalHosting: false,
                CancellationToken.None);

            // Assert - Principal ID should still be set, role assignment just skipped
            principalId.Should().Be("test-principal-id");
            
            // Verify role assignment was NOT attempted (since user ID retrieval failed)
            await _commandExecutor.DidNotReceive().ExecuteAsync("az",
                Arg.Is<string>(s => s.Contains("role assignment create")),
                captureOutput: true,
                suppressErrorLogging: true);
        }
        finally
        {
            // Cleanup
            if (File.Exists(generatedConfigPath))
                File.Delete(generatedConfigPath);
            if (Directory.Exists(deploymentProjectPath))
                Directory.Delete(deploymentProjectPath, true);
        }
    }

    [Fact]
    public async Task CreateInfrastructureAsync_WhenRoleAssignmentFails_ContinuesWithWarning()
    {
        // Arrange
        var subscriptionId = "test-sub-id";
        var tenantId = "test-tenant-id";
        var resourceGroup = "test-rg";
        var location = "eastus";
        var planName = "test-plan";
        var webAppName = "test-webapp";
        var generatedConfigPath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.json");
        var deploymentProjectPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
        var logger = Substitute.For<ILogger>();
        
        try
        {
            // Create temporary project directory
            Directory.CreateDirectory(deploymentProjectPath);
            
            // Setup mock CommandExecutor
            _commandExecutor.ExecuteAsync("az", Arg.Any<string>(), captureOutput: true, suppressErrorLogging: Arg.Any<bool>())
                .Returns(callInfo =>
                {
                    var args = callInfo.ArgAt<string>(1);
                    
                    // Resource group exists check
                    if (args.Contains("group exists"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "true" };
                    
                    // App service plan show
                    if (args.Contains("appservice plan show"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"name\": \"test-plan\"}" };

                    // Web app show - doesn't exist initially, then exists after creation
                    if (args.Contains("webapp show"))
                    {
                        // Return success after creation to avoid retry timeout
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"name\": \"test-webapp\", \"state\": \"Running\"}" };
                    }

                    // Web app create
                    if (args.Contains("webapp create"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"name\": \"test-webapp\"}" };

                    // Managed identity assign
                    if (args.Contains("webapp identity assign"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"principalId\": \"test-principal-id\"}" };

                    // MSI verification
                    if (args.Contains("ad sp show"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"id\": \"test-principal-id\"}" };

                    // Get current user object ID - Use valid GUID format
                    if (args.Contains("ad signed-in-user show"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "12345678-1234-1234-1234-123456789abc" };

                    // Role assignment - fails with permission error
                    if (args.Contains("role assignment create"))
                        return new CommandResult { ExitCode = 1, StandardError = "Insufficient permissions" };

                    // Role assignment verification - succeeds but returns empty (no role found)
                    if (args.Contains("role assignment list"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "" };

                    return new CommandResult { ExitCode = 0 };
                });

            // Act - Should not throw, just log a warning
            (string? principalId, bool anyAlreadyExisted) = await InfrastructureSubcommand.CreateInfrastructureAsync(
                _commandExecutor,
                subscriptionId,
                tenantId,
                resourceGroup,
                location,
                planName,
                "B1",
                webAppName,
                generatedConfigPath,
                deploymentProjectPath,
                ProjectPlatform.DotNet,
                logger,
                needDeployment: true,
                skipInfra: false,
                externalHosting: false,
                CancellationToken.None);

            // Assert - Principal ID should still be set, warning logged
            principalId.Should().Be("test-principal-id");
            
            // Verify warning was logged for assignment failure
            logger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Could not assign Website Contributor role")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());

            // Verify warning was logged for verification failure
            logger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Could not verify Website Contributor role")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            // Cleanup
            if (File.Exists(generatedConfigPath))
                File.Delete(generatedConfigPath);
            if (Directory.Exists(deploymentProjectPath))
                Directory.Delete(deploymentProjectPath, true);
        }
    }

    [Fact]
    public async Task CreateInfrastructureAsync_WhenRoleAlreadyExists_VerifiesSuccessfully()
    {
        // Arrange
        var subscriptionId = "test-sub-id";
        var tenantId = "test-tenant-id";
        var resourceGroup = "test-rg";
        var location = "eastus";
        var planName = "test-plan";
        var webAppName = "test-webapp";
        var generatedConfigPath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.json");
        var deploymentProjectPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
        var logger = Substitute.For<ILogger>();

        try
        {
            // Create temporary project directory
            Directory.CreateDirectory(deploymentProjectPath);

            // Setup mock CommandExecutor
            _commandExecutor.ExecuteAsync("az", Arg.Any<string>(), captureOutput: true, suppressErrorLogging: Arg.Any<bool>())
                .Returns(callInfo =>
                {
                    var args = callInfo.ArgAt<string>(1);

                    // Resource group exists check
                    if (args.Contains("group exists"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "true" };

                    // App service plan show
                    if (args.Contains("appservice plan show"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"name\": \"test-plan\"}" };

                    // Web app show - succeeds after creation to avoid retry timeout
                    if (args.Contains("webapp show"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"name\": \"test-webapp\", \"state\": \"Running\"}" };

                    // Web app create
                    if (args.Contains("webapp create"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"name\": \"test-webapp\"}" };

                    // Managed identity assign
                    if (args.Contains("webapp identity assign"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"principalId\": \"test-principal-id\"}" };

                    // MSI verification
                    if (args.Contains("ad sp show"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "{\"id\": \"test-principal-id\"}" };

                    // Get current user object ID - Use valid GUID format
                    if (args.Contains("ad signed-in-user show"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "12345678-1234-1234-1234-123456789abc" };

                    // Role assignment - already exists
                    if (args.Contains("role assignment create"))
                        return new CommandResult { ExitCode = 1, StandardError = "Role assignment already exists for this principal" };

                    // Role assignment verification - succeeds because it already exists
                    if (args.Contains("role assignment list"))
                        return new CommandResult { ExitCode = 0, StandardOutput = "Website Contributor" };

                    return new CommandResult { ExitCode = 0 };
                });

            // Act
            (string? principalId, bool anyAlreadyExisted) = await InfrastructureSubcommand.CreateInfrastructureAsync(
                _commandExecutor,
                subscriptionId,
                tenantId,
                resourceGroup,
                location,
                planName,
                "B1",
                webAppName,
                generatedConfigPath,
                deploymentProjectPath,
                ProjectPlatform.DotNet,
                logger,
                needDeployment: true,
                skipInfra: false,
                externalHosting: false,
                CancellationToken.None);

            // Assert - Principal ID should be set
            principalId.Should().Be("test-principal-id");

            // Verify role assignment verification was called
            await _commandExecutor.Received().ExecuteAsync("az",
                Arg.Is<string>(s =>
                    s.Contains("role assignment list") &&
                    s.Contains("Website Contributor") &&
                    s.Contains("12345678-1234-1234-1234-123456789abc")),
                captureOutput: true,
                suppressErrorLogging: true);

            // Verify success confirmation was logged
            logger.Received().Log(
                LogLevel.Information,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Current user is confirmed as Website Contributor")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            // Cleanup
            if (File.Exists(generatedConfigPath))
                File.Delete(generatedConfigPath);
            if (Directory.Exists(deploymentProjectPath))
                Directory.Delete(deploymentProjectPath, true);
        }
    }
}
