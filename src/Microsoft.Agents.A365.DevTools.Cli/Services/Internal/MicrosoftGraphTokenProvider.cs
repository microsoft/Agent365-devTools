// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Implements Microsoft Graph token acquisition via PowerShell Microsoft.Graph module.
/// </summary>
public sealed class MicrosoftGraphTokenProvider : IMicrosoftGraphTokenProvider, IDisposable
{
    private readonly CommandExecutor _executor;
    private readonly ILogger<MicrosoftGraphTokenProvider> _logger;

    // Cache tokens per (tenant + clientId + scopes) for the lifetime of this CLI process.
    // This reduces repeated Connect-MgGraph prompts in setup flows.
    private readonly ConcurrentDictionary<string, CachedToken> _tokenCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    
    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresOnUtc);

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _locks)
        {
            try 
            { 
                kvp.Value.Dispose(); 
            }
            catch (Exception ex)
            { 
                _logger.LogDebug(ex, "Failed to dispose semaphore for key '{Key}' in MicrosoftGraphTokenProvider.", kvp.Key); 
            }
        }

        _locks.Clear();
        _tokenCache.Clear();
    }

    public MicrosoftGraphTokenProvider(
        CommandExecutor executor,
        ILogger<MicrosoftGraphTokenProvider> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> GetMgGraphAccessTokenAsync(
        string tenantId,
        IEnumerable<string> scopes,
        bool useDeviceCode = true,
        string? clientAppId = null,
        CancellationToken ct = default)
    {
        var validatedScopes = ValidateAndPrepareScopes(scopes);
        ValidateTenantId(tenantId);
        
        if (!string.IsNullOrWhiteSpace(clientAppId))
        {
            ValidateClientAppId(clientAppId);
        }

        var cacheKey = MakeCacheKey(tenantId, validatedScopes, clientAppId);
        var tokenExpirationMinutes = AuthenticationConstants.TokenExpirationBufferMinutes;

        // Fast path: cached + not expiring soon
        if (_tokenCache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresOnUtc > DateTimeOffset.UtcNow.AddMinutes(tokenExpirationMinutes) &&
            !string.IsNullOrWhiteSpace(cached.AccessToken))
        {
            _logger.LogDebug("Reusing cached Graph token for key {Key} expiring at {Exp}",
                cacheKey, cached.ExpiresOnUtc);
            return cached.AccessToken;
        }

        // Single-flight: only one PowerShell auth per key at a time
        var gate = _locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-check inside lock
            if (_tokenCache.TryGetValue(cacheKey, out cached) &&
                cached.ExpiresOnUtc > DateTimeOffset.UtcNow.AddMinutes(tokenExpirationMinutes) &&
                !string.IsNullOrWhiteSpace(cached.AccessToken))
            {
                _logger.LogDebug("Reusing cached Graph token (post-lock) for key {Key} expiring at {Exp}",
                    cacheKey, cached.ExpiresOnUtc);
                return cached.AccessToken;
            }

            _logger.LogInformation(
                "Acquiring Microsoft Graph delegated access token via PowerShell (Device Code: {UseDeviceCode})",
                useDeviceCode);

            var script = BuildPowerShellScript(tenantId, validatedScopes, useDeviceCode, clientAppId);
            var result = await ExecuteWithFallbackAsync(script, ct);
            var token = ProcessResult(result);

            // If authentication failed with WAM redirect URI error and using custom client app,
            // automatically retry with device code flow
            if (string.IsNullOrWhiteSpace(token) && 
                !useDeviceCode && 
                !string.IsNullOrWhiteSpace(clientAppId) &&
                IsWamRedirectUriError(result))
            {
                _logger.LogWarning(
                    "Authentication failed with WAM redirect URI error for custom client app. " +
                    "Retrying with device code authentication...");
                
                var deviceCodeScript = BuildPowerShellScript(tenantId, validatedScopes, useDeviceCode: true, clientAppId);
                result = await ExecuteWithFallbackAsync(deviceCodeScript, ct);
                token = ProcessResult(result);
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            // Cache expiry from JWT exp; if parsing fails, cache short (10 min) to still reduce spam
            if (!TryGetJwtExpiryUtc(token, out var expUtc))
            {
                expUtc = DateTimeOffset.UtcNow.AddMinutes(10);
                _logger.LogDebug("Could not parse JWT exp; caching token for a short duration until {Exp}", expUtc);
            }

            _tokenCache[cacheKey] = new CachedToken(token, expUtc);
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    private string[] ValidateAndPrepareScopes(IEnumerable<string> scopes)
    {
        if (scopes == null)
            throw new ArgumentNullException(nameof(scopes));

        var validScopes = scopes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (validScopes.Length == 0)
            throw new ArgumentException("At least one scope is required", nameof(scopes));

        foreach (var scope in validScopes)
        {
            if (CommandStringHelper.ContainsDangerousCharacters(scope))
                throw new ArgumentException(
                    $"Scope contains invalid characters: {scope}",
                    nameof(scopes));
        }

        return validScopes;
    }

    private static void ValidateTenantId(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentNullException(nameof(tenantId));

        if (!IsValidTenantId(tenantId))
            throw new ArgumentException(
                "Tenant ID must be a valid GUID or domain name",
                nameof(tenantId));
    }

    private static void ValidateClientAppId(string clientAppId)
    {
        if (!Guid.TryParse(clientAppId, out _))
            throw new ArgumentException(
                "Client App ID must be a valid GUID format",
                nameof(clientAppId));
    }

    private static string BuildPowerShellScript(string tenantId, string[] scopes, bool useDeviceCode, string? clientAppId = null)
    {
        var escapedTenantId = CommandStringHelper.EscapePowerShellString(tenantId);
        var scopesArray = BuildScopesArray(scopes);

        // Use -UseDeviceCode for CLI-friendly authentication (no browser popup/download)
        var authMethod = useDeviceCode ? "-UseDeviceCode" : "";
        
        // Include -ClientId parameter if provided (ensures authentication uses the custom client app)
        // Add leading space only when parameter is present to avoid double spaces
        var clientIdParam = !string.IsNullOrWhiteSpace(clientAppId) 
            ? $" -ClientId '{CommandStringHelper.EscapePowerShellString(clientAppId)}'" 
            : "";

        // Workaround for older Microsoft.Graph versions that don't have Get-MgAccessToken
        // We make a dummy Graph request and extract the token from the Authorization header
        return
            $"Import-Module Microsoft.Graph.Authentication -ErrorAction Stop; " +
            $"Connect-MgGraph -TenantId '{escapedTenantId}'{clientIdParam} -Scopes {scopesArray} {authMethod} -NoWelcome -ErrorAction Stop; " +
            $"$ctx = Get-MgContext; " +
            $"if ($null -eq $ctx) {{ throw 'Failed to establish Graph context' }}; " +
            // Try to get token directly if available (newer versions)
            $"if ($ctx.PSObject.Properties.Name -contains 'AccessToken' -and -not [string]::IsNullOrWhiteSpace($ctx.AccessToken)) {{ " +
            $"  $ctx.AccessToken " +
            $"}} else {{ " +
            // Fallback: Extract token from a test Graph request (older versions)
            $"  $response = Invoke-MgGraphRequest -Method GET -Uri 'https://graph.microsoft.com/v1.0/$metadata' -OutputType HttpResponseMessage -ErrorAction Stop; " +
            $"  $token = $response.RequestMessage.Headers.Authorization.Parameter; " +
            $"  if ([string]::IsNullOrWhiteSpace($token)) {{ throw 'Failed to extract access token from request' }}; " +
            $"  $token " +
            $"}}";
    }

    private static string BuildScopesArray(string[] scopes)
    {
        var escapedScopes = scopes.Select(s => $"'{CommandStringHelper.EscapePowerShellString(s)}'");
        return $"@({string.Join(",", escapedScopes)})";
    }

    private async Task<CommandResult> ExecuteWithFallbackAsync(
        string script,
        CancellationToken ct)
    {
        // Try PowerShell Core first (cross-platform)
        var result = await ExecutePowerShellAsync("pwsh", script, ct);

        // Fallback to Windows PowerShell if pwsh is not available
        if (!result.Success && IsPowerShellNotFoundError(result))
        {
            _logger.LogDebug("PowerShell Core not found, falling back to Windows PowerShell");
            result = await ExecutePowerShellAsync("powershell", script, ct);
        }

        return result;
    }

    private async Task<CommandResult> ExecutePowerShellAsync(
        string shell,
        string script,
        CancellationToken ct)
    {
        var arguments = BuildPowerShellArguments(shell, script);

        return await _executor.ExecuteWithStreamingAsync(
            command: shell,
            arguments: arguments,
            workingDirectory: null,
            outputPrefix: "",
            interactive: true,
            cancellationToken: ct);
    }

    private static string BuildPowerShellArguments(string shell, string script)
    {
        var baseArgs = shell == "pwsh"
            ? "-NoProfile -NonInteractive"
            : "-NoLogo -NoProfile -NonInteractive";

        var wrappedScript = $"try {{ {script} }} catch {{ Write-Error $_.Exception.Message; exit 1 }}";

        return $"{baseArgs} -Command \"{wrappedScript}\"";
    }

    private string? ProcessResult(CommandResult result)
    {
        if (!result.Success)
        {
            _logger.LogError(
                "Failed to acquire Microsoft Graph access token. Error: {Error}",
                result.StandardError);
            return null;
        }

        var token = result.StandardOutput?.Trim();

        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("PowerShell succeeded but returned empty output");
            return null;
        }

        if (!IsValidJwtFormat(token))
        {
            _logger.LogWarning("Returned token does not appear to be a valid JWT");
        }

        _logger.LogInformation("Microsoft Graph access token acquired successfully");
        return token;
    }

    private static bool IsPowerShellNotFoundError(CommandResult result)
    {
        if (string.IsNullOrWhiteSpace(result.StandardError))
            return false;

        var error = result.StandardError;
        return error.Contains("not recognized", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("No such file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWamRedirectUriError(CommandResult result)
    {
        if (string.IsNullOrWhiteSpace(result.StandardError))
            return false;

        var error = result.StandardError;
        // Check for AADSTS50011 error code (redirect URI mismatch)
        // and WAM broker plugin references
        return (error.Contains("AADSTS50011", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("redirect URI", StringComparison.OrdinalIgnoreCase)) &&
               (error.Contains("ms-appx-web://Microsoft.AAD.BrokerPlugin", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("BrokerPlugin", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsValidTenantId(string tenantId)
    {
        // GUID format
        if (Guid.TryParse(tenantId, out _))
            return true;

        // Domain name format (basic validation)
        return tenantId.Contains('.') &&
               tenantId.Length <= 253 &&
               !CommandStringHelper.ContainsDangerousCharacters(tenantId);
    }

    private static bool IsValidJwtFormat(string token)
    {
        // JWT tokens have three base64 parts separated by dots
        // Header typically starts with "eyJ" when base64-decoded
        return token.StartsWith("eyJ", StringComparison.Ordinal) &&
               token.Count(c => c == '.') == 2;
    }

    private static string MakeCacheKey(string tenantId, IEnumerable<string> scopes, string? clientAppId)
    {
        var scopeKey = string.Join(" ", scopes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

        return $"{tenantId}::{clientAppId ?? ""}::{scopeKey}";
    }

    private bool TryGetJwtExpiryUtc(string jwt, out DateTimeOffset expiresOnUtc)
    {
        expiresOnUtc = default;

        if (string.IsNullOrWhiteSpace(jwt)) return false;

        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return false;

            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);

            if (!doc.RootElement.TryGetProperty("exp", out var expEl)) return false;
            if (expEl.ValueKind != JsonValueKind.Number) return false;

            // exp is seconds since Unix epoch
            var expSeconds = expEl.GetInt64();
            expiresOnUtc = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse JWT expiry (exp) from access token.");
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        // Base64Url decode with padding fix
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}