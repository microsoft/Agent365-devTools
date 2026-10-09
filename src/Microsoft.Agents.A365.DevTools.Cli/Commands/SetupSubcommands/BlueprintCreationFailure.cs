// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Category of a blueprint creation failure; selects the error code and remediation shown to the user.
/// </summary>
internal enum BlueprintCreationFailureKind
{
    /// <summary>The tenant requires serviceManagementReference on new applications, or rejected the value sent.</summary>
    ServiceManagementReference,

    /// <summary>The signed-in account is not authorized to create the blueprint.</summary>
    PermissionDenied,

    /// <summary>Any other failure.</summary>
    Other,
}

/// <summary>
/// Why the agent blueprint could not be created, including the Graph status and error code when available.
/// </summary>
internal sealed record BlueprintCreationFailure(
    BlueprintCreationFailureKind Kind,
    string Message,
    int? HttpStatusCode = null,
    string? GraphErrorCode = null)
{
    internal const string Operation = "Create Agent Blueprint";

    /// <summary>Label of the output line that carries the error code.</summary>
    internal const string ErrorCodeLabel = "Error code";

    private const int MaxRawBodyLength = 500;

    private static readonly Regex UrlPattern = new(@"https?://[^\s`'""<>()\[\]]+", RegexOptions.Compiled);

    /// <summary>A failure that did not come from a Graph create response.</summary>
    internal static BlueprintCreationFailure Other(string message) => new(BlueprintCreationFailureKind.Other, message);

    /// <summary>Classifies a failed Graph create response from its status code and error body.</summary>
    internal static BlueprintCreationFailure FromGraphResponse(HttpStatusCode statusCode, string? responseBody)
    {
        var (code, message) = ParseGraphError(responseBody);
        var status = (int)statusCode;

        if (string.IsNullOrWhiteSpace(message))
        {
            message = string.IsNullOrWhiteSpace(responseBody)
                ? statusCode.ToString()
                : responseBody.Length > MaxRawBodyLength ? responseBody[..MaxRawBodyLength] + "..." : responseBody;
        }

        return new BlueprintCreationFailure(Classify(status, code, message), message, status, code);
    }

    internal static BlueprintCreationFailureKind Classify(int statusCode, string? graphErrorCode, string? message)
    {
        if (statusCode is 401 or 403
            || string.Equals(graphErrorCode, "Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase))
            return BlueprintCreationFailureKind.PermissionDenied;

        // Match the property name in the Graph error rather than specific error codes, which vary by check.
        if (statusCode == 400 && (MentionsServiceManagementReference(graphErrorCode) || MentionsServiceManagementReference(message)))
            return BlueprintCreationFailureKind.ServiceManagementReference;

        return BlueprintCreationFailureKind.Other;
    }

    private static bool MentionsServiceManagementReference(string? text) =>
        text?.Contains(ServiceManagementReferenceConstants.GraphPropertyName, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Reads error.code and error.message from a Graph error body; nulls when the body is not a Graph error.</summary>
    internal static (string? Code, string? Message) ParseGraphError(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                return (code, message);
            }
        }
        catch (JsonException)
        {
            // Not JSON (e.g. a proxy error page); the caller falls back to the raw body.
        }

        return (null, null);
    }

    /// <summary>The troubleshooting link included in the Graph error message, or null when there is none.</summary>
    internal static string? GetTroubleshootingUrl(string? message)
    {
        var match = message is null ? null : UrlPattern.Match(message);
        return match is { Success: true } ? match.Value.TrimEnd('.', ',', ';', ':') : null;
    }

    /// <summary>One-line summary of the Graph response, or the failure message when there was none.</summary>
    internal string Describe()
    {
        if (HttpStatusCode is null)
            return Message;

        var code = string.IsNullOrWhiteSpace(GraphErrorCode) ? string.Empty : $" {GraphErrorCode}";
        return $"Graph response: {HttpStatusCode}{code} - {Message}";
    }

    /// <summary>
    /// Builds the user-facing error for a failed blueprint creation, with remediation for its cause.
    /// <paramref name="serviceManagementReference"/> is the value sent to Graph, or null when none was sent.
    /// </summary>
    internal static GraphApiException CreateException(BlueprintCreationFailure? failure, string? serviceManagementReference)
    {
        if (failure is null)
        {
            return Build(
                ErrorCodes.GraphApiFailed,
                new List<string> { "Blueprint creation did not complete. Review the errors logged above." },
                GraphApiException.DefaultMitigationSteps());
        }

        const string option = ServiceManagementReferenceConstants.OptionName;
        const string configKey = ServiceManagementReferenceConstants.GraphPropertyName;

        switch (failure.Kind)
        {
            case BlueprintCreationFailureKind.ServiceManagementReference when string.IsNullOrWhiteSpace(serviceManagementReference):
                return Build(
                    ErrorCodes.ServiceManagementReferenceRequired,
                    new List<string>
                    {
                        $"This tenant requires {configKey} on every new application, including agent blueprints.",
                        failure.Describe(),
                    },
                    WithTroubleshootingUrl(failure, new List<string>
                    {
                        $"Re-run with {option} <GUID your tenant expects in {configKey}>.",
                        $"To avoid passing it on every run, set \"{configKey}\" in a365.config.json (read when --agent-name is not used).",
                    }));

            case BlueprintCreationFailureKind.ServiceManagementReference:
                return Build(
                    ErrorCodes.ServiceManagementReferenceRejected,
                    new List<string>
                    {
                        $"Microsoft Graph rejected the {configKey} value '{serviceManagementReference}'.",
                        failure.Describe(),
                    },
                    WithTroubleshootingUrl(failure, new List<string>
                    {
                        $"Verify that {serviceManagementReference} is the {configKey} value your tenant expects for this agent.",
                        $"Re-run with the correct {option} value, or correct \"{configKey}\" in a365.config.json.",
                    }));

            case BlueprintCreationFailureKind.PermissionDenied:
                var roleGuidance = new List<string>();
                if (failure.HttpStatusCode == 401)
                {
                    roleGuidance.Add("Sign in again; the access token may have expired or been issued for a different tenant.");
                }
                roleGuidance.Add($"Creating an agent blueprint requires an active {AuthenticationConstants.BlueprintCreationRequiredRoles} role.");
                roleGuidance.Add("If the role is assigned as eligible through Privileged Identity Management (PIM), activate it first; activation can take a few minutes to apply.");
                roleGuidance.Add("Then re-run the command.");
                return Build(
                    ErrorCodes.GraphPermissionDenied,
                    new List<string>
                    {
                        "The signed-in account is not authorized to create agent blueprints in this tenant.",
                        failure.Describe(),
                    },
                    roleGuidance);

            default:
                return Build(
                    ErrorCodes.GraphApiFailed,
                    new List<string> { "Blueprint creation failed.", failure.Describe() },
                    IsRejectedRequest(failure.HttpStatusCode)
                        ? new List<string>
                        {
                            "Correct the problem described in the Graph response above, then re-run the command.",
                            "If it persists, open an issue at https://github.com/microsoft/Agent365-devTools/issues with the request-id from the log file.",
                        }
                        : GraphApiException.DefaultMitigationSteps());
        }
    }

    // The shared formatter prints context lines, so the code reaches both setup commands' output
    // without changing how errors from other commands are formatted.
    private static GraphApiException Build(string errorCode, List<string> errorDetails, List<string> mitigationSteps) =>
        new(Operation, errorCode, errorDetails, mitigationSteps,
            new Dictionary<string, string> { [ErrorCodeLabel] = errorCode });

    private static List<string> WithTroubleshootingUrl(BlueprintCreationFailure failure, List<string> steps)
    {
        var url = GetTroubleshootingUrl(failure.Message);
        if (url is not null)
            steps.Add($"Troubleshooting: {url}");
        return steps;
    }

    // A 4xx other than timeout/throttling means Graph rejected the request itself, so retrying unchanged will not help.
    private static bool IsRejectedRequest(int? statusCode) =>
        statusCode is >= 400 and < 500 and not 408 and not 429;
}
