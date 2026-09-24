// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// Subcommands that report and grant the MCP server permissions an agent identity needs
/// to call a BYO MCP server.
/// </summary>
public static class McpServerPermissionsSubcommands
{
    private const string GrantAgentsAccessCommandName = "grant-agents-access";

    /// <summary>
    /// Creates the grant-agents-access subcommand, which reports the agent instances
    /// of a blueprint that are missing the MCP server scope and offers to grant it.
    /// </summary>
    public static Command CreateGrantAgentsAccessSubcommand(
        ILogger logger,
        McpServerPermissionService permissionService)
    {
        var command = new Command(GrantAgentsAccessCommandName,
            $"Grant the '{McpConstants.V2ScopeValue}' permission for an MCP server to agent identities. " +
            "Lists the blueprint's agent instances missing it and prompts before granting.");

        var blueprintIdOption = new Option<string?>(
            "--agent-blueprint-id",
            description: "Agent blueprint ID (GUID) whose agent instances should be checked. " +
                         $"First-party blueprints: {AgentBlueprintCatalog.FormatForHelp()}.");

        var serverNameOption = new Option<string?>(
            ["--mcp-server-name", "-s"],
            description: "MCP server name. The Entra application is resolved as '{name} - BYO'.")
        {
            IsRequired = true,
        };

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Defaults to the current Azure CLI context.");

        var yesOption = new Option<bool>(
            ["--yes", "-y"],
            description: "Grant the missing permission to every listed agent instance without prompting.");

        var deviceCodeOption = new Option<bool>(
            "--device-code",
            description: "Use device code authentication instead of the interactive browser flow (the WAM broker on Windows). Use when WAM cannot show a sign-in dialog, such as an embedded or remote terminal. Opens https://microsoft.com/devicelogin in your browser.");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            description: "Report which agent instances are missing the permission without granting it.");

