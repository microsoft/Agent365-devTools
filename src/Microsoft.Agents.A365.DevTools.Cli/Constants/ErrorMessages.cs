// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;

/// <summary>
/// Centralized error and warning messages for Agent365 CLI.
/// Provides consistent, user-friendly messaging across commands.
/// </summary>
public static class ErrorMessages
{
    #region Azure Authentication Messages

    public const string AzureCliNotAuthenticated =
        "You are not logged in to Azure CLI. Please run 'az login' and select your subscription, then try again";

    public const string AzureCliInstallRequired =
        "Azure CLI is not installed. Install from: https://aka.ms/azure-cli";

    #endregion

    #region Configuration Messages

    public const string ConfigFileNotFound =
        "Configuration file not found. Run 'a365 setup all --agent-name <name>' to set up from scratch.";

    public const string InvalidConfigFormat =
        "Configuration file has invalid JSON format";

    #endregion

    #region Client App Validation Messages

    public const string ClientAppValidationFailed =
        "Client app validation FAILED:";

    public const string ClientAppValidationFixHeader =
        "To fix this:";

    #endregion

}
