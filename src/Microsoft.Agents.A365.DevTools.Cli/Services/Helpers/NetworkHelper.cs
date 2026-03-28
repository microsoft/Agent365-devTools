// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Sockets;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Shared network diagnostics helpers used by ArmApiService and GraphApiService.
/// </summary>
internal static class NetworkHelper
{
    /// <summary>
    /// Returns true when the exception chain contains a SocketException with ConnectionReset (10054).
    /// This error occurs on corporate networks where a TLS inspection proxy (Zscaler, Netskope, etc.)
    /// drops long-running connections. Logging a targeted warning helps users self-diagnose and retry.
    /// </summary>
    internal static bool IsConnectionResetByProxy(Exception ex)
    {
        var inner = ex.InnerException;
        while (inner != null)
        {
            if (inner is SocketException se && se.SocketErrorCode == SocketError.ConnectionReset)
                return true;
            inner = inner.InnerException;
        }
        return ex is SocketException root && root.SocketErrorCode == SocketError.ConnectionReset;
    }

    internal const string ConnectionResetWarning =
        "Network connection was reset by a remote host (SocketError 10054). " +
        "This often occurs on corporate networks with TLS inspection proxies. " +
        "Re-running the command usually succeeds.";
}
