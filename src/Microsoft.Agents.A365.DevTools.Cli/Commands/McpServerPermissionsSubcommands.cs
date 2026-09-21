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
    private const string ListCommandName = "list-agent-instances";
    private const string GrantCommandName = "grant-mcpserver-permissions";

    /// <summary>
    /// Creates the list-agent-instances subcommand, which reports agent instances of a blueprint
    /// that are missing the MCP server scope and offers to grant it.
    /// </summary>
    public static Command CreateListAgentInstancesSubcommand(
        ILogger logger,
        McpServerPermissionService permissionService)
    {
        var command = new Command(ListCommandName,
            $"List agent instances of a blueprint that are missing the '{McpConstants.V2ScopeValue}' permission for an MCP server.\n" +
            "Offers to grant the permission interactively; prints the equivalent commands when input is redirected.");

        var blueprintIdOption = new Option<string?>(
            "--agent-blueprint-id",
            description: "Agent blueprint ID (GUID) whose agent instances should be checked.")
        {
            IsRequired = true,
        };

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

        command.AddOption(blueprintIdOption);
        command.AddOption(serverNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(yesOption);
        command.AddOption(new Option<bool>(["--verbose", "-v"], description: "Enable verbose logging"));

        command.SetHandler(async (InvocationContext context) =>
        {
            var blueprintIdRaw = context.ParseResult.GetValueForOption(blueprintIdOption);
            var serverName = context.ParseResult.GetValueForOption(serverNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var grantAll = context.ParseResult.GetValueForOption(yesOption);
            var ct = context.GetCancellationToken();

            if (!TryValidateGuid(blueprintIdRaw, "--agent-blueprint-id", logger, out var blueprintId))
            {
                context.ExitCode = 1;
                return;
            }

            if (!TryValidateServerName(serverName, logger))
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

            var resource = await permissionService.ResolveServerResourceAsync(tenantId, serverName!.Trim(), ct);
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
                return;
            }

            var missing = statuses.Where(s => !s.HasScope).ToList();
            logger.LogInformation("MCP server : {DisplayName} ({AppId})", resource.DisplayName, resource.AppId);
            logger.LogInformation("Scope      : {Scope}", McpConstants.V2ScopeValue);
            logger.LogInformation("Instances  : {Total} total, {Missing} missing the permission", statuses.Count, missing.Count);
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

            var selected = ResolveSelection(missing, grantAll, resource, logger, ct);
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
    /// Creates the grant-mcpserver-permissions subcommand, which grants a single agent identity
    /// the MCP server scope.
    /// </summary>
    public static Command CreateGrantPermissionsSubcommand(
        ILogger logger,
        McpServerPermissionService permissionService)
    {
        var command = new Command(GrantCommandName,
            $"Grant an agent identity the '{McpConstants.V2ScopeValue}' permission for an MCP server.\n" +
            "Creates a tenant-wide (AllPrincipals) delegated permission grant.");

        var agentSpIdOption = new Option<string?>(
            "--agent-serviceprincipal-id",
            description: "Object ID (GUID) of the agent identity service principal receiving the permission.")
        {
            IsRequired = true,
        };

        var serverNameOption = new Option<string?>(
            ["--mcp-server-name", "-s"],
            description: "MCP server name. The Entra application is resolved as '{name} - BYO'.")
        {
            IsRequired = true,
        };

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Defaults to the current Azure CLI context.");

        command.AddOption(agentSpIdOption);
        command.AddOption(serverNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(new Option<bool>(["--verbose", "-v"], description: "Enable verbose logging"));

        command.SetHandler(async (InvocationContext context) =>
        {
            var agentSpIdRaw = context.ParseResult.GetValueForOption(agentSpIdOption);
            var serverName = context.ParseResult.GetValueForOption(serverNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var ct = context.GetCancellationToken();

            if (!TryValidateGuid(agentSpIdRaw, "--agent-serviceprincipal-id", logger, out var agentSpId))
            {
                context.ExitCode = 1;
                return;
            }

            if (!TryValidateServerName(serverName, logger))
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

            var resource = await permissionService.ResolveServerResourceAsync(tenantId, serverName!.Trim(), ct);
            if (resource is null)
            {
                context.ExitCode = 1;
                return;
            }

            var granted = await permissionService.GrantServerScopeAsync(tenantId, agentSpId, resource.ServicePrincipalObjectId, ct);
            if (!granted)
            {
                logger.LogError("Failed to grant '{Scope}' on '{DisplayName}' to agent identity {AgentSpId}.",
                    McpConstants.V2ScopeValue, resource.DisplayName, agentSpId);
                context.ExitCode = 1;
                return;
            }

            logger.LogInformation("Granted '{Scope}' on '{DisplayName}' to agent identity {AgentSpId}.",
                McpConstants.V2ScopeValue, resource.DisplayName, agentSpId);
        });

        return command;
    }

    /// <summary>
    /// Determines which instances to grant: all when --yes is set, the user's selection when a
    /// terminal is attached, or none (printing the equivalent commands) when input is redirected.
    /// </summary>
    private static List<AgentInstancePermissionStatus> ResolveSelection(
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

        if (Console.IsInputRedirected)
        {
            logger.LogInformation("Input is redirected. Re-run with --yes to grant, or run the commands below:");
            foreach (var instance in missing)
            {
                logger.LogInformation("  a365 develop-mcp {Command} --agent-serviceprincipal-id {SpObjectId} --mcp-server-name {ServerName}",
                    GrantCommandName, instance.ServicePrincipalObjectId, resource.ServerName);
            }
            return [];
        }

        Console.Write($"Grant '{McpConstants.V2ScopeValue}' now? Enter 'all', a comma-separated list of numbers, or press Enter to skip: ");
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
        foreach (var token in response.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(token, out var index) || index < 1 || index > missing.Count)
            {
                logger.LogError("Invalid selection '{Token}'. Enter numbers between 1 and {Max}, or 'all'.", token, missing.Count);
                return [];
            }

            var instance = missing[index - 1];
            if (!selected.Contains(instance))
            {
                selected.Add(instance);
            }
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

    private static bool TryValidateGuid(string? value, string optionName, ILogger logger, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value.Trim(), out var guid))
        {
            logger.LogError("{OptionName} must be a GUID.", optionName);
            normalized = string.Empty;
            return false;
        }

        normalized = guid.ToString("D");
        return true;
    }

    private static bool TryValidateServerName(string? serverName, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            logger.LogError("--mcp-server-name must not be empty.");
            return false;
        }

        return true;
    }

    private static async Task<string?> ResolveTenantIdAsync(string? tenantIdFlag, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(tenantIdFlag))
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
