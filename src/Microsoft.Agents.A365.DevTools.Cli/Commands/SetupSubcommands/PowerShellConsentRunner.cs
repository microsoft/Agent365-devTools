// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

// TODO(issue #429, pre-PR): replace this runner with AzRestConsentRunner before the PR
// lands. Connect-MgGraph is unreliable in practice — module load is 5-10s cold, MSAL/WAM
// browser negotiation takes anywhere from 5s to 2 minutes, and the operator cannot tell
// whether the subprocess is making progress or hung. The operator already has an az login
// as Global Administrator, whose token is sufficient to POST /v1.0/oauth2PermissionGrants
// directly via `az rest`. Symmetric with the new az ad sp create flow and far faster.
// Once AzRestConsentRunner is confirmed working end-to-end in a real run, delete this file
// and its tests.

/// <summary>
/// Runs the delegated admin-consent grant via a PowerShell subprocess as a fallback for the
/// browser-based <c>/v2.0/adminconsent</c> flow. Used when (a) the browser flow times out
/// without observing the grant, (b) Entra rejects the URL with an OAuth error (e.g.
/// AADSTS650053 from a scope/SP mismatch), or (c) the operator explicitly opts into the
/// PowerShell path. Mirrors <see cref="PowerShellS2SRunner"/>'s structure so the two
/// fallback runners behave the same operationally.
///
/// <para>
/// The script uses <c>Connect-MgGraph</c> with <c>DelegatedPermissionGrant.ReadWrite.All</c>
/// + <c>Application.Read.All</c> and creates <c>AllPrincipals</c> (tenant-wide) grants via
/// <c>New-MgOauth2PermissionGrant</c>. Connect-MgGraph drives its own interactive sign-in
/// prompt via the operator's browser/WAM session, so a Global Administrator's permissions
/// are exercised in PowerShell — not via the CLI's MSAL token (which does not carry
/// <c>DelegatedPermissionGrant.ReadWrite.All</c>).
/// </para>
/// <para>
/// The runner accepts the <em>original</em> (un-filtered) spec scopes by design. The
/// <c>oauth2PermissionGrants</c> POST API is lenient about scope existence and will create
/// the grant row even for scope names the resource SP does not currently publish — useful
/// when a first-party SP is expected to expose the scope shortly or when the operator
/// wants to record intent regardless. The user-visible warning emitted by the caller makes
/// this trade-off explicit.
/// </para>
/// </summary>
internal static partial class PowerShellConsentRunner
{
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidPattern();

    // Delegated scope value names allow the same characters S2S app roles use; reused here.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeScopePattern();

