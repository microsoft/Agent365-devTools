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
    /// scopes. Required whenever the operation reads or repairs tenant directory objects for the
    /// client app: a token issued for that app carries only its own default scope, so Graph
    /// refuses application and servicePrincipal queries. Note that requested scopes are silently
    /// discarded on this path, so callers needing a specific scope must not use it.
    /// </summary>
    Ambient = 1
}
