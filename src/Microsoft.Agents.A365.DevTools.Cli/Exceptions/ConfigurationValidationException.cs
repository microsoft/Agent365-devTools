// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Exception thrown when configuration validation fails.
/// This is a USER ERROR - configuration file has invalid values.
/// </summary>
public class ConfigurationValidationException : Agent365Exception
{
    /// <summary>
    /// Path to the configuration file that failed validation.
    /// </summary>
    public string ConfigFilePath { get; }

    /// <summary>
    /// List of validation errors (field name + error message).
    /// </summary>
    public List<ValidationError> ValidationErrors { get; }

    public ConfigurationValidationException(
        string configFilePath,
        List<ValidationError> validationErrors)
        : base(
            errorCode: "CONFIG_VALIDATION_FAILED",
            issueDescription: "Configuration validation failed",
            errorDetails: validationErrors.Select(e => e.ToString()).ToList(),
            mitigationSteps: BuildMitigationSteps(configFilePath, validationErrors),
            context: new Dictionary<string, string>
            {
                ["ConfigFile"] = configFilePath
            })
    {
        ConfigFilePath = configFilePath;
        ValidationErrors = validationErrors;
    }

    private static List<string> BuildMitigationSteps(string configFilePath, List<ValidationError> errors)
    {
        var steps = new List<string>
        {
            $"Open your configuration file: {configFilePath}",
            "Fix the validation error(s) listed above",
            "Run 'a365 setup' again"
        };

        // Add contextual help based on error types
        var contextualHelp = new List<string>();
        
        foreach (var error in errors)
        {
            var fieldLower = error.FieldName.ToLowerInvariant();
            
            if (fieldLower.Contains("webappname") && !contextualHelp.Any(h => h.Contains("WebAppName")))
            {
                contextualHelp.Add("WebAppName: Use only letters, numbers, and hyphens (no underscores)");
                contextualHelp.Add("WebAppName: Must be 2-60 characters");
                contextualHelp.Add("WebAppName: Cannot start or end with hyphen");
            }
            
            if (fieldLower.Contains("resourcegroup") && !contextualHelp.Any(h => h.Contains("ResourceGroup")))
            {
                contextualHelp.Add("ResourceGroup: Letters, numbers, hyphens, underscores, periods, parentheses");
                contextualHelp.Add("ResourceGroup: Maximum 90 characters");
            }
            
            if ((fieldLower.Contains("tenantid") || fieldLower.Contains("subscriptionid") || error.Message.ToLowerInvariant().Contains("guid")) 
                && !contextualHelp.Any(h => h.Contains("GUID")))
            {
                contextualHelp.Add("TenantId/SubscriptionId: Must be valid GUIDs (format: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)");
            }
        }

        if (contextualHelp.Count > 0)
        {
            steps.Add("");
            steps.Add("Common Azure naming rules:");
            steps.AddRange(contextualHelp.Select(h => $"  � {h}"));
            steps.Add("");
            steps.Add("See Azure naming conventions: https://learn.microsoft.com/azure/azure-resource-manager/management/resource-name-rules");
        }

        return steps;
    }

    public override int ExitCode => 2; // Configuration error
}

/// <summary>
/// Represents a single validation error for a configuration field.
/// </summary>
public class ValidationError
{
    public string FieldName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    public ValidationError() { }

    public ValidationError(string fieldName, string message)
    {
        FieldName = fieldName;
        Message = message;
    }

    public override string ToString() => $"{FieldName}: {Message}";
}
