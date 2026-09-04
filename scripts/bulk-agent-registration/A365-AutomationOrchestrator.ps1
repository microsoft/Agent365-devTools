<#
.SYNOPSIS
    Provisions a complete Microsoft Agent 365 agent: blueprint, agent identity, registry
    registration and optionally an agent user, in one pass, using only Microsoft Graph.

.DESCRIPTION
    Orchestrates the scripts that make up the A365 provisioning pipeline:

        1. New-A365AgentBlueprint.ps1      the blueprint application + its principal
        2. New-A365AgentIdentity.ps1       an agent identity created from that blueprint
        3. New-A365AgentRegistration.ps1   the agent's entry in the Agent 365 registry
        4. New-A365AgentUser.ps1           the agent user bound to that identity

    Nothing is reimplemented here - the step scripts remain the single source of truth and
    are invoked as-is, so their idempotency, retry and permission pre-flight behaviour all
    still apply. Re-running after a partial failure is safe: existing objects are reused
    rather than duplicated.

    SAY WHAT YOU WANT CREATED

    Each scenario has its own switch. Nothing runs unless you ask for it:

        -NewBlueprint          the blueprint application and its principal
        -NewAgentIdentity      an agent identity under a blueprint
        -NewAgentRegistration  the registry entry for an agent identity
        -NewAgentUser          the agent user (a real, licensable directory object)

    Combine them freely. The whole pipeline is:

        -NewBlueprint -NewAgentIdentity -NewAgentRegistration

    SAY WHERE TO START WHEN YOU ARE NOT CREATING THE WHOLE CHAIN

    A create needs a parent. Normally the parent comes from an earlier phase of the same run,
    and nothing extra is needed. When the parent already exists, name it:

        -UseExistingBlueprint    <appId>     build the identity on this blueprint
        -UseExistingAgentIdentity         <objectId>  create the agent user under this identity
        -UseExistingAgentIdentity <objectId>     create the agent user under, and/or
                                                 register, this identity

    Each is an attribute of the phase that needs it, exactly like -BlueprintDisplayName or
    -AgentIdentitySponsor. Each is refused alongside any action in the same run that would
    produce that same parent, so a run can never both create and reuse the same object.

    Every phase therefore has exactly one stated source. Nothing is inferred from which ids
    happen to be present.

    CHANGING SOMETHING THAT ALREADY EXISTS

    Each object has an update parameter that TAKES THE ID of the object to change, so an
    update names its own target:

        -UpdateBlueprint         <blueprint appId or objectId>
        -UpdateAgentIdentity     <agent identity objectId>
        -UpdateAgentUser         <agent user objectId or UPN>
        -UpdateAgentRegistration <registration id, starts with T_>

    ONLY THE ATTRIBUTES YOU SPECIFY ARE WRITTEN. A parameter you leave off the command line
    is not forwarded at all, so it cannot overwrite what is already stored - including
    parameters that have a default value on the create path, such as -AgentUserUsageLocation.
    That is the whole contract of update mode, and it is enforced by forwarding on
    "was this parameter bound", never on "does this variable have a value".

    -NewX and -UpdateX are mutually exclusive for the same object: a single run either
    creates it or changes it. Update parameters can be combined across objects, so one run can
    update the blueprint, the identity and the registration together.

    An update is also a source for the phases below it. -UpdateBlueprint <id> -NewAgentIdentity
    builds the new identity on the blueprint phase 1 just acted on, with no second id needed.

    To change a single object, there is also a dedicated script per object, which takes the
    same attribute names without the scenario prefix:

        Update-A365Blueprint.ps1          -BlueprintId
        Update-A365AgentIdentity.ps1      -AgentIdentityId
        Update-A365AgentUser.ps1          -AgentUserId
        Update-A365AgentRegistration.ps1  -RegistrationId

    Those scripts and this one run exactly the same code, so pick whichever reads better: this
    orchestrator when a run touches several objects, the dedicated script when it touches one.

    A few merge semantics are worth knowing before you rely on them. Graph replaces a collection
    it is sent, but the step scripts do not always send one, so the behaviour you actually get is:

        custom security attributes  merge per attribute - untouched attributes survive, but a
                                    multi-valued attribute that is written is replaced wholesale
        tags (agent identity)       merge - the script reads the current set and PATCHes the
                                    union, so re-running to add one tag does not drop the others
        owners (agent identity)     additive - existing owners are skipped, new ones are POSTed
                                    to owners/$ref, so nothing already assigned is removed
        ownerIds (registration)     replaces the whole collection - list every owner to keep

    DELETING SOMETHING

    Each object also has a removal parameter that takes the id of the object to delete:

        -RemoveAgentRegistration <T_id>      the admin-center inventory entry
        -RemoveAgentUser         <objectId>  the agent user (mailbox and UPN)
        -RemoveAgentIdentity     <objectId>  the agent identity
        -RemoveBlueprint         <appId>     the blueprint, shared by every identity under it

    Removal runs AFTER any create or update phases in the same run, and in REVERSE dependency
    order - registration, then agent user, then agent identity, then blueprint. Deleting a
    parent first would orphan its children; the agent user especially, because it is discovered
    through its identity.

    Deletion is a SOFT delete: objects move to deleted items and are restorable for 30 days.
    -RemovePermanent purges them, which is irreversible. -RemoveInspectOnly reports the full
    plan and deletes nothing, and is the right first command every time.

    -RemoveBlueprint refuses while any agent identity still exists under the blueprint, because
    the blueprint is shared; -RemoveForce cascades instead.

    Removing an object is mutually exclusive with creating or updating that same object.

    WHAT DEPENDS ON WHAT

        -NewAgentIdentity      needs -NewBlueprint, -UpdateBlueprint or -UseExistingBlueprint
        -NewAgentRegistration  needs -NewAgentIdentity, -UpdateAgentIdentity or -UseExistingAgentIdentity
        -NewAgentUser          needs -NewAgentIdentity, -UpdateAgentIdentity or -UseExistingAgentIdentity

    Agent user and registration are siblings, not a chain: both need only the agent
    identity, so an agent user failure does not stop the registration being created.

    Parameters belonging to a scenario you did not select are reported as ignored rather
    than silently doing nothing.

    ONE PARAMETER, ONE OBJECT

    -TenantId is the only setting shared by every scenario. Everything else names the object
    it configures, so no parameter ever fans out to more than one phase and nothing is
    derived from a shared base name. Where two phases need the same value - an owner, say -
    give it to each of them.

.PARAMETER NewBlueprint
    Create the blueprint application and its blueprint principal (phase 1).

    Every setting for this phase is prefixed -Blueprint*: -BlueprintDisplayName (required),
    -BlueprintSponsor (required), -BlueprintDescription, -BlueprintOwner,
    -BlueprintRequireOwnerAssignment, -BlueprintRequiredPermission,
    -BlueprintGrantAdminConsent, -BlueprintSkipInheritablePermissions,
    -BlueprintNewClientSecret, -BlueprintManagedIdentityPrincipalId and
    -BlueprintParameter.

.PARAMETER NewAgentIdentity
    Create an agent identity (phase 2). Needs -NewBlueprint, -UpdateBlueprint or
    -UseExistingBlueprint.

    Every setting for this phase is prefixed -AgentIdentity*: -AgentIdentityDisplayName
    (required), -AgentIdentitySponsor (required), -AgentIdentityOwner,
    -AgentIdentityRequireOwnerAssignment, -AgentIdentityTag,
    -AgentIdentityCustomSecurityAttribute, -AgentIdentityRequiredPermission,
    -AgentIdentityGrantAdminConsent and -AgentIdentityParameter.


.PARAMETER NewAgentRegistration
    Register the agent in the Agent 365 registry (phase 4). Needs -NewAgentIdentity,
    -UpdateAgentIdentity or -UseExistingAgentIdentity.

    Every setting for this phase is prefixed -AgentRegistration*:
    -AgentRegistrationDisplayName, -AgentRegistrationDescription, -AgentRegistrationOwner,
    -AgentRegistrationAuth, -AgentRegistrationOwnerId and -AgentRegistrationParameter.


.PARAMETER NewAgentUser
    Create the agent user bound to the agent identity (phase 3). Needs -NewAgentIdentity,
    -UpdateAgentIdentity or -UseExistingAgentIdentity.

    This creates a real directory object that can hold a licence and a mailbox, so it is
    never implied by any other switch.

    Every setting for this phase is prefixed -AgentUser*: -AgentUserPrincipalName
    (required), -AgentUserDisplayName, -AgentUserMailNickname, -AgentUserManagerUserId,
    -AgentUserManagerUpn, -AgentUserUsageLocation, -AgentUserAssignLicense,
    -AgentUserLicenseSkuId, -AgentUserLicenseSkuPartNumber and -AgentUserParameter.

    There is no -AgentUserSponsor, -AgentUserOwner or -AgentUserDescription because
    New-A365AgentUser.ps1 accepts none of them; an agent user takes its sponsorship from the
    identity it is created under.

    Requires AgentIdUser.ReadWrite.All. User.ReadWrite.All does NOT authorize it.


.PARAMETER UseExistingBlueprint
    AppId of a blueprint that already exists, to create the agent identity under. Only needed
    when the blueprint is not part of this run: with -NewBlueprint or -UpdateBlueprint the id
    threads through automatically, and passing it as well is refused.

.PARAMETER UseExistingAgentIdentity
    Object id (the service principal id) of an agent identity that already exists, to create
    the agent user under and/or to register. Only needed when the identity is not part of this
    run.

    One parameter serves both phases, so a single run can create the agent user and the
    registration for the same existing identity without naming it twice.

.PARAMETER AgentRegistrationIdentityId
    Re-points an EXISTING registration at a different agent identity, with
    -UpdateAgentRegistration. That is a value being changed, not a parent being reused, which
    is why it keeps its own name.

    To register an agent identity that already exists, use -UseExistingAgentIdentity instead;
    one parameter then serves both the agent user and the registration phase in the same run.
    On a -NewAgentRegistration run this parameter is still accepted and means the same thing,
    but supplying both with different ids is refused rather than silently resolved.

.PARAMETER UpdateBlueprint
    Update the blueprint with this id (phase 1). Accepts either the application (client) id
    or the object id.

    ONLY the attributes you actually pass on the command line are written. A -Blueprint*
    parameter you leave out is not sent at all, so it cannot overwrite what is already
    there. Pass -BlueprintDisplayName, -BlueprintDescription, -BlueprintSponsor,
    -BlueprintOwner, -BlueprintRequiredPermission and so on to change just those.

    The blueprint this names is also the one a -NewAgentIdentity in the same run builds on.

    Cannot be combined with -NewBlueprint: an object is either created or updated in a
    single run.

.PARAMETER UpdateAgentIdentity
    Update the agent identity with this object id (phase 2).

    Only the -AgentIdentity* attributes you pass are written. Typical uses are renaming
    (-AgentIdentityDisplayName), retagging (-AgentIdentityTag) and changing custom security
    attributes (-AgentIdentityCustomSecurityAttribute).

    Note the differing merge semantics, which come from Graph rather than from this script:
    custom security attributes merge per attribute, whereas tags replace the whole
    collection - so -AgentIdentityTag must list every tag you want to keep.

    The identity this names is also the one a -NewAgentUser or -NewAgentRegistration in the
    same run acts on.

    Cannot be combined with -NewAgentIdentity.

.PARAMETER UpdateAgentUser
    Update the agent user with this object id or user principal name (phase 3).

    Only the -AgentUser* attributes you pass are written. -AgentUserUsageLocation defaults
    to US on create, so it is deliberately NOT sent on an update unless you pass it
    explicitly; otherwise every update would silently rewrite the account's usage location.

    -AgentUserPrincipalName is rejected here: the UPN cannot be changed after creation, and
    the account is identified by -UpdateAgentUser itself.

    Like -NewAgentUser this requires app-only authentication.

    Cannot be combined with -NewAgentUser.

.PARAMETER UpdateAgentRegistration
    Update the agent registration with this id (phase 4). This is the id the SERVICE returned
    when the agent was registered - it starts with 'T_'. The service assigns that id itself
    and ignores any id supplied at creation time, so a client-generated GUID never resolves.
    Aliased as -UpdateRegistration.

    Only the -AgentRegistration* attributes you pass are written. -AgentRegistrationOwner
    and -AgentRegistrationOwnerId replace the whole ownerIds collection, so list every owner
    you want to keep; leaving both out preserves the existing owners rather than falling
    back to the signed-in user.

    Pass -AgentRegistrationIdentityId to re-point the registration at a different agent
    identity.

    The registration API reports a 200 on writes that do not stick, so the step script reads
    the object back and warns per property that failed to persist.

    Cannot be combined with -NewAgentRegistration.

.PARAMETER RemoveBlueprint
    DELETE the blueprint with this id (appId or objectId). The most destructive operation here:
    a blueprint is shared by every agent identity built from it.

    Refuses if any agent identity still exists under the blueprint, listing them and printing
    the command to remove them first. Add -RemoveForce to cascade instead, which deletes those
    identities and their agent users before the blueprint.

.PARAMETER RemoveAgentIdentity
    DELETE the agent identity with this object id, and nothing else. An agent user attached to
    it is reported but NOT deleted - remove it first with -RemoveAgentUser, in the same run.

.PARAMETER RemoveAgentUser
    DELETE the agent user with this object id. This destroys the agent's mailbox and releases
    its user principal name. The agent identity is left alone.

.PARAMETER RemoveAgentRegistration
    DELETE the registration with this id - the 'T_' prefixed id the service returned. This is
    what removes the agent from the Microsoft 365 admin center inventory.

.PARAMETER RemoveInspectOnly
    Resolve and report everything the removal phases would delete, then exit without deleting
    anything. Run this first: it is the cheapest way to discover that an object has dependents
    you did not know about.

.PARAMETER RemovePermanent
    After the soft delete, also purge from /directory/deletedItems. IRREVERSIBLE. Without it a
    deleted object is restorable for 30 days, and a deleted agent user still appears under
    deleted users in the admin center.


.PARAMETER RemoveForce
    Suppress confirmation prompts, and allow -RemoveBlueprint to cascade to the agent
    identities and agent users built from the blueprint. Required for unattended runs.

.PARAMETER TenantId
    Directory (tenant) ID or verified domain. The only parameter shared by all scenarios.

.PARAMETER BlueprintDisplayName
    Display name for the blueprint. Required with -NewBlueprint.

.PARAMETER AgentIdentityDisplayName
    Display name for the agent identity. Required with -NewAgentIdentity.

.PARAMETER AgentRegistrationDisplayName
    Display name for the registration. Defaults to -AgentIdentityDisplayName when the
    identity is created in the same run; required when registering an existing identity.

    The registration script rewrites a trailing " Identity" to " Agent".

.PARAMETER BlueprintRequiredPermission
    Permissions declared on the blueprint, as an array of hashtables:

        @{ ResourceAppId = '<appId>'; DelegatedScopes = @('..'); AppRoles = @('..') }

    Supplying this REPLACES the blueprint script's defaults rather than adding to them.
    Omit it entirely to keep them.

    Pair it with -BlueprintGrantAdminConsent to consent the permissions as well as declare
    them.

.PARAMETER BlueprintGrantAdminConsent
    Consent the declared permissions on the blueprint principal, and enable inheritance so
    agent identities created from the blueprint receive them.

    This is the switch you almost always want. Its counterpart
    -AgentIdentityGrantAdminConsent means something quite different - see there.

.PARAMETER BlueprintSkipInheritablePermissions
    Declare and consent the permissions but do not make them inheritable. Agent identities
    created from this blueprint will not receive them.

.PARAMETER BlueprintSponsor
    Sponsor(s) for the blueprint. Required with -NewBlueprint. Accepts several: the create
    API takes a sponsors collection, verified against the live API where two sponsors both
    persisted.

    Pass users, Microsoft 365 groups or dynamic groups; static security groups and
    role-assignable groups are rejected by the service.

    Sponsors are set on the create call only. Re-running against an existing blueprint will not
    add sponsors to it.

.PARAMETER AgentIdentitySponsor
    Sponsor(s) for the agent identity. Required with -NewAgentIdentity. Accepts several,
    with the same accepted principal types as -BlueprintSponsor.

.PARAMETER BlueprintOwner
    Owners applied to the blueprint. See -AgentIdentityOwner for the accepted forms and the
    permissions an owner assignment needs.

.PARAMETER AgentIdentityOwner
    Owners applied to the agent identity.

    Accepts object ids, UPNs, mail addresses or display names.

    To make an app an owner you may pass its appId, its application object id, or its service
    principal object id - steps 1 and 2 translate the first two to the service principal, which
    is the only one of the three that Entra ID accepts in an owners/$ref reference.

    Assigning an owner needs permissions wider than the AgentIdentity* roles the rest of the
    pipeline uses, and the target decides which one: the blueprint APPLICATION and the agent
    identity need Application.ReadWrite.All (or Application.ReadWrite.OwnedBy) plus
    Directory.Read.All, while the blueprint PRINCIPAL needs
    AgentIdentityBlueprintPrincipal.ReadWrite.All. Application.ReadWrite.All does not cover the
    principal - verified against the live service. Delegated callers also need an Application
    Administrator or Cloud Application Administrator role. If the caller lacks them the owner
    writes are reported as warnings and the run continues, because the blueprint and identity are
    complete and usable without them.

.PARAMETER AgentRegistrationOwner
    Owners for the registration. Step 4 resolves them to user object ids through Graph and
    records them in the registration's ownerIds/createdBy, which is what lets the
    registration run app-only without a /me call. Owners that are not users are skipped.

.PARAMETER BlueprintRequireOwnerAssignment
    Treat a refused owner assignment on the blueprint as fatal instead of continuing with a
    warning.

.PARAMETER AgentIdentityRequireOwnerAssignment
    Treat a refused owner assignment on the agent identity as fatal instead of continuing
    with a warning.

.PARAMETER BlueprintDescription
    Description applied to the blueprint.

.PARAMETER AgentRegistrationDescription
    Description recorded on the registration.

.PARAMETER AgentRegistrationOwnerId
    Object id recorded as the registration's owner. Optional: when it is omitted the
    registration step resolves the owners from -AgentRegistrationOwner through Graph, and
    falls back to the signed-in user when running delegated.

.PARAMETER AgentRegistrationAuth
    How the registration phase authenticates:
        Same         (default) reuse the credentials given to this script
        Interactive  sign in as a user for the registration phase only

    Both registration permissions ARE published as application roles
    (AgentRegistration.ReadWrite.All, AgentInstance.ReadWrite.All), so 'Same' works for an
    app-only run provided the automation app has been granted them and you pass
    -AgentRegistrationOwner or -AgentRegistrationOwnerId (there is no /me to read ownerIds
    and createdBy from). Use 'Interactive' only when the app lacks those roles.

    To not register at all, simply omit -NewAgentRegistration.

.PARAMETER BlueprintNewClientSecret
    Adds a client secret to the blueprint. Printed once, never written to -OutputJsonPath,
    unless -BlueprintKeyVaultName saves it to Azure Key Vault instead.
    Prefer -BlueprintManagedIdentityPrincipalId for anything that outlives a demo.

.PARAMETER BlueprintKeyVaultName
    Save the blueprint client secret to this Azure Key Vault rather than printing it once.
    Accepts a vault name or the full https://<vault>.vault.azure.net URI. Only meaningful
    with -BlueprintNewClientSecret, which is what creates a secret to save.

    Key Vault is not reachable through Microsoft Graph - a Graph token is rejected there
    with HTTP 401 - so the write uses the Key Vault data plane on a second token minted
    from the same credential. The caller needs the "Key Vault Secrets Officer" DATA-plane
    role; Owner and Contributor do NOT grant it.

.PARAMETER BlueprintKeyVaultSecretName
    Name to store the blueprint secret under. Defaults to the blueprint display name folded
    to the characters Key Vault allows.

.PARAMETER KeyVaultAccessToken
    A bearer token for https://vault.azure.net, needed only when one cannot be derived from
    the Graph credential - with -AccessToken, or -Interactive without a signed-in Azure
    session.
.PARAMETER BlueprintManagedIdentityPrincipalId
    Principal id of a managed identity to federate to the blueprint, so the agent can get
    tokens with no stored secret.

.PARAMETER AgentIdentityTag
    Tags applied to the agent identity.

.PARAMETER AgentIdentityCustomSecurityAttribute
    Custom security attributes assigned to the agent identity. Either a list of
    "AttributeSet:Attribute:Value" strings, which is the easiest form to type:

        -AgentIdentityCustomSecurityAttribute "AgentAttributes:AgentEnvironment:Production",
                                              "AgentAttributes:AgentBusinessUnit:HR",
                                              "AgentAttributes:AgentApprovalStatus:New,In_Review"

    Commas separate the values of a multi-valued attribute, and only the first two colons separate,
    so a value may itself contain colons. Whether a single value means 'New' or @('New') is decided
    from the attribute's own definition. An empty value removes the assignment.

    Or a nested hashtable of attribute set -> attribute name -> value, which is the escape hatch for
    a value that itself contains a comma:

        -AgentIdentityCustomSecurityAttribute @{
            AgentAttributes = @{
                AgentEnvironment    = 'Production'
                AgentBusinessUnit   = 'HR'
                AgentApprovalStatus = @('New','In_Review')
            }
        }

    Several attributes across several sets can be assigned at once, and both forms may be mixed.
    The request is validated against the tenant's attribute definitions before anything is created,
    because set names, attribute names and predefined values are all case-sensitive in Graph and a
    mismatch is otherwise reported as "Custom Security attributes not found on tenant".

    Needs CustomSecAttributeAssignment.ReadWrite.All and CustomSecAttributeDefinition.Read.All,
    which no other directory permission implies. See -AgentIdentitySkipCustomSecurityAttributeValidation.

