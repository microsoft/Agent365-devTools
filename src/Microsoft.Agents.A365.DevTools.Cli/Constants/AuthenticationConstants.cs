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
    /// Localhost redirect URI for interactive browser authentication
    /// </summary>
    public const string LocalhostRedirectUri = "http://localhost";

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
}