        command.AddOption(blueprintIdOption);
        command.AddOption(serverNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(yesOption);
        command.AddOption(deviceCodeOption);
        command.AddOption(dryRunOption);
        command.AddOption(new Option<bool>(["--verbose", "-v"], description: "Enable verbose logging"));

        command.SetHandler(async (InvocationContext context) =>
        {
            var blueprintIdRaw = context.ParseResult.GetValueForOption(blueprintIdOption);
            var serverNameRaw = context.ParseResult.GetValueForOption(serverNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var grantAll = context.ParseResult.GetValueForOption(yesOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var ct = context.GetCancellationToken();

            permissionService.UseDeviceCodeAuthentication = context.ParseResult.GetValueForOption(deviceCodeOption);

            if (!TryValidateGuid(blueprintIdRaw, "--agent-blueprint-id", logger, out var blueprintId,
                    listBlueprints: true))
            {
                context.ExitCode = 1;
                return;
            }

            if (!TryValidateServerName(serverNameRaw, logger, out var serverName))
            {
                context.ExitCode = 1;
                return;
            }

            var tenantId = await ResolveTenantIdAsync(tenantIdFlag, logger);
            if (tenantId is null)
            {
                context.ExitCode = 1;
                return;
            }

            var resource = await permissionService.ResolveServerResourceAsync(
                tenantId, serverName, ct,
                selectAppId: candidates => SelectServerApplication(candidates, serverName, logger, ct));
            if (resource is null)
            {
                context.ExitCode = 1;
                return;
            }

            IReadOnlyList<AgentInstancePermissionStatus> statuses;
            try
            {
                statuses = await permissionService.GetAgentInstanceStatusesAsync(tenantId, blueprintId, resource.ServicePrincipalObjectId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Failed to list agent instances for blueprint {BlueprintId}: {Message}", blueprintId, ex.Message);
                context.ExitCode = 1;
                return;
            }

            if (statuses.Count == 0)
            {
                logger.LogWarning("No agent instances are linked to blueprint {BlueprintId}.", blueprintId);
                context.ExitCode = 1;
                return;
            }

            var missing = statuses.Where(s => !s.HasScope).ToList();
            logger.LogInformation("MCP server       : {DisplayName} ({AppId})", resource.DisplayName, resource.AppId);
            logger.LogInformation("MCP Server Scope : {Scope}", McpConstants.V2ScopeValue);
            logger.LogInformation("Agent Instances  : {Total} total, {Missing} missing the permission", statuses.Count, missing.Count);
            logger.LogInformation("");

            if (missing.Count == 0)
            {
                logger.LogInformation("All agent instances already have the permission. Nothing to do.");
                return;
            }

            for (int i = 0; i < missing.Count; i++)
            {
                logger.LogInformation("  [{Index}] {DisplayName}  {SpObjectId}",
                    i + 1, missing[i].DisplayName ?? "(no display name)", missing[i].ServicePrincipalObjectId);
            }
            logger.LogInformation("");

            if (dryRun)
            {
                logger.LogInformation("[DRY RUN] Would grant '{Scope}' on '{DisplayName}' to the {Count} agent instance(s) above.",
                    McpConstants.V2ScopeValue, resource.DisplayName, missing.Count);
                return;
            }

            var selected = ResolveSelection(missing, grantAll, resource, logger, ct);
            if (selected is null)
            {
                // Invalid input is a failure, not a decision to skip.
                context.ExitCode = 1;
                return;
            }

            if (selected.Count == 0)
            {
                return;
            }

            var failures = await GrantToSelectedAsync(permissionService, tenantId, resource, selected, logger, ct);
            if (failures > 0)
            {
                context.ExitCode = 1;
            }
        });

        return command;
    }

    /// <summary>
    /// Prompts for which application to use when several share the MCP server's display name.
    /// Returns null when there is nobody to ask or the response is not a valid choice, so an
    /// ambiguous name is never resolved by guessing.
    /// </summary>
    private static string? SelectServerApplication(
        IReadOnlyList<string> candidates,
        string serverName,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogWarning("MCP server '{ServerName}' matches {Count} Entra applications:", serverName, candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            logger.LogInformation("  [{Index}] {AppId}", i + 1, candidates[i]);
        }
        logger.LogInformation("");

        if (ConsoleHelper.IsInputRedirected)
        {
            logger.LogError("Input is redirected, so the application cannot be selected interactively. Rename or remove the duplicates so '{ServerName}' resolves to one application.", serverName);
            return null;
        }

        Console.Write($"Enter the number of the application to use (1-{candidates.Count}), or press Enter to cancel: ");
        var response = ConsoleHelper.ReadLineCancellable(ct)?.Trim();

        if (string.IsNullOrWhiteSpace(response))
        {
            logger.LogError("No application selected, so no permissions were granted.");
            return null;
        }

        if (!int.TryParse(response, out var index) || index < 1 || index > candidates.Count)
        {
            logger.LogError("Invalid selection '{Response}'. Enter a number between 1 and {Max}.", response, candidates.Count);
            return null;
        }

        return candidates[index - 1];
    }

    /// <summary>
    /// Determines which instances to grant: all when --yes is set, the user's selection when a
    /// terminal is attached, or none when input is redirected and there is nobody to prompt.
    /// Returns null when the response could not be parsed, which is a failure rather than a
    /// decision to grant nothing.
    /// </summary>
    private static List<AgentInstancePermissionStatus>? ResolveSelection(
        List<AgentInstancePermissionStatus> missing,
        bool grantAll,
        McpServerResource resource,
        ILogger logger,
        CancellationToken ct)
    {
        if (grantAll)
        {
            return missing;
        }

        if (ConsoleHelper.IsInputRedirected)
        {
            logger.LogInformation("Input is redirected, so the agent instances above cannot be selected interactively. Re-run with --yes to grant to all of them.");
            return [];
        }

        Console.Write($"Grant MCP Server {resource.ServerName}'s scope {McpConstants.V2ScopeValue} now? Enter 'all', a comma-separated list of numbers, or press Enter to skip: ");
        var response = ConsoleHelper.ReadLineCancellable(ct)?.Trim();

        if (string.IsNullOrWhiteSpace(response))
        {
            logger.LogInformation("No permissions granted.");
            return [];
        }

        if (string.Equals(response, "all", StringComparison.OrdinalIgnoreCase))
        {
            return missing;
        }

        var selected = new List<AgentInstancePermissionStatus>();
        // Not RemoveEmptyEntries: input like "," or "1," must be rejected, not silently treated
        // as a skip, or a typo would leave agents without the permission and still exit 0.
        foreach (var token in response.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(token, out var index) || index < 1 || index > missing.Count)
            {
                logger.LogError("Invalid selection '{Token}'. Enter numbers between 1 and {Max}, or 'all'.", token, missing.Count);
                return null;
            }

            var instance = missing[index - 1];
            if (!selected.Contains(instance))
            {
                selected.Add(instance);
            }
        }

        if (selected.Count == 0)
        {
            logger.LogError("Invalid selection '{Response}'. Enter numbers between 1 and {Max}, or 'all'.", response, missing.Count);
            return null;
        }

        return selected;
    }

    /// <summary>
    /// Grants the MCP server scope to each selected instance. Returns the number of failures so
    /// the caller can set a non-zero exit code.
    /// </summary>
    private static async Task<int> GrantToSelectedAsync(
        McpServerPermissionService permissionService,
        string tenantId,
        McpServerResource resource,
        List<AgentInstancePermissionStatus> selected,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogInformation("");
        var failures = 0;

        foreach (var instance in selected)
        {
            ct.ThrowIfCancellationRequested();

            bool granted;
            try
            {
                granted = await permissionService.GrantServerScopeAsync(
                    tenantId, instance.ServicePrincipalObjectId, resource.ServicePrincipalObjectId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("  {DisplayName} ({SpObjectId}): {Message}",
                    instance.DisplayName ?? "(no display name)", instance.ServicePrincipalObjectId, ex.Message);
                failures++;
                continue;
            }

            if (granted)
            {
                logger.LogInformation("  Granted: {DisplayName} ({SpObjectId})",
                    instance.DisplayName ?? "(no display name)", instance.ServicePrincipalObjectId);
            }
            else
            {
                logger.LogError("  Failed: {DisplayName} ({SpObjectId})",
                    instance.DisplayName ?? "(no display name)", instance.ServicePrincipalObjectId);
                failures++;
            }
        }

        logger.LogInformation("");
        logger.LogInformation("{Granted} of {Total} grant(s) succeeded.", selected.Count - failures, selected.Count);
        return failures;
    }

    private static bool TryValidateGuid(string? value, string optionName, ILogger logger, out string normalized, bool listBlueprints = false)
    {
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value.Trim(), out var guid))
        {
            logger.LogError(value is null
                ? $"{optionName} is required and must be a GUID."
                : $"{optionName} must be a GUID.");

            // List the IDs here rather than pointing elsewhere: this is the moment the caller
            // needs one, and a redirect costs them another command.
            var lines = listBlueprints ? AgentBlueprintCatalog.FormatAsLines() : [];
            if (lines.Count > 0)
            {
                logger.LogError("First-party blueprints:");
                foreach (var line in lines)
                {
                    logger.LogError("{Blueprint}", line);
                }
            }

            normalized = string.Empty;
            return false;
        }

        normalized = guid.ToString("D");
        return true;
    }

    private static bool TryValidateServerName(string? serverName, ILogger logger, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(serverName))
        {
            logger.LogError("--mcp-server-name must not be empty.");
            return false;
        }

        // The name is interpolated into a Graph OData filter, so reuse the same allowlist
        // register-external-mcp-server applies rather than accepting any non-blank string.
        try
        {
            var validated = DevelopMcpCommand.InputValidator.ValidateInput(serverName, "Server name");
            if (string.IsNullOrWhiteSpace(validated))
            {
                logger.LogError("--mcp-server-name must not be empty.");
                return false;
            }

            normalized = validated;
            return true;
        }
        catch (ArgumentException ex)
        {
            logger.LogError("Invalid --mcp-server-name: {Message}", ex.Message);
            return false;
        }
    }

    private static async Task<string?> ResolveTenantIdAsync(string? tenantIdFlag, ILogger logger)
    {
        // Only a missing option falls back to the Azure CLI context. An explicitly blank value is
        // a mistake, and silently detecting a tenant could act on a different one than intended.
        if (tenantIdFlag is not null)
        {
            if (!Guid.TryParse(tenantIdFlag.Trim(), out var tenantGuid))
            {
                logger.LogError("--tenant-id must be a GUID.");
                return null;
            }
            return tenantGuid.ToString("D");
        }

        var detected = await TenantDetectionHelper.DetectTenantIdAsync(null, logger);
        if (string.IsNullOrWhiteSpace(detected))
        {
            logger.LogError("Tenant ID could not be determined. Pass --tenant-id or run 'az login'.");
            return null;
        }

        return detected;
    }
}
