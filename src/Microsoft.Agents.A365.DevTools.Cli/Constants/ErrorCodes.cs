// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
namespace Microsoft.Agents.A365.DevTools.Cli.Constants
{
    public static class ErrorCodes
    {
        public const string AzureAuthFailed = "AZURE_AUTH_FAILED";
        public const string AzurePermissionDenied = "AZURE_PERMISSION_DENIED";
        public const string AzureResourceFailed = "AZURE_RESOURCE_FAILED";
        public const string AzureWebAppNameTaken = "AZURE_WEBAPP_NAME_TAKEN";
        public const string AzureResourceGroupFailed = "AZURE_RESOURCE_GROUP_FAILED";
        public const string AzureAppServicePlanFailed = "AZURE_APP_SERVICE_PLAN_FAILED";
        public const string PythonNotFound = "PYTHON_NOT_FOUND";
        public const string DeploymentAppFailed = "DEPLOYMENT_APP_FAILED";
        public const string DeploymentAppCompileFailed = "DEPLOYMENT_APP_COMPILE_FAILED";
        public const string DeploymentScopesFailed = "DEPLOYMENT_SCOPES_FAILED";
        public const string DeploymentMcpFailed = "DEPLOYMENT_MCP_FAILED";
        public const string HighPrivilegeScopeDetected = "HIGH_PRIVILEGE_SCOPE_DETECTED";
        public const string NodeBuildFailed = "NODE_BUILD_FAILED";
        public const string NodeDependencyInstallFailed = "NODE_DEPENDENCY_INSTALL_FAILED";
        public const string NodeProjectNotFound = "NODE_PROJECT_NOT_FOUND";
        public const string RetryExhausted = "RETRY_EXHAUSTED";
        public const string SetupValidationFailed = "SETUP_VALIDATION_FAILED";
        public const string ClientAppValidationFailed = "CLIENT_APP_VALIDATION_FAILED";
        public const string MosTokenAcquisitionFailed = "MOS_TOKEN_ACQUISITION_FAILED";
        public const string MosPrerequisitesFailed = "MOS_PREREQUISITES_FAILED";
        public const string MosAdminConsentRequired = "MOS_ADMIN_CONSENT_REQUIRED";
        public const string MosServicePrincipalCreationFailed = "MOS_SERVICE_PRINCIPAL_CREATION_FAILED";
        public const string MosInsufficientPrivileges = "MOS_INSUFFICIENT_PRIVILEGES";
        public const string MosPermissionUpdateFailed = "MOS_PERMISSION_UPDATE_FAILED";
        public const string DotNetSdkVersionMismatch = "DOTNET_SDK_VERSION_MISMATCH";
    }
}
