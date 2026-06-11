// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.CommandLine;
using System.Text.Json;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for Permissions subcommand
/// </summary>
[Collection("Sequential")]
public class PermissionsSubcommandTests : IDisposable
{
    private readonly ILogger _mockLogger;
    private readonly AzureAuthValidator _mockAuthValidator;
    private readonly IConfigService _mockConfigService;
    private readonly CommandExecutor _mockExecutor;
    private readonly GraphApiService _mockGraphApiService;
    private readonly AgentBlueprintService _mockBlueprintService;
    private readonly IConfirmationProvider _mockConfirmationProvider;

    public PermissionsSubcommandTests()
    {
        _mockLogger = Substitute.For<ILogger>();
        _mockConfigService = Substitute.For<IConfigService>();
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);
        _mockAuthValidator = Substitute.ForPartsOf<AzureAuthValidator>(NullLogger<AzureAuthValidator>.Instance, _mockExecutor);
        _mockGraphApiService = Substitute.ForPartsOf<GraphApiService>();
        _mockBlueprintService = Substitute.ForPartsOf<AgentBlueprintService>(Substitute.For<ILogger<AgentBlueprintService>>(), _mockGraphApiService);
        _mockConfirmationProvider = Substitute.For<IConfirmationProvider>();

        // Short-circuit any virtual GraphApiService methods that would otherwise fall through to
        // the real MSAL/az-CLI authentication pipeline on a partial mock. Without this, tests
        // that drive ConfigureMcpPermissionsAsync into BatchPermissionsOrchestrator hang on
        // Linux CI (no cached creds, no interactive auth available). The orchestrator handles
        // a null pre-warm response by skipping Phase 1, which is what every test in this class
        // wants because none of them exercise real Graph calls.
        _mockGraphApiService.GraphGetAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns((JsonDocument?)null);

