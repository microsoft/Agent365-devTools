// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Selects which identity a Microsoft Graph request authenticates as.
/// </summary>
public enum GraphAuthenticationMode
{
    /// <summary>
    /// Authenticate as the resolved client app (<see cref="GraphApiService.CustomClientAppId"/>)
    /// when it is available, otherwise fall back to the ambient bootstrap identity.
    /// </summary>
    ResolvedClientApp = 0,

    /// <summary>
    /// Force the ambient bootstrap identity, ignoring the resolved client app and any requested
    /// scopes. Required when reading or repairing a client app registration so an underconfigured
    /// app does not need to authorize its own diagnosis.
    /// </summary>
    Ambient = 1,

    /// <summary>
    /// Authenticate as the Microsoft Graph Command Line Tools app with the fixed
    /// <see cref="Constants.AuthenticationConstants.McpAgentAccessScopes"/> set, for the
    /// 'develop-mcp grant-agents-access' flow. That app is preauthorized for Graph delegated
    /// scopes; <see cref="Ambient"/> resolves to an app that is not, so its '.default' token
    /// omits these scopes and the calls 403.
    /// </summary>
    McpAgentAccess = 2
}
