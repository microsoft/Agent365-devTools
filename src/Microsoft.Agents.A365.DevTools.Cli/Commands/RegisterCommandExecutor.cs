// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// Raw CLI arguments passed to the register-external-mcp-server command
/// </summary>
internal record RawRegisterArgs(
    string? ServerName,
    string? ServerUrl,
    string? AuthType,
    string? IdpAuthUrl,
    string? IdpTokenUrl,
    string? IdpScopes,
    string? IdpClientId,
    string? IdpClientSecret,
    string? ApiKeyLocation,
    string? ApiKeyName,
    string? ToolsInput,
    string? InputFile,
    string? RemoteScopes,
    string? TenantId,
    string? ServiceTreeId,
    int? SecretLifetimeMonths,
    string? PublisherName,
    string? Description,
    bool DryRun);

/// <summary>
/// Orchestrates external MCP server registration, broken into focused steps.
/// </summary>
internal class RegisterCommandExecutor
{
    private readonly ILogger _logger;
    private readonly IAgent365ToolingService _toolingService;
    private readonly GraphApiService? _graphApiService;
    private readonly RetryHelper _retryHelper;

    internal RegisterCommandExecutor(
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
        public required string ServerName { get; init; }
        public required string ServerUrl { get; init; }
        public required string AuthType { get; init; }
        public required bool IsEntra { get; init; }
        public required bool IsExternalIdp { get; init; }
        public required bool IsNoAuth { get; init; }
        public required bool IsApiKey { get; init; }
        public required List<string> ToolList { get; init; }
        public required Dictionary<string, string> ToolDescriptions { get; init; }
        public required string PublisherName { get; init; }
        public required string Description { get; init; }
        public required bool DryRun { get; init; }
        public string? RemoteScopes { get; init; }
        public string? TenantId { get; init; }
        public string? ServiceTreeId { get; init; }
        public int? SecretLifetimeMonths { get; init; }
        public string? IdpAuthUrl { get; init; }
        public string? IdpTokenUrl { get; init; }
        public string? IdpScopes { get; init; }
        public string? IdpClientId { get; init; }
        public string? IdpClientSecret { get; init; }
        public string? ApiKeyLocation { get; init; }
        public string? ApiKeyName { get; init; }
    }

    private sealed record EntraAppSet(
        string A365AppClientId,
        string A365AppSecret,
        string A365AppObjectId,
        string A365AppName,
        string? RemoteProxyClientId,
        string? RemoteProxySecret,
        string? RemoteProxyObjectId,
        string RemoteProxyAppName,
        string? PublicClientsClientId,
        string? PublicClientsObjectId,
        string PublicClientsAppName);