    /// <summary>
    /// Builds and executes the delegated admin-consent PowerShell script.
    /// </summary>
    /// <param name="executor">Command executor used to invoke pwsh.</param>
    /// <param name="tenantId">Tenant where the grants are created.</param>
    /// <param name="blueprintSpObjectId">
    /// Service-principal object ID of the agent blueprint. Required up front so the script
    /// can issue the grant against the blueprint SP without re-resolving it at runtime
    /// (Phase 1 has already done that look-up).
    /// </param>
    /// <param name="specs">
    /// Permission specs whose <c>Scopes</c> become the grant scope list for each resource.
    /// Specs with empty <c>Scopes</c> are skipped. Pass the orchestrator's <em>original</em>
    /// spec list (pre-validation) so the user can stamp scopes even when the resource SP
    /// does not publish them — see class-level remark.
    /// </param>
    /// <param name="logger">Logger for breadcrumbs and warnings.</param>
    /// <param name="ct">Cancellation token; honored alongside an internal 5-minute cap on the subprocess.</param>
    /// <returns>
    /// (Attempted, Succeeded):
    /// - Attempted=false when prerequisites fail (bad GUID inputs, no delegated specs, pwsh not found, timeout).
    /// - Succeeded=true only when the pwsh subprocess exits with code 0.
    /// </returns>
    public static async Task<(bool Attempted, bool Succeeded)> TryRunAsync(
        CommandExecutor executor,
        string tenantId,
        string blueprintSpObjectId,
        IReadOnlyList<ResourcePermissionSpec> specs,
        ILogger logger,
        CancellationToken ct)
    {
        if (!GuidPattern().IsMatch(tenantId) || !GuidPattern().IsMatch(blueprintSpObjectId))
        {
            logger.LogWarning("PowerShell consent runner: invalid tenantId or blueprint SP id - skipping.");
            return (false, false);
        }

        var delegatedSpecs = specs
            .Where(s => s.Scopes is { Length: > 0 })
            .ToList();

        if (delegatedSpecs.Count == 0)
            return (false, false);

        // Validate all resource app IDs and scope values before building the script. A bad
        // value here is far cheaper to surface as a warning than to surface as a pwsh
        // syntax error after launching a subprocess.
        foreach (var spec in delegatedSpecs)
        {
            if (!GuidPattern().IsMatch(spec.ResourceAppId))
            {
                logger.LogWarning("PowerShell consent runner: spec '{ResourceName}' has invalid ResourceAppId - skipping.", spec.ResourceName);
                return (false, false);
            }

            foreach (var scope in spec.Scopes)
            {
                if (!SafeScopePattern().IsMatch(scope))
                {
                    logger.LogWarning("PowerShell consent runner: spec '{ResourceName}' has unsafe scope value '{Scope}' - skipping.", spec.ResourceName, scope);
                    return (false, false);
                }
            }
        }

        var script = BuildScript(tenantId, blueprintSpObjectId, delegatedSpecs);

        // Same prep messaging as PowerShellS2SRunner — the browser-open is the slow step
        // and the operator can't see what pwsh is doing until the sign-in window appears.
        logger.LogInformation("Connecting to Microsoft Graph. This may take a moment; a browser window may open for sign-in...");

        logger.LogDebug("Executing delegated consent PowerShell script via temp file...");
        logger.LogDebug("Delegated consent PowerShell script:{NewLine}{Script}", Environment.NewLine, script);

        // Write to a temp file rather than piping via stdin -Command -; same rationale as
        // PowerShellS2SRunner: Connect-MgGraph's device-code path reads stdin and exits
        // on EOF, which kills auth before it completes.
        var tempFile = Path.Combine(Path.GetTempPath(), $"a365-consent-{Guid.NewGuid():N}.ps1");
        CommandResult result;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await File.WriteAllTextAsync(tempFile, script, ct);

            // Same Windows-only env-override rationale as PowerShellS2SRunner: the parent
            // dotnet host's PSModulePath / DOTNET_ROOT* values collide with pwsh assembly
            // resolution and produce "[Assembly with same name is already loaded]"
            // failures. On Linux/Mac these vars are unset or load-bearing for legitimate
            // module discovery, so removing them would break the script instead of fixing it.
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
            logger.LogWarning("PowerShell consent runner timed out after 5 minutes. The 'Action Required' block at the end of setup contains manual steps you can run yourself.");
            return (false, false);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort cleanup */ }
        }

        logger.LogDebug("pwsh exited with code {ExitCode}", result.ExitCode);

        var succeeded = result.ExitCode == 0;
        return (true, succeeded);
    }

    private static string BuildScript(
        string tenantId,
        string blueprintSpObjectId,
        IReadOnlyList<ResourcePermissionSpec> delegatedSpecs)
    {
        var sb = new StringBuilder();

        // Stop on any non-terminating error so the exit code accurately reflects success/failure.
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("");

        // Force-load by absolute path. Authentication first because Connect-MgGraph lives
        // there and Applications/Identity.SignIns require it. Exit code 2 is reserved for
        // "modules missing" so callers can distinguish missing-prereq from auth failure.
        sb.AppendLine("foreach ($name in @('Microsoft.Graph.Authentication','Microsoft.Graph.Applications','Microsoft.Graph.Identity.SignIns')) {");
        sb.AppendLine("  $m = Get-Module $name -ListAvailable | Sort-Object Version -Descending | Select-Object -First 1");
        sb.AppendLine("  if (-not $m) { Write-Error \"Required PowerShell module '$name' is not installed. Run: Install-Module $name -Scope CurrentUser\"; exit 2 }");
        sb.AppendLine("  Import-Module $m.Path -Force");
        sb.AppendLine("}");
        sb.AppendLine("");

        // Disconnect any stale cached session first; see PowerShellS2SRunner for the same
        // hazard around reused DeviceCodeCredential. -ContextScope Process bypasses the
        // persistent token cache so a stale credential cannot bleed through.
        sb.AppendLine("try { Disconnect-MgGraph -Confirm:$false -ErrorAction SilentlyContinue } catch { }");
        sb.AppendLine($"Connect-MgGraph -TenantId '{tenantId}' -Scopes 'DelegatedPermissionGrant.ReadWrite.All','Application.Read.All' -NoWelcome -ContextScope Process");
        sb.AppendLine("$_ctx = Get-MgContext");
        sb.AppendLine("if (-not $_ctx -or [string]::IsNullOrEmpty($_ctx.Account)) { Write-Error 'Authentication did not complete - no account in context after Connect-MgGraph'; exit 1 }");
        sb.AppendLine($"$bpId = '{blueprintSpObjectId}'");

        foreach (var spec in delegatedSpecs)
        {
            // PowerShell escapes a single-quote inside a single-quoted string by doubling it.
            var safeResourceName = spec.ResourceName.Replace("'", "''");
            // Space-delimited scope list per Entra's grant body format.
            var scopeList = string.Join(' ', spec.Scopes);

            sb.AppendLine($"# {safeResourceName}: {scopeList}");
            sb.AppendLine($"$res = Get-MgServicePrincipal -Filter \"appId eq '{spec.ResourceAppId}'\"");
            sb.AppendLine($"if (-not $res) {{ Write-Error 'Resource SP not found for {safeResourceName}'; exit 1 }}");
            // Idempotent: look for an existing AllPrincipals grant for this (clientId,
            // resourceId) pair and merge our scope set into it rather than POSTing a
            // duplicate (Entra would 400 with Request_MultipleObjectsWithSameKeyValue).
            sb.AppendLine("$existing = Get-MgOauth2PermissionGrant -All -Filter \"clientId eq '$bpId' and consentType eq 'AllPrincipals' and resourceId eq '$($res.Id)'\" | Select-Object -First 1");
            sb.AppendLine($"$desired = '{scopeList}'");
            sb.AppendLine("if ($existing) {");
            sb.AppendLine("  $cur = @()");
            sb.AppendLine("  if ($existing.Scope) { $cur = $existing.Scope -split ' ' | Where-Object { $_ } }");
            sb.AppendLine("  $merged = ($cur + ($desired -split ' ' | Where-Object { $_ })) | Sort-Object -Unique");
            sb.AppendLine("  Update-MgOauth2PermissionGrant -OAuth2PermissionGrantId $existing.Id -Scope ($merged -join ' ') | Out-Null");
            sb.AppendLine("} else {");
            sb.AppendLine("  New-MgOauth2PermissionGrant -ClientId $bpId -ConsentType 'AllPrincipals' -ResourceId $res.Id -Scope $desired | Out-Null");
            sb.AppendLine("}");
        }

        // No success marker emitted — the parent process keys off the pwsh exit code
        // (0 == OK). Writing a marker line would only leak into the operator's terminal.
        return sb.ToString();
    }
}
