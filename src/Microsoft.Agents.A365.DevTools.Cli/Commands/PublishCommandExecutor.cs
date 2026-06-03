// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// Raw CLI arguments passed to the develop-mcp publish command.
/// </summary>
internal record RawPublishArgs(
    string? EnvironmentId,
    string? ServerName,
    string? Alias,
    string? DisplayName,
    string? PublisherName,
    bool Yes,
    bool DryRun);

/// <summary>
/// Orchestrates first-party MCP server publish in one CLI command. The shape mirrors
/// <see cref="RegisterCommandExecutor"/> for BYO register: the CLI creates the Public Clients
/// Entra app in the user's own tenant, calls the platform publish endpoint, and back-fills the
/// PPMI scope grant on it after the platform resolves the underlying server's PPMI identity.
/// </summary>
internal class PublishCommandExecutor
{
    // protected (instead of private) on the seam methods + non-sealed class so tests can stub
    // out the parts that hit external systems (Azure CLI for tenant detection). The class is
    // still internal — overrides only happen in the test assembly via InternalsVisibleTo.
    private readonly ILogger _logger;
    private readonly IAgent365ToolingService _toolingService;
    private readonly GraphApiService? _graphApiService;
    private readonly RetryHelper _retryHelper;

    internal PublishCommandExecutor(
        ILogger logger,
        IAgent365ToolingService toolingService,
        GraphApiService? graphApiService)
    {
        _logger = logger;
        _toolingService = toolingService;
        _graphApiService = graphApiService;
        _retryHelper = new RetryHelper(logger, maxRetries: 5, baseDelaySeconds: 3);
    }

    private sealed record ResolvedInput
    {
        public required string EnvironmentId { get; init; }
        public required string ServerName { get; init; }
        public required string Alias { get; init; }
        public required string DisplayName { get; init; }
        public required bool DryRun { get; init; }

        // Null when the caller didn't supply --publisher-name and didn't enter one at the prompt.
        // The platform's v2 publish validator rejects null/empty for custom (user-created) servers
        // and ignores the value for 1p app-based servers (auto-fills "Microsoft"). The CLI can't
        // classify ahead of time without knowing the server's mapping, so it leaves the value
        // null when unspecified and lets the platform decide.
        public string? PublisherName { get; init; }

        // When true, skip the interactive "Proceed with publish? (y/N)" confirmation. Set via
        // --yes / -y. Required for non-interactive contexts (CI scripts, automation).
        public required bool Yes { get; init; }
    }

    internal sealed record EntraAppSet(
        string? PublicClientsClientId,
        string? PublicClientsObjectId,
        string PublicClientsAppName);

    internal async Task<bool> ExecuteAsync(RawPublishArgs args, CancellationToken ct = default)
    {
        var input = ResolveInputs(args);
        if (input is null) return false;

        DisplayPublishSummary(input);

        if (input.DryRun)
        {
            _logger.LogInformation("[DRY RUN] Would create Entra app '{PublicClients}' in tenant", $"{input.ServerName}-PublicClients");
            _logger.LogInformation("[DRY RUN] Would call publish endpoint and back-fill PPMI scope on the created app");
            return true;
        }

        if (!input.Yes)
        {
            Console.Write("Proceed with publish? (y/N): ");
            var confirmation = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (confirmation != "y" && confirmation != "yes")
            {
                Console.WriteLine("Publish cancelled.");
                // User cancellation is not a failure — exit 0. Matches the register command's same
                // prompt-cancel path.
                return true;
            }
        }
        else
        {
            _logger.LogDebug("Skipping interactive confirmation (--yes was supplied).");
        }

        Console.WriteLine();
        ct.ThrowIfCancellationRequested();
        Console.WriteLine($"Publishing MCP server '{input.ServerName}' as '{input.Alias}' to environment {input.EnvironmentId}...");

        var tenantId = await DetectTenantIdAsync();
        if (tenantId is null) return false;

        if (_graphApiService is null)
        {
            _logger.LogError("Graph API service is not available. Cannot create Entra applications.");
            return false;
        }

        var warnings = new List<string>();
        var apps = await CreateEntraAppsAsync(input, tenantId, warnings, ct);
        if (apps is null) return false;

        ct.ThrowIfCancellationRequested();

        var request = new PublishMcpServerRequest
        {
            Alias = input.Alias,
            DisplayName = input.DisplayName,
            PublicClientsAppId = apps.PublicClientsClientId,
            PublisherName = input.PublisherName,
        };

        PublishMcpServerResponse? publishResponse;
        try
        {
            // Hits the platform's v2 publish endpoint via the tooling service, which performs the
            // full elevation orchestration (PPMI provisioning, MOS upload). The platform's v1
            // endpoint remains for older CLI binaries.
            publishResponse = await _toolingService.PublishServerAsync(input.EnvironmentId, input.ServerName, request, ct);
        }
        catch (Exception ex)
        {
            // Caller cancellation (Ctrl+C) should abort fast and predictably rather than be reported
            // as a publish failure and trigger rollback work. Rethrow so the process exits quickly,
            // matching the cancellation handling in the setup flows.
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogError("Failed to publish MCP server '{ServerName}': {Error}", input.ServerName, ex.Message);
            _logger.LogDebug("Exception details: {Exception}", ex.ToString());
            await RollbackEntraAppsAsync(apps, tenantId, ct);
            return false;
        }

        if (publishResponse is null || !publishResponse.IsSuccess)
        {
            var errorMsg = publishResponse?.Message ?? "No response received";
            _logger.LogError("Failed to publish MCP server {ServerName}: {Error}", input.ServerName, errorMsg);
            await RollbackEntraAppsAsync(apps, tenantId, ct);
            return false;
        }

        _logger.LogDebug("Successfully published MCP server {ServerName}", input.ServerName);

        await ConfigureEntraAppsAsync(input, apps, publishResponse, tenantId, warnings, ct);

        DisplayResults(input, warnings);
        return true;
    }

