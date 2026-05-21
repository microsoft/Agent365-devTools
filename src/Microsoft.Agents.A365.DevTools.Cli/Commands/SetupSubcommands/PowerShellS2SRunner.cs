// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Runs the S2S app role assignment PowerShell script automatically when the programmatic
/// Graph API path fails for a Global Administrator. Requires pwsh and the Microsoft.Graph
/// modules; run 'a365 setup requirements' to check prerequisites.
/// </summary>
internal static partial class PowerShellS2SRunner
{
    // Matches a standard GUID (8-4-4-4-12 hex, case-insensitive).
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidPattern();

    // App role scope values must only contain alphanumeric characters, dots, hyphens, and underscores.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeScopePattern();

    /// <summary>
    /// Builds and executes the S2S app role assignment PowerShell script.
    /// </summary>
    /// <returns>
    /// (Attempted, Succeeded, MissingModules):
    /// - Attempted=false when prerequisites fail (bad GUID inputs, no S2S specs, pwsh not found).
    /// - Succeeded=true only when all assignments completed and the sentinel marker was written.
    /// - MissingModules=true when stderr indicates Microsoft.Graph modules are not installed.
    /// </returns>
    public static async Task<(bool Attempted, bool Succeeded, bool MissingModules)> TryRunAsync(
        CommandExecutor executor,
        string tenantId,
        string blueprintAppId,
        IReadOnlyList<ResourcePermissionSpec> specs,
        ILogger logger,
        CancellationToken ct)
    {
        if (!GuidPattern().IsMatch(tenantId) || !GuidPattern().IsMatch(blueprintAppId))
        {
            logger.LogWarning("PowerShell S2S runner: invalid tenantId or blueprintAppId — skipping.");
            return (false, false, false);
        }

        var s2sSpecs = specs
            .Where(s => s.AppRoleScopes is { Length: > 0 })
            .ToList();

        if (s2sSpecs.Count == 0)
            return (false, false, false);

        // Validate all resource app IDs and role values before building the script.
        foreach (var spec in s2sSpecs)
        {
            if (!GuidPattern().IsMatch(spec.ResourceAppId))
            {
                logger.LogWarning("PowerShell S2S runner: spec '{ResourceName}' has invalid ResourceAppId — skipping.", spec.ResourceName);
                return (false, false, false);
            }

            foreach (var role in spec.AppRoleScopes!)
            {
                if (!SafeScopePattern().IsMatch(role))
                {
                    logger.LogWarning("PowerShell S2S runner: spec '{ResourceName}' has unsafe role value '{Role}' — skipping.", spec.ResourceName, role);
                    return (false, false, false);
                }
            }
        }

        var script = BuildScript(tenantId, blueprintAppId, s2sSpecs);

        logger.LogInformation("S2S app role assignment requires interactive authentication.");
        logger.LogInformation("A device code prompt will appear below — follow the on-screen instructions to sign in, then wait for setup to continue.");

        logger.LogDebug("Executing S2S PowerShell script via temp file...");
        logger.LogDebug("S2S PowerShell script:{NewLine}{Script}", Environment.NewLine, script);

        // Write the script to a temp file and invoke pwsh -File <path> rather than piping
        // via stdin (-Command -). When stdin carries the script, it reaches EOF as soon as the
        // script is fully written; Connect-MgGraph -UseDeviceCode reads stdin as part of its
        // device-code polling loop and exits immediately on EOF — auth never completes.
        // With -File, stdin stays connected to the parent terminal so the device code wait works.
        var tempFile = Path.Combine(Path.GetTempPath(), $"a365-s2s-{Guid.NewGuid():N}.ps1");
        CommandResult result;
        try
        {
            await File.WriteAllTextAsync(tempFile, script, ct);
            result = await executor.ExecuteWithStreamingAsync(
                "pwsh", $"-NoProfile -File \"{tempFile}\"",
                outputPrefix: "  [pwsh] ",
                interactive: true,
                suppressErrorLogging: true,
                cancellationToken: ct);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            logger.LogDebug("pwsh not found on this system.");
            return (false, false, false);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort cleanup */ }
        }

