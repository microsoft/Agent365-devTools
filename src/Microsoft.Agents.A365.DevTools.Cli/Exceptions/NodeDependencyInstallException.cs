// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Thrown when installation of Node.js dependencies fails.
/// </summary>
public class NodeDependencyInstallException : Agent365Exception
{
    public override int ExitCode => 1;
    public override bool IsUserError => true;

    public NodeDependencyInstallException(string projectDirectory, string? npmErrorOutput)
        : base(
            errorCode: ErrorCodes.NodeDependencyInstallFailed,
            issueDescription: "Failed to install Node.js dependencies for the project.",
            errorDetails: BuildDetails(projectDirectory, npmErrorOutput),
            mitigationSteps: new List<string>
            {
                "Run 'npm install' (or 'npm ci') locally in the project directory and fix any errors.",
                "Check that your internet connection and npm registry access are working.",
                "If you use a private registry or npm auth, ensure those settings are configured on the machine running 'a365 deploy'.",
                "After fixing the issue, rerun 'a365 deploy'."
            },
            context: new Dictionary<string, string>
            {
                ["ProjectDirectory"] = projectDirectory
            })
    {
    }

    private static List<string> BuildDetails(string projectDirectory, string? npmErrorOutput)
    {
        var details = new List<string>
        {
            $"Project directory: {projectDirectory}",
        };

        if (!string.IsNullOrWhiteSpace(npmErrorOutput))
        {
            details.Add("npm error output (truncated):");
            details.Add($"  {TrimError(npmErrorOutput)}");
        }

        return details;
    }

    private static string TrimError(string error)
    {
        const int maxLen = 400;
        error = error.Trim();
        return error.Length <= maxLen ? error : error[..maxLen] + " ...";
    }
}