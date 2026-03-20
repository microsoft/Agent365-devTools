// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;

/// <summary>
/// Constants for Microsoft Power Platform API authentication and permissions
/// </summary>
public static class PowerPlatformConstants
{
    /// <summary>
    /// Power Platform API resource app ID
    /// </summary>
    public const string PowerPlatformApiResourceAppId = "8578e004-a5c6-46e7-913e-12f58912df43";

    /// <summary>
    /// Power Platform API identifier URI (used for admin consent URL construction).
    /// </summary>
    public const string PowerPlatformApiIdentifierUri = "https://api.powerplatform.com";

    /// <summary>
    /// Delegated permission scope names for resource applications.
    /// </summary>
    public static class PermissionNames
    {
        /// <summary>
        /// Power Platform API - CopilotStudio.Copilots.Invoke permission scope name
        /// </summary>
        public const string PowerPlatformCopilotStudioInvoke = "CopilotStudio.Copilots.Invoke";
    }
}
