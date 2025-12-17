// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Exception thrown when client app validation fails.
/// This indicates the configured client app does not exist or lacks required permissions.
/// </summary>
public sealed class ClientAppValidationException : Agent365Exception
{
    public ClientAppValidationException(
        string issueDescription,
        List<string> errorDetails,
        List<string> mitigationSteps,
        Dictionary<string, string>? context = null)
        : base(
            errorCode: ErrorCodes.ClientAppValidationFailed,
            issueDescription: issueDescription,
            errorDetails: errorDetails,
            mitigationSteps: mitigationSteps,
            context: context)
    {
    }

    /// <summary>
    /// Creates exception for when client app is not found in tenant.
    /// </summary>
    public static ClientAppValidationException AppNotFound(string clientAppId, string tenantId)
    {
        return new ClientAppValidationException(
            issueDescription: "Client app not found in tenant",
            errorDetails: new List<string>
            {
                $"Client app with ID '{clientAppId}' does not exist in tenant '{tenantId}'",
                "The app may not be registered, or you may be using the wrong ID"
            },
            mitigationSteps: new List<string>
            {
                "Verify 'clientAppId' in a365.config.json is the Application (client) ID, not Object ID.",
                "Run 'a365 config init' to create a new app, or check the app exists in Azure Portal.",
                "Ensure you are logged in with the correct tenant using 'az login'.",
                $"See setup guide: {ConfigConstants.Agent365CliDocumentationUrl}"
            },
            context: new Dictionary<string, string>
            {
                ["clientAppId"] = clientAppId,
                ["tenantId"] = tenantId
            });
    }

    /// <summary>
    /// Creates exception for missing permissions.
    /// </summary>
    public static ClientAppValidationException MissingPermissions(
        string clientAppId,
        List<string> missingPermissions)
    {
        return new ClientAppValidationException(
            issueDescription: "Client app is missing required API permissions",
            errorDetails: new List<string>
            {
                $"Missing permissions: {string.Join(", ", missingPermissions)}"
            },
            mitigationSteps: new List<string>
            {
                "Add missing Microsoft Graph delegated permissions in Azure Portal > App registrations > Your app > API permissions.",
                "Grant admin consent after adding permissions.",
                "Wait a few minutes for permission changes to propagate.",
                "Verify the permissions match the required list exactly.",
                $"See setup guide: {ConfigConstants.Agent365CliDocumentationUrl}"
            },
            context: new Dictionary<string, string>
            {
                ["clientAppId"] = clientAppId,
                ["missingPermissions"] = string.Join(", ", missingPermissions)
            });
    }

    /// <summary>
    /// Creates exception for missing admin consent.
    /// </summary>
    public static ClientAppValidationException MissingAdminConsent(string clientAppId)
    {
        return new ClientAppValidationException(
            issueDescription: "Admin consent not granted for client app",
            errorDetails: new List<string>
            {
                "The required permissions are configured but admin consent is missing",
                "Admin consent must be granted by a Global Administrator"
            },
            mitigationSteps: new List<string>
            {
                "Grant admin consent at: Azure Portal > App registrations > Your app > API permissions.",
                "Click 'Grant admin consent for [Your Tenant]' and wait for propagation.",
                "Confirm the consent dialog when prompted.",
                "Verify the status shows 'Granted for [Your Tenant]' with a green checkmark.",
                "Wait a few minutes for consent to propagate through Azure AD.",
                $"See setup guide: {ConfigConstants.Agent365CliDocumentationUrl}"
            },
            context: new Dictionary<string, string>
            {
                ["clientAppId"] = clientAppId
            });
    }

    /// <summary>
    /// Creates exception for general validation failures with custom details.
    /// </summary>
    public static ClientAppValidationException ValidationFailed(
        string issueDescription,
        List<string> errorDetails,
        string? clientAppId = null)
    {
        var context = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(clientAppId))
        {
            context["clientAppId"] = clientAppId;
        }

        return new ClientAppValidationException(
            issueDescription: issueDescription,
            errorDetails: errorDetails,
            mitigationSteps: new List<string>
            {
                "Check the error details above",
                "Ensure you are logged in with 'az login'",
                "Verify your client app configuration in Azure Portal",
                $"See setup guide: {ConfigConstants.Agent365CliDocumentationUrl}"
            },
            context: context);
    }
}
