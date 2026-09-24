// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// Tenant network configuration for Agent 365.
///
/// Global Secure Access is normally set per Power Platform environment, which needs the id of the
/// environment being configured. Agent 365 does not publish that id, so these subcommands ask the
/// platform to apply the setting to the environment it resolves for your tenant.
/// </summary>
public static class NetworkCommand
{
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Creates the network command and its gsa subcommand tree.
    /// </summary>
    public static Command CreateCommand(
        ILogger logger,
        IAzureCliService azureCliService,
        IGsaService gsaService,
        IConfirmationProvider confirmationProvider)
    {
        var networkCommand = new Command(CommandNames.Network, "Configure tenant networking for Agent 365");

        var gsaCommand = new Command(
            "gsa",
            "Turn Global Secure Access on or off for your Agent 365 environment. " +
            "Requires the Global Administrator or Power Platform Administrator role.");

        gsaCommand.AddCommand(CreateGsaSetSubcommand(
            logger, gsaService, azureCliService, confirmationProvider, enabled: true));
        gsaCommand.AddCommand(CreateGsaSetSubcommand(
            logger, gsaService, azureCliService, confirmationProvider, enabled: false));
        gsaCommand.AddCommand(CreateGsaStatusSubcommand(logger, gsaService, azureCliService));

        networkCommand.AddCommand(gsaCommand);
        return networkCommand;
    }

    /// <summary>
    /// Resolves the Azure account to act as, or logs why it could not and returns null.
    /// </summary>
    /// <remarks>
    /// Resolved once per invocation and passed to every call that follows. <c>az account show</c>
    /// reads mutable local state, so reading it again for authentication after prompting could
    /// confirm one tenant and change another, and a transient CLI failure between two reads could
    /// fail a command that had already succeeded at resolving the tenant.
    /// </remarks>
    internal static async Task<AzureAccountInfo?> ResolveAccountAsync(
        ILogger logger,
        IAzureCliService azureCliService)
    {
        var account = await azureCliService.GetCurrentAccountAsync();
        if (account is null || string.IsNullOrWhiteSpace(account.TenantId))
        {
            logger.LogError("Could not determine your Azure tenant. Run 'az login' and try again.");
            return null;
        }

        return account;
    }

    /// <summary>
    /// Asks the operator to confirm a change to tenant-wide networking, naming the tenant and the
    /// action so the prompt is answerable without scrolling back.
    /// </summary>
    internal static async Task<bool> ConfirmChangeAsync(
        IConfirmationProvider confirmationProvider,
        bool yes,
        string action,
        string tenantId)
    {
        if (yes)
        {
            return true;
        }

        return await confirmationProvider.ConfirmAsync(
            $"{action} for tenant {tenantId}. This changes networking for every Agent 365 agent in the tenant. Continue?");
    }

    /// <summary>
    /// Creates the gsa enable or disable subcommand. The two differ only in the value they send
    /// and the words they use, so they share one builder.
    /// </summary>
    private static Command CreateGsaSetSubcommand(
        ILogger logger,
        IGsaService gsaService,
        IAzureCliService azureCliService,
        IConfirmationProvider confirmationProvider,
        bool enabled)
    {
        var verb = enabled ? "enable" : "disable";
        var command = new Command(
            verb,
            $"Turn Global Secure Access {(enabled ? "on" : "off")} for your Agent 365 environment.");

        var waitOption = new Option<bool>(
            "--wait",
            "Keep polling until the change appears on the environment, instead of returning while " +
            "it is still being applied.");

        var yesOption = new Option<bool>(
            ["--yes", "-y"],
            "Skip the confirmation prompt.");

        var verboseOption = new Option<bool>(["--verbose", "-v"], "Enable verbose logging");

        command.AddOption(waitOption);
        command.AddOption(yesOption);
        command.AddOption(verboseOption);

        command.SetHandler(async (InvocationContext context) =>
        {
            var wait = context.ParseResult.GetValueForOption(waitOption);
            var yes = context.ParseResult.GetValueForOption(yesOption);
            var ct = context.GetCancellationToken();

            // Resolved once, then used for both the prompt and the call, so the tenant named in
            // the prompt is provably the tenant changed.
            var account = await ResolveAccountAsync(logger, azureCliService);
            if (account == null)
            {
                context.ExitCode = 1;
                return;
            }

            if (!await ConfirmChangeAsync(
                confirmationProvider, yes, $"Turn Global Secure Access {(enabled ? "on" : "off")}", account.TenantId))
            {
                logger.LogInformation("Cancelled.");
                context.ExitCode = 1;
                return;
            }

            var result = await gsaService.SetAsync(account, enabled, ct);
            context.ExitCode = await ReportGsaAsync(logger, gsaService, account, result, wait, enabled, ct);
        });

        return command;
    }

    private static Command CreateGsaStatusSubcommand(
        ILogger logger,
        IGsaService gsaService,
        IAzureCliService azureCliService)
    {
        var command = new Command(
            "status",
            "Show whether Global Secure Access is on for your Agent 365 environment.");

        var verboseOption = new Option<bool>(["--verbose", "-v"], "Enable verbose logging");
        command.AddOption(verboseOption);

        command.SetHandler(async (InvocationContext context) =>
        {
            var ct = context.GetCancellationToken();

            var account = await ResolveAccountAsync(logger, azureCliService);
            if (account == null)
            {
                context.ExitCode = 1;
                return;
            }

            var status = await gsaService.GetStatusAsync(account, ct);
            if (status == null)
            {
                context.ExitCode = 1;
                return;
            }

            LogGsaStatus(logger, status);
            context.ExitCode = 0;
        });

        return command;
    }

    /// <summary>
    /// Renders the outcome of a Global Secure Access change, optionally waiting for it to appear
    /// first, and maps it to a process exit code.
    /// </summary>
    internal static async Task<int> ReportGsaAsync(
        ILogger logger,
        IGsaService gsaService,
        AzureAccountInfo account,
        GsaStatusResponse? result,
        bool wait,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (result == null)
        {
            return 1;
        }

        var expectedStatus = enabled ? "Enabled" : "Disabled";

        if (wait && result.Pending)
        {
            logger.LogInformation("The change is still being applied. Waiting for it to appear...");
            result = await gsaService.WaitForStatusAsync(account, expectedStatus, DefaultWaitTimeout, cancellationToken);

            if (result == null)
            {
                return 1;
            }
        }

        LogGsaStatus(logger, result);

        // Still pending is not a failure. The platform accepted the change and the environment
        // will catch up; reporting non-zero here would break scripts that chain on success.
        if (result.Pending)
        {
            logger.LogInformation(
                "Still being applied. Check on it with: a365 network gsa status");
        }

        return 0;
    }

    private static void LogGsaStatus(ILogger logger, GsaStatusResponse status)
    {
        logger.LogInformation("Global Secure Access: {Status}", status.Status ?? "Unknown");

        if (string.Equals(status.Status, "NotConfigured", StringComparison.OrdinalIgnoreCase))
        {
            // Worth spelling out: a tenant that has never set this is not the same as one that
            // turned it off, and the distinction changes what an admin should do next.
            logger.LogInformation("This tenant has never set Global Secure Access, so no value is stored.");
        }

        if (!string.IsNullOrWhiteSpace(status.Reason))
        {
            logger.LogWarning("Reason: {Reason}", status.Reason);
        }
    }
}
