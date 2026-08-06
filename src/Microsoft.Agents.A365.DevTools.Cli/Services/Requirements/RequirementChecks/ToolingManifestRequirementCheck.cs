// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Validates the ToolingManifest.json schema when present.
/// Checks that the manifest is valid JSON, has required fields, and has no duplicate server names.
/// </summary>
public class ToolingManifestRequirementCheck : RequirementCheck
{
    /// <inheritdoc />
    public override string Name => "Tooling Manifest";

    /// <inheritdoc />
    public override string Description => "Validates ToolingManifest.json schema and server configuration";

    /// <inheritdoc />
    public override string Category => "Configuration";

    /// <inheritdoc />
    public override async Task<RequirementCheckResult> CheckAsync(
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteCheckWithLoggingAsync(config, logger, CheckImplementationAsync, cancellationToken);
    }

    private async Task<RequirementCheckResult> CheckImplementationAsync(
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var projectPath = ResolveProjectPath(config);
        var manifestPath = Path.Combine(projectPath, McpConstants.ToolingManifestFileName);

        if (!File.Exists(manifestPath))
        {
            // ToolingManifest.json is optional — agents that do not use MCP tool servers
            // will not have one. Returning Success here is intentional.
            return RequirementCheckResult.Success("ToolingManifest.json not present, skipping");
        }

        ToolingManifest? manifest;
        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            manifest = JsonSerializer.Deserialize<ToolingManifest>(json);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to parse ToolingManifest.json at {ManifestPath}", manifestPath);
            return RequirementCheckResult.Failure(
                "ToolingManifest.json contains invalid JSON",
                "Fix the JSON syntax in ToolingManifest.json and try again");
        }

        if (manifest is null || manifest.McpServers is null)
        {
            return RequirementCheckResult.Failure(
                "ToolingManifest.json is invalid: mcpServers must be an array",
                "Ensure ToolingManifest.json contains a valid JSON object with an mcpServers array");
        }

        var errors = manifest.GetValidationErrors();
        if (errors.Length > 0)
        {
            return RequirementCheckResult.Failure(
                $"ToolingManifest.json has {errors.Length} validation error(s):\n" +
                    string.Join("\n", errors.Select(e => $"  - {e}")),
                "Fix the reported issues in ToolingManifest.json and run 'a365 validate' again");
        }

        return RequirementCheckResult.Success(
            $"{manifest.McpServers.Length} MCP server(s) configured");
    }

    /// <summary>
    /// Returns deploymentProjectPath if configured, otherwise falls back to the current directory.
    /// </summary>
    private static string ResolveProjectPath(Agent365Config config)
    {
        return string.IsNullOrWhiteSpace(config.DeploymentProjectPath)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(config.DeploymentProjectPath);
    }
}
