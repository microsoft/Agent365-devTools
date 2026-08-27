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
    /// Display name used to discover a tenant-owned custom client app when the first-party
    /// Agent 365 CLI service principal is unavailable.
    /// </summary>
    public const string WellKnownClientAppDisplayName = "Agent 365 CLI";

    /// <summary>
    /// Well-known first-party Agent 365 CLI application ID used by setup/bootstrap by default.
    /// </summary>
    public const string WellKnownClientAppId = "f54280f4-395e-4ea8-9e48-bf2d4952aa14";

    private static readonly Guid WellKnownClientAppGuid = Guid.Parse(WellKnownClientAppId);

    /// <summary>
    /// Returns whether <paramref name="clientAppId"/> identifies the first-party Agent 365 CLI app.
    /// </summary>
    public static bool IsWellKnownFirstPartyClientApp(string? clientAppId) =>
        Guid.TryParse(clientAppId, out var parsedClientAppId) &&
        parsedClientAppId == WellKnownClientAppGuid;

    /// <summary>
    /// Application name for cache directory
    /// </summary>
    public const string ApplicationName = "Microsoft.Agents.A365.DevTools.Cli";

    /// <summary>
    /// MSAL persistent token cache file name.
    /// Used by MsalBrowserCredential (WAM/browser auth) and referenced by
    /// AuthenticationService when clearing stale cross-tenant cached tokens.
    /// </summary>
    public const string MsalCacheFileName = "msal-token-cache";

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
    /// Agent 365 manager application ID.
    /// Set as the managerApplications value on blueprint creation to enable manageability for A365.
    /// </summary>
    public const string A365ManagerAppId = "e8be65d6-d430-4289-a665-51bf2a194bda";

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
    /// Delegated scope for broad directory read access. Retained for callers that explicitly need
    /// a tenant-wide read; production code prefers the narrower <c>Application.Read.All</c> for
    /// service-principal lookups by appId. Role detection no longer requires this scope — directory
    /// role membership is read from the <c>wids</c> optional claim on the access token.
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
    /// Delegated scope required to delete an Agent Identity (service principal).
    /// Per the Agent ID permissions reference, DELETE /beta/servicePrincipals/{id} for agent identities
    /// requires this scope — blueprint deletion is covered by the AgentIdentityBlueprint.ReadWrite.All
    /// umbrella so no separate blueprint-delete scope is defined here.
    /// </summary>
    public const string AgentIdentityDeleteRestoreAllScope = "AgentIdentity.DeleteRestore.All";

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
    /// Not required for blueprint SP creation — the correct endpoint is
    /// POST /v1.0/serviceprincipals/graph.agentIdentityBlueprintPrincipal which accepts
    /// AgentIdentityBlueprintPrincipal.Create alone (confirmed by Kyle Marsh, Agent ID team).
    /// Retained as a named constant for reference and potential future use.
    /// </summary>
    public const string ApplicationReadWriteAllScope = "Application.ReadWrite.All";

    /// <summary>
    /// Delegated scope for reading application and service principal details.
    /// Narrower replacement for Directory.Read.All — covers SP lookups by appId
    /// (GET /v1.0/servicePrincipals?$filter=appId eq '...') without granting
    /// broad directory read access.
    /// </summary>
    public const string ApplicationReadAllScope = "Application.Read.All";

    /// <summary>
    /// Delegated scope for creating and managing agent user accounts.
    /// Agent-specific replacement for User.ReadWrite.All — covers agent user creation,
    /// usageLocation update, and license assignment without granting broad write
    /// access to all users in the tenant.
    /// </summary>
    public const string AgentIdUserReadWriteAllScope = "AgentIdUser.ReadWrite.All";

    /// <summary>
    /// Delegated scope required to create and delete agent registrations.
    /// </summary>
    public const string AgentRegistrationReadWriteAllScope = "AgentRegistration.ReadWrite.All";

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
        "AgentIdentityBlueprint.ReadWrite.All",    // Umbrella — covers blueprint creation, UpdateAuthProperties, AddRemoveCreds, DeleteRestore sub-scopes
        "AgentIdentityBlueprintPrincipal.Create",  // Required for POST /v1.0/serviceprincipals/graph.agentIdentityBlueprintPrincipal — separate from ReadWrite.All (different resource)
        AgentRegistrationReadWriteAllScope,  // Required for POST/DELETE /beta/copilot/agentRegistrations (agent registration)
        "AgentIdentity.Read.All",       // Required for GET /beta/servicePrincipals/microsoft.graph.agentIdentity?$filter=agentIdentityBlueprintId (idempotency check)
        "AgentIdentity.DeleteRestore.All",  // Required for 'a365 cleanup' to delete the Agent Identity service principal
        "Application.Read.All",         // Narrower replacement for Directory.Read.All — covers SP lookups by appId
        "User.Read",  // Required for /me endpoint to resolve the signed-in user's object ID for blueprint owner/sponsor assignment
    };

    /// <summary>
    /// Scopes for blueprint setup operations — same as <see cref="RequiredClientAppPermissions"/>
    /// but without AgentRegistration.ReadWrite.All. Blueprint token acquisition must not request
    /// the registration scope: apps that haven't yet been updated with that permission would get
    /// an MSAL consent error with no actionable guidance.
    /// </summary>
    public static readonly string[] BlueprintOperationScopes = RequiredClientAppPermissions
        .Where(s => s != AgentRegistrationReadWriteAllScope)
        .ToArray();

    /// <summary>
    /// Explicit delegated scopes passed to EnsureGraphHeadersAsync for permission-grant operations.
    /// Intentionally empty: the operations that previously needed explicit scopes here
    /// (DelegatedPermissionGrant.ReadWrite.All for oauth2 grant CRUD,
    /// AgentIdentityBlueprint.UpdateAuthProperties.All for inheritable permissions) are now
    /// covered by the AgentIdentityBlueprint.ReadWrite.All umbrella in RequiredClientAppPermissions.
    /// An empty array causes EnsureGraphHeadersAsync to route through the standard token path
    /// (GetGraphAccessTokenAsync / AuthenticationService), which already carries all required scopes.
    /// Validated end-to-end across all 4 setup variants (PR #409).
    /// </summary>
    public static readonly string[] RequiredPermissionGrantScopes = [];

    /// <summary>
    /// Additional scopes for S2S app role assignment calls in BatchPermissionsOrchestrator.
    /// Intentionally empty: AppRoleAssignment.ReadWrite.All was removed from the required
    /// permission set. Global Admins can assign app roles without that scope (admin bypass);
    /// developers receive a 403 and fall back to PowerShell instructions as intended.
    /// Validated across admin and developer paths (PR #409).
    /// </summary>
    public static readonly string[] RequiredS2SGrantScopes = [];

    /// <summary>
    /// Entra roles that can perform S2S app role assignments programmatically.
    /// Verified: Agent ID Administrator cannot create S2S app role assignments (403). Application Administrator and Global Administrator confirmed working.
    /// </summary>
    public const string S2SGrantRequiredRoles = "Application Administrator or Global Administrator";

    /// <summary>
    /// Roles required to create tenant-wide AllPrincipals oauth2PermissionGrants.
    /// The CLI's automated path detects Global Administrator via the wids token claim only.
    /// </summary>
    public const string DelegatedGrantRequiredRoles = "Global Administrator";

    /// <summary>
    /// Roles required to configure inheritable permissions on an agent blueprint or agent identity
    /// service principal (PATCH /v1.0/servicePrincipals/{id}/permissionGrantPolicies).
    /// Agent ID Administrator role covers the inheritable permissions endpoint.
    /// </summary>
    public const string InheritablePermissionsRequiredRoles = "Agent ID Administrator or Global Administrator";

    /// <summary>
    /// Scopes requested when acquiring an interactive Graph token for blueprint creation
    /// and inheritable permissions configuration (used by InteractiveGraphAuthService).
    /// Expressed as fully-qualified URIs as required by the Graph SDK credential constructor.
    /// </summary>
    public static readonly string[] BlueprintInteractiveAuthScopes = new[]
    {
        $"{MicrosoftGraphResourceUri}/AgentIdentityBlueprint.ReadWrite.All",
        $"{MicrosoftGraphResourceUri}/AgentIdentityBlueprintPrincipal.Create",
        $"{MicrosoftGraphResourceUri}/User.Read"
    };

    /// <summary>
    /// Delegated scope for reading Agent Identities as another client (portal, CLI, management tool).
    /// Required for GET /beta/servicePrincipals/microsoft.graph.agentIdentity?$filter=agentIdentityBlueprintId eq '...'
    /// Per the Agent ID permissions reference (other-client read row): AgentIdentity.Read.All.
    /// </summary>
    public const string AgentIdentityReadAllScope = "AgentIdentity.Read.All";

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
    /// Returns the per-server bearer token env var name for a given MCP server unique name.
    /// e.g. "mcp_WordServer" -> "BEARER_TOKEN_MCP_WORDSERVER"
    /// Takes precedence over <see cref="BearerTokenEnvironmentVariable"/> for V2 per-audience tokens.
    /// </summary>
    public static string GetPerServerBearerTokenEnvVar(string serverUniqueName) =>
        $"BEARER_TOKEN_{serverUniqueName.ToUpperInvariant().Replace('-', '_')}";

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

    /// <summary>
    /// WAM error string indicating that the WAM broker rejected one or more requested scopes.
    /// Appears in the WAM error message as "declined scopes are present".
    ///
    /// This is a distinct failure mode from <see cref="WamConsentRequiredError"/>:
    /// - WamConsentRequiredError (0xcaa90019): consent has NOT been granted — admin must grant it.
    /// - WamDeclinedScopesError: the WAM broker rejects the request with ApiContractViolation,
    ///   reporting that declined scopes are present. Observed with Exchange-specific delegated Graph
    ///   scopes (e.g. MailboxSettings.ReadWrite, ExchangeMessageTrace.Read.All). The scopes
    ///   themselves are valid and grantable; the failure is known broker behavior rather than
    ///   a consent or scope-validity problem. Device code flow does not go through the WAM broker
    ///   and succeeds for these scopes. The precise broker-side trigger is not publicly documented;
    ///   see https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/issues/5232.
    ///
    /// The WAM internal error code for this condition is 0x236496A2 (593794722 decimal), which
    /// does NOT match <see cref="WamErrorPrefix"/> ("0xcaa"), so a separate check is required.
    /// </summary>
    public const string WamDeclinedScopesError = "declined scopes are present";

    /// <summary>
    /// WAM error classification that accompanies <see cref="WamDeclinedScopesError"/>.
    /// WAM surfaces this as "Error Message: ApiContractViolation" when scope validation fails.
    /// Used together with <see cref="WamDeclinedScopesError"/> to distinguish this specific
    /// fallback-eligible failure from other ApiContractViolation variants.
    /// </summary>
    public const string WamApiContractViolation = "ApiContractViolation";
}
