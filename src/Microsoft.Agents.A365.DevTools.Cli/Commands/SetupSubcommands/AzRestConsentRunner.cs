// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Grants delegated admin consent on the agent blueprint service principal by shelling out
/// to <c>az rest</c> against the Graph <c>oauth2PermissionGrants</c> endpoint, using the
/// operator's existing <c>az login</c> session. Replaces <c>PowerShellConsentRunner</c> —
/// see CHANGELOG for the issue #429 motivation. <c>Connect-MgGraph</c> takes 5–10 seconds
/// to cold-boot the SDK and the MSAL/WAM browser negotiation is unreliable in practice
/// (operators observed 2-minute hangs); <c>az rest</c> is synchronous, fast, and reuses an
/// already-authenticated session.
///
/// <para>
/// Privilege model: a Global Administrator's az login token implicitly carries every
/// Graph application permission via the GA directory role, including
/// <c>DelegatedPermissionGrant.ReadWrite.All</c> — the scope <c>POST /oauth2PermissionGrants</c>
/// requires. No special consent is needed on the well-known az CLI app for this to work.
/// </para>
/// </summary>
internal static partial class AzRestConsentRunner
{
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidPattern();

    // Delegated scope value names allow alphanumerics, dots, hyphens, underscores. Stricter
    // than what Entra technically accepts so we never interpolate untrusted strings into the
    // OData $filter parameter without an allowlist pass first.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeScopePattern();

    /// <summary>
    /// Executes the delegated admin-consent grants, one resource at a time, against the
    /// operator's az session. Each grant is idempotent: if an
    /// <c>AllPrincipals</c> grant already exists between the blueprint and the resource SP,
    /// the existing scope set is union-merged with the requested set and PATCHed; otherwise
    /// a new grant is POSTed.
    /// </summary>
    /// <param name="executor">Command executor used to invoke <c>az rest</c>.</param>
    /// <param name="blueprintSpObjectId">
    /// Service-principal object id of the blueprint — used as the <c>clientId</c> on every
    /// oauth2PermissionGrant row. Phase 1 resolves this and the orchestrator passes it in.
    /// </param>
    /// <param name="specs">
    /// Permission specs whose <c>Scopes</c> become the grant scope set per resource. Specs
    /// with empty <c>Scopes</c> are skipped.
    /// </param>
    /// <param name="logger">Logger.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// (Attempted, Succeeded):
    /// - Attempted=false when prerequisites fail (bad GUID, unsafe scope value, no delegated specs).
    /// - Succeeded=true only when every per-spec grant completed without error AND the
    ///   resource SP was resolvable. Partial failures across specs flag overall failure but
    ///   the loop continues so successful grants are still persisted on the wire.
    /// </returns>
    public static async Task<(bool Attempted, bool Succeeded)> TryRunAsync(
        CommandExecutor executor,
        string blueprintSpObjectId,
        IReadOnlyList<ResourcePermissionSpec> specs,
        ILogger logger,
        CancellationToken ct)
    {
        if (!GuidPattern().IsMatch(blueprintSpObjectId))
        {
            logger.LogWarning("az rest consent runner: invalid blueprint SP id - skipping.");
            return (false, false);
        }

        var delegatedSpecs = specs
            .Where(s => s.Scopes is { Length: > 0 })
            .ToList();

        if (delegatedSpecs.Count == 0) return (false, false);

        // Validate appId / scope values upfront before any az calls. A bad value here is
        // far cheaper to surface as a warning than to surface as a half-completed grant.
        foreach (var spec in delegatedSpecs)
        {
            if (!GuidPattern().IsMatch(spec.ResourceAppId))
            {
                logger.LogWarning("az rest consent runner: spec '{ResourceName}' has invalid ResourceAppId - skipping.", spec.ResourceName);
                return (false, false);
            }

            foreach (var scope in spec.Scopes)
            {
                if (!SafeScopePattern().IsMatch(scope))
                {
                    logger.LogWarning("az rest consent runner: spec '{ResourceName}' has unsafe scope value '{Scope}' - skipping.", spec.ResourceName, scope);
                    return (false, false);
                }
            }
        }

        logger.LogInformation("Granting delegated admin consent...");

        var allOk = true;
        foreach (var spec in delegatedSpecs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ok = await GrantOneAsync(executor, blueprintSpObjectId, spec, logger, ct);
                if (!ok) allOk = false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("  '{Name}': unexpected exception while granting consent - {Message}", spec.ResourceName, ex.Message);
                allOk = false;
            }
        }