    internal async Task<bool> ExecuteAsync(RawRegisterArgs args, CancellationToken ct = default)
    {
        var input = await ResolveInputsAsync(args);
        if (input is null) return false;

        DisplayRegistrationSummary(input);

        if (input.DryRun)
        {
            _logger.LogInformation("[DRY RUN] Would create Entra app registrations for server '{ServerName}'", input.ServerName);
            _logger.LogInformation("[DRY RUN] Auth type: {AuthType}", input.AuthType);
            _logger.LogInformation("[DRY RUN] Tools to register: {ToolCount} ({Tools})", input.ToolList.Count, string.Join(", ", input.ToolList));
            _logger.LogInformation("[DRY RUN] Would call AddMcpServer API and configure redirect URIs");
            return true;
        }

        Console.Write("Proceed with registration? (y/N): ");
        var confirmation = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (confirmation != "y" && confirmation != "yes")
        {
            Console.WriteLine("Registration cancelled.");
            return true;
        }

        Console.WriteLine();

        ct.ThrowIfCancellationRequested();

        await _toolingService.LogRegisterUsageAsync(input.ServerName, input.AuthType, input.ToolList.Count);
        Console.WriteLine($"Registering MCP server '{input.ServerName}'...");

        var tenantId = await DetectTenantIdAsync(input.TenantId);
        if (tenantId is null) return false;

        if (_graphApiService is null)
        {
            _logger.LogError("Graph API service is not available. Cannot create Entra applications.");
            return false;
        }

        var warnings = new List<string>();
        var apps = await CreateEntraAppsAsync(input, tenantId, warnings);
        if (apps is null) return false;

        ct.ThrowIfCancellationRequested();

        var addRequest = BuildRequest(input, apps);

        AddMcpServerResponse? addResponse;
        try
        {
            addResponse = await _toolingService.AddMcpServerAsync(addRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to register MCP server '{ServerName}': {Error}", input.ServerName, ex.Message);
            _logger.LogDebug("Exception details: {Exception}", ex.ToString());
            _logger.LogWarning("Entra app registrations were NOT rolled back. Delete them manually in the Azure portal if needed.");
            return false;
        }

        if (addResponse is null || !addResponse.IsSuccess)
        {
            var errorMsg = addResponse?.Message ?? "No response received";

            if (errorMsg.Contains("violates a database constraint", StringComparison.OrdinalIgnoreCase)
                || errorMsg.Contains("delete the existing record", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                var prevColor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR: A server named '{input.ServerName}' already exists. Please choose a different name.");
                Console.ForegroundColor = prevColor;
                _logger.LogError("A server named '{ServerName}' already exists. Please choose a different name.", input.ServerName);
                _logger.LogDebug("Raw server error: {Error}", errorMsg);
            }
            else
            {
                _logger.LogError("Failed to add MCP server {ServerName}: {Error}", input.ServerName, errorMsg);
            }

            _logger.LogWarning("Entra app registrations were NOT rolled back. Delete them manually in the Azure portal if needed.");
            Console.WriteLine($"Entra app registrations were NOT rolled back. Delete them manually in the Azure portal if needed.");
            return false;
        }

        _logger.LogDebug("Successfully added MCP server {ServerName}", input.ServerName);

        await ConfigureEntraAppsAsync(input, apps, addResponse, tenantId, warnings, ct);

        DisplayResults(input, addResponse.Server?.RemoteMCPServerProxyRedirectUri, warnings);
        return true;
    }

    private async Task<ResolvedInput?> ResolveInputsAsync(RawRegisterArgs args)
    {
        var serverName = args.ServerName;
        var serverUrl = args.ServerUrl;
        var authType = args.AuthType;
        var idpAuthUrl = args.IdpAuthUrl;
        var idpTokenUrl = args.IdpTokenUrl;
        var idpScopes = args.IdpScopes;
        var idpClientId = args.IdpClientId;
        var idpClientSecret = args.IdpClientSecret;
        var apiKeyLocation = args.ApiKeyLocation;
        var apiKeyName = args.ApiKeyName;
        var toolsInput = args.ToolsInput;
        var remoteScopes = args.RemoteScopes;
        var userTenantId = args.TenantId;
        var serviceTreeId = args.ServiceTreeId;
        var secretLifetimeMonths = args.SecretLifetimeMonths;
        var publisherName = args.PublisherName;
        var serverDescription = args.Description;

        RegisterExternalMcpServerInput? inputFileData = null;
        if (!string.IsNullOrWhiteSpace(args.InputFile))
        {
            if (!File.Exists(args.InputFile))
            {
                _logger.LogError("Input file not found: {InputFile}", args.InputFile);
                return null;
            }

            try
            {
                var jsonContent = await File.ReadAllTextAsync(args.InputFile);
                inputFileData = JsonSerializer.Deserialize<RegisterExternalMcpServerInput>(jsonContent);
            }
            catch (JsonException ex)
            {
                _logger.LogError("Failed to parse input file '{InputFile}': {Error}", args.InputFile, ex.Message);
                return null;
            }

            if (inputFileData is not null)
            {
                _logger.LogDebug("Loaded input file: {InputFile}", args.InputFile);

                serverName ??= inputFileData.ServerName;
                serverUrl ??= inputFileData.ServerUrl;
                authType ??= inputFileData.AuthType;
                remoteScopes ??= inputFileData.RemoteScopes;
                userTenantId ??= inputFileData.TenantId;
                serviceTreeId ??= inputFileData.ServiceTreeId;
                secretLifetimeMonths ??= inputFileData.SecretLifetimeMonths;
                publisherName ??= inputFileData.PublisherName;
                serverDescription ??= inputFileData.Description;

                if (inputFileData.ExternalOAuth is not null)
                {
                    idpAuthUrl ??= inputFileData.ExternalOAuth.AuthorizationUrl;
                    idpTokenUrl ??= inputFileData.ExternalOAuth.TokenUrl;
                    idpScopes ??= inputFileData.ExternalOAuth.Scopes;
                    idpClientId ??= inputFileData.ExternalOAuth.ClientId;
                    idpClientSecret ??= inputFileData.ExternalOAuth.ClientSecret;
                }

                if (inputFileData.ApiKey is not null)
                {
                    apiKeyLocation ??= inputFileData.ApiKey.Location;
                    apiKeyName ??= inputFileData.ApiKey.Name;
                }
            }
        }

        bool isEntra, isExternalIdp, isNoAuth, isApiKey;
        List<string>? toolList = null;
        Dictionary<string, string>? toolDescriptions = null;

        try
        {
            if (string.IsNullOrWhiteSpace(serverName))
            {
                serverName = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter MCP server name (must start with 'ext_', e.g. ext_MyServer): ", "Server name", 100);
                if (string.IsNullOrWhiteSpace(serverName)) { _logger.LogError("Server name is required"); return null; }
            }
            else
            {
                serverName = DevelopMcpCommand.InputValidator.ValidateInput(serverName, "Server name");
                if (serverName == null) { _logger.LogError("Invalid server name format"); return null; }
            }

            if (!serverName.StartsWith("ext_", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Server name must start with 'ext_' prefix. Got: '{ServerName}'", serverName);
                return null;
            }

            const int maxServerNameLength = 20;
            if (serverName.Length > maxServerNameLength)
            {
                _logger.LogError("Server name '{ServerName}' is {Length} characters, exceeding the maximum of {Max} characters (including prefix)", serverName, serverName.Length, maxServerNameLength);
                return null;
            }

            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                serverUrl = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter remote MCP server URL: ", "Server URL", 500);
                if (string.IsNullOrWhiteSpace(serverUrl)) { _logger.LogError("Server URL is required"); return null; }
            }

            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var parsedUri) ||
                (parsedUri.Scheme != "https" && parsedUri.Scheme != "http"))
            {
                _logger.LogError("Server URL '{ServerUrl}' is not a valid HTTP/HTTPS URL", serverUrl);
                return null;
            }

            if (secretLifetimeMonths is { } lifetime && (lifetime < 1 || lifetime > 24))
            {
                _logger.LogError("--secret-lifetime-months must be between 1 and 24 (Graph's maximum is ~2 years). Got: {Value}", lifetime);
                return null;
            }

            if (string.IsNullOrWhiteSpace(authType))
            {
                authType = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter authentication type (EntraOAuth, ExternalOAuth, APIKey, or NoAuth): ", "Auth type", 20);
                if (string.IsNullOrWhiteSpace(authType)) { _logger.LogError("Auth type is required"); return null; }
            }

            if (authType.Equals("Entra", StringComparison.OrdinalIgnoreCase)) authType = "EntraOAuth";
            if (authType.Equals("ExternalIDP", StringComparison.OrdinalIgnoreCase)) authType = "ExternalOAuth";

            isEntra = authType.Equals("EntraOAuth", StringComparison.OrdinalIgnoreCase);
            isExternalIdp = authType.Equals("ExternalOAuth", StringComparison.OrdinalIgnoreCase);
            isNoAuth = authType.Equals("NoAuth", StringComparison.OrdinalIgnoreCase);
            isApiKey = authType.Equals("APIKey", StringComparison.OrdinalIgnoreCase);
            if (!isEntra && !isExternalIdp && !isNoAuth && !isApiKey)
            {
                _logger.LogError("Invalid auth type '{AuthType}'. Must be 'EntraOAuth', 'ExternalOAuth', 'APIKey', or 'NoAuth'", authType);
                return null;
            }

            if (isExternalIdp)
            {
                if (string.IsNullOrWhiteSpace(idpAuthUrl))
                {
                    idpAuthUrl = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter external OAuth authorization URL: ", "Authorization URL", 500);
                    if (string.IsNullOrWhiteSpace(idpAuthUrl)) { _logger.LogError("Authorization URL is required for ExternalOAuth"); return null; }
                }

                if (string.IsNullOrWhiteSpace(idpTokenUrl))
                {
                    idpTokenUrl = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter external OAuth token URL: ", "Token URL", 500);
                    if (string.IsNullOrWhiteSpace(idpTokenUrl)) { _logger.LogError("Token URL is required for ExternalOAuth"); return null; }
                }

                if (string.IsNullOrWhiteSpace(idpScopes))
                {
                    idpScopes = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter external OAuth scopes: ", "Scopes", 500);
                    if (string.IsNullOrWhiteSpace(idpScopes)) { _logger.LogError("Scopes are required for ExternalOAuth"); return null; }
                }

                if (string.IsNullOrWhiteSpace(idpClientId))
                {
                    idpClientId = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter external OAuth client ID: ", "Client ID", 100);
                    if (string.IsNullOrWhiteSpace(idpClientId)) { _logger.LogError("Client ID is required for ExternalOAuth"); return null; }
                }

                if (string.IsNullOrWhiteSpace(idpClientSecret))
                {
                    idpClientSecret = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter external OAuth client secret: ", "Client secret", 500);
                    if (string.IsNullOrWhiteSpace(idpClientSecret)) { _logger.LogError("Client secret is required for ExternalOAuth"); return null; }
                }
            }

            if (isApiKey)
            {
                if (string.IsNullOrWhiteSpace(apiKeyLocation))
                {
                    apiKeyLocation = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter API key location (Header or Query): ", "API key location", 10);
                    if (string.IsNullOrWhiteSpace(apiKeyLocation)) { _logger.LogError("API key location is required for APIKey"); return null; }
                }

                if (!apiKeyLocation.Equals("Header", StringComparison.OrdinalIgnoreCase) &&
                    !apiKeyLocation.Equals("Query", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Invalid API key location '{Location}'. Must be 'Header' or 'Query'", apiKeyLocation);
                    return null;
                }

                if (string.IsNullOrWhiteSpace(apiKeyName))
                {
                    var prompt = apiKeyLocation.Equals("Header", StringComparison.OrdinalIgnoreCase)
                        ? "Enter API key header name (e.g., 'X-API-Key'): "
                        : "Enter API key query parameter name (e.g., 'token'): ";
                    apiKeyName = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput(prompt, "API key name", 100);
                    if (string.IsNullOrWhiteSpace(apiKeyName)) { _logger.LogError("API key name is required for APIKey"); return null; }
                }
            }

            // Tool names: CLI --tools > input file tools > interactive prompt
            if (string.IsNullOrWhiteSpace(toolsInput) && inputFileData?.Tools is not null && inputFileData.Tools.Count > 0)
            {
                foreach (var tool in inputFileData.Tools)
                {
                    if (string.IsNullOrWhiteSpace(tool.Name))
                    {
                        _logger.LogError("Input file contains a tool with a null or empty name");
                        return null;
                    }
                }

                var duplicateTools = inputFileData.Tools
                    .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();
                if (duplicateTools.Count > 0)
                {
                    _logger.LogError("Input file contains duplicate tool names (case-insensitive): {Duplicates}", string.Join(", ", duplicateTools));
                    return null;
                }

                toolList = inputFileData.Tools.Select(t => t.Name).ToList();
                toolDescriptions = new Dictionary<string, string>();
                foreach (var tool in inputFileData.Tools)
                {
                    if (!string.IsNullOrWhiteSpace(tool.Description))
                    {
                        toolDescriptions[tool.Name] = tool.Description;
                    }
                }

                _logger.LogDebug("Tools loaded from input file: {Tools}", string.Join(", ", toolList));
            }
            else
            {
                if (string.IsNullOrWhiteSpace(toolsInput))
                {
                    toolsInput = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter comma-separated list of tool names: ", "Tool names", 2000);
                    if (string.IsNullOrWhiteSpace(toolsInput)) { _logger.LogError("At least one tool name is required"); return null; }
                }

                toolList = toolsInput!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (toolList.Count == 0)
                {
                    _logger.LogError("At least one tool name is required");
                    return null;
                }

                _logger.LogDebug("Tools to register: {Tools}", string.Join(", ", toolList));
            }

            if (toolList.Count == 0)
            {
                _logger.LogError("At least one tool name is required");
                return null;
            }

            toolDescriptions ??= new Dictionary<string, string>();
            foreach (var tool in toolList)
            {
                if (!toolDescriptions.ContainsKey(tool))
                {
                    var desc = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput($"Enter description for tool '{tool}': ", $"Description for tool '{tool}'", 200);
                    if (string.IsNullOrWhiteSpace(desc)) { _logger.LogError("Tool description is required for '{Tool}'", tool); return null; }
                    toolDescriptions[tool] = desc;
                }
            }

            if (string.IsNullOrWhiteSpace(publisherName))
            {
                publisherName = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter publisher name: ", "Publisher name", 200);
                if (string.IsNullOrWhiteSpace(publisherName)) { _logger.LogError("Publisher name is required"); return null; }
            }

            if (string.IsNullOrWhiteSpace(serverDescription))
            {
                serverDescription = DevelopMcpCommand.InputValidator.PromptAndValidateRequiredInput("Enter server description: ", "Server description", 500);
                if (string.IsNullOrWhiteSpace(serverDescription)) { _logger.LogError("Server description is required"); return null; }
            }

            if (!isNoAuth && !isApiKey && string.IsNullOrWhiteSpace(remoteScopes))
            {
                Console.Write("Enter scopes for the remote MCP server (leave empty for no auth): ");
                remoteScopes = Console.ReadLine()?.Trim() ?? string.Empty;
            }
        }
        catch (ArgumentException ex)
        {
            _logger.LogError("Input validation failed: {Message}", ex.Message);
            return null;
        }

        return new ResolvedInput
        {
            ServerName = serverName,
            ServerUrl = serverUrl,
            AuthType = authType,
            IsEntra = isEntra,
            IsExternalIdp = isExternalIdp,
            IsNoAuth = isNoAuth,
            IsApiKey = isApiKey,
            ToolList = toolList,
            ToolDescriptions = toolDescriptions,
            PublisherName = publisherName,
            Description = serverDescription,
            DryRun = args.DryRun,
            RemoteScopes = remoteScopes,
            TenantId = userTenantId,
            ServiceTreeId = serviceTreeId,
            SecretLifetimeMonths = secretLifetimeMonths,
            IdpAuthUrl = idpAuthUrl,
            IdpTokenUrl = idpTokenUrl,
            IdpScopes = idpScopes,
            IdpClientId = idpClientId,
            IdpClientSecret = idpClientSecret,
            ApiKeyLocation = apiKeyLocation,
            ApiKeyName = apiKeyName,
        };
    }

    private void DisplayRegistrationSummary(ResolvedInput input)
    {
        Console.WriteLine();
        var prevColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Registration Summary");
        Console.WriteLine("====================");
        Console.ForegroundColor = prevColor;
        DevelopMcpCommand.WriteLabel("  Server Name:    "); Console.WriteLine(input.ServerName);
        DevelopMcpCommand.WriteLabel("  Server URL:     "); Console.WriteLine(input.ServerUrl);
        DevelopMcpCommand.WriteLabel("  Auth Type:      "); Console.WriteLine(input.AuthType);
        DevelopMcpCommand.WriteLabel("  Publisher:      "); Console.WriteLine(input.PublisherName);
        DevelopMcpCommand.WriteLabel("  Description:    "); Console.WriteLine(input.Description);
        DevelopMcpCommand.WriteLabel("  Tools:");
        Console.WriteLine();
        foreach (var tool in input.ToolList)
        {
            var desc = input.ToolDescriptions.GetValueOrDefault(tool);
            Console.WriteLine(desc is not null ? $"    - {tool}: {desc}" : $"    - {tool}");
        }

        if (!input.IsNoAuth && !input.IsApiKey && !string.IsNullOrWhiteSpace(input.RemoteScopes))
        {
            DevelopMcpCommand.WriteLabel("  Remote Scopes:  "); Console.WriteLine(input.RemoteScopes);
        }

        if (input.SecretLifetimeMonths is { } lifetime)
        {
            DevelopMcpCommand.WriteLabel("  Secret Lifetime: "); Console.WriteLine($"{lifetime} month(s)");
        }

        if (input.IsExternalIdp)
        {
            DevelopMcpCommand.WriteLabel("  IDP Auth URL:   "); Console.WriteLine(input.IdpAuthUrl);
            DevelopMcpCommand.WriteLabel("  IDP Token URL:  "); Console.WriteLine(input.IdpTokenUrl);
            DevelopMcpCommand.WriteLabel("  IDP Scopes:     "); Console.WriteLine(input.IdpScopes);
            DevelopMcpCommand.WriteLabel("  IDP Client ID:  "); Console.WriteLine(input.IdpClientId);
        }

        if (input.IsApiKey)
        {
            DevelopMcpCommand.WriteLabel("  API Key Location: "); Console.WriteLine(input.ApiKeyLocation);
            DevelopMcpCommand.WriteLabel("  API Key Name:     "); Console.WriteLine(input.ApiKeyName);
        }

        Console.WriteLine();

        prevColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("WARNING: Tool names must exactly match the names exposed by the remote MCP server. Mismatched names will cause tool invocations to fail at runtime.");
        Console.ForegroundColor = prevColor;
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

    private async Task<EntraAppSet?> CreateEntraAppsAsync(
        ResolvedInput input, string tenantId, List<string> warnings)
    {
        var factory = new EntraAppFactory(_logger, _graphApiService!, _retryHelper);

        var a365 = await factory.CreateProxyAppAsync(
            input.ServerName, tenantId, suffix: "A365Proxy", roleDisplay: "A365 Proxy", serviceTreeId: input.ServiceTreeId);
        if (a365 == null) return null;

        string? remoteProxyClientId = null;
        string? remoteProxySecret = null;
        string? remoteProxyObjectId = null;
        var remoteProxyAppName = $"{input.ServerName}-RemoteProxy";

        if (input.IsEntra)
        {
            var remote = await factory.CreateProxyAppAsync(
                input.ServerName, tenantId, suffix: "RemoteProxy", roleDisplay: "Remote Proxy", serviceTreeId: input.ServiceTreeId);
            if (remote == null) return null;

            remoteProxyClientId = remote.ClientId;
            remoteProxySecret = remote.Secret;
            remoteProxyObjectId = remote.ObjectId;
            remoteProxyAppName = remote.AppName;
        }

        var publicClients = await factory.CreatePublicClientsAppAsync(
            input.ServerName, tenantId, serviceTreeId: input.ServiceTreeId, warnings);

        return new EntraAppSet(
            A365AppClientId: a365.ClientId,
            A365AppSecret: a365.Secret,
            A365AppObjectId: a365.ObjectId,
            A365AppName: a365.AppName,
            RemoteProxyClientId: remoteProxyClientId,
            RemoteProxySecret: remoteProxySecret,
            RemoteProxyObjectId: remoteProxyObjectId,
            RemoteProxyAppName: remoteProxyAppName,
            PublicClientsClientId: publicClients.ClientId,
            PublicClientsObjectId: publicClients.ObjectId,
            PublicClientsAppName: publicClients.AppName);
    }

    private static AddMcpServerRequest BuildRequest(ResolvedInput input, EntraAppSet apps)
    {
        AddMcpServerAuthMetadata authMetadata;

        if (input.IsNoAuth || input.IsApiKey)
        {
            authMetadata = new AddMcpServerAuthMetadata
            {
                ClientApp1Id = apps.A365AppClientId,
                ClientApp1Secret = apps.A365AppSecret,
            };
        }
        else
        {
            string clientApp2Id;
            string clientApp2Secret;

            if (input.IsEntra)
            {
                clientApp2Id = apps.RemoteProxyClientId!;
                clientApp2Secret = apps.RemoteProxySecret!;
            }
            else
            {
                clientApp2Id = input.IdpClientId!;
                clientApp2Secret = input.IdpClientSecret!;
            }

            authMetadata = new AddMcpServerAuthMetadata
            {
                ClientApp1Id = apps.A365AppClientId,
                ClientApp1Secret = apps.A365AppSecret,
                ClientApp2Id = clientApp2Id,
                ClientApp2Secret = clientApp2Secret,
            };
        }

        return new AddMcpServerRequest
        {
            ServerName = input.ServerName,
            ServerUrl = input.ServerUrl,
            ToolList = input.ToolList,
            ToolDescriptions = input.ToolDescriptions.Count > 0 ? input.ToolDescriptions : null,
            AuthType = input.AuthType,
            AuthMetadata = authMetadata,
            ExternalIdp = input.IsExternalIdp ? new ExternalIdpDetails
            {
                AuthorizationUrl = input.IdpAuthUrl,
                TokenUrl = input.IdpTokenUrl,
                Scopes = input.IdpScopes,
            } : null,
            ApiKeyDetails = input.IsApiKey ? new ApiKeyDetails
            {
                Location = input.ApiKeyLocation,
                Name = input.ApiKeyName,
            } : null,
            RemoteServerScopes = input.RemoteScopes,
            PublisherName = input.PublisherName,
            Description = input.Description,
            CopilotClientAppId = apps.PublicClientsClientId,
        };
    }

    private async Task ConfigureEntraAppsAsync(
        ResolvedInput input, EntraAppSet apps, AddMcpServerResponse response,
        string tenantId, List<string> warnings, CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        var concurrentWarnings = new System.Collections.Concurrent.ConcurrentBag<string>();

        var a365RedirectUri = response.Server?.A365ProxyRedirectUri;
        var remoteRedirectUri = response.Server?.RemoteMCPServerProxyRedirectUri;

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

        if (input.IsEntra && !string.IsNullOrWhiteSpace(remoteRedirectUri) && apps.RemoteProxyObjectId != null)
        {
            tasks.Add(UpdateRemoteProxyRedirectUrisAsync(tenantId, apps, remoteRedirectUri, concurrentWarnings, ct));
        }
        else if (input.IsEntra && string.IsNullOrWhiteSpace(remoteRedirectUri))
        {
            var msg = "Remote MCP Proxy redirect URI was not returned by the server. Redirect URI configuration skipped.";
            _logger.LogWarning(msg);
            concurrentWarnings.Add(msg);
        }
        else if (input.IsEntra && apps.RemoteProxyObjectId == null)
        {
            var msg = "Remote Proxy Entra app was not created. Redirect URI configuration skipped.";
            _logger.LogWarning(msg);
            concurrentWarnings.Add(msg);
        }

        if (input.IsEntra && apps.RemoteProxyObjectId != null && !string.IsNullOrWhiteSpace(input.RemoteScopes))
        {
            tasks.Add(AddRemoteProxyScopePermissionAsync(tenantId, input, apps, concurrentWarnings, ct));
        }

        var ppmiAppClientId = response.Server?.PpmiAppClientId;
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
        string tenantId, EntraAppSet apps, string a365RedirectUri,
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

    private async Task UpdateRemoteProxyRedirectUrisAsync(
        string tenantId, EntraAppSet apps, string remoteRedirectUri,
        System.Collections.Concurrent.ConcurrentBag<string> concurrentWarnings,
        CancellationToken ct = default)
    {
        try
        {
            var remoteTcUri = DevelopMcpCommand.AddTcPrefix(remoteRedirectUri);
            var remoteNonTcUri = DevelopMcpCommand.RemoveTcPrefix(remoteRedirectUri);
            var remoteUris = DevelopMcpCommand.BuildRedirectUriList(remoteRedirectUri, remoteTcUri, remoteNonTcUri);
            _logger.LogDebug("Updating redirect URIs on '{AppName}' ({ObjectId})", apps.RemoteProxyAppName, apps.RemoteProxyObjectId);
            var success = await _retryHelper.ExecuteWithRetryAsync(
                async retryCt => await _graphApiService!.UpdateAppRedirectUrisAsync(tenantId, apps.RemoteProxyObjectId!, remoteUris, retryCt),
                result => !result,
                cancellationToken: ct);
            if (!success)
            {
                var msg = $"Failed to update redirect URIs on Remote Proxy app '{apps.RemoteProxyAppName}' after retries.";
                _logger.LogError(msg);
                concurrentWarnings.Add(msg);
            }
            else
            {
                _logger.LogInformation("Updated redirect URIs on '{AppName}'", apps.RemoteProxyAppName);
            }
        }
        catch (Exception ex)
        {
            var msg = $"Failed to update redirect URIs on Remote Proxy app: {ex.Message}";
            _logger.LogError(msg);
            concurrentWarnings.Add(msg);
        }
    }

    private async Task AddRemoteProxyScopePermissionAsync(
        string tenantId, ResolvedInput input, EntraAppSet apps,
        System.Collections.Concurrent.ConcurrentBag<string> concurrentWarnings,
        CancellationToken ct = default)
    {
        try
        {
            var scopeUri = input.RemoteScopes!.Trim();
            string? resourceAppId = null;
            string? scopeName = null;

            if (scopeUri.StartsWith("api://", StringComparison.OrdinalIgnoreCase))
            {
                var path = scopeUri.Substring("api://".Length);
                var slashIndex = path.IndexOf('/');
                if (slashIndex > 0)
                {
                    resourceAppId = path.Substring(0, slashIndex);
                    scopeName = path.Substring(slashIndex + 1);
                }
            }

            if (!string.IsNullOrWhiteSpace(resourceAppId) && !string.IsNullOrWhiteSpace(scopeName))
            {
                _logger.LogDebug("Looking up scope '{ScopeName}' on resource app {ResourceAppId}...", scopeName, resourceAppId);
                Guid? remoteScopeId = null;
                try
                {
                    remoteScopeId = await _retryHelper.ExecuteWithRetryAsync(
                        async retryCt => await _graphApiService!.GetOAuth2PermissionScopeIdAsync(tenantId, resourceAppId, scopeName, retryCt),
                        result => !result.HasValue,
                        cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    var msg = $"Failed to look up scope '{scopeName}' on resource app {resourceAppId} after retries: {ex.Message}. API permission not added to RemoteProxy app.";
                    _logger.LogError(msg);
                    concurrentWarnings.Add(msg);
                }

                if (remoteScopeId.HasValue)
                {
                    _logger.LogDebug("Adding API permission '{ScopeName}' (resource: {ResourceAppId}) on '{AppName}' ({ObjectId})", scopeName, resourceAppId, apps.RemoteProxyAppName, apps.RemoteProxyObjectId);
                    var success = await _retryHelper.ExecuteWithRetryAsync(
                        async retryCt => await _graphApiService!.AddRequiredResourceAccessAsync(
                            tenantId, apps.RemoteProxyObjectId!, resourceAppId, remoteScopeId.Value, retryCt),
                        result => !result,
                        cancellationToken: ct);
                    if (!success)
                    {
                        var msg = $"Failed to add API permission '{scopeName}' from resource app {resourceAppId} on RemoteProxy app '{apps.RemoteProxyAppName}' after retries.";
                        _logger.LogError(msg);
                        concurrentWarnings.Add(msg);
                    }
                }
                else
                {
                    var msg = $"Could not find scope '{scopeName}' on resource app {resourceAppId}. API permission not added to RemoteProxy app.";
                    _logger.LogError(msg);
                    concurrentWarnings.Add(msg);
                }
            }
            else
            {
                var msg = $"Could not parse resource app ID and scope from '{input.RemoteScopes}'. Expected format: api://{{appId}}/{{scopeName}}";
                _logger.LogWarning(msg);
                concurrentWarnings.Add(msg);
            }
        }
        catch (Exception ex)
        {
            var msg = $"Failed to add API permissions on RemoteProxy app: {ex.Message}";
            _logger.LogError(msg);
            concurrentWarnings.Add(msg);
        }
    }

    private async Task AddPpmiPermissionAsync(
        string tenantId, string appObjectId, string appName,
        string ppmiAppClientId, Guid ppmiScopeId,
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

    private void DisplayResults(
        ResolvedInput input, string? remoteRedirectUri, List<string> warnings)
    {
        if (warnings.Count == 0)
        {
            var prevColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"MCP server '{input.ServerName}' has been registered successfully.");
            Console.ForegroundColor = prevColor;
        }
        else
        {
            var prevColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"MCP server '{input.ServerName}' was registered with {warnings.Count} warning(s):");
            Console.ForegroundColor = prevColor;
            Console.WriteLine();
            foreach (var w in warnings)
            {
                _logger.LogWarning("  - {Warning}", w);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Please ask your tenant admin to approve MCP server '{input.ServerName}'.");
        if (input.IsExternalIdp && !string.IsNullOrWhiteSpace(remoteRedirectUri))
        {
            Console.WriteLine();
            Console.WriteLine($"Redirect URI: {remoteRedirectUri}");
            Console.WriteLine($"Please add this redirect URI to your external IDP application ({input.IdpClientId}).");
        }
    }
}
