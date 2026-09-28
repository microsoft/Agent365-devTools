// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Assigns S2S app roles on the agent blueprint service principal by shelling out to
/// <c>az rest</c> against the Graph <c>servicePrincipals/{id}/appRoleAssignments</c>
/// endpoint, using the operator's existing <c>az login</c> session. Replaces
/// <c>PowerShellS2SRunner</c> — see CHANGELOG for the issue #429 motivation. Same
/// reasoning as <see cref="AzRestConsentRunner"/>: a GA's az token implicitly carries
/// <c>AppRoleAssignment.ReadWrite.All</c> via the directory role; <c>az rest</c> is
/// synchronous and fast; <c>Connect-MgGraph</c> module-load + MSAL/WAM is unreliable.
/// </summary>
internal static partial class AzRestS2SRunner
{
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidPattern();

    // App role value names follow the same allowlist as delegated scope names — Entra accepts
    // a wider set, but we reject anything outside this character class so an attacker-controlled
    // value never reaches the OData $filter or the request body without validation.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeRolePattern();

    /// <summary>
    /// Assigns every <see cref="ResourcePermissionSpec.AppRoleScopes"/> on every spec to
    /// the blueprint SP. Each assignment is idempotent: an existing assignment with the
    /// same <c>(principalId, resourceId, appRoleId)</c> tuple is skipped.
    /// </summary>
    /// <param name="executor">Command executor used to invoke <c>az rest</c>.</param>
    /// <param name="blueprintSpObjectId">
    /// Service-principal object id of the blueprint — the assignment is created against
    /// this SP and lists it as both <c>principalId</c> (the assignee) and the path target.
    /// </param>
    /// <param name="specs">Permission specs whose <c>AppRoleScopes</c> are the role values to assign.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// (Attempted, Succeeded):
    /// - Attempted=false when prerequisites fail (bad GUID, unsafe role value, no S2S specs).
    /// - Succeeded=true only when every role on every spec was either already assigned or
    ///   newly POSTed without error.
    /// </returns>
    public static async Task<(bool Attempted, bool Succeeded)> TryRunAsync(
        CommandExecutor executor,
        string blueprintSpObjectId,
        IReadOnlyList<ResourcePermissionSpec> specs,
        ILogger logger,
        CancellationToken ct,
        string graphBaseUrl = GraphApiConstants.BaseUrl)
    {
        if (!GuidPattern().IsMatch(blueprintSpObjectId))
        {
            logger.LogWarning("az rest S2S runner: invalid blueprint SP id - skipping.");
            return (false, false);
        }

        var s2sSpecs = specs
            .Where(s => s.AppRoleScopes is { Length: > 0 })
            .ToList();

        if (s2sSpecs.Count == 0) return (false, false);

        // Allowlist validation up front so we never interpolate untrusted values into the
        // OData filter (resource SP lookup) or the request body (role id lookup).
        foreach (var spec in s2sSpecs)
        {
            if (!GuidPattern().IsMatch(spec.ResourceAppId))
            {
                logger.LogWarning("az rest S2S runner: spec '{ResourceName}' has invalid ResourceAppId - skipping.", spec.ResourceName);
                return (false, false);
            }
            foreach (var role in spec.AppRoleScopes!)
            {
                if (!SafeRolePattern().IsMatch(role))
                {
                    logger.LogWarning("az rest S2S runner: spec '{ResourceName}' has unsafe role value '{Role}' - skipping.", spec.ResourceName, role);
                    return (false, false);
                }
            }
        }

        // Resolve the Graph base URL once so every az rest call targets the configured
        // (sovereign / commercial) cloud endpoint rather than a hardcoded commercial host.
        var baseUrl = ConfigConstants.NormalizeGraphBaseUrl(graphBaseUrl);

        logger.LogInformation("Assigning S2S app roles...");

        var allOk = true;

        // Fetch the existing assignment list once at the top — every per-role idempotency
        // check then compares against this in-memory set, avoiding N+1 Graph round-trips.
        var existingAssignments = await GetExistingAssignmentsAsync(executor, blueprintSpObjectId, baseUrl, logger, ct);
        if (existingAssignments is null)
        {
            // The GET itself failed; that's a hard stop because we can't reason about
            // idempotency without it.
            return (true, false);
        }

        foreach (var spec in s2sSpecs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ok = await AssignOneAsync(executor, blueprintSpObjectId, spec, existingAssignments, baseUrl, logger, ct);
                if (!ok) allOk = false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("  '{Name}': unexpected exception while assigning app role - {Message}", spec.ResourceName, ex.Message);
                allOk = false;
            }
        }

