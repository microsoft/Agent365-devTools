// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Input model for the register-external-mcp-server command when reading parameters from a JSON file.
/// </summary>
public class RegisterExternalMcpServerInput
{
    /// <summary>
    /// MCP server name (max 20 chars, must start with 'ext_', e.g. ext_MyServer).
    /// </summary>
    [JsonPropertyName("serverName")]
    public string? ServerName { get; set; }

    /// <summary>
    /// Remote MCP server URL
    /// </summary>
    [JsonPropertyName("serverUrl")]
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Authentication type: EntraOAuth, ExternalOAuth, APIKey, or NoAuth
    /// </summary>
    [JsonPropertyName("authType")]
    public string? AuthType { get; set; }

    /// <summary>
    /// Server description (used in MOS package metadata)
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Publisher name (used in MOS package metadata)
    /// </summary>
    [JsonPropertyName("publisherName")]
    public string? PublisherName { get; set; }

    /// <summary>
    /// List of tools with names and descriptions exposed by this server
    /// </summary>
    [JsonPropertyName("tools")]
    public List<ToolEntry>? Tools { get; set; }

    /// <summary>
    /// Scopes for the remote MCP server (e.g., 'api://myapp/.default')
    /// </summary>
    [JsonPropertyName("remoteScopes")]
    public string? RemoteScopes { get; set; }

    /// <summary>
    /// Entra tenant ID for app registration
    /// </summary>
    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    /// <summary>
    /// ServiceTree ID for Entra app registration (required in Microsoft corporate tenants)
    /// </summary>
    [JsonPropertyName("serviceTreeId")]
    public string? ServiceTreeId { get; set; }

    /// <summary>
    /// Lifetime (in months) of generated client secrets on the Entra apps created for this registration.
    /// When null the Graph default (2 years) is used. Set a smaller value when the target tenant
    /// enforces an appManagementPolicies cap that the default exceeds. Valid range: 1-24 (Graph's max).
    /// </summary>
    [JsonPropertyName("secretLifetimeMonths")]
    public int? SecretLifetimeMonths { get; set; }

    /// <summary>
    /// External OAuth configuration (required when authType is ExternalOAuth)
    /// </summary>
    [JsonPropertyName("externalOAuth")]
    public ExternalOAuthInput? ExternalOAuth { get; set; }

    /// <summary>
    /// API key configuration (required when authType is APIKey)
    /// </summary>
    [JsonPropertyName("apiKey")]
    public ApiKeyInput? ApiKey { get; set; }

    /// <summary>
    /// Represents a tool entry with a name and description
    /// </summary>
    public class ToolEntry
    {
        /// <summary>
        /// Tool name
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Tool description
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    /// <summary>
    /// External OAuth configuration details
    /// </summary>
    public class ExternalOAuthInput
    {
        /// <summary>
        /// External OAuth authorization URL
        /// </summary>
        [JsonPropertyName("authorizationUrl")]
        public string? AuthorizationUrl { get; set; }

        /// <summary>
        /// External OAuth token URL
        /// </summary>
        [JsonPropertyName("tokenUrl")]
        public string? TokenUrl { get; set; }

        /// <summary>
        /// External OAuth scopes
        /// </summary>
        [JsonPropertyName("scopes")]
        public string? Scopes { get; set; }

        /// <summary>
        /// External OAuth client ID
        /// </summary>
        [JsonPropertyName("clientId")]
        public string? ClientId { get; set; }

        /// <summary>
        /// External OAuth client secret
        /// </summary>
        [JsonPropertyName("clientSecret")]
        public string? ClientSecret { get; set; }
    }

    /// <summary>
    /// API key configuration details
    /// </summary>
    public class ApiKeyInput
    {
        /// <summary>
        /// API key location: Header or Query
        /// </summary>
        [JsonPropertyName("location")]
        public string? Location { get; set; }

        /// <summary>
        /// API key parameter/header name (e.g., 'X-API-Key' or 'token')
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
