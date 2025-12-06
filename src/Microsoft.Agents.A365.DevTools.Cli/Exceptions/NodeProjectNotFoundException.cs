// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Thrown when a Node.js deployment is requested from a directory that does not contain a package.json.
/// </summary>
public class NodeProjectNotFoundException : Agent365Exception
{
    public override int ExitCode => 1;
    public override bool IsUserError => true;

    public NodeProjectNotFoundException(string projectDirectory)
        : base(
            errorCode: ErrorCodes.NodeProjectNotFound,
            issueDescription: "No Node.js project was found in the specified directory.",
            errorDetails: new List<string>
            {
                "The deployment expects a package.json file to identify the Node.js project.",
                $"Checked directory: {projectDirectory}"
            },
            mitigationSteps: new List<string>
            {
                "Run this command from the root of your Node.js project (where package.json is located), or",
                "Update the --project-path in your a365 config to point to the folder containing package.json.",
                "Verify that package.json is checked into source control and not ignored or deleted."
            },
            context: new Dictionary<string, string>
            {
                ["ProjectDirectory"] = projectDirectory
            })
    {
    }
}