        return (true, allOk);
    }

    /// <summary>
    /// Looks up every role on <paramref name="spec"/> and POSTs a new assignment per role
    /// that isn't already present in <paramref name="existingAssignments"/>. The resource
    /// SP lookup is one Graph call (with <c>$select=id,appRoles</c>); the role id is
    /// derived from the embedded <c>appRoles</c> array — no second GET needed.
    /// </summary>
    private static async Task<bool> AssignOneAsync(
        CommandExecutor executor,
        string blueprintSpObjectId,
        ResourcePermissionSpec spec,
        HashSet<(string ResourceId, string AppRoleId)> existingAssignments,
        string graphBaseUrl,
        ILogger logger,
        CancellationToken ct)
    {
        var spResult = await executor.ExecuteAsync(
            "az",
            $"rest --method GET --url \"{graphBaseUrl}/v1.0/servicePrincipals?$filter=appId eq '{spec.ResourceAppId}'&$select=id,appRoles\"",
            captureOutput: true,
            suppressErrorLogging: true,
            cancellationToken: ct);

        if (!spResult.Success)
        {
            logger.LogWarning(
                "  '{Name}': failed to look up resource service principal (exit {ExitCode}): {Stderr}",
                spec.ResourceName, spResult.ExitCode, (spResult.StandardError ?? string.Empty).Trim());
            return false;
        }

        var (resourceSpId, appRolesByValue) = TryExtractFirstSpIdAndAppRoles(spResult.StandardOutput);
        if (string.IsNullOrWhiteSpace(resourceSpId))
        {
            logger.LogWarning("  '{Name}': resource service principal not found in tenant — cannot assign app role.", spec.ResourceName);
            return false;
        }

        var allOk = true;
        foreach (var role in spec.AppRoleScopes!)
        {
            if (!appRolesByValue.TryGetValue(role, out var appRoleId))
            {
                logger.LogWarning("  '{Name}': app role '{Role}' is not published on the resource — skipping.", spec.ResourceName, role);
                allOk = false;
                continue;
            }

            if (existingAssignments.Contains((resourceSpId, appRoleId)))
            {
                logger.LogInformation("  '{Name}': app role '{Role}' already assigned — no change needed.", spec.ResourceName, role);
                continue;
            }

            var createBody = JsonSerializer.Serialize(new
            {
                principalId = blueprintSpObjectId,
                resourceId = resourceSpId,
                appRoleId = appRoleId,
            });
            var created = await ExecuteAzRestWithBodyAsync(
                executor,
                method: "POST",
                url: $"{graphBaseUrl}/v1.0/servicePrincipals/{blueprintSpObjectId}/appRoleAssignments",
                bodyJson: createBody,
                logger: logger,
                ct: ct);

            if (!created)
            {
                logger.LogWarning("  '{Name}': POST of new appRoleAssignment for role '{Role}' failed.", spec.ResourceName, role);
                allOk = false;
                continue;
            }

            // Update our in-memory cache so a later spec in the same run doesn't double-POST
            // the same (resource, role) pair if it ever shows up twice in the spec list.
            existingAssignments.Add((resourceSpId, appRoleId));
            logger.LogInformation("  '{Name}': app role '{Role}' assigned.", spec.ResourceName, role);
        }

        return allOk;
    }

    /// <summary>
    /// Pulls the full appRoleAssignments collection for the blueprint SP. Returns a
    /// <c>HashSet&lt;(resourceId, appRoleId)&gt;</c> for O(1) per-role idempotency checks
    /// during the assignment loop. Returns null when the GET fails so the caller can
    /// short-circuit.
    /// </summary>
    private static async Task<HashSet<(string, string)>?> GetExistingAssignmentsAsync(
        CommandExecutor executor,
        string blueprintSpObjectId,
        string graphBaseUrl,
        ILogger logger,
        CancellationToken ct)
    {
        var result = await executor.ExecuteAsync(
            "az",
            $"rest --method GET --url \"{graphBaseUrl}/v1.0/servicePrincipals/{blueprintSpObjectId}/appRoleAssignments\"",
            captureOutput: true,
            suppressErrorLogging: true,
            cancellationToken: ct);

        if (!result.Success)
        {
            logger.LogWarning(
                "az rest GET appRoleAssignments failed (exit {ExitCode}): {Stderr}",
                result.ExitCode, (result.StandardError ?? string.Empty).Trim());
            return null;
        }

        var assignments = new HashSet<(string, string)>();
        if (string.IsNullOrWhiteSpace(result.StandardOutput)) return assignments;
        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            if (doc.RootElement.TryGetProperty("value", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("resourceId", out var resourceEl) &&
                        item.TryGetProperty("appRoleId", out var roleEl) &&
                        resourceEl.ValueKind == JsonValueKind.String &&
                        roleEl.ValueKind == JsonValueKind.String)
                    {
                        var resourceId = resourceEl.GetString();
                        var appRoleId = roleEl.GetString();
                        if (!string.IsNullOrEmpty(resourceId) && !string.IsNullOrEmpty(appRoleId))
                            assignments.Add((resourceId, appRoleId));
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Failed to parse appRoleAssignments response - assuming empty set: {Message}", ex.Message);
            return assignments;
        }
        return assignments;
    }

    /// <summary>
    /// Same temp-file approach as <see cref="AzRestConsentRunner"/> — passes the JSON body
    /// via <c>--body @&lt;tempfile&gt;</c> to avoid inline quote-escaping through cmd.exe.
    /// </summary>
    private static async Task<bool> ExecuteAzRestWithBodyAsync(
        CommandExecutor executor,
        string method,
        string url,
        string bodyJson,
        ILogger logger,
        CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"a365-azrest-s2s-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(tempFile, bodyJson, ct);

            var result = await executor.ExecuteAsync(
                "az",
                $"rest --method {method} --url \"{url}\" --body @\"{tempFile}\" --headers Content-Type=application/json",
                captureOutput: true,
                suppressErrorLogging: true,
                cancellationToken: ct);

            if (!result.Success)
            {
                logger.LogWarning(
                    "az rest {Method} {Url} failed (exit {ExitCode}): {Stderr}",
                    method, url, result.ExitCode, (result.StandardError ?? string.Empty).Trim());
                return false;
            }
            return true;
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Parses the resource-SP-with-appRoles response and returns the SP id plus a
    /// case-insensitive map from app role <c>value</c> (the user-facing name like
    /// "Agent365.Observability.OtelWrite") to <c>id</c> (the GUID required by the
    /// assignment body). Map is empty when the SP exposes no app roles.
    /// </summary>
    internal static (string? SpId, Dictionary<string, string> AppRolesByValue) TryExtractFirstSpIdAndAppRoles(string? azStandardOutput)
    {
        var rolesByValue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(azStandardOutput)) return (null, rolesByValue);
        try
        {
            using var doc = JsonDocument.Parse(azStandardOutput);
            if (!doc.RootElement.TryGetProperty("value", out var value)) return (null, rolesByValue);
            if (value.GetArrayLength() == 0) return (null, rolesByValue);

            var first = value[0];
            string? spId = first.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;

            if (first.TryGetProperty("appRoles", out var rolesEl) && rolesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var role in rolesEl.EnumerateArray())
                {
                    if (role.TryGetProperty("value", out var roleValEl) &&
                        role.TryGetProperty("id", out var roleIdEl) &&
                        roleValEl.ValueKind == JsonValueKind.String &&
                        roleIdEl.ValueKind == JsonValueKind.String)
                    {
                        var v = roleValEl.GetString();
                        var i = roleIdEl.GetString();
                        if (!string.IsNullOrEmpty(v) && !string.IsNullOrEmpty(i))
                            rolesByValue[v] = i;
                    }
                }
            }

            return (spId, rolesByValue);
        }
        catch (JsonException)
        {
            return (null, rolesByValue);
        }
    }
}