    private ResolvedInput? ResolveInputs(RawPublishArgs args)
    {
        // Dry-run skips interactive prompts so the command stays scriptable / CI-friendly.
        // Missing required values get a clearly-labeled placeholder for summary purposes; the
        // executor short-circuits before any platform call, so the placeholders never leave
        // this process. User-supplied values still go through normal validation.
        const string DryRunPlaceholder = "(unspecified)";

        try
        {
            var environmentId = args.EnvironmentId;
            if (string.IsNullOrWhiteSpace(environmentId))
            {
                environmentId = args.DryRun
                    ? DryRunPlaceholder
                    : DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter Dataverse environment ID: ", "Environment ID");
                if (string.IsNullOrWhiteSpace(environmentId)) { _logger.LogError("Environment ID is required"); return null; }
            }
            else
            {
                environmentId = DevelopMcpCommand.InputValidator.ValidateInput(environmentId, "Environment ID");
                if (environmentId == null) { _logger.LogError("Invalid environment ID format"); return null; }
            }

            var serverName = args.ServerName;
            if (string.IsNullOrWhiteSpace(serverName))
            {
                serverName = args.DryRun
                    ? DryRunPlaceholder
                    : DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter MCP server name to publish: ", "Server name", 100);
                if (string.IsNullOrWhiteSpace(serverName)) { _logger.LogError("Server name is required"); return null; }
            }
            else
            {
                serverName = DevelopMcpCommand.InputValidator.ValidateInput(serverName, "Server name");
                if (serverName == null) { _logger.LogError("Invalid server name format"); return null; }
            }

            var alias = args.Alias;
            if (string.IsNullOrWhiteSpace(alias))
            {
                alias = args.DryRun
                    ? DryRunPlaceholder
                    : DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter alias for the MCP server: ", "Alias", 50);
                if (string.IsNullOrWhiteSpace(alias)) { _logger.LogError("Alias is required"); return null; }
            }
            else
            {
                alias = DevelopMcpCommand.InputValidator.ValidateInput(alias, "Alias", maxLength: 50);
                if (alias == null) { _logger.LogError("Invalid alias format"); return null; }
            }

            var displayName = args.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = args.DryRun
                    ? DryRunPlaceholder
                    : DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter display name for the MCP server: ", "Display name", 30);
                if (string.IsNullOrWhiteSpace(displayName)) { _logger.LogError("Display name is required"); return null; }
            }
            else
            {
                displayName = DevelopMcpCommand.InputValidator.ValidateInput(displayName, "Display name", maxLength: 30);
                if (displayName == null) { _logger.LogError("Invalid display name format"); return null; }
            }

            // Publisher name: optional from the CLI's perspective. The platform's v2 validator
            // requires a non-empty value for custom (user-created) servers and ignores it for
            // 1p app-based servers. Prompt only when the option was omitted entirely (null) — a
            // Microsoft developer publishing msdyn_DataverseMCPServer can just press Enter. An
            // explicitly empty/whitespace value (e.g. --publisher-name "" from a script) is treated
            // as "no publisher" without prompting, so non-interactive automation never hangs. If a
            // custom server ends up with no value, the platform's error message says what's missing.
            // Dry-run also skips the prompt.
            var publisherName = args.PublisherName;
            if (publisherName is null)
            {
                publisherName = args.DryRun
                    ? null
                    : DevelopMcpCommand.InputValidator.PromptAndValidateOptionalInput(
                        "Enter publisher name (optional for 1p Microsoft-owned servers, required otherwise): ",
                        "Publisher name",
                        maxLength: 100);
            }
            else if (string.IsNullOrWhiteSpace(publisherName))
            {
                publisherName = null;
            }
            else
            {
                publisherName = DevelopMcpCommand.InputValidator.ValidateInput(publisherName, "Publisher name", maxLength: 100);
                if (publisherName == null) { _logger.LogError("Invalid publisher name format"); return null; }
            }

            return new ResolvedInput
            {
                EnvironmentId = environmentId,
                ServerName = serverName,
                Alias = alias,
                DisplayName = displayName,
                PublisherName = string.IsNullOrWhiteSpace(publisherName) ? null : publisherName,
                Yes = args.Yes,
                DryRun = args.DryRun,
            };
        }
        catch (ArgumentException ex)
        {
            _logger.LogError("Input validation failed: {Message}", ex.Message);
            return null;
        }
    }

    private void DisplayPublishSummary(ResolvedInput input)
    {
        Console.WriteLine();
        var prevColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Publish Summary");
        Console.WriteLine("===============");
        Console.ForegroundColor = prevColor;
        DevelopMcpCommand.WriteLabel("  Environment:    "); Console.WriteLine(input.EnvironmentId);
        DevelopMcpCommand.WriteLabel("  Server Name:    "); Console.WriteLine(input.ServerName);
        DevelopMcpCommand.WriteLabel("  Alias:          "); Console.WriteLine(input.Alias);
        DevelopMcpCommand.WriteLabel("  Display Name:   "); Console.WriteLine(input.DisplayName);
        DevelopMcpCommand.WriteLabel("  Publisher:      "); Console.WriteLine(input.PublisherName ?? "(none — platform will reject if this is a custom server)");
        Console.WriteLine();
    }

    protected virtual async Task<string?> DetectTenantIdAsync()
    {
        var tenantId = await TenantDetectionHelper.DetectTenantIdAsync(null, _logger);

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogError("Tenant ID could not be determined. Run 'az login' and try again.");
            return null;
        }

        return tenantId;
    }

    private async Task<EntraAppSet?> CreateEntraAppsAsync(ResolvedInput input, string tenantId, List<string> warnings, CancellationToken ct = default)
    {
        var provisioner = new EntraAppProvisioner(_logger, _graphApiService!, _retryHelper);

        var publicClients = await provisioner.CreatePublicClientsAppAsync(
            input.ServerName, tenantId, serviceTreeId: null, warnings, ct);

        return new EntraAppSet(
            PublicClientsClientId: publicClients.ClientId,
            PublicClientsObjectId: publicClients.ObjectId,
            PublicClientsAppName: publicClients.AppName);
    }

    // Best-effort compensating delete for the Entra apps created in CreateEntraAppsAsync, run when
    // the platform publish call fails after app creation. Each delete is wrapped independently so
    // a failure on the first app doesn't skip the second. Failures are logged with both clientId
    // and objectId so the user can clean up manually.
    internal async Task RollbackEntraAppsAsync(EntraAppSet apps, string tenantId, CancellationToken ct = default)
    {
        if (_graphApiService is null)
        {
            _logger.LogWarning("Graph API service is unavailable; cannot roll back Entra app '{PublicClients}'. Delete it manually in the Azure portal.", apps.PublicClientsAppName);
            return;
        }

        _logger.LogInformation("Rolling back Entra app registrations created for failed publish...");

        if (!string.IsNullOrWhiteSpace(apps.PublicClientsObjectId))
        {
            await DeleteOneAsync(apps.PublicClientsObjectId, apps.PublicClientsClientId, apps.PublicClientsAppName, ct);
        }

        async Task DeleteOneAsync(string objectId, string? clientId, string appName, CancellationToken cancellationToken)
        {
            try
            {
                var deleted = await _graphApiService!.DeleteEntraAppAsync(tenantId, objectId, cancellationToken);
                if (deleted)
                {
                    _logger.LogInformation("Rolled back Entra app '{AppName}' (objectId {ObjectId})", appName, objectId);
                }
                else
                {
                    _logger.LogError(
                        "Failed to roll back Entra app '{AppName}' (clientId {ClientId}, objectId {ObjectId}). Delete it manually in the Azure portal.",
                        appName, clientId ?? "<unknown>", objectId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Exception rolling back Entra app '{AppName}' (clientId {ClientId}, objectId {ObjectId}). Delete it manually in the Azure portal.",
                    appName, clientId ?? "<unknown>", objectId);
            }
        }
    }

    private async Task ConfigureEntraAppsAsync(
        ResolvedInput input,
        EntraAppSet apps,
        PublishMcpServerResponse response,
        string tenantId,
        List<string> warnings,
        CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        var concurrentWarnings = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Grant required-resource-access on the just-created Public Clients Entra app.
        // The platform resolves the right resource per server type (Custom: managedidentityid; app-based
        // / Dataverse MCP: 1p mappings; fallback: platform's own app id) and returns both the resource
        // app id and the scope name. We look up the scope guid on the resource app, then add it as
        // requiredResourceAccess on the Entra app we created.
        var resourceAppId = response.McpServerAppId;
        var resourceScopeName = response.McpServerScope;
        Guid? resourceScopeId = null;
        if (!string.IsNullOrWhiteSpace(resourceAppId) && !string.IsNullOrWhiteSpace(resourceScopeName))
        {
            _logger.LogDebug("Resolving scope '{ScopeName}' on underlying server app {AppId}", resourceScopeName, resourceAppId);
            try
            {
                resourceScopeId = await _retryHelper.ExecuteWithRetryAsync(
                    async retryCt => await _graphApiService!.GetOAuth2PermissionScopeIdAsync(
                        tenantId, resourceAppId, resourceScopeName, retryCt),
                    result => !result.HasValue,
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                var msg = $"Could not find '{resourceScopeName}' scope on app {resourceAppId} after retries: {ex.Message}. API permissions not added.";
                _logger.LogError(msg);
                concurrentWarnings.Add(msg);
            }
        }
        else
        {
            var msg = $"Underlying server app id or scope was not returned by publish (appId='{resourceAppId}', scope='{resourceScopeName}'). API permissions not added.";
            _logger.LogWarning(msg);
            concurrentWarnings.Add(msg);
        }

        if (resourceScopeId.HasValue)
        {
            if (apps.PublicClientsObjectId != null)
            {
                tasks.Add(AddRequiredResourceAccessAsync(tenantId, apps.PublicClientsObjectId, apps.PublicClientsAppName, resourceAppId!, resourceScopeId.Value, concurrentWarnings, ct));
            }
        }
        else if (!string.IsNullOrWhiteSpace(resourceAppId) && !string.IsNullOrWhiteSpace(resourceScopeName))
        {
            var msg = $"Could not find '{resourceScopeName}' scope on app {resourceAppId}. API permissions not added.";
            _logger.LogError(msg);
            concurrentWarnings.Add(msg);
        }

        await Task.WhenAll(tasks);

        foreach (var w in concurrentWarnings)
            warnings.Add(w);
    }

    private async Task AddRequiredResourceAccessAsync(
        string tenantId,
        string appObjectId,
        string appName,
        string resourceAppId,
        Guid resourceScopeId,
        System.Collections.Concurrent.ConcurrentBag<string> concurrentWarnings,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug("Adding required-resource-access for resource {ResourceAppId} on '{AppName}' ({ObjectId})", resourceAppId, appName, appObjectId);
            var success = await _retryHelper.ExecuteWithRetryAsync(
                async retryCt => await _graphApiService!.AddRequiredResourceAccessAsync(
                    tenantId, appObjectId, resourceAppId, resourceScopeId, retryCt),
                result => !result,
                cancellationToken: ct);
            if (!success)
            {
                var msg = $"Failed to add required-resource-access on '{appName}' after retries.";
                _logger.LogError(msg);
                concurrentWarnings.Add(msg);
            }
            else
            {
                _logger.LogInformation("Added API permission on '{AppName}'", appName);
            }
        }
        catch (Exception ex)
        {
            var msg = $"Failed to add required-resource-access on '{appName}': {ex.Message}";
            _logger.LogError(msg);
            concurrentWarnings.Add(msg);
        }
    }

    private void DisplayResults(ResolvedInput input, List<string> warnings)
    {
        Console.WriteLine();
        if (warnings.Count == 0)
        {
            var prevColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"MCP server '{input.ServerName}' published as '{input.Alias}' to environment {input.EnvironmentId}.");
            Console.ForegroundColor = prevColor;
        }
        else
        {
            var prevColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"MCP server '{input.ServerName}' was published with {warnings.Count} warning(s):");
            Console.ForegroundColor = prevColor;
            Console.WriteLine();
            foreach (var w in warnings)
            {
                _logger.LogWarning("  - {Warning}", w);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Please ask your tenant admin to approve MCP server '{input.ServerName}' in the Microsoft 365 Admin Center.");
    }
}
