namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Provides delegated access tokens for Microsoft Graph via PowerShell authentication.
/// </summary>
public interface IMicrosoftGraphTokenProvider
{
    /// <summary>
    /// Acquires a delegated access token for Microsoft Graph using PowerShell authentication.
    /// </summary>
    /// <param name="tenantId">The Azure AD tenant ID (GUID or domain name).</param>
    /// <param name="scopes">The permission scopes to request.</param>
    /// <param name="useDeviceCode">If true, uses device code flow (CLI-friendly). If false, uses interactive browser flow.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The access token, or null if acquisition fails.</returns>
    Task<string?> GetMgGraphAccessTokenAsync(
        string tenantId,
        IEnumerable<string> scopes,
        bool useDeviceCode = true,
        CancellationToken ct = default);
}
