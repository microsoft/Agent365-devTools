// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;

/// <summary>
/// Constants for Microsoft Graph API endpoints and resources.
/// All constants are expressed relative to <see cref="BaseUrl"/> so that
/// sovereign-cloud support only requires changing that one value.
/// </summary>
public static class GraphApiConstants
{
    /// <summary>
    /// Base URL for the Microsoft Graph API (commercial cloud).
    /// Override per-config via <see cref="Models.Agent365Config.GraphBaseUrl"/> for
    /// sovereign clouds: "https://graph.microsoft.us" (GCC High / DoD) or
    /// "https://microsoftgraph.chinacloudapi.cn" (China 21Vianet).
    /// </summary>
    public const string BaseUrl = "https://graph.microsoft.com";

    /// <summary>
    /// Resource identifier used in Azure CLI token acquisition (base URL + trailing slash).
    /// </summary>
    public static string GetResource(string graphBaseUrl) => graphBaseUrl.TrimEnd('/') + "/";

    /// <summary>
    /// Endpoint versions
    /// </summary>
    public static class Versions
    {
        /// <summary>
        /// Stable v1.0 endpoint for production workloads
        /// </summary>
        public const string V1 = "v1.0";

        /// <summary>
        /// Beta endpoint for preview features
        /// </summary>
        public const string Beta = "beta";
    }

    /// <summary>
    /// Builds a fully-qualified Graph URL from a base URL and a relative path.
    /// If <paramref name="relativePath"/> already starts with "http" it is returned unchanged.
    /// </summary>
    public static string BuildUrl(string graphBaseUrl, string relativePath)
        => relativePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativePath
            : $"{graphBaseUrl.TrimEnd('/')}{relativePath}";

    /// <summary>
    /// Common Microsoft Graph permission scopes (commercial cloud defaults).
    /// For sovereign clouds build the scope string via
    /// <c>$"{graphBaseUrl}/Application.ReadWrite.All"</c>.
    /// </summary>
    public static class Scopes
    {
        /// <summary>
        /// Application.ReadWrite.All permission scope - required for managing application registrations
        /// </summary>
        public const string ApplicationReadWriteAll = "https://graph.microsoft.com/Application.ReadWrite.All";

        /// <summary>
        /// Directory.ReadWrite.All permission scope - required for directory-level operations
        /// </summary>
        public const string DirectoryReadWriteAll = "https://graph.microsoft.com/Directory.ReadWrite.All";
    }
}
