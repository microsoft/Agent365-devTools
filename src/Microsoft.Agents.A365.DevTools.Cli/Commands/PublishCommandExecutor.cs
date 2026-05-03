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
    string? TenantId,
    string? ServiceTreeId,
    bool DryRun);

/// <summary>
/// Orchestrates first-party MCP server publish in one CLI command. The shape mirrors
/// <see cref="RegisterCommandExecutor"/> for BYO register: the CLI creates the Entra apps it has
/// delegated authority to create (A365 Proxy + Public Clients in the user's own tenant), calls the
/// platform publish endpoint with those credentials, and back-fills the apps' redirect URIs and
/// PPMI scope grants after the platform creates the CMS connector and PPMI identity.
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
        public string? TenantId { get; init; }
        public string? ServiceTreeId { get; init; }
    }

    private sealed record EntraAppSet(
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
            _logger.LogInformation("[DRY RUN] Would create Entra apps '{A365}' and '{PublicClients}' in tenant", $"{input.Alias}-A365Proxy", $"{input.Alias}-PublicClients");
            _logger.LogInformation("[DRY RUN] Would call publish endpoint and back-fill redirect URI + PPMI scope on the created apps");
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

        var tenantId = await DetectTenantIdAsync(input.TenantId);
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

        var request = new PublishMcpServerRequest
        {
            Alias = input.Alias,
            DisplayName = input.DisplayName,
            A365ProxyClientId = Guid.Parse(apps.A365AppClientId),
            A365ProxyClientSecret = apps.A365AppSecret,
            PublicClientsAppId = apps.PublicClientsClientId,
        };

        PublishMcpServerResponse? publishResponse;
        try
        {
            publishResponse = await _toolingService.PublishServerAsync(input.EnvironmentId, input.ServerName, request, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to publish MCP server '{ServerName}': {Error}", input.ServerName, ex.Message);
            _logger.LogDebug("Exception details: {Exception}", ex.ToString());
            _logger.LogWarning("Entra app registrations were NOT rolled back. Delete them manually in the Azure portal if needed.");
            return;
        }

        if (publishResponse is null || !publishResponse.IsSuccess)
        {
            var errorMsg = publishResponse?.Message ?? "No response received";
            _logger.LogError("Failed to publish MCP server {ServerName}: {Error}", input.ServerName, errorMsg);
            _logger.LogWarning("Entra app registrations were NOT rolled back. Delete them manually in the Azure portal if needed.");
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
                displayName = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter display name for the MCP server: ", "Display name", 100);
                if (string.IsNullOrWhiteSpace(displayName)) { _logger.LogError("Display name is required"); return null; }
            }
            else
            {
                displayName = DevelopMcpCommand.InputValidator.ValidateInput(displayName, "Display name", maxLength: 100);
                if (displayName == null) { _logger.LogError("Invalid display name format"); return null; }
            }

            return new ResolvedInput
            {
                EnvironmentId = environmentId,
                ServerName = serverName,
                Alias = alias,
                DisplayName = displayName,
                DryRun = args.DryRun,
                TenantId = args.TenantId,
                ServiceTreeId = args.ServiceTreeId,
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
        Console.WriteLine();
    }

    private async Task<string?> DetectTenantIdAsync(string? userTenantId)
    {
        var tenantId = userTenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            tenantId = await TenantDetectionHelper.DetectTenantIdAsync(null, _logger);
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogError("Tenant ID could not be determined. Pass --tenant-id or run 'az login'.");
            return null;
        }

        return tenantId;
    }

    private async Task<EntraAppSet?> CreateEntraAppsAsync(ResolvedInput input, string tenantId, List<string> warnings)
    {
        var a365AppName = $"{input.ServerName}-A365Proxy";
        var publicClientsAppName = $"{input.ServerName}-PublicClients";

        _logger.LogDebug("Creating Entra application for A365 Proxy...");
        var a365App = await _graphApiService!.CreateEntraAppAsync(tenantId, a365AppName, serviceTreeId: input.ServiceTreeId);
        if (a365App == null)
        {
            _logger.LogError("Failed to create Entra application '{AppName}'. Ensure you have Application.ReadWrite.All permission in the target tenant. Run with -v for details.", a365AppName);
            return null;
        }
        _logger.LogInformation("Created Entra app '{AppName}' (clientId: {ClientId})", a365AppName, a365App.Value.ClientId);

        var a365Secret = await _graphApiService.AddAppPasswordAsync(tenantId, a365App.Value.ObjectId);
        if (string.IsNullOrWhiteSpace(a365Secret))
        {
            _logger.LogError("Failed to create secret for '{AppName}'. Run with -v for details.", a365AppName);
            return null;
        }

        if (string.IsNullOrWhiteSpace(a365App.Value.ClientId))
        {
            _logger.LogError("A365 Proxy Entra application was created but returned an empty client ID");
            return null;
        }

        _logger.LogDebug("Created A365 Proxy app: {ClientId}", a365App.Value.ClientId);

        string? publicClientsClientId = null;
        string? publicClientsObjectId = null;

        _logger.LogDebug("Creating Entra application for Public Clients...");
        var copilotApp = await _graphApiService.CreateEntraAppAsync(tenantId, publicClientsAppName, serviceTreeId: input.ServiceTreeId);
        if (copilotApp != null)
        {
            publicClientsClientId = copilotApp.Value.ClientId;
            publicClientsObjectId = copilotApp.Value.ObjectId;
            _logger.LogInformation("Created Entra app '{AppName}' (clientId: {ClientId})", publicClientsAppName, publicClientsClientId);

            var copilotRedirectUri = $"ms-appx-web://Microsoft.AAD.BrokerPlugin/{publicClientsClientId}";
            var publicClientUris = new[] { copilotRedirectUri, "http://localhost:8080/callback", "https://vscode.dev/redirect", "http://localhost" };
            try
            {
                var success = await _retryHelper.ExecuteWithRetryAsync(
                    async ct => await _graphApiService.UpdateAppPublicClientRedirectUrisAsync(tenantId, publicClientsObjectId, publicClientUris, ct),
                    result => !result);
                if (!success)
                {
                    var msg = $"Failed to set redirect URIs on Public Clients app '{publicClientsAppName}' after retries.";
                    _logger.LogError(msg);
                    warnings.Add(msg);
                }
                else
                {
                    _logger.LogDebug(
                        "Set {RedirectUriCount} redirect URIs on '{AppName}' ({ObjectId}): {RedirectUris}",
                        publicClientUris.Length,
                        publicClientsAppName,
                        publicClientsObjectId,
                        string.Join(", ", publicClientUris));
                }
            }
            catch (Exception ex)
            {
                var msg = $"Failed to set redirect URIs on Public Clients app: {ex.Message}";
                _logger.LogError(msg);
                warnings.Add(msg);
            }
        }
        else
        {
            var msg = "Failed to create Public Clients Entra app. Continuing without it.";
            _logger.LogWarning(msg);
            warnings.Add(msg);
        }

        return new EntraAppSet(
            A365AppClientId: a365App.Value.ClientId,
            A365AppSecret: a365Secret,
            A365AppObjectId: a365App.Value.ObjectId,
            A365AppName: a365AppName,
            PublicClientsClientId: publicClientsClientId,
            PublicClientsObjectId: publicClientsObjectId,
            PublicClientsAppName: publicClientsAppName);
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

        var ppmiAppClientId = response.PpmiAppClientId;
        Guid? ppmiScopeId = null;
        if (!string.IsNullOrWhiteSpace(ppmiAppClientId))
        {
            _logger.LogDebug("PPMI app provisioned: {PpmiAppClientId}", ppmiAppClientId);
            try
            {
                ppmiScopeId = await _retryHelper.ExecuteWithRetryAsync(
                    async retryCt => await _graphApiService!.GetOAuth2PermissionScopeIdAsync(
                        tenantId, ppmiAppClientId, "Tools.ListInvoke.All", retryCt),
                    result => !result.HasValue,
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                var msg = $"Could not find 'Tools.ListInvoke.All' scope on PPMI app {ppmiAppClientId} after retries: {ex.Message}. API permissions not added.";
                _logger.LogError(msg);
                concurrentWarnings.Add(msg);
            }
        }

        if (ppmiScopeId.HasValue)
        {
            tasks.Add(AddPpmiPermissionAsync(tenantId, apps.A365AppObjectId, apps.A365AppName, ppmiAppClientId!, ppmiScopeId.Value, concurrentWarnings, ct));

            if (apps.PublicClientsObjectId != null)
            {
                tasks.Add(AddPpmiPermissionAsync(tenantId, apps.PublicClientsObjectId, apps.PublicClientsAppName, ppmiAppClientId!, ppmiScopeId.Value, concurrentWarnings, ct));
            }
        }
        else if (!string.IsNullOrWhiteSpace(ppmiAppClientId) && ppmiScopeId == null)
        {
            var msg = $"Could not find 'Tools.ListInvoke.All' scope on PPMI app {ppmiAppClientId}. API permissions not added.";
            _logger.LogError(msg);
            concurrentWarnings.Add(msg);
        }

        await Task.WhenAll(tasks);

        foreach (var w in concurrentWarnings)
            warnings.Add(w);
    }

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

    private async Task AddPpmiPermissionAsync(
        string tenantId,
        string appObjectId,
        string appName,
        string ppmiAppClientId,
        Guid ppmiScopeId,
        System.Collections.Concurrent.ConcurrentBag<string> concurrentWarnings,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug("Adding PPMI 'Tools.ListInvoke.All' permission on '{AppName}' ({ObjectId})", appName, appObjectId);
            var success = await _retryHelper.ExecuteWithRetryAsync(
                async retryCt => await _graphApiService!.AddRequiredResourceAccessAsync(
                    tenantId, appObjectId, ppmiAppClientId, ppmiScopeId, retryCt),
                result => !result,
                cancellationToken: ct);
            if (!success)
            {
                var msg = $"Failed to add PPMI permission on '{appName}' after retries.";
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
            var msg = $"Failed to add PPMI permission on '{appName}': {ex.Message}";
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