        return (true, allOk);
    }

    /// <summary>
    /// Grants <paramref name="spec"/>'s scopes for the given blueprint SP against a single
    /// resource. Lookup, idempotency check, and write are three separate Graph round-trips.
    /// </summary>
    private static async Task<bool> GrantOneAsync(
        CommandExecutor executor,
        string blueprintSpObjectId,
        ResourcePermissionSpec spec,
        ILogger logger,
        CancellationToken ct)
    {
        // 1. Resolve the resource SP object id.
        var resourceSpResult = await executor.ExecuteAsync(
            "az",
            $"rest --method GET --url \"https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId eq '{spec.ResourceAppId}'&$select=id\"",
            captureOutput: true,
            suppressErrorLogging: true,
            cancellationToken: ct);

        if (!resourceSpResult.Success)
        {
            logger.LogWarning(
                "  '{Name}': failed to look up resource service principal (exit {ExitCode}): {Stderr}",
                spec.ResourceName, resourceSpResult.ExitCode, (resourceSpResult.StandardError ?? string.Empty).Trim());
            return false;
        }

        var resourceSpId = TryExtractFirstId(resourceSpResult.StandardOutput);
        if (string.IsNullOrWhiteSpace(resourceSpId))
        {
            logger.LogWarning(
                "  '{Name}': resource service principal not found in tenant — cannot grant consent.",
                spec.ResourceName);
            return false;
        }

        // 2. Check for an existing AllPrincipals grant for this (client, resource) pair.
        //    A leftover Principal-scoped grant (e.g. from an earlier --authmode obo run)
        //    must NOT satisfy this check — that would leave the tenant-wide grant
        //    un-created. Filter on consentType to be precise.
        var grantQueryResult = await executor.ExecuteAsync(
            "az",
            $"rest --method GET --url \"https://graph.microsoft.com/v1.0/oauth2PermissionGrants?$filter=clientId eq '{blueprintSpObjectId}' and resourceId eq '{resourceSpId}' and consentType eq 'AllPrincipals'\"",
            captureOutput: true,
            suppressErrorLogging: true,
            cancellationToken: ct);

        if (!grantQueryResult.Success)
        {
            logger.LogWarning(
                "  '{Name}': failed to query existing oauth2PermissionGrants (exit {ExitCode}): {Stderr}",
                spec.ResourceName, grantQueryResult.ExitCode, (grantQueryResult.StandardError ?? string.Empty).Trim());
            return false;
        }

        var (existingGrantId, existingScope) = TryExtractFirstGrantIdAndScope(grantQueryResult.StandardOutput);
        var existingScopes = string.IsNullOrWhiteSpace(existingScope)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : existingScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mergedScopes = new HashSet<string>(existingScopes, StringComparer.OrdinalIgnoreCase);
        foreach (var s in spec.Scopes) mergedScopes.Add(s);

        if (existingGrantId is not null && mergedScopes.Count == existingScopes.Count)
        {
            logger.LogInformation("  '{Name}': delegated grant already includes the required scopes — no change needed.", spec.ResourceName);
            return true;
        }

        // 3. PATCH the existing grant (merged scope set) or POST a new one.
        var scopeValue = string.Join(' ', mergedScopes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        if (existingGrantId is not null)
        {
            var patchBody = JsonSerializer.Serialize(new { scope = scopeValue });
            var patched = await ExecuteAzRestWithBodyAsync(
                executor,
                method: "PATCH",
                url: $"https://graph.microsoft.com/v1.0/oauth2PermissionGrants/{existingGrantId}",
                bodyJson: patchBody,
                logger: logger,
                ct: ct);
            if (!patched)
            {
                logger.LogWarning("  '{Name}': PATCH of existing oauth2PermissionGrant failed.", spec.ResourceName);
                return false;
            }
            logger.LogInformation("  '{Name}': delegated grant updated (merged {New} new scope(s) into existing grant).",
                spec.ResourceName, mergedScopes.Count - existingScopes.Count);
            return true;
        }

        var createBody = JsonSerializer.Serialize(new
        {
            clientId = blueprintSpObjectId,
            consentType = "AllPrincipals",
            resourceId = resourceSpId,
            scope = scopeValue,
        });
        var created = await ExecuteAzRestWithBodyAsync(
            executor,
            method: "POST",
            url: "https://graph.microsoft.com/v1.0/oauth2PermissionGrants",
            bodyJson: createBody,
            logger: logger,
            ct: ct);
        if (!created)
        {
            logger.LogWarning("  '{Name}': POST of new oauth2PermissionGrant failed.", spec.ResourceName);
            return false;
        }

        logger.LogInformation("  '{Name}': delegated grant created ({Count} scope(s)).", spec.ResourceName, mergedScopes.Count);
        return true;
    }

    /// <summary>
    /// Writes <paramref name="bodyJson"/> to a temp file and shells out to
    /// <c>az rest --method &lt;method&gt; --url "&lt;url&gt;" --body @&lt;tempfile&gt; --headers Content-Type=application/json</c>.
    /// Temp file because passing JSON inline through Windows <c>cmd.exe</c> and az's own
    /// argv parser requires double-escaping every internal quote, which is fragile across
    /// shells. <c>@&lt;file&gt;</c> is the documented, robust path.
    /// </summary>
    private static async Task<bool> ExecuteAzRestWithBodyAsync(
        CommandExecutor executor,
        string method,
        string url,
        string bodyJson,
        ILogger logger,
        CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"a365-azrest-{Guid.NewGuid():N}.json");
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
    /// Parses an OData collection response (<c>{"value":[{...},...]}</c>) and returns the
    /// <c>id</c> of the first element, or null when the JSON is empty / unparseable / missing
    /// the id property. Used to convert a <c>$filter=appId eq '...'</c> lookup into an SP
    /// object id without needing System.Text.Json elsewhere.
    /// </summary>
    internal static string? TryExtractFirstId(string? azStandardOutput)
    {
        if (string.IsNullOrWhiteSpace(azStandardOutput)) return null;
        try
        {
            using var doc = JsonDocument.Parse(azStandardOutput);
            if (!doc.RootElement.TryGetProperty("value", out var value)) return null;
            if (value.GetArrayLength() == 0) return null;
            if (!value[0].TryGetProperty("id", out var id)) return null;
            if (id.ValueKind != JsonValueKind.String) return null;
            return id.GetString();
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Parses an oauth2PermissionGrants OData collection response and returns the first
    /// element's <c>id</c> and <c>scope</c> (space-separated scope set). Both can be null
    /// when the collection is empty.
    /// </summary>
    internal static (string? GrantId, string? Scope) TryExtractFirstGrantIdAndScope(string? azStandardOutput)
    {
        if (string.IsNullOrWhiteSpace(azStandardOutput)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(azStandardOutput);
            if (!doc.RootElement.TryGetProperty("value", out var value)) return (null, null);
            if (value.GetArrayLength() == 0) return (null, null);
            var first = value[0];
            string? id = first.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;
            string? scope = first.TryGetProperty("scope", out var scEl) && scEl.ValueKind == JsonValueKind.String
                ? scEl.GetString()
                : null;
            return (id, scope);
        }
        catch (JsonException) { return (null, null); }
    }
}
