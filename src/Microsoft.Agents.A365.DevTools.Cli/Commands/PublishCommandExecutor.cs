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
    bool DryRun);

/// <summary>
/// Orchestrates first-party MCP server publish in one CLI command. The shape mirrors
/// <see cref="RegisterCommandExecutor"/> for BYO register: the CLI creates the Public Clients
/// Entra app in the user's own tenant, calls the platform publish endpoint, and back-fills the
/// PPMI scope grant on it after the platform resolves the underlying server's PPMI identity.
/// NOTE: A365 Proxy Entra app creation + redirect-URI back-fill are TEMPORARILY DISABLED while
/// the platform-side custom connector flow is commented out (see PublishMCPServerV2Async in
/// MCPDataverseEnvironmentService). Reinstate the proxy-related blocks below together with the
/// platform-side flow.
/// </summary>
internal class PublishCommandExecutor
{
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
    }

    internal sealed record EntraAppSet(
        string A365AppClientId,
        string A365AppSecret,
        string A365AppObjectId,
        string A365AppName,
        string? PublicClientsClientId,
        string? PublicClientsObjectId,
        string PublicClientsAppName);

    internal async Task ExecuteAsync(RawPublishArgs args, CancellationToken ct = default)
    {
        var input = ResolveInputs(args);
        if (input is null) return;

        DisplayPublishSummary(input);

        if (input.DryRun)
        {
            // TEMPORARILY DISABLED: A365 Proxy dry-run lines (proxy app creation + redirect URI
            // back-fill). Reinstate together with the corresponding logic in CreateEntraAppsAsync
            // and ConfigureEntraAppsAsync.
            /*
            _logger.LogInformation("[DRY RUN] Would create Entra apps '{A365}' and '{PublicClients}' in tenant", $"{input.ServerName}-A365Proxy", $"{input.ServerName}-PublicClients");
            _logger.LogInformation("[DRY RUN] Would call publish endpoint and back-fill redirect URI + PPMI scope on the created apps");
            */
            _logger.LogInformation("[DRY RUN] Would create Entra app '{PublicClients}' in tenant", $"{input.ServerName}-PublicClients");
            _logger.LogInformation("[DRY RUN] Would call publish endpoint and back-fill PPMI scope on the created app");
            return;
        }

        Console.Write("Proceed with publish? (y/N): ");
        var confirmation = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (confirmation != "y" && confirmation != "yes")
        {
            Console.WriteLine("Publish cancelled.");
            return;
        }

        Console.WriteLine();
        ct.ThrowIfCancellationRequested();
        Console.WriteLine($"Publishing MCP server '{input.ServerName}' as '{input.Alias}' to environment {input.EnvironmentId}...");

        var tenantId = await DetectTenantIdAsync();
        if (tenantId is null) return;

        if (_graphApiService is null)
        {
            _logger.LogError("Graph API service is not available. Cannot create Entra applications.");
            return;
        }

        var warnings = new List<string>();
        var apps = await CreateEntraAppsAsync(input, tenantId, warnings);
        if (apps is null) return;

        ct.ThrowIfCancellationRequested();

        // TEMPORARILY DISABLED: A365 Proxy client-id parse + creds assignment to the publish request.
        // The platform's v2 publish ignores these fields while the custom connector flow is
        // disabled (see PublishMCPServerV2Async Step 3 comment block in MCPDataverseEnvironmentService).
        // Reinstate the parse block, the apps == null short-circuit above, and the two request
        // fields together when the proxy flow returns.
        /*
        if (!Guid.TryParse(apps.A365AppClientId, out var a365ProxyClientId))
        {
            _logger.LogError("A365 Proxy Entra app returned an invalid client ID '{ClientId}'. Expected a GUID. Cannot continue publish.", apps.A365AppClientId);
            await RollbackEntraAppsAsync(apps, tenantId, ct);
            return;
        }
        */

        var request = new PublishMcpServerRequest
        {
            Alias = input.Alias,
            DisplayName = input.DisplayName,
            // A365ProxyClientId = a365ProxyClientId,
            // A365ProxyClientSecret = apps.A365AppSecret,
            PublicClientsAppId = apps.PublicClientsClientId,
            PublisherName = input.PublisherName,
        };

        PublishMcpServerResponse? publishResponse;
        try
        {
            // Hits the platform's v2 publish endpoint via the tooling service, which performs the
            // full elevation orchestration (PPMI provisioning, MOS upload). A365 Proxy CMS
            // connector creation is TEMPORARILY DISABLED on the platform side (see
            // PublishMCPServerV2Async Step 3 comment block) while the custom connector flow is
            // being re-evaluated. The platform's v1 endpoint remains for older CLI binaries.
            publishResponse = await _toolingService.PublishServerAsync(input.EnvironmentId, input.ServerName, request, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to publish MCP server '{ServerName}': {Error}", input.ServerName, ex.Message);
            _logger.LogDebug("Exception details: {Exception}", ex.ToString());
            await RollbackEntraAppsAsync(apps, tenantId, ct);
            return;
        }

        if (publishResponse is null || !publishResponse.IsSuccess)
        {
            var errorMsg = publishResponse?.Message ?? "No response received";
            _logger.LogError("Failed to publish MCP server {ServerName}: {Error}", input.ServerName, errorMsg);
            await RollbackEntraAppsAsync(apps, tenantId, ct);
            return;
        }

        _logger.LogDebug("Successfully published MCP server {ServerName}", input.ServerName);

        await ConfigureEntraAppsAsync(input, apps, publishResponse, tenantId, warnings, ct);

        DisplayResults(input, warnings);
    }

    private ResolvedInput? ResolveInputs(RawPublishArgs args)
    {
        try
        {
            var environmentId = args.EnvironmentId;
            if (string.IsNullOrWhiteSpace(environmentId))
            {
                environmentId = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter Dataverse environment ID: ", "Environment ID");
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
                serverName = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter MCP server name to publish: ", "Server name", 100);
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
                alias = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter alias for the MCP server: ", "Alias", 50);
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
                displayName = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter display name for the MCP server: ", "Display name", 30);
                if (string.IsNullOrWhiteSpace(displayName)) { _logger.LogError("Display name is required"); return null; }
            }
            else
            {
                displayName = DevelopMcpCommand.InputValidator.ValidateInput(displayName, "Display name", maxLength: 30);
                if (displayName == null) { _logger.LogError("Invalid display name format"); return null; }
            }

            // Publisher name: optional from the CLI's perspective. The platform's v2 validator
            // requires a non-empty value for custom (user-created) servers and ignores it for
            // 1p app-based servers. Prompt the user when not supplied, but allow empty input —
            // a Microsoft developer publishing msdyn_DataverseMCPServer shouldn't have to type
            // anything. If they're publishing a custom server with no value, the platform's
            // error message tells them what's missing.
            var publisherName = args.PublisherName;
            if (string.IsNullOrWhiteSpace(publisherName))
            {
                publisherName = DevelopMcpCommand.InputValidator.PromptAndValidateOptionalInput(
                    "Enter publisher name (optional for 1p Microsoft-owned servers, required otherwise): ",
                    "Publisher name",
                    maxLength: 100);
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

    private async Task<string?> DetectTenantIdAsync()
    {
        var tenantId = await TenantDetectionHelper.DetectTenantIdAsync(null, _logger);

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogError("Tenant ID could not be determined. Run 'az login' and try again.");
            return null;
        }

        return tenantId;
    }

    private async Task<EntraAppSet?> CreateEntraAppsAsync(ResolvedInput input, string tenantId, List<string> warnings)
    {
        var factory = new EntraAppFactory(_logger, _graphApiService!, _retryHelper);

        // TEMPORARILY DISABLED: A365 Proxy Entra app creation. The platform-side custom connector
        // flow that consumed these credentials is commented out, so creating the app here would
        // leak an unused Entra registration in the user's tenant. Reinstate together with the
        // platform flow. A365App* fields on the returned EntraAppSet are placeholder empties.
        /*
        var a365 = await factory.CreateProxyAppAsync(
            input.ServerName, tenantId, suffix: "A365Proxy", roleDisplay: "A365 Proxy", serviceTreeId: null);
        if (a365 == null) return null;
        */

        var publicClients = await factory.CreatePublicClientsAppAsync(
            input.ServerName, tenantId, serviceTreeId: null, warnings);

        return new EntraAppSet(
            A365AppClientId: string.Empty,
            A365AppSecret: string.Empty,
            A365AppObjectId: string.Empty,
            A365AppName: string.Empty,
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
            _logger.LogWarning("Graph API service is unavailable; cannot roll back Entra apps '{A365}' / '{PublicClients}'. Delete them manually in the Azure portal.", apps.A365AppName, apps.PublicClientsAppName);
            return;
        }

        _logger.LogInformation("Rolling back Entra app registrations created for failed publish...");

        // TEMPORARILY DISABLED: A365 Proxy delete. The proxy Entra app is no longer created
        // (see CreateEntraAppsAsync). Reinstate together with the proxy app creation.
        /*
        await DeleteOneAsync(apps.A365AppObjectId, apps.A365AppClientId, apps.A365AppName, ct);
        */

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

        // TEMPORARILY DISABLED: A365 Proxy redirect-URI back-fill. The platform's v2 publish no
        // longer creates the proxy connector, so A365ProxyRedirectUri always comes back null and
        // there is no A365 Proxy Entra app to write a redirect URI onto. Reinstate together with
        // the proxy app creation in CreateEntraAppsAsync.
        /*
        var a365RedirectUri = response.A365ProxyRedirectUri;

        if (!string.IsNullOrWhiteSpace(a365RedirectUri))
        {
            tasks.Add(UpdateA365RedirectUrisAsync(tenantId, apps, a365RedirectUri, concurrentWarnings, ct));
        }
        else
        {
            var msg = "A365 Proxy redirect URI was not returned by the server. Redirect URI configuration skipped.";
            _logger.LogWarning(msg);
            concurrentWarnings.Add(msg);
        }
        */

        // Grant required-resource-access on the just-created A365 Proxy + Public Clients Entra apps.
        // The platform resolves the right resource per server type (Custom: managedidentityid; app-based
        // / Dataverse MCP: 1p mappings; fallback: platform's own app id) and returns both the resource
        // app id and the scope name. We look up the scope guid on the resource app, then add it as
        // requiredResourceAccess on each of the two Entra apps we created.
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
            // TEMPORARILY DISABLED: required-resource-access grant on the A365 Proxy app. The proxy
            // app is no longer created (see CreateEntraAppsAsync). Reinstate together with the
            // proxy app creation.
            /*
            tasks.Add(AddRequiredResourceAccessAsync(tenantId, apps.A365AppObjectId, apps.A365AppName, resourceAppId!, resourceScopeId.Value, concurrentWarnings, ct));
            */

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

    // TEMPORARILY DISABLED: UpdateA365RedirectUrisAsync. Sole caller in ConfigureEntraAppsAsync is
    // commented out. Reinstate together with the A365 Proxy app creation in CreateEntraAppsAsync.
    /*
    private async Task UpdateA365RedirectUrisAsync(
        string tenantId,
        EntraAppSet apps,
        string a365RedirectUri,
        System.Collections.Concurrent.ConcurrentBag<string> concurrentWarnings,
        CancellationToken ct = default)
    {
        try
        {
            var a365TcUri = DevelopMcpCommand.AddTcPrefix(a365RedirectUri);
            var a365NonTcUri = DevelopMcpCommand.RemoveTcPrefix(a365RedirectUri);
            var a365Uris = DevelopMcpCommand.BuildRedirectUriList(a365RedirectUri, a365TcUri, a365NonTcUri);
            _logger.LogDebug("Updating redirect URIs on '{AppName}' ({ObjectId})", apps.A365AppName, apps.A365AppObjectId);
            var success = await _retryHelper.ExecuteWithRetryAsync(
                async retryCt => await _graphApiService!.UpdateAppRedirectUrisAsync(tenantId, apps.A365AppObjectId, a365Uris, retryCt),
                result => !result,
                cancellationToken: ct);
            if (!success)
            {
                var msg = $"Failed to update redirect URIs on A365 Proxy app '{apps.A365AppName}' after retries.";
                _logger.LogError(msg);
                concurrentWarnings.Add(msg);
            }
            else
            {
                _logger.LogInformation("Updated redirect URIs on '{AppName}'", apps.A365AppName);
            }
        }
        catch (Exception ex)
        {
            var msg = $"Failed to update redirect URIs on A365 Proxy app: {ex.Message}";
            _logger.LogError(msg);
            concurrentWarnings.Add(msg);
        }
    }
    */

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
