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
/// Subnet injection normally ends with Enable-SubnetInjection from the
/// Microsoft.PowerPlatform.EnterprisePolicies module, which needs the id of the Power Platform
/// environment being linked. Agent 365 does not publish that id, so these subcommands ask the
/// platform to perform the link against the environment it resolves for your tenant.
/// </summary>
public static class NetworkCommand
{
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Creates the network command and its vnet subcommand tree.
    /// </summary>
    public static Command CreateCommand(
        ILogger logger,
        IVNetLinkService vnetLinkService,
        IAzureCliService azureCliService,
        IConfirmationProvider confirmationProvider)
    {
        var networkCommand = new Command(CommandNames.Network, "Configure tenant networking for Agent 365");

        var vnetCommand = new Command(
            "vnet",
            "Link an Azure virtual network enterprise policy to your Agent 365 environment. " +
            "Requires the Global Administrator or Power Platform Administrator role.");

        vnetCommand.AddCommand(CreateLinkSubcommand(logger, vnetLinkService, azureCliService, confirmationProvider));
        vnetCommand.AddCommand(CreateUnlinkSubcommand(logger, vnetLinkService, azureCliService, confirmationProvider));
        vnetCommand.AddCommand(CreateStatusSubcommand(logger, vnetLinkService, azureCliService));

        networkCommand.AddCommand(vnetCommand);
        return networkCommand;
    }

    /// <summary>
    /// Resolves the tenant to authenticate against, or logs why it could not and returns null.
    /// </summary>
    /// <remarks>
    /// An explicitly blank <c>--tenant-id</c> is treated as a mistake rather than as a request for
    /// the default. Falling back silently would run a tenant-wide change against whichever tenant
    /// <c>az</c> happens to be signed in to, which is not what someone who typed the option meant.
    /// </remarks>
    internal static async Task<string?> ResolveTenantIdAsync(
        ILogger logger,
        IAzureCliService azureCliService,
        string? tenantIdOption)
    {
        if (tenantIdOption is not null)
        {
            if (string.IsNullOrWhiteSpace(tenantIdOption))
            {
                logger.LogError(
                    "--tenant-id was supplied but is empty. Pass a tenant id, or omit the option " +
                    "to use the tenant of your current az login.");
                return null;
            }

            return tenantIdOption;
        }

        var account = await azureCliService.GetCurrentAccountAsync();
        var tenantId = account?.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            logger.LogError("Could not determine your Azure tenant. Run 'az login', or pass --tenant-id.");
            return null;
        }

