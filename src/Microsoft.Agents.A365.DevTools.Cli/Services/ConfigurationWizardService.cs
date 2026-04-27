// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for simplifying Agent 365 configuration initialization with smart defaults and Azure CLI integration.
/// 
/// IMPORTANT: This is an interactive wizard service. Output pattern:
/// - Console.WriteLine: All user-facing output (info, prompts, errors) for direct console interaction
/// - _logger.LogDebug: Internal diagnostics only, not shown to users by default
/// - _logger.LogInformation: Wizard lifecycle events for logging infrastructure
/// 
/// This follows Azure CLI pattern where interactive commands write directly to console for immediate feedback.
/// </summary>
public interface IConfigurationWizardService
{
    /// <summary>
    /// Runs an interactive configuration wizard that minimizes user input by leveraging Azure CLI and smart defaults
    /// </summary>
    /// <param name="existingConfig">Existing configuration to use for defaults, if any</param>
    /// <returns>Configured Agent365Config instance</returns>
    Task<Agent365Config?> RunWizardAsync(Agent365Config? existingConfig = null);
}

public class ConfigurationWizardService : IConfigurationWizardService
{
    private readonly IAzureCliService _azureCliService;
    private readonly PlatformDetector _platformDetector;
    private readonly ILogger<ConfigurationWizardService> _logger;

    public ConfigurationWizardService(
        IAzureCliService azureCliService,
        PlatformDetector platformDetector,
        ILogger<ConfigurationWizardService> logger)
    {
        _azureCliService = azureCliService;
        _platformDetector = platformDetector;
        _logger = logger;
    }

    private static string ExtractDomainFromAccount(AzureAccountInfo accountInfo)
    {
        if (!string.IsNullOrWhiteSpace(accountInfo?.User?.Name) && accountInfo.User.Name.Contains("@"))
        {
            var parts = accountInfo.User.Name.Split('@');
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
                return parts[1];
        }
        return string.Empty;
    }