.PARAMETER AgentIdentitySkipCustomSecurityAttributeValidation
    Assign -AgentIdentityCustomSecurityAttribute without first reading the tenant's attribute
    definitions. Use when the caller may assign attributes but not read their definitions.

.PARAMETER AgentIdentityRequiredPermission
    Permissions granted directly on the agent identity itself, in the same hashtable shape as
    -BlueprintRequiredPermission.

    Rarely needed. Permissions normally reach an agent identity by INHERITANCE from its
    blueprint (-BlueprintRequiredPermission plus -BlueprintGrantAdminConsent). Use this only
    to give one identity something its blueprint does not confer on all of them.

    Has no effect unless -AgentIdentityGrantAdminConsent is also given.

.PARAMETER AgentIdentityGrantAdminConsent
    Consent -AgentIdentityRequiredPermission on the agent identity's own service principal.

    This is NOT the same operation as -BlueprintGrantAdminConsent, which consents the
    BLUEPRINT's declared permissions and turns on inheritance for every identity beneath it.
    The two were a single switch in earlier versions of this script, which meant an
    identity-only run silently forwarded a blueprint-shaped intent to the identity step. They
    are now separate so each does exactly what its name says.

.PARAMETER BlueprintParameter
    Hashtable splatted into New-A365AgentBlueprint.ps1, for parameters this script does not
    surface (-ExposedScopeValue, -IdentifierUri, ...). Applied last, so it wins over the
    dedicated parameters on a collision.

.PARAMETER AgentIdentityParameter
    Hashtable splatted into New-A365AgentIdentity.ps1.

.PARAMETER AgentRegistrationParameter
    Hashtable splatted into New-A365AgentRegistration.ps1 (-Api, -ClientAppId, -Force, ...).

.PARAMETER AgentUserPrincipalName
    UPN for the agent user, e.g. research.assistant@contoso.com. Mandatory when
    -NewAgentUser is used; the step script has no fallback and rejects an invalid UPN.

    The agent user is always created UNDER an agent identity, which the pipeline supplies
    automatically - either the one it just created or -UseExistingAgentIdentity. It is
    forwarded to the step script as its -AgentIdentityId, the service principal OBJECT ID,
    and lands in the create call as identityParentId.

    -NewAgentUser requires app-only authentication: New-A365AgentUser.ps1 is
    client-credentials only and has no -Interactive mode, so the combination is rejected up
    front rather than failing after the earlier phases have run.

.PARAMETER AgentUserDisplayName
    Display name for the agent user. Optional: when omitted, New-A365AgentUser.ps1 defaults
    it to the local part of -AgentUserPrincipalName.

.PARAMETER AgentUserMailNickname
    Mail nickname. Defaults to the local part of the UPN.

.PARAMETER AgentUserManagerUserId
    Object id of the user to set as the agent user's manager. The step script also accepts a
    UPN or mail address here. Mutually exclusive with -AgentUserManagerUpn.

.PARAMETER AgentUserManagerUpn
    UPN or mail address of the agent user's manager, resolved to an object id by the step
    script. Mutually exclusive with -AgentUserManagerUserId.

    An unresolvable manager fails before the agent user is created. A manager that resolves
    but cannot be assigned does NOT fail the step - the step script warns and reports
    ManagerAssigned = false - so this script marks the phase 'Partial' rather than letting a
    half-configured agent user report as a clean success.

.PARAMETER AgentUserUsageLocation
    Two-letter usage location for the agent user, e.g. 'US'. Required before any licence can
    be assigned; the step script defaults it to 'US'.

.PARAMETER AgentUserAssignLicense
    Assigns a licence to the agent user. Needs -AgentUserUsageLocation, and either
    -AgentUserLicenseSkuId or -AgentUserLicenseSkuPartNumber.

.PARAMETER AgentUserLicenseSkuId
    SKU id of the licence to assign to the agent user.

.PARAMETER AgentUserLicenseSkuPartNumber
    SKU part number of the licence to assign, resolved to a SKU id by the step script.

.PARAMETER AgentUserParameter
    Hashtable splatted into New-A365AgentUser.ps1, for parameters this script does not
    surface (-DisabledPlans, -ConfigurePermissions, -MaxRetries, ...). Keys are the STEP
    SCRIPT's parameter names, so they carry no AgentUser prefix.

.PARAMETER ScriptRoot
    Folder holding the step scripts. Defaults to this script's own folder.

.PARAMETER OutputJsonPath
    Writes a full run report as JSON, broken down phase by phase.

    The file contains a "phases" array with one entry per phase (Blueprint, AgentIdentity,
    Registration). Each entry records the script that ran, its status, start/end timestamps and
    duration, the parameters it was called with, every identifier it produced, the step script's
    complete return object, and - for a failed phase - the error message and the Graph status
    code when one could be parsed. Alongside it are "run" (tenant, auth mode, host, invocation),
    "summary" (the identifiers you usually want, plus the token request template) and "warnings".

    Secrets are REDACTED unless -IncludeBlueprintSecretsInOutput is also given. Metadata about a created client
    secret - its keyId, hint, and expiry - is always written, because it identifies the
    credential without disclosing it.

.PARAMETER IncludeBlueprintSecretsInOutput
    Writes the blueprint client secret created by -BlueprintNewClientSecret into
    -OutputJsonPath in
    plaintext. Off by default: a report file is easy to commit, sync or forward by accident, and
    Graph only ever returns this value once.

    The credentials used to AUTHENTICATE this run (-ClientSecret, -CertificatePassword,
    -AccessToken) are redacted even with this switch. They are already known to whoever started
    the run, and they are usually far longer-lived than the secret being reported.

    On Windows the report file is created with an ACL granting only the current user when this
    switch is used. Treat the file as a credential.

.PARAMETER ClientId
    Application (client) ID to authenticate as, or the client app id for -Interactive. The
    stock Microsoft Graph PowerShell app has no consent for the A365 preview scopes, so
    interactive runs generally need your own -ClientId.

.PARAMETER ClientSecret
    Client secret, as a SecureString or a plain string. Also read from
    $env:A365_CLIENT_SECRET, which is preferred because it keeps the value out of shell
    history.

.PARAMETER CertificateThumbprint
    Thumbprint of a certificate in the current user's store.

.PARAMETER Certificate
    An X509Certificate2 to authenticate with.

.PARAMETER CertificatePath
    Path to a .pfx to authenticate with.

.PARAMETER CertificatePassword
    Password for -CertificatePath, as a SecureString or a plain string.

.PARAMETER UseManagedIdentity
    Authenticate with the host's managed identity.

.PARAMETER AccessToken
    A pre-acquired Graph access token, as a SecureString or a plain string.

.PARAMETER Interactive
    Sign in as a user.

.PARAMETER SkipPermissionCheck
    Skips each step's permission pre-flight.

.PARAMETER LogPath
    Write a timestamped, correlation-id-tagged log of this run. A path that names an
    existing directory (or ends in a separator) gets a generated file name inside it;
    anything else is used as the exact file name. Forwarded to every step script this run
    invokes, so one -LogPath produces one log per script, all sharing -LogCorrelationId.
    Omit it to log nothing.

.PARAMETER LogIncludeSecrets
    Allow plain-string client secrets and passwords in the log. SecureString values and bearer
    tokens remain redacted. Off by default; only meaningful with -LogPath.

.PARAMETER LogCorrelationId
    Correlation id written into this run's log and forwarded to every step script's log, so
    every log file produced by one run can be tied together. Generated automatically when
    omitted.

.EXAMPLE
    # The whole pipeline, interactive. Each object is named where it is created.
    .\A365-AutomationOrchestrator.ps1 -TenantId contoso.onmicrosoft.com `
        -NewBlueprint -NewAgentIdentity -NewAgentRegistration `
        -BlueprintDisplayName 'Expense Helper Blueprint' -BlueprintSponsor ana@contoso.com `
        -AgentIdentityDisplayName 'Expense Helper Identity' -AgentIdentitySponsor ana@contoso.com `
        -Interactive -ClientId <your-app-id>

    The registration inherits -AgentIdentityDisplayName, so it needs no name of its own.

.EXAMPLE
    # A blueprint on its own, with a specific permission set declared, consented and made
    # inheritable.
    .\A365-AutomationOrchestrator.ps1 -TenantId contoso.onmicrosoft.com `
        -NewBlueprint -BlueprintDisplayName 'Weather MCP Blueprint' `
        -BlueprintSponsor ana@contoso.com `
        -ClientId <automation-app-id> -ClientSecret $env:A365_CLIENT_SECRET `
        -BlueprintGrantAdminConsent `
        -BlueprintRequiredPermission @(
            @{  # Agent 365 observability
                ResourceAppId   = '9b975845-388f-4429-889e-eab1ef63949c'
                DelegatedScopes = @('Agent365.Observability.OtelWrite')
                AppRoles        = @('Agent365.Observability.OtelWrite')
            },
            @{  # Power Platform API
                ResourceAppId   = '8578e004-a5c6-46e7-913e-12f58912df43'
                DelegatedScopes = @('Connectivity.Connections.Read')
                AppRoles        = @()
            }
        )

    -BlueprintRequiredPermission REPLACES the blueprint script's defaults. Omit it to keep them.

.EXAMPLE
    # Unattended blueprint and identity, interactive only for the registry step.
    $env:A365_CLIENT_SECRET = '<secret>'
    .\A365-AutomationOrchestrator.ps1 -TenantId contoso.onmicrosoft.com `
        -NewBlueprint -NewAgentIdentity -NewAgentRegistration `
        -BlueprintDisplayName 'Expense Helper Blueprint' `
        -BlueprintSponsor ana@contoso.com -BlueprintOwner bob@contoso.com `
        -AgentIdentityDisplayName 'Expense Helper Identity' `
        -AgentIdentitySponsor ana@contoso.com -AgentIdentityOwner bob@contoso.com `
        -AgentRegistrationOwner bob@contoso.com `
        -ClientId <automation-app-id> `
        -BlueprintManagedIdentityPrincipalId <mi-principal-id> -BlueprintGrantAdminConsent `
        -AgentRegistrationAuth Interactive -OutputJsonPath .\expense-helper.json

    An owner is given to each object that should have one. Nothing fans out implicitly.

.EXAMPLE
    # Add a second agent identity to an existing blueprint, and register it.
    .\A365-AutomationOrchestrator.ps1 -TenantId contoso.onmicrosoft.com `
        -NewAgentIdentity -NewAgentRegistration `
        -UseExistingBlueprint <existing-blueprint-appId> `
        -AgentIdentityDisplayName 'Expense Helper EU Identity' `
        -AgentIdentitySponsor ana@contoso.com -Interactive

.EXAMPLE
    # See the plan without writing anything.
    .\A365-AutomationOrchestrator.ps1 -TenantId contoso.onmicrosoft.com `
        -NewBlueprint -NewAgentIdentity -NewAgentRegistration `
        -BlueprintDisplayName 'Expense Helper Blueprint' -BlueprintSponsor ana@contoso.com `
        -AgentIdentityDisplayName 'Expense Helper Identity' -AgentIdentitySponsor ana@contoso.com `
        -Interactive -WhatIf

.EXAMPLE
    # Full run captured to a phase-by-phase JSON report, including the client secret.
    .\A365-AutomationOrchestrator.ps1 -TenantId contoso.onmicrosoft.com `
        -NewBlueprint -NewAgentIdentity -NewAgentRegistration `
        -BlueprintDisplayName 'Expense Helper Blueprint' `
        -BlueprintSponsor ana@contoso.com -BlueprintOwner bob@contoso.com `
        -AgentIdentityDisplayName 'Expense Helper Identity' `
        -AgentIdentitySponsor ana@contoso.com -AgentIdentityOwner bob@contoso.com `
        -ClientId <automation-app-id> `
        -ClientSecret $env:A365_CLIENT_SECRET -BlueprintNewClientSecret -BlueprintGrantAdminConsent `
        -OutputJsonPath .\expense-helper-run.json -IncludeBlueprintSecretsInOutput

    # Then read individual phases back out:
    $report = Get-Content .\expense-helper-run.json -Raw | ConvertFrom-Json
    $report.phases | Format-Table phase, name, status, durationSeconds
    $report.phases.Where{ $_.name -eq 'Blueprint' }.secrets.clientSecret
    $report.summary.identifiers

.EXAMPLE
    # A report is still written when a phase fails, so the objects created before the failure
    # are not lost. Inspect the failing phase:
    $report = Get-Content .\expense-helper-run.json -Raw | ConvertFrom-Json
    $report.phases | Where-Object status -eq 'Failed' |
        Select-Object name, @{n='http';e={$_.error.httpStatus}}, @{n='code';e={$_.error.graphErrorCode}}

.EXAMPLE
    .\A365-AutomationOrchestrator.ps1 -TenantId $tid `
        -NewBlueprint -NewAgentIdentity -NewAgentRegistration -NewAgentUser `
        -ClientId $appId -ClientSecret $env:A365_CLIENT_SECRET `
        -BlueprintDisplayName 'Research Assistant Blueprint' `
        -BlueprintSponsor alice@contoso.com -BlueprintOwner alice@contoso.com `
        -AgentIdentityDisplayName 'Research Assistant Identity' `
        -AgentIdentitySponsor alice@contoso.com -AgentIdentityOwner alice@contoso.com `
        -AgentRegistrationOwner alice@contoso.com `
        -AgentUserPrincipalName research.assistant@contoso.com `
        -AgentUserManagerUpn alice@contoso.com `
        -AgentUserUsageLocation US -AgentUserAssignLicense `
        -AgentUserLicenseSkuPartNumber Microsoft_365_Copilot

    All four scenarios. -NewAgentUser needs app-only auth, because New-A365AgentUser.ps1
    is client-credentials only.

    Note there is no identity parameter here: the agent user is bound to the agent identity
    this run creates, which the pipeline forwards automatically.

.EXAMPLE
    .\A365-AutomationOrchestrator.ps1 -TenantId $tid `
        -NewAgentUser -UseExistingAgentIdentity $identityId `
        -ClientId $appId -ClientSecret $env:A365_CLIENT_SECRET `
        -AgentUserPrincipalName research.assistant@contoso.com

    The agent user on its own, against an agent identity that already exists.
    -UseExistingAgentIdentity is the service principal object id of the agent identity.

.EXAMPLE
    .\A365-AutomationOrchestrator.ps1 -TenantId $tid `
        -ClientId $appId -ClientSecret $env:A365_CLIENT_SECRET `
        -UpdateAgentIdentity $identityId `
        -AgentIdentityCustomSecurityAttribute 'AgentAttributes:AgentApprovalStatus:HR_Approved,IT_Approved'

    Change one custom security attribute on an existing agent identity and nothing else. The
    display name, tags, sponsors and owners are not passed, so they are not written.

.EXAMPLE
    .\A365-AutomationOrchestrator.ps1 -TenantId $tid `
        -ClientId $appId -ClientSecret $env:A365_CLIENT_SECRET `
        -UpdateBlueprint $blueprintAppId `
        -BlueprintDescription 'Weather MCP blueprint - production'

    Rewrite only the blueprint description. -BlueprintDisplayName is left out, so the name is
    untouched.

.EXAMPLE
    .\A365-AutomationOrchestrator.ps1 -TenantId $tid `
        -ClientId $appId -ClientSecret $env:A365_CLIENT_SECRET `
        -UpdateBlueprint    $blueprintAppId -BlueprintDescription 'Q3 refresh' `
        -UpdateAgentIdentity $identityId  -AgentIdentityTag 'env:prod','team:hr' `
        -UpdateAgentRegistration $registrationId -AgentRegistrationDisplayName 'HR Agent'

    Several updates in one run. Each phase targets its own object and writes only the
    attributes named for it. Note that -AgentIdentityTag replaces the whole tag collection,
    so it must list every tag to keep.

.EXAMPLE
    .\A365-AutomationOrchestrator.ps1 -TenantId $tid -Interactive `
        -RemoveAgentRegistration $regId -RemoveAgentIdentity $identityId `
        -RemoveInspectOnly

    Report everything those two removals would delete, and delete nothing. Always the first
    command to run before a removal.

.EXAMPLE
    .\A365-AutomationOrchestrator.ps1 -TenantId $tid -Interactive `
        -RemoveAgentRegistration $regId -RemoveAgentUser $agentUserId `
        -RemoveAgentIdentity $identityId -RemoveForce

    Retire one agent completely: its registry entry, its agent user and its agent identity.
    Name each object explicitly - one -Remove* switch per object, each handled by its own script.
    The objects are soft-deleted and restorable for 30 days; add -RemovePermanent to purge.

