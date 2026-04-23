// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// Status command - displays local configuration and optionally live Entra state.
/// Replaces the deleted 'config display' subcommand.
/// Always exits 0 — status is read-only and works with or without a config file.
/// </summary>
public class StatusCommand
{
    public static Command CreateCommand(
        ILogger logger,
        IConfigService configService,
        GraphApiService? graphApiService = null,
        IBootstrapConfigResolver? resolver = null)
    {
        var command = new Command("status", "Display the status of an agent");

        var agentNameOption = new Option<string?>(
            ["--agent-name", "-n"],
            description: "Agent base name. When provided, no config file is required.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            description: "Azure AD tenant ID. Overrides auto-detection. Use with --agent-name.");

        var offlineOption = new Option<bool>(
            "--offline",
            description: "Skip live Entra checks. Only show local file and config state.");

        var fieldOption = new Option<string?>(
            "--field",
            description: "Output a single field value for scripting. " +
                         "Valid: TenantId, ClientAppId, AgentBlueprintId, AgentInstanceId, AgentRegistrationId, AgenticAppId.");

        command.AddOption(agentNameOption);
        command.AddOption(tenantIdOption);
        command.AddOption(offlineOption);
        command.AddOption(fieldOption);

        command.SetHandler(async (System.CommandLine.Invocation.InvocationContext context) =>
        {
            var configFile = new FileInfo("a365.config.json");
            var agentName = context.ParseResult.GetValueForOption(agentNameOption);
            var tenantIdFlag = context.ParseResult.GetValueForOption(tenantIdOption);
            var offline = context.ParseResult.GetValueForOption(offlineOption);
            var fieldFilter = context.ParseResult.GetValueForOption(fieldOption);
            var ct = context.GetCancellationToken();

            Agent365Config? config = null;
            string? loadError = null;

            // Only invoke the resolver when it can actually do something:
            // - --agent-name given  → bootstrap from Entra
            // - config file present → load from file
            // When neither is true, leave config null and let the "Files" section explain the state.
            bool shouldResolve = !string.IsNullOrWhiteSpace(agentName) || configFile.Exists;

            if (shouldResolve)
            {
                if (resolver != null)
                {
                    try
                    {
                        config = await resolver.ResolveAsync(agentName, tenantIdFlag, configFile, isCleanupMode: false, ct);
                    }
                    catch (Exception ex)
                    {
                        loadError = ex.Message;
                    }
                }
                else
                {
                    try
                    {
                        config = await configService.LoadAsync(configFile.FullName);
                    }
                    catch (Exception ex)
                    {
                        loadError = ex.Message;
                    }
                }
            }

            // --field: machine-readable single-value output
            if (!string.IsNullOrWhiteSpace(fieldFilter))
            {
                var supportedFields = new Dictionary<string, Func<Agent365Config, string?>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TenantId"]                  = c => c.TenantId,
                    ["ClientAppId"]               = c => c.ClientAppId,
                    ["AgentIdentityDisplayName"]  = c => c.AgentIdentityDisplayName,
                    ["AgentBlueprintDisplayName"] = c => c.AgentBlueprintDisplayName,
                    ["MessagingEndpoint"]         = c => c.MessagingEndpoint,
                    ["AgentBlueprintId"]          = c => c.AgentBlueprintId,
                    ["AgentInstanceId"]           = c => c.AgentInstanceId,
                    ["AgentRegistrationId"]       = c => c.AgentRegistrationId,
                    ["AgenticAppId"]              = c => c.AgenticAppId,
                };

                if (!supportedFields.TryGetValue(fieldFilter, out var accessor))
                {
                    var valid = string.Join(", ", supportedFields.Keys);
                    logger.LogError("Unknown field '{Field}'. Valid fields: {Valid}.", fieldFilter, valid);
                    context.ExitCode = 1;
                    return;
                }

                var value = config is null ? "(not set)" : accessor(config).OrNone();
                Console.WriteLine(value);
                return;
            }

            if (config is null)
            {
                if (!string.IsNullOrWhiteSpace(loadError))
                    logger.LogWarning("Could not load configuration: {Error}", loadError);
                else
                    logger.LogInformation("No agent configuration found. Pass --agent-name <name> to check agent status.");
                return;
            }

            logger.LogDebug("Configuration loaded from {ConfigFile}", configFile.FullName);

            // Static config
            logger.LogInformation("");
            logger.LogInformation("Configuration");
            logger.LogInformation("  TenantId                  : {Value}", config.TenantId.OrNone());
            logger.LogInformation("  ClientAppId               : {Value}", config.ClientAppId.OrNone());
            logger.LogInformation("  AgentIdentityDisplayName  : {Value}", config.AgentIdentityDisplayName.OrNone());
            logger.LogInformation("  AgentBlueprintDisplayName : {Value}", (config.AgentBlueprintDisplayName ?? string.Empty).OrNone());
            logger.LogInformation("  MessagingEndpoint         : {Value}", config.MessagingEndpoint.OrNone());
            logger.LogInformation("  Environment               : {Value}", config.Environment.OrNone());

            // Generated config
            logger.LogInformation("");
            logger.LogInformation("Generated State");
            logger.LogInformation("  AgentBlueprintId          : {Value}", (config.AgentBlueprintId ?? string.Empty).OrNone());
            logger.LogInformation("  AgentInstanceId           : {Value}", (config.AgentInstanceId ?? string.Empty).OrNone());
            logger.LogInformation("  AgentRegistrationId       : {Value}", (config.AgentRegistrationId ?? string.Empty).OrNone());
            logger.LogInformation("  AgenticAppId              : {Value}", (config.AgenticAppId ?? string.Empty).OrNone());
            logger.LogInformation("  Completed                 : {Value}", config.Completed ? "yes" : "no");
            if (config.LastUpdated.HasValue)
                logger.LogInformation("  LastUpdated               : {Value}", config.LastUpdated.Value.ToString("o"));

            if (offline || graphApiService is null)
                return;

            // Live Entra checks
            if (string.IsNullOrWhiteSpace(config.TenantId) || string.IsNullOrWhiteSpace(config.AgentBlueprintId))
            {
                logger.LogInformation("");
                logger.LogInformation("Live Entra checks skipped: TenantId or AgentBlueprintId not set.");
                return;
            }

            logger.LogInformation("");
            logger.LogInformation("Live Entra State (blueprint app)");
            try
            {
                var spObjectId = await graphApiService.LookupServicePrincipalByAppIdAsync(
                    config.TenantId, config.AgentBlueprintId);
                logger.LogInformation("  Service principal         : {Value}", spObjectId ?? "(not found)");
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Service principal lookup failed");
                logger.LogInformation("  Service principal         : (lookup failed — run with --verbose for details)");
            }
        });

        return command;
    }
}

internal static class StringStatusExtensions
{
    internal static string OrNone(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? "(not set)" : s;
}
