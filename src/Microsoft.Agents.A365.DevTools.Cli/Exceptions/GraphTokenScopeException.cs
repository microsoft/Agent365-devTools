// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Exception thrown when a Graph access token contains disallowed high-privilege scopes.
/// </summary>
public class GraphTokenScopeException : Agent365Exception
{
    private const string IssueDescriptionText = "Graph token contains high-privilege scopes";

    public GraphTokenScopeException(string scope)
        : base(
            errorCode: ErrorCodes.HighPrivilegeScopeDetected,
            issueDescription: IssueDescriptionText,
            errorDetails: new List<string> { $"Disallowed scope detected in token: {scope}" },
            mitigationSteps: new List<string>
            {
                "Check Microsoft Graph Command Line Tools app permissions: Azure portal ? App registrations ? 'Microsoft Graph Command Line Tools' (App ID: 14d82eec-204b-4c2f-b7e8-296a70dab67e) ? API permissions.",
                "Look for 'Directory.AccessAsUser.All' and remove it or replace it with a least-privilege alternative (for example 'Directory.Read.All') if appropriate.",
                "Re-run the CLI and, when the browser consent prompt appears, approve only the scopes requested by the CLI.",
                "Note: Removing tenant-wide admin consent for this permission may impact other tools or automation that rely on it. Verify impact before removal."
            })
    {
    }

    public override int ExitCode => 2; // Configuration / permission error
}
