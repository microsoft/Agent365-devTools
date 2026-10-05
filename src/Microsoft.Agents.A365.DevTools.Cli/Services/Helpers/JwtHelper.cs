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

    /// <summary>
    /// Attempts to decode a space-delimited claim (e.g. the v2.0 <c>scp</c> delegated-scope
    /// claim) into a case-insensitive set. Malformed tokens, invalid JSON, absent claims, and
    /// non-string claims return a failure reason instead of being conflated with an empty claim.
    /// </summary>
    internal static bool TryDecodeSpaceDelimitedClaim(
        string? jwt,
        string claimName,
        out HashSet<string> values,
        out string failureReason)
    {
        values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(jwt))
        {
            failureReason = "The acquired access token was empty.";
            return false;
        }

        var parts = jwt.Split('.');
        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[1]))
        {
            failureReason = "The acquired access token is not a valid compact JWT.";
            return false;
        }

        byte[] payloadBytes;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            payloadBytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            failureReason = "The acquired access token contains an invalid Base64Url payload.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadBytes);
        }
        catch (JsonException)
        {
            failureReason = "The acquired access token payload is not valid JSON.";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                failureReason = "The acquired access token payload is not a JSON object.";
                return false;
            }

            if (!document.RootElement.TryGetProperty(claimName, out var claim))
            {
                failureReason = $"The acquired access token does not contain a '{claimName}' claim.";
                return false;
            }

            if (claim.ValueKind != JsonValueKind.String)
            {
                failureReason = $"The acquired access token '{claimName}' claim is not a string.";
                return false;
            }

            var raw = claim.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                values = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        failureReason = string.Empty;
        return true;
    }

    /// <summary>
    /// Returns whether <paramref name="claimName"/> is present in the JWT payload, or null when
    /// the token cannot be decoded — an undecodable token proves nothing about the claim.
    /// </summary>
    internal static bool? ClaimExists(string? jwt, string claimName)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1])) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(claimName, out _);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
