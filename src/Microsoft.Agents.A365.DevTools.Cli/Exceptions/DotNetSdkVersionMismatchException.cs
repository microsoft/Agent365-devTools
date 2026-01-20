// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Exceptions;

/// <summary>
/// Thrown when there is a .NET SDK version mismatch.
/// </summary>
public sealed class DotNetSdkVersionMismatchException : Agent365Exception
{
    public override bool IsUserError => true;
    public override int ExitCode => 1;

    public DotNetSdkVersionMismatchException(
        string requiredVersion,
        string? installedVersion,
        string projectFilePath)
        : base(
            errorCode: ErrorCodes.DotNetSdkVersionMismatch,
            issueDescription: $"The project targets .NET {requiredVersion}, but the required .NET SDK is not installed.",
            errorDetails: new List<string>
            {
                $"Project file: {projectFilePath}",
                $"TargetFramework: net{requiredVersion}",
                $"Installed SDK version: {installedVersion ?? "Not found"}"
            },
            mitigationSteps: new List<string>
            {
                $"Install the .NET {requiredVersion} SDK from https://dotnet.microsoft.com/download",
                "Restart your terminal after installation",
                "Re-run the a365 deploy command"
            },
            context: new Dictionary<string, string>
            {
                ["TargetDotNetVersion"] = requiredVersion,
                ["ProjectFile"] = projectFilePath
            })
    {
    }
}