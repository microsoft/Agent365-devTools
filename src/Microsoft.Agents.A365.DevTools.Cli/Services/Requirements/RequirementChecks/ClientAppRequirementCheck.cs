// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Validates first-party or custom client-app presence and Microsoft Graph authorization.
/// </summary>
public class ClientAppRequirementCheck : RequirementCheck
{
    private readonly IClientAppValidator _clientAppValidator;

    /// <param name="clientAppValidator">The validator used to check the client app configuration.</param>
    public ClientAppRequirementCheck(IClientAppValidator clientAppValidator)
    {
        _clientAppValidator = clientAppValidator ?? throw new ArgumentNullException(nameof(clientAppValidator));
    }

    /// <inheritdoc />
    public override string Name => "Client App Configuration";

    /// <inheritdoc />
    public override string Description => "Validates that the first-party or custom client app exists and has the required Microsoft Graph authorization";

    /// <inheritdoc />
    public override string Category => "Authentication";

    /// <inheritdoc />
    public override async Task<RequirementCheckResult> CheckAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken = default)
    {
        return await ExecuteCheckWithLoggingAsync(config, logger, CheckImplementationAsync, cancellationToken);
    }

    /// <summary>
    /// The actual implementation of the client app requirement check
    /// </summary>
    private async Task<RequirementCheckResult> CheckImplementationAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken)
    {
        // Check if clientAppId is configured
        if (string.IsNullOrWhiteSpace(config.ClientAppId))
        {
            return RequirementCheckResult.Failure(
                errorMessage: "clientAppId is not configured",
                resolutionGuidance: "Run 'a365 config' to configure your client app ID, or run 'a365 setup blueprint' to create a new client app",
                details: "The clientAppId must be specified in the configuration to validate the client app setup."
            );
        }

        // Check if tenantId is configured
        if (string.IsNullOrWhiteSpace(config.TenantId))
        {
            return RequirementCheckResult.Failure(
                errorMessage: "tenantId is not configured",
                resolutionGuidance: "Run 'a365 config' to configure your tenant ID",
                details: "The tenantId must be specified in the configuration to validate the client app setup."
            );
        }

        try
        {
            // First-party semantics are keyed off the well-known app ID inside the validator, so
            // Microsoft's own registration is never PATCHed regardless of the call site.
            await _clientAppValidator.EnsureValidClientAppAsync(
                config.ClientAppId,
                config.TenantId,
                ct: cancellationToken
            );

            return RequirementCheckResult.Success(
                details: config.ClientAppId
            );
        }
        catch (ClientAppValidationException ex)
        {
            // Use IssueDescription + ErrorDetails to avoid exposing internal error codes from ex.Message
            var errorLines = new List<string> { ex.IssueDescription };
            errorLines.AddRange(ex.ErrorDetails);
            return RequirementCheckResult.Failure(
                errorMessage: string.Join("\n", errorLines),
                resolutionGuidance: string.Join("\n", ex.MitigationSteps),
                details: $"Client app validation failed for {config.ClientAppId}. Please ensure the app exists and has the required configuration."
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unexpected error validating client app");
            return RequirementCheckResult.Failure(
                errorMessage: $"Unexpected error validating client app: {ex.Message}",
                resolutionGuidance: "Check the logs for more details. Ensure you are logged in with 'az login' and have the necessary permissions.",
                details: ex.ToString()
            );
        }
    }
}
