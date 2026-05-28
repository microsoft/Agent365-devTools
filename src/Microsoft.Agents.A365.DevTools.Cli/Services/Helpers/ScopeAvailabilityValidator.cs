// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Pre-flight validator that filters a permission spec list against what each resource
/// service principal actually exposes in the tenant. Issue #429: the unified
/// <c>/v2.0/adminconsent</c> endpoint rejects the entire URL with AADSTS650053 when any
/// requested scope does not exist on its resource SP — a single bad scope blocks every
/// other resource. This helper queries Graph once per resource SP and drops missing scopes
/// from the spec list before <see cref="BatchPermissionsOrchestrator"/> builds the URL.
/// <para>
/// The helper is intentionally pure (no console I/O, no PowerShell, no decisions about
/// "should we fall back" — those live in the orchestrator). The result reports both the
/// effective spec list to use for the URL and the dropped scopes so the orchestrator can
/// surface warnings and offer the PowerShell fallback for users who want to stamp them
/// anyway via the programmatic <c>oauth2PermissionGrants</c> path (which is lenient and
/// will create the grant row even for non-existent scopes — useful when a resource SP is
/// expected to expose the scope shortly, e.g. just-registered first-party SP).
/// </para>
/// </summary>
internal static class ScopeAvailabilityValidator
{
    /// <summary>
    /// Validates each spec's <c>Scopes</c> against the delegated scopes the resource SP
    /// actually exposes. Returns the filtered specs and a per-resource breakdown of what
    /// was dropped. Specs whose SP cannot be resolved are passed through unchanged — the
    /// caller already handles missing SPs in <see cref="BatchPermissionsOrchestrator"/>
    /// Phase 1 and not having a resolved SP id is not the same as "the SP exposes nothing."
    /// </summary>
    /// <param name="graph">Graph API service used to query each resource SP's published scopes.</param>
    /// <param name="tenantId">Tenant ID for the Graph calls.</param>
    /// <param name="specs">Input specs to validate.</param>
    /// <param name="resourceSpObjectIds">
    /// Map from resource appId to resolved SP object id (Phase 1 output). Specs whose appId
    /// is missing from this map are passed through unchanged.
    /// </param>
    /// <param name="logger">Logger for debug-level breadcrumbs only — the caller is responsible for user-facing warnings.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<ValidationResult> ValidateAsync(
        GraphApiService graph,
        string tenantId,
        IReadOnlyList<ResourcePermissionSpec> specs,
        IReadOnlyDictionary<string, string> resourceSpObjectIds,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(resourceSpObjectIds);
        ArgumentNullException.ThrowIfNull(logger);

        var effective = new List<ResourcePermissionSpec>(specs.Count);
        var dropped = new List<DroppedScope>();

        foreach (var spec in specs)
        {
            // No SP id means Phase 1 could not resolve it. Pass through — keeping the
            // pre-existing behavior where a missing SP is logged in Phase 1 and the rest
            // of the orchestrator decides what to do. Validating against an empty set
            // would silently drop every scope on the resource, which is far worse than
            // surfacing AADSTS650053 if it ever gets that far.
            if (!resourceSpObjectIds.TryGetValue(spec.ResourceAppId, out var spObjectId) ||
                string.IsNullOrWhiteSpace(spObjectId))
            {
                effective.Add(spec);
                continue;
            }

            // Nothing to filter — keep as-is.
            if (spec.Scopes is not { Length: > 0 })
            {
                effective.Add(spec);
                continue;
            }

            // GetAvailableScopeNamesAsync can throw (transient Graph failure, disposed
            // JsonDocument in stubbed tests, network error). Treat any exception the same
            // way as "returned no scopes" — pass through unchanged. Validation is a safety
            // net; we'd rather miss a filter opportunity than block setup on a side-quest
            // error inside the validator.
            HashSet<string> available;
            try
            {
                available = await graph.GetAvailableScopeNamesAsync(tenantId, spObjectId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "Could not query published scopes for '{ResourceName}' ({AppId}); passing spec through unchanged.",
                    spec.ResourceName, spec.ResourceAppId);
                effective.Add(spec);
                continue;
            }

            if (available.Count == 0)
            {
                // Graph call returned no scopes. Could be the SP genuinely exposes none,
                // or the call failed silently (GetAvailableScopeNamesAsync swallows errors
                // and returns an empty set). Pass through unchanged — dropping every scope
                // here would block setup the same way AADSTS650053 does, just from a
                // different cause; the orchestrator's existing handling is preferable.
                logger.LogDebug(
                    "Resource '{ResourceName}' ({AppId}) returned no published delegated scopes — passing spec through unchanged.",
                    spec.ResourceName, spec.ResourceAppId);
                effective.Add(spec);
                continue;
            }

            var validScopes = new List<string>(spec.Scopes.Length);
            foreach (var scope in spec.Scopes)
            {
                if (available.Contains(scope))
                    validScopes.Add(scope);
                else
                    dropped.Add(new DroppedScope(spec.ResourceName, spec.ResourceAppId, scope));
            }

            effective.Add(spec with { Scopes = validScopes.ToArray() });
        }

        return new ValidationResult(effective, dropped);
    }

    /// <summary>
    /// Outcome of <see cref="ValidateAsync"/>.
    /// </summary>
    /// <param name="EffectiveSpecs">
    /// Spec list with unavailable scopes removed. Caller passes this to the consent URL
    /// builder. A spec with all scopes dropped is preserved with an empty <c>Scopes</c>
    /// array; the URL builder already filters those out.
    /// </param>
    /// <param name="DroppedScopes">
    /// One entry per (resource, scope) pair that was filtered out. Used by the caller to
    /// emit user-facing warnings and decide whether to offer the PowerShell fallback.
    /// </param>
    public sealed record ValidationResult(
        IReadOnlyList<ResourcePermissionSpec> EffectiveSpecs,
        IReadOnlyList<DroppedScope> DroppedScopes);

    /// <summary>
    /// A single scope that was filtered out because the resource SP does not expose it.
    /// </summary>
    public sealed record DroppedScope(string ResourceName, string ResourceAppId, string Scope);
}
