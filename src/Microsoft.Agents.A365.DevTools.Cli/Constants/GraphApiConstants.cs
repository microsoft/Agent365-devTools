// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;

/// <summary>
/// Constants for Microsoft Graph API endpoints and resources
/// </summary>
public static class GraphApiConstants
{
    /// <summary>
    /// Base URL for Microsoft Graph API
    /// </summary>
    public const string BaseUrl = "https://graph.microsoft.com";

    /// <summary>
    /// Resource identifier for Microsoft Graph API (used in Azure CLI token acquisition)
    /// </summary>
    public const string Resource = "https://graph.microsoft.com/";

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
    /// Common Microsoft Graph permission scopes
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
