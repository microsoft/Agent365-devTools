// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Shared helper for dry-run config loading across commands.
///
/// Pattern: when --dry-run is active the config file (and resolver) must not block
/// execution — the flag must work even in a fresh directory with no a365.config.json.
/// Use this helper instead of duplicating the try/catch guard in every command handler.
/// </summary>
internal static class DryRunHelper
{
    /// <summary>
    /// Attempts to load the Agent365 config for enriching dry-run output.
    /// Never throws; returns null if the config cannot be loaded.
    /// Call this only inside a dry-run block — not as a replacement for the
    /// real resolver call that runs when dry-run is false.
    /// </summary>
    internal static async Task<Agent365Config?> TryLoadConfigForDryRunAsync(
        string? agentName,
        string? tenantIdFlag,
        FileInfo configFile,
        IBootstrapConfigResolver? resolver,
        IConfigService configService,
        bool isCleanupMode = false,
        CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(agentName) && resolver != null)
                return await resolver.ResolveAsync(agentName, tenantIdFlag, configFile, isCleanupMode, ct);

            // When no bootstrap resolver is available, call LoadAsync directly.
            // FileNotFoundException is caught below — config is optional for dry-run.
            if (resolver == null)
                return await configService.LoadAsync(configFile.FullName);

            // Resolver is present but no agentName: only load if the file exists to avoid
            // the resolver logging a spurious ERROR for a missing config file.
            if (configFile.Exists)
                return await configService.LoadAsync(configFile.FullName);

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
