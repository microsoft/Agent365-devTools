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
    /// Well-known display name for the Agent 365 CLI client app registration in the tenant.
    /// Used to resolve the clientAppId automatically when --agent-name is provided without a config file.
    /// Tenants must register an Entra app with this exact display name and grant it the required permissions.
    /// </summary>
    public const string WellKnownClientAppDisplayName = "Agent 365 CLI";

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
    /// OAuth2 v2 scope used to acquire a fresh Graph token via az CLI's scope-based
    /// acquisition path. Requesting .default forces az CLI to bypass its resource-keyed
    /// token cache and obtain a new access token from AAD that reflects the user's
    /// current role assignments and consented permissions.
    /// </summary>
    public const string MicrosoftGraphDefaultScope = "https://graph.microsoft.com/.default";

    /// <summary>
    /// Well-known application ID for the Microsoft Azure CLI.
    /// All GraphApiService calls use az CLI's delegated token; scopes that need to appear
    /// in that token's <c>scp</c> claim must be consented on this application, not only on
    /// the custom client app registered by the user.
    /// </summary>
    public const string AzureCliAppId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

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
    /// Delegated scope required to create the Agent Blueprint service principal
    /// (Agent Blueprint Principal) via POST /v1.0/servicePrincipals.
    /// Per the Agent ID team (Kyle Marsh), AgentIdentityBlueprintPrincipal.Create is the correct
    /// scope — AgentIdentityBlueprintPrincipal.ReadWrite.All alone returns 403.
    /// </summary>
    public const string AgentIdentityBlueprintPrincipalCreateScope = "AgentIdentityBlueprintPrincipal.Create";

    /// <summary>
    /// Delegated scope required to delete an Agent Blueprint.
    /// Per the Agent ID permissions reference, this is the correct scope for Delete operations.
    /// </summary>
    public const string AgentIdentityBlueprintDeleteRestoreAllScope = "AgentIdentityBlueprint.DeleteRestore.All";

    /// <summary>
    /// Delegated scope required to delete an Agent Identity (service principal).
    /// Per the Agent ID permissions reference, DELETE /beta/servicePrincipals/{id} for agent identities
    /// requires this scope — NOT AgentIdentityBlueprint.DeleteRestore.All, which is blueprint-only.
    /// </summary>
    public const string AgentIdentityDeleteRestoreAllScope = "AgentIdentity.DeleteRestore.All";

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
    /// Delegated scope for full read/write access to Entra ID applications.
    /// No longer in RequiredClientAppPermissions — replaced by AgentIdentityBlueprintPrincipal.Create
    /// for blueprint SP creation per Agent ID team guidance.
    /// Retained as a named constant for reference and potential future use.
    /// </summary>
    public const string ApplicationReadWriteAllScope = "Application.ReadWrite.All";

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
        "AgentIdentityBlueprintPrincipal.Create",  // Required for POST /v1.0/servicePrincipals (blueprint SP creation) — per Agent ID team (Kyle Marsh)
        "AgentIdentityBlueprint.ReadWrite.All",
        "AgentIdentityBlueprint.UpdateAuthProperties.All",
        "AgentIdentityBlueprint.AddRemoveCreds.All",  // Required for passwordCredentials and FICs during setup and cleanup
        "DelegatedPermissionGrant.ReadWrite.All",
        "Directory.Read.All",
        "AgentInstance.ReadWrite.All",  // Required for POST /beta/agentRegistry/agentInstances (non-DW blueprint setup)
        // AgentIdentity.ReadWrite.All removed — no code requests it as a token scope.
        // Create uses blueprint client credentials (AgentIdentity.CreateAsManager automatic).
        // Delete uses AgentIdentity.DeleteRestore.All. Read uses AgentIdentity.Read.All.
        // AgentIdentity.Create.All is app-only (not a delegated scope) — cannot be granted on a client app.
        // Agent identity creation uses blueprint client credentials (app-only) which get AgentIdentity.CreateAsManager automatically.
        "AgentIdentityBlueprint.DeleteRestore.All",  // Required for 'a365 cleanup' to delete the Agent Blueprint application
        "AgentIdentity.DeleteRestore.All",  // Required for 'a365 cleanup' to delete the Agent Identity service principal
        "User.Read",  // Required for /me endpoint to resolve the signed-in user's object ID for blueprint owner/sponsor assignment
        // Note: RoleManagementReadDirectoryScope is excluded because Directory.Read.All covers the needed read operations.
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
        "DelegatedPermissionGrant.ReadWrite.All",
        "AgentIdentityBlueprint.UpdateAuthProperties.All"
    };

    /// <summary>
    /// Scopes requested when acquiring an interactive Graph token for blueprint creation
    /// and inheritable permissions configuration (used by InteractiveGraphAuthService).
    /// Expressed as fully-qualified URIs as required by the Graph SDK credential constructor.
    /// </summary>
    public static readonly string[] BlueprintInteractiveAuthScopes = new[]
    {
        $"{MicrosoftGraphResourceUri}/AgentIdentityBlueprintPrincipal.Create",
        $"{MicrosoftGraphResourceUri}/AgentIdentityBlueprint.ReadWrite.All",
        $"{MicrosoftGraphResourceUri}/AgentIdentityBlueprint.UpdateAuthProperties.All",
        $"{MicrosoftGraphResourceUri}/User.Read"
    };

    /// <summary>
    /// Delegated scope for creating an Agent Identity (service principal) from a blueprint.
    /// Used by POST /beta/servicePrincipals/Microsoft.Graph.AgentIdentity with agentIdentityBlueprintId.
    /// Requires Agent ID Administrator, Agent ID Developer, or Global Administrator role.
    /// This path does NOT require a blueprint client secret.
    /// AgentIdentity.Create.All is required — AgentIdentity.ReadWrite.All alone is NOT sufficient
    /// (confirmed via Graph Explorer: the endpoint returns 403 without Create.All in the scp claim).
    /// </summary>
    public const string AgentIdentityCreateAllScope = "AgentIdentity.Create.All";

    /// <summary>
    /// Delegated scope for creating and managing agent instances in the Microsoft Agent Registry.
    /// Required for POST /beta/agentRegistry/agentInstances.
    /// Requires the "Agent Registry Administrator" Entra role.
    /// </summary>
    public const string AgentInstanceReadWriteAllScope = "AgentInstance.ReadWrite.All";

    /// <summary>
    /// Environment variable name for bearer token used in local development.
    /// This token is stored in .env files (Python/Node.js) or launchSettings.json (.NET)
    /// for testing purposes only. It should NOT be deployed to production Azure environments.
    /// </summary>
    public const string BearerTokenEnvironmentVariable = "BEARER_TOKEN";

    /// <summary>
    /// Application ID of the AgentX service (private preview Agent Registration API V2).
    /// </summary>
    public const string AgentXAppId = "59eca866-2f46-40b8-96ff-63f663121ef9";

    /// <summary>
    /// Resource URI for the AgentX service (private preview Agent Registration API V2).
    /// Used with 'az account get-access-token --resource' to acquire a bearer token.
    /// </summary>
    public const string AgentXResource = $"api://{AgentXAppId}";

    /// <summary>
    /// Base URL for the AgentX service (private preview Agent Registration API V2 endpoint).
    /// </summary>
    public const string AgentXBaseUrl = "https://agentxppe.microsoft.com";

    /// <summary>
    /// Delegated scope for the AgentX Agent Registration API V2.
    /// This scope must be consented on the custom client app to use the V2 registration endpoint.
    /// </summary>
    public const string AgentXAccessScope = $"api://{AgentXAppId}/AgentX.Access";

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

    /// <summary>
    /// Windows Account Manager (WAM) error prefix for authentication failures.
    /// WAM errors (e.g. 0xcaa90019) surface when Conditional Access Policy or device compliance
    /// policies block the WAM broker flow. Device code flow bypasses the WAM broker and may succeed.
    /// </summary>
    public const string WamErrorPrefix = "0xcaa";

    /// <summary>
    /// WAM error code for "Need admin approval" (admin consent not granted).
    /// This error means the client app's oauth2PermissionGrant is per-user (Principal) only,
    /// not tenant-wide (AllPrincipals). Do NOT fall back to device code for this error —
    /// device code will show the same browser page and hang if the user returns without consenting.
    /// Instead, print the admin consent URL and exit cleanly.
    /// </summary>
    public const string WamConsentRequiredError = "0xcaa90019";
}
