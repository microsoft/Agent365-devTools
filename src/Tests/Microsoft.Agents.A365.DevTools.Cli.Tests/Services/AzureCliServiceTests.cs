// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class AzureCliServiceTests
{
    private readonly ILogger<AzureCliService> _logger;
    private readonly CommandExecutor _commandExecutor;
    private readonly AzureCliService _azureCliService;

    public AzureCliServiceTests()
    {
        _logger = Substitute.For<ILogger<AzureCliService>>();
        _commandExecutor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        _azureCliService = new AzureCliService(_commandExecutor, _logger);
    }

    [Fact]
    public async Task IsLoggedInAsync_WhenAzureCliReturnsSuccess_ReturnsTrue()
    {
        // Arrange
        var result = new CommandResult { ExitCode = 0, StandardOutput = "" };
        _commandExecutor.ExecuteAsync("az", "account show", suppressErrorLogging: true)
            .Returns(Task.FromResult(result));

        // Act
        var isLoggedIn = await _azureCliService.IsLoggedInAsync();

        // Assert
        isLoggedIn.Should().BeTrue();
    }

    [Fact]
    public async Task IsLoggedInAsync_WhenAzureCliFails_ReturnsFalse()
    {
        // Arrange
        var result = new CommandResult { ExitCode = 1, StandardError = "ERROR: Please run 'az login'" };
        _commandExecutor.ExecuteAsync("az", "account show", suppressErrorLogging: true)
            .Returns(Task.FromResult(result));

        // Act
        var isLoggedIn = await _azureCliService.IsLoggedInAsync();

        // Assert
        isLoggedIn.Should().BeFalse();
    }

    [Fact]
    public async Task IsLoggedInAsync_WhenExceptionThrown_ReturnsFalse()
    {
        // Arrange
        _commandExecutor.ExecuteAsync("az", "account show", suppressErrorLogging: true)
            .Returns(Task.FromException<CommandResult>(new Exception("Azure CLI not found")));

        // Act
        var isLoggedIn = await _azureCliService.IsLoggedInAsync();

        // Assert
        isLoggedIn.Should().BeFalse();
    }

    [Fact]
    public async Task GetCurrentAccountAsync_WhenSuccessful_ReturnsAccountInfo()
    {
        // Arrange
        var jsonOutput = """
            {
              "id": "12345678-1234-1234-1234-123456789abc",
              "name": "Test Subscription",
              "tenantId": "87654321-4321-4321-4321-cba987654321",
              "user": {
                "name": "test@example.com",
                "type": "user"
              },
              "state": "Enabled",
              "isDefault": true
            }
            """;
        var result = new CommandResult { ExitCode = 0, StandardOutput = jsonOutput };
        _commandExecutor.ExecuteAsync("az", "account show --output json")
            .Returns(Task.FromResult(result));

        // Act
        var accountInfo = await _azureCliService.GetCurrentAccountAsync();

        // Assert
        accountInfo.Should().NotBeNull();
        accountInfo!.Id.Should().Be("12345678-1234-1234-1234-123456789abc");
        accountInfo.Name.Should().Be("Test Subscription");
        accountInfo.TenantId.Should().Be("87654321-4321-4321-4321-cba987654321");
        accountInfo.User.Name.Should().Be("test@example.com");
        accountInfo.User.Type.Should().Be("user");
        accountInfo.State.Should().Be("Enabled");
        accountInfo.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task GetCurrentAccountAsync_WhenAzureCliFails_ReturnsNull()
    {
        // Arrange
        var result = new CommandResult { ExitCode = 1, StandardError = "ERROR: Please run 'az login'" };
        _commandExecutor.ExecuteAsync("az", "account show --output json")
            .Returns(Task.FromResult(result));

        // Act
        var accountInfo = await _azureCliService.GetCurrentAccountAsync();

        // Assert
        accountInfo.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentAccountAsync_WhenJsonInvalid_ReturnsNull()
    {
        // Arrange
        var result = new CommandResult { ExitCode = 0, StandardOutput = "invalid json" };
        _commandExecutor.ExecuteAsync("az", "account show --output json")
            .Returns(Task.FromResult(result));

        // Act
        var accountInfo = await _azureCliService.GetCurrentAccountAsync();

        // Assert
        accountInfo.Should().BeNull();
    }

    [Fact]
    public async Task ListResourceGroupsAsync_WhenSuccessful_ReturnsResourceGroups()
    {
        // Arrange
        var jsonOutput = """
            [
              {
                "name": "rg-test-001",
                "location": "eastus",
                "id": "/subscriptions/12345678-1234-1234-1234-123456789abc/resourceGroups/rg-test-001"
              },
              {
                "name": "rg-test-002",
                "location": "westus",
                "id": "/subscriptions/12345678-1234-1234-1234-123456789abc/resourceGroups/rg-test-002"
              }
            ]
            """;
        var result = new CommandResult { ExitCode = 0, StandardOutput = jsonOutput };
        _commandExecutor.ExecuteAsync("az", "group list --output json")
            .Returns(Task.FromResult(result));

        // Act
        var resourceGroups = await _azureCliService.ListResourceGroupsAsync();

        // Assert
        resourceGroups.Should().HaveCount(2);
        resourceGroups[0].Name.Should().Be("rg-test-001");
        resourceGroups[0].Location.Should().Be("eastus");
        resourceGroups[1].Name.Should().Be("rg-test-002");
        resourceGroups[1].Location.Should().Be("westus");
    }

    [Fact]
    public async Task ListResourceGroupsAsync_WhenNoResourceGroups_ReturnsEmptyList()
    {
        // Arrange
        var result = new CommandResult { ExitCode = 0, StandardOutput = "[]" };
        _commandExecutor.ExecuteAsync("az", "group list --output json")
            .Returns(Task.FromResult(result));

        // Act
        var resourceGroups = await _azureCliService.ListResourceGroupsAsync();

        // Assert
        resourceGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task ListResourceGroupsAsync_WhenAzureCliFails_ReturnsEmptyList()
    {
        // Arrange
        var result = new CommandResult { ExitCode = 1, StandardError = "ERROR: Failed to list resource groups" };
        _commandExecutor.ExecuteAsync("az", "group list --output json")
            .Returns(Task.FromResult(result));

        // Act
        var resourceGroups = await _azureCliService.ListResourceGroupsAsync();

        // Assert
        resourceGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAppServicePlansAsync_WhenSuccessful_ReturnsAppServicePlans()
    {
        // Arrange
        var jsonOutput = """
            [
              {
                "name": "asp-test-001",
                "resourceGroup": "rg-test-001",
                "location": "eastus",
                "sku": {
                  "name": "B1"
                },
                "id": "/subscriptions/12345678-1234-1234-1234-123456789abc/resourceGroups/rg-test-001/providers/Microsoft.Web/serverfarms/asp-test-001"
              }
            ]
            """;
        var result = new CommandResult { ExitCode = 0, StandardOutput = jsonOutput };
        _commandExecutor.ExecuteAsync("az", "appservice plan list --output json")
            .Returns(Task.FromResult(result));

        // Act
        var appServicePlans = await _azureCliService.ListAppServicePlansAsync();

        // Assert
        appServicePlans.Should().HaveCount(1);
        appServicePlans[0].Name.Should().Be("asp-test-001");
        appServicePlans[0].ResourceGroup.Should().Be("rg-test-001");
        appServicePlans[0].Sku.Should().Be("B1");
    }

    [Fact]
    public async Task ListLocationsAsync_WhenSuccessful_ReturnsLocations()
    {
        // Arrange
        var jsonOutput = """
            [
              {
                "name": "eastus",
                "displayName": "East US",
                "regionalDisplayName": "(US) East US"
              }
            ]
            """;
        var result = new CommandResult { ExitCode = 0, StandardOutput = jsonOutput };
        _commandExecutor.ExecuteAsync("az", "account list-locations --output json")
            .Returns(Task.FromResult(result));

        // Act
        var locations = await _azureCliService.ListLocationsAsync();

        // Assert
        locations.Should().HaveCount(1);
        locations[0].Name.Should().Be("eastus");
        locations[0].DisplayName.Should().Be("East US");
        locations[0].RegionalDisplayName.Should().Be("(US) East US");
    }

    [Fact]
    public async Task ListLocationsAsync_WhenRegionalDisplayNameMissing_HandlesGracefully()
    {
        // Arrange
        var jsonOutput = """
            [
              {
                "name": "eastus",
                "displayName": "East US"
              }
            ]
            """;
        var result = new CommandResult { ExitCode = 0, StandardOutput = jsonOutput };
        _commandExecutor.ExecuteAsync("az", "account list-locations --output json")
            .Returns(Task.FromResult(result));

        // Act
        var locations = await _azureCliService.ListLocationsAsync();

        // Assert
        locations.Should().HaveCount(1);
        locations[0].RegionalDisplayName.Should().BeEmpty();
    }
}
