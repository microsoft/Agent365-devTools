// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Shared validation helper methods for setup subcommands
/// </summary>
public static class ValidationHelper
{
    /// <summary>
    /// Validates that a blueprint exists (shared by Bot and CopilotStudio permissions).
    /// </summary>
    public static Task<List<string>> ValidateBlueprintAsync(
        Agent365Config config,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
        {
            errors.Add("Blueprint ID not found. Run 'a365 setup blueprint' first");
        }

        return Task.FromResult(errors);
    }

    /// <summary>
    /// Validates MCP permissions prerequisites without performing any actions.
    /// </summary>
    public static Task<List<string>> ValidateMcpAsync(
        Agent365Config config,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
        {
            errors.Add("Blueprint ID not found. Run 'a365 setup blueprint' first");
        }

        if (string.IsNullOrWhiteSpace(config.DeploymentProjectPath))
        {
            errors.Add("deploymentProjectPath is required to read toolingManifest.json");
            return Task.FromResult(errors);
        }

        var manifestPath = Path.Combine(config.DeploymentProjectPath, "toolingManifest.json");
        if (!File.Exists(manifestPath))
        {
            errors.Add($"toolingManifest.json not found at {manifestPath}");
        }

        return Task.FromResult(errors);
    }
}