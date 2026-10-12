// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
namespace Microsoft.Agents.A365.DevTools.Cli.Constants
{
    public static class ErrorCodes
    {
        public const string AzureAuthFailed = "AZURE_AUTH_FAILED";
        public const string AzurePermissionDenied = "AZURE_PERMISSION_DENIED";
        public const string PythonNotFound = "PYTHON_NOT_FOUND";
        public const string DeploymentScopesFailed = "DEPLOYMENT_SCOPES_FAILED";
        public const string DeploymentMcpFailed = "DEPLOYMENT_MCP_FAILED";
        public const string HighPrivilegeScopeDetected = "HIGH_PRIVILEGE_SCOPE_DETECTED";
        public const string NodeProjectNotFound = "NODE_PROJECT_NOT_FOUND";
        public const string RetryExhausted = "RETRY_EXHAUSTED";
        public const string SetupValidationFailed = "SETUP_VALIDATION_FAILED";
        public const string ClientAppValidationFailed = "CLIENT_APP_VALIDATION_FAILED";
        public const string EvaluationFailed = "EVALUATION_FAILED";
        public const string SchemaDiscoveryFailed = "SCHEMA_DISCOVERY_FAILED";
        public const string GraphApiFailed = "GRAPH_API_FAILED";
        public const string GraphPermissionDenied = "GRAPH_PERMISSION_DENIED";
        public const string ServiceManagementReferenceRequired = "SERVICE_MANAGEMENT_REFERENCE_REQUIRED";
        public const string ServiceManagementReferenceRejected = "SERVICE_MANAGEMENT_REFERENCE_REJECTED";
    }
}
