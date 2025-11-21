// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Constants;

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

    /// <summary>
    /// Gets the full path to a config file in the global directory.
    /// </summary>
    private static string GetGlobalConfigPath(string fileName)
    {
        return Path.Combine(GetGlobalConfigDirectory(), fileName);
    }

    private static string GetGlobalGeneratedConfigPath()
    {
        return GetGlobalConfigPath("a365.generated.config.json");
    }

    /// <summary>
    /// Syncs a config file to the global directory for portability.
    /// This allows CLI commands to run from any directory.
    /// </summary>
    private async Task<bool> SyncConfigToGlobalDirectoryAsync(string fileName, string content, bool throwOnError = false)
    {
        try
        {
            var globalDir = GetGlobalConfigDirectory();
            Directory.CreateDirectory(globalDir);
            
            var globalPath = GetGlobalConfigPath(fileName);
            
            // Write the config content to the global directory
            await File.WriteAllTextAsync(globalPath, content);
            
            _logger?.LogInformation("Synced configuration to global directory: {Path}", globalPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to sync {FileName} to global directory. CLI may not work from other directories.", fileName);
            if (throwOnError) throw;
            return false;
        }
    }

    public static void WarnIfLocalGeneratedConfigIsStale(string? localPath, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) return;
        var globalPath = GetGlobalGeneratedConfigPath();
        if (!File.Exists(globalPath)) return;
        
        try
        {
            // Compare the lastUpdated timestamps from INSIDE the JSON content, not file system timestamps
            // This is because SaveStateAsync writes local first, then global, creating a small time difference
            // in file system timestamps even though the content (and lastUpdated field) are identical
            var localJson = File.ReadAllText(localPath);
            var globalJson = File.ReadAllText(globalPath);
            
            using var localDoc = JsonDocument.Parse(localJson);
            using var globalDoc = JsonDocument.Parse(globalJson);
            
            var localRoot = localDoc.RootElement;
            var globalRoot = globalDoc.RootElement;
            
            // Get lastUpdated from both files
            if (!localRoot.TryGetProperty("lastUpdated", out var localUpdated)) return;
            if (!globalRoot.TryGetProperty("lastUpdated", out var globalUpdated)) return;
            
            // Compare the raw string values instead of DateTime objects to avoid timezone conversion issues
            var localTimeStr = localUpdated.GetString();
            var globalTimeStr = globalUpdated.GetString();
            
            // If the timestamps are identical as strings, they're from the same save operation
            if (localTimeStr == globalTimeStr)
            {
                return; // Same save operation, no warning needed
            }
            
            // If timestamps differ, parse and compare them
            var localTime = localUpdated.GetDateTime();
            var globalTime = globalUpdated.GetDateTime();
            
            // Only warn if the content timestamps differ (meaning they're from different save operations)
            // TODO: Current design uses local folder data even if it's older than %LocalAppData%.
            // This needs to be revisited to determine if we should:
            // 1. Always prefer %LocalAppData% as authoritative source
            // 2. Prompt user to choose which config to use
            // 3. Auto-sync from newer to older location
            if (globalTime > localTime)
            {
                var msg = $"Warning: The local generated config (at {localPath}) is older than the global config (at {globalPath}). You may be using stale configuration. Consider syncing or running setup again.";
                if (logger != null)
                    logger.LogDebug(msg);
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(msg);
                    Console.ResetColor();
                }
            }
        }
        catch (Exception)
        {
            // If we can't parse or compare, just skip the warning rather than crashing
            // This method is a helpful check, not critical functionality
            return;
        }
    }
    
    private readonly ILogger<ConfigService>? _logger;

    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
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
            _logger?.LogError("Static configuration file not found: {ConfigPath}", resolvedConfigPath);
            throw new FileNotFoundException(
                $"Static configuration file not found: {resolvedConfigPath}. " +
                $"Run 'a365 init' to create a new configuration or specify a different path.");
        }

        // Load static configuration (required)
        var staticJson = await File.ReadAllTextAsync(resolvedConfigPath);
        var staticConfig = JsonSerializer.Deserialize<Agent365Config>(staticJson, DefaultJsonOptions)
            ?? throw new JsonException($"Failed to deserialize static configuration from {resolvedConfigPath}");

        _logger?.LogInformation("Loaded static configuration from: {ConfigPath}", resolvedConfigPath);

        // Sync static config to global directory if loaded from current directory
        // This ensures portability - user can run CLI commands from any directory
        var currentDirConfigPath = Path.Combine(Environment.CurrentDirectory, configPath);
        bool loadedFromCurrentDir = Path.GetFullPath(resolvedConfigPath).Equals(
            Path.GetFullPath(currentDirConfigPath), 
            StringComparison.OrdinalIgnoreCase);
        
        if (loadedFromCurrentDir)
        {
            await SyncConfigToGlobalDirectoryAsync(Path.GetFileName(configPath), staticJson, throwOnError: false);
        }

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

        // Warn if local generated config is stale (only if loading the default state file)
        if (Path.GetFileName(resolvedStatePath).Equals("a365.generated.config.json", StringComparison.OrdinalIgnoreCase))
        {
            WarnIfLocalGeneratedConfigIsStale(actualStatePath, _logger);
        }

        // Load dynamic state if exists (optional)
        if (actualStatePath != null && File.Exists(actualStatePath))
        {
            var stateJson = await File.ReadAllTextAsync(actualStatePath);
            var stateData = JsonSerializer.Deserialize<JsonElement>(stateJson, DefaultJsonOptions);

            // Merge dynamic properties into static config
            MergeDynamicProperties(staticConfig, stateData);
            _logger?.LogInformation("Merged dynamic state from: {StatePath}", actualStatePath);
        }
        else
        {
            _logger?.LogInformation("No dynamic state file found at: {StatePath}", resolvedStatePath);
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

        // Only update in current directory if it already exists
        var currentDirPath = Path.Combine(Environment.CurrentDirectory, statePath);
        if (File.Exists(currentDirPath))
        {
            try
            {
                // Save the state to the local current directory
                await File.WriteAllTextAsync(currentDirPath, json);
                _logger?.LogInformation("Saved dynamic state to: {StatePath}", currentDirPath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to save dynamic state to: {StatePath}", currentDirPath);
                throw;
            }
        }

        // Always sync to global directory for portability
        await SyncConfigToGlobalDirectoryAsync(statePath, json, throwOnError: true);
    }

    /// <inheritdoc />
    public async Task<ValidationResult> ValidateAsync(Agent365Config config)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        ValidateRequired(config.TenantId, nameof(config.TenantId), errors);
        ValidateGuid(config.TenantId, nameof(config.TenantId), errors);

        if (config.NeedDeployment)
        {
            // Validate required static properties
            ValidateRequired(config.SubscriptionId, nameof(config.SubscriptionId), errors);
            ValidateRequired(config.ResourceGroup, nameof(config.ResourceGroup), errors);
            ValidateRequired(config.Location, nameof(config.Location), errors);
            ValidateRequired(config.AppServicePlanName, nameof(config.AppServicePlanName), errors);
            ValidateRequired(config.WebAppName, nameof(config.WebAppName), errors);

            // Validate GUID formats
            ValidateGuid(config.SubscriptionId, nameof(config.SubscriptionId), errors);

            // Validate Azure naming conventions
            ValidateResourceGroupName(config.ResourceGroup, errors);
            ValidateAppServicePlanName(config.AppServicePlanName, errors);
            ValidateWebAppName(config.WebAppName, errors);
        }
        else
        {
            // Only validate bot messaging endpoint
            ValidateRequired(config.MessagingEndpoint, nameof(config.MessagingEndpoint), errors);
            ValidateUrl(config.MessagingEndpoint, nameof(config.MessagingEndpoint), errors);
        }

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
            SubscriptionId = string.Empty,
            ResourceGroup = string.Empty,
            Location = string.Empty,
            AppServicePlanName = string.Empty,
            AppServicePlanSku = "B1", // Default SKU that works for development
            WebAppName = string.Empty,
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
    /// Searches for a config file in multiple standard locations.
    /// </summary>
    /// <param name="fileName">The config file name to search for</param>
    /// <returns>The full path to the config file if found, otherwise null</returns>
    private static string? FindConfigFile(string fileName)
    {
        // 1. Current directory
        var currentDirPath = Path.Combine(Environment.CurrentDirectory, fileName);
        if (File.Exists(currentDirPath))
            return currentDirPath;

        // 2. Global config directory (use consistent path resolution)
        var globalConfigPath = Path.Combine(GetGlobalConfigDirectory(), fileName);
        if (File.Exists(globalConfigPath))
            return globalConfigPath;

        // Not found
        return null;
    }
    
    /// <summary>
    /// Gets the path to the static configuration file (a365.config.json).
    /// Searches current directory first, then global config directory.
    /// </summary>
    /// <returns>Full path if found, otherwise null</returns>
    public static string? GetConfigFilePath()
    {
        return FindConfigFile("a365.config.json");
    }
    
    /// <summary>
    /// Gets the path to the generated configuration file (a365.generated.config.json).
    /// Searches current directory first, then global config directory.
    /// </summary>
    /// <returns>Full path if found, otherwise null</returns>
    public static string? GetGeneratedConfigFilePath()
    {
        return FindConfigFile("a365.generated.config.json");
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
            return element.GetString();

        if (underlyingType == typeof(int))
            return element.GetInt32();

        if (underlyingType == typeof(bool))
            return element.GetBoolean();

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

    private void ValidateRequired(string? value, string propertyName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{propertyName} is required but was not provided.");
        }
    }

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

    private void ValidateResourceGroupName(string? value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        if (value.Length > 90)
        {
            errors.Add("ResourceGroup name must not exceed 90 characters.");
        }

        if (!Regex.IsMatch(value, @"^[a-zA-Z0-9_\-\.()]+$"))
        {
            errors.Add("ResourceGroup name can only contain alphanumeric characters, underscores, hyphens, periods, and parentheses.");
        }
    }

    private void ValidateAppServicePlanName(string? value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        if (value.Length > 40)
        {
            errors.Add("AppServicePlanName must not exceed 40 characters.");
        }

        if (!Regex.IsMatch(value, @"^[a-zA-Z0-9\-]+$"))
        {
            errors.Add("AppServicePlanName can only contain alphanumeric characters and hyphens.");
        }
    }

    private void ValidateWebAppName(string? value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        // Azure App Service names: 2-60 characters (not 64 as sometimes documented)
        // Must contain only alphanumeric characters and hyphens
        // Cannot start or end with a hyphen
        // Must be globally unique
        
        if (value.Length < 2 || value.Length > 60)
        {
            errors.Add($"WebAppName must be between 2 and 60 characters (currently {value.Length} characters).");
        }

        // Check for invalid characters (only alphanumeric and hyphens allowed)
        if (!Regex.IsMatch(value, @"^[a-zA-Z0-9\-]+$"))
        {
            errors.Add("WebAppName can only contain alphanumeric characters and hyphens (no underscores or other special characters).");
        }

        // Check if starts or ends with hyphen
        if (value.StartsWith('-') || value.EndsWith('-'))
        {
            errors.Add("WebAppName cannot start or end with a hyphen.");
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