.EXAMPLE
    .\A365-AutomationOrchestrator.ps1 -TenantId $tid -Interactive `
        -RemoveBlueprint $blueprintAppId -RemoveForce

    Delete a blueprint together with every agent identity and agent user built from it.
    Without -RemoveForce this is refused while any identity still exists.

.NOTES
    MIGRATING FROM 2.0.0 AND EARLIER

    The -UseExisting* parameters were removed in 3.0.0 and the -Create* switches were renamed
    to -New*. As of 3.1.0 the -Create* spellings no longer bind at all. -UseExisting* meant two
    different things depending on where it appeared, so it maps to more than one replacement:

        old                          new
        -CreateBlueprint             -NewBlueprint
        -CreateAgentIdentity         -NewAgentIdentity
        -CreateAgentUser             -NewAgentUser
        -CreateAgentRegistration     -NewAgentRegistration

        -UpdateBlueprint -UseExistingBlueprint <id>
                                     -UpdateBlueprint <id>
        -UpdateAgentIdentity -UseExistingAgentIdentity <id>
                                     -UpdateAgentIdentity <id>
        -UpdateAgentUser -UseExistingAgentUser <id>
                                     -UpdateAgentUser <id>
        -UpdateRegistration -UseExistingRegistration <id>
                                     -UpdateAgentRegistration <id>   (-UpdateRegistration kept as alias)

        -CreateAgentIdentity -UseExistingBlueprint <appId>
                                     -NewAgentIdentity -UseExistingBlueprint <appId>
        -CreateAgentUser -UseExistingAgentIdentity <id>
                                     -NewAgentUser -UseExistingAgentIdentity <id>
        -CreateAgentRegistration -UseExistingAgentIdentity <id>
                                     -NewAgentRegistration -UseExistingAgentIdentity <id>

    A saved command line that still passes -UseExisting* fails with PowerShell's "a parameter
    cannot be found" error; the table above says what to put in its place.

    The registration phase targets POST /beta/copilot/agentRegistrations, which is what the
    a365 CLI actually calls. It is preview surface, absent from the public Graph docs, and
    may require tenant enrolment.

    New-A365AgentUser.ps1 reports failure by exiting with a non-zero code rather than
    throwing. This script checks $LASTEXITCODE for that reason - a try/catch alone never
    fires.

    That script also reports the manager and the licence in its result object instead of
    failing, so an agent user can be created with neither attached. The phase is marked
    'Partial' and the run reports 'Incomplete', rather than calling it a success.

    To provision many agents from one CSV file instead of one at a time, see
    A365-BulkOnboarding.ps1, which drives this orchestrator once per row in
    dependency order.
#>

#requires -Version 7

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'CertificatePassword',
    Justification = 'Forwarded verbatim to the step scripts, which convert it immediately.')]
param(
    # =====================================================================
    # WHAT TO CREATE - pick one or more. At least one action is required.
    # =====================================================================
    [switch] $NewBlueprint,
    [switch] $NewAgentIdentity,
    [switch] $NewAgentRegistration,
    [switch] $NewAgentUser,

    # =====================================================================
    # WHAT TO UPDATE - each takes the id of the object to change, so an
    # update names its own target rather than needing a second parameter.
    # Only the attributes you actually pass on the command line are written.
    # =====================================================================
    [string] $UpdateBlueprint,
    [string] $UpdateAgentIdentity,
    [string] $UpdateAgentUser,
    [Alias('UpdateRegistration')]
    [string] $UpdateAgentRegistration,

    # =====================================================================
    # WHAT TO REMOVE - each takes the id of the object to delete.
    # DESTRUCTIVE. Removal runs in reverse dependency order, after any
    # create or update phases in the same run.
    # =====================================================================
    [string] $RemoveBlueprint,
    [string] $RemoveAgentIdentity,
    [string] $RemoveAgentUser,
    [string] $RemoveAgentRegistration,

    # Modifiers for the removal phases.
    [switch] $RemovePermanent,
    [switch] $RemoveInspectOnly,
    [switch] $RemoveForce,

    # =====================================================================
    # PARENT IDS - only needed when creating something whose parent already
    # exists and is not part of this run. Omit them and the id threads
    # automatically from the phase that produced it.
    # =====================================================================
    [string] $UseExistingBlueprint,
    [string] $UseExistingAgentIdentity,
    [string] $AgentRegistrationIdentityId,

    # =====================================================================
    # COMMON - the only setting every scenario shares
    # =====================================================================
    [Parameter(Mandatory)][string] $TenantId,

    # =====================================================================
    # -NewBlueprint
    # =====================================================================
    [string]      $BlueprintDisplayName,
    [string]      $BlueprintDescription,
    [string[]]    $BlueprintSponsor,
    [string[]]    $BlueprintOwner,
    [switch]      $BlueprintRequireOwnerAssignment,
    [hashtable[]] $BlueprintRequiredPermission,
    [switch]      $BlueprintGrantAdminConsent,
    [switch]      $BlueprintSkipInheritablePermissions,
    [switch]      $BlueprintNewClientSecret,
    [string]      $BlueprintKeyVaultName,
    [string]      $BlueprintKeyVaultSecretName,
    [object]      $KeyVaultAccessToken,
    [string]      $BlueprintManagedIdentityPrincipalId,
    [hashtable]   $BlueprintParameter = @{},

    # =====================================================================
    # -NewAgentIdentity        (needs -NewBlueprint, -UpdateBlueprint or -UseExistingBlueprint)
    # =====================================================================
    [string]      $AgentIdentityDisplayName,
    [string[]]    $AgentIdentitySponsor,
    [string[]]    $AgentIdentityOwner,
    [switch]      $AgentIdentityRequireOwnerAssignment,
    [string[]]    $AgentIdentityTag,
    [object[]]    $AgentIdentityCustomSecurityAttribute,
    [switch]      $AgentIdentitySkipCustomSecurityAttributeValidation,
    # Permissions granted on this one identity, rather than inherited from the blueprint.
    # Rarely needed - use it only when a single identity needs something its siblings must not.
    [hashtable[]] $AgentIdentityRequiredPermission,
    [switch]      $AgentIdentityGrantAdminConsent,
    [hashtable]   $AgentIdentityParameter = @{},

    # =====================================================================
    # -NewAgentRegistration    (needs -NewAgentIdentity, -UpdateAgentIdentity or -AgentRegistrationIdentityId)
    # =====================================================================
    [string] $AgentRegistrationDisplayName,
    [string] $AgentRegistrationDescription,
    [string[]] $AgentRegistrationOwner,
    [ValidateSet('Same', 'Interactive')]
    [string] $AgentRegistrationAuth = 'Same',
    [string] $AgentRegistrationOwnerId,
    [hashtable] $AgentRegistrationParameter = @{},

    # =====================================================================
    # -NewAgentUser            (needs -NewAgentIdentity, -UpdateAgentIdentity or -UseExistingAgentIdentity)
    # =====================================================================
    [string]    $AgentUserPrincipalName,
    [string]    $AgentUserDisplayName,
    [string]    $AgentUserMailNickname,
    [string]    $AgentUserManagerUserId,
    [string]    $AgentUserManagerUpn,
    [string]    $AgentUserUsageLocation,
    [switch]    $AgentUserAssignLicense,
    [string]    $AgentUserLicenseSkuId,
    [string]    $AgentUserLicenseSkuPartNumber,
    [hashtable] $AgentUserParameter = @{},

    # =====================================================================
    # RUN
    # =====================================================================
    [string] $ScriptRoot,
    [string] $OutputJsonPath,
    [switch] $IncludeBlueprintSecretsInOutput,

    # =====================================================================
    # AUTHENTICATION
    # =====================================================================
    [string]       $ClientId,
    [object]       $ClientSecret,
    [string]       $CertificateThumbprint,
    [System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
    [string]       $CertificatePath,
    [object]       $CertificatePassword,
    [switch]       $UseManagedIdentity,
    [object]       $AccessToken,
    [switch]       $Interactive,
    [switch]       $SkipPermissionCheck,

    # =====================================================================
    # LOGGING
    # =====================================================================
    [string] $LogPath,
    [switch] $LogIncludeSecrets,
    [string] $LogCorrelationId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Logging (shared shape across the A365 suite)
#
# One log file per RUN, never appended to by another run: the name carries the date and
# the time to the second, plus a short random suffix so that two runs starting inside the
# same second cannot collide. Windows treats ':' as an alternate-data-stream separator and
# rejects it in a file name, so the time is written HH-mm-ss.
#
# Every line is flushed as it is written rather than buffered, so a log is complete up to
# the moment of a crash - which is exactly when it is worth having.
#
# SECRETS ARE REDACTED BY DEFAULT. -LogIncludeSecrets opts in to recording client secrets
# and passwords. BEARER TOKENS ARE ALWAYS REDACTED and there is deliberately no switch to
# include them: a captured token is replayable by anyone who reads the file until it
# expires, whereas a client secret at least implies possession of the app registration.
# ---------------------------------------------------------------------------

$script:LogFile         = $null
$script:LogDirectory    = $null
$script:LogCorrelation  = $null
$script:KnownLogFiles   = [System.Collections.Generic.HashSet[string]]::new()
$script:ChildLogFiles   = [System.Collections.Generic.List[object]]::new()
$script:LogSeq          = 0
$script:LogGraphCalls   = 0
$script:LogGraphFailed  = 0
$script:LogWarnCount    = 0
$script:LogErrorCount   = 0
$script:LogStart        = $null
$script:LogCompleted    = $false
$script:LogRedactions   = 0

# Property / field names whose VALUE is a credential. Redacted unless -LogIncludeSecrets.
$script:LogSecretNames = @(
    'secretText', 'clientSecret', 'client_secret', 'password', 'CertificatePassword',
    'privateKey', 'proof', 'assertion', 'client_assertion', 'secret', 'pwd'
)

# Property / field names whose value is a bearer token or equivalent. ALWAYS redacted.
$script:LogTokenNames = @(
    'access_token', 'accessToken', 'id_token', 'idToken', 'refresh_token', 'refreshToken',
    'Authorization', 'authorization', 'token', 'Token', 'AccessToken', 'EndpointAccessToken'
)

function Protect-LogText {
    <#
      Redacts credentials from arbitrary text - JSON bodies, form-encoded payloads, header
      dumps and error messages alike. Key-aware first, then a shape-based catch-all: a JWT
      is recognisable on sight, so it is removed no matter which key carried it or whether
      the key was one this function knows about.
    #>
    param([string] $Text)

    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $before = $Text

    # --- always: bearer tokens and anything JWT-shaped -------------------------------
    $Text = [regex]::Replace($Text, 'eyJ[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]*', '<redacted:jwt>')
    $Text = [regex]::Replace($Text, '(?i)(bearer\s+)[A-Za-z0-9._~+/\-]{8,}=*', '${1}<redacted:token>')

    foreach ($name in $script:LogTokenNames) {
        $n = [regex]::Escape($name)
        # JSON: "name": "value"   (handles escaped quotes inside the value)
        $Text = [regex]::Replace($Text, "(?i)(""$n""\s*:\s*)""(?:[^""\\]|\\.)*""", '${1}"<redacted:token>"')
        # form / header / assignment: name=value  or  name: value
        $Text = [regex]::Replace($Text, "(?i)\b$n\s*[=:]\s*[^&,;\r\n""}\s]+", "$name=<redacted:token>")
    }

    # --- by default: client secrets and passwords ------------------------------------
    if (-not $script:LogIncludeSecrets) {
        foreach ($name in $script:LogSecretNames) {
            $n = [regex]::Escape($name)
            $Text = [regex]::Replace($Text, "(?i)(""$n""\s*:\s*)""(?:[^""\\]|\\.)*""", '${1}"<redacted:secret>"')
            $Text = [regex]::Replace($Text, "(?i)\b$n\s*[=:]\s*[^&,;\r\n""}\s]+", "$name=<redacted:secret>")
        }
    }

    if ($Text -ne $before) { $script:LogRedactions++ }
    return $Text
}

function Test-LogSecretName {
    <#
      True when a NAME denotes a credential. Needed because the parameter dump prints the
      name and the value in separate columns, so Protect-LogText - which works on
      "name=value" shapes - sees a bare value with no key to recognise it by. Without this
      the run header would print -ClientSecret in full, which is precisely the leak the
      redaction exists to prevent.
    #>
    param([string] $Name, [switch] $TokensOnly)

    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    foreach ($t in $script:LogTokenNames) { if ($Name -like "*$t*") { return $true } }
    if ($TokensOnly) { return $false }
    foreach ($s in $script:LogSecretNames) { if ($Name -like "*$s*") { return $true } }
    return $false
}

function Initialize-A365Log {
    <#
      Creates the log file for this run and writes the header. A -LogPath that names an
      existing directory (or ends in a separator) gets a generated file name inside it; any
      other value is taken as the file name itself, so a caller can pin an exact path.
    #>
    param(
        [string]    $Path,
        [string]    $ScriptName,
        [hashtable] $BoundParameters = @{},
        [switch]    $IncludeSecrets,
        [string]    $CorrelationId
    )

    $script:LogIncludeSecrets = [bool]$IncludeSecrets
    $script:LogStart          = Get-Date
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }

    # One id shared by every script in a run. Supplied by the orchestrator; generated here
    # when a script is run on its own, so a standalone run is still self-identifying.
    $script:LogCorrelation = if ([string]::IsNullOrWhiteSpace($CorrelationId)) { [guid]::NewGuid().ToString('N').Substring(0, 8) } else { $CorrelationId }
    $stamp  = $script:LogStart.ToString('yyyy-MM-dd_HH-mm-ss')
    $unique = $script:LogCorrelation
    $base   = [IO.Path]::GetFileNameWithoutExtension($ScriptName)

    $isDirectory = (Test-Path -LiteralPath $Path -PathType Container) -or
                   $Path.EndsWith('\') -or $Path.EndsWith('/') -or
                   [string]::IsNullOrEmpty([IO.Path]::GetExtension($Path))

    if ($isDirectory) {
        # -WhatIf:$true would otherwise DEFER this New-Item, and the Resolve-Path below would
        # then fail on a directory that was never created. Creating the log directory is not the
        # operation the user is dry-running, and a dry run should still leave a log behind.
        if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path -Force -WhatIf:$false -Confirm:$false | Out-Null }
        $script:LogDirectory = (Resolve-Path -LiteralPath $Path).ProviderPath
        $script:LogFile = Join-Path $script:LogDirectory "$base-$stamp-$unique.log"
        # A correlation id is shared on purpose, so it cannot also guarantee uniqueness:
        # one run can invoke the same step script twice - a removal cascade does exactly
        # that - and two starts inside the same second would otherwise overwrite each other.
        $dupe = 2
        while (Test-Path -LiteralPath $script:LogFile) {
            $script:LogFile = Join-Path $script:LogDirectory "$base-$stamp-$unique-$dupe.log"
            $dupe++
        }
    }
    else {
        $parent = Split-Path -Parent $Path
        if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force -WhatIf:$false -Confirm:$false | Out-Null }
        $script:LogFile = $Path
        $script:LogDirectory = Split-Path -Parent $script:LogFile
    }

    $secretMode = if ($script:LogIncludeSecrets) {
        'INCLUDED (-LogIncludeSecrets) - this file contains live credentials'
    } else {
        'redacted (pass -LogIncludeSecrets to record client secrets and passwords)'
    }

    $params = foreach ($k in ($BoundParameters.Keys | Sort-Object)) {
        $v = $BoundParameters[$k]
        # Name-aware first: a bare value in its own column carries no clue that it is a
        # credential. Tokens are refused here whatever -LogIncludeSecrets says.
        $rendered =
            if (Test-LogSecretName -Name $k -TokensOnly)                      { '<redacted:token>' }
            elseif ((Test-LogSecretName -Name $k) -and -not $script:LogIncludeSecrets) { '<redacted:secret>' }
            elseif ($null -eq $v)                                             { '(null)' }
            elseif ($v -is [securestring])                                    { '<redacted:securestring>' }
            elseif ($v -is [System.Management.Automation.SwitchParameter])    { [string][bool]$v }
            elseif ($v -is [System.Collections.IDictionary])                  { '{' + (($v.Keys | Sort-Object) -join ', ') + '}' }
            elseif ($v -is [System.Collections.IEnumerable] -and $v -isnot [string]) { (@($v) -join ', ') }
            else                                                              { [string]$v }
        '   {0,-34} {1}' -f $k, (Protect-LogText $rendered)
    }

    $header = @(
        '================================================================================'
        ' Microsoft Agent 365 - script log'
        '================================================================================'
        ('  Script            : {0}' -f $ScriptName)
        ('  Started (local)   : {0}' -f $script:LogStart.ToString('yyyy-MM-dd HH:mm:ss K'))
        ('  Started (UTC)     : {0}' -f $script:LogStart.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
        ('  Correlation id    : {0}' -f $script:LogCorrelation)
        ('  Log file          : {0}' -f $script:LogFile)
        ('  Client secrets    : {0}' -f $secretMode)
        ('  Bearer tokens     : ALWAYS redacted - no switch enables them')
        ('  PowerShell        : {0}' -f $PSVersionTable.PSVersion)
        ('  OS                : {0}' -f [System.Runtime.InteropServices.RuntimeInformation]::OSDescription.Trim())
        ('  Host / user       : {0} / {1}' -f [Environment]::MachineName, [Environment]::UserName)
        ('  Process id        : {0}' -f $PID)
        '--------------------------------------------------------------------------------'
        ' Parameters as bound'
        '--------------------------------------------------------------------------------'
    ) + @($params) + @(
        '================================================================================'
        ''
    )

    [System.IO.File]::WriteAllLines($script:LogFile, $header, (New-Object System.Text.UTF8Encoding($false)))
    return $script:LogFile
}

function Write-A365Log {
    <#
      Appends one line. Levels are fixed width so the file stays column-aligned and greppable:
      TRACE DEBUG INFO WARN ERROR GRAPH STEP.
    #>
    param(
        [ValidateSet('TRACE', 'DEBUG', 'INFO', 'WARN', 'ERROR', 'GRAPH', 'STEP')]
        [string] $Level = 'INFO',
        [string] $Message = '',
        [string] $Detail
    )

    if ($Level -eq 'WARN')  { $script:LogWarnCount++ }
    if ($Level -eq 'ERROR') { $script:LogErrorCount++ }
    if (-not $script:LogFile) { return }

    $script:LogSeq++
    $ts    = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
    $lines = @('[{0}] [{1,-5}] [{2:0000}] {3}' -f $ts, $Level, $script:LogSeq, (Protect-LogText $Message))

    if ($PSBoundParameters.ContainsKey('Detail') -and -not [string]::IsNullOrWhiteSpace($Detail)) {
        foreach ($d in (Protect-LogText $Detail) -split "`r?`n") {
            $lines += ('{0}{1}' -f (' ' * 44), $d)
        }
    }

    # Append per line rather than buffering: a log that stops mid-run is still worth reading.
    [System.IO.File]::AppendAllLines($script:LogFile, [string[]]$lines, (New-Object System.Text.UTF8Encoding($false)))
}

function Write-A365LogGraphRequest {
    param([string] $Method, [string] $Uri, $Body, [int] $Attempt = 1, [int] $MaxAttempts = 1)

    $script:LogGraphCalls++
    if (-not $script:LogFile) { return }

    $suffix = if ($MaxAttempts -gt 1 -and $Attempt -gt 1) { " (attempt $Attempt of $MaxAttempts)" } else { '' }
    $detail = $null
    if ($null -ne $Body) {
        $detail = if ($Body -is [string]) { $Body } else { try { $Body | ConvertTo-Json -Depth 25 } catch { "$Body" } }
        $detail = "request body: $detail"
    }
    Write-A365Log -Level GRAPH -Message ("--> {0} {1}{2}" -f $Method, $Uri, $suffix) -Detail $detail
}

function Write-A365LogGraphResponse {
    param(
        [string] $Method, [string] $Uri, $Response,
        [int] $DurationMs = -1, [int] $Status = 0, [switch] $AsFailure, [string] $ErrorText
    )

    if ($AsFailure) { $script:LogGraphFailed++ }
    if (-not $script:LogFile) { return }

    $took   = if ($DurationMs -ge 0) { " in ${DurationMs}ms" } else { '' }
    $code   = if ($Status -gt 0) { " [$Status]" } else { '' }

    if ($AsFailure) {
        Write-A365Log -Level ERROR -Message ("<-- FAILED {0} {1}{2}{3}" -f $Method, $Uri, $code, $took) -Detail $ErrorText
        return
    }

    $detail = $null
    if ($null -ne $Response) {
        $detail = try { $Response | ConvertTo-Json -Depth 12 -Compress } catch { "$Response" }
        if ($detail.Length -gt 4000) { $detail = $detail.Substring(0, 4000) + " ...<truncated, $($detail.Length) chars>" }
        $detail = "response: $detail"
    }
    Write-A365Log -Level GRAPH -Message ("<-- ok {0} {1}{2}{3}" -f $Method, $Uri, $code, $took) -Detail $detail
}

function Register-A365ChildLog {
    <#
      Records any log file that has appeared in the log directory since the last check and
      attributes it to the phase that produced it. Discovery rather than prediction: each
      child script generates its own file name from its own script name and start time, so
      the orchestrator cannot know it up front - and predicting it would break the moment a
      child changed the format. Discovery also catches grandchildren, such as the removal
      cascade a blueprint delegates to, which is exactly what a reader needs to follow.
    #>
    param([string] $PhaseName = '')

    if (-not $script:LogFile -or -not $script:LogDirectory) { return }

    foreach ($f in (Get-ChildItem -LiteralPath $script:LogDirectory -Filter '*.log' -File -ErrorAction SilentlyContinue)) {
        if ($script:KnownLogFiles.Contains($f.Name)) { continue }
        [void]$script:KnownLogFiles.Add($f.Name)
        $script:ChildLogFiles.Add([pscustomobject]@{ Phase = $PhaseName; Name = $f.Name })
        Write-A365Log -Level INFO -Message ("child log{0}: {1}" -f $(if ($PhaseName) { " [$PhaseName]" } else { '' }), $f.Name)
    }
}

function Write-A365LogCorrelation {
    <#
      Writes the index that ties one orchestrator run to every file it produced, so a reader
      knows the complete set to examine rather than guessing from timestamps in a directory
      that may hold many runs.
    #>
    if (-not $script:LogFile) { return }
    Register-A365ChildLog

    $lines = @(
        ''
        '--------------------------------------------------------------------------------'
        ' Correlated log files for this run'
        '--------------------------------------------------------------------------------'
        ('  orchestrator      : {0}' -f (Split-Path -Leaf $script:LogFile))
    )
    if ($script:ChildLogFiles.Count -eq 0) {
        $lines += '  (no child scripts wrote a log - no phase invoked one)'
    }
    else {
        foreach ($c in $script:ChildLogFiles) {
            $lines += ('  {0,-19}: {1}' -f $(if ($c.Phase) { $c.Phase } else { 'child' }), $c.Name)
        }
    }
    $lines += ('  directory         : {0}' -f $script:LogDirectory)
    [System.IO.File]::AppendAllLines($script:LogFile, [string[]]$lines, (New-Object System.Text.UTF8Encoding($false)))
}
function Complete-A365Log {
    param([string] $Outcome = 'Succeeded')

    if (-not $script:LogFile -or $script:LogCompleted) { return }
    $script:LogCompleted = $true

    $elapsed = if ($script:LogStart) { (Get-Date) - $script:LogStart } else { [TimeSpan]::Zero }
    $footer = @(
        ''
        '================================================================================'
        ' Run summary'
        '================================================================================'
        ('  Outcome           : {0}' -f $Outcome)
        ('  Duration          : {0:n1}s' -f $elapsed.TotalSeconds)
        ('  Graph calls       : {0} ({1} failed)' -f $script:LogGraphCalls, $script:LogGraphFailed)
        ('  Warnings / errors : {0} / {1}' -f $script:LogWarnCount, $script:LogErrorCount)
        ('  Redactions applied: {0}' -f $script:LogRedactions)
        ('  Finished (local)  : {0}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss K'))
        ('  Correlation id    : {0}' -f $script:LogCorrelation)
        ('  Log file          : {0}' -f $script:LogFile)
        '================================================================================'
    )
    [System.IO.File]::AppendAllLines($script:LogFile, [string[]]$footer, (New-Object System.Text.UTF8Encoding($false)))
}


# Start the log before anything else happens, so a failure during validation is still
# recorded. A trap gives the file a proper footer even when the script dies: 'break' inside
# a trap re-throws to the caller, so the error still surfaces exactly as before.
# One id for the whole run. Every script invoked below stamps it into its own log, so the
# complete set is findable with a single search even in a directory holding many runs.
if ([string]::IsNullOrWhiteSpace($LogCorrelationId)) { $LogCorrelationId = [guid]::NewGuid().ToString('N').Substring(0, 8) }

$null = Initialize-A365Log -Path $LogPath -ScriptName 'A365-AutomationOrchestrator.ps1' `
    -BoundParameters $PSBoundParameters -IncludeSecrets:$LogIncludeSecrets -CorrelationId $LogCorrelationId
if ($script:LogFile) {
    # Claim our own file before any phase runs, otherwise the first Register-A365ChildLog
    # sweep sees it as new and attributes the orchestrator's log to that phase.
    [void]$script:KnownLogFiles.Add((Split-Path -Leaf $script:LogFile))
    Write-Host "  Log file           : $($script:LogFile)" -ForegroundColor DarkGray
    Write-Host "  Correlation id     : $LogCorrelationId" -ForegroundColor DarkGray
}

