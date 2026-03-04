// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Represents custom API permissions to be granted to the agent blueprint.
/// These permissions are in addition to the standard permissions required for agent operation.
/// </summary>
public class CustomResourcePermission
{
    /// <summary>
    /// Application ID of the resource API (e.g., Microsoft Graph, custom API).
    /// Must be a valid GUID format.
    /// </summary>
    [JsonPropertyName("resourceAppId")]
    public string ResourceAppId { get; set; } = string.Empty;

    /// <summary>
    /// Optional display name of the resource for logging and tracking.
    /// If not provided, will be auto-resolved during setup from Azure.
    /// Used in configuration output and error messages.
    /// </summary>
    [JsonPropertyName("resourceName")]
    public string? ResourceName { get; set; }

    /// <summary>
    /// List of delegated permission scopes to grant (e.g., "Presence.ReadWrite", "Files.Read.All").
    /// These are OAuth2 delegated permissions that allow the blueprint to act on behalf of users.
    /// </summary>
    private List<string> _scopes = new();
    [JsonPropertyName("scopes")]
    public List<string> Scopes
    {
        get => _scopes;
        set => _scopes = value != null
            ? value.Select(s => s?.Trim() ?? string.Empty).ToList()
            : new(); // Null protection and whitespace normalization at boundary
    }

    /// <summary>
    /// Validates the custom resource permission configuration.
    /// </summary>
    /// <returns>Tuple indicating if validation passed and list of error messages if any.</returns>
    public (bool isValid, List<string> errors) Validate()
    {
        var errors = new List<string>();

        // Validate resourceAppId
        if (string.IsNullOrWhiteSpace(ResourceAppId))
        {
            errors.Add("resourceAppId is required");
        }
        else if (!Guid.TryParse(ResourceAppId, out _))
        {
            errors.Add($"resourceAppId must be a valid GUID format: {ResourceAppId}");
        }

        // ResourceName is optional - will be auto-resolved during setup if not provided

        // Validate scopes
        if (Scopes.Count == 0)
        {
            errors.Add("At least one scope is required");
        }
        else
        {
            // Check for empty or whitespace-only scopes
            var emptyScopes = Scopes
                .Select((scope, index) => new { scope, index })
                .Where(x => string.IsNullOrWhiteSpace(x.scope))
                .ToList();

            if (emptyScopes.Any())
            {
                var indices = string.Join(", ", emptyScopes.Select(x => x.index));
                errors.Add($"Scopes cannot contain empty values (indices: {indices})");
            }

            // Check for duplicate scopes (case-insensitive)
            var duplicateScopes = Scopes
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .GroupBy(s => s.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateScopes.Any())
            {
                errors.Add($"Duplicate scopes found: {string.Join(", ", duplicateScopes)}");
            }
        }

        return (errors.Count == 0, errors);
    }

    /// <summary>
    /// Adds a new permission or updates the scopes of an existing one in the given list.
    /// </summary>
    /// <returns>True if the permission was added; false if an existing entry was updated.</returns>
    public static bool AddOrUpdate(
        List<CustomResourcePermission> permissions,
        string resourceAppId,
        List<string> scopes)
    {
        var existing = permissions.FirstOrDefault(
            p => p.ResourceAppId.Equals(resourceAppId, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.Scopes = scopes;
            return false;
        }

        permissions.Add(new CustomResourcePermission
        {
            ResourceAppId = resourceAppId,
            ResourceName = null,
            Scopes = scopes
        });
        return true;
    }
}