        logger.LogDebug("pwsh exited with code {ExitCode}", result.ExitCode);
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            logger.LogDebug("pwsh stdout:{NewLine}{Stdout}", Environment.NewLine, result.StandardOutput);
        if (!string.IsNullOrWhiteSpace(result.StandardError))
            logger.LogDebug("pwsh stderr:{NewLine}{Stderr}", Environment.NewLine, result.StandardError);

        var missingModules = IsMissingModulesError(result.StandardError);
        var succeeded = result.StandardOutput.Contains("A365-S2S-OK", StringComparison.Ordinal);
        return (true, succeeded, missingModules);
    }

    private static string BuildScript(
        string tenantId,
        string blueprintAppId,
        IReadOnlyList<ResourcePermissionSpec> s2sSpecs)
    {
        var sb = new StringBuilder();

        // Disconnect any stale cached session first; a reused DeviceCodeCredential from a prior
        // run can leave the context in a broken state where Connect-MgGraph returns without error
        // but subsequent cmdlets throw a NullReferenceException inside the credential object.
        sb.AppendLine("try { Disconnect-MgGraph -Confirm:$false -ErrorAction SilentlyContinue } catch { }");
        // -ContextScope Process forces an in-memory-only connection, bypassing the persistent token
        // cache. Without this, Connect-MgGraph reloads a stale DeviceCodeCredential from disk even
        // after Disconnect-MgGraph, causing a NullReferenceException in subsequent cmdlets.
        sb.AppendLine($"Connect-MgGraph -TenantId '{tenantId}' -Scopes 'AppRoleAssignment.ReadWrite.All','Directory.Read.All' -UseDeviceCode -NoWelcome -ContextScope Process");
        // Guard: Connect-MgGraph can return without throwing even when auth did not complete.
        sb.AppendLine("$_ctx = Get-MgContext");
        sb.AppendLine("if (-not $_ctx -or [string]::IsNullOrEmpty($_ctx.Account)) { Write-Error 'Authentication did not complete — no account in context after Connect-MgGraph'; exit 1 }");
        sb.AppendLine($"$bp = Get-MgServicePrincipal -Filter \"appId eq '{blueprintAppId}'\"");
        sb.AppendLine("if (-not $bp) { Write-Error 'Blueprint SP not found'; exit 1 }");

        foreach (var spec in s2sSpecs)
        {
            // PowerShell escapes a single-quote inside a single-quoted string by doubling it.
            var safeResourceName = spec.ResourceName.Replace("'", "''");
            foreach (var role in spec.AppRoleScopes!)
            {
                sb.AppendLine($"# {safeResourceName}: {role}");
                sb.AppendLine($"$res = Get-MgServicePrincipal -Filter \"appId eq '{spec.ResourceAppId}'\"");
                sb.AppendLine($"if (-not $res) {{ Write-Error 'Resource SP not found for {safeResourceName}'; exit 1 }}");
                sb.AppendLine($"$rid = ($res.AppRoles | Where-Object {{ $_.Value -eq '{role}' }}).Id");
                sb.AppendLine($"if (-not $rid) {{ Write-Error 'App role not found: {role}'; exit 1 }}");
                // Idempotent: skip if already assigned (error code 'Request_MultipleObjectsWithSameKeyValue').
                sb.AppendLine("$existing = Get-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $bp.Id | Where-Object { $_.AppRoleId -eq $rid -and $_.ResourceId -eq $res.Id }");
                sb.AppendLine("if (-not $existing) {");
                sb.AppendLine("  New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $bp.Id -PrincipalId $bp.Id -ResourceId $res.Id -AppRoleId $rid | Out-Null");
                sb.AppendLine("}");
            }
        }

        sb.AppendLine("Write-Output 'A365-S2S-OK'");
        return sb.ToString();
    }

    private static bool IsMissingModulesError(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        return stderr.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("cannot find module", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Could not find module", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("CommandNotFoundException", StringComparison.OrdinalIgnoreCase);
    }
}
