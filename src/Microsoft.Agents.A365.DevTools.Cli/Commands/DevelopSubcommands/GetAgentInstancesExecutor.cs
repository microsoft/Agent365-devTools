// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.DevelopSubcommands;

/// <summary>
/// Executor for the get-agent-instances subcommand.
/// Lists agent instance service principals linked to a given blueprint ID.
/// </summary>
internal sealed class GetAgentInstancesExecutor
{
    private readonly ILogger _logger;
    private readonly AgentBlueprintService _blueprintService;

    public GetAgentInstancesExecutor(ILogger logger, AgentBlueprintService blueprintService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _blueprintService = blueprintService ?? throw new ArgumentNullException(nameof(blueprintService));
    }

    /// <summary>
    /// Executes the get-agent-instances command.
    /// </summary>
    /// <param name="blueprintId">Agent Identity Blueprint ID (GUID).</param>
    /// <param name="tenantId">Tenant ID (auto-detected if null).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True on success, false on failure.</returns>
    public async Task<bool> ExecuteAsync(
        string blueprintId,
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blueprintId))
        {
            _logger.LogError("Blueprint ID is required");
            return false;
        }

        if (!Guid.TryParse(blueprintId, out _))
        {
            _logger.LogError("Invalid blueprint ID format: {BlueprintId}. Must be a valid GUID.", blueprintId);
            return false;
        }

        // Resolve tenant ID
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            tenantId = await ResolveTenantIdAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                _logger.LogError("Could not determine tenant ID. Provide --tenant-id or sign in with 'az login'.");
                return false;
            }
        }
        else if (!Guid.TryParse(tenantId, out _))
        {
            _logger.LogError("Invalid tenant ID format: {TenantId}. Must be a valid GUID.", tenantId);
            return false;
        }

        _logger.LogInformation("Listing agent instances for blueprint {BlueprintId} in tenant {TenantId}...", blueprintId, tenantId);

        try
        {
            var instances = await _blueprintService.GetAgentInstancesForBlueprintAsync(tenantId, blueprintId, cancellationToken);

            if (instances.Count == 0)
            {
                _logger.LogInformation("No agent instances found for blueprint {BlueprintId}", blueprintId);
                return true;
            }

            // Table header
            _logger.LogInformation("");
            Console.WriteLine($"{"IdentitySpId",-40} {"DisplayName",-30} {"AgentUserId",-40}");
            Console.WriteLine(new string('-', 112));

            foreach (var instance in instances)
            {
                Console.WriteLine($"{instance.IdentitySpId,-40} {instance.DisplayName ?? "(none)",-30} {instance.AgentUserId ?? "(none)",-40}");
            }

            _logger.LogInformation("");
            _logger.LogInformation("Found {Count} agent instance(s)", instances.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list agent instances for blueprint {BlueprintId}", blueprintId);
            return false;
        }
    }

    private static async Task<string?> ResolveTenantIdAsync(CancellationToken cancellationToken)
    {
        // Use az account show to detect tenant ID from the current Azure CLI session
        try
        {
            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows);
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "az",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (isWindows)
            {
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add("az");
            }
            startInfo.ArgumentList.Add("account");
            startInfo.ArgumentList.Add("show");
            startInfo.ArgumentList.Add("--query");
            startInfo.ArgumentList.Add("tenantId");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("tsv");

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) return null;

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                var tenantId = outputTask.Result.Trim();
                if (!string.IsNullOrWhiteSpace(tenantId))
                    return tenantId;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Non-fatal: fall through to null
        }

        return null;
    }
}
