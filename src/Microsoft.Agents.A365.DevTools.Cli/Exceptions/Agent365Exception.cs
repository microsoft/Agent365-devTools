// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Base exception for all Agent365 CLI errors.
/// Provides structured error information for consistent user-facing error messages.
/// Follows Microsoft CLI best practices (Azure CLI, dotnet CLI patterns).
/// </summary>
public abstract class Agent365Exception : Exception
{
    /// <summary>
    /// Unique error code for this error type (e.g., "CONFIG_INVALID", "AUTH_FAILED").
    /// Used for programmatic error handling and documentation.
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// Human-readable description of what went wrong.
    /// </summary>
    public string IssueDescription { get; }

    /// <summary>
    /// List of specific error details (e.g., validation errors).
    /// </summary>
    public List<string> ErrorDetails { get; }

    /// <summary>
    /// Suggested mitigation steps to resolve the error.
    /// </summary>
    public List<string> MitigationSteps { get; }

    /// <summary>
    /// Additional context data for error reporting (optional).
    /// Example: file paths, resource names, etc.
    /// </summary>
    public Dictionary<string, string> Context { get; }

    /// <summary>
    /// Exit code to return when this exception is caught at the entry point.
    /// Default: 1 (generic error).
    /// </summary>
    public virtual int ExitCode => 1;

    /// <summary>
    /// Whether this is a user error (validation, config issue) vs system error (bug).
    /// User errors suppress stack traces in output.
    /// </summary>
    public virtual bool IsUserError => true;

    protected Agent365Exception(
        string errorCode,
        string issueDescription,
        List<string>? errorDetails = null,
        List<string>? mitigationSteps = null,
        Dictionary<string, string>? context = null,
        Exception? innerException = null)
        : base(BuildMessage(errorCode, issueDescription, errorDetails), innerException)
    {
        ErrorCode = errorCode;
        IssueDescription = issueDescription;
        ErrorDetails = errorDetails ?? new List<string>();
        MitigationSteps = mitigationSteps ?? new List<string>();
        Context = context ?? new Dictionary<string, string>();
    }

    /// <summary>
    /// Build exception message for logging (includes all structured data).
    /// </summary>
    private static string BuildMessage(string errorCode, string issueDescription, List<string>? errorDetails)
    {
        var sb = new StringBuilder();
        sb.Append($"[{errorCode}] {issueDescription}");
        
        if (errorDetails?.Count > 0)
        {
            sb.AppendLine();
            foreach (var detail in errorDetails)
            {
                sb.AppendLine($"  * {detail}");
            }
        }
        
        return sb.ToString();
    }

    /// <summary>
    /// Get formatted error message for CLI output (user-friendly, no technical jargon).
    /// </summary>
    public virtual string GetFormattedMessage()
    {
        var sb = new StringBuilder();

        // Error header - Azure CLI style (no leading blank line, ERROR: prefix)
        sb.AppendLine($"ERROR: {IssueDescription}");

        // Error details (no header, just indented content)
        if (ErrorDetails.Count > 0)
        {
            sb.AppendLine();
            foreach (var detail in ErrorDetails)
            {
                sb.AppendLine($"  {detail}");
            }
        }

        // Mitigation steps - clearer header
        if (MitigationSteps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("To resolve this issue:");
            for (int i = 0; i < MitigationSteps.Count; i++)
            {
                sb.AppendLine($"  {i + 1}. {MitigationSteps[i]}");
            }
        }

        // Context information
        if (Context.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Additional context:");
            foreach (var kvp in Context)
            {
                sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
            }
        }

        // Error code at the end (Azure CLI style)
        sb.AppendLine();
        sb.AppendLine($"Error code: {ErrorCode}");

        return sb.ToString();
    }
}
