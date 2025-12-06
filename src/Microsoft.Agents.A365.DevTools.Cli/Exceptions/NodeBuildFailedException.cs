// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Thrown when the build process of a Node.js project fails.
/// </summary>
public sealed class NodeBuildFailedException : Agent365Exception
{
    public override int ExitCode => 1;
    public override bool IsUserError => true;

    public NodeBuildFailedException(string projectDirectory, string? npmErrorOutput)
        : base(
            errorCode: ErrorCodes.NodeBuildFailed,
            issueDescription: "Failed to build the Node.js project using 'npm run build'.",
            errorDetails: BuildDetails(projectDirectory, npmErrorOutput),
            mitigationSteps: new List<string>
            {
                "Run 'npm run build' locally in the project directory and fix any TypeScript/webpack/build errors.",
                "Verify that the 'build' script is defined correctly in package.json.",
                "If the build depends on environment variables or private packages, ensure those are configured on the machine running 'a365 deploy'.",
                "After resolving the build issues, rerun 'a365 deploy'."
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
            details.Add("npm build error output (truncated):");
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