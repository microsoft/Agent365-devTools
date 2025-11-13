// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Unified Azure validator that orchestrates all Azure-related validations.
/// </summary>
public interface IAzureValidator
{
    /// <summary>
    /// Validates Azure CLI authentication, subscription, and environment.
    /// </summary>
    /// <param name="subscriptionId">Expected subscription ID</param>
    /// <returns>True if all validations pass</returns>
    Task<bool> ValidateAllAsync(string subscriptionId);
}

public class AzureValidator : IAzureValidator
{
    private readonly AzureAuthValidator _authValidator;
    private readonly IAzureEnvironmentValidator _environmentValidator;
    private readonly ILogger<AzureValidator> _logger;

    public AzureValidator(
        AzureAuthValidator authValidator,
        IAzureEnvironmentValidator environmentValidator,
        ILogger<AzureValidator> logger)
    {
        _authValidator = authValidator;
        _environmentValidator = environmentValidator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ValidateAllAsync(string subscriptionId)
    {
        _logger.LogInformation("Validating Azure CLI authentication and subscription...");
        
        // Authentication validation (critical - stops execution if failed)
        if (!await _authValidator.ValidateAuthenticationAsync(subscriptionId))
        {
            _logger.LogError("Setup cannot proceed without proper Azure CLI authentication and subscription");
            return false;
        }

        // Environment validation (warnings only - doesn't stop execution)
        await _environmentValidator.ValidateEnvironmentAsync();
        
        return true;
    }
}