    public async Task<Agent365Config?> RunWizardAsync(Agent365Config? existingConfig = null)
    {
        try
        {
            if (existingConfig != null)
            {
                _logger.LogDebug("Using existing configuration with deploymentProjectPath: {Path}", existingConfig.DeploymentProjectPath ?? "(null)");
                Console.WriteLine("Found existing configuration. Default values will be used where available.");
                Console.WriteLine("Press Enter to keep a current value, or type a new one to update it.");
                Console.WriteLine();
            }

            // Step 1: Verify Azure CLI login
            if (!await VerifyAzureLoginAsync())
            {
                Console.WriteLine("ERROR: Configuration wizard cancelled: Azure CLI authentication required");
                _logger.LogDebug("Configuration wizard cancelled: Azure CLI authentication required");
                return null;
            }

            // Step 2: Get Azure account info
            var accountInfo = await _azureCliService.GetCurrentAccountAsync();
            if (accountInfo == null)
            {
                Console.WriteLine("ERROR: Failed to retrieve Azure account information. Please run 'az login' first");
                _logger.LogDebug("Failed to retrieve Azure account information");
                return null;
            }

            Console.WriteLine($"Subscription ID: {accountInfo.Id} ({accountInfo.Name})");
            Console.WriteLine($"Tenant ID: {accountInfo.TenantId}");
            Console.WriteLine();
            Console.WriteLine("NOTE: Defaulted from current Azure account. To use a different Azure subscription, run 'az login' and then 'az account set --subscription <subscription-id>' before running this command.");
            Console.WriteLine();

            // Step 3: Get and validate Client App ID (required for authentication)
            var clientAppId = await PromptForClientAppIdAsync(existingConfig, accountInfo.TenantId);
            if (string.IsNullOrWhiteSpace(clientAppId))
            {
                Console.WriteLine("ERROR: Client App ID is required. Configuration cancelled");
                _logger.LogDebug("Client App ID not provided, configuration cancelled");
                return null;
            }

            // Step 4: Get unique agent name
            var agentName = PromptForAgentName(existingConfig);
            if (string.IsNullOrWhiteSpace(agentName))
            {
                Console.WriteLine("ERROR: Agent name is required. Configuration cancelled");
                _logger.LogDebug("Agent name not provided, configuration cancelled");
                return null;
            }

            var domain = ExtractDomainFromAccount(accountInfo);
            var derivedNames = GenerateDerivedNames(agentName, domain);

            // Step 4: Validate deployment project path
            var deploymentPath = PromptForDeploymentPath(existingConfig);
            if (string.IsNullOrWhiteSpace(deploymentPath))
            {
                Console.WriteLine("ERROR: Configuration wizard cancelled: Deployment project path not provided or invalid");
                _logger.LogDebug("Deployment path validation failed, configuration cancelled");
                return null;
            }

            // Step 5: Messaging endpoint (optional — warn if empty)
            string messagingEndpoint = PromptForMessagingEndpoint(existingConfig);
            if (string.IsNullOrWhiteSpace(messagingEndpoint))
            {
                Console.WriteLine("WARNING: No messaging endpoint provided. You can configure it later in a365.config.json.");
                messagingEndpoint = string.Empty;
            }

            // Step 7: Get manager email (required for agent creation)
            var managerEmail = PromptForManagerEmail(existingConfig, accountInfo);
            if (string.IsNullOrWhiteSpace(managerEmail))
            {
                Console.WriteLine("ERROR: Configuration wizard cancelled: Manager email not provided");
                _logger.LogDebug("Manager email not provided, configuration cancelled");
                return null;
            }

            // Step 8: Optional custom blueprint permissions (before summary so they appear in it)
            var customPermissions = PromptForCustomBlueprintPermissions(
                existingConfig?.CustomBlueprintPermissions);

            // Step 9: Show configuration summary and allow override
            Console.WriteLine();
            Console.WriteLine("=================================================================");
            Console.WriteLine(" Configuration Summary");
            Console.WriteLine("=================================================================");
            Console.WriteLine($"Client App ID          : {clientAppId}");
            Console.WriteLine($"Agent Name             : {agentName}");
            if (!string.IsNullOrWhiteSpace(messagingEndpoint))
                Console.WriteLine($"Messaging Endpoint     : {messagingEndpoint}");
            Console.WriteLine($"Agent Identity Name    : {derivedNames.AgentIdentityDisplayName}");
            Console.WriteLine($"Agent Blueprint Name   : {derivedNames.AgentBlueprintDisplayName}");
            Console.WriteLine($"Agent UPN              : {derivedNames.AgentUserPrincipalName}");
            Console.WriteLine($"Agent Display Name     : {derivedNames.AgentUserDisplayName}");
            Console.WriteLine($"Manager Email          : {managerEmail}");
            Console.WriteLine($"Deployment Path        : {deploymentPath}");
            Console.WriteLine($"Tenant                 : {accountInfo.TenantId}");
            Console.WriteLine($"Custom Permissions     : {(customPermissions.Count > 0 ? $"{customPermissions.Count} configured" : "None")}");
            Console.WriteLine();

            // Step 10: Allow customization of derived names
            var customizedNames = PromptForNameCustomization(derivedNames);

            // Step 11: Final confirmation to save configuration
            Console.Write("Save this configuration? (Y/n): ");
            var saveResponse = Console.ReadLine()?.Trim().ToLowerInvariant();

            if (saveResponse is null || saveResponse == "n" || saveResponse == "no")
            {
                Console.WriteLine("Configuration cancelled.");
                _logger.LogInformation("Configuration wizard cancelled by user");
                return null;
            }

            // Step 12: Build final configuration
            var config = new Agent365Config
            {
                TenantId = accountInfo.TenantId,
                ClientAppId = clientAppId,
                Environment = existingConfig?.Environment ?? "prod", // Default to prod, not asking for this
                MessagingEndpoint = messagingEndpoint,
                AgentIdentityDisplayName = customizedNames.AgentIdentityDisplayName,
                AgentBlueprintDisplayName = customizedNames.AgentBlueprintDisplayName,
                AgentUserPrincipalName = customizedNames.AgentUserPrincipalName,
                AgentUserDisplayName = customizedNames.AgentUserDisplayName,
                ManagerEmail = managerEmail,
                AgentUserUsageLocation = GetUsageLocationFromAccount(accountInfo),
                DeploymentProjectPath = deploymentPath,
                AgentDescription = $"{agentName} - Agent 365 Agent",
                CustomBlueprintPermissions = customPermissions.Count > 0 ? customPermissions : null
            };

            _logger.LogInformation("Configuration wizard completed successfully");
            return config;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Configuration wizard failed: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<bool> VerifyAzureLoginAsync()
    {
        if (!await _azureCliService.IsLoggedInAsync())
        {
            _logger.LogError(ErrorMessages.AzureCliNotAuthenticated);
            return false;
        }

        return true;
    }

    private string PromptForAgentName(Agent365Config? existingConfig)
    {
        string defaultName;
        if (existingConfig != null)
        {
            defaultName = ExtractAgentNameFromConfig(existingConfig);
        }
        else
        {
            // Generate alphanumeric-only default
            var username = System.Text.RegularExpressions.Regex.Replace(Environment.UserName, @"[^a-zA-Z0-9]", "");
            defaultName = $"{username}agent{DateTime.Now:MMdd}";
        }

        return PromptWithDefault(
            "Agent name",
            defaultName,
            ValidateAgentName
        );
    }

    private static string ExtractAgentNameFromConfig(Agent365Config config)
    {
        // Fall back to date-based default
        _ = config; // suppress unused parameter warning
        return $"agent{DateTime.Now:MMdd}";
    }

    private string PromptForDeploymentPath(Agent365Config? existingConfig)
    {
        var defaultPath = existingConfig?.DeploymentProjectPath ?? Environment.CurrentDirectory;

        Console.WriteLine();
        Console.WriteLine("=================================================================");
        Console.WriteLine(" Deployment Project Path");
        Console.WriteLine("=================================================================");
        Console.WriteLine("The path to your agent application's source code directory.");
        Console.WriteLine("This is used to detect your project type (.NET, Node.js, or Python)");
        Console.WriteLine("and as the source directory for Azure App Service deployment.");
        Console.WriteLine();
        Console.WriteLine("  Absolute and relative paths are both accepted and will be resolved to a full path.");
        Console.WriteLine(@"  Example: /home/user/my-agent  or  C:\Projects\my-agent  or  .");
        Console.WriteLine("=================================================================");
        Console.WriteLine();

        var path = PromptWithDefault(
            "Deployment project path",
            defaultPath,
            ValidateDeploymentPath
        );

        // Additional validation using PlatformDetector
        if (!string.IsNullOrWhiteSpace(path))
        {
            var platform = _platformDetector.Detect(path);
            if (platform == ProjectPlatform.Unknown)
            {
                Console.WriteLine("WARNING: Could not detect a supported project type (.NET, Node.js, or Python) in the specified directory.");
                Console.Write("Continue anyway? (y/N): ");
                var response = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (response != "y" && response != "yes")
                {
                    Console.WriteLine("ERROR: Deployment path must contain a valid project. Configuration cancelled");
                    _logger.LogDebug("User cancelled due to invalid project detection");
                    return string.Empty;
                }
            }
            else
            {
                Console.WriteLine($"Detected {platform} project");
            }
        }

        return Path.GetFullPath(path);
    }


    private string PromptForManagerEmail(Agent365Config? existingConfig, AzureAccountInfo accountInfo)
    {
        return PromptWithDefault(
            "Manager email",
            accountInfo?.User?.Name ?? "",
            ValidateEmail
        );
    }

    private string PromptForMessagingEndpoint(Agent365Config? existingConfig)
    {
        Console.WriteLine("Provide the messaging endpoint URL where your Agent will receive messages.");
        Console.WriteLine("[Example: https://SampleAgent.azurewebsites.net/api/messages]");

        return PromptWithDefault(
            "Messaging endpoint URL",
            existingConfig?.MessagingEndpoint ?? "",
            ValidateUrl
        );
    }

    private ConfigDerivedNames GenerateDerivedNames(string agentName, string domain)
    {
        var cleanName = System.Text.RegularExpressions.Regex.Replace(agentName, @"[^a-zA-Z0-9]", "").ToLowerInvariant();
        return new ConfigDerivedNames
        {
            AgentIdentityDisplayName = $"{agentName} Identity",
            AgentBlueprintDisplayName = $"{agentName} Blueprint",
            AgentUserPrincipalName = $"{cleanName}@{domain}",
            AgentUserDisplayName = $"{agentName} Agent User"
        };
    }

    private ConfigDerivedNames PromptForNameCustomization(ConfigDerivedNames defaultNames)
    {
        Console.Write("Would you like to customize the generated names? (y/N): ");
        var response = Console.ReadLine()?.Trim().ToLowerInvariant();
        
        if (response != "y" && response != "yes")
        {
            return defaultNames;
        }

        Console.WriteLine();
        Console.WriteLine("Customizing generated names (press Enter to keep default):");
        
        return new ConfigDerivedNames
        {
            AgentIdentityDisplayName = PromptWithDefault("Agent identity name", defaultNames.AgentIdentityDisplayName),
            AgentBlueprintDisplayName = PromptWithDefault("Agent blueprint name", defaultNames.AgentBlueprintDisplayName),
            AgentUserPrincipalName = PromptWithDefault("Agent UPN", defaultNames.AgentUserPrincipalName, ValidateEmail),
            AgentUserDisplayName = PromptWithDefault("Agent display name", defaultNames.AgentUserDisplayName)
        };
    }

    private List<CustomResourcePermission> PromptForCustomBlueprintPermissions(
        List<CustomResourcePermission>? existing)
    {
        Console.WriteLine();
        Console.WriteLine("=== Optional: Custom Blueprint Permissions ===");
        Console.WriteLine("If your agent needs access to additional external resources");
        Console.WriteLine("(e.g. Teams presence, OneDrive files, custom APIs) beyond");
        Console.WriteLine("standard permissions, you can configure them here.");
        Console.WriteLine("Most agents do not require this.");

        if (existing?.Count > 0)
        {
            Console.WriteLine("\nCurrently configured:");
            foreach (var p in existing)
            {
                var name = string.IsNullOrWhiteSpace(p.ResourceName)
                    ? p.ResourceAppId
                    : $"{p.ResourceName} ({p.ResourceAppId})";
                Console.WriteLine($"  - {name}: {string.Join(", ", p.Scopes)}");
            }
        }

        Console.Write("\nConfigure custom blueprint permissions? (y/N): ");
        var response = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (response != "y" && response != "yes")
            return existing ?? new List<CustomResourcePermission>();

        var permissions = existing != null
            ? new List<CustomResourcePermission>(existing)
            : new List<CustomResourcePermission>();

        while (true)
        {
            Console.WriteLine();
            Console.Write("Resource App ID (GUID) - press Enter when done: ");
            var resourceAppId = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(resourceAppId))
                break;

            if (!Guid.TryParse(resourceAppId, out _))
            {
                Console.WriteLine("ERROR: Must be a valid GUID format (e.g. 00000003-0000-0000-c000-000000000000)");
                continue;
            }

            // Inner loop: re-prompt scopes only (GUID is already valid)
            List<string> scopesList;
            while (true)
            {
                Console.Write("Scopes (comma-separated, e.g. Presence.ReadWrite,Files.Read.All): ");
                var scopesInput = Console.ReadLine()?.Trim();
                scopesList = scopesInput?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList() ?? new List<string>();

                if (scopesList.Count == 0)
                {
                    Console.WriteLine("ERROR: At least one scope is required.");
                    continue;
                }

                var permission = new CustomResourcePermission
                {
                    ResourceAppId = resourceAppId,
                    ResourceName = null,
                    Scopes = scopesList
                };

                var (isValid, errors) = permission.Validate();
                if (!isValid)
                {
                    foreach (var error in errors)
                        Console.WriteLine($"ERROR: {error}");
                    continue;
                }

                break;
            }

            var added = CustomResourcePermission.AddOrUpdate(permissions, resourceAppId, scopesList);
            Console.WriteLine(added ? "Permission added." : "Permission updated.");
        }

        return permissions;
    }

    private string PromptWithDefault(
        string prompt,
        string defaultValue = "",
        Func<string, (bool isValid, string error)>? validator = null)
    {
        // Azure CLI style: "Prompt [default]: "
        while (true)
        {
            if (!string.IsNullOrEmpty(defaultValue))
            {
                Console.Write($"{prompt} [{defaultValue}]: ");
            }
            else
            {
                Console.Write($"{prompt}: ");
            }
            
            var input = Console.ReadLine()?.Trim() ?? "";
            
            if (string.IsNullOrWhiteSpace(input) && !string.IsNullOrEmpty(defaultValue))
            {
                input = defaultValue;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("ERROR: This field is required.");
                continue;
            }

            if (validator != null)
            {
                var (isValid, error) = validator(input);
                if (!isValid)
                {
                    Console.WriteLine($"ERROR: {error}");
                    continue;
                }
            }

            return input;
        }
    }

    private static (bool isValid, string error) ValidateAgentName(string input)
    {
        if (input.Length < 2 || input.Length > 50)
            return (false, "Agent name must be between 2-50 characters");
        
        if (!System.Text.RegularExpressions.Regex.IsMatch(input, @"^[a-zA-Z][a-zA-Z0-9]*$"))
            return (false, "Agent name must start with a letter and contain only letters and numbers (no special characters for cross-platform compatibility)");
        
        return (true, "");
    }

    private (bool isValid, string error) ValidateDeploymentPath(string input)
    {
        try
        {
            var fullPath = Path.GetFullPath(input);
            if (!Directory.Exists(fullPath))
                return (false, $"Directory does not exist: {fullPath}");
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"Invalid path: {ex.Message}");
        }
    }

