// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

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
    public virtual async Task<bool> ValidateAuthenticationAsync(string? expectedSubscriptionId = null, CancellationToken ct = default)
    {
        try
        {
            // Check Azure CLI authentication by trying to get current account
            var result = await _executor.ExecuteAsync("az", "account show --output json", captureOutput: true, suppressErrorLogging: true, cancellationToken: ct);

            if (!result.Success)
            {
                _logger.LogDebug("Azure CLI authentication check failed: {Error}", result.StandardError);
                return false;
            }

            // Clean and parse the account information
            var cleanedOutput = JsonDeserializationHelper.CleanAzureCliJsonOutput(result.StandardOutput);
            var accountJson = JsonDocument.Parse(cleanedOutput);
            var root = accountJson.RootElement;

            var subscriptionId = root.GetProperty("id").GetString() ?? string.Empty;
            var subscriptionName = root.GetProperty("name").GetString() ?? string.Empty;
            var userName = root.GetProperty("user").GetProperty("name").GetString() ?? string.Empty;

            _logger.LogDebug("Azure CLI authenticated as: {UserName}", userName);
            _logger.LogDebug("   Active subscription: {SubscriptionName} ({SubscriptionId})", 
                subscriptionName, subscriptionId);

            // Validate subscription if specified
            if (!string.IsNullOrEmpty(expectedSubscriptionId))
            {
                if (!string.Equals(subscriptionId, expectedSubscriptionId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Subscription mismatch — expected: {Expected}, current: {Current}", expectedSubscriptionId, subscriptionId);
                    return false;
                }
                
                _logger.LogDebug("Using correct subscription: {SubscriptionId}", expectedSubscriptionId);
            }

            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogError("Failed to parse Azure account information: {Message}", ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to validate Azure CLI authentication");
            return false;
        }
    }

    /// <summary>
    /// Probes the Azure App Service token scope to verify deployment credentials are valid.
    /// Returns false if the grant is expired or revoked (AADSTS50173 / invalid_grant).
    /// </summary>
    public virtual async Task<bool> GetAppServiceTokenAsync(CancellationToken ct = default)
    {
        var result = await _executor.ExecuteAsync(
            "az",
            "account get-access-token --resource https://appservice.azure.com",
            captureOutput: true,
            suppressErrorLogging: true,
            cancellationToken: ct);

        _logger.LogDebug("App Service token probe: {Result}", result.Success ? "valid" : "expired or revoked");
        return result.Success;
    }
}