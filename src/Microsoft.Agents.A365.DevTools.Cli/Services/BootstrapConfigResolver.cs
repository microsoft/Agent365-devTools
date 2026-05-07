// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Resolves <see cref="Agent365Config"/> from CLI arguments or from disk.
/// Centralizes the three-mode resolution logic used by every subcommand that previously
/// required a365.config.json to be present.
/// </summary>
public interface IBootstrapConfigResolver
{
    /// <summary>
    /// Resolves configuration using one of three modes:
    /// 1. <paramref name="agentName"/> non-null → bootstrap in-memory config from Entra lookups.
    /// 2. Config file exists → load from disk via <see cref="IConfigService.LoadAsync"/>.
    /// 3. Neither present → log actionable error, return <c>null</c>.
    /// </summary>
    /// <param name="agentName">Value of the --agent-name option, or null if not supplied.</param>
    /// <param name="tenantIdFlag">Value of the --tenant-id option, or null to auto-detect.</param>
    /// <param name="configFile">Path to a365.config.json (may or may not exist).</param>
    /// <param name="isCleanupMode">
    /// When true, additionally resolves blueprint and registration IDs from Entra and the
    /// generated config so that cleanup commands can delete resources without a config file.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Resolved config, or <c>null</c> if resolution failed.</returns>
    Task<Agent365Config?> ResolveAsync(
        string? agentName,
        string? tenantIdFlag,
        FileInfo configFile,
        bool isCleanupMode = false,
        CancellationToken ct = default);

    /// <summary>
    /// Writes a minimal <c>a365.config.json</c> to <paramref name="path"/> containing only the
    /// init-only (static) fields from <paramref name="config"/>.  Called by <c>setup all</c> after
    /// bootstrap so that subsequent <see cref="IConfigService.SaveStateAsync"/> calls write the
    /// generated file next to the static config rather than to the global directory.
    /// </summary>
    Task WriteBootstrapConfigAsync(Agent365Config config, string path);

    /// <summary>
    /// When running a bootstrap setup (<c>--agent-name</c>), checks whether any config files in the
    /// current directory belong to a different tenant than <paramref name="resolvedTenantId"/>. If so,
    /// backs both files up with a timestamp suffix and removes the originals so setup starts clean
    /// without inheriting stale resource IDs from a previous run.
    /// </summary>
    Task BackupAndClearStaleConfigAsync(string configPath, string resolvedTenantId);

    /// <summary>
    /// Detects the current az CLI tenant via <c>az account show</c> and compares it against the
    /// tenant stored in <paramref name="configPath"/>. If they differ, backs up both config files
    /// and returns <c>true</c> so the caller can start fresh. Returns <c>false</c> when az CLI is
    /// unavailable, not signed in, or the tenants match — in all cases the caller may proceed normally.
    /// </summary>
    Task<bool> CheckAndBackupStaleConfigAsync(string configPath, CancellationToken ct = default);
}

/// <inheritdoc/>
internal sealed class BootstrapConfigResolver : IBootstrapConfigResolver
{
    private readonly IConfigService _configService;
    private readonly CommandExecutor _executor;
    private readonly GraphApiService? _graphApiService;
    private readonly ILogger<BootstrapConfigResolver> _logger;

    public BootstrapConfigResolver(
        IConfigService configService,
        CommandExecutor executor,
        GraphApiService? graphApiService,
        ILoggerFactory loggerFactory)
    {
        _configService = configService;
        _executor = executor;
        _graphApiService = graphApiService;
        _logger = loggerFactory.CreateLogger<BootstrapConfigResolver>();
    }

    /// <inheritdoc/>
    public async Task<Agent365Config?> ResolveAsync(
        string? agentName,
        string? tenantIdFlag,
        FileInfo configFile,
        bool isCleanupMode = false,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(agentName))
        {
            return isCleanupMode
                ? await BuildBootstrapConfigForCleanupAsync(agentName, tenantIdFlag, ct)
                : await BuildBootstrapConfigAsync(agentName, tenantIdFlag, ct);
        }

        if (configFile.Exists)
        {
            // Before loading, detect whether the user switched az login tenants since the last
            // setup run. If so, silently back up stale config files and start clean so this run
            // does not inherit resource IDs from a different tenant.
            var currentTenant = await TryGetCurrentAzTenantAsync();
            if (!string.IsNullOrWhiteSpace(currentTenant))
                await BackupAndClearStaleConfigAsync(configFile.FullName, currentTenant);

            if (!File.Exists(configFile.FullName))
            {
                // Config was backed up because the tenant changed. The user must supply
                // --agent-name to set up fresh for the new tenant.
                _logger.LogInformation("Run 'a365 setup all --agent-name <name>' to set up for the new tenant.");
                return null;
            }

            try
            {
                var config = await _configService.LoadAsync(configFile.FullName);
                _logger.LogDebug("Loaded configuration from {ConfigFile}", configFile.FullName);
                return config;
            }
            catch (ConfigFileNotFoundException)
            {
                _logger.LogError("Agent configuration could not be loaded. Use --agent-name to specify the agent name.");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load configuration: {Message}", ex.Message);
                return null;
            }
        }

