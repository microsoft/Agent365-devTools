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
    /// scopes. Required when probing whether a client app exists: authenticating as the app being
    /// probed makes its own absence unverifiable.
    /// </summary>
    Ambient = 1
}