    private static (bool isValid, string error) ValidateEmail(string input)
    {
        if (!input.Contains("@") || !input.Contains("."))
            return (false, "Must be a valid email format");

        var parts = input.Split('@');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            return (false, "Invalid email format. Use: username@domain");

        return (true, "");
    }

    private static (bool isValid, string error) ValidateUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return (false, "URL cannot be empty");

        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? uri))
            return (false, "Must be a valid URL format");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return (false, "URL must use HTTP or HTTPS protocol");

        return (true, "");
    }

    private string GetUsageLocationFromAccount(AzureAccountInfo accountInfo)
    {
        // Default to US for now - could be enhanced to detect from account location
        return "US";
    }

    private async Task<string?> PromptForClientAppIdAsync(Agent365Config? existingConfig, string tenantId)
    {
        Console.WriteLine();
        Console.WriteLine("=================================================================");
        Console.WriteLine(" Client App Configuration (REQUIRED)");
        Console.WriteLine("=================================================================");
        Console.WriteLine("The a365 CLI requires a custom client app registration in your");
        Console.WriteLine("Entra ID tenant with specific permissions for authentication.");
        Console.WriteLine();
        Console.WriteLine("CRITICAL: Add these as DELEGATED permissions (NOT Application):");
        foreach (var permission in AuthenticationConstants.RequiredClientAppPermissions)
        {
            Console.WriteLine($"  - {permission}");
        }
        Console.WriteLine();
        Console.WriteLine("Why Delegated? You sign in interactively, CLI acts on your behalf.");
        Console.WriteLine("Application permissions are for background services only.");
        Console.WriteLine();
        Console.WriteLine($"See: {ConfigConstants.CustomClientAppRegistrationUrl}");
        Console.WriteLine("=================================================================");
        Console.WriteLine();

        string? clientAppId = null;
        int attemptCount = 0;
        const int maxAttempts = 3;

        while (attemptCount < maxAttempts)
        {
            attemptCount++;

            // Prompt for Client App ID
            var defaultValue = existingConfig?.ClientAppId ?? string.Empty;
            clientAppId = PromptWithDefault(
                "Client App ID (GUID format)",
                defaultValue,
                input =>
                {
                    if (string.IsNullOrWhiteSpace(input))
                        return (false, "Client App ID is required");

                    if (!Guid.TryParse(input, out _))
                        return (false, "Must be a valid GUID format (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)");

                    return (true, "");
                });

            if (string.IsNullOrWhiteSpace(clientAppId))
            {
                Console.WriteLine("Client App ID is required. Setup cannot continue without it.");
                continue;
            }

            // Validate the client app
            Console.WriteLine();
            Console.WriteLine("Validating client app configuration...");
            Console.WriteLine("This may take a few seconds...");

            using var validationLoggerFactory = LoggerFactoryHelper.CreateCleanLoggerFactory();
            var executor = new CommandExecutor(validationLoggerFactory.CreateLogger<CommandExecutor>());
            var graphApiService = new GraphApiService(validationLoggerFactory.CreateLogger<GraphApiService>(), executor);
            var validator = new ClientAppValidator(validationLoggerFactory.CreateLogger<ClientAppValidator>(), graphApiService);

            try
            {
                await validator.EnsureValidClientAppAsync(clientAppId, tenantId, ct: CancellationToken.None);
                Console.WriteLine("Client app validation successful!");
                Console.WriteLine();
                return clientAppId;
            }
            catch (ClientAppValidationException ex)
            {
                // Validation failed - show errors
                Console.WriteLine();
                Console.WriteLine(ErrorMessages.ClientAppValidationFailed);
                Console.WriteLine($"  {ex.IssueDescription}");
                foreach (var error in ex.ErrorDetails)
                {
                    Console.WriteLine($"  {error}");
                }
                if (ex.MitigationSteps.Count > 0)
                {
                    foreach (var step in ex.MitigationSteps)
                    {
                        Console.WriteLine(step);
                    }
                }
                Console.WriteLine();
            }

            if (attemptCount < maxAttempts)
            {
                Console.WriteLine($"Please fix the issues and try again. (Attempt {attemptCount}/{maxAttempts})");
                Console.WriteLine("Press Enter to retry, or type 'cancel' to abort setup.");
                var response = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (response is null || response == "cancel")
                {
                    return null;
                }
            }
            else
            {
                Console.WriteLine($"Validation failed after {maxAttempts} attempts.");
                Console.WriteLine("Please fix the client app configuration and run 'a365 config init' again.");
                Console.WriteLine();
                Console.WriteLine("Common issues:");
                Console.WriteLine("  1. App not created in Azure Portal > Entra ID > App registrations");
                Console.WriteLine("  2. Permissions added as 'Application' instead of 'Delegated' type");
                Console.WriteLine("  3. Required API permissions not added");
                Console.WriteLine("  4. Admin consent not granted");
                Console.WriteLine();
                Console.WriteLine($"See: {ConfigConstants.Agent365CliDocumentationUrl}");
                return null;
            }
        }

        return clientAppId;
    }
}