trap {
    Write-A365Log -Level ERROR -Message "UNHANDLED: $($_.Exception.Message)" -Detail $_.ScriptStackTrace
    Write-A365LogCorrelation
    Complete-A365Log -Outcome 'Failed'
    break
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

# Member enumeration ($collection.Name) throws under StrictMode when the property collection
# is empty, and Graph-shaped objects routinely are. Walk the properties instead.
function Test-HasProperty {
    param($Object, [Parameter(Mandatory)][string] $Name)
    if ($null -eq $Object) { return $false }
    foreach ($p in $Object.PSObject.Properties) {
        if ($p.Name -eq $Name) { return $true }
    }
    return $false
}

function Get-PropertyOrDefault {
    param($Object, [Parameter(Mandatory)][string] $Name, $Default = $null)
    if (Test-HasProperty $Object $Name) { return $Object.$Name }
    return $Default
}

function Write-Phase {
    param([int] $Number, [string] $Text)
    # Phases are selected individually, so the step's own number (1-4) is not its position in
    # this run - printing it verbatim would produce "Phase 4 of 1". Count the steps that are
    # actually running for the banner, and keep the fixed number only in the report, where it
    # identifies which step script ran.
    $script:PhaseIndex++
    $total = if ($null -ne $script:PhaseTotal) { $script:PhaseTotal } else { $script:PhaseIndex }
    Write-Host ''
    Write-Host ('#' * 78) -ForegroundColor DarkCyan
    Write-Host ("## Phase $($script:PhaseIndex) of $total : $Text  [step $Number]") -ForegroundColor Cyan
    Write-Host ('#' * 78) -ForegroundColor DarkCyan
    Write-A365Log -Level STEP -Message ("Phase {0} of {1} : {2}  [step {3}]" -f $script:PhaseIndex, $total, $Text, $Number)
}

function Resolve-StepScript {
    param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][string] $Root)
    $path = Join-Path $Root $Name
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required step script '$Name' was not found in '$Root'. Keep the step scripts together with this one, or pass -ScriptRoot."
    }
    return (Resolve-Path -LiteralPath $path).ProviderPath
}

# The step scripts write progress with Write-Host and return their summary on the pipeline.
# Take the last object carrying the property we need, so any stray pipeline output from a
# future edit cannot be mistaken for the summary.
function Select-StepSummary {
    param($Output, [Parameter(Mandatory)][string] $RequiredProperty)
    $match = $null
    foreach ($item in @($Output)) {
        if ($null -eq $item) { continue }
        if (Test-HasProperty $item $RequiredProperty) { $match = $item }
    }
    return $match
}

$script:Steps = [System.Collections.Generic.List[object]]::new()
function Add-StepResult {
    param([string] $Name, [string] $Status, [string] $Detail = '')
    $script:Steps.Add([pscustomobject]@{ Step = $Name; Status = $Status; Detail = $Detail })
    $lvl = switch -Regex ($Status) { 'Failed|Error'  { 'ERROR' } 'Warn|Partial|Unknown|Incomplete' { 'WARN' } default { 'INFO' } }
    Write-A365Log -Level $lvl -Message ("step result: {0} = {1}{2}" -f $Name, $Status, $(if ($Detail) { " ($Detail)" } else { '' }))
}

# ---------------------------------------------------------------------------
# Run report
# ---------------------------------------------------------------------------

# Parameters whose values are credentials in their own right. These are the credentials used to
# AUTHENTICATE the run - not the secret the run creates - so they are redacted unconditionally.
$script:SensitiveParameterNames = @(
    'ClientSecret', 'CertificatePassword', 'AccessToken', 'Certificate', 'Password', 'Secret'
)

# The report is JSON, so every value has to survive ConvertTo-Json. SecureStrings, switches and
# certificates do not, and a SecureString would serialise to a useless type name anyway.
#
# -RedactByName drops any value whose KEY looks like a credential. That is right for the
# parameters a phase was called with, and wrong for the object a phase returned: the blueprint
# returns its new secret in a property literally called 'clientSecret', and whether that is
# written is -IncludeBlueprintSecretsInOutput' decision, made later in New-RunReport. Name matching in PowerShell
# is case-insensitive, so without this split 'clientSecret' would silently collide with the
# '-ClientSecret' input parameter and be redacted even when the caller asked for it.
function ConvertTo-ReportValue {
    param($Value, [switch] $RedactByName, [int] $Depth = 0)

    if ($null -eq $Value)  { return $null }
    if ($Depth -gt 8)      { return [string]$Value }

    if ($Value -is [System.Security.SecureString])       { return '(redacted)' }
    if ($Value -is [System.Management.Automation.SwitchParameter]) { return [bool]$Value.IsPresent }
    if ($Value -is [System.Security.Cryptography.X509Certificates.X509Certificate2]) {
        return "(certificate thumbprint $($Value.Thumbprint))"
    }
    if ($Value -is [string] -or $Value.GetType().IsPrimitive -or $Value -is [datetime]) { return $Value }

    if ($Value -is [System.Collections.IDictionary]) {
        $map = [ordered]@{}
        foreach ($key in $Value.Keys) {
            if ($RedactByName -and ($script:SensitiveParameterNames -contains [string]$key)) { $map[[string]$key] = '(redacted)' }
            else { $map[[string]$key] = ConvertTo-ReportValue $Value[$key] -RedactByName:$RedactByName -Depth ($Depth + 1) }
        }
        return $map
    }
    # The step scripts return PSCustomObjects. Flatten them to ordered dictionaries so the
    # redaction pass in New-RunReport can actually reach into them by key.
    if ($Value -is [psobject] -and $Value.PSObject.Properties) {
        $map = [ordered]@{}
        $any = $false
        foreach ($property in $Value.PSObject.Properties) {
            $any = $true
            if ($RedactByName -and ($script:SensitiveParameterNames -contains $property.Name)) { $map[$property.Name] = '(redacted)' }
            else { $map[$property.Name] = ConvertTo-ReportValue $property.Value -RedactByName:$RedactByName -Depth ($Depth + 1) }
        }
        if ($any) { return $map }
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        return @(foreach ($item in $Value) { ConvertTo-ReportValue $item -RedactByName:$RedactByName -Depth ($Depth + 1) })
    }
    return $Value
}

# Records what a phase was actually asked to do. Useful when a run is reproduced later from the
# report alone, which is the main reason the parameters are captured at all.
function ConvertTo-ReportParameterMap {
    param([System.Collections.IDictionary] $Arguments)
    if (-not $Arguments) { return $null }
    $map = [ordered]@{}
    foreach ($key in (@($Arguments.Keys) | Sort-Object)) {
        if ($script:SensitiveParameterNames -contains [string]$key) { $map[[string]$key] = '(redacted)'; continue }
        $map[[string]$key] = ConvertTo-ReportValue $Arguments[$key] -RedactByName
    }
    return $map
}

$script:Phases = [System.Collections.Generic.List[object]]::new()

function Start-PhaseRecord {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '',
        Justification = 'Builds an in-memory report record. It changes nothing on the system, so gating it behind ShouldProcess would blank the report under -WhatIf.')]
    param(
        [Parameter(Mandatory)][int]    $Number,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Script
    )
    $record = [ordered]@{
        phase           = $Number
        name            = $Name
        script          = [IO.Path]::GetFileName($Script)
        scriptPath      = $Script
        status          = 'Pending'
        detail          = ''
        ran             = $false
        startedUtc      = [DateTimeOffset]::UtcNow.ToString('o')
        completedUtc    = $null
        durationSeconds = $null
        parameters      = $null
        identifiers     = [ordered]@{}
        secrets         = $null
        result          = $null
        error           = $null
    }
    $script:Phases.Add($record)
    return $record
}

# A phase that never ran still gets an entry, so the report always has all three and a consumer
# never has to guess whether a missing phase failed or was deliberately skipped.
function Add-SkippedPhaseRecord {
    param(
        [Parameter(Mandatory)][int]    $Number,
        [Parameter(Mandatory)][string] $Name,
        # A phase that was never selected may legitimately have no step script on disk.
        [AllowNull()][AllowEmptyString()][string] $Script,
        [Parameter(Mandatory)][string] $Reason,
        [hashtable] $Identifiers = @{}
    )
    $record = [ordered]@{
        phase           = $Number
        name            = $Name
        script          = $(if ($Script) { [IO.Path]::GetFileName($Script) } else { $null })
        scriptPath      = $(if ($Script) { $Script } else { $null })
        status          = 'Skipped'
        detail          = $Reason
        ran             = $false
        startedUtc      = $null
        completedUtc    = $null
        durationSeconds = $null
        parameters      = $null
        identifiers     = [ordered]@{}
        secrets         = $null
        result          = $null
        error           = $null
    }
    foreach ($key in ($Identifiers.Keys | Sort-Object)) { $record.identifiers[[string]$key] = $Identifiers[$key] }
    $script:Phases.Add($record)
    return $record
}

function Complete-PhaseRecord {
    param(
        [Parameter(Mandatory)] $Record,
        [Parameter(Mandatory)][string] $Status,
        [string] $Detail = '',
        $Result,
        [hashtable] $Identifiers = @{},
        $ErrorRecord
    )
    if ($null -eq $Record) { return }

    # Each child script names its own log file, so the orchestrator cannot know the name in
    # advance - it discovers it here instead. Called from every phase-completion site, on the
    # success and the failure path alike, so a phase that died still has its log correlated.
    # This also picks up GRANDCHILD logs, such as the cascade a blueprint removal delegates to.
    Register-A365ChildLog -PhaseName $Record.name

    $Record.status       = $Status
    $Record.detail       = $Detail
    $Record.ran          = $true
    $Record.completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    if ($Record.startedUtc) {
        $Record.durationSeconds = [math]::Round(
            ([DateTimeOffset]::Parse($Record.completedUtc) - [DateTimeOffset]::Parse($Record.startedUtc)).TotalSeconds, 2)
    }
    if ($PSBoundParameters.ContainsKey('Result')) { $Record.result = $Result }
    foreach ($key in ($Identifiers.Keys | Sort-Object)) { $Record.identifiers[[string]$key] = $Identifiers[$key] }

    if ($ErrorRecord) {
        $message = [string]$ErrorRecord.Exception.Message
        # Invoke-Graph rethrows a string, so the HTTP status only survives inside the message text.
        $status  = $null
        if ($message -match 'failed \[(\d{3})[\s\]]') { $status = [int]$Matches[1] }
        elseif ($message -match 'HTTP/1\.1 (\d{3})') { $status = [int]$Matches[1] }
        $graphCode = $null
        if ($message -match '"code"\s*:\s*"([^"]+)"') { $graphCode = $Matches[1] }

        $Record.error = [ordered]@{
            message         = $message
            httpStatus      = $status
            graphErrorCode  = $graphCode
            scriptStackTrace = [string]$ErrorRecord.ScriptStackTrace
        }
    }
}

# Everything that lands on disk goes through here, so there is exactly one place where the
# decision to write or withhold a secret is made.
function New-RunReport {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '',
        Justification = 'Builds an in-memory object. The single caller, Write-RunReport, owns the ShouldProcess gate for the actual file write.')]
    param([bool] $WithSecrets)

    $phases = foreach ($phase in $script:Phases) {
        $copy = [ordered]@{}
        foreach ($key in $phase.Keys) { $copy[$key] = $phase[$key] }

        $copy.result = ConvertTo-ReportValue $phase.result
        if (-not $WithSecrets -and $copy.result -is [System.Collections.IDictionary]) {
            foreach ($secretKey in @('clientSecret', 'secretText')) {
                if ($copy.result.Contains($secretKey) -and $copy.result[$secretKey]) {
                    $copy.result[$secretKey] = '(redacted - re-run with -IncludeBlueprintSecretsInOutput)'
                }
            }
        }

        if ($phase.secrets) {
            $secrets = [ordered]@{}
            foreach ($key in $phase.secrets.Keys) { $secrets[$key] = $phase.secrets[$key] }
            if (-not $WithSecrets -and $secrets.Contains('clientSecret') -and $secrets['clientSecret']) {
                $secrets['clientSecret'] = '(redacted - re-run with -IncludeBlueprintSecretsInOutput)'
            }
            $copy.secrets = $secrets
        }
        [pscustomobject]$copy
    }

    return [ordered]@{
        schemaVersion = '1.1'
        kind          = 'A365ProvisioningRunReport'
        generatedUtc  = [DateTimeOffset]::UtcNow.ToString('o')
        secretsIncluded = $WithSecrets
        run           = $script:RunContext
        phases        = @($phases)
        summary       = $script:RunSummary
        steps         = @($script:Steps)
    }
}

function Write-RunReport {
    param(
        [Parameter(Mandatory)][string] $Path,
        [bool] $WithSecrets
    )

    $report = New-RunReport -WithSecrets $WithSecrets

    # Set-Content, New-Item and Set-Acl all implement ShouldProcess, so under -WhatIf they would
    # refuse to write and the run would produce no report at all - even though the report only
    # DESCRIBES the run and creating it is not one of the operations being simulated. A -WhatIf
    # report is the most useful kind, so suppress the preference across the writes and restore it.
    $previousWhatIfPreference = $WhatIfPreference
    try {
        $WhatIfPreference = $false

        $directory = Split-Path -Parent $Path
        if ($directory -and -not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }

        # Create the file before writing so the ACL is tightened while it is still empty, rather
        # than leaving a window where the secret sits on disk world-readable.
        if ($WithSecrets) {
            New-Item -ItemType File -Path $Path -Force | Out-Null
            Protect-ReportFile -Path $Path
        }

        $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
    }
    finally {
        $WhatIfPreference = $previousWhatIfPreference
    }

    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    Write-Host ''
    if ($WithSecrets) {
        Write-Host "Run report written to $resolved" -ForegroundColor Green
        Write-Host '  This file CONTAINS the client secret in plaintext. Treat it as a credential.' -ForegroundColor Yellow
    }
    else {
        Write-Host "Run report written to $resolved (secrets redacted)" -ForegroundColor Green
        if ($script:CreatedSecretCount -gt 0) {
            Write-Host '  Add -IncludeBlueprintSecretsInOutput to capture the client secret in the report.' -ForegroundColor DarkGray
        }
    }
    return $resolved
}

# Best effort - an ACL cannot be set on every filesystem, and failing to tighten it must not lose
# the report that was just generated. The warning matters more than the failure.
function Protect-ReportFile {
    param([Parameter(Mandatory)][string] $Path)
    if (-not $IsWindows) {
        Write-Host '  Note: file permissions were not restricted (non-Windows host).' -ForegroundColor DarkGray
        return
    }
    try {
        $acl = Get-Acl -LiteralPath $Path
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($rule in @($acl.Access)) { [void]$acl.RemoveAccessRule($rule) }
        $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
                $me, 'FullControl', 'None', 'None', 'Allow'))
        Set-Acl -LiteralPath $Path -AclObject $acl
    }
    catch {
        Write-Warning "Could not restrict permissions on '$Path': $($_.Exception.Message). Secure it yourself."
    }
}

# ---------------------------------------------------------------------------
# Resolve the names
# ---------------------------------------------------------------------------

$root = if ($ScriptRoot) { $ScriptRoot } elseif ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).ProviderPath }
if (-not (Test-Path -LiteralPath $root)) { throw "-ScriptRoot '$root' does not exist." }
$root = (Resolve-Path -LiteralPath $root).ProviderPath

$bpName  = $BlueprintDisplayName
$aiName  = $AgentIdentityDisplayName
# The registration names the identity it represents, so it still falls back to the identity's
# display name when that identity is created in the same run. Against an existing identity there
# is nothing to fall back to, and the validation below asks for the name explicitly.
$regName = if ($AgentRegistrationDisplayName) { $AgentRegistrationDisplayName } else { $aiName }
$auName  = $AgentUserDisplayName

# ---------------------------------------------------------------------------
# Work out what will actually run, and fail early on impossible combinations
#
# The -New* switches say what to CREATE. The -Update* parameters each carry the id of the
# object they change, so an update names its own target. When a create needs a parent that is
# not part of this run, the parent id is an ordinary attribute of the phase that needs it
# (-UseExistingBlueprint, -UseExistingAgentIdentity, -AgentRegistrationIdentityId). Every phase
# therefore has exactly one stated source instead of being inferred from which ids are present.
# ---------------------------------------------------------------------------

# -UseExistingBlueprint and -UseExistingAgentIdentity name a parent that already exists, for a
# create phase whose parent is not part of this run. The two -UseExisting* names that are NOT
# back are the ones that never meant "create under this" - they meant "change this object", and
# each is now the -Update* parameter that carries its own target:
#   -UseExistingAgentUser    -> -UpdateAgentUser <id-or-upn>
#   -UseExistingRegistration -> -UpdateAgentRegistration <T_id>
#
# -AgentRegistrationIdentityId keeps its own name deliberately: on -UpdateAgentRegistration it
# re-points a registration at a DIFFERENT agent identity, which is not a "use existing" meaning.

$runBlueprint    = [bool]$NewBlueprint
$runIdentity     = [bool]$NewAgentIdentity
$runRegistration = [bool]$NewAgentRegistration
$runAgentUser    = [bool]$NewAgentUser

# Update is a separate axis from create: $run* stays "create this", so every existing
# create-only rule below (mandatory names, sponsors, upstream sources) keeps its exact meaning.
# $phase* is "this phase runs at all", and is what drives phase counting and the stray-parameter
# guard. An update is requested by supplying an id, so presence of the id IS the switch.
$updBlueprint    = -not [string]::IsNullOrWhiteSpace($UpdateBlueprint)
$updIdentity     = -not [string]::IsNullOrWhiteSpace($UpdateAgentIdentity)
$updRegistration = -not [string]::IsNullOrWhiteSpace($UpdateAgentRegistration)
$updAgentUser    = -not [string]::IsNullOrWhiteSpace($UpdateAgentUser)

$phaseBlueprint    = $runBlueprint    -or $updBlueprint
$phaseIdentity     = $runIdentity     -or $updIdentity
$phaseRegistration = $runRegistration -or $updRegistration
$phaseAgentUser    = $runAgentUser    -or $updAgentUser

# Removal is a third axis, deliberately kept out of $phase*: a removal phase runs after every
# create and update phase, in reverse dependency order, and must not be counted as the source
# of an object for the phases above it.
$rmBlueprint    = -not [string]::IsNullOrWhiteSpace($RemoveBlueprint)
$rmIdentity     = -not [string]::IsNullOrWhiteSpace($RemoveAgentIdentity)
$rmAgentUser    = -not [string]::IsNullOrWhiteSpace($RemoveAgentUser)
$rmRegistration = -not [string]::IsNullOrWhiteSpace($RemoveAgentRegistration)
$anyRemoval     = $rmBlueprint -or $rmIdentity -or $rmAgentUser -or $rmRegistration

if (-not ($phaseBlueprint -or $phaseIdentity -or $phaseRegistration -or $phaseAgentUser -or $anyRemoval)) {
    throw @'
Nothing was selected. Choose at least one action.

Create:
  -NewBlueprint              create a blueprint application and its principal
  -NewAgentIdentity          create an agent identity
  -NewAgentUser              create the agent user
  -NewAgentRegistration      register it in the registry

Update (takes the id of the object to change; only the attributes you pass are written):
  -UpdateBlueprint         <blueprint appId or objectId>
  -UpdateAgentIdentity     <agent identity objectId>
  -UpdateAgentUser         <agent user objectId or UPN>
  -UpdateAgentRegistration <registration id, starts with T_>

When a create needs a parent that already exists, name it:
  -UseExistingBlueprint    <blueprint appId>   for -NewAgentIdentity
  -UseExistingAgentIdentity         <identity objectId> for -NewAgentUser
  -UseExistingAgentIdentity   <identity objectId> for -NewAgentUser and/or -NewAgentRegistration

Or delete something (DESTRUCTIVE; add -RemoveInspectOnly to see the plan first):
  -RemoveBlueprint         <blueprint appId or objectId>
  -RemoveAgentIdentity     <agent identity objectId>
  -RemoveAgentUser         <agent user objectId>
  -RemoveAgentRegistration <registration id, starts with T_>

The whole pipeline:
  -NewBlueprint -NewAgentIdentity -NewAgentRegistration
'@
}

# Creating, updating or deleting the same object in one run is contradictory.
$removalPairs = @(
    @{ On = $rmBlueprint;    Name = '-RemoveBlueprint';         Create = $runBlueprint;    CreateSwitch = '-NewBlueprint';         Update = $updBlueprint;    UpdateName = '-UpdateBlueprint' },
    @{ On = $rmIdentity;     Name = '-RemoveAgentIdentity';     Create = $runIdentity;     CreateSwitch = '-NewAgentIdentity';     Update = $updIdentity;     UpdateName = '-UpdateAgentIdentity' },
    @{ On = $rmAgentUser;    Name = '-RemoveAgentUser';         Create = $runAgentUser;    CreateSwitch = '-NewAgentUser';         Update = $updAgentUser;    UpdateName = '-UpdateAgentUser' },
    @{ On = $rmRegistration; Name = '-RemoveAgentRegistration'; Create = $runRegistration; CreateSwitch = '-NewAgentRegistration'; Update = $updRegistration; UpdateName = '-UpdateAgentRegistration' }
)
foreach ($pair in $removalPairs) {
    if (-not $pair.On) { continue }
    if ($pair.Create) {
        throw "$($pair.Name) and $($pair.CreateSwitch) cannot both be used: creating an object and deleting it in the same run is contradictory."
    }
    if ($pair.Update) {
        throw "$($pair.Name) and $($pair.UpdateName) cannot both be used: there is no point writing attributes to an object this run then deletes."
    }
}

