// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class BotConfiguratorTests
{
    private readonly ILogger<BotConfigurator> _logger;
    private readonly CommandExecutor _executor;
    private readonly BotConfigurator _configurator;

    public BotConfiguratorTests()
    {
        _logger = Substitute.For<ILogger<BotConfigurator>>();
        _executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        _configurator = new BotConfigurator(_logger, _executor);
    }

    [Fact]
    public async Task EnsureBotServiceProviderAsync_ProviderAlreadyRegistered_ReturnsTrue()
    {
        // Arrange
        var subscriptionId = "test-subscription-id";
        var resourceGroup = "test-rg";
        var checkResult = new CommandResult { ExitCode = 0, StandardOutput = "Registered" };
        
        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("provider show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(checkResult));

        // Act
        var result = await _configurator.EnsureBotServiceProviderAsync(subscriptionId, resourceGroup);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task EnsureBotServiceProviderAsync_ProviderNotRegistered_RegistersAndReturnsTrue()
    {
        // Arrange
        var subscriptionId = "test-subscription-id";
        var resourceGroup = "test-rg";
        var checkResult = new CommandResult { ExitCode = 1, StandardOutput = "" };
        var registerResult = new CommandResult { ExitCode = 0, StandardOutput = "" };
        
        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("provider show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(checkResult));

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("provider register")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(registerResult));

        // Act
        var result = await _configurator.EnsureBotServiceProviderAsync(subscriptionId, resourceGroup);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task EnsureBotServiceProviderAsync_RegistrationFails_ReturnsFalse()
    {
        // Arrange
        var subscriptionId = "test-subscription-id";
        var resourceGroup = "test-rg";
        var checkResult = new CommandResult { ExitCode = 1, StandardOutput = "" };
        var registerResult = new CommandResult { ExitCode = 1, StandardError = "Registration failed" };
        
        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("provider show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(checkResult));

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("provider register")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(registerResult));

        // Act
        var result = await _configurator.EnsureBotServiceProviderAsync(subscriptionId, resourceGroup);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task GetManagedIdentityAsync_IdentityExists_ReturnsIdentityDetails()
    {
        // Arrange
        var identityName = "test-identity";
        var resourceGroup = "test-rg";
        var subscriptionId = "test-subscription-id";
        var location = "eastus";
        var identityJson = @"{
            ""clientId"": ""test-client-id"",
            ""tenantId"": ""test-tenant-id"",
            ""id"": ""/subscriptions/test-sub/resourcegroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity""
        }";
        var checkResult = new CommandResult { ExitCode = 0, StandardOutput = identityJson };
        
        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("identity show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(true), // suppressErrorLogging should be true
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(checkResult));

        // Act
        var result = await _configurator.GetManagedIdentityAsync(identityName, resourceGroup, subscriptionId, location);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("test-client-id", result.ClientId);
        Assert.Equal("test-tenant-id", result.TenantId);
        Assert.Contains("test-identity", result.ResourceId);
    }

    [Fact]
    public async Task GetManagedIdentityAsync_IdentityDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var identityName = "non-existent-identity";
        var resourceGroup = "test-rg";
        var subscriptionId = "test-subscription-id";
        var location = "eastus";
        var checkResult = new CommandResult { ExitCode = 3, StandardError = "ResourceNotFound" };
        
        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("identity show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(true), // suppressErrorLogging should be true
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(checkResult));

        // Act
        var result = await _configurator.GetManagedIdentityAsync(identityName, resourceGroup, subscriptionId, location);

        // Assert
        Assert.False(result.Success);
        Assert.Null(result.ClientId);
        Assert.Null(result.TenantId);
        Assert.Null(result.ResourceId);
    }

    [Fact]
    public async Task CreateOrUpdateBotAsync_IdentityDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var managedIdentityName = "non-existent-identity";
        var botName = "test-bot";
        var resourceGroup = "test-rg";
        var subscriptionId = "test-subscription-id";
        var location = "global";
        var messagingEndpoint = "https://test.azurewebsites.net/api/messages";
        var description = "Test Bot";
        var sku = "F0";
        
        var checkResult = new CommandResult { ExitCode = 3, StandardError = "ResourceNotFound" };
        
        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("identity show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(true),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(checkResult));

        // Act
        var result = await _configurator.CreateOrUpdateBotAsync(
            managedIdentityName, botName, resourceGroup, subscriptionId, 
            location, messagingEndpoint, description, sku);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task CreateOrUpdateBotAsync_BotExists_UpdatesBot()
    {
        // Arrange
        var managedIdentityName = "test-identity";
        var botName = "existing-bot";
        var resourceGroup = "test-rg";
        var subscriptionId = "test-subscription-id";
        var location = "global";
        var messagingEndpoint = "https://test.azurewebsites.net/api/messages";
        var description = "Test Bot";
        var sku = "F0";
        
        var identityJson = @"{
            ""clientId"": ""test-client-id"",
            ""tenantId"": ""test-tenant-id"",
            ""id"": ""/subscriptions/test-sub/resourcegroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity""
        }";
        
        var identityResult = new CommandResult { ExitCode = 0, StandardOutput = identityJson };
        var botCheckResult = new CommandResult { ExitCode = 0, StandardOutput = "/subscriptions/test/bot-id" };
        var updateResult = new CommandResult { ExitCode = 0, StandardOutput = "" };
        
        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("identity show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(true),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(identityResult));

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("bot show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(true),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(botCheckResult));

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("bot update")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(updateResult));

        // Act
        var result = await _configurator.CreateOrUpdateBotAsync(
            managedIdentityName, botName, resourceGroup, subscriptionId, 
            location, messagingEndpoint, description, sku);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CreateOrUpdateBotAsync_BotDoesNotExist_CreatesBot()
    {
        // Arrange
        var managedIdentityName = "test-identity";
        var botName = "new-bot";
        var resourceGroup = "test-rg";
        var subscriptionId = "test-subscription-id";
        var location = "global";
        var messagingEndpoint = "https://test.azurewebsites.net/api/messages";
        var description = "Test Bot";
        var sku = "F0";
        
        var identityJson = @"{
            ""clientId"": ""test-client-id"",
            ""tenantId"": ""test-tenant-id"",
            ""id"": ""/subscriptions/test-sub/resourcegroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity""
        }";
        
        var identityResult = new CommandResult { ExitCode = 0, StandardOutput = identityJson };
        var botCheckResult = new CommandResult { ExitCode = 3, StandardError = "ResourceNotFound" };
        var createResult = new CommandResult { ExitCode = 0, StandardOutput = "" };
        
        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("identity show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(true),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(identityResult));

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("bot show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(true),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(botCheckResult));

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("bot create")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createResult));

        // Act
        var result = await _configurator.CreateOrUpdateBotAsync(
            managedIdentityName, botName, resourceGroup, subscriptionId, 
            location, messagingEndpoint, description, sku);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ConfigureMsTeamsChannelAsync_ChannelExists_ReturnsTrue()
    {
        // Arrange
        var botName = "test-bot";
        var resourceGroup = "test-rg";
        var checkResult = new CommandResult { ExitCode = 0, StandardOutput = "channel info" };
        
        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("bot msteams show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(true),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(checkResult));

        // Act
        var result = await _configurator.ConfigureMsTeamsChannelAsync(botName, resourceGroup);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ConfigureMsTeamsChannelAsync_ChannelDoesNotExist_CreatesChannel()
    {
        // Arrange
        var botName = "test-bot";
        var resourceGroup = "test-rg";
        var checkResult = new CommandResult { ExitCode = 3, StandardError = "ResourceNotFound" };
        var createResult = new CommandResult { ExitCode = 0, StandardOutput = "" };
        
        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("bot msteams show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(true),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(checkResult));

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("bot msteams create")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createResult));

        // Act
        var result = await _configurator.ConfigureMsTeamsChannelAsync(botName, resourceGroup);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ConfigureMsTeamsChannelAsync_CreationFails_ReturnsFalse()
    {
        // Arrange
        var botName = "test-bot";
        var resourceGroup = "test-rg";
        var checkResult = new CommandResult { ExitCode = 3, StandardError = "ResourceNotFound" };
        var createResult = new CommandResult { ExitCode = 1, StandardError = "Creation failed" };
        
        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("bot msteams show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(true),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(checkResult));

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("bot msteams create")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createResult));

        // Act
        var result = await _configurator.ConfigureMsTeamsChannelAsync(botName, resourceGroup);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task CreateOrUpdateBotWithSystemIdentityAsync_IdentityDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var appServiceName = "test-app-service";
        var botName = "test-bot";
        var resourceGroupName = "test-resource-group";
        var subscriptionId = "test-subscription";
        var location = "westus2";
        var messagingEndpoint = "https://test.azurewebsites.net/api/messages";
        var description = "Test Bot Description";
        var sku = "F0";

        var identityCheckResult = new CommandResult { ExitCode = 1, StandardError = "Identity not found" };

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains($"webapp identity show --name {appServiceName} --resource-group {resourceGroupName}")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(identityCheckResult));

        // Act
        var result = await _configurator.CreateOrUpdateBotWithSystemIdentityAsync(
            appServiceName, botName, resourceGroupName, subscriptionId, location, messagingEndpoint, description, sku);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task CreateOrUpdateBotWithSystemIdentityAsync_BotCreationSucceeds_ReturnsTrue()
    {
        // Arrange
        var appServiceName = "test-app-service";
        var botName = "test-bot";
        var resourceGroupName = "test-resource-group";
        var subscriptionId = "test-subscription";
        var location = "westus2";
        var messagingEndpoint = "https://test.azurewebsites.net/api/messages";
        var description = "Test Bot Description";
        var sku = "F0";

        var identityResult = new CommandResult 
        { 
            ExitCode = 0, 
            StandardOutput = """
                {
                  "principalId": "test-principal-id",
                  "tenantId": "test-tenant-id"
                }
                """ 
        };

        var botCheckResult = new CommandResult { ExitCode = 1, StandardError = "Bot not found" };
        var botCreateResult = new CommandResult 
        { 
            ExitCode = 0, 
            StandardOutput = """{"name": "test-bot"}""" 
        };

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains($"webapp identity show --name {appServiceName} --resource-group {resourceGroupName}")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(identityResult));

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains($"bot show --resource-group {resourceGroupName} --name {botName} --subscription {subscriptionId}")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(true),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(botCheckResult));

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains($"bot create --resource-group {resourceGroupName} --name {botName}")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(botCreateResult));

        // Act
        var result = await _configurator.CreateOrUpdateBotWithSystemIdentityAsync(
            appServiceName, botName, resourceGroupName, subscriptionId, location, messagingEndpoint, description, sku);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CreateOrUpdateBotWithAgentBlueprintAsync_IdentityDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var appServiceName = "test-app-service";
        var botName = "test-bot";
        var resourceGroupName = "test-resource-group";
        var subscriptionId = "test-subscription";
        var location = "westus2";
        var messagingEndpoint = "https://test.azurewebsites.net/api/messages";
        var description = "Test Bot Description";
        var sku = "F0";
        var agentBlueprintId = "test-agent-blueprint-id";

        var subscriptionResult = new CommandResult { ExitCode = 1, StandardError = "Subscription not found" };

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("account show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(subscriptionResult));

        // Act
        var result = await _configurator.CreateOrUpdateBotWithAgentBlueprintAsync(
            appServiceName, botName, resourceGroupName, subscriptionId, location, messagingEndpoint, description, sku, agentBlueprintId);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task CreateOrUpdateBotWithAgentBlueprintAsync_BotCreationSucceeds_ReturnsTrue()
    {
        // Arrange
        var appServiceName = "test-app-service";
        var botName = "test-bot";
        var resourceGroupName = "test-resource-group";
        var subscriptionId = "test-subscription";
        var location = "westus2";
        var messagingEndpoint = "https://test.azurewebsites.net/api/messages";
        var description = "Test Bot Description";
        var sku = "F0";
        var agentBlueprintId = "test-agent-blueprint-id";

        var subscriptionResult = new CommandResult 
        { 
            ExitCode = 0, 
            StandardOutput = """
                {
                  "tenantId": "test-tenant-id"
                }
                """ 
        };

        var botCheckResult = new CommandResult { ExitCode = 1, StandardError = "Bot not found" };
        var botCreateResult = new CommandResult 
        { 
            ExitCode = 0, 
            StandardOutput = """{"name": "test-bot"}""" 
        };

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("account show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(subscriptionResult));

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains($"bot show --name {botName} --resource-group {resourceGroupName}")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(true), // suppressErrorLogging: true (bot doesn't exist is expected)
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(botCheckResult));

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains($"bot create --resource-group {resourceGroupName} --name {botName}")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(botCreateResult));

        // Act
        var result = await _configurator.CreateOrUpdateBotWithAgentBlueprintAsync(
            appServiceName, botName, resourceGroupName, subscriptionId, location, messagingEndpoint, description, sku, agentBlueprintId);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ConfigureChannelsAsync_TeamsOnlyEnabled_ConfiguresTeamsChannel()
    {
        // Arrange
        var botName = "test-bot";
        var resourceGroupName = "test-resource-group";
        var enableTeams = true;
        var enableEmail = false;
        var agentUserPrincipalName = "agent@test.com";

        var checkResult = new CommandResult { ExitCode = 3, StandardError = "ResourceNotFound" };
        var createResult = new CommandResult 
        { 
            ExitCode = 0, 
            StandardOutput = """{"channelName": "MsTeamsChannel"}""" 
        };

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("bot msteams show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(true),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(checkResult));

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("bot msteams create")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createResult));

        // Act
        var result = await _configurator.ConfigureChannelsAsync(botName, resourceGroupName, enableTeams, enableEmail, agentUserPrincipalName);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ConfigureAllChannelsAsync_ConfiguresAllChannels_ReturnsTrue()
    {
        // Arrange
        var botName = "test-bot";
        var resourceGroupName = "test-resource-group";
        var agentUserPrincipalName = "agent@test.com";

        var checkResult = new CommandResult { ExitCode = 3, StandardError = "ResourceNotFound" };
        var createResult = new CommandResult 
        { 
            ExitCode = 0, 
            StandardOutput = """{"channelName": "MsTeamsChannel"}""" 
        };

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("bot msteams show")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(true),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(checkResult));

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains("bot msteams create")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createResult));

        // Act
        var result = await _configurator.ConfigureAllChannelsAsync(botName, resourceGroupName, agentUserPrincipalName);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task TestBotConfigurationAsync_BotExists_ReturnsTrue()
    {
        // Arrange
        var botName = "test-bot";
        var resourceGroupName = "test-resource-group";

        var testResult = new CommandResult 
        { 
            ExitCode = 0, 
            StandardOutput = """
                {
                  "name": "test-bot",
                  "properties": {
                    "displayName": "Test Bot"
                  }
                }
                """ 
        };

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains($"bot show --resource-group {resourceGroupName} --name {botName} --query")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(testResult));

        // Act
        var result = await _configurator.TestBotConfigurationAsync(botName, resourceGroupName);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task TestBotConfigurationAsync_BotDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var botName = "test-bot";
        var resourceGroupName = "test-resource-group";

        var testResult = new CommandResult { ExitCode = 1, StandardError = "Bot not found" };

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains($"bot show --resource-group {resourceGroupName} --name {botName} --query")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(testResult));

        // Act
        var result = await _configurator.TestBotConfigurationAsync(botName, resourceGroupName);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task GetBotConfigurationAsync_BotExists_ReturnsBotConfiguration()
    {
        // Arrange
        var botName = "test-bot";
        var resourceGroupName = "test-resource-group";

        var botResult = new CommandResult 
        { 
            ExitCode = 0, 
            StandardOutput = """
                {
                  "name": "test-bot",
                  "properties": {
                    "displayName": "Test Bot",
                    "endpoint": "https://test.azurewebsites.net/api/messages",
                    "msaAppId": "test-app-id",
                    "msaAppType": "UserAssignedMSI",
                    "msaAppTenantId": "test-tenant-id",
                    "msaAppMSIResourceId": "/subscriptions/test/resourceGroups/test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity"
                  }
                }
                """ 
        };

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains($"bot show --resource-group {resourceGroupName} --name {botName} --output json")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(botResult));

        // Act
        var result = await _configurator.GetBotConfigurationAsync(resourceGroupName, botName);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test-bot", result.Name);
        Assert.Equal("Test Bot", result.Properties.DisplayName);
        Assert.Equal("https://test.azurewebsites.net/api/messages", result.Properties.Endpoint);
        Assert.Equal("test-app-id", result.Properties.MsaAppId);
    }

    [Fact]
    public async Task GetBotConfigurationAsync_BotDoesNotExist_ReturnsNull()
    {
        // Arrange
        var botName = "test-bot";
        var resourceGroupName = "test-resource-group";

        var botResult = new CommandResult { ExitCode = 1, StandardError = "Bot not found" };

        _executor.ExecuteAsync(
            Arg.Is("az"),
            Arg.Is<string>(s => s.Contains($"bot show --resource-group {resourceGroupName} --name {botName} --output json")),
            Arg.Any<string?>(),
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(botResult));

        // Act
        var result = await _configurator.GetBotConfigurationAsync(resourceGroupName, botName);

        // Assert
        Assert.Null(result);
    }
}
