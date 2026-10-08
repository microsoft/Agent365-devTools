// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;

/// <summary>
/// Known out-of-box (first-party) Dataverse MCP server names, mirrored by name from the MCP
/// platform's OOBDataverseServerNamesToScopeMapping (the source of truth in bap-microsoft/MCP-Platform).
/// The platform fronts these with its own first-party Entra app, so publishing them needs no A365
/// proxy app, secret, or Power Platform connector. Matched exactly (case-insensitive), not by prefix:
/// the platform keys off exact membership, and a custom server merely named <c>msdyn_*</c> is treated
/// as custom. If the lists drift, publish still creates then reconciles away an unused proxy app, so
/// the mismatch costs a round-trip rather than breaking the connector.
/// </summary>
internal static class FirstPartyMcpServers
{
    private static readonly HashSet<string> OobDataverseServerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "msdyn_SalesMCPServer",
        "msdyn_ServiceMCPServer",
        "msdyn_ERPAnalyticsMCPServer",
        "msdyn_D365ContactCenterAdminMCPServer",
        "msdyn_ContactCenterMCPServer",
        "MCP_DataverseMCPServer",
        "msdyn_DataverseMCPServer",
        "msdyn_DataversePreviewMCPServer",
        "msdyn_CIMCPServer",
        "msdyn_FnOMCPServer",
    };

    /// <summary>
    /// Returns true when <paramref name="serverName"/> is a known out-of-box Dataverse MCP server
    /// the platform fronts with its own first-party app (so no A365 proxy app/connector is needed).
    /// </summary>
    internal static bool IsOobDataverseServer(string? serverName) =>
        !string.IsNullOrWhiteSpace(serverName) && OobDataverseServerNames.Contains(serverName);
}
