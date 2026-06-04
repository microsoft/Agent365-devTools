// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Shared helper for decoding JWT token claims.
/// Consolidates the duplicated Base64Url-decode logic that previously existed in
/// AuthenticationService.TryExtractUpnFromJwt, GraphApiService.TryDecodeTokenClaim,
/// and MsalBrowserCredential inline decode blocks.
/// </summary>
internal static class JwtHelper
{
    /// <summary>
    /// Decodes a single claim from the payload of a JWT string.
    /// Returns null if the token is malformed, the claim is absent, or decoding fails.
    /// </summary>
    internal static string? TryDecodeClaim(string? jwt, string claimName)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1];
            // JWT uses Base64Url encoding: restore standard Base64 chars and padding.
            payload = payload.Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.TryGetProperty(claimName, out var claim)
                ? claim.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
