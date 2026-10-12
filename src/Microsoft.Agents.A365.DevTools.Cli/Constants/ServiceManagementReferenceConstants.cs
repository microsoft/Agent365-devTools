// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;

/// <summary>
/// Constants for the serviceManagementReference value that some tenants require on every new
/// Entra application, including agent blueprints.
/// </summary>
public static class ServiceManagementReferenceConstants
{
    /// <summary>CLI option that supplies the value; matches 'az ad app create --service-management-reference'.</summary>
    public const string OptionName = "--service-management-reference";

    /// <summary>Microsoft Graph application property, also used as the a365.config.json key.</summary>
    public const string GraphPropertyName = "serviceManagementReference";
}
