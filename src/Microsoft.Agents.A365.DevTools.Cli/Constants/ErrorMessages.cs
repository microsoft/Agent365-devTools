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

    #region Project Secret Destination Messages

    public const string SecretDestinationRemediation =
        "To fix this, point deploymentProjectPath at the directory that contains a365.config.json or one of its subdirectories, " +
        "or move a365.config.json and a365.generated.config.json into your project directory (or a parent directory) and run the CLI from there. " +
        "Replace any symbolic link or junction on that path with a regular directory or file.";

    public const string ProjectSettingsBlockedWarning =
        "Project settings: not written because the project directory or a settings file is outside the directory that contains a365.config.json, " +
        "or is a symbolic link or junction. Point deploymentProjectPath at that directory or one of its subdirectories, then re-run setup.";

    #endregion

    #region Client App Validation Messages

    public const string ClientAppValidationFailed =
        "Client app validation FAILED:";

    public const string ClientAppValidationFixHeader =
        "To fix this:";

    #endregion

    #region Graph Authentication Messages

    /// <summary>
    /// Formatted with the requested scopes. A null token has several causes, so this names the app
    /// and who can consent without asserting that missing consent is the reason.
    /// </summary>
    public const string AgentAccessTokenUnavailable =
        "Could not acquire a Microsoft Graph token for 'Microsoft Graph Command Line Tools' " +
        "(app ID 14d82eec-204b-4c2f-b7e8-296a70dab67e) with scopes: {Scopes}. " +
        "See the preceding authentication error for the cause. If these scopes have not been " +
        "consented to in this tenant, a Global Administrator or Privileged Role Administrator " +
        "must grant them to that enterprise application.";

    #endregion

}
