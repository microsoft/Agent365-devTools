// Copyright (c) Microsoft Corporation.  
// Licensed under the MIT License.  
using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Model representing a Dataverse environment
/// </summary>
public class DataverseEnvironment
{
    /// <summary>
    /// The unique identifier for the environment
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// The display name of the environment
    /// </summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// The type of environment (e.g., Production, Developer, Sandbox, Default)
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// The URL for accessing the environment
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// The tenant ID associated with the environment
    /// </summary>
    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    /// <summary>
    /// The geographical region where the environment is hosted (e.g., "unitedstates")
    /// </summary>
    [JsonPropertyName("geo")]
    public string? Geo { get; set; }

    /// <summary>
    /// Gets the environment ID
    /// </summary>
    public string? GetEnvironmentId() => Id;

    /// <summary>
    /// Validates that the environment has required fields
    /// </summary>
    /// <returns>True if valid, false otherwise</returns>
    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(Id);
    }

    /// <summary>
    /// Gets a display-friendly string representation of the environment
    /// </summary>
    /// <returns>Formatted string with environment name and ID</returns>
    public override string ToString()
    {
        var envId = Id ?? "Unknown";
        var envName = DisplayName ?? "Unknown";
        var envType = Type ?? "Unknown";
        return $"{envName} ({envType}) - {envId}";
    }
}

/// <summary>
/// Response model for the list-environments endpoint
/// </summary>
public class DataverseEnvironmentsResponse
{
    /// <summary>
    /// Status of the API call (e.g., "Success")
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// Message describing the result
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    /// Array of Dataverse environments
    /// </summary>
    [JsonPropertyName("environments")]
    public DataverseEnvironment[] Environments { get; set; } = Array.Empty<DataverseEnvironment>();

    /// <summary>
    /// Timestamp of the response
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; set; }
}