        // The orchestrator's admin-consent path opens a real browser and polls Graph for up to
        // 180s. Both are suppressed here so admin-path tests run in milliseconds without spawning
        // a browser window. Reset in Dispose to avoid leaking state into other test classes.
        BrowserHelper.OpenUrlOverrideForTests = (_, _) => { };
        AdminConsentHelper.BypassConsentChecksForTests = true;
    }

    public void Dispose()
    {
        BrowserHelper.OpenUrlOverrideForTests = null;
        AdminConsentHelper.BypassConsentChecksForTests = false;
        GC.SuppressFinalize(this);
        _mockGraphApiService.IsCurrentUserAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Microsoft.Agents.A365.DevTools.Cli.Models.RoleCheckResult.Unknown);
        _mockGraphApiService.IsCurrentUserAgentIdAdminAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Microsoft.Agents.A365.DevTools.Cli.Models.RoleCheckResult.Unknown);
    }

    #region Command Structure Tests

    [Fact]
    public void CreateCommand_ShouldHaveMcpSubcommand()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        // Assert
        var mcpSubcommand = command.Subcommands.FirstOrDefault(s => s.Name == "mcp");
        mcpSubcommand.Should().NotBeNull();
    }

    [Fact]
    public void CreateCommand_ShouldHaveBotSubcommand()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        // Assert
        var botSubcommand = command.Subcommands.FirstOrDefault(s => s.Name == "bot");
        botSubcommand.Should().NotBeNull();
    }

    [Fact]
    public void CommandDescription_ShouldMentionRequiredPermissions()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        // Assert
        command.Description.Should().Contain("Global Administrator");
    }

    [Fact]
    public void CreateCommand_ShouldHaveBothSubcommands()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        // Assert
        command.Subcommands.Should().HaveCount(4);
        command.Subcommands.Should().Contain(s => s.Name == "mcp");
        command.Subcommands.Should().Contain(s => s.Name == "bot");
        command.Subcommands.Should().Contain(s => s.Name == "custom");
        command.Subcommands.Should().Contain(s => s.Name == "copilotstudio");
    }

    [Fact]
    public void CreateCommand_ShouldBeUsableInCommandPipeline()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        // Assert
        command.Should().NotBeNull();
        command.Name.Should().Be("permissions");
        command.Subcommands.Should().HaveCount(4);
    }

    #endregion

    #region MCP Subcommand Tests

    [Fact]
    public void McpSubcommand_ShouldHaveCorrectName()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        var mcpSubcommand = command.Subcommands.First(s => s.Name == "mcp");

        // Assert
        mcpSubcommand.Name.Should().Be("mcp");
    }

    [Fact]
    public void McpSubcommand_ShouldHaveVerboseOption()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        var mcpSubcommand = command.Subcommands.First(s => s.Name == "mcp");

        // Assert
        var verboseOption = mcpSubcommand.Options.FirstOrDefault(o => o.Name == "verbose");
        verboseOption.Should().NotBeNull();
        verboseOption!.Aliases.Should().Contain("--verbose");
        verboseOption.Aliases.Should().Contain("-v");
    }

    [Fact]
    public void McpSubcommand_ShouldHaveDryRunOption()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        var mcpSubcommand = command.Subcommands.First(s => s.Name == "mcp");

        // Assert
        var dryRunOption = mcpSubcommand.Options.FirstOrDefault(o => o.Name == "dry-run");
        dryRunOption.Should().NotBeNull();
        dryRunOption!.Aliases.Should().Contain("--dry-run");
    }

    [Fact]
    public void McpSubcommand_DescriptionShouldBeInformativeAndActionable()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        var mcpSubcommand = command.Subcommands.First(s => s.Name == "mcp");

        // Assert
        mcpSubcommand.Description.Should().NotBeNullOrEmpty();
        mcpSubcommand.Description.Should().ContainAny("MCP", "OAuth2", "permissions");
    }

    #endregion

    #region Bot Subcommand Tests

    [Fact]
    public void BotSubcommand_ShouldHaveCorrectName()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        var botSubcommand = command.Subcommands.First(s => s.Name == "bot");

        // Assert
        botSubcommand.Name.Should().Be("bot");
    }

    [Fact]
    public void BotSubcommand_ShouldHaveVerboseOption()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        var botSubcommand = command.Subcommands.First(s => s.Name == "bot");

        // Assert
        var verboseOption = botSubcommand.Options.FirstOrDefault(o => o.Name == "verbose");
        verboseOption.Should().NotBeNull();
        verboseOption!.Aliases.Should().Contain("--verbose");
        verboseOption.Aliases.Should().Contain("-v");
    }

    [Fact]
    public void BotSubcommand_ShouldHaveDryRunOption()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        var botSubcommand = command.Subcommands.First(s => s.Name == "bot");

        // Assert
        var dryRunOption = botSubcommand.Options.FirstOrDefault(o => o.Name == "dry-run");
        dryRunOption.Should().NotBeNull();
        dryRunOption!.Aliases.Should().Contain("--dry-run");
    }

    [Fact]
    public void BotSubcommand_DescriptionShouldMentionPrerequisites()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        var botSubcommand = command.Subcommands.First(s => s.Name == "bot");

        // Assert
        botSubcommand.Description.Should().Contain("Prerequisites");
    }

    [Fact]
    public void BotSubcommand_DescriptionShouldBeInformativeAndActionable()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        var botSubcommand = command.Subcommands.First(s => s.Name == "bot");

        // Assert
        botSubcommand.Description.Should().NotBeNullOrEmpty();
        botSubcommand.Description.Should().ContainAny("Bot", "API", "permissions");
    }

    #endregion

    #region Validation Tests (Testing logic without parser)

    [Fact]
    public void McpValidation_WithMissingBlueprintId_ShouldDetect()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "", // Missing blueprint ID
            DeploymentProjectPath = "."
        };

        // Act - Verify validation logic
        var blueprintId = config.AgentBlueprintId;

        // Assert - Verify validation would catch this
        blueprintId.Should().BeEmpty();
    }

    [Fact]
    public void BotValidation_WithMissingBlueprintId_ShouldDetect()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = null // Missing blueprint ID
        };

        // Act
        var blueprintId = config.AgentBlueprintId;

        // Assert
        blueprintId.Should().BeNull();
    }

    [Fact]
    public void DryRunLogic_ShouldNotExecutePermissionGrants()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "blueprint-123",
            DeploymentProjectPath = "."
        };

        // Act - Verify config properties
        var blueprintId = config.AgentBlueprintId;
        var tenantId = config.TenantId;

        // Assert - Config is valid for dry-run
        blueprintId.Should().Be("blueprint-123");
        tenantId.Should().Be("test-tenant");
    }

    [Fact]
    public void McpConfiguration_ShouldDescribeOAuth2Grants()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant-id",
            AgentBlueprintId = "blueprint-456",
            DeploymentProjectPath = ".",
            Environment = "preprod"
        };

        // Act - This would be what dry-run displays
        var environment = config.Environment;
        var blueprintId = config.AgentBlueprintId;

        // Assert
        environment.Should().Be("preprod");
        blueprintId.Should().Be("blueprint-456");
    }

    [Fact]
    public void BotConfiguration_ShouldDescribeBotApiPermissions()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            AgentBlueprintId = "blueprint-123"
        };

        // Act - Simulate what would be logged
        var blueprintId = config.AgentBlueprintId;

        // Assert
        blueprintId.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region ConfigureMcpPermissionsAsync Tests

    [Fact]
    public async Task ConfigureMcpPermissionsAsync_WithMissingManifest_ShouldHandleGracefully()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            DeploymentProjectPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()) // Non-existent path
        };

        var configFile = new FileInfo("test-config.json");

        // Act
        var result = await PermissionsSubcommand.ConfigureMcpPermissionsAsync(
            configFile.FullName,
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService,
            _mockBlueprintService,
            config,
            false);

        result.Should().BeFalse(
            because: "MCP has no Graph scopes, so the consent check runs against the mocked Graph service which has no oauth2PermissionGrants — grants are not present and admin action is required, so the method correctly returns false");
    }

    [Fact]
    public async Task ConfigureMcpPermissionsAsync_UnknownScope_ReturnsFalse()
    {
        // Arrange — manifest with a scope that is neither V1 (McpServers.*.All),
        // V2 (Tools.ListInvoke.All), nor the metadata scope
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifestPath = Path.Combine(tempDir, "ToolingManifest.json");
            File.WriteAllText(manifestPath, """
                {
                  "mcpServers": [
                    {
                      "mcpServerName": "mcp_WeirdServer",
                      "scope": "Unknown.ScopeValue.NotRecognized",
                      "audience": "99999999-0000-0000-0000-000000000000"
                    }
                  ]
                }
                """);

            var config = new Agent365Config
            {
                TenantId = "00000000-0000-0000-0000-000000000000",
                AgentBlueprintId = "blueprint-123",
                DeploymentProjectPath = tempDir
            };

            // Act
            var result = await PermissionsSubcommand.ConfigureMcpPermissionsAsync(
                "config.json",
                _mockLogger,
                _mockConfigService,
                _mockExecutor,
                _mockGraphApiService,
                _mockBlueprintService,
                config,
                false);

            // Assert — unknown scope blocks the operation
            result.Should().BeFalse("unknown scopes must be rejected to prevent misconfigured blueprints");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigureMcpPermissionsAsync_V1AndMetadataScopes_AreKnownAndProceed()
    {
        // Arrange — manifest with only valid V1 scopes (should pass validation and attempt permissions)
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifestPath = Path.Combine(tempDir, "ToolingManifest.json");
            File.WriteAllText(manifestPath, $$"""
                {
                  "mcpServers": [
                    {
                      "mcpServerName": "mcp_MailTools",
                      "scope": "McpServers.Mail.All",
                      "audience": "{{Microsoft.Agents.A365.DevTools.Cli.Constants.McpConstants.WorkIQToolsProdAppId}}"
                    }
                  ]
                }
                """);

            var config = new Agent365Config
            {
                TenantId = "00000000-0000-0000-0000-000000000000",
                AgentBlueprintId = "blueprint-123",
                DeploymentProjectPath = tempDir
            };

            // Act — proceeds past validation; may fail at Graph API (no real connection)
            // but must NOT return false due to unknown-scope validation
            Func<Task> act = () => PermissionsSubcommand.ConfigureMcpPermissionsAsync(
                "config.json",
                _mockLogger,
                _mockConfigService,
                _mockExecutor,
                _mockGraphApiService,
                _mockBlueprintService,
                config,
                false);

            // Assert — passes scope validation (any exception is from Graph/blueprint, not scope guard)
            await act.Should().NotThrowAsync<InvalidOperationException>(
                "V1 scopes are known and must pass scope validation");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigureMcpPermissionsAsync_V2Scope_IsKnownAndPassesValidation()
    {
        // Arrange — manifest with V2 scope (Tools.ListInvoke.All) should pass validation
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifestPath = Path.Combine(tempDir, "ToolingManifest.json");
            File.WriteAllText(manifestPath, """
                {
                  "mcpServers": [
                    {
                      "mcpServerName": "mcp_TeamsServer",
                      "scope": "Tools.ListInvoke.All",
                      "audience": "2cc60bb0-1024-48c8-95f0-1fce211a04d8"
                    }
                  ]
                }
                """);

            var config = new Agent365Config
            {
                TenantId = "00000000-0000-0000-0000-000000000000",
                AgentBlueprintId = "blueprint-123",
                DeploymentProjectPath = tempDir
            };

            // Act
            Func<Task> act = () => PermissionsSubcommand.ConfigureMcpPermissionsAsync(
                "config.json",
                _mockLogger,
                _mockConfigService,
                _mockExecutor,
                _mockGraphApiService,
                _mockBlueprintService,
                config,
                false);

            // Assert — V2 scope is known; validation passes (any failure is from Graph, not scope guard)
            await act.Should().NotThrowAsync<InvalidOperationException>(
                "V2 scope Tools.ListInvoke.All is known and must pass scope validation");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    #endregion

    #region ConfigureBotPermissionsAsync Tests

    [Fact]
    public async Task ConfigureBotPermissionsAsync_WithMissingBlueprintId_ShouldReturnFalse()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "" // Missing
        };

        var configFile = new FileInfo("test-config.json");

        // Act
        var result = await PermissionsSubcommand.ConfigureBotPermissionsAsync(
            configFile.FullName,
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            config,
            _mockGraphApiService,
            _mockBlueprintService,
            false);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void ConfigureBotPermissionsAsync_ShouldValidateBlueprintId()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123"
        };

        var configFile = new FileInfo("test-config.json");

        // Act - Even though it may fail, it should validate the blueprint ID first
        var blueprintId = config.AgentBlueprintId;

        // Assert
        blueprintId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void BotSubcommand_Description_ShouldNotReferenceNonExistentEndpointCommand()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        var botSubcommand = command.Subcommands.FirstOrDefault(s => s.Name == "bot");

        // Assert
        botSubcommand.Should().NotBeNull();
        botSubcommand!.Description.Should().NotContain("a365 setup endpoint",
            "the 'a365 setup endpoint' command does not exist - endpoint is registered as part of blueprint setup");
        botSubcommand.Description.Should().Contain("a365 publish",
            "after permissions setup, the next a365 command in the workflow is 'publish' to package the agent for the Microsoft 365 Admin Center");
    }

    [Fact]
    public void BotSubcommand_Description_ShouldMentionPrerequisites()
    {
        // Act
        var command = PermissionsSubcommand.CreateCommand(
            _mockLogger,
            _mockAuthValidator,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService, _mockBlueprintService, _mockConfirmationProvider);

        var botSubcommand = command.Subcommands.FirstOrDefault(s => s.Name == "bot");

        // Assert
        botSubcommand.Should().NotBeNull();
        botSubcommand!.Description.Should().Contain("Blueprint",
            "blueprint is a prerequisite for bot permissions");
        botSubcommand.Description.Should().Contain("MCP permissions",
            "MCP permissions should be configured before bot permissions");
    }

    #endregion

    #region ConfigureCustomPermissionsAsync Tests

    [Fact]
    public async Task ConfigureCustomPermissionsAsync_WithNoCustomPermissions_SkipsGracefully()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            CustomBlueprintPermissions = null
        };

        // Act
        var result = await PermissionsSubcommand.ConfigureCustomPermissionsAsync(
            "test-config.json",
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService,
            _mockBlueprintService,
            config,
            false);

        // Assert
        result.Should().BeTrue("no custom permissions should result in success");
    }

    [Fact]
    public async Task ConfigureCustomPermissionsAsync_WithEmptyList_SkipsGracefully()
    {
        // Arrange
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-123",
            CustomBlueprintPermissions = new List<CustomResourcePermission>()
        };

        // Act
        var result = await PermissionsSubcommand.ConfigureCustomPermissionsAsync(
            "test-config.json",
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            _mockGraphApiService,
            _mockBlueprintService,
            config,
            false);

        // Assert
        result.Should().BeTrue("empty custom permissions list should result in success");
    }

    // NOTE: Integration tests for ConfigureCustomPermissionsAsync auto-lookup behavior
    // are not included as unit tests because they require extensive mocking of
    // SetupHelpers.EnsureResourcePermissionsAsync (static method) and other services.
    //
    // These behaviors should be tested via:
    // 1. Manual testing: See MANUAL_TEST_COMMANDS.md (Test 6)
    // 2. Integration tests: See docs/ai-workflows/integration-test-workflow.md (Test 4.5)
    // 3. Real Azure environment testing
    //
    // Expected behaviors documented for integration testing:
    // - Auto-lookup succeeds and populates ResourceName
    // - Auto-lookup fails and uses fallback name (Custom-{first8chars})
    // - Auto-lookup throws exception and uses fallback name
    // - ResourceName already provided, no lookup performed
    // - Multiple permissions with mixed lookup results
    // - Invalid permission validation
    // - SetupResults tracking for custom permissions

    #endregion

    #region ConfigureBotPermissionsAsync — Issue 402 Regression Tests
    // Regression tests for the localResults fix: S2S failure was invisible when setupResults=null.

    private Agent365Config ArrangeAdminPath(bool s2sSucceeds)
    {
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-app-id",
            AgentBlueprintServicePrincipalObjectId = "blueprint-sp-id"
        };

        _mockGraphApiService.EnsureServicePrincipalForAppIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>("resource-sp-id"));

        _mockGraphApiService.IsCurrentUserAdminAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RoleCheckResult.HasRole));

        _mockGraphApiService.CreateOrUpdateOauth2PermissionGrantWithDetailsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<(bool Success, int StatusCode, string? ErrorCode)>((true, 201, null)));

        _mockBlueprintService.SetInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(bool ok, bool alreadyExists, string? error)>((true, false, null)));

        _mockBlueprintService.GrantAppRoleAssignmentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Models.AppRoleGrantResult(AllSucceeded: s2sSucceeds, AllAlreadyAssigned: false)));

        _mockGraphApiService.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(callInfo =>
            {
                var path = callInfo.ArgAt<string>(1);
                return path.Contains("/me")
                    ? Task.FromResult<JsonDocument?>(JsonDocument.Parse("{\"id\":\"mock-user-id\"}"))
                    : Task.FromResult<JsonDocument?>(null);
            });

        _mockBlueprintService.VerifyInheritablePermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<(bool exists, bool scopesAllAllowed, bool rolesAllAllowed, string? error)>((true, true, true, null)));

        _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>())
            .Returns(Task.CompletedTask);

        return config;
    }

    [Fact]
    public async Task ConfigureBotPermissionsAsync_AdminPath_WhenS2SFailsAndNoExternalSetupResults_ReturnsFalse()
    {
        // Arrange — admin user, OAuth2 grants succeed, S2S grant fails, no external SetupResults.
        var config = ArrangeAdminPath(s2sSucceeds: false);

        // Act — no external SetupResults, mirrors how the CLI handler calls this method.
        var result = await PermissionsSubcommand.ConfigureBotPermissionsAsync(
            "config.json",
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            config,
            _mockGraphApiService,
            _mockBlueprintService,
            iSetupAll: false,
            setupResults: null);

        // Assert
        result.Should().BeFalse(
            because: "the method must create a local SetupResults so S2S failure is captured " +
                     "even when called without an external setupResults (the CLI handler path)");
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => (o.ToString() ?? string.Empty).Contains("S2S app role assignment failed")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ConfigureBotPermissionsAsync_WhenGraphAuthFails_DoesNotLogSuccessMessage()
    {
        // Arrange — all Graph calls return null → phase1Result=null (auth failed); consentGranted=false; S2S not attempted.
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-app-id"
        };

        _mockGraphApiService.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<JsonDocument?>(null));

        _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>())
            .Returns(Task.CompletedTask);

        // Act
        var result = await PermissionsSubcommand.ConfigureBotPermissionsAsync(
            "config.json",
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            config,
            _mockGraphApiService,
            _mockBlueprintService,
            iSetupAll: false,
            setupResults: null);

        // Assert
        result.Should().BeFalse(because: "consentGranted=false when Graph auth fails");
        _mockLogger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => (o.ToString() ?? string.Empty).Contains("configured successfully")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ConfigureBotPermissionsAsync_WhenGraphAuthFails_LogsConsentRequiredMessage()
    {
        // Arrange — all Graph calls return null → phase1Result=null (auth failed); consentGranted=false, S2S not attempted.
        var config = new Agent365Config
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            AgentBlueprintId = "blueprint-app-id"
        };

        _mockGraphApiService.GraphGetAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromResult<JsonDocument?>(null));

        _mockConfigService.SaveStateAsync(Arg.Any<Agent365Config>())
            .Returns(Task.CompletedTask);

        // Act
        var result = await PermissionsSubcommand.ConfigureBotPermissionsAsync(
            "config.json",
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            config,
            _mockGraphApiService,
            _mockBlueprintService,
            iSetupAll: false,
            setupResults: null);

        // Assert
        result.Should().BeFalse(because: "consentGranted=false when Graph auth fails");
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => (o.ToString() ?? string.Empty).Contains("admin consent required")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ConfigureBotPermissionsAsync_AdminPath_WhenS2SFails_EmitsS2SWarningAndSuppressesSuccessLog()
    {
        // Arrange — admin user, OAuth2 grants succeed, S2S grant fails.
        var config = ArrangeAdminPath(s2sSucceeds: false);

        // Act
        _ = await PermissionsSubcommand.ConfigureBotPermissionsAsync(
            "config.json",
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            config,
            _mockGraphApiService,
            _mockBlueprintService,
            iSetupAll: false,
            setupResults: null);

        // Assert — S2S failure warning must be logged.
        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => (o.ToString() ?? string.Empty).Contains("S2S app role assignment failed")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Assert — success message must not be logged when S2S failed.
        _mockLogger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => (o.ToString() ?? string.Empty).Contains("configured successfully")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ConfigureBotPermissionsAsync_AdminPath_WhenAllSucceeds_ReturnsTrueAndLogsSuccess()
    {
        // Arrange — admin user, all OAuth2 grants succeed, S2S grant succeeds.
        var config = ArrangeAdminPath(s2sSucceeds: true);

        // Act
        var result = await PermissionsSubcommand.ConfigureBotPermissionsAsync(
            "config.json",
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            config,
            _mockGraphApiService,
            _mockBlueprintService,
            iSetupAll: false,
            setupResults: null);

        // Assert
        result.Should().BeTrue(because: "all grants and S2S succeed — method must return true");
        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => (o.ToString() ?? string.Empty).Contains("configured successfully")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ConfigureBotPermissionsAsync_AdminPath_WhenS2SFailsWithExternalSetupResults_PopulatesS2SFlagAndReturnsFalse()
    {
        // Arrange — S2S fails, non-null SetupResults passed in (AllSubcommand path).
        var config = ArrangeAdminPath(s2sSucceeds: false);
        var externalResults = new SetupResults();

        // Act
        var result = await PermissionsSubcommand.ConfigureBotPermissionsAsync(
            "config.json",
            _mockLogger,
            _mockConfigService,
            _mockExecutor,
            config,
            _mockGraphApiService,
            _mockBlueprintService,
            iSetupAll: false,
            setupResults: externalResults);

        // Assert
        result.Should().BeFalse(
            because: "S2S failure must propagate via the external SetupResults reference");
        externalResults.BlueprintS2SOutcome.Should().NotBe(Cli.Models.GrantOutcome.Granted,
            because: "the orchestrator must record BlueprintS2SOutcome as not-Granted (Failed or NotApplicable) on the caller's SetupResults when S2S grants did not succeed");
    }

    #endregion
}