        _logger.LogError("Agent name required. Use --agent-name <name> to specify the agent name.");
        return null;
    }

    /// <inheritdoc/>
    public async Task WriteBootstrapConfigAsync(Agent365Config config, string path)
    {
        var staticFields = new Dictionary<string, object?>
        {
            ["tenantId"] = config.TenantId,
            ["clientAppId"] = config.ClientAppId,
            ["agentIdentityDisplayName"] = config.AgentIdentityDisplayName,
            ["agentBlueprintDisplayName"] = config.AgentBlueprintDisplayName,
            ["agentDescription"] = config.AgentDescription,
            ["aiTeammate"] = config.AiTeammate,
            ["useBlueprint"] = config.UseBlueprint,
        };

        var json = JsonSerializer.Serialize(staticFields, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
        _logger.LogDebug("Wrote bootstrap config to {Path}", path);
    }

    /// <inheritdoc/>
    public async Task BackupAndClearStaleConfigAsync(string configPath, string resolvedTenantId)
    {
        if (!File.Exists(configPath))
            return;

        // shouldBackup is true when: (a) the file is unreadable/malformed, or (b) the tenant
        // is present and explicitly differs from the resolved tenant.
        bool shouldBackup = false;
        string? existingTenantId = null;
        try
        {
            var json = await File.ReadAllTextAsync(configPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tenantId", out var prop))
            {
                existingTenantId = prop.GetString();
                shouldBackup = !string.IsNullOrWhiteSpace(existingTenantId) &&
                               !string.Equals(existingTenantId, resolvedTenantId, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Unreadable or malformed config — back it up so setup starts clean.
            shouldBackup = true;
        }

        if (!shouldBackup)
            return;

        _logger.LogInformation(
            "Detected tenant change — previous setup was for tenant {OldTenant}, " +
            "current session is tenant {NewTenant}. Starting fresh setup for the new tenant.",
            existingTenantId, resolvedTenantId);

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var configDir = Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory;

        var configBackup = configPath + ".bak." + timestamp;
        File.Move(configPath, configBackup);
        _logger.LogDebug("Backed up: {File}", Path.GetFileName(configBackup));

        var generatedPath = Path.Combine(configDir, "a365.generated.config.json");
        if (File.Exists(generatedPath))
        {
            var generatedBackup = generatedPath + ".bak." + timestamp;
            File.Move(generatedPath, generatedBackup);
            _logger.LogDebug("Backed up: {File}", Path.GetFileName(generatedBackup));
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CheckAndBackupStaleConfigAsync(string configPath, CancellationToken ct = default)
    {
        var currentTenant = await TryGetCurrentAzTenantAsync();
        if (string.IsNullOrWhiteSpace(currentTenant))
            return false;
        await BackupAndClearStaleConfigAsync(configPath, currentTenant);
        return !File.Exists(configPath);
    }

    private async Task<string?> TryGetCurrentAzTenantAsync()
    {
        try
        {
            var result = await _executor.ExecuteAsync(
                "az", "account show --query tenantId -o tsv",
                captureOutput: true, suppressErrorLogging: true);
            var tenant = result.StandardOutput?.Trim();
            return string.IsNullOrWhiteSpace(tenant) ? null : tenant;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve current Azure CLI tenant.");
            return null;
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<Agent365Config?> BuildBootstrapConfigAsync(
        string agentName,
        string? tenantIdFlag,
        CancellationToken ct)
    {
        var tenantId = await SetupHelpers.ResolveBootstrapTenantIdAsync(tenantIdFlag, _executor, _logger);
        if (tenantId is null)
            return null;

        var clientAppId = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
            tenantId, _graphApiService, _logger, ct);
        if (string.IsNullOrWhiteSpace(clientAppId))
            return null;

        if (_graphApiService != null)
            _graphApiService.CustomClientAppId = clientAppId;

        var config = new Agent365Config
        {
            TenantId = tenantId,
            ClientAppId = clientAppId,
            AgentIdentityDisplayName = $"{agentName} Identity",
            AgentBlueprintDisplayName = $"{agentName} Blueprint",
            AgentDescription = agentName,
            AiTeammate = false,
            UseBlueprint = true,
        };

        var errors = config.ValidateNonDwMinimal();
        if (errors.Count > 0)
        {
            foreach (var err in errors)
                _logger.LogError("{Error}", err);
            return null;
        }

        return config;
    }

    private async Task<Agent365Config?> BuildBootstrapConfigForCleanupAsync(
        string agentName,
        string? tenantIdFlag,
        CancellationToken ct)
    {
        // Step 1: Resolve tenant ID.
        var tenantId = await SetupHelpers.ResolveBootstrapTenantIdAsync(tenantIdFlag, _executor, _logger);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogError("Could not detect tenant ID. Sign in with 'az login' or pass --tenant-id.");
            return null;
        }

        // Step 2: Resolve client app ID — prefer local a365.config.json when tenant matches.
        var resolvedClientAppId = await SetupHelpers.ResolveBootstrapClientAppIdAsync(
            tenantId, _graphApiService, _logger, ct, preferLocalConfig: true);
        if (string.IsNullOrWhiteSpace(resolvedClientAppId))
            return null;

        if (_graphApiService != null)
            _graphApiService.CustomClientAppId = resolvedClientAppId;

        // Step 3: Resolve blueprint ID from Entra (authoritative source).
        var blueprintDisplayName = $"{agentName} Blueprint";
        string? resolvedBlueprintId = null;
        if (_graphApiService != null)
        {
            resolvedBlueprintId = await _graphApiService.FindApplicationByDisplayNameAsync(
                tenantId, blueprintDisplayName, ct);
            if (string.IsNullOrWhiteSpace(resolvedBlueprintId))
                _logger.LogWarning(
                    "Blueprint '{Name}' not found in Entra.",
                    blueprintDisplayName);
        }

        // Step 4: Load generated config and cross-validate blueprint IDs.
        var localGeneratedPath = Path.Combine(Environment.CurrentDirectory, "a365.generated.config.json");
        var globalGeneratedPath = Path.Combine(ConfigService.GetGlobalConfigDirectory(), "a365.generated.config.json");
        var generatedConfigPath = File.Exists(localGeneratedPath) ? localGeneratedPath : globalGeneratedPath;

        string? agentRegistrationId = null;
        string? agenticAppId = null;
        string? agentBlueprintSpObjectId = null;
        string? configBlueprintId = null;

        if (File.Exists(generatedConfigPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(generatedConfigPath, ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                configBlueprintId = SetupHelpers.GetJsonString(root, "agentBlueprintId");

                if (!string.IsNullOrWhiteSpace(resolvedBlueprintId) &&
                    string.Equals(resolvedBlueprintId, configBlueprintId, StringComparison.OrdinalIgnoreCase))
                {
                    agentRegistrationId = SetupHelpers.GetJsonString(root, "agentRegistrationId");
                    agenticAppId = SetupHelpers.GetJsonString(root, "AgenticAppId");
                    agentBlueprintSpObjectId = SetupHelpers.GetJsonString(root, "agentBlueprintServicePrincipalObjectId");
                    _logger.LogInformation("Loaded resource IDs from {Path}", generatedConfigPath);
                }
                else if (!string.IsNullOrWhiteSpace(configBlueprintId) && !string.IsNullOrWhiteSpace(resolvedBlueprintId))
                {
                    _logger.LogWarning(
                        "Generated config blueprint ID ({ConfigId}) does not match Entra-resolved ID ({ResolvedId}). Skipping resource IDs from file.",
                        configBlueprintId, resolvedBlueprintId);
                }
                else if (string.IsNullOrWhiteSpace(resolvedBlueprintId))
                {
                    // Entra lookup failed — fall back to file values for all IDs.
                    agentRegistrationId = SetupHelpers.GetJsonString(root, "agentRegistrationId");
                    agenticAppId = SetupHelpers.GetJsonString(root, "AgenticAppId");
                    agentBlueprintSpObjectId = SetupHelpers.GetJsonString(root, "agentBlueprintServicePrincipalObjectId");
                    _logger.LogInformation(
                        "Loaded resource IDs from {Path} (Entra lookup unavailable)", generatedConfigPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Could not read generated config at {Path}: {Message}", generatedConfigPath, ex.Message);
            }
        }
        else
        {
            _logger.LogWarning(
                "No generated config found at {Path}. Resource IDs may be missing.",
                generatedConfigPath);
        }

        var blueprintId = resolvedBlueprintId ?? configBlueprintId;

        var config = new Agent365Config
        {
            TenantId = tenantId,
            ClientAppId = resolvedClientAppId,
            AgentIdentityDisplayName = $"{agentName} Identity",
            AgentBlueprintDisplayName = blueprintDisplayName,
            AgentDescription = agentName,
            AiTeammate = false,
            UseBlueprint = true,
        };

        config.AgentBlueprintId = blueprintId;
        config.AgentBlueprintServicePrincipalObjectId = agentBlueprintSpObjectId;
        config.AgentRegistrationId = agentRegistrationId;
        config.AgenticAppId = agenticAppId;

        _logger.LogDebug("Bootstrap cleanup config:");
        _logger.LogDebug("  TenantId:        {TenantId}", tenantId);
        _logger.LogDebug("  ClientAppId:     {ClientAppId}", resolvedClientAppId);
        _logger.LogDebug("  BlueprintId:     {BlueprintId}", blueprintId ?? "(not found)");
        _logger.LogDebug("  BlueprintSP:     {SpId}", agentBlueprintSpObjectId ?? "(not found)");
        _logger.LogDebug("  AgentIdentitySP: {SpId}", agenticAppId ?? "(not found)");
        _logger.LogDebug("  RegistrationId:  {RegId}", agentRegistrationId ?? "(not found)");

        return config;
    }
}
