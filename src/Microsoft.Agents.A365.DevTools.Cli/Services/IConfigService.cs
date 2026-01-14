// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for loading, saving, and validating Microsoft Agent 365 configuration.
/// 
/// DESIGN PATTERN: Handles merge (load) and split (save) of two-file config model
/// - LoadAsync: Merges a365.config.json + a365.generated.config.json to single Agent365Config
/// - SaveStateAsync: Extracts dynamic properties to writes to a365.generated.config.json
/// - ValidateAsync: Validates both static and dynamic configuration
/// </summary>
public interface IConfigService
{
    /// <summary>
    /// Loads and merges static configuration (a365.config.json) and dynamic state (a365.generated.config.json)
    /// into a single Agent365Config object.
    /// </summary>
    /// <param name="configPath">Path to the static configuration file (default: a365.config.json)</param>
    /// <param name="statePath">Path to the generated state file (default: a365.generated.config.json)</param>
    /// <returns>Merged configuration object with both static (init) and dynamic (get/set) properties</returns>
    /// <exception cref="FileNotFoundException">Thrown when configPath doesn't exist</exception>
    /// <exception cref="JsonException">Thrown when JSON parsing fails</exception>
    /// <exception cref="ValidationException">Thrown when configuration validation fails</exception>
    Task<Agent365Config> LoadAsync(
        string configPath = ConfigConstants.DefaultConfigFileName,
        string statePath = ConfigConstants.DefaultStateFileName);

    /// <summary>
    /// Saves only the dynamic properties (get/set) from the config object to the generated state file.
    /// Static properties (init-only) are NOT saved as they should only be modified in a365.config.json.
    /// </summary>
    /// <param name="config">Configuration object containing both static and dynamic properties</param>
    /// <param name="statePath">Path to the generated state file (default: a365.generated.config.json)</param>
    /// <exception cref="IOException">Thrown when file write fails</exception>
    /// <exception cref="JsonException">Thrown when JSON serialization fails</exception>
    Task SaveStateAsync(
        Agent365Config config,
        string statePath = "a365.generated.config.json");

    /// <summary>
    /// Validates the configuration object, checking required properties, formats, and business rules.
    /// </summary>
    /// <param name="config">Configuration object to validate</param>
    /// <returns>Validation result with success/failure and error messages</returns>
    Task<ValidationResult> ValidateAsync(Agent365Config config);

    /// <summary>
    /// Checks if the static configuration file exists.
    /// </summary>
    /// <param name="configPath">Path to the static configuration file</param>
    /// <returns>True if file exists, false otherwise</returns>
    Task<bool> ConfigExistsAsync(string configPath = "a365.config.json");

    /// <summary>
    /// Checks if the generated state file exists.
    /// </summary>
    /// <param name="statePath">Path to the generated state file</param>
    /// <returns>True if file exists, false otherwise</returns>
    Task<bool> StateExistsAsync(string statePath = "a365.generated.config.json");

    /// <summary>
    /// Creates a new static configuration file with default/template values.
    /// Useful for initialization scenarios.
    /// </summary>
    /// <param name="configPath">Path where the configuration file should be created</param>
    /// <param name="templateConfig">Optional template config to use instead of defaults</param>
    /// <exception cref="IOException">Thrown when file already exists or write fails</exception>
    Task CreateDefaultConfigAsync(
        string configPath = "a365.config.json",
        Agent365Config? templateConfig = null);

    /// <summary>
    /// Initializes an empty generated state file. Typically called during first-time setup.
    /// </summary>
    /// <param name="statePath">Path where the state file should be created</param>
    /// <exception cref="IOException">Thrown when file write fails</exception>
    Task InitializeStateAsync(string statePath = "a365.generated.config.json");

    /// <summary>
    /// Loads and deserializes a team configuration file.
    /// </summary>
    /// <param name="teamConfigPath">Path to the team configuration file (e.g., team.config.json)</param>
    /// <returns>The deserialized team configuration</returns>
    /// <exception cref="FileNotFoundException">If the team config file doesn't exist</exception>
    /// <exception cref="JsonException">If the team config file is malformed</exception>
    Task<TeamConfig> LoadTeamConfigAsync(string teamConfigPath = "team.config.json");

    /// <summary>
    /// Validates a team configuration and returns validation errors.
    /// </summary>
    /// <param name="teamConfig">The team configuration to validate</param>
    /// <returns>List of validation errors (empty if valid)</returns>
    Task<List<string>> ValidateTeamConfigAsync(TeamConfig teamConfig);

    /// <summary>
    /// Merges team agent configuration with shared resources to create a complete Agent365Config.
    /// </summary>
    /// <param name="teamConfig">The team configuration containing shared resources</param>
    /// <param name="agentConfig">The individual agent configuration</param>
    /// <returns>A complete Agent365Config ready for deployment</returns>
    Task<Agent365Config> MergeTeamAgentConfigAsync(TeamConfig teamConfig, TeamAgentConfig agentConfig);
}

/// <summary>
/// Result of configuration validation.
/// </summary>
public class ValidationResult
{
    /// <summary>
    /// Indicates whether validation passed.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// List of validation error messages.
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// List of validation warning messages (non-fatal).
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static ValidationResult Success() => new() { IsValid = true };

    /// <summary>
    /// Creates a failed validation result with error messages.
    /// </summary>
    public static ValidationResult Failure(params string[] errors) => new()
    {
        IsValid = false,
        Errors = errors.ToList()
    };

    /// <summary>
    /// Creates a failed validation result with error messages.
    /// </summary>
    public static ValidationResult Failure(IEnumerable<string> errors) => new()
    {
        IsValid = false,
        Errors = errors.ToList()
    };
}