        return tenantId;
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
            $"{action} for tenant {tenantId}. This changes networking for every Agent 365 agent in the tenant. Continue? (y/N): ");
    }

    private static Command CreateLinkSubcommand(
        ILogger logger,
        IVNetLinkService vnetLinkService,
        IAzureCliService azureCliService,
        IConfirmationProvider confirmationProvider)
    {
        var command = new Command(
            "link",
            "Link a NetworkInjection enterprise policy to your Agent 365 environment. " +
            "Create the policy first with New-SubnetInjectionEnterprisePolicy; this replaces the " +
            "Enable-SubnetInjection step that requires an environment id.");

        var policyArmIdOption = new Option<string>(
            ["--policy-arm-id", "-p"],
            "ARM resource id of the NetworkInjection enterprise policy, as returned by " +
            "New-SubnetInjectionEnterprisePolicy")
        {
            IsRequired = true,
        };

        var swapOption = new Option<bool>(
            "--swap",
            "Replace an existing link to a different policy. Without this, an existing different " +
            "link is reported as a conflict rather than silently replaced.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            "Tenant to authenticate against, for both the Azure policy read and the Agent 365 call. " +
            "Defaults to the tenant of your current az login.");

        var waitOption = new Option<bool>(
            "--wait",
            "Keep polling until the link settles, instead of returning an operation id.");

        var verboseOption = new Option<bool>(["--verbose", "-v"], "Enable verbose logging");

        var yesOption = new Option<bool>(
            ["--yes", "-y"],
            "Skip the confirmation prompt.");

        command.AddOption(policyArmIdOption);
        command.AddOption(swapOption);
        command.AddOption(tenantIdOption);
        command.AddOption(waitOption);
        command.AddOption(yesOption);
        command.AddOption(verboseOption);

        command.SetHandler(async (InvocationContext context) =>
        {
            var policyArmId = context.ParseResult.GetValueForOption(policyArmIdOption)!;
            var swap = context.ParseResult.GetValueForOption(swapOption);
            var tenantIdOptionValue = context.ParseResult.GetValueForOption(tenantIdOption);
            var wait = context.ParseResult.GetValueForOption(waitOption);
            var yes = context.ParseResult.GetValueForOption(yesOption);
            var ct = context.GetCancellationToken();

            // IsRequired is satisfied by an explicitly blank value, which would otherwise reach
            // LinkAsync and surface as "Unexpected error occurred. This may be a bug in the CLI."
            if (string.IsNullOrWhiteSpace(policyArmId))
            {
                logger.LogError("--policy-arm-id is required and cannot be blank.");
                context.ExitCode = 1;
                return;
            }

            var tenantId = await ResolveTenantIdAsync(logger, azureCliService, tenantIdOptionValue);
            if (tenantId == null)
            {
                context.ExitCode = 1;
                return;
            }

            // Every link is confirmed, not just --swap. Power Platform documents that enabling
            // subnet delegation can leave the environment unstable for up to 30 minutes while
            // connections re-initialize, and the change is tenant-wide either way.
            var action = swap
                ? "Replace the existing virtual network link"
                : "Link a virtual network";

            if (!await ConfirmChangeAsync(confirmationProvider, yes, action, tenantId))
            {
                logger.LogInformation("Cancelled.");
                context.ExitCode = 1;
                return;
            }

            var result = await vnetLinkService.LinkAsync(policyArmId, swap, tenantId, ct);
            context.ExitCode = await ReportAsync(logger, vnetLinkService, result, wait, "Link", tenantId, ct);
        });

        return command;
    }

    private static Command CreateUnlinkSubcommand(
        ILogger logger,
        IVNetLinkService vnetLinkService,
        IAzureCliService azureCliService,
        IConfirmationProvider confirmationProvider)
    {
        var command = new Command(
            "unlink",
            "Remove the virtual network link from your Agent 365 environment.");

        var waitOption = new Option<bool>(
            "--wait",
            "Keep polling until the unlink settles, instead of returning an operation id.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            "Tenant to authenticate against. Defaults to the tenant of your current az login.");

        var yesOption = new Option<bool>(
            ["--yes", "-y"],
            "Skip the confirmation prompt.");

        var verboseOption = new Option<bool>(["--verbose", "-v"], "Enable verbose logging");

        command.AddOption(waitOption);
        command.AddOption(tenantIdOption);
        command.AddOption(yesOption);
        command.AddOption(verboseOption);

        command.SetHandler(async (InvocationContext context) =>
        {
            var wait = context.ParseResult.GetValueForOption(waitOption);
            var tenantIdOptionValue = context.ParseResult.GetValueForOption(tenantIdOption);
            var yes = context.ParseResult.GetValueForOption(yesOption);
            var ct = context.GetCancellationToken();

            var tenantId = await ResolveTenantIdAsync(logger, azureCliService, tenantIdOptionValue);
            if (tenantId == null)
            {
                context.ExitCode = 1;
                return;
            }

            if (!await ConfirmChangeAsync(
                confirmationProvider, yes, "Remove the virtual network link", tenantId))
            {
                logger.LogInformation("Cancelled.");
                context.ExitCode = 1;
                return;
            }

            var result = await vnetLinkService.UnlinkAsync(tenantId, ct);
            context.ExitCode = await ReportAsync(logger, vnetLinkService, result, wait, "Unlink", tenantId, ct);
        });

        return command;
    }

    private static Command CreateStatusSubcommand(
        ILogger logger,
        IVNetLinkService vnetLinkService,
        IAzureCliService azureCliService)
    {
        var command = new Command(
            "status",
            "Show whether a virtual network policy is linked to your Agent 365 environment.");

        var operationIdOption = new Option<string?>(
            "--operation-id",
            "Operation handle returned by a link or unlink that was still running.");

        var tenantIdOption = new Option<string?>(
            "--tenant-id",
            "Tenant to authenticate against. Defaults to the tenant of your current az login.");

        var verboseOption = new Option<bool>(["--verbose", "-v"], "Enable verbose logging");

        command.AddOption(operationIdOption);
        command.AddOption(tenantIdOption);
        command.AddOption(verboseOption);

        command.SetHandler(async (InvocationContext context) =>
        {
            var operationId = context.ParseResult.GetValueForOption(operationIdOption);
            var tenantIdOptionValue = context.ParseResult.GetValueForOption(tenantIdOption);
            var ct = context.GetCancellationToken();

            var tenantId = await ResolveTenantIdAsync(logger, azureCliService, tenantIdOptionValue);
            if (tenantId == null)
            {
                context.ExitCode = 1;
                return;
            }

            var status = await vnetLinkService.GetStatusAsync(tenantId, operationId, ct);
            if (status == null)
            {
                context.ExitCode = 1;
                return;
            }

            LogStatus(logger, status);
            context.ExitCode = string.Equals(status.Status, "Failed", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        });

        return command;
    }

    /// <summary>
    /// Renders the outcome of a link or unlink, optionally waiting for a running operation first,
    /// and maps it to a process exit code.
    /// </summary>
    internal static async Task<int> ReportAsync(
        ILogger logger,
        IVNetLinkService vnetLinkService,
        VNetStatusResponse? result,
        bool wait,
        string operationLabel,
        string tenantId,
        CancellationToken cancellationToken)
    {
        if (result == null)
        {
            return 1;
        }

        if (wait && VNetLinkService.IsRunning(result.Status) && !string.IsNullOrWhiteSpace(result.OperationId))
        {
            logger.LogInformation("{Operation} is running. Waiting for it to settle...", operationLabel);
            result = await vnetLinkService.WaitForCompletionAsync(tenantId, result.OperationId, DefaultWaitTimeout, cancellationToken);

            if (result == null)
            {
                return 1;
            }
        }

        LogStatus(logger, result);

        if (string.Equals(result.Status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        // The platform answers 200 with Unknown for an operation id it cannot find -- mistyped,
        // expired, or from another tenant. Nothing settled, so exiting 0 would report success for
        // an operation that was never observed to finish. A missing status is the same situation
        // arrived at differently: LogStatus already renders it as Unknown, so exit the same way.
        if (string.IsNullOrWhiteSpace(result.Status)
            || string.Equals(result.Status, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "The operation id is unknown or has expired, so its outcome could not be read. " +
                "Operation ids are scoped to a tenant; check the id and --tenant-id, or read the " +
                "current link with: a365 network vnet status --tenant-id {TenantId}",
                tenantId);
            return 1;
        }

        if (VNetLinkService.IsRunning(result.Status))
        {
            // The platform always returns an id alongside a running status. Without one there is
            // nothing to resume from, so this is an unexpected response rather than a nudge.
            if (string.IsNullOrWhiteSpace(result.OperationId))
            {
                logger.LogError(
                    "{Operation} reported as running but returned no operation id, so it cannot be resumed.",
                    operationLabel);
                return 1;
            }

            logger.LogInformation(
                "{Operation} is still running. Check on it with: " +
                "a365 network vnet status --operation-id {OperationId} --tenant-id {TenantId}",
                operationLabel,
                result.OperationId,
                tenantId);
        }

        return 0;
    }

    private static void LogStatus(ILogger logger, VNetStatusResponse status)
    {
        logger.LogInformation("Status: {Status}", status.Status ?? "Unknown");

        if (!string.IsNullOrWhiteSpace(status.PolicyArmId))
        {
            logger.LogInformation("Policy: {PolicyArmId}", status.PolicyArmId);
        }

        if (!string.IsNullOrWhiteSpace(status.OperationId))
        {
            logger.LogInformation("Operation: {OperationId}", status.OperationId);
        }

        if (!string.IsNullOrWhiteSpace(status.Reason))
        {
            logger.LogWarning("Reason: {Reason}", status.Reason);
        }
    }
}
