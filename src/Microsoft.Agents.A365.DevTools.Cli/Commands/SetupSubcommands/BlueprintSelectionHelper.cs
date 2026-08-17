// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Validates and applies an existing blueprint selection for <c>setup all</c>.
/// </summary>
internal static class BlueprintSelectionHelper
{
    /// <summary>
    /// Validates blueprint-selection options before setup mutates state.
    /// </summary>
    internal static bool ValidateOptions(
        string? blueprintId,
        bool blueprintIdSpecified,
        bool selectBlueprint,
        bool? aiTeammateFlag,
        bool dryRun,
        bool nonInteractive,
        ILogger logger)
    {
        if (blueprintIdSpecified && selectBlueprint)
        {
            logger.LogError("Options --blueprint-id and --select-blueprint cannot be used together. Choose one.");
            return false;
        }

        if (!blueprintIdSpecified && !selectBlueprint)
            return true;

        var optionName = blueprintIdSpecified ? "--blueprint-id" : "--select-blueprint";

        if (aiTeammateFlag == true)
        {
            logger.LogError(
                "{Option} is not supported with --aiteammate. Explicit blueprint selection applies to blueprint agents only (the default; omit --aiteammate).",
                optionName);
            return false;
        }

        if (blueprintIdSpecified && string.IsNullOrWhiteSpace(blueprintId))
        {
            logger.LogError("--blueprint-id cannot be empty or whitespace. Provide a GUID from 'a365 setup blueprint list'.");
            return false;
        }

        if (blueprintIdSpecified && !Guid.TryParse(blueprintId, out _))
        {
            logger.LogError(
                "Invalid --blueprint-id value '{Value}'. Provide the blueprint's application (client) ID as a GUID; see 'a365 setup blueprint list'.",
                blueprintId);
            return false;
        }

        if (selectBlueprint && nonInteractive)
        {
            logger.LogError(
                "--select-blueprint requires an interactive terminal. Use --blueprint-id <guid> for non-interactive/CI runs (see 'a365 setup blueprint list').");
            return false;
        }

        if (selectBlueprint && dryRun)
        {
            logger.LogError(
                "--select-blueprint requires a real run to query the tenant and cannot be combined with --dry-run. Use --blueprint-id <guid> for a dry-run preview instead.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves and verifies a selected blueprint in the active tenant.
    /// </summary>
    internal static async Task<BlueprintLookupResult?> ResolveAsync(
        BlueprintLookupService blueprintLookupService,
        string tenantId,
        string? blueprintId,
        bool selectBlueprint,
        ILogger logger,
        CancellationToken ct)
    {
        var resolvedAppId = blueprintId;

        if (selectBlueprint)
        {
            IReadOnlyList<BlueprintLookupResult> blueprints;
            try
            {
                blueprints = await blueprintLookupService.ListBlueprintsAsync(tenantId, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to list agent identity blueprints for selection: {Message}", ex.Message);
                return null;
            }

            if (blueprints.Count == 0)
            {
                logger.LogError(
                    "No Agent Identity Blueprints found in tenant {TenantId}. Omit --select-blueprint to create a new one.",
                    tenantId);
                return null;
            }

            logger.LogInformation("Agent Identity Blueprints in tenant {TenantId}:", tenantId);
            using (logger.Indent())
            {
                for (var i = 0; i < blueprints.Count; i++)
                {
                    logger.LogInformation("{Index}. {DisplayName}  (Blueprint ID: {AppId})",
                        i + 1, blueprints[i].DisplayName ?? "(unnamed)", blueprints[i].AppId ?? "(unknown)");
                }
            }
            logger.LogInformation("");

            Console.Write($"Select a blueprint [1-{blueprints.Count}]: ");
            var input = ConsoleHelper.ReadLineCancellable(ct)?.Trim();
            if (!int.TryParse(input, out var choice) || choice < 1 || choice > blueprints.Count)
            {
                logger.LogError(
                    "Invalid selection '{Input}'. Run 'a365 setup all --select-blueprint' again and choose a number from the list.",
                    input);
                return null;
            }

            resolvedAppId = blueprints[choice - 1].AppId;
        }

        if (string.IsNullOrWhiteSpace(resolvedAppId))
        {
            logger.LogError("Could not determine a blueprint application ID to use.");
            return null;
        }

        var lookup = await blueprintLookupService.GetBlueprintByAppIdAsync(tenantId, resolvedAppId, ct);
        if (!lookup.Found)
        {
            if (!string.IsNullOrWhiteSpace(lookup.ErrorMessage))
            {
                logger.LogError(
                    "Could not verify blueprint '{BlueprintId}' in tenant '{TenantId}': {Error}",
                    resolvedAppId, tenantId, lookup.ErrorMessage);
            }
            else
            {
                logger.LogError(
                    "Blueprint '{BlueprintId}' was not found in tenant '{TenantId}'. Verify the ID with 'a365 setup blueprint list' and that it belongs to the currently signed-in tenant.",
                    resolvedAppId, tenantId);
            }
            return null;
        }

        if (string.IsNullOrWhiteSpace(lookup.AppId) ||
            string.IsNullOrWhiteSpace(lookup.ObjectId) ||
            string.IsNullOrWhiteSpace(lookup.DisplayName) ||
            !string.Equals(lookup.AppId, resolvedAppId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "Blueprint '{BlueprintId}' returned incomplete or inconsistent identifiers from Microsoft Graph.",
                resolvedAppId);
            return null;
        }

        return lookup;
    }

    /// <summary>
    /// Applies authoritative blueprint IDs and clears state owned by another blueprint or identity.
    /// </summary>
    internal static Agent365Config ApplyExplicitBlueprintSelection(
        Agent365Config config,
        BlueprintLookupResult blueprint,
        string? cachedAgentIdentityDisplayName = null)
    {
        var isSameBlueprintAsCached = !string.IsNullOrWhiteSpace(config.AgentBlueprintId) &&
            string.Equals(config.AgentBlueprintId, blueprint.AppId, StringComparison.OrdinalIgnoreCase);

        var isSameIdentityAsCached = isSameBlueprintAsCached &&
            !string.IsNullOrWhiteSpace(cachedAgentIdentityDisplayName) &&
            string.Equals(cachedAgentIdentityDisplayName, config.AgentIdentityDisplayName, StringComparison.Ordinal);

        var updated = config.WithAgentBlueprintDisplayName(blueprint.DisplayName);
        updated.AgentBlueprintId = blueprint.AppId;
        updated.AgentBlueprintObjectId = blueprint.ObjectId;
        updated.ResourceConsents = isSameBlueprintAsCached
            ? new(config.ResourceConsents)
            : new();

        if (isSameBlueprintAsCached)
        {
            updated.AgentBlueprintServicePrincipalObjectId = config.AgentBlueprintServicePrincipalObjectId;
            updated.AgentBlueprintClientSecret = config.AgentBlueprintClientSecret;
            updated.AgentBlueprintClientSecretProtected = config.AgentBlueprintClientSecretProtected;
        }
        else
        {
            updated.AgentBlueprintServicePrincipalObjectId = null;
            updated.AgentBlueprintClientSecret = null;
            updated.AgentBlueprintClientSecretProtected = false;
        }

        if (!isSameIdentityAsCached)
        {
            updated.AgentInstanceId = null;
            updated.AgenticAppId = null;
            updated.AgenticUserId = null;
            updated.AgentRegistrationId = null;
            updated.BotId = null;
            updated.BotMsaAppId = null;
            updated.BotMessagingEndpoint = null;
            updated.Completed = false;
            updated.CompletedAt = null;
        }

        return updated;
    }
}
