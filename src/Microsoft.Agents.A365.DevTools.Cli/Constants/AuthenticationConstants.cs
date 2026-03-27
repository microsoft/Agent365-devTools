// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;

/// <summary>
/// Constants for authentication and security operations
/// </summary>
public static class AuthenticationConstants
{
    /// <summary>
    /// Azure CLI public client ID (well-known, not a secret)
    /// This is a Microsoft first-party app ID that's publicly documented
    /// </summary>
    public const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    public const string PowershellClientId = "1950a258-227b-4e31-a9cf-717495945fc2";

    /// <summary>
    /// Common tenant ID for multi-tenant authentication
    /// </summary>
    public const string CommonTenantId = "common";

    /// <summary>
    /// Localhost redirect URI for interactive browser authentication.
    /// Uses a fixed port (8400) to ensure consistent OAuth callbacks across multiple
    /// authentication attempts. Users must configure this exact URI in their custom
    /// client app registration: http://localhost:8400/
    /// </summary>
    public const string LocalhostRedirectUri = "http://localhost:8400/";

    /// <summary>
    /// Required redirect URIs for authentication.
    /// <list type="bullet">
    /// <item><term>http://localhost</term><description>Required by the Microsoft Graph PowerShell SDK
    /// (<c>Connect-MgGraph -ClientId</c>). Without this URI, PowerShell-based operations (OAuth2 grants,
    /// service principal lookups) fall back to the Azure CLI token, which lacks required delegated
    /// permissions and causes 403 errors on inheritable permissions operations.</description></item>
    /// <item><term>http://localhost:8400/</term><description>Required by MSAL for interactive browser
    /// authentication. Uses a fixed port to ensure consistent OAuth callbacks.</description></item>
    /// </list>
    /// See also <see cref="WamBrokerRedirectUriFormat"/> for the Windows WAM broker URI.
    /// </summary>
    public static readonly string[] RequiredRedirectUris = new[]
    {
        "http://localhost",
        "http://localhost:8400/"
    };

    /// <summary>
    /// WAM (Windows Authentication Broker) redirect URI format.
    /// This URI is required for WAM-based authentication on Windows.
    /// The {0} placeholder should be replaced with the client app ID.
    /// </summary>
    public const string WamBrokerRedirectUriFormat = "ms-appx-web://microsoft.aad.brokerplugin/{0}";

    /// <summary>
    /// Gets all required redirect URIs including the WAM broker URI for a specific client app.
    /// Note: This method allocates a new array on each call. Callers should cache the result
    /// if they need to use it multiple times.
    /// </summary>
    /// <param name="clientAppId">The client application ID</param>
    /// <returns>Array of all required redirect URIs</returns>
    public static string[] GetRequiredRedirectUris(string clientAppId)
    {
        var uris = new List<string>(RequiredRedirectUris);
        if (!string.IsNullOrWhiteSpace(clientAppId))
        {
            uris.Add(string.Format(WamBrokerRedirectUriFormat, clientAppId));
        }
        return uris.ToArray();
    }

    /// <summary>
    /// Application name for cache directory
    /// </summary>
    public const string ApplicationName = "Microsoft.Agents.A365.DevTools.Cli";

    /// <summary>
    /// Token cache file name
    /// </summary>
    public const string TokenCacheFileName = "auth-token.json";

    /// <summary>
    /// Token expiration buffer in minutes
    /// Tokens are considered expired this many minutes before actual expiration
    /// to prevent using tokens that expire during a request
    /// </summary>
    public const int TokenExpirationBufferMinutes = 5;

    /// <summary>
    /// Microsoft Graph resource app ID (well-known constant)
    /// Used to identify Microsoft Graph API in permission requests
    /// </summary>
    public const string MicrosoftGraphResourceAppId = "00000003-0000-0000-c000-000000000000";

    /// <summary>
    /// Microsoft Graph identifier URI (used for admin consent URL construction).
    /// </summary>
    public const string MicrosoftGraphResourceUri = "https://graph.microsoft.com";

    /// <summary>
    /// Redirect URI registered on the blueprint application to support the /v2.0/adminconsent flow.
    /// AAD requires at least one redirect URI on the application — AADSTS500113 is returned otherwise.
    /// This is the standard Entra Portal redirect URI used for admin consent; it shows a generic
    /// "consent granted" page and requires no real endpoint on our side.
    /// </summary>
    public const string BlueprintConsentRedirectUri = "https://entra.microsoft.com/TokenAuthorize";

    /// <summary>
    /// Delegated scope for reading directory role assignments.
    /// Retained as a named constant for use cases where a lower-privilege role-read scope is required.
    /// </summary>
    public const string RoleManagementReadDirectoryScope = "RoleManagement.Read.Directory";

    /// <summary>
    /// Delegated scope granted implicitly to all Microsoft Graph delegated tokens.
    /// Used for /me and /me/transitiveMemberOf calls that require only basic user identity access.
    /// </summary>
    public const string UserReadScope = "User.Read";

    /// <summary>
    /// Well-known template ID for the "Global Administrator" built-in Entra role.
    /// Required to grant tenant-wide admin consent interactively.
    /// </summary>
    public const string GlobalAdminRoleTemplateId = "62e90394-69f5-4237-9190-012177145e10";

