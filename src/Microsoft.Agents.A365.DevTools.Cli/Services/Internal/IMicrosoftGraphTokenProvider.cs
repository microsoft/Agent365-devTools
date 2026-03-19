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
    /// <param name="clientAppId">Optional client app ID to use for authentication. If not provided, uses default Microsoft Graph PowerShell app.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="loginHint">Optional UPN/email of the expected user. When provided, MSAL uses this identity for
    /// both silent cache lookup and interactive auth (WAM/browser), preventing stale cached tokens from a
    /// different user contaminating this session.</param>
    /// <returns>The access token, or null if acquisition fails.</returns>
    Task<string?> GetMgGraphAccessTokenAsync(
        string tenantId,
        IEnumerable<string> scopes,
        bool useDeviceCode = false,
        string? clientAppId = null,
        CancellationToken ct = default,
        string? loginHint = null);
}
