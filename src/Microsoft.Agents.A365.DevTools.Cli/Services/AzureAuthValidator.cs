// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for validating Azure CLI authentication using the existing CommandExecutor.
/// </summary>
public class AzureAuthValidator
{
    private readonly ILogger<AzureAuthValidator> _logger;
    private readonly CommandExecutor _executor;

    public AzureAuthValidator(ILogger<AzureAuthValidator> logger, CommandExecutor executor)
    {
        _logger = logger;
        _executor = executor;
    }

    /// <summary>
    /// Validates Azure CLI authentication and optionally checks the active subscription.
    /// </summary>
    /// <param name="expectedSubscriptionId">The expected subscription ID to validate against. If null, only checks authentication.</param>
    /// <returns>True if authenticated and subscription matches (if specified), false otherwise.</returns>
    public async Task<bool> ValidateAuthenticationAsync(string? expectedSubscriptionId = null)
    {
        try
        {
            // Check Azure CLI authentication by trying to get current account
            var result = await _executor.ExecuteAsync("az", "account show --output json", captureOutput: true);
            
            if (!result.Success)
            {
                _logger.LogError("Azure CLI authentication required!");
                _logger.LogInformation("");
                _logger.LogInformation("Please run the following command to log in to Azure:");
                _logger.LogInformation("   az login");
                _logger.LogInformation("");
                _logger.LogInformation("After logging in, run this command again.");
                _logger.LogInformation("");
                _logger.LogInformation("For more information about Azure CLI authentication:");
                _logger.LogInformation("   https://docs.microsoft.com/en-us/cli/azure/authenticate-azure-cli");
                _logger.LogInformation("");
                return false;
            }

            // Parse the account information
            var accountJson = JsonDocument.Parse(result.StandardOutput);
            var root = accountJson.RootElement;

            var subscriptionId = root.GetProperty("id").GetString() ?? string.Empty;
            var subscriptionName = root.GetProperty("name").GetString() ?? string.Empty;
            var userName = root.GetProperty("user").GetProperty("name").GetString() ?? string.Empty;

            _logger.LogInformation("Azure CLI authenticated as: {UserName}", userName);
            _logger.LogInformation("   Active subscription: {SubscriptionName} ({SubscriptionId})", 
                subscriptionName, subscriptionId);

            // Validate subscription if specified
            if (!string.IsNullOrEmpty(expectedSubscriptionId))
            {
                if (!string.Equals(subscriptionId, expectedSubscriptionId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Azure CLI is using a different subscription than configured");
                    _logger.LogError("   Expected: {ExpectedSubscription}", expectedSubscriptionId);
                    _logger.LogError("   Current:  {CurrentSubscription}", subscriptionId);
                    _logger.LogInformation("");
                    _logger.LogInformation("Please switch to the correct subscription:");
                    _logger.LogInformation("   az account set --subscription {ExpectedSubscription}", expectedSubscriptionId);
                    _logger.LogInformation("");
                    return false;
                }
                
                _logger.LogInformation("Using correct subscription: {SubscriptionId}", expectedSubscriptionId);
            }

            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogError("Failed to parse Azure account information: {Message}", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate Azure CLI authentication");
            return false;
        }
    }
}