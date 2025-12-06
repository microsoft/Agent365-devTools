using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Implements Microsoft Graph token acquisition via PowerShell Microsoft.Graph module.
/// </summary>
public sealed class MicrosoftGraphTokenProvider : IMicrosoftGraphTokenProvider
{
    private readonly CommandExecutor _executor;
    private readonly ILogger<MicrosoftGraphTokenProvider> _logger;

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
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Acquiring Microsoft Graph delegated access token via PowerShell (Device Code: {UseDeviceCode})",
            useDeviceCode);

        var validatedScopes = ValidateAndPrepareScopes(scopes);
        ValidateTenantId(tenantId);

        var script = BuildPowerShellScript(tenantId, validatedScopes, useDeviceCode);
        var result = await ExecuteWithFallbackAsync(script, useDeviceCode, ct);

        return ProcessResult(result);
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
            if (ContainsDangerousCharacters(scope))
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

    private static string BuildPowerShellScript(string tenantId, string[] scopes, bool useDeviceCode)
    {
        var escapedTenantId = EscapePowerShellString(tenantId);
        var scopesArray = BuildScopesArray(scopes);

        // Use -UseDeviceCode for CLI-friendly authentication (no browser popup/download)
        var authMethod = useDeviceCode ? "-UseDeviceCode" : "";

        // Workaround for older Microsoft.Graph versions that don't have Get-MgAccessToken
        // We make a dummy Graph request and extract the token from the Authorization header
        return
            $"Import-Module Microsoft.Graph.Authentication -ErrorAction Stop; " +
            $"Connect-MgGraph -TenantId '{escapedTenantId}' -Scopes {scopesArray} {authMethod} -NoWelcome -ErrorAction Stop; " +
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
        var escapedScopes = scopes.Select(s => $"'{EscapePowerShellString(s)}'");
        return $"@({string.Join(",", escapedScopes)})";
    }

    private async Task<CommandResult> ExecuteWithFallbackAsync(
        string script,
        bool useDeviceCode,
        CancellationToken ct)
    {
        // Try PowerShell Core first (cross-platform)
        var result = await ExecutePowerShellAsync("pwsh", script, useDeviceCode, ct);

        // Fallback to Windows PowerShell if pwsh is not available
        if (!result.Success && IsPowerShellNotFoundError(result))
        {
            _logger.LogDebug("PowerShell Core not found, falling back to Windows PowerShell");
            result = await ExecutePowerShellAsync("powershell", script, useDeviceCode, ct);
        }

        return result;
    }

    private async Task<CommandResult> ExecutePowerShellAsync(
        string shell,
        string script,
        bool useDeviceCode,
        CancellationToken ct)
    {
        var arguments = BuildPowerShellArguments(shell, script);

        if (useDeviceCode)
        {
            // Use streaming for device code flow so user sees the instructions in real-time
            return await _executor.ExecuteWithStreamingAsync(
                command: shell,
                arguments: arguments,
                workingDirectory: null,
                outputPrefix: "",
                interactive: true, // Allow user to see device code instructions
                cancellationToken: ct);
        }
        else
        {
            // Use standard execution for browser flow (captures output silently)
            return await _executor.ExecuteAsync(
                command: shell,
                arguments: arguments,
                workingDirectory: null,
                captureOutput: true,
                suppressErrorLogging: true, // We handle logging ourselves
                cancellationToken: ct);
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

    private static bool IsValidTenantId(string tenantId)
    {
        // GUID format
        if (Guid.TryParse(tenantId, out _))
            return true;

        // Domain name format (basic validation)
        return tenantId.Contains('.') &&
               tenantId.Length <= 253 &&
               !ContainsDangerousCharacters(tenantId);
    }

    private static bool ContainsDangerousCharacters(string input)
    {
        var dangerous = new[] { '\'', '"', ';', '`', '$', '&', '|', '<', '>', '\n', '\r', '\t' };
        return input.IndexOfAny(dangerous) >= 0;
    }

    private static string EscapePowerShellString(string input)
    {
        // In PowerShell single-quoted strings, only single quotes need escaping
        return input.Replace("'", "''");
    }

    private static bool IsValidJwtFormat(string token)
    {
        // JWT tokens have three base64 parts separated by dots
        // Header typically starts with "eyJ" when base64-decoded
        return token.StartsWith("eyJ", StringComparison.Ordinal) &&
               token.Count(c => c == '.') == 2;
    }
}