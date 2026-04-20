// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Implements Microsoft Graph token acquisition via MSAL.NET (primary) with PowerShell fallback.
///
/// AUTHENTICATION METHOD:
/// - Primary: MSAL.NET with WAM on Windows (native broker, no browser, CAP-compliant),
///   system browser on macOS, device code on Linux
/// - Fallback: PowerShell Connect-MgGraph (used when MSAL is unavailable, e.g. no clientAppId)
///
/// WHY MSAL PRIMARY:
/// - WAM authenticates via the OS broker — no browser popup, works on corporate tenants
///   with Conditional Access Policies that block browser-based auth
/// - Token cache is keyed by user identity (HomeAccountId) — prevents cross-user token
///   contamination on shared machines
///
/// TOKEN CACHING:
/// - In-memory cache per CLI process: Tokens cached by (tenant + clientId + scopes)
/// - MSAL persistent cache: DPAPI on Windows, Keychain on macOS, in-memory on Linux
///
/// USAGE:
/// - Called by GraphApiService when specific scopes are required
/// - Integrates with overall CLI authentication strategy (1-2 total prompts)
/// </summary>
public sealed class MicrosoftGraphTokenProvider : IMicrosoftGraphTokenProvider, IDisposable
{
    private readonly CommandExecutor _executor;
    private readonly ILogger<MicrosoftGraphTokenProvider> _logger;