# Creating and updating the same object in one run is contradictory: the create path already
# writes every attribute it was given.
$actionPairs = @(
    @{ On = $updBlueprint;    Switch = '-UpdateBlueprint';         Create = $runBlueprint;    CreateSwitch = '-NewBlueprint' },
    @{ On = $updIdentity;     Switch = '-UpdateAgentIdentity';     Create = $runIdentity;     CreateSwitch = '-NewAgentIdentity' },
    @{ On = $updAgentUser;    Switch = '-UpdateAgentUser';         Create = $runAgentUser;    CreateSwitch = '-NewAgentUser' },
    @{ On = $updRegistration; Switch = '-UpdateAgentRegistration'; Create = $runRegistration; CreateSwitch = '-NewAgentRegistration' }
)
foreach ($pair in $actionPairs) {
    if ($pair.On -and $pair.Create) {
        throw "$($pair.Switch) and $($pair.CreateSwitch) cannot both be used: an object is either created or updated in a single run. The create path already applies every attribute you pass."
    }
}

# A parent id names something that must ALREADY exist, so it contradicts any action in the
# same run that produces that same parent.
if ($UseExistingBlueprint -and $runBlueprint) {
    throw '-UseExistingBlueprint names a blueprint that already exists, so -NewBlueprint cannot also apply. Drop one of them.'
}
if ($UseExistingBlueprint -and $updBlueprint) {
    throw '-UseExistingBlueprint is not needed with -UpdateBlueprint: the agent identity is built on the blueprint that phase 1 acts on. Drop -UseExistingBlueprint.'
}
$identityAnchors = @(
    @{ Name = '-UseExistingAgentIdentity';         Value = $UseExistingAgentIdentity },
    @{ Name = '-AgentRegistrationIdentityId'; Value = $AgentRegistrationIdentityId }
)
foreach ($anchor in $identityAnchors) {
    if (-not $anchor.Value) { continue }
    if ($runIdentity) {
        throw "$($anchor.Name) names an agent identity that already exists, so -NewAgentIdentity cannot also apply. Drop one of them."
    }
    if ($runBlueprint) {
        throw "$($anchor.Name) starts from an existing agent identity, so -NewBlueprint cannot also apply. Drop one of them."
    }
}

# Each phase must have a source for the object it builds on. Phase 1 supplies the blueprint
# whether it created or updated one; phase 2 likewise supplies the agent identity.
if ($runIdentity -and -not ($runBlueprint -or $updBlueprint -or $UseExistingBlueprint)) {
    throw '-NewAgentIdentity needs a blueprint. Add -NewBlueprint to create one, -UpdateBlueprint <id> to act on one, or -UseExistingBlueprint <appId> to build on an existing one.'
}
if ($runRegistration -and -not ($runIdentity -or $updIdentity -or $UseExistingAgentIdentity -or $AgentRegistrationIdentityId)) {
    throw '-NewAgentRegistration needs an agent identity. Add -NewAgentIdentity to create one, -UpdateAgentIdentity <id> to act on one, or -UseExistingAgentIdentity <objectId> to register an existing one.'
}
# Creating a registration registers exactly one agent identity, so two different ids for it is
# not a preference to resolve - it is a contradiction. This only applies to the create path:
# with -UpdateAgentRegistration, -AgentRegistrationIdentityId re-points the registration at a
# DIFFERENT identity on purpose, so the two legitimately differ there.
if ($runRegistration -and $UseExistingAgentIdentity -and $AgentRegistrationIdentityId -and
    $UseExistingAgentIdentity -ne $AgentRegistrationIdentityId) {
    throw "-UseExistingAgentIdentity ('$UseExistingAgentIdentity') and -AgentRegistrationIdentityId ('$AgentRegistrationIdentityId') name different agent identities, but -NewAgentRegistration registers only one. Drop one of them."
}
if ($runAgentUser -and -not ($runIdentity -or $updIdentity -or $UseExistingAgentIdentity)) {
    throw '-NewAgentUser needs an agent identity to create the user under. Add -NewAgentIdentity to create one, -UpdateAgentIdentity <id> to act on one, or -UseExistingAgentIdentity <objectId> to use an existing one.'
}

# The ids the later phases build on, whether produced by an earlier phase here or supplied.
# An update in phase 1 or 2 is a source for the phases below it, exactly as a create is.
$existingBlueprintAppId = if ($UseExistingBlueprint) { $UseExistingBlueprint }
                          elseif ($updBlueprint)         { $UpdateBlueprint }
                          else                           { '' }
$existingAgentIdentityId = if ($UseExistingAgentIdentity)             { $UseExistingAgentIdentity }
                           elseif ($AgentRegistrationIdentityId) { $AgentRegistrationIdentityId }
                           elseif ($updIdentity)                 { $UpdateAgentIdentity }
                           else                                  { '' }

$script:PhaseTotal = @($phaseBlueprint, $phaseIdentity, $phaseRegistration, $phaseAgentUser,
                       $rmRegistration, $rmAgentUser, $rmIdentity, $rmBlueprint).Where({ $_ }).Count
$script:PhaseIndex = 0

# Parameters that belong to a scenario that was not selected would otherwise be accepted in
# silence and do nothing, which is exactly the confusion the switches exist to remove.
# The gate is "was this phase selected at all", so update runs keep access to their own
# parameters.
$scenarioParams = @(
    @{ Run = $phaseBlueprint;    Switch = '-NewBlueprint / -UpdateBlueprint';         Names = @('BlueprintDisplayName', 'BlueprintDescription', 'BlueprintSponsor', 'BlueprintOwner', 'BlueprintRequireOwnerAssignment', 'BlueprintRequiredPermission', 'BlueprintGrantAdminConsent', 'BlueprintSkipInheritablePermissions', 'BlueprintNewClientSecret', 'BlueprintManagedIdentityPrincipalId', 'BlueprintParameter', 'BlueprintKeyVaultName', 'BlueprintKeyVaultSecretName') }
    @{ Run = $phaseIdentity;     Switch = '-NewAgentIdentity / -UpdateAgentIdentity'; Names = @('AgentIdentityDisplayName', 'AgentIdentitySponsor', 'AgentIdentityOwner', 'AgentIdentityRequireOwnerAssignment', 'AgentIdentityTag', 'AgentIdentityCustomSecurityAttribute', 'AgentIdentitySkipCustomSecurityAttributeValidation', 'AgentIdentityRequiredPermission', 'AgentIdentityGrantAdminConsent', 'AgentIdentityParameter', 'UseExistingBlueprint') }
    @{ Run = $phaseRegistration; Switch = '-NewAgentRegistration / -UpdateAgentRegistration'; Names = @('AgentRegistrationDisplayName', 'AgentRegistrationDescription', 'AgentRegistrationOwner', 'AgentRegistrationAuth', 'AgentRegistrationOwnerId', 'AgentRegistrationParameter', 'AgentRegistrationIdentityId', 'UseExistingAgentIdentity') }
    @{ Run = $phaseAgentUser;    Switch = '-NewAgentUser / -UpdateAgentUser';         Names = @('AgentUserPrincipalName', 'AgentUserDisplayName', 'AgentUserMailNickname', 'AgentUserManagerUserId', 'AgentUserManagerUpn', 'AgentUserUsageLocation', 'AgentUserAssignLicense', 'AgentUserLicenseSkuId', 'AgentUserLicenseSkuPartNumber', 'AgentUserParameter', 'UseExistingAgentIdentity') }
)
# -UseExistingAgentIdentity belongs to two phases, so a parameter is only stray when NONE of the
# phases claiming it ran. Warning per group would report it as ignored for the phase that was not
# selected even while the other phase was actively using it, and would report a genuinely stray
# one twice - once per claiming phase. Both are avoided by reporting per parameter, not per group.
$claimedByRunningPhase = @($scenarioParams | Where-Object { $_.Run } | ForEach-Object { $_.Names } | Sort-Object -Unique)
$strayPhases = [ordered]@{}
foreach ($group in $scenarioParams) {
    if ($group.Run) { continue }
    foreach ($name in $group.Names) {
        if (-not $PSBoundParameters.ContainsKey($name)) { continue }
        if ($claimedByRunningPhase -contains $name) { continue }
        if (-not $strayPhases.Contains($name)) { $strayPhases[$name] = @() }
        $strayPhases[$name] += $group.Switch
    }
}
foreach ($name in $strayPhases.Keys) {
    $owners = @($strayPhases[$name])
    $tail = if ($owners.Count -gt 1) { 'none of which was selected' } else { 'which was not selected' }
    Write-Warning ("Ignored: -{0} applies to {1}, {2}." -f $name, ($owners -join ' and '), $tail)
}

# Each scenario now names its own object; there is no shared base name to derive one from.
# A missing name is refused here, where the message can say which parameter to add, rather
# than surfacing later as a bind error against a step script's mandatory -DisplayName.
# -BlueprintKeyVaultName only ever means "store the secret this run creates". Without a secret
# to store it is a silent no-op, which is the worst outcome for a credential-handling option:
# the run reports success and nothing is saved.
if (($BlueprintKeyVaultName -or $BlueprintKeyVaultSecretName) -and -not $BlueprintNewClientSecret) {
    throw '-BlueprintKeyVaultName / -BlueprintKeyVaultSecretName store the client secret this run creates, so they need -BlueprintNewClientSecret. Without it there is no secret to store and nothing would be written to the vault.'
}
if (($BlueprintKeyVaultName -or $BlueprintKeyVaultSecretName) -and -not ($runBlueprint -or $updBlueprint)) {
    throw '-BlueprintKeyVaultName applies to the blueprint phase, which was not selected. Add -NewBlueprint or -UpdateBlueprint <id>.'
}
if ($runBlueprint -and -not $BlueprintDisplayName) {
    throw '-NewBlueprint requires -BlueprintDisplayName.'
}
if ($runIdentity -and -not $AgentIdentityDisplayName) {
    throw '-NewAgentIdentity requires -AgentIdentityDisplayName.'
}
if ($runRegistration -and -not $regName) {
    throw '-NewAgentRegistration requires -AgentRegistrationDisplayName. (It is optional only when -NewAgentIdentity runs in the same call, where it defaults to -AgentIdentityDisplayName.)'
}

# Sponsorship is per-object: each scenario names its own sponsors. Both APIs take a sponsors
# collection, so both parameters accept a list.
$sponsorHelp = 'Pass a user, a Microsoft 365 group or a dynamic group. Static security groups and role-assignable groups are rejected by the service.'
if ($runBlueprint -and (-not $BlueprintSponsor -or $BlueprintSponsor.Count -eq 0)) {
    throw "-NewBlueprint requires -BlueprintSponsor. $sponsorHelp"
}
if ($runIdentity -and (-not $AgentIdentitySponsor -or $AgentIdentitySponsor.Count -eq 0)) {
    throw "-NewAgentIdentity requires -AgentIdentitySponsor. $sponsorHelp"
}

# ---------------------------------------------------------------------------
# Resolve the step scripts
#
# Each one is required only when its scenario was selected. A run that just creates an agent
# user must not fail because the blueprint script is missing; the path is still probed so the
# report can name it on a skipped phase.
# ---------------------------------------------------------------------------

function Resolve-OptionalStepScript {
    param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][string] $Root, [bool] $Required)
    if ($Required) { return Resolve-StepScript -Name $Name -Root $Root }
    $probe = Join-Path $Root $Name
    if (Test-Path -LiteralPath $probe) { return (Resolve-Path -LiteralPath $probe).ProviderPath }
    return $null
}

# All four update phases have the same shape - announce, run the step script's own -Update mode,
# record the outcome - so the plumbing lives here once. The orchestrator still makes no Graph
# calls of its own; each step script owns its update semantics, which is what keeps the
# "only the attributes you supplied" behaviour identical whether the step script is run directly
# or through this orchestrator.
function Invoke-UpdatePhase {
    param(
        [Parameter(Mandatory)][int]       $Number,
        [Parameter(Mandatory)][string]    $Name,
        [Parameter(Mandatory)][string]    $Title,
        [Parameter(Mandatory)][string]    $ScriptPath,
        [Parameter(Mandatory)][hashtable] $Arguments,
        [Parameter(Mandatory)][string]    $SummaryProperty,
        [Parameter(Mandatory)][string]    $Target
    )

    Write-Phase $Number $Title
    $record = Start-PhaseRecord -Number $Number -Name $Name -Script $ScriptPath
    $record.parameters = ConvertTo-ReportParameterMap $Arguments

    Write-A365Log -Level INFO -Message ("invoking {0}" -f [IO.Path]::GetFileName($ScriptPath)) `
        -Detail (($Arguments.Keys | Sort-Object | ForEach-Object { "-$_ $($Arguments[$_])" }) -join ' ')
    try {
        $output = & $ScriptPath @Arguments -WhatIf:$WhatIfPreference
    }
    catch {
        Add-StepResult $Name 'Failed' $_.Exception.Message
        Complete-PhaseRecord -Record $record -Status 'Failed' -Detail $_.Exception.Message -ErrorRecord $_
        throw "Phase $Number ($Name update) failed: $($_.Exception.Message)"
    }

    $summary = Select-StepSummary -Output $output -RequiredProperty $SummaryProperty

    # Not every step script emits a summary object without -PassThru, and an update that printed
    # its own success is still a success. Report the missing summary rather than failing the run.
    if (-not $summary) {
        Add-StepResult $Name 'Updated' "$Target (no summary object returned)"
        Complete-PhaseRecord -Record $record -Status 'Updated' -Detail "$Target (no summary object returned)"
        return $null
    }

    Add-StepResult $Name 'Updated' $Target
    Complete-PhaseRecord -Record $record -Status 'Updated' -Detail $Target -Result $summary `
        -Identifiers @{ $SummaryProperty = (Get-PropertyOrDefault $summary $SummaryProperty) }
    return $summary
}

$blueprintScript    = Resolve-OptionalStepScript -Name 'New-A365AgentBlueprint.ps1'    -Root $root -Required $phaseBlueprint
$identityScript     = Resolve-OptionalStepScript -Name 'New-A365AgentIdentity.ps1'     -Root $root -Required $phaseIdentity
$registrationScript = Resolve-OptionalStepScript -Name 'New-A365AgentRegistration.ps1' -Root $root -Required $phaseRegistration
$agentUserScript    = Resolve-OptionalStepScript -Name 'New-A365AgentUser.ps1'         -Root $root -Required $phaseAgentUser

# Agent user preconditions, checked before anything is created rather than several phases in.
if ($runAgentUser) {
    if (-not $AgentUserPrincipalName) {
        throw '-NewAgentUser requires -AgentUserPrincipalName. New-A365AgentUser.ps1 has no fallback and cannot derive one.'
    }
    if ($AgentUserPrincipalName -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') {
        throw "-AgentUserPrincipalName '$AgentUserPrincipalName' is not a valid UPN. Expected <name>@<verified-domain>."
    }
    if ($AgentUserAssignLicense -and -not ($AgentUserLicenseSkuId -or $AgentUserLicenseSkuPartNumber -or
                                  $AgentUserParameter.ContainsKey('LicenseSkuId') -or
                                  $AgentUserParameter.ContainsKey('LicenseSkuPartNumber'))) {
        throw '-AgentUserAssignLicense needs -AgentUserLicenseSkuId or -AgentUserLicenseSkuPartNumber.'
    }
    # The step script silently prefers ManagerUserId when both are set. Silent precedence
    # between two parameters that mean different things is worth refusing outright.
    if ($AgentUserManagerUserId -and $AgentUserManagerUpn) {
        throw 'Specify -AgentUserManagerUserId or -AgentUserManagerUpn, not both. New-A365AgentUser.ps1 would silently ignore the UPN.'
    }
    # The step script's own -BlueprintAppId and -AgentIdentityId are mutually exclusive, and this
    # phase always supplies the identity. Catch the collision here, where the message can say
    # why, rather than letting the step script reject it after the earlier phases have run.
    if ($AgentUserParameter.ContainsKey('BlueprintAppId')) {
        throw 'Do not pass BlueprintAppId through -AgentUserParameter. The agent user is always bound to the agent identity, and New-A365AgentUser.ps1 rejects both together.'
    }
    if ($AgentUserParameter.ContainsKey('AgentIdentityId')) {
        throw 'Do not pass AgentIdentityId through -AgentUserParameter. It is supplied automatically; use -UseExistingAgentIdentity <objectId> to target an identity that already exists.'
    }
    foreach ($mk in @('ManagerUserId', 'ManagerUpn')) {
        if ($AgentUserParameter.ContainsKey($mk) -and ($AgentUserManagerUserId -or $AgentUserManagerUpn)) {
            throw "Manager was given twice: '$mk' via -AgentUserParameter and also as -AgentUser$mk. Use one or the other."
        }
    }
}

# Agent user update preconditions. The create-only rules above (a UPN is mandatory, a licence
# needs a SKU) do not apply, but the ambiguous-manager rule does.
if ($updAgentUser) {
    if ($AgentUserManagerUserId -and $AgentUserManagerUpn) {
        throw 'Specify -AgentUserManagerUserId or -AgentUserManagerUpn, not both. New-A365AgentUser.ps1 would silently ignore the UPN.'
    }
    if ($PSBoundParameters.ContainsKey('AgentUserPrincipalName')) {
        throw '-UpdateAgentUser already names the account to change. The agent user principal name cannot be changed after creation, so -AgentUserPrincipalName is not accepted here.'
    }
    foreach ($blocked in @('AgentIdentityId', 'BlueprintAppId', 'Update', 'AgentUserId')) {
        if ($AgentUserParameter.ContainsKey($blocked)) {
            throw "Do not pass $blocked through -AgentUserParameter during an update; it is supplied automatically from -UpdateAgentUser."
        }
    }
}

# One auth method must be chosen explicitly, exactly as the step scripts require, so an
# unattended run cannot silently stall on a sign-in prompt.
$authModes = @()
if ($Interactive)        { $authModes += 'Interactive' }
if ($AccessToken)        { $authModes += 'AccessToken' }
if ($UseManagedIdentity) { $authModes += 'ManagedIdentity' }
if ($CertificateThumbprint -or $Certificate -or $CertificatePath) { $authModes += 'Certificate' }
if ($ClientSecret -or $env:A365_CLIENT_SECRET) { $authModes += 'ClientSecret' }

if ($authModes.Count -eq 0) {
    throw 'No authentication method was specified. To run as an application pass -ClientId with -ClientSecret, -CertificateThumbprint, -Certificate or -CertificatePath (or use -UseManagedIdentity / -AccessToken). To sign in as a user pass -Interactive.'
}
if ($authModes.Count -gt 1) {
    throw "Conflicting authentication options ($($authModes -join ', ')). Supply exactly one."
}
$authMode  = $authModes[0]
$isAppOnly = $authMode -in @('ClientSecret', 'Certificate', 'ManagedIdentity')

# New-A365AgentUser.ps1 is client-credentials only: it has no -Interactive and no
# -SkipPermissionCheck parameter. Reject the impossible combination now rather than letting
# phases 1-4 create objects and then failing on a parameter that does not exist.
if (($runAgentUser -or $updAgentUser) -and -not $isAppOnly) {
    $auSwitch = if ($runAgentUser) { '-NewAgentUser' } else { '-UpdateAgentUser' }
    throw "$auSwitch requires app-only authentication, but this run authenticates as '$authMode'. " +
          'New-A365AgentUser.ps1 supports client secret, certificate and managed identity only. ' +
          'Re-run with -ClientId plus -ClientSecret / -CertificateThumbprint / -UseManagedIdentity, ' +
          'and use -AgentRegistrationAuth Interactive if the registry step needs a signed-in user.'
}

# Shared auth splat, forwarded verbatim to each step.
$authSplat = @{}
foreach ($k in 'ClientId', 'ClientSecret', 'CertificateThumbprint', 'Certificate', 'CertificatePath',
               'CertificatePassword', 'UseManagedIdentity', 'AccessToken', 'Interactive', 'SkipPermissionCheck') {
    if ($PSBoundParameters.ContainsKey($k)) { $authSplat[$k] = $PSBoundParameters[$k] }
}

# Step 4 authenticates separately when asked. The registry APIs read ownerIds/createdBy from
# /me, so an app-only token often cannot drive them at all.
$registrationAuthSplat = $authSplat
if ($AgentRegistrationAuth -eq 'Interactive') {
    $registrationAuthSplat = @{ Interactive = $true }
    if ($ClientId)            { $registrationAuthSplat.ClientId            = $ClientId }
    if ($SkipPermissionCheck) { $registrationAuthSplat.SkipPermissionCheck = $true }
}

# Phase 3 takes the same credentials minus the two parameters its script does not declare.
# Splatting an undeclared parameter is a hard bind error, so filter rather than forward.
$agentUserAuthSplat = @{}
foreach ($k in $authSplat.Keys) {
    if ($k -in @('Interactive', 'SkipPermissionCheck')) { continue }
    $agentUserAuthSplat[$k] = $authSplat[$k]
}

