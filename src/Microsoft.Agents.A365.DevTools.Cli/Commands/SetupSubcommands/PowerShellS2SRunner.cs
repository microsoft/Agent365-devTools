// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

// TODO(issue #429, pre-PR): replace this runner with AzRestS2SRunner before the PR lands.
// Same reasoning as the PowerShellConsentRunner TODO — Connect-MgGraph is slow and
// unreliable, and the operator's az login is sufficient to POST appRoleAssignments
// directly via `az rest`. Once AzRestS2SRunner is confirmed working end-to-end, delete
// this file and its tests.

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
    /// (Attempted, Succeeded):
    /// - Attempted=false when prerequisites fail (bad GUID inputs, no S2S specs, pwsh not found, timeout).
    /// - Succeeded=true only when the pwsh subprocess exits with code 0.
    /// </returns>
    public static async Task<(bool Attempted, bool Succeeded)> TryRunAsync(
        CommandExecutor executor,
        string tenantId,
        string blueprintAppId,
        IReadOnlyList<ResourcePermissionSpec> specs,
        ILogger logger,
        CancellationToken ct)
    {
        if (!GuidPattern().IsMatch(tenantId) || !GuidPattern().IsMatch(blueprintAppId))
        {
            logger.LogWarning("PowerShell S2S runner: invalid tenantId or blueprintAppId - skipping.");
            return (false, false);
        }

        var s2sSpecs = specs
            .Where(s => s.AppRoleScopes is { Length: > 0 })
            .ToList();

        if (s2sSpecs.Count == 0)
            return (false, false);

        // Validate all resource app IDs and role values before building the script.
        foreach (var spec in s2sSpecs)
        {
            if (!GuidPattern().IsMatch(spec.ResourceAppId))
            {
                logger.LogWarning("PowerShell S2S runner: spec '{ResourceName}' has invalid ResourceAppId - skipping.", spec.ResourceName);
                return (false, false);
            }

            foreach (var role in spec.AppRoleScopes!)
            {
                if (!SafeScopePattern().IsMatch(role))
                {
                    logger.LogWarning("PowerShell S2S runner: spec '{ResourceName}' has unsafe role value '{Role}' - skipping.", spec.ResourceName, role);
                    return (false, false);
                }
            }
        }

        var script = BuildScript(tenantId, blueprintAppId, s2sSpecs);

        // Browser-open is the slow step the operator can't observe directly — call it out
        // with prep text so they don't think the CLI is hung while pwsh launches.
        logger.LogInformation("Connecting to Microsoft Graph. This may take a moment; a browser window may open for sign-in...");

        logger.LogDebug("Executing S2S PowerShell script via temp file...");
        logger.LogDebug("S2S PowerShell script:{NewLine}{Script}", Environment.NewLine, script);

        // Write the script to a temp file and invoke pwsh -File <path> rather than piping
        // via stdin (-Command -). When stdin carries the script, it reaches EOF as soon as the
        // script is fully written; Connect-MgGraph -UseDeviceCode reads stdin as part of its
        // device-code polling loop and exits immediately on EOF — auth never completes.
        // With -File, stdin stays connected to the parent terminal so the device code wait works.
        var tempFile = Path.Combine(Path.GetTempPath(), $"a365-s2s-{Guid.NewGuid():N}.ps1");
        CommandResult result;

        // Cap the pwsh subprocess at 5 minutes. If Connect-MgGraph hangs (e.g. on a
        // headless machine where the browser launch never completes), we abandon the
        // attempt rather than blocking the CLI forever.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await File.WriteAllTextAsync(tempFile, script, ct);

            // Remove environment variables that can cause assembly loading conflicts.
            // This is Windows-only: the parent dotnet host injects PSModulePath /
            // DOTNET_ROOT* values that collide with pwsh's own assembly resolution and
            // produce "[Assembly with same name is already loaded]" failures. On
            // Linux/Mac these vars are either unset or carry legitimate module search
            // paths, so removing them would break module discovery instead of fixing it.
            var envOverrides = new Dictionary<string, string?>();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                envOverrides["PSModulePath"] = null;
                envOverrides["DOTNET_TOOLS"] = null;
                envOverrides["DOTNET_ROOT"] = null;
                envOverrides["DOTNET_ROOT_X64"] = null;
                envOverrides["DOTNET_STARTUP_HOOKS"] = null;
                envOverrides["DOTNETSTARTUPHOOKS"] = null;
            }

            result = await executor.ExecuteWithStreamingAsync(
                "pwsh", $"-NoProfile -ExecutionPolicy Bypass -File \"{tempFile}\"",
                interactive: true,
                suppressErrorLogging: true,
                cancellationToken: timeoutCts.Token,
                environmentOverrides: envOverrides,
                redirectOutput: false);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            logger.LogWarning("PowerShell 7+ ('pwsh') is not installed or not on PATH. Install from https://aka.ms/powershell, then run 'a365 setup requirements' to verify.");
            return (false, false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning("PowerShell S2S runner timed out after 5 minutes. The 'Action Required' block at the end of setup contains manual steps you can run yourself.");
            return (false, false);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort cleanup */ }
        }

        logger.LogDebug("pwsh exited with code {ExitCode}", result.ExitCode);

        // Note: stdout/stderr are not redirected (redirectOutput: false) so the child
        // writes directly to the console. Success is determined by the exit code.
        var succeeded = result.ExitCode == 0;
        return (true, succeeded);
    }

    private static string BuildScript(
        string tenantId,
        string blueprintAppId,
        IReadOnlyList<ResourcePermissionSpec> s2sSpecs)
    {
        var sb = new StringBuilder();

        // Stop on any non-terminating error so the script's exit code accurately reflects success/failure.
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("");

        // Force-load the Graph modules to avoid assembly loading conflicts. Pin the highest
        // installed version of each module and import by absolute path so PowerShell does not
        // silently pick up a different version through its standard probing.
        // Authentication must be imported before Applications because Connect-MgGraph lives in
        // Authentication and Applications transitively requires it. Exit code 2 is reserved for
        // "modules missing" so callers can distinguish a missing-prereq from an auth failure.
        sb.AppendLine("foreach ($name in @('Microsoft.Graph.Authentication','Microsoft.Graph.Applications')) {");
        sb.AppendLine("  $m = Get-Module $name -ListAvailable | Sort-Object Version -Descending | Select-Object -First 1");
        sb.AppendLine("  if (-not $m) { Write-Error \"Required PowerShell module '$name' is not installed. Run: Install-Module $name -Scope CurrentUser\"; exit 2 }");
        sb.AppendLine("  Import-Module $m.Path -Force");
        sb.AppendLine("}");
        sb.AppendLine("");

        // Disconnect any stale cached session first; a reused DeviceCodeCredential from a prior
        // run can leave the context in a broken state where Connect-MgGraph returns without error
        // but subsequent cmdlets throw a NullReferenceException inside the credential object.
        sb.AppendLine("try { Disconnect-MgGraph -Confirm:$false -ErrorAction SilentlyContinue } catch { }");
        // -ContextScope Process forces an in-memory-only connection, bypassing the persistent token
        // cache. Without this, Connect-MgGraph reloads a stale DeviceCodeCredential from disk even
        // after Disconnect-MgGraph, causing a NullReferenceException in subsequent cmdlets.
        sb.AppendLine($"Connect-MgGraph -TenantId '{tenantId}' -Scopes 'AppRoleAssignment.ReadWrite.All','Application.Read.All' -NoWelcome -ContextScope Process");
        // Guard: Connect-MgGraph can return without throwing even when auth did not complete.
        sb.AppendLine("$_ctx = Get-MgContext");
        sb.AppendLine("if (-not $_ctx -or [string]::IsNullOrEmpty($_ctx.Account)) { Write-Error 'Authentication did not complete - no account in context after Connect-MgGraph'; exit 1 }");
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

        // No success marker emitted — the parent process keys off the pwsh exit code
        // (0 == OK). Writing a marker line would only leak into the operator's terminal.
        return sb.ToString();
    }

}