    // Cache tokens per (tenant + clientId + scopes) for the lifetime of this CLI process.
    // This reduces repeated auth prompts during multi-step setup flows.
    private readonly ConcurrentDictionary<string, CachedToken> _tokenCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    // Test seam: override MSAL token acquisition in unit tests without requiring WAM/browser.
    // Null in production; set by tests to return controlled token values.
    internal Func<string, string[], string?, CancellationToken, Task<string?>>? MsalTokenAcquirerOverride { get; set; }
    
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
        bool useDeviceCode = false,
        string? clientAppId = null,
        CancellationToken ct = default,
        string? loginHint = null,
        bool forceRefresh = false)
    {
        var validatedScopes = ValidateAndPrepareScopes(scopes);
        ValidateTenantId(tenantId);

        if (!string.IsNullOrWhiteSpace(clientAppId))
        {
            ValidateClientAppId(clientAppId);
        }

        var cacheKey = MakeCacheKey(tenantId, validatedScopes, clientAppId, loginHint);
        var tokenExpirationMinutes = AuthenticationConstants.TokenExpirationBufferMinutes;

        // When forceRefresh is requested, evict the cached entry so the acquire path always runs.
        if (forceRefresh)
            _tokenCache.TryRemove(cacheKey, out _);

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
            // Re-check inside lock (forceRefresh already evicted the entry above the lock)
            if (_tokenCache.TryGetValue(cacheKey, out cached) &&
                cached.ExpiresOnUtc > DateTimeOffset.UtcNow.AddMinutes(tokenExpirationMinutes) &&
                !string.IsNullOrWhiteSpace(cached.AccessToken))
            {
                _logger.LogDebug("Reusing cached Graph token (post-lock) for key {Key} expiring at {Exp}",
                    cacheKey, cached.ExpiresOnUtc);
                return cached.AccessToken;
            }

            _logger.LogDebug("Acquiring Microsoft Graph delegated access token...");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _logger.LogDebug("A Windows authentication dialog may appear. Complete sign-in, then return here — the CLI will continue automatically.");
            }
            else
            {
                _logger.LogDebug("A browser window or device code prompt may appear. Complete sign-in, then return here — the CLI will continue automatically.");
            }

            // MSAL/WAM is primary: user-identity-aware cache prevents cross-user token contamination,
            // and WAM on Windows authenticates via the OS broker (no browser, CAP-compliant).
            var token = MsalTokenAcquirerOverride != null
                ? await MsalTokenAcquirerOverride(tenantId, validatedScopes, clientAppId, ct)
                : await AcquireGraphTokenViaMsalAsync(tenantId, validatedScopes, clientAppId, ct, loginHint);

            // Fall back to PowerShell Connect-MgGraph if MSAL is unavailable (e.g. no clientAppId)
            // or fails for any reason.
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogDebug("MSAL token acquisition failed, falling back to PowerShell Connect-MgGraph...");
                var script = BuildPowerShellScript(tenantId, validatedScopes, useDeviceCode, clientAppId);
                var result = await ExecuteWithFallbackAsync(script, ct);
                token = ProcessResult(result);

                // If PowerShell browser auth was blocked (Conditional Access Policy or interactive
                // browser unavailable in embedded terminal), retry with device code.
                if (string.IsNullOrWhiteSpace(token) && !useDeviceCode &&
                    (IsConditionalAccessError(result) || IsInteractiveBrowserFailure(result)))
                {
                    _logger.LogWarning(
                        "PowerShell interactive browser authentication failed (Conditional Access Policy or embedded terminal). " +
                        "Retrying with device code authentication...");
                    var deviceCodeScript = BuildPowerShellScript(tenantId, validatedScopes, useDeviceCode: true, clientAppId);
                    var deviceCodeResult = await ExecuteWithFallbackAsync(deviceCodeScript, ct);
                    token = ProcessResult(deviceCodeResult);
                }
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

        // Use interactive browser auth by default (useDeviceCode=false)
        // If useDeviceCode=true, use device code flow instead
        var authMethod = useDeviceCode ? "-UseDeviceCode" : "";
        
        // Include -ClientId parameter if provided (ensures authentication uses the custom client app)
        // Add leading space only when parameter is present to avoid double spaces
        var clientIdParam = !string.IsNullOrWhiteSpace(clientAppId) 
            ? $" -ClientId '{CommandStringHelper.EscapePowerShellString(clientAppId)}'" 
            : "";

        // Extract the access token from the Authorization header of a live Graph request.
        // $ctx.AccessToken is NOT used because Microsoft.Graph.Authentication v2+ returns an
        // opaque (non-JWT) value that is rejected when used as a Bearer token in Graph API calls.
        // The Authorization header on an actual request always contains the real JWT Bearer token.
        return
            $"Import-Module Microsoft.Graph.Authentication -ErrorAction Stop; " +
            $"Connect-MgGraph -TenantId '{escapedTenantId}'{clientIdParam} -Scopes {scopesArray} {authMethod} -NoWelcome -ErrorAction Stop; " +
            $"$ctx = Get-MgContext; " +
            $"if ($null -eq $ctx) {{ throw 'Failed to establish Graph context' }}; " +
            $"$response = Invoke-MgGraphRequest -Method GET -Uri 'https://graph.microsoft.com/v1.0/$metadata' -OutputType HttpResponseMessage -ErrorAction Stop; " +
            $"$token = $response.RequestMessage.Headers.Authorization.Parameter; " +
            $"$response.Dispose(); " +
            $"if ([string]::IsNullOrWhiteSpace($token)) {{ throw 'Failed to extract access token from Graph request headers' }}; " +
            $"$token";
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
        var shell = "pwsh";
        var result = await ExecutePowerShellAsync(shell, script, ct);

        // Fallback to Windows PowerShell if pwsh is not available
        if (!result.Success && IsPowerShellNotFoundError(result))
        {
            _logger.LogDebug("PowerShell Core not found, falling back to Windows PowerShell");
            shell = "powershell";
            result = await ExecutePowerShellAsync(shell, script, ct);
        }

        // If the failure is due to a missing or broken module, attempt auto-install and retry once.
        // This handles cases where Get-Module -ListAvailable reports the module as present but
        // Import-Module fails at runtime (e.g., corrupt install, partial uninstall, path mismatch).
        if (!result.Success && IsPowerShellModuleMissingError(result))
        {
            if (await TryAutoInstallRequiredModulesAsync(shell, ct))
            {
                _logger.LogInformation("Auto-installed missing PowerShell module(s). Retrying...");
                result = await ExecutePowerShellAsync(shell, script, ct);
            }
        }

        return result;
    }

    /// <summary>
    /// Acquires a Microsoft Graph access token via MSAL.NET (primary authentication path).
    /// On Windows uses WAM (no browser, CAP-compliant); on Linux/macOS uses device code.
    /// Uses MsalBrowserCredential whose token cache is keyed by user identity, preventing
    /// cross-user token contamination on shared machines.
    /// Returns null if clientAppId is unavailable; caller falls back to PowerShell Connect-MgGraph.
    /// </summary>
    private async Task<string?> AcquireGraphTokenViaMsalAsync(
        string tenantId,
        string[] scopes,
        string? clientAppId,
        CancellationToken ct,
        string? loginHint = null)
    {
        if (string.IsNullOrWhiteSpace(clientAppId))
        {
            _logger.LogDebug("MSAL token acquisition skipped: no client app ID configured. Falling back to PowerShell Connect-MgGraph.");
            return null;
        }

        try
        {
            // MSAL requires fully-qualified scope URIs; PS Connect-MgGraph handles this internally.
            var fullScopes = scopes
                .Select(s => s.Contains("://", StringComparison.Ordinal) ? s : $"https://graph.microsoft.com/{s}")
                .ToArray();

            _logger.LogDebug("Acquiring Graph token via MSAL for scopes: {Scopes}", string.Join(", ", fullScopes));

            var msalCredential = new MsalBrowserCredential(clientAppId, tenantId, logger: _logger, loginHint: loginHint);
            var tokenResult = await msalCredential.GetTokenAsync(new TokenRequestContext(fullScopes), ct);

            if (string.IsNullOrWhiteSpace(tokenResult.Token))
                return null;

            _logger.LogDebug("Microsoft Graph access token acquired successfully.");
            return tokenResult.Token;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("MSAL Graph token acquisition cancelled.");
            return null;
        }
        catch (Exception ex)
        {
            var isCanceled = ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase);
            _logger.LogDebug(ex, "MSAL Graph token acquisition failed");
            if (!isCanceled)
                _logger.LogWarning("MSAL Graph token acquisition failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Attempts to auto-install the PowerShell modules required for Graph token acquisition.
    /// Returns true if installation succeeded, false otherwise.
    /// </summary>
    private async Task<bool> TryAutoInstallRequiredModulesAsync(string shell, CancellationToken ct)
    {
        _logger.LogInformation("Detected missing or broken PowerShell module. Attempting auto-install...");
        try
        {
            var installScript =
                "Install-Module -Name 'Microsoft.Graph.Authentication' -Repository 'PSGallery' -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop; " +
                "Install-Module -Name 'Microsoft.Graph.Applications' -Repository 'PSGallery' -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop";
            var result = await ExecutePowerShellAsync(shell, installScript, ct);
            if (result.Success)
            {
                _logger.LogInformation("PowerShell modules auto-installed successfully.");
                return true;
            }
            _logger.LogWarning("Auto-install of PowerShell modules failed: {Error}", result.StandardError);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Auto-install of PowerShell modules threw an exception: {Error}", ex.Message);
            return false;
        }
    }

    private static bool IsPowerShellModuleMissingError(CommandResult result)
    {
        if (string.IsNullOrWhiteSpace(result.StandardError)) return false;
        var error = result.StandardError;
        return error.Contains("module", StringComparison.OrdinalIgnoreCase) &&
               (error.Contains("was not loaded", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("not found in any module", StringComparison.OrdinalIgnoreCase));
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
            outputTransform: FormatDeviceCodeLine,
            cancellationToken: ct);
    }

    /// <summary>
    /// Intercepts the PS Connect-MgGraph device code line and reformats it to match the MSAL box format.
    /// Input:  "To sign in, use a web browser to open the page {url} and enter the code {code} to authenticate."
    /// Output: the MSAL-style === box with URL and code on separate lines.
    /// Returns null to suppress the original line; returns the line unchanged for all other output.
    /// </summary>
    private static string? FormatDeviceCodeLine(string line)
    {
        const string marker = "To sign in, use a web browser to open the page ";
        const string codeMarker = " and enter the code ";
        const string suffix = " to authenticate.";

        if (!line.Contains(marker, StringComparison.OrdinalIgnoreCase))
            return line;

        try
        {
            var pageStart = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length;
            var codeStart = line.IndexOf(codeMarker, pageStart, StringComparison.OrdinalIgnoreCase);
            if (codeStart < 0) return line;

            var url = line[pageStart..codeStart].Trim();
            var codeEnd = line.IndexOf(suffix, codeStart + codeMarker.Length, StringComparison.OrdinalIgnoreCase);
            var code = codeEnd >= 0
                ? line[(codeStart + codeMarker.Length)..codeEnd].Trim()
                : line[(codeStart + codeMarker.Length)..].Trim();

            var sep = new string('=', 74);
            return $"{sep}\nTo sign in, use a web browser to open the page:\n    {url}\nAnd enter the code: {code}\n{sep}";
        }
        catch
        {
            return line;
        }
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

            if (IsPowerShellModuleMissingError(result))
            {
                _logger.LogError(
                    "Required PowerShell module could not be loaded (auto-install was attempted but failed). " +
                    "Run 'a365 setup requirements' to manually install missing modules.");
            }

            return null;
        }

        // The script ends with `$token`, which outputs the JWT as the last line.
        // Connect-MgGraph may also write informational messages (e.g. device code prompt)
        // to stdout in non-interactive environments. Extract only the last non-empty line
        // so those messages do not contaminate the token.
        var token = result.StandardOutput?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l => !string.IsNullOrWhiteSpace(l));

        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("PowerShell succeeded but returned empty output");
            return null;
        }

        if (!IsValidJwtFormat(token))
        {
            _logger.LogWarning("Returned token does not appear to be a valid JWT");
        }

        _logger.LogDebug("Microsoft Graph access token acquired successfully");
        return token;
    }

    private static bool IsConditionalAccessError(CommandResult result)
    {
        return !string.IsNullOrWhiteSpace(result.StandardError) &&
               (result.StandardError.Contains(AuthenticationConstants.ConditionalAccessPolicyBlockedError, StringComparison.Ordinal) ||
                result.StandardError.Contains(AuthenticationConstants.DeviceCompliancePolicyBlockedError, StringComparison.Ordinal));
    }

    private static bool IsInteractiveBrowserFailure(CommandResult result)
    {
        return !string.IsNullOrWhiteSpace(result.StandardError) &&
               result.StandardError.Contains("InteractiveBrowserCredential authentication failed",
                   StringComparison.OrdinalIgnoreCase);
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

    private static string MakeCacheKey(string tenantId, IEnumerable<string> scopes, string? clientAppId, string? loginHint = null)
    {
        var scopeKey = string.Join(" ", scopes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

        return $"{tenantId}::{clientAppId ?? ""}::{scopeKey}::{loginHint ?? ""}";
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