# The removal scripts declare a narrower auth surface again: no -Certificate, -CertificatePath,
# -CertificatePassword, -UseManagedIdentity or -SkipPermissionCheck. Forward only what they
# accept, for the same reason - an undeclared parameter is a hard bind error, and it would
# surface only once a destructive phase had already started.
$removalAuthSplat = @{}
foreach ($k in $authSplat.Keys) {
    if ($k -in @('ClientId', 'ClientSecret', 'CertificateThumbprint', 'AccessToken', 'Interactive')) {
        $removalAuthSplat[$k] = $authSplat[$k]
    }
}

# Every phase merges one of these splats into its argument hash, so adding the log settings
# here reaches all of them - creates, updates and removals alike - without touching each
# phase individually. Each child generates its OWN file name from its own script name and
# start time, so one run of the orchestrator produces one log per script that actually ran,
# all in the same directory, plus the orchestrator's own.
if ($PSBoundParameters.ContainsKey('LogPath')) {
    foreach ($splat in @($authSplat, $agentUserAuthSplat, $registrationAuthSplat, $removalAuthSplat)) {
        $splat['LogPath'] = $LogPath
        $splat['LogCorrelationId'] = $LogCorrelationId
        if ($LogIncludeSecrets) { $splat['LogIncludeSecrets'] = $true }
    }
}

Write-Host ''
Write-Host '=== Agent 365 provisioning pipeline ===' -ForegroundColor Cyan
Write-Host ("  Tenant             : {0}" -f $TenantId)
Write-Host ("  Authentication     : {0}{1}" -f $authMode, $(if ($isAppOnly) { ' (app-only)' } else { ' (delegated)' }))
$creatingList = @(
    if ($runBlueprint)    { 'blueprint' }
    if ($runIdentity)     { 'agent identity' }
    if ($runAgentUser)    { 'agent user' }
    if ($runRegistration) { 'registration' }
)
$updatingList = @(
    if ($updBlueprint)    { 'blueprint' }
    if ($updIdentity)     { 'agent identity' }
    if ($updAgentUser)    { 'agent user' }
    if ($updRegistration) { 'registration' }
)
$removingList = @(
    if ($rmRegistration) { 'registration' }
    if ($rmAgentUser)    { 'agent user' }
    if ($rmIdentity)     { 'agent identity' }
    if ($rmBlueprint)    { 'blueprint' }
)
if ($creatingList.Count -gt 0) {
    Write-Host ("  Creating           : {0}" -f ($creatingList -join ', '))
}
if ($updatingList.Count -gt 0) {
    Write-Host ("  Updating           : {0}" -f ($updatingList -join ', '))
    Write-Host '                       (only the attributes you passed are written)' -ForegroundColor DarkGray
}
if ($removingList.Count -gt 0) {
    $verb = if ($RemoveInspectOnly) { 'Inspecting (no delete)' } else { 'REMOVING' }
    Write-Host ("  {0,-19}: {1}" -f $verb, ($removingList -join ', ')) -ForegroundColor $(if ($RemoveInspectOnly) { 'Gray' } else { 'Red' })
    if (-not $RemoveInspectOnly) {
        Write-Host '                       deletion is a soft delete; add -RemovePermanent to purge' -ForegroundColor DarkGray
        Write-Host '                       run with -RemoveInspectOnly first to see the plan' -ForegroundColor DarkGray
    }
}
Write-Host ''

# The per-object lines below describe what happens to each object, across all three axes. A
# removal is reported here too: without it a run that deletes the blueprint printed
# "Blueprint : not needed", which reads as though nothing touches it.
$rmVerb = if ($RemoveInspectOnly) { 'INSPECT' } else { 'REMOVE ' }

Write-Host ("  Blueprint          : {0}" -f $(
    if ($runBlueprint)               { "CREATE  $bpName" }
    elseif ($updBlueprint)           { "UPDATE  $UpdateBlueprint" }
    elseif ($rmBlueprint)            { "$rmVerb $RemoveBlueprint" }
    elseif ($existingBlueprintAppId) { "existing  $existingBlueprintAppId" }
    else                             { 'not needed' }))
Write-Host ("  Agent identity     : {0}" -f $(
    if ($runIdentity)                 { "CREATE  $aiName" }
    elseif ($updIdentity)             { "UPDATE  $UpdateAgentIdentity" }
    elseif ($rmIdentity)              { "$rmVerb $RemoveAgentIdentity" }
    elseif ($existingAgentIdentityId) { "existing  $existingAgentIdentityId" }
    else                              { 'not needed' }))
Write-Host ("  Agent user         : {0}" -f $(
    if ($runAgentUser)     { "CREATE  $auName  <$AgentUserPrincipalName>" }
    elseif ($updAgentUser) { "UPDATE  $UpdateAgentUser" }
    elseif ($rmAgentUser)  { "$rmVerb $RemoveAgentUser" }
    else                   { 'not selected' }))
if ($runAgentUser) {
    Write-Host ("     bound to identity: {0}" -f $(if ($existingAgentIdentityId) { $existingAgentIdentityId } else { 'the one created in this run' }))
    if ($AgentUserManagerUserId -or $AgentUserManagerUpn) {
        Write-Host ("     manager          : {0}" -f $(if ($AgentUserManagerUserId) { $AgentUserManagerUserId } else { $AgentUserManagerUpn }))
    }
    # POST /beta/users for an agentUser is authorized by AgentIdUser.ReadWrite.All specifically.
    # User.ReadWrite.All does NOT cover it, the step script does not pre-check its token, and the
    # refusal is a bare "Insufficient privileges" naming no permission - so say it here, while the
    # run has not yet created anything.
    Write-Host '     needs AgentIdUser.ReadWrite.All (New-A365AutomationApp.ps1 -Scenario AgentUser).' -ForegroundColor DarkGray
    Write-Host '     User.ReadWrite.All alone does not authorize agent user creation.' -ForegroundColor DarkGray
}
Write-Host ("  Registration       : {0}" -f $(
    if ($runRegistration)     { "CREATE  $regName  [auth: $AgentRegistrationAuth]" }
    elseif ($updRegistration) { "UPDATE  $UpdateAgentRegistration  [auth: $AgentRegistrationAuth]" }
    elseif ($rmRegistration)  { "$rmVerb $RemoveAgentRegistration" }
    else                      { 'not selected' }))

if ($OutputJsonPath) {
    Write-Host ("  Report             : {0}" -f $OutputJsonPath)
    if ($IncludeBlueprintSecretsInOutput) {
        Write-Host '                       -IncludeSecrets: the report will contain the client secret in plaintext.' -ForegroundColor Yellow
    }
}

$script:RunStartUtc      = [DateTimeOffset]::UtcNow
$script:CreatedSecretCount = 0
$script:RunSummary       = [ordered]@{}
$script:RunContext = [ordered]@{
    tenantId       = $TenantId
    authMode       = $authMode
    isAppOnly      = $isAppOnly
    clientId       = $ClientId
    whatIf         = [bool]$WhatIfPreference
    startedUtc     = $script:RunStartUtc.ToString('o')
    completedUtc   = $null
    durationSeconds = $null
    scriptRoot     = $root
    plannedNames   = [ordered]@{
        blueprint    = $(if ($runBlueprint)    { $bpName }  else { $null })
        agentIdentity = $(if ($runIdentity)     { $aiName }  else { $null })
        agentUser    = $(if ($runAgentUser)    { $auName }  else { $null })
        registration = $(if ($runRegistration) { $regName } else { $null })
    }
    plannedPhases  = [ordered]@{
        blueprint    = $runBlueprint
        agentIdentity = $runIdentity
        agentUser    = $runAgentUser
        registration = $runRegistration
    }
    existingInput  = [ordered]@{
        blueprintAppId  = $existingBlueprintAppId
        agentIdentityId = $existingAgentIdentityId
    }
    invocation     = [ordered]@{
        command    = $MyInvocation.MyCommand.Path
        parameters = (ConvertTo-ReportParameterMap $PSBoundParameters)
    }
    host           = [ordered]@{
        psVersion   = $PSVersionTable.PSVersion.ToString()
        os          = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription.Trim()
        machineName = [System.Environment]::MachineName
        userName    = [System.Environment]::UserName
    }
}

if ($isAppOnly -and $runRegistration -and $AgentRegistrationAuth -eq 'Same') {
    if (-not $AgentRegistrationOwnerId -and -not $AgentRegistrationOwner) {
        Write-Warning 'Step 4 running app-only needs an owner, because it cannot resolve one from /me. Pass -AgentRegistrationOwner (resolved through Graph) or -AgentRegistrationOwnerId.'
    }
}

# ---------------------------------------------------------------------------
# Phases 1-4
#
# Wrapped so that the summary and the run report are produced even when a phase throws. A
# failed run is exactly when the report of what DID get created is most valuable - the
# blueprint and identity survive a later-phase failure and must not be lost.
# ---------------------------------------------------------------------------

$blueprint            = $null
$resolvedBpAppId      = $existingBlueprintAppId
$blueprintSecret      = $null
$identity             = $null
$resolvedIdentityId   = $existingAgentIdentityId
$registration         = $null
$agentUser            = $null
$resolvedAgentUserId  = $null

try {

# ---------------------------------------------------------------------------
# Phase 1 - blueprint
# ---------------------------------------------------------------------------

if ($runBlueprint) {
    Write-Phase 1 "Blueprint  ->  $bpName"
    $bpPhase = Start-PhaseRecord -Number 1 -Name 'Blueprint' -Script $blueprintScript

    $bpArgs = @{
        TenantId    = $TenantId
        DisplayName = $bpName
        Sponsor     = $BlueprintSponsor
    }
    if ($BlueprintOwner)                  { $bpArgs.Owner                  = $BlueprintOwner }
    if ($BlueprintRequireOwnerAssignment) { $bpArgs.RequireOwnerAssignment = $true }
    if ($BlueprintDescription)            { $bpArgs.Description            = $BlueprintDescription }
    if ($BlueprintNewClientSecret)         { $bpArgs.NewClientSecret         = $true }
    if ($BlueprintKeyVaultName)           { $bpArgs.KeyVaultName           = $BlueprintKeyVaultName }
    if ($BlueprintKeyVaultSecretName)     { $bpArgs.KeyVaultSecretName     = $BlueprintKeyVaultSecretName }
    if ($KeyVaultAccessToken)             { $bpArgs.KeyVaultAccessToken    = $KeyVaultAccessToken }
    if ($BlueprintManagedIdentityPrincipalId) { $bpArgs.ManagedIdentityPrincipalId = $BlueprintManagedIdentityPrincipalId }
    if ($BlueprintGrantAdminConsent)          { $bpArgs.GrantAdminConsent          = $true }
    if ($BlueprintSkipInheritablePermissions) { $bpArgs.SkipInheritablePermissions = $true }
    # Only forward the permission set when the caller actually supplied it. The blueprint script
    # tests $PSBoundParameters.ContainsKey('RequiredPermission') to decide whether to apply its
    # own defaults, so passing an empty array would silently replace them with nothing.
    # Note the two names: our -BlueprintRequiredPermission becomes its -RequiredPermission.
    if ($PSBoundParameters.ContainsKey('BlueprintRequiredPermission')) { $bpArgs.RequiredPermission = $BlueprintRequiredPermission }
    foreach ($k in $authSplat.Keys)          { $bpArgs[$k] = $authSplat[$k] }
    foreach ($k in $BlueprintParameter.Keys) { $bpArgs[$k] = $BlueprintParameter[$k] }


    $bpPhase.parameters = ConvertTo-ReportParameterMap $bpArgs

    try {
        $blueprint = Select-StepSummary -Output (& $blueprintScript @bpArgs -WhatIf:$WhatIfPreference) -RequiredProperty 'blueprintAppId'
    }
    catch {
        Add-StepResult 'Blueprint' 'Failed' $_.Exception.Message
        Complete-PhaseRecord -Record $bpPhase -Status 'Failed' -Detail $_.Exception.Message -ErrorRecord $_
        throw "Phase 1 (blueprint) failed: $($_.Exception.Message)"
    }

    if (-not $blueprint) {
        if ($WhatIfPreference) {
            Write-Host ''
            Write-Host '[WhatIf] The blueprint was not created, so the later phases cannot be simulated.' -ForegroundColor Yellow
            Add-StepResult 'Blueprint' 'WhatIf' $bpName
            Complete-PhaseRecord -Record $bpPhase -Status 'WhatIf' -Detail $bpName
            if ($runIdentity) {
                Add-StepResult 'AgentIdentity' 'Skipped' 'needs a real blueprint appId'
                Add-SkippedPhaseRecord -Number 2 -Name 'AgentIdentity' -Script $identityScript -Reason 'needs a real blueprint appId' | Out-Null
            }
            if ($runAgentUser) {
                Add-StepResult 'AgentUser' 'Skipped' 'needs a real agent identity id'
                Add-SkippedPhaseRecord -Number 3 -Name 'AgentUser' -Script $agentUserScript -Reason 'needs a real agent identity id' | Out-Null
            }
            if ($runRegistration) {
                Add-StepResult 'Registration' 'Skipped' 'needs a real agent identity id'
                Add-SkippedPhaseRecord -Number 4 -Name 'Registration' -Script $registrationScript -Reason 'needs a real agent identity id' | Out-Null
            }
            return
        }
        Complete-PhaseRecord -Record $bpPhase -Status 'Unknown' -Detail 'no summary object returned'
        throw 'Phase 1 (blueprint) returned no summary object. Re-run New-A365AgentBlueprint.ps1 on its own to see what happened.'
    }

    $resolvedBpAppId = Get-PropertyOrDefault $blueprint 'blueprintAppId'
    $blueprintSecret = Get-PropertyOrDefault $blueprint 'clientSecret'
    if (-not $resolvedBpAppId) {
        Complete-PhaseRecord -Record $bpPhase -Status 'Unknown' -Detail 'no blueprintAppId returned' -Result $blueprint
        throw 'Phase 1 (blueprint) did not return a blueprintAppId.'
    }

    # A wrong-typed blueprint principal makes every later phase fail with a confusing 400, so stop
    # here with the actual cause instead of letting phase 2 inherit it.
    if ((Get-PropertyOrDefault $blueprint 'blueprintPrincipalIsTyped' $true) -eq $false) {
        $staleId = Get-PropertyOrDefault $blueprint 'blueprintPrincipalId' '(unknown)'
        Complete-PhaseRecord -Record $bpPhase -Status 'Failed' -Detail 'blueprint principal is not typed' -Result $blueprint `
            -Identifiers @{ blueprintAppId = $resolvedBpAppId; blueprintPrincipalId = $staleId }
        throw ("Phase 1 (blueprint) produced a service principal that is NOT an " +
               "agentIdentityBlueprintPrincipal ($staleId). Agent identities cannot be created " +
               "from it. Delete it and re-run:  Remove-MgServicePrincipal -ServicePrincipalId $staleId")
    }

    if ($blueprintSecret) {
        $script:CreatedSecretCount++
        $secretRecord = [ordered]@{ clientSecret = $blueprintSecret }
        $secretDetail = Get-PropertyOrDefault $blueprint 'clientSecretDetail'
        if ($secretDetail) {
            foreach ($property in $secretDetail.PSObject.Properties) { $secretRecord[$property.Name] = $property.Value }
        }
        $secretRecord['usedForClientId'] = $resolvedBpAppId
        $secretRecord['tokenEndpoint']   = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"
        $bpPhase.secrets = $secretRecord
    }

    Add-StepResult 'Blueprint' 'Completed' $resolvedBpAppId
    Complete-PhaseRecord -Record $bpPhase -Status 'Completed' -Detail $resolvedBpAppId -Result $blueprint -Identifiers @{
        blueprintAppId        = $resolvedBpAppId
        blueprintObjectId     = (Get-PropertyOrDefault $blueprint 'blueprintObjectId')
        blueprintPrincipalId  = (Get-PropertyOrDefault $blueprint 'blueprintPrincipalId')
        identifierUri         = (Get-PropertyOrDefault $blueprint 'identifierUri')
        sponsorIds            = (Get-PropertyOrDefault $blueprint 'sponsorIds')
        sponsorId             = (Get-PropertyOrDefault $blueprint 'sponsorId')
        assignedOwnerIds      = (Get-PropertyOrDefault $blueprint 'assignedOwnerIds')
    }
    Write-Host ''
    Write-Host "  -> blueprint appId $resolvedBpAppId" -ForegroundColor Green
}
elseif ($existingBlueprintAppId -and -not $updBlueprint) {
    Add-StepResult 'Blueprint' 'Skipped' "using existing $existingBlueprintAppId"
    Add-SkippedPhaseRecord -Number 1 -Name 'Blueprint' -Script $blueprintScript `
        -Reason "using existing blueprint appId $existingBlueprintAppId" -Identifiers @{ blueprintAppId = $existingBlueprintAppId } | Out-Null
}
elseif (-not $updBlueprint) {
    Add-SkippedPhaseRecord -Number 1 -Name 'Blueprint' -Script $blueprintScript `
        -Reason '-NewBlueprint was not selected' | Out-Null
}

if ($updBlueprint) {
    $bpUpdArgs = @{
        TenantId    = $TenantId
        Update      = $true
        BlueprintId = $UpdateBlueprint
    }
    if ($PSBoundParameters.ContainsKey('BlueprintDisplayName')) { $bpUpdArgs.DisplayName = $BlueprintDisplayName }
    if ($PSBoundParameters.ContainsKey('BlueprintDescription')) { $bpUpdArgs.Description = $BlueprintDescription }
    if ($PSBoundParameters.ContainsKey('BlueprintSponsor'))     { $bpUpdArgs.Sponsor     = $BlueprintSponsor }
    if ($PSBoundParameters.ContainsKey('BlueprintOwner'))       { $bpUpdArgs.Owner       = $BlueprintOwner }
    if ($BlueprintRequireOwnerAssignment)     { $bpUpdArgs.RequireOwnerAssignment     = $true }
    if ($BlueprintNewClientSecret)         { $bpUpdArgs.NewClientSecret         = $true }
    # The update path creates a client secret exactly as the create path does, so it needs the
    # same Key Vault forwarding. Omitting these here made -BlueprintKeyVaultName a SILENT no-op
    # on an update: the secret was created and printed to the console but never stored.
    if ($BlueprintKeyVaultName)           { $bpUpdArgs.KeyVaultName           = $BlueprintKeyVaultName }
    if ($BlueprintKeyVaultSecretName)     { $bpUpdArgs.KeyVaultSecretName     = $BlueprintKeyVaultSecretName }
    if ($KeyVaultAccessToken)             { $bpUpdArgs.KeyVaultAccessToken    = $KeyVaultAccessToken }
    if ($BlueprintGrantAdminConsent)          { $bpUpdArgs.GrantAdminConsent          = $true }
    if ($BlueprintSkipInheritablePermissions) { $bpUpdArgs.SkipInheritablePermissions = $true }
    if ($BlueprintManagedIdentityPrincipalId) { $bpUpdArgs.ManagedIdentityPrincipalId = $BlueprintManagedIdentityPrincipalId }
    if ($PSBoundParameters.ContainsKey('BlueprintRequiredPermission')) { $bpUpdArgs.RequiredPermission = $BlueprintRequiredPermission }
    foreach ($k in $authSplat.Keys)          { $bpUpdArgs[$k] = $authSplat[$k] }
    foreach ($k in $BlueprintParameter.Keys) { $bpUpdArgs[$k] = $BlueprintParameter[$k] }

    $blueprint = Invoke-UpdatePhase -Number 1 -Name 'Blueprint' -Title "Blueprint update  ->  $UpdateBlueprint" `
        -ScriptPath $blueprintScript -Arguments $bpUpdArgs -SummaryProperty 'blueprintAppId' -Target $UpdateBlueprint

    if ($blueprint) {
        $resolvedBpAppId = Get-PropertyOrDefault $blueprint 'blueprintAppId'
        $blueprintSecret = Get-PropertyOrDefault $blueprint 'clientSecret'
        if ($blueprintSecret) { $script:CreatedSecretCount++ }
    }
}

# ---------------------------------------------------------------------------
# Phase 2 - agent identity
# ---------------------------------------------------------------------------

if ($runIdentity) {
    Write-Phase 2 "Agent identity  ->  $aiName"
    $aiPhase = Start-PhaseRecord -Number 2 -Name 'AgentIdentity' -Script $identityScript

    $aiArgs = @{
        TenantId       = $TenantId
        DisplayName    = $aiName
        BlueprintAppId = $resolvedBpAppId
        Sponsor        = $AgentIdentitySponsor
    }
    if ($AgentIdentityOwner)                  { $aiArgs.Owner                  = $AgentIdentityOwner }
    if ($AgentIdentityRequireOwnerAssignment) { $aiArgs.RequireOwnerAssignment = $true }
    if ($AgentIdentityTag)                    { $aiArgs.Tag                    = $AgentIdentityTag }
    if ($PSBoundParameters.ContainsKey('AgentIdentityCustomSecurityAttribute')) {
        $aiArgs.CustomSecurityAttribute = $AgentIdentityCustomSecurityAttribute
    }
    if ($AgentIdentitySkipCustomSecurityAttributeValidation) {
        $aiArgs.SkipCustomSecurityAttributeValidation = $true
    }
    # Consent here means something different from consent on the blueprint: the identity script
    # grants its own -RequiredPermission directly on this one identity, rather than consenting the
    # blueprint's permissions for every identity to inherit. Hence two separate switches.
    if ($AgentIdentityGrantAdminConsent) { $aiArgs.GrantAdminConsent = $true }
    if ($PSBoundParameters.ContainsKey('AgentIdentityRequiredPermission')) { $aiArgs.RequiredPermission = $AgentIdentityRequiredPermission }
    foreach ($k in $authSplat.Keys)             { $aiArgs[$k] = $authSplat[$k] }
    foreach ($k in $AgentIdentityParameter.Keys) { $aiArgs[$k] = $AgentIdentityParameter[$k] }

    $aiPhase.parameters = ConvertTo-ReportParameterMap $aiArgs

    try {
        $identity = Select-StepSummary -Output (& $identityScript @aiArgs -WhatIf:$WhatIfPreference) -RequiredProperty 'agentIdentityId'
    }
    catch {
        Add-StepResult 'AgentIdentity' 'Failed' $_.Exception.Message
        Complete-PhaseRecord -Record $aiPhase -Status 'Failed' -Detail $_.Exception.Message -ErrorRecord $_
        throw "Phase 2 (agent identity) failed: $($_.Exception.Message)"
    }

    if (-not $identity) {
        if ($WhatIfPreference) {
            Write-Host ''
            Write-Host '[WhatIf] The agent identity was not created, so the later phases cannot be simulated.' -ForegroundColor Yellow
            Add-StepResult 'AgentIdentity' 'WhatIf' $aiName
            Complete-PhaseRecord -Record $aiPhase -Status 'WhatIf' -Detail $aiName
            if ($runAgentUser) {
                Add-StepResult 'AgentUser' 'Skipped' 'needs a real agent identity id'
                Add-SkippedPhaseRecord -Number 3 -Name 'AgentUser' -Script $agentUserScript -Reason 'needs a real agent identity id' | Out-Null
            }
            if ($runRegistration) {
                Add-StepResult 'Registration' 'Skipped' 'needs a real agent identity id'
                Add-SkippedPhaseRecord -Number 4 -Name 'Registration' -Script $registrationScript -Reason 'needs a real agent identity id' | Out-Null
            }
            return
        }
        Complete-PhaseRecord -Record $aiPhase -Status 'Unknown' -Detail 'no summary object returned'
        throw 'Phase 2 (agent identity) returned no summary object. Re-run New-A365AgentIdentity.ps1 on its own to see what happened.'
    }

    $resolvedIdentityId = Get-PropertyOrDefault $identity 'agentIdentityId'
    if (-not $resolvedIdentityId) {
        Complete-PhaseRecord -Record $aiPhase -Status 'Unknown' -Detail 'no agentIdentityId returned' -Result $identity
        throw 'Phase 2 (agent identity) did not return an agentIdentityId.'
    }

    Add-StepResult 'AgentIdentity' 'Completed' $resolvedIdentityId
    Complete-PhaseRecord -Record $aiPhase -Status 'Completed' -Detail $resolvedIdentityId -Result $identity -Identifiers @{
        agentIdentityId          = $resolvedIdentityId
        agentIdentityClientId    = (Get-PropertyOrDefault $identity 'agentIdentityClientId')
        fmiPath                  = (Get-PropertyOrDefault $identity 'fmiPath')
        agentIdentityBlueprintId = (Get-PropertyOrDefault $identity 'agentIdentityBlueprintId')
        blueprintPrincipalId     = (Get-PropertyOrDefault $identity 'blueprintPrincipalId')
    }
    Write-Host ''
    Write-Host "  -> agent identity $resolvedIdentityId" -ForegroundColor Green
}
elseif ($existingAgentIdentityId -and -not $updIdentity) {
    Add-StepResult 'AgentIdentity' 'Skipped' "using existing $existingAgentIdentityId"
    Add-SkippedPhaseRecord -Number 2 -Name 'AgentIdentity' -Script $identityScript `
        -Reason "using existing agent identity $existingAgentIdentityId" -Identifiers @{ agentIdentityId = $existingAgentIdentityId } | Out-Null
}
elseif (-not $updIdentity) {
    Add-SkippedPhaseRecord -Number 2 -Name 'AgentIdentity' -Script $identityScript `
        -Reason '-NewAgentIdentity was not selected' | Out-Null
}

if ($updIdentity) {
    $aiUpdArgs = @{
        TenantId        = $TenantId
        Update          = $true
        AgentIdentityId = $UpdateAgentIdentity
    }
    if ($PSBoundParameters.ContainsKey('AgentIdentityDisplayName')) { $aiUpdArgs.DisplayName = $AgentIdentityDisplayName }
    if ($PSBoundParameters.ContainsKey('AgentIdentitySponsor'))     { $aiUpdArgs.Sponsor     = $AgentIdentitySponsor }
    if ($PSBoundParameters.ContainsKey('AgentIdentityOwner'))       { $aiUpdArgs.Owner       = $AgentIdentityOwner }
    if ($PSBoundParameters.ContainsKey('AgentIdentityTag'))         { $aiUpdArgs.Tag         = $AgentIdentityTag }
    if ($PSBoundParameters.ContainsKey('AgentIdentityCustomSecurityAttribute')) {
        $aiUpdArgs.CustomSecurityAttribute = $AgentIdentityCustomSecurityAttribute
    }
    if ($AgentIdentitySkipCustomSecurityAttributeValidation) { $aiUpdArgs.SkipCustomSecurityAttributeValidation = $true }
    if ($AgentIdentityRequireOwnerAssignment)                { $aiUpdArgs.RequireOwnerAssignment                = $true }
    if ($AgentIdentityGrantAdminConsent)                     { $aiUpdArgs.GrantAdminConsent                     = $true }
    if ($PSBoundParameters.ContainsKey('AgentIdentityRequiredPermission')) { $aiUpdArgs.RequiredPermission = $AgentIdentityRequiredPermission }
    foreach ($k in $authSplat.Keys)             { $aiUpdArgs[$k] = $authSplat[$k] }
    foreach ($k in $AgentIdentityParameter.Keys) { $aiUpdArgs[$k] = $AgentIdentityParameter[$k] }

    $identity = Invoke-UpdatePhase -Number 2 -Name 'AgentIdentity' -Title "Agent identity update  ->  $UpdateAgentIdentity" `
        -ScriptPath $identityScript -Arguments $aiUpdArgs -SummaryProperty 'agentIdentityId' -Target $UpdateAgentIdentity

    $resolvedIdentityId = if ($identity) { Get-PropertyOrDefault $identity 'agentIdentityId' $UpdateAgentIdentity } else { $UpdateAgentIdentity }
}

# ---------------------------------------------------------------------------
# Phase 3 - agent user
#
# A sibling of phase 4, not a predecessor: it needs only the agent identity, so a failure here
# does not stop the registration running afterwards.
# ---------------------------------------------------------------------------

if ($runAgentUser) {
    Write-Phase 3 "Agent user  ->  $AgentUserPrincipalName"
    $auPhase = Start-PhaseRecord -Number 3 -Name 'AgentUser' -Script $agentUserScript

    $auArgs = @{
        TenantId          = $TenantId
        AgentIdentityId   = $resolvedIdentityId
        UserPrincipalName = $AgentUserPrincipalName
        PassThru          = $true   # without it the script returns nothing at all
    }
    # -DisplayName is optional on the step script, which falls back to the UPN's local part.
    # Forward it only when asked, so that fallback is reachable now the base name is gone.
    if ($auName)                { $auArgs.DisplayName          = $auName }
    if ($AgentUserMailNickname) { $auArgs.MailNickname         = $AgentUserMailNickname }
    if ($AgentUserManagerUserId)         { $auArgs.ManagerUserId        = $AgentUserManagerUserId }
    if ($AgentUserManagerUpn)            { $auArgs.ManagerUpn           = $AgentUserManagerUpn }
    if ($AgentUserUsageLocation)         { $auArgs.UsageLocation        = $AgentUserUsageLocation }
    if ($AgentUserAssignLicense)         { $auArgs.AssignLicense        = $true }
    if ($AgentUserLicenseSkuId)          { $auArgs.LicenseSkuId         = $AgentUserLicenseSkuId }
    if ($AgentUserLicenseSkuPartNumber)  { $auArgs.LicenseSkuPartNumber = $AgentUserLicenseSkuPartNumber }
    foreach ($k in $agentUserAuthSplat.Keys) { $auArgs[$k] = $agentUserAuthSplat[$k] }
    foreach ($k in $AgentUserParameter.Keys) { $auArgs[$k] = $AgentUserParameter[$k] }

    $auPhase.parameters = ConvertTo-ReportParameterMap $auArgs

    # New-A365AgentUser.ps1 handles its own errors and ends with 'exit 1' instead of
    # rethrowing, so a failure arrives as a non-zero exit code with no exception. Catching
    # alone would silently read that as "no summary returned", so check both.
    $global:LASTEXITCODE = 0
    $auFailed = $false
    try {
        $agentUser = Select-StepSummary -Output (& $agentUserScript @auArgs -WhatIf:$WhatIfPreference) -RequiredProperty 'AgentUserId'
    }
    catch {
        $auFailed = $true
        Add-StepResult 'AgentUser' 'Failed' $_.Exception.Message
        Complete-PhaseRecord -Record $auPhase -Status 'Failed' -Detail $_.Exception.Message -ErrorRecord $_
        Write-Warning "Phase 3 (agent user) failed: $($_.Exception.Message)"
    }

    if (-not $auFailed -and $LASTEXITCODE -ne 0) {
        $auFailed = $true
        $detail = "New-A365AgentUser.ps1 exited with code $LASTEXITCODE. Its own error output above has the detail."
        Add-StepResult 'AgentUser' 'Failed' $detail
        Complete-PhaseRecord -Record $auPhase -Status 'Failed' -Detail $detail
        Write-Warning "Phase 3 (agent user) failed: $detail"
    }

    if ($auFailed) {
        # The step script does not pre-check its token, so the commonest cause is a missing app
        # role that Graph reports only as "Insufficient privileges", naming nothing.
        Write-Host '  Common causes:' -ForegroundColor Gray
        Write-Host '    * Missing AgentIdUser.ReadWrite.All. User.ReadWrite.All does NOT authorize' -ForegroundColor Gray
        Write-Host '      POST /beta/users for an agentUser, and the 403 names no permission.' -ForegroundColor Gray
        Write-Host '      Fix: New-A365AutomationApp.ps1 -Scenario AgentUser  (it grants by default)' -ForegroundColor Gray
        Write-Host '    * The UPN is taken, or its domain is not a verified domain in this tenant.' -ForegroundColor Gray
        Write-Host '    * A licence was requested without an available seat of that SKU.' -ForegroundColor Gray
        Write-Host '  The blueprint and agent identity are unaffected, and the registration phase' -ForegroundColor Gray
        Write-Host '  still runs after this one.' -ForegroundColor Gray
    }

    if (-not $auFailed) {
        if ($agentUser) {
            $resolvedAgentUserId = Get-PropertyOrDefault $agentUser 'AgentUserId'
            $existed  = (Get-PropertyOrDefault $agentUser 'UserAlreadyExisted' $false) -eq $true

            # The step script reports the manager and the licence in its RESULT rather than
            # throwing: Set-AgentUserManager warns and returns false, and licensing behaves the
            # same way. So the agent user can exist while something explicitly asked for did not
            # happen. Reporting that as a clean success is precisely the false-success trap.
            $managerRequested = [bool]($AgentUserManagerUserId -or $AgentUserManagerUpn -or
                                       $AgentUserParameter.ContainsKey('ManagerUserId') -or
                                       $AgentUserParameter.ContainsKey('ManagerUpn'))
            $managerAssigned  = (Get-PropertyOrDefault $agentUser 'ManagerAssigned' $false) -eq $true
            $licenseRequested = [bool]($AgentUserAssignLicense -or $AgentUserParameter.ContainsKey('AssignLicense'))
            $licenseAssigned  = (Get-PropertyOrDefault $agentUser 'LicenseAssigned' $false) -eq $true

            $unmet = @()
            if ($managerRequested -and -not $managerAssigned) { $unmet += 'manager not assigned' }
            if ($licenseRequested -and -not $licenseAssigned) { $unmet += 'licence not assigned' }

            $auStatus = if ($unmet.Count -gt 0) { 'Partial' }
                        elseif ($existed)       { 'AlreadyExisted' }
                        else                    { 'Completed' }
            $auDetail = if ($unmet.Count -gt 0) { "$resolvedAgentUserId ($($unmet -join '; '))" }
                        else                    { $resolvedAgentUserId }

            Add-StepResult 'AgentUser' $auStatus $auDetail
            Complete-PhaseRecord -Record $auPhase -Status $auStatus -Detail $auDetail -Result $agentUser -Identifiers @{
                agentUserId       = $resolvedAgentUserId
                userPrincipalName = (Get-PropertyOrDefault $agentUser 'UserPrincipalName')
                agentIdentityId   = (Get-PropertyOrDefault $agentUser 'AgentIdentityId')
                licenseAssigned   = (Get-PropertyOrDefault $agentUser 'LicenseAssigned')
                licenseSkuId      = (Get-PropertyOrDefault $agentUser 'LicenseSkuId')
                managerRequested  = $managerRequested
                managerAssigned   = (Get-PropertyOrDefault $agentUser 'ManagerAssigned')
            }
            Write-Host ''
            Write-Host "  -> agent user $resolvedAgentUserId$(if ($existed) { ' (existing)' })" -ForegroundColor Green
            if ($managerRequested) {
                Write-Host ("  -> manager {0}" -f $(if ($managerAssigned) { 'assigned' } else { 'NOT assigned' })) `
                    -ForegroundColor $(if ($managerAssigned) { 'Green' } else { 'Yellow' })
            }

            if ($managerRequested -and -not $managerAssigned) {
                Write-Warning 'The agent user was created but the manager was not assigned. New-A365AgentUser.ps1 reports this without failing, so the user exists unmanaged.'
                Write-Host '    The manager resolved (an unresolvable one would have failed before creation),' -ForegroundColor Gray
                Write-Host '    so the PUT /users/{id}/manager/$ref itself was refused - usually a missing' -ForegroundColor Gray
                Write-Host '    User.ReadWrite.All application role.' -ForegroundColor Gray
                Write-Host ("    Retry just this: New-A365AgentUser.ps1 -AgentIdentityId {0} -UserPrincipalName {1} -ManagerUpn ..." -f $resolvedIdentityId, $AgentUserPrincipalName) -ForegroundColor Gray
            }
            if ($licenseRequested -and -not $licenseAssigned) {
                Write-Warning 'The agent user was created but the licence was not assigned. Check the usage location and that the SKU has a free seat.'
            }
        }
        elseif ($WhatIfPreference) {
            Add-StepResult 'AgentUser' 'WhatIf' $AgentUserPrincipalName
            Complete-PhaseRecord -Record $auPhase -Status 'WhatIf' -Detail $AgentUserPrincipalName
        }
        else {
            Add-StepResult 'AgentUser' 'Unknown' 'no result object returned'
            Complete-PhaseRecord -Record $auPhase -Status 'Unknown' -Detail 'no result object returned'
            Write-Warning 'Phase 3 returned no summary object despite exiting cleanly. Re-run New-A365AgentUser.ps1 on its own to see what happened.'
        }
    }
}
elseif ($agentUserScript -and -not $updAgentUser) {
    # Not listed in the steps table: with explicit scenario switches, a phase you did not ask
    # for is simply absent, exactly as the other three are. The report still records it so a
    # run can be told apart from one where the script was missing.
    Add-SkippedPhaseRecord -Number 3 -Name 'AgentUser' -Script $agentUserScript -Reason '-NewAgentUser was not selected' | Out-Null
}

if ($updAgentUser) {
    $auUpdArgs = @{
        TenantId    = $TenantId
        Update      = $true
        AgentUserId = $UpdateAgentUser
        PassThru    = $true
    }
    if ($PSBoundParameters.ContainsKey('AgentUserDisplayName'))  { $auUpdArgs.DisplayName   = $AgentUserDisplayName }
    if ($PSBoundParameters.ContainsKey('AgentUserMailNickname')) { $auUpdArgs.MailNickname  = $AgentUserMailNickname }
    if ($PSBoundParameters.ContainsKey('AgentUserUsageLocation')){ $auUpdArgs.UsageLocation = $AgentUserUsageLocation }
    if ($PSBoundParameters.ContainsKey('AgentUserManagerUserId')){ $auUpdArgs.ManagerUserId = $AgentUserManagerUserId }
    if ($PSBoundParameters.ContainsKey('AgentUserManagerUpn'))   { $auUpdArgs.ManagerUpn    = $AgentUserManagerUpn }
    if ($AgentUserAssignLicense) { $auUpdArgs.AssignLicense = $true }
    if ($PSBoundParameters.ContainsKey('AgentUserLicenseSkuId'))         { $auUpdArgs.LicenseSkuId         = $AgentUserLicenseSkuId }
    if ($PSBoundParameters.ContainsKey('AgentUserLicenseSkuPartNumber')) { $auUpdArgs.LicenseSkuPartNumber = $AgentUserLicenseSkuPartNumber }
    foreach ($k in $agentUserAuthSplat.Keys) { $auUpdArgs[$k] = $agentUserAuthSplat[$k] }
    foreach ($k in $AgentUserParameter.Keys) { $auUpdArgs[$k] = $AgentUserParameter[$k] }

    $agentUser = Invoke-UpdatePhase -Number 3 -Name 'AgentUser' -Title "Agent user update  ->  $UpdateAgentUser" `
        -ScriptPath $agentUserScript -Arguments $auUpdArgs -SummaryProperty 'AgentUserId' -Target $UpdateAgentUser

    if ($agentUser) { $resolvedAgentUserId = Get-PropertyOrDefault $agentUser 'AgentUserId' }
}

# ---------------------------------------------------------------------------
# Phase 4 - registration
# ---------------------------------------------------------------------------

# Defined here rather than with the other helpers because it reads run state that is only
# settled by this point. A registration-only run created neither a blueprint nor an identity,
# so a fixed "the blueprint and agent identity were still created" would be simply untrue.
# The agent user is included too: it now runs in phase 3, so it survives a phase 4 failure.
function Get-RegistrationRetryAdvice {
    $made = @()
    if ($runBlueprint)        { $made += 'blueprint' }
    if ($runIdentity)         { $made += 'agent identity' }
    if ($resolvedAgentUserId) { $made += 'agent user' }
    $lead = if ($made.Count -gt 0) { "The $($made -join ' and ') $(if ($made.Count -gt 1) { 'were' } else { 'was' }) still created. " } else { '' }
    $id   = if ($resolvedIdentityId) { $resolvedIdentityId } else { '<agent-identity-object-id>' }
    return ($lead + "Retry only the registration with: -NewAgentRegistration -UseExistingAgentIdentity $id")
}

if ($runRegistration) {
    Write-Phase 4 "Registration  ->  $regName"
    $regPhase = Start-PhaseRecord -Number 4 -Name 'Registration' -Script $registrationScript

    if ($AgentRegistrationAuth -eq 'Interactive' -and $isAppOnly) {
        Write-Host '  Signing in interactively for this phase only.' -ForegroundColor Yellow
    }

    $regArgs = @{
        TenantId        = $TenantId
        DisplayName     = $regName
        AgentIdentityId = $resolvedIdentityId
    }
    if ($resolvedBpAppId)            { $regArgs.BlueprintAppId = $resolvedBpAppId }
    if ($AgentRegistrationDescription) { $regArgs.Description  = $AgentRegistrationDescription }
    if ($AgentRegistrationOwnerId)   { $regArgs.OwnerId        = $AgentRegistrationOwnerId }
    # No -AgentRegistrationOwnerId: hand the registration step the -AgentRegistrationOwner list so
    # it can resolve the object ids itself instead of needing /me.
    elseif ($AgentRegistrationOwner) { $regArgs.Owner          = $AgentRegistrationOwner }
    foreach ($k in $registrationAuthSplat.Keys) { $regArgs[$k] = $registrationAuthSplat[$k] }
    foreach ($k in $AgentRegistrationParameter.Keys) { $regArgs[$k] = $AgentRegistrationParameter[$k] }

    $regPhase.parameters = ConvertTo-ReportParameterMap $regArgs

    try {
        $regOutput    = @(& $registrationScript @regArgs -WhatIf:$WhatIfPreference)
        $registration = Select-StepSummary -Output $regOutput -RequiredProperty 'RegistrationId'
    }
    catch {
        # A registry failure does not invalidate the blueprint and identity that already
        # exist, so report it and still print the summary rather than losing their ids.
        Add-StepResult 'Registration' 'Failed' $_.Exception.Message
        Complete-PhaseRecord -Record $regPhase -Status 'Failed' -Detail $_.Exception.Message -ErrorRecord $_
        Write-Warning "Phase 4 (registration) failed: $($_.Exception.Message)"
        Write-Warning (Get-RegistrationRetryAdvice)
    }

    if ($registration) {
        $regStatus = Get-PropertyOrDefault $registration 'Status' 'Unknown'
        $regId     = Get-PropertyOrDefault $registration 'RegistrationId'
        Add-StepResult 'Registration' $regStatus $regId
        Complete-PhaseRecord -Record $regPhase -Status $regStatus -Detail $regId -Result $registration -Identifiers @{
            registrationId  = $regId
            agentIdentityId = (Get-PropertyOrDefault $registration 'AgentIdentityId')
            blueprintAppId  = (Get-PropertyOrDefault $registration 'BlueprintAppId')
        }

        # The registration script reports per-agent failures in its result object rather than
        # throwing, so the summary below must react to the status as well as to exceptions.
        if ($regStatus -in @('Registered', 'AlreadyRegistered', 'AlreadyRegisteredIdUnknown')) {
            Write-Host ''
            Write-Host "  -> registration $regId [$regStatus]" -ForegroundColor Green
        }
        else {
            Write-Host ''
            Write-Host "  -> registration $regStatus" -ForegroundColor Red
            Write-Warning (Get-RegistrationRetryAdvice)
            if ($isAppOnly -and $AgentRegistrationAuth -eq 'Same') {
                Write-Warning "If this was a 403, grant AgentRegistration.ReadWrite.All as an application role (New-A365AutomationApp.ps1 -Scenario Registration) or re-run with -AgentRegistrationAuth Interactive."
            }
        }
    }
    elseif ($WhatIfPreference) {
        Add-StepResult 'Registration' 'WhatIf' $regName
        Complete-PhaseRecord -Record $regPhase -Status 'WhatIf' -Detail $regName
    }
    elseif (-not ($script:Steps | Where-Object { $_.Step -eq 'Registration' })) {
        Add-StepResult 'Registration' 'Unknown' 'no result object returned'
        Complete-PhaseRecord -Record $regPhase -Status 'Unknown' -Detail 'no result object returned'
    }
}
elseif (-not $updRegistration) {
    Add-SkippedPhaseRecord -Number 4 -Name 'Registration' -Script $registrationScript -Reason '-NewAgentRegistration was not selected' | Out-Null
}

if ($updRegistration) {
    $regUpdArgs = @{
        TenantId       = $TenantId
        Update         = $true
        RegistrationId = $UpdateAgentRegistration
    }
    if ($PSBoundParameters.ContainsKey('AgentRegistrationDisplayName')) { $regUpdArgs.DisplayName = $AgentRegistrationDisplayName }
    if ($PSBoundParameters.ContainsKey('AgentRegistrationDescription')) { $regUpdArgs.Description = $AgentRegistrationDescription }
    if ($PSBoundParameters.ContainsKey('AgentRegistrationOwner'))       { $regUpdArgs.Owner       = $AgentRegistrationOwner }
    if ($PSBoundParameters.ContainsKey('AgentRegistrationOwnerId'))     { $regUpdArgs.OwnerId     = $AgentRegistrationOwnerId }
    # Re-point the registration at a different agent identity. -AgentRegistrationIdentityId
    # names the identity a registration belongs to, whether it is being created or re-pointed.
    # There is no blueprint equivalent: pass BlueprintAppId through -AgentRegistrationParameter
    # in the rare case a registration has to move between blueprints.
    if ($PSBoundParameters.ContainsKey('AgentRegistrationIdentityId')) { $regUpdArgs.AgentIdentityId = $AgentRegistrationIdentityId }
    foreach ($k in $registrationAuthSplat.Keys)       { $regUpdArgs[$k] = $registrationAuthSplat[$k] }
    foreach ($k in $AgentRegistrationParameter.Keys)  { $regUpdArgs[$k] = $AgentRegistrationParameter[$k] }

    $registration = Invoke-UpdatePhase -Number 4 -Name 'Registration' -Title "Registration update  ->  $UpdateAgentRegistration" `
        -ScriptPath $registrationScript -Arguments $regUpdArgs -SummaryProperty 'RegistrationId' -Target $UpdateAgentRegistration
}

