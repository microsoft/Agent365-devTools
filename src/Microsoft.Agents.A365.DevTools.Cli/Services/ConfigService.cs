// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Implementation of configuration service for Agent 365 CLI.
/// Handles loading, saving, and validating the two-file configuration model.
/// </summary>
public class ConfigService : IConfigService
{
    /// <summary>
    /// Gets the global directory path for config files.
    /// Cross-platform implementation following XDG Base Directory Specification:
    /// - Windows: %LocalAppData%\Microsoft.Agents.A365.DevTools.Cli
    /// - Linux/Mac: $XDG_CONFIG_HOME/a365 (default: ~/.config/a365)
    /// </summary>
    public static string GetGlobalConfigDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetEnvironmentVariable("LocalAppData");
            if (!string.IsNullOrEmpty(localAppData))
                return Path.Combine(localAppData, AuthenticationConstants.ApplicationName);
            
            // Fallback to SpecialFolder if environment variable not set
            var fallbackPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(fallbackPath, AuthenticationConstants.ApplicationName);
        }
        else
        {
            // On non-Windows, use XDG Base Directory Specification
            // https://specifications.freedesktop.org/basedir-spec/basedir-spec-latest.html
            var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrEmpty(xdgConfigHome))
                return Path.Combine(xdgConfigHome, "a365");
            
            // Default to ~/.config/a365 if XDG_CONFIG_HOME not set
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
                return Path.Combine(home, ".config", "a365");
            
            // Final fallback to current directory
            return Environment.CurrentDirectory;
        }
    }

    /// <summary>
    /// Gets the logs directory path for CLI command execution logs.
    /// Follows Microsoft CLI patterns (Azure CLI, .NET CLI).
    /// - Windows: %LocalAppData%\Microsoft.Agents.A365.DevTools.Cli\logs\
    /// - Linux/Mac: ~/.config/a365/logs/
    /// </summary>
    public static string GetLogsDirectory()
    {
        var configDir = GetGlobalConfigDirectory();
        var logsDir = Path.Combine(configDir, "logs");
        
        // Ensure directory exists
        try
        {
            Directory.CreateDirectory(logsDir);
        }
        catch
        {
            // If we can't create the logs directory, fall back to temp
            logsDir = Path.Combine(Path.GetTempPath(), "a365-logs");
            Directory.CreateDirectory(logsDir);
        }
        
        return logsDir;
    }

    /// <summary>
    /// Gets the log file path for a specific command.
    /// Always overwrites - keeps only the latest run for debugging.
    /// </summary>
    /// <param name="commandName">Name of the command (e.g., "setup", "deploy", "create-instance")</param>
    /// <returns>Full path to the command log file (e.g., "a365.setup.log")</returns>
    public static string GetCommandLogPath(string commandName)
    {
        var logsDir = GetLogsDirectory();
        return Path.Combine(logsDir, $"a365.{commandName}.log");
    }

    private readonly ILogger<ConfigService>? _logger;

    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Use relaxed encoder so URL-valued fields (e.g. consentUrl) keep literal '&' instead
        // of being escaped to '\u0026', which would break copy-paste into a browser.
        // This applies globally to all config serialization; only URL-typed string values
        // meaningfully benefit from or require the setting — all other scalar values are unaffected.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ConfigService(ILogger<ConfigService>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Agent365Config> LoadAsync(
        string configPath = "a365.config.json",
        string statePath = "a365.generated.config.json")
    {
        // SMART PATH RESOLUTION:
        // If configPath is absolute or contains directory separators, resolve statePath relative to it
        // This ensures generated config is loaded from the same directory as the main config
        string resolvedStatePath = statePath;
        
        if (Path.IsPathRooted(configPath) || configPath.Contains(Path.DirectorySeparatorChar) || configPath.Contains(Path.AltDirectorySeparatorChar))
        {
            // Config path is absolute or relative with directory - resolve state path in same directory
            var configDir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(configDir))
            {
                // Extract just the filename from statePath (in case caller passed a full path)
                var stateFileName = Path.GetFileName(statePath);
                resolvedStatePath = Path.Combine(configDir, stateFileName);
                _logger?.LogDebug("Resolved state path to: {StatePath} (same directory as config)", resolvedStatePath);
            }
        }
        
        // Resolve config file path
        var resolvedConfigPath = FindConfigFile(configPath) ?? configPath;

        // Validate static config file exists
        if (!File.Exists(resolvedConfigPath))
        {
            throw new ConfigFileNotFoundException();
        }

        // Load static configuration (required)
        var staticJson = await File.ReadAllTextAsync(resolvedConfigPath);
        var staticConfig = JsonSerializer.Deserialize<Agent365Config>(staticJson, DefaultJsonOptions)
            ?? throw new JsonException($"Failed to deserialize static configuration from {resolvedConfigPath}");

        _logger?.LogDebug("Loaded static configuration from: {ConfigPath}", resolvedConfigPath);

        // Try to find state file (use resolved path first, then fallback to search)
        string? actualStatePath = null;
        
        // First, try the resolved state path (same directory as config)
        if (File.Exists(resolvedStatePath))
        {
            actualStatePath = resolvedStatePath;
            _logger?.LogDebug("Found state file at resolved path: {StatePath}", actualStatePath);
        }
        else
        {
            // Fallback: search for state file
            actualStatePath = FindConfigFile(Path.GetFileName(statePath));
            if (actualStatePath != null)
            {
                _logger?.LogDebug("Found state file via search: {StatePath}", actualStatePath);
            }
        }

        // Load dynamic state if exists (optional)
        if (actualStatePath != null && File.Exists(actualStatePath))
        {
            var stateJson = await File.ReadAllTextAsync(actualStatePath);
            var stateData = JsonSerializer.Deserialize<JsonElement>(stateJson, DefaultJsonOptions);

            // Merge dynamic properties into static config
            MergeDynamicProperties(staticConfig, stateData);
            _logger?.LogDebug("Merged dynamic state from: {StatePath}", actualStatePath);
        }
        else
        {
            _logger?.LogDebug("No dynamic state file found at: {StatePath}", resolvedStatePath);
        }

        // Validate the merged configuration
        var validationResult = await ValidateAsync(staticConfig);
        if (!validationResult.IsValid)
        {
            _logger?.LogError("Configuration validation failed:");
            foreach (var error in validationResult.Errors)
            {
                _logger?.LogError("  * {Error}", error);
            }
            
            // Convert validation errors to structured exception
            var validationErrors = validationResult.Errors
                .Select(e => ParseValidationError(e))
                .ToList();
            
            throw new Exceptions.ConfigurationValidationException(resolvedConfigPath, validationErrors);
        }

        // Log warnings if any
        if (validationResult.Warnings.Count > 0)
        {
            foreach (var warning in validationResult.Warnings)
            {
                _logger?.LogWarning("  * {Warning}", warning);
            }
        }

        return staticConfig;
    }

    /// <inheritdoc />
    public async Task SaveStateAsync(
        Agent365Config config,
        string statePath = "a365.generated.config.json")
    {
        // Extract only dynamic (get/set) properties
        var dynamicData = ExtractDynamicProperties(config);

        // Update metadata
        dynamicData["lastUpdated"] = DateTime.UtcNow;
        dynamicData["cliVersion"] = GetCliVersion();

        // Serialize to JSON
        var json = JsonSerializer.Serialize(dynamicData, DefaultJsonOptions);

        // If an absolute path is provided, use it directly (for testing and explicit control)
        if (Path.IsPathRooted(statePath))
        {
            try
            {
                await File.WriteAllTextAsync(statePath, json);
                _logger?.LogDebug("Saved dynamic state to absolute path: {StatePath}", statePath);
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to save dynamic state to: {StatePath}", statePath);
                throw;
            }
        }

        // Always save relative to the current directory.
        // Global directory fallback has been removed — config is always project-local.
        var currentDirPath = Path.Combine(Environment.CurrentDirectory, statePath);
        try
        {
            await File.WriteAllTextAsync(currentDirPath, json);
            _logger?.LogDebug("Saved dynamic state to: {StatePath}", currentDirPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save dynamic state to: {StatePath}", currentDirPath);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string?> InvalidateGeneratedConfigAsync(
        Agent365Config config,
        string reason,
        string statePath = "a365.generated.config.json")
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);

        // Resolve the target file. Honour absolute paths; otherwise treat as current-directory relative
        // (mirrors SaveStateAsync semantics).
        var targetPath = Path.IsPathRooted(statePath)
            ? statePath
            : Path.Combine(Environment.CurrentDirectory, statePath);

        // Back up the existing file (if any) before we overwrite it. Sanitize the reason to keep the
        // file name portable across Windows/macOS/Linux.
        string? backupPath = null;
        if (File.Exists(targetPath))
        {
            var safeReason = SanitizeForFileName(reason);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var dir = Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory;
            backupPath = Path.Combine(dir, $"a365.generated.config.before-{safeReason}-{timestamp}.json");

            try
            {
                File.Copy(targetPath, backupPath, overwrite: false);
                _logger?.LogDebug(
                    "Invalidating generated configuration ({Reason}). Existing file backed up to: {BackupPath}",
                    reason, backupPath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Failed to back up existing generated configuration before invalidation. Aborting reset to avoid data loss: {TargetPath}",
                    targetPath);
                throw;
            }
        }
        else
        {
            _logger?.LogDebug(
                "Invalidating generated configuration ({Reason}). No existing file to back up at: {TargetPath}",
                reason, targetPath);
        }

        // Reset every dynamic (get/set) property to its default. This wipes the in-memory mirror of
        // the generated file so callers do not continue acting on stale identifiers (agent identity,
        // registration, SP IDs, secrets, consents, bot, infra) that belong to a now-orphaned root.
        ResetDynamicProperties(config);

        // Persist the empty state so subsequent writers see a clean file and the on-disk view matches
        // the in-memory view atomically.
        await SaveStateAsync(config, statePath);

        return backupPath;
    }

    /// <summary>
    /// Resets every dynamic (get/set) property on the supplied config to its CLR default
    /// (null for reference/Nullable&lt;T&gt;, default(T) for value types). Collection properties whose
    /// default would be null are replaced with a fresh empty instance to preserve non-null
    /// invariants expected by downstream code (e.g. ResourceConsents).
    /// </summary>
    private static void ResetDynamicProperties(Agent365Config config)
    {
        var type = typeof(Agent365Config);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in properties)
        {
            var setMethod = prop.GetSetMethod();
            if (setMethod == null) continue;

            // Skip init-only setters (static config surface). Detect via IsInitOnly modreq, matching
            // ExtractDynamicProperties' definition of "dynamic".
            var returnParamMods = setMethod.ReturnParameter.GetRequiredCustomModifiers();
            var isInitOnly = returnParamMods.Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
            if (isInitOnly) continue;

            // Preserve non-null collection invariants by allocating an empty instance instead of null.
            if (prop.PropertyType == typeof(List<Models.ResourceConsent>))
            {
                prop.SetValue(config, new List<Models.ResourceConsent>());
                continue;
            }

            // For all other dynamic properties, set to default(T).
            var defaultValue = prop.PropertyType.IsValueType
                ? Activator.CreateInstance(prop.PropertyType)
                : null;
            prop.SetValue(config, defaultValue);
        }
    }

    /// <summary>
    /// Replaces characters that are invalid in file names (cross-platform) with a hyphen and trims
    /// leading and trailing hyphens. Does not collapse internal runs of hyphens — successive invalid
    /// characters in the input produce successive hyphens in the output, which is harmless for the
    /// backup file-name suffix use case. Returns "reset" when the sanitized string is empty.
    /// </summary>
    private static string SanitizeForFileName(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                sb.Append(ch);
            else
                sb.Append('-');
        }
        var sanitized = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(sanitized) ? "reset" : sanitized;
    }

    /// <inheritdoc />
    public async Task<ValidationResult> ValidateAsync(Agent365Config config)
    {
        // Required-field rules live in Agent365Config.Validate() — single source of truth.
        var errors = new List<string>(config.Validate());
        var warnings = new List<string>();

        // Format-only checks — run only when the value is present (required-field errors already above).
        if (!string.IsNullOrWhiteSpace(config.TenantId))
            ValidateGuid(config.TenantId, nameof(config.TenantId), errors);

        // MessagingEndpoint is optional; if provided it must be a valid URL.
        if (!string.IsNullOrWhiteSpace(config.MessagingEndpoint))
            ValidateUrl(config.MessagingEndpoint, nameof(config.MessagingEndpoint), errors);

        // Validate dynamic properties if they exist
        if (config.ManagedIdentityPrincipalId != null)
        {
            ValidateGuid(config.ManagedIdentityPrincipalId, nameof(config.ManagedIdentityPrincipalId), errors);
        }

        if (config.AgenticAppId != null)
        {
            ValidateGuid(config.AgenticAppId, nameof(config.AgenticAppId), errors);
        }

        if (config.BotId != null)
        {
            ValidateGuid(config.BotId, nameof(config.BotId), errors);
        }

        if (config.BotMsaAppId != null)
        {
            ValidateGuid(config.BotMsaAppId, nameof(config.BotMsaAppId), errors);
        }

        // Validate URLs if present
        if (config.BotMessagingEndpoint != null)
        {
            ValidateUrl(config.BotMessagingEndpoint, nameof(config.BotMessagingEndpoint), errors);
        }

        // Add warnings for best practices
        if (string.IsNullOrEmpty(config.AgentDescription))
        {
            warnings.Add("AgentDescription is not set. Consider adding a description for better user experience.");
        }

        // AgentIdentityScopes and AgentApplicationScopes are now hardcoded defaults - no validation needed

        var result = errors.Count == 0
            ? ValidationResult.Success()
            : new ValidationResult { IsValid = false, Errors = errors, Warnings = warnings };

        if (!result.IsValid)
        {
            _logger?.LogWarning("Configuration validation failed with {ErrorCount} errors", errors.Count);
        }

        return await Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<bool> ConfigExistsAsync(string configPath = "a365.config.json")
    {
        var resolvedPath = FindConfigFile(configPath);
        return Task.FromResult(resolvedPath != null);
    }

    /// <inheritdoc />
    public Task<bool> StateExistsAsync(string statePath = "a365.generated.config.json")
    {
        var resolvedPath = FindConfigFile(statePath);
        return Task.FromResult(resolvedPath != null);
    }

    /// <inheritdoc />
    public async Task CreateDefaultConfigAsync(
        string configPath = "a365.config.json",
        Agent365Config? templateConfig = null)
    {
        // Only update in current directory if it already exists
        var config = templateConfig ?? new Agent365Config
        {
            TenantId = string.Empty,
            AgentIdentityDisplayName = string.Empty,
            // AgentIdentityScopes and AgentApplicationScopes are now hardcoded defaults
            DeploymentProjectPath = string.Empty,
            AgentDescription = string.Empty
        };

        // Only serialize static (init) properties for the config file
        var staticData = ExtractStaticProperties(config);
        var json = JsonSerializer.Serialize(staticData, DefaultJsonOptions);

        var currentDirPath = Path.Combine(Environment.CurrentDirectory, configPath);
        if (File.Exists(currentDirPath))
        {
            await File.WriteAllTextAsync(currentDirPath, json);
            _logger?.LogInformation("Updated configuration at: {ConfigPath}", currentDirPath);
        }
    }

    /// <inheritdoc />
    public async Task InitializeStateAsync(string statePath = "a365.generated.config.json")
    {
        // Create in current directory if no path components, otherwise use as-is
        var targetPath = Path.IsPathRooted(statePath) || statePath.Contains(Path.DirectorySeparatorChar)
            ? statePath
            : Path.Combine(Environment.CurrentDirectory, statePath);

        var emptyState = new Dictionary<string, object?>
        {
            ["lastUpdated"] = DateTime.UtcNow,
            ["cliVersion"] = GetCliVersion()
        };

        var json = JsonSerializer.Serialize(emptyState, DefaultJsonOptions);
        await File.WriteAllTextAsync(targetPath, json);
        _logger?.LogInformation("Initialized empty state file at: {StatePath}", targetPath);
    }

    #region Config File Resolution

    /// <summary>
    /// Searches for a config file in the current working directory only.
    /// Global config directory lookup has been removed to prevent stale config in one
    /// project directory from contaminating commands run in a different directory
    /// (e.g. a leftover global a365.config.json interfering with --agent-name bootstrap).
    /// </summary>
    /// <param name="fileName">The config file name to search for</param>
    /// <returns>The full path to the config file if found in the current directory, otherwise null</returns>
    private static string? FindConfigFile(string fileName)
    {
        var currentDirPath = Path.Combine(Environment.CurrentDirectory, fileName);
        return File.Exists(currentDirPath) ? currentDirPath : null;
    }

    /// <summary>
    /// Gets the path to the static configuration file (a365.config.json) in the current directory.
    /// </summary>
    /// <returns>Full path if found in the current directory, otherwise null</returns>
    public static string? GetConfigFilePath()
    {
        return FindConfigFile("a365.config.json");
    }

    /// <summary>
    /// Gets the path to the generated configuration file (a365.generated.config.json) in the current directory.
    /// </summary>
    /// <returns>Full path if found in the current directory, otherwise null</returns>
    public static string? GetGeneratedConfigFilePath()
    {
        return FindConfigFile("a365.generated.config.json");
    }

    /// <inheritdoc />
    public async Task TryResolveClientAppIdAsync(GraphApiService graphApiService, CancellationToken ct = default)
    {
        var configPath = GetConfigFilePath();
        if (configPath == null)
        {
            _logger?.LogDebug("No a365.config.json found — skipping client app ID resolution.");
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(configPath, ct);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;

            root.TryGetProperty("tenantId", out var tenantIdEl);
            root.TryGetProperty("clientAppId", out var clientAppIdEl);
            var tenantId = tenantIdEl.ValueKind == JsonValueKind.String ? tenantIdEl.GetString() : null;
            var configuredId = clientAppIdEl.ValueKind == JsonValueKind.String ? clientAppIdEl.GetString() : null;

            if (string.IsNullOrWhiteSpace(tenantId))
            {
                _logger?.LogDebug("No tenantId in config — skipping client app ID resolution.");
                return;
            }

            // If a clientAppId is configured, validate it still exists.
            if (!string.IsNullOrWhiteSpace(configuredId))
            {
                var exists = await graphApiService.ApplicationExistsByAppIdAsync(tenantId, configuredId, ct);
                if (exists)
                {
                    _logger?.LogDebug("Configured clientAppId {Id} is valid.", configuredId);
                    return;
                }

                _logger?.LogInformation(
                    "Configured clientAppId {Id} was not found in the tenant. Looking up by display name '{Name}'...",
                    configuredId, AuthenticationConstants.WellKnownClientAppDisplayName);
            }

            // Look up by well-known display name.
            var resolvedId = await graphApiService.FindApplicationByDisplayNameAsync(
                tenantId, AuthenticationConstants.WellKnownClientAppDisplayName, ct);

            if (string.IsNullOrWhiteSpace(resolvedId))
            {
                _logger?.LogDebug(
                    "No app named '{Name}' found — client app ID unresolved.",
                    AuthenticationConstants.WellKnownClientAppDisplayName);
                return;
            }

            if (string.Equals(resolvedId, configuredId, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogDebug("Resolved clientAppId matches configured value — no update needed.");
                return;
            }

            // Patch clientAppId in the JSON file preserving all other fields.
            await PatchClientAppIdInConfigFileAsync(configPath, resolvedId, ct);
            _logger?.LogInformation(
                "clientAppId updated to {NewId} (found by display name '{Name}'). a365.config.json has been updated.",
                resolvedId, AuthenticationConstants.WellKnownClientAppDisplayName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Client app ID resolution skipped due to error: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Patches only the clientAppId field in a365.config.json, preserving all other fields and formatting.
    /// Uses targeted regex replacement so JSON property order and any comments are kept intact.
    /// Falls back to deserialize/re-serialize if the field is not found (e.g., first-time write).
    /// </summary>
    private static async Task PatchClientAppIdInConfigFileAsync(string configPath, string newClientAppId, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(configPath, ct);
        var escapedValue = JsonSerializer.Serialize(newClientAppId); // produces "\"value\""

        // Replace the clientAppId value in-place, preserving property order and comments.
        var patched = Regex.Replace(
            json,
            @"(""clientAppId""\s*:\s*)""[^""\\]*(?:\\.[^""\\]*)*""",
            $"$1{escapedValue}",
            RegexOptions.None);

        if (patched != json)
        {
            await File.WriteAllTextAsync(configPath, patched, ct);
            return;
        }

        // Field not present — fall back to deserialize/re-serialize (first-time write).
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            json, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip })
            ?? throw new JsonException("Failed to parse config file for patching.");

        dict["clientAppId"] = JsonSerializer.SerializeToElement(newClientAppId);
        var updated = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(configPath, updated, ct);
    }

    #endregion

    #region Private Helper Methods

    /// <summary>
    /// Merges dynamic properties from JSON into the config object.
    /// </summary>
    private void MergeDynamicProperties(Agent365Config config, JsonElement stateData)
    {
        var type = typeof(Agent365Config);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in properties)
        {
            // Only process properties with public setter (not init-only)
            if (!HasPublicSetter(prop)) continue;

            var jsonName = GetJsonPropertyName(prop);
            if (stateData.TryGetProperty(jsonName, out var value))
            {
                try
                {
                    var convertedValue = ConvertJsonElement(value, prop.PropertyType);
                    prop.SetValue(config, convertedValue);
                }
                catch (Exception ex)
                {
                    // Log warning but continue - don't fail entire load for one bad property
                    _logger?.LogWarning(ex, "Failed to set property {PropertyName}", prop.Name);
                }
            }
        }

        // Migrate legacy key: generated configs written by older CLI versions use "botMessagingEndpoint".
        // If the new key "messagingEndpoint" was not found (BotMessagingEndpoint is still null),
        // fall back to the legacy key so existing setups continue to work without re-running setup.
        if (string.IsNullOrWhiteSpace(config.BotMessagingEndpoint) &&
            stateData.TryGetProperty("botMessagingEndpoint", out var legacyEndpoint) &&
            legacyEndpoint.ValueKind == JsonValueKind.String)
        {
            config.BotMessagingEndpoint = legacyEndpoint.GetString();
        }

        // Migrate legacy PascalCase keys written by older CLI versions (now camelCase).
        if (string.IsNullOrWhiteSpace(config.AgenticAppId) &&
            stateData.TryGetProperty("AgenticAppId", out var legacyAgenticAppId) &&
            legacyAgenticAppId.ValueKind == JsonValueKind.String)
        {
            config.AgenticAppId = legacyAgenticAppId.GetString();
        }

        if (string.IsNullOrWhiteSpace(config.AgenticUserId) &&
            stateData.TryGetProperty("AgenticUserId", out var legacyAgenticUserId) &&
            legacyAgenticUserId.ValueKind == JsonValueKind.String)
        {
            config.AgenticUserId = legacyAgenticUserId.GetString();
        }
    }

    /// <summary>
    /// Extracts only dynamic (get/set) properties from the config object.
    /// </summary>
    private Dictionary<string, object?> ExtractDynamicProperties(Agent365Config config)
    {
        var result = new Dictionary<string, object?>();
        var type = typeof(Agent365Config);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in properties)
        {
            // Only include properties with public setter (not init-only)
            if (!HasPublicSetter(prop)) continue;

            var jsonName = GetJsonPropertyName(prop);
            var value = prop.GetValue(config);
            // Omit null values: JsonIgnoreCondition.WhenWritingNull in DefaultIgnoreCondition does NOT
            // apply to dictionary values (dotnet/runtime#30690), so we filter here to avoid writing
            // "agentBlueprintClientSecret": null and similar fields that would then re-merge as null
            // on the next LoadAsync, silently losing previously-written values.
            if (value != null)
                result[jsonName] = value;
        }

        return result;
    }

    /// <summary>
    /// Extracts only static (init) properties from the config object.
    /// </summary>
    private Dictionary<string, object?> ExtractStaticProperties(Agent365Config config)
    {
        var result = new Dictionary<string, object?>();
        var type = typeof(Agent365Config);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in properties)
        {
            // Only include properties without public setter (init-only)
            if (HasPublicSetter(prop)) continue;

            var jsonName = GetJsonPropertyName(prop);
            var value = prop.GetValue(config);

            // Skip null values for cleaner JSON
            if (value != null)
            {
                result[jsonName] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// Checks if a property has a public setter (not init-only).
    /// </summary>
    private bool HasPublicSetter(PropertyInfo prop)
    {
        var setMethod = prop.GetSetMethod();
        if (setMethod == null) return false;

        // Check if it's an init-only property
        var returnParam = setMethod.ReturnParameter;
        var modifiers = returnParam.GetRequiredCustomModifiers();
        return !modifiers.Contains(typeof(IsExternalInit));
    }

    /// <summary>
    /// Gets the JSON property name from JsonPropertyName attribute or property name.
    /// </summary>
    private string GetJsonPropertyName(PropertyInfo prop)
    {
        var attr = prop.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>();
        return attr?.Name ?? prop.Name;
    }

    /// <summary>
    /// Converts JsonElement to the target property type.
    /// </summary>
    private object? ConvertJsonElement(JsonElement element, Type targetType)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;

        // Handle nullable types
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlyingType == typeof(string))
            return element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.GetRawText(); // fallback: convert any other JSON type to string

        if (underlyingType == typeof(int))
            return element.GetInt32();

        if (underlyingType == typeof(bool))
        {
            if (element.ValueKind == JsonValueKind.True) return true;
            if (element.ValueKind == JsonValueKind.False) return false;
            if (element.ValueKind == JsonValueKind.String &&
                bool.TryParse(element.GetString(), out var result))
                return result;

            return element.GetBoolean();
        }

        if (underlyingType == typeof(DateTime))
            return element.GetDateTime();

        if (underlyingType == typeof(Guid))
            return element.GetGuid();

        if (underlyingType == typeof(List<string>))
        {
            var list = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                list.Add(item.GetString() ?? string.Empty);
            }
            return list;
        }

        // For complex types, deserialize
        return JsonSerializer.Deserialize(element.GetRawText(), targetType, DefaultJsonOptions);
    }

    /// <summary>
    /// Gets the current CLI version.
    /// </summary>
    private string GetCliVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version?.ToString() ?? "1.0.0";
    }

    #endregion

    #region Validation Helpers

    private void ValidateGuid(string? value, string propertyName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        if (!Guid.TryParse(value, out _))
        {
            errors.Add($"{propertyName} must be a valid GUID format.");
        }
    }

    private void ValidateUrl(string? value, string propertyName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"{propertyName} must be a valid HTTP or HTTPS URL.");
        }
    }


    /// <summary>
    /// Parses a validation error message into a ValidationError object.
    /// Error format: "PropertyName must ..." or "PropertyName: error message"
    /// </summary>
    private Exceptions.ValidationError ParseValidationError(string errorMessage)
    {
        // Try to extract field name from error message
        // Common patterns:
        // - "PropertyName must ..."
        // - "PropertyName: error message"
        // - "PropertyName is required ..."
        
        var parts = errorMessage.Split(new[] { ' ', ':' }, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            var fieldName = parts[0].Trim();
            var message = parts[1].Trim();
            return new Exceptions.ValidationError(fieldName, message);
        }
        
        // Fallback: treat entire message as the error
        return new Exceptions.ValidationError("Configuration", errorMessage);
    }

    #endregion
}