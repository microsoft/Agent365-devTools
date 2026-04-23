// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Unit tests for AllSubcommand helpers.
/// </summary>
public class AllSubcommandTests : IDisposable
{
    private readonly string _tempDir;

    public AllSubcommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AllSubcommandTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // -----------------------------------------------------------------------
    // BackupAndClearStaleConfigAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task BackupAndClearStaleConfig_WhenTenantMatches_LeavesFilesUntouched()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, """{"tenantId": "same-tenant"}""");

        await AllSubcommand.BackupAndClearStaleConfigAsync(configPath, "same-tenant", NullLogger.Instance);

        File.Exists(configPath).Should().BeTrue(
            because: "files must not be touched when the tenant matches");
        Directory.GetFiles(_tempDir, "*.bak.*").Should().BeEmpty(
            because: "no backup should be created when the tenant is the same");
    }

    [Fact]
    public async Task BackupAndClearStaleConfig_WhenConfigFileAbsent_DoesNothing()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        // deliberately not created

        await AllSubcommand.BackupAndClearStaleConfigAsync(configPath, "new-tenant", NullLogger.Instance);

        Directory.GetFiles(_tempDir).Should().BeEmpty(
            because: "nothing should be written when no config file exists");
    }

    [Fact]
    public async Task BackupAndClearStaleConfig_WhenTenantDiffers_BacksUpConfigAndRemovesOriginal()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, """{"tenantId": "old-tenant"}""");

        await AllSubcommand.BackupAndClearStaleConfigAsync(configPath, "new-tenant", NullLogger.Instance);

        File.Exists(configPath).Should().BeFalse(
            because: "the original config file must be removed when the tenant differs");
        Directory.GetFiles(_tempDir, "a365.config.json.bak.*").Should().HaveCount(1,
            because: "the old config must be backed up with a timestamp suffix");
    }

    [Fact]
    public async Task BackupAndClearStaleConfig_WhenTenantDiffers_AlsoBacksUpGeneratedConfig()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        var generatedPath = Path.Combine(_tempDir, "a365.generated.config.json");
        File.WriteAllText(configPath, """{"tenantId": "old-tenant"}""");
        File.WriteAllText(generatedPath, """{"agentBlueprintId": "bp-from-old-tenant"}""");

        await AllSubcommand.BackupAndClearStaleConfigAsync(configPath, "new-tenant", NullLogger.Instance);

        File.Exists(generatedPath).Should().BeFalse(
            because: "the generated config must also be removed when the tenant differs");
        Directory.GetFiles(_tempDir, "a365.generated.config.json.bak.*").Should().HaveCount(1,
            because: "the generated config must be backed up alongside the static config");
    }

    [Fact]
    public async Task BackupAndClearStaleConfig_WhenTenantDiffersButNoGeneratedConfig_OnlyBacksUpStaticConfig()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, """{"tenantId": "old-tenant"}""");
        // deliberately no a365.generated.config.json

        await AllSubcommand.BackupAndClearStaleConfigAsync(configPath, "new-tenant", NullLogger.Instance);

        Directory.GetFiles(_tempDir, "a365.config.json.bak.*").Should().HaveCount(1,
            because: "the static config must be backed up");
        Directory.GetFiles(_tempDir, "a365.generated.config.json.bak.*").Should().BeEmpty(
            because: "no generated config backup should be created when the file did not exist");
    }

    [Fact]
    public async Task BackupAndClearStaleConfig_WhenConfigIsMalformedJson_BacksUpAsIfMismatch()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, "this is not valid json");

        await AllSubcommand.BackupAndClearStaleConfigAsync(configPath, "new-tenant", NullLogger.Instance);

        File.Exists(configPath).Should().BeFalse(
            because: "a malformed config file cannot be trusted and must be backed up");
        Directory.GetFiles(_tempDir, "a365.config.json.bak.*").Should().HaveCount(1);
    }

    [Fact]
    public async Task BackupAndClearStaleConfig_TenantComparisonIsCaseInsensitive()
    {
        var configPath = Path.Combine(_tempDir, "a365.config.json");
        File.WriteAllText(configPath, """{"tenantId": "TENANT-ABC"}""");

        await AllSubcommand.BackupAndClearStaleConfigAsync(configPath, "tenant-abc", NullLogger.Instance);

        File.Exists(configPath).Should().BeTrue(
            because: "tenant ID comparison must be case-insensitive");
        Directory.GetFiles(_tempDir, "*.bak.*").Should().BeEmpty(
            because: "no backup should be created when tenants match case-insensitively");
    }
}
