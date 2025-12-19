// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;

/// <summary>
/// Constants for authentication and security operations
/// </summary>
public static class AuthenticationConstants
{
    /// <summary>
    /// Azure CLI public client ID (well-known, not a secret)
    /// This is a Microsoft first-party app ID that's publicly documented
    /// </summary>
    public const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    public const string PowershellClientId = "1950a258-227b-4e31-a9cf-717495945fc2";

    /// <summary>
    /// Common tenant ID for multi-tenant authentication
    /// </summary>
    public const string CommonTenantId = "common";

    /// <summary>
    /// Localhost redirect URI for interactive browser authentication.
    /// Uses a fixed port (8400) to ensure consistent OAuth callbacks across multiple
    /// authentication attempts. Users must configure this exact URI in their custom
    /// client app registration: http://localhost:8400/
    /// </summary>
    public const string LocalhostRedirectUri = "http://localhost:8400/";

    /// <summary>
    /// Required redirect URIs for Microsoft Graph PowerShell SDK authentication.
    /// The SDK requires both http://localhost and http://localhost:8400/ for different auth flows.
    /// </summary>
    public static readonly string[] RequiredRedirectUris = new[]
    {
        "http://localhost",
        "http://localhost:8400/"
    };

    /// <summary>
    /// Application name for cache directory
    /// </summary>
    public const string ApplicationName = "Microsoft.Agents.A365.DevTools.Cli";

    /// <summary>
    /// Token cache file name
    /// </summary>
    public const string TokenCacheFileName = "auth-token.json";

    /// <summary>
    /// Token expiration buffer in minutes
    /// Tokens are considered expired this many minutes before actual expiration
    /// to prevent using tokens that expire during a request
    /// </summary>
    public const int TokenExpirationBufferMinutes = 5;

    /// <summary>
    /// Microsoft Graph resource app ID (well-known constant)
    /// Used to identify Microsoft Graph API in permission requests
    /// </summary>
    public const string MicrosoftGraphResourceAppId = "00000003-0000-0000-c000-000000000000";

    /// <summary>
    /// Required delegated permissions for the custom client app used by a365 CLI.
    /// These permissions enable the CLI to manage Entra ID applications and agent blueprints.
    /// All permissions require admin consent.
    /// 
    /// Permission GUIDs are resolved dynamically at runtime from Microsoft Graph to ensure
    /// compatibility across different tenants and API versions.
    /// </summary>
    public static readonly string[] RequiredClientAppPermissions = new[]
    {
        "Application.ReadWrite.All",
        "AgentIdentityBlueprint.ReadWrite.All",
        "AgentIdentityBlueprint.UpdateAuthProperties.All",
        "DelegatedPermissionGrant.ReadWrite.All",
        "Directory.Read.All"
    };

    /// <summary>
    /// Environment variable name for bearer token used in local development.
    /// This token is stored in .env files (Python/Node.js) or launchSettings.json (.NET)
    /// for testing purposes only. It should NOT be deployed to production Azure environments.
    /// </summary>
    public const string BearerTokenEnvironmentVariable = "BEARER_TOKEN";
}