    /// <summary>
    /// Well-known template ID for the "Agent ID Administrator" built-in Entra role.
    /// Required to create or update inheritable permissions on agent blueprints.
    /// </summary>
    public const string AgentIdAdminRoleTemplateId = "db506228-d27e-4b7d-95e5-295956d6615f";

    /// <summary>
    /// Delegated scope for broad directory read access.
    /// Required for /me/memberOf and other directory read operations.
    /// </summary>
    public const string DirectoryReadAllScope = "Directory.Read.All";

    /// <summary>
    /// Delegated scope for read/write access to Entra ID applications.
    /// Used for FIC retrieval and deletion operations that are not yet covered by
    /// more granular AgentIdentityBlueprint.* scopes.
    /// </summary>
    public const string ApplicationReadWriteAllScope = "Application.ReadWrite.All";

    /// <summary>
    /// Delegated scope required to delete an Agent Blueprint.
    /// Per the Agent ID permissions reference, this is the correct scope for Delete operations.
    /// </summary>
    public const string AgentIdentityBlueprintDeleteRestoreAllScope = "AgentIdentityBlueprint.DeleteRestore.All";

    /// <summary>
    /// Delegated scope required to add or remove federated identity credentials and password credentials
    /// on an Agent Blueprint. Per the Agent ID permissions reference, covers keyCredentials,
    /// passwordCredentials, and federatedIdentityCredentials. Requires Global Administrator or
    /// Agent ID Administrator role.
    /// </summary>
    public const string AgentIdentityBlueprintAddRemoveCredsAllScope = "AgentIdentityBlueprint.AddRemoveCreds.All";

    /// <summary>
    /// Delegated scope for full read/write access to an Agent Blueprint.
    /// Includes all granular update permissions (UpdateAuthProperties, AddRemoveCreds, UpdateBranding).
    /// Used for client secret creation where AddRemoveCreds.All may not yet be individually consented
    /// on the client app — ReadWrite.All is already consented and avoids bundling
    /// Directory.AccessAsUser.All that comes with Application.ReadWrite.All/.default.
    /// </summary>
    public const string AgentIdentityBlueprintReadWriteAllScope = "AgentIdentityBlueprint.ReadWrite.All";

    /// <summary>
    /// Required delegated permissions for the custom client app used by a365 CLI.
    /// These permissions enable the CLI to manage Entra ID applications and agent blueprints.
    /// All permissions require admin consent.
    ///
    /// Permission GUIDs are resolved dynamically at runtime from Microsoft Graph to ensure
    /// compatibility across different tenants and API versions.
    /// </summary>
    public static readonly string[] RequiredClientAppPermissions = new[]
    {
        "Application.ReadWrite.All",
        "AgentIdentityBlueprint.ReadWrite.All",
        "AgentIdentityBlueprint.UpdateAuthProperties.All",
        "AgentIdentityBlueprint.AddRemoveCreds.All",  // Required for passwordCredentials and FICs during setup and cleanup
        "DelegatedPermissionGrant.ReadWrite.All",
        "Directory.Read.All"
        // Note: RoleManagementReadDirectoryScope and AgentIdentityBlueprint.DeleteRestore.All are
        // intentionally excluded. DeleteRestore.All is a cleanup-only scope acquired on-demand via
        // interactive consent during 'a365 cleanup'. RoleManagementReadDirectoryScope is excluded
        // because Directory.Read.All already covers the needed read operations.
    };

    /// <summary>
    /// Required scopes for all PowerShell-based Microsoft Graph operations (OAuth2 grants,
    /// service principal lookups, and inheritable permissions).
    /// Using a single unified set ensures Connect-MgGraph authenticates once and the resulting
    /// token is reused from the in-process cache for all downstream Graph operations.
    /// All scopes require admin consent and are included in RequiredClientAppPermissions.
    /// </summary>
    public static readonly string[] RequiredPermissionGrantScopes = new[]
    {
        "Application.ReadWrite.All",
        "DelegatedPermissionGrant.ReadWrite.All",
        "AgentIdentityBlueprint.UpdateAuthProperties.All"
    };

    /// <summary>
    /// Environment variable name for bearer token used in local development.
    /// This token is stored in .env files (Python/Node.js) or launchSettings.json (.NET)
    /// for testing purposes only. It should NOT be deployed to production Azure environments.
    /// </summary>
    public const string BearerTokenEnvironmentVariable = "BEARER_TOKEN";

    /// <summary>
    /// AADSTS53003: Access blocked by Conditional Access Policy.
    /// MSAL throws MsalServiceException with ErrorCode "access_denied" and this code in the Message.
    /// Device code flow may succeed depending on your tenant's Conditional Access Policy configuration.
    /// Reference: https://learn.microsoft.com/en-us/entra/identity-platform/reference-error-codes
    /// </summary>
    public const string ConditionalAccessPolicyBlockedError = "AADSTS53003";

    /// <summary>
    /// AADSTS53000: Device does not comply with device compliance policy (a subset of CAP).
    /// Treated identically to AADSTS53003 for fallback purposes.
    /// Device code flow may succeed depending on your tenant's Conditional Access Policy configuration.
    /// </summary>
    public const string DeviceCompliancePolicyBlockedError = "AADSTS53000";
}