# ---------------------------------------------------------------------------
# Removal phases
#
# Reverse dependency order: registration, agent user, agent identity, blueprint. Deleting a
# parent before its children would orphan them - the agent user in particular becomes hard to
# find once its identity is gone, because it is discovered THROUGH the identity.
#
# These run after every create and update phase, so a single run can rebuild something and
# tidy up what it replaced.
# ---------------------------------------------------------------------------

if ($anyRemoval) {
    $removals = @(
        @{ Order = 1; Name = 'Registration';  Label = 'registration';   On = $rmRegistration; Id = $RemoveAgentRegistration; Script = 'Remove-A365AgentRegistration.ps1'; IdParam = 'RegistrationId' },
        @{ Order = 2; Name = 'AgentUser';     Label = 'agent user';     On = $rmAgentUser;    Id = $RemoveAgentUser;         Script = 'Remove-A365AgentUser.ps1';         IdParam = 'AgentUserId' },
        @{ Order = 3; Name = 'AgentIdentity'; Label = 'agent identity'; On = $rmIdentity;     Id = $RemoveAgentIdentity;     Script = 'Remove-A365AgentIdentity.ps1';     IdParam = 'AgentIdentityId' },
        @{ Order = 4; Name = 'Blueprint';     Label = 'blueprint';      On = $rmBlueprint;    Id = $RemoveBlueprint;         Script = 'Remove-A365Blueprint.ps1';         IdParam = 'BlueprintId' }
    )

    foreach ($rm in $removals) {
        if (-not $rm.On) { continue }

        $rmScript = Join-Path $root $rm.Script
        if (-not (Test-Path -LiteralPath $rmScript)) {
            throw "$($rm.Script) was not found at '$root'. It performs the $($rm.Label) removal; keep the suite together."
        }
        $rmScript = (Resolve-Path -LiteralPath $rmScript).ProviderPath

        $title = if ($RemoveInspectOnly) { "$($rm.Label) removal (inspect only)  ->  $($rm.Id)" } else { "$($rm.Label) REMOVAL  ->  $($rm.Id)" }
        Write-Phase $rm.Order $title
        $rmRecord = Start-PhaseRecord -Number $rm.Order -Name "Remove$($rm.Name)" -Script $rmScript

        $rmArgs = @{ $rm.IdParam = $rm.Id }
        if ($TenantId)              { $rmArgs.TenantId    = $TenantId }
        if ($RemoveInspectOnly)     { $rmArgs.InspectOnly = $true }
        if ($RemoveForce)           { $rmArgs.Force       = $true }

        # -RemovePermanent purges from the recycle bin, which only applies to directory objects.
        # An agent registration is a Microsoft 365 admin center entry, not a directory object, so
        # Remove-A365AgentRegistration.ps1 has no -Permanent and must not be sent one.
        if ($RemovePermanent -and $rm.Name -ne 'Registration') { $rmArgs.Permanent = $true }

        foreach ($k in $removalAuthSplat.Keys) { $rmArgs[$k] = $removalAuthSplat[$k] }

        # Each -Remove* switch routes to a single-purpose script, and those scripts deliberately
        # expose different parameters. Bind-check before splatting so a mismatch surfaces here,
        # named, instead of as a cryptic parameter-binding failure part-way through a deletion.
        # Skipped for a script that declares no parameters of its own: it collects $args and
        # accepts anything, so there is nothing to check against.
        $rmCommon   = [System.Management.Automation.PSCmdlet]::CommonParameters +
                      [System.Management.Automation.PSCmdlet]::OptionalCommonParameters
        $rmAccepted = @((Get-Command -Name $rmScript).Parameters.Keys | Where-Object { $rmCommon -notcontains $_ })
        if ($rmAccepted.Count -gt 0) {
            $rmUnbindable = @($rmArgs.Keys | Where-Object { $rmAccepted -notcontains $_ })
            if ($rmUnbindable.Count -gt 0) {
                throw "$([IO.Path]::GetFileName($rmScript)) does not accept: $($rmUnbindable -join ', '). Each removal script handles exactly one object type; the orchestrator must not forward a parameter the target script does not define."
            }
        }

        try {
            $rmOutput = & $rmScript @rmArgs -WhatIf:$WhatIfPreference
            $status   = if ($RemoveInspectOnly) { 'Inspected' } elseif ($WhatIfPreference) { 'WhatIf' } else { 'Removed' }
            Add-StepResult "Remove$($rm.Name)" $status $rm.Id
            Complete-PhaseRecord -Record $rmRecord -Status $status -Detail $rm.Id -Result $rmOutput `
                -Identifiers @{ $rm.IdParam = $rm.Id }
        }
        catch {
            Add-StepResult "Remove$($rm.Name)" 'Failed' $_.Exception.Message
            Complete-PhaseRecord -Record $rmRecord -Status 'Failed' -Detail $_.Exception.Message -ErrorRecord $_
            throw "Removal of the $($rm.Label) failed: $($_.Exception.Message)"
        }
    }
}

}
finally {

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

$ownerDenied = ((Get-PropertyOrDefault $blueprint 'ownerAssignmentDenied' $false) -eq $true) -or
               ((Get-PropertyOrDefault $identity  'ownerAssignmentDenied' $false) -eq $true)

$result = [ordered]@{
    tenantId      = $TenantId
    authMode      = $authMode
    isAppOnly     = $isAppOnly
    generatedUtc  = [DateTimeOffset]::UtcNow.ToString('o')
    blueprint     = if ($blueprint) { $blueprint } else { [pscustomobject]@{ blueprintAppId = $resolvedBpAppId; reused = $true } }
    agentIdentity = if ($identity) { $identity } else { [pscustomobject]@{ agentIdentityId = $resolvedIdentityId; reused = $true } }
    agentUser     = $agentUser
    registration  = $registration
    ownerAssignmentDenied = $ownerDenied
    steps         = @($script:Steps)
}

# A phase can create its object and still fail to consent its permissions. The step scripts
# each print their own link, but a multi-phase run buries that above later phases, so the
# unfinished consents are collected and repeated once at the end.
$script:ConsentToFinish = @()
foreach ($pair in @(
        @{ Label = 'Blueprint';     Obj = $blueprint },
        @{ Label = 'Agent identity'; Obj = $identity })) {
    $o = $pair.Obj
    if (-not $o) { continue }
    $fails = @(Get-PropertyOrDefault $o 'consentFailures' @())
    if ($fails.Count -eq 0) { continue }
    $script:ConsentToFinish += [pscustomobject]@{
        Label     = $pair.Label
        Count     = $fails.Count
        NeedsPriv = @($fails | Where-Object { (Get-PropertyOrDefault $_ 'isGraphAppRole' $false) -eq $true }).Count -gt 0
        Url       = [string](Get-PropertyOrDefault $o 'adminConsentUrl' '')
        PortalUrl = [string](Get-PropertyOrDefault $o 'portalPermissionsUrl' '')
    }
}

Write-Host ''
Write-Host ('=' * 78) -ForegroundColor Cyan
Write-Host '=== Pipeline complete ===' -ForegroundColor Cyan
Write-Host ('=' * 78) -ForegroundColor Cyan
$script:Steps | Format-Table Step, Status, Detail -AutoSize | Out-Host

Write-Host 'Identifiers:' -ForegroundColor Cyan
Write-Host ("  Blueprint appId        : {0}" -f $(if ($resolvedBpAppId)   { $resolvedBpAppId }   else { '(none)' }))
if ($blueprint) {
    Write-Host ("  Blueprint objectId     : {0}" -f (Get-PropertyOrDefault $blueprint 'blueprintObjectId' '(unknown)'))
    Write-Host ("  Blueprint principalId  : {0}" -f (Get-PropertyOrDefault $blueprint 'blueprintPrincipalId' '(unknown)'))
}
Write-Host ("  Agent identity id      : {0}" -f $(if ($resolvedIdentityId) { $resolvedIdentityId } else { '(none)' }))
Write-Host ("  fmi_path               : {0}" -f $(if ($resolvedIdentityId) { $resolvedIdentityId } else { '(none)' }))
if ($agentUser) {
    Write-Host ("  Agent user id          : {0}" -f $(if ($resolvedAgentUserId) { $resolvedAgentUserId } else { '(unknown)' }))
    Write-Host ("  Agent user UPN         : {0}" -f (Get-PropertyOrDefault $agentUser 'UserPrincipalName' '(unknown)'))
    if ($AgentUserManagerUserId -or $AgentUserManagerUpn -or $AgentUserParameter.ContainsKey('ManagerUserId') -or $AgentUserParameter.ContainsKey('ManagerUpn')) {
        Write-Host ("  Agent user manager     : {0}" -f $(if ((Get-PropertyOrDefault $agentUser 'ManagerAssigned' $false) -eq $true) { 'assigned' } else { 'NOT assigned' }))
    }
}
if ($registration) {
    Write-Host ("  Registration id        : {0}" -f (Get-PropertyOrDefault $registration 'RegistrationId' '(unknown)'))
}

if ($blueprintSecret) {
    Write-Host ''
    if ($OutputJsonPath -and $IncludeBlueprintSecretsInOutput) {
        Write-Host 'Blueprint client secret (shown once, and written to the report file):' -ForegroundColor Yellow
    }
    else {
        Write-Host 'Blueprint client secret (shown once, not written to disk):' -ForegroundColor Yellow
    }
    Write-Host "  $blueprintSecret" -ForegroundColor Yellow
    if ($OutputJsonPath -and -not $IncludeBlueprintSecretsInOutput) {
        Write-Host '  Copy it now - add -IncludeBlueprintSecretsInOutput to have it captured in the report instead.' -ForegroundColor DarkGray
    }
}

$ownerDenied = ((Get-PropertyOrDefault $blueprint 'ownerAssignmentDenied' $false) -eq $true) -or
               ((Get-PropertyOrDefault $identity  'ownerAssignmentDenied' $false) -eq $true)
if ($ownerDenied) {
    Write-Host ''
    Write-Host 'Owners were not fully assigned (403). Everything else completed.' -ForegroundColor Yellow
    Write-Host '  Owners on the blueprint APPLICATION and the agent identity need' -ForegroundColor Gray
    Write-Host '  Application.ReadWrite.All (or .OwnedBy) AND Directory.Read.All.' -ForegroundColor Gray
    Write-Host '  Owners on the blueprint PRINCIPAL need AgentIdentityBlueprintPrincipal.ReadWrite.All' -ForegroundColor Gray
    Write-Host '  instead - Application.ReadWrite.All does NOT authorize that call, so granting more' -ForegroundColor Gray
    Write-Host '  Application.* permissions will not fix a principal owner denial.' -ForegroundColor Gray
    Write-Host '  Delegated callers also need the Application Administrator or Cloud Application' -ForegroundColor Gray
    Write-Host '  Administrator role in Microsoft Entra.' -ForegroundColor Gray
    Write-Host '  Re-run New-A365AutomationApp.ps1 -Scenario Blueprint to add the' -ForegroundColor Gray
    Write-Host '  roles, then re-run this script - it adds the missing owners without duplicating.' -ForegroundColor Gray
}

if (@($script:ConsentToFinish).Count -gt 0) {
    Write-Host ''
    Write-Host 'ACTION REQUIRED - permissions still need an administrator.' -ForegroundColor Red
    Write-Host '  The objects below were created, but some permissions could not be consented, so' -ForegroundColor Gray
    Write-Host '  they grant no claims yet. Each phase printed the detail above.' -ForegroundColor Gray
    foreach ($c in $script:ConsentToFinish) {
        Write-Host ''
        Write-Host ("  {0} - {1} permission(s) outstanding:" -f $c.Label, $c.Count) -ForegroundColor Yellow
        if ($c.Url)       { Write-Host ("    consent link : {0}" -f $c.Url) -ForegroundColor Cyan }
        if ($c.PortalUrl) { Write-Host ("    portal       : {0}" -f $c.PortalUrl) -ForegroundColor Cyan }
        if ($c.NeedsPriv) {
            Write-Host '    role needed  : Privileged Role Administrator or Global Administrator' -ForegroundColor Gray
            Write-Host '                   (Microsoft Graph app roles are involved; Application' -ForegroundColor Gray
            Write-Host '                   Administrator cannot consent those)' -ForegroundColor Gray
        }
        else {
            Write-Host '    role needed  : Application Administrator, Cloud Application Administrator,' -ForegroundColor Gray
            Write-Host '                   Privileged Role Administrator or Global Administrator' -ForegroundColor Gray
        }
    }
}

if ($resolvedBpAppId -and $resolvedIdentityId) {
    Write-Host ''
    Write-Host 'Acquiring a token as this agent:' -ForegroundColor Cyan
    Write-Host "  POST https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"
    Write-Host '  Content-Type: application/x-www-form-urlencoded'
    Write-Host ''
    Write-Host "    client_id=$resolvedBpAppId"
    Write-Host '    scope=https://graph.microsoft.com/.default'
    Write-Host '    grant_type=client_credentials'
    Write-Host '    client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
    Write-Host '    client_assertion=<managed-identity-or-certificate-assertion>'
    Write-Host "    fmi_path=$resolvedIdentityId"
}

$failedSteps = @($script:Steps | Where-Object { $_.Status -in @('Failed', 'Unknown', 'Unverified', 'Partial') })
if ($failedSteps.Count -gt 0) {
    Write-Host ''
    Write-Host 'Some steps did not complete cleanly:' -ForegroundColor Red
    foreach ($s in $failedSteps) { Write-Host "  * $($s.Step): $($s.Status) $($s.Detail)" -ForegroundColor Red }
    Write-Host '  Every step is idempotent - fix the cause and re-run. Use -UseExistingBlueprint or' -ForegroundColor Gray
    Write-Host '  -UseExistingAgentIdentity / -AgentRegistrationIdentityId to resume without redoing earlier phases.' -ForegroundColor Gray
    if ($failedSteps.Step -contains 'AgentUser' -and $resolvedIdentityId) {
        Write-Host ''
        Write-Host '  To retry only the agent user:' -ForegroundColor Gray
        Write-Host ("    .\A365-AutomationOrchestrator.ps1 -TenantId $TenantId ``") -ForegroundColor Gray
        Write-Host ("      -NewAgentUser -UseExistingAgentIdentity $resolvedIdentityId ``") -ForegroundColor Gray
        Write-Host ("      -AgentUserPrincipalName $AgentUserPrincipalName ...") -ForegroundColor Gray
    }
}

# ---------------------------------------------------------------------------
# Run report
# ---------------------------------------------------------------------------

$script:RunContext.completedUtc    = [DateTimeOffset]::UtcNow.ToString('o')
$script:RunContext.durationSeconds = [math]::Round(([DateTimeOffset]::UtcNow - $script:RunStartUtc).TotalSeconds, 2)

$script:RunSummary = [ordered]@{
    outcome     = if ($failedSteps.Count -gt 0) { 'Incomplete' } else { 'Succeeded' }
    identifiers = [ordered]@{
        blueprintAppId       = $resolvedBpAppId
        blueprintObjectId    = (Get-PropertyOrDefault $blueprint 'blueprintObjectId')
        blueprintPrincipalId = (Get-PropertyOrDefault $blueprint 'blueprintPrincipalId')
        agentIdentityId      = $resolvedIdentityId
        fmiPath              = $resolvedIdentityId
        agentUserId          = $resolvedAgentUserId
        agentUserPrincipalName = (Get-PropertyOrDefault $agentUser 'UserPrincipalName')
        agentUserManagerAssigned = (Get-PropertyOrDefault $agentUser 'ManagerAssigned')
        registrationId       = (Get-PropertyOrDefault $registration 'RegistrationId')
    }
    ownerAssignmentDenied = $ownerDenied
    # Empty when nothing is outstanding, so automation can branch on .Count without
    # re-deriving anything. Present per object because the consent link is per app.
    consentActionRequired = @($script:ConsentToFinish | ForEach-Object {
        [ordered]@{
            object               = $_.Label
            outstandingCount     = $_.Count
            adminConsentUrl      = $_.Url
            portalPermissionsUrl = $_.PortalUrl
            requiredRole         = if ($_.NeedsPriv) { 'Privileged Role Administrator or Global Administrator' }
                                   else { 'Application Administrator, Cloud Application Administrator, Privileged Role Administrator or Global Administrator' }
        }
    })
    secretCreated         = ($script:CreatedSecretCount -gt 0)
    failedSteps           = @($failedSteps | ForEach-Object {
                                [ordered]@{ step = $_.Step; status = $_.Status; detail = $_.Detail } })
    tokenRequest          = if ($resolvedBpAppId -and $resolvedIdentityId) {
        [ordered]@{
            method   = 'POST'
            url      = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"
            form     = [ordered]@{
                client_id             = $resolvedBpAppId
                scope                 = 'https://graph.microsoft.com/.default'
                grant_type            = 'client_credentials'
                client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
                client_assertion      = '<managed-identity-or-certificate-assertion>'
                fmi_path              = $resolvedIdentityId
            }
        }
    } else { $null }
}

if ($OutputJsonPath) {
    # Never let a reporting problem mask the pipeline result, or the exception that got us here.
    try   { Write-RunReport -Path $OutputJsonPath -WithSecrets ([bool]$IncludeBlueprintSecretsInOutput) | Out-Null }
    catch { Write-Warning "Could not write the run report to '$OutputJsonPath': $($_.Exception.Message)" }
}

# The in-memory result keeps the secret; only the file is gated.
$result.report = [ordered]@{
    path            = if ($OutputJsonPath) { $OutputJsonPath } else { $null }
    secretsIncluded = [bool]$IncludeBlueprintSecretsInOutput
}
$result.phases = @($script:Phases | ForEach-Object { [pscustomobject]$_ })
$result.summary = $script:RunSummary

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green

}

[pscustomobject]$result

Write-A365LogCorrelation
Complete-A365Log -Outcome 'Succeeded'