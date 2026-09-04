<#
.SYNOPSIS
    Creates an agent identity from an EXISTING Microsoft Entra Agent ID / Agent 365 agent identity
    blueprint, using ONLY Microsoft Graph REST API calls. Does not use the Agent 365 CLI.

.DESCRIPTION
    Companion to New-A365AgentBlueprint.ps1. The blueprint is the reusable template; this script
    creates one agent identity (the account an individual running agent authenticates as) from it.

      1. Connect to Microsoft Graph as an application (client credentials) or as a user.
      2. Resolve and validate the blueprint (by appId or displayName) and its blueprint principal.
      3. Resolve sponsors and owners, enforcing the documented principal-type rules.
      4. GET    /servicePrincipals/microsoft.graph.agentIdentity        (idempotency probe)
      5. POST   /servicePrincipals/microsoft.graph.agentIdentity        (create agent identity)
      6. POST   /servicePrincipals/{id}/microsoft.graph.agentIdentity/owners/$ref
      7. POST   /servicePrincipals/{id}/microsoft.graph.agentIdentity/sponsors/$ref   (repair only)
      8. PATCH  /servicePrincipals/{id}/microsoft.graph.agentIdentity   (only with -Disabled)
      9. POST   /oauth2PermissionGrants + /servicePrincipals/{id}/appRoleAssignments  (optional
         per-identity consent, on top of whatever the blueprint already grants by inheritance)

    Every call is issued through Invoke-MgGraphRequest so the wire request is a literal Graph REST call.
    The script is re-runnable: an existing agent identity with the same display name under the same
    blueprint is reused, and missing owners are added rather than duplicated.

    Agent identities have NO credentials of their own. The blueprint holds the credential and mints
    tokens for the agent identity using the fmi_path parameter. This script therefore never creates
    a secret or federated credential on the agent identity - see the token sample it prints at the end.

    AUTHENTICATION
    The script is built to run unattended as an application. Pass -ClientId together with one of
    -ClientSecret, -CertificateThumbprint, -Certificate or -CertificatePath, or use
    -UseManagedIdentity / -AccessToken. In that mode permissions come from Microsoft Graph
    APPLICATION app roles granted to the app registration - delegated scopes are neither requested
    nor honoured - and the script verifies up front that every role it needs is actually granted.
    Use New-A365AutomationApp.ps1 to create that app registration.

    To sign in as a human instead, pass -Interactive. An authentication method must be chosen
    explicitly so an unattended run can never silently block on a sign-in prompt.

    Running app-only also matters functionally: adding sponsors to an ALREADY EXISTING agent
    identity is an application-only operation with no delegated equivalent, so step 7 can only
    repair sponsors when the script runs as an application.

    Application (app role) permissions required on Microsoft Graph:
        AgentIdentity.Create.All          AgentIdentityBlueprint.Read.All
        AgentIdentity.Read.All            Application.Read.All
        AgentIdentity.ReadWrite.All       User.Read.All, Group.Read.All
    plus AgentIdentity.EnableDisable.All with -Disabled, and
    DelegatedPermissionGrant.ReadWrite.All + AppRoleAssignment.ReadWrite.All with -GrantAdminConsent.

.PARAMETER TenantId
    The Entra ID tenant to operate against.

.PARAMETER ClientId
    Application (client) ID to authenticate as. Required for client secret and certificate auth,
    optional for a user-assigned managed identity or a custom -Interactive app.

.PARAMETER ClientSecret
    Client secret, as a SecureString or a plain string. May also be supplied through the A365_CLIENT_SECRET
    environment variable so it never appears in a command line or transcript.

.PARAMETER CertificateThumbprint
    Thumbprint of a certificate in the current user's or machine's certificate store.

.PARAMETER Certificate
    An already-loaded X509Certificate2 to authenticate with.

.PARAMETER CertificatePath
    Path to a .pfx file. Combine with -CertificatePassword when the file is protected.

.PARAMETER CertificatePassword
    Password for -CertificatePath, as a SecureString or a plain string.

.PARAMETER UseManagedIdentity
    Authenticate with the host's managed identity. Add -ClientId for a user-assigned identity.

.PARAMETER AccessToken
    A Microsoft Graph access token, as a SecureString or a plain string, for callers that mint
    tokens themselves.

.PARAMETER Interactive
    Sign in as a user with delegated scopes instead of running as an application.

.PARAMETER SkipPermissionCheck
    Skips the app role pre-flight check performed in app-only mode.

.PARAMETER MintWithBlueprintCredential
    Issues the create call with a token belonging to the blueprint app itself, which Entra ID
    auto-grants AgentIdentity.CreateAsManager. This is how the Agent 365 CLI mints identities, and
    it removes the need for the caller to hold AgentIdentity.Create.All.

    Only that one call uses the blueprint token. The session stays on the caller credential,
    because a blueprint token carries AgentIdentity.CreateAsManager and NOTHING else - no
    directory-read roles at all - so connecting as the blueprint makes every read in this script
    fail with 403 Authorization_RequestDenied (blueprint validation and sponsor/owner lookup
    included). Requires -BlueprintAppId and -BlueprintClientSecret.

.PARAMETER BlueprintClientSecret
    The blueprint application's own client secret, used only with -MintWithBlueprintCredential.
    Accepts a SecureString or a plain string; $env:A365_BLUEPRINT_CLIENT_SECRET is used when the
    parameter is omitted, which keeps the secret out of shell history.

.PARAMETER DisplayName
    Display name of the agent identity. Also used as the idempotency key, scoped to the blueprint.

.PARAMETER BlueprintAppId
    appId (client ID) of an existing agent identity blueprint. This is the value that is written to
    the agent identity's agentIdentityBlueprintId property. Mutually exclusive with
    -BlueprintDisplayName.

.PARAMETER BlueprintDisplayName
    Display name of an existing blueprint, resolved to its appId. Mutually exclusive with
    -BlueprintAppId. Fails if the name is ambiguous.

.PARAMETER Sponsor
    REQUIRED by the create API. One or more sponsor UPNs, group names or object IDs. A sponsor is
    the human (or group) accountable for the agent, used for security-incident contact.
    Users, Microsoft 365 groups and dynamic-membership groups are supported. Static security groups
    and role-assignable groups are NOT - the script validates this before writing anything.

    NOTE: sponsors can only be added AFTER creation with application permissions; the delegated flow
    used by this script cannot. So sponsors are always set in the create call.

.PARAMETER Owner
    Optional owner UPN(s), service principal name(s) or object ID(s). Owners may modify the agent
    identity without holding an Agent ID admin role. Groups are NOT supported as owners.
    The calling principal is added as an owner automatically by Entra ID.

    An app registration has THREE different GUIDs and only one of them can own anything. If you
    pass an appId or an application object id, this script translates it to the app's service
    principal automatically and prints a note:
        appId ("Application (client) ID")   b2475fce-...   -> translated
        application object id               6b43a0b1-...   -> translated
        service principal object id         b28e3ca9-...   -> used as-is  <- the one owners/$ref wants
    Without that translation Entra ID answers with errors that name the GUID but never explain the
    mismatch: 404 Request_ResourceNotFound for an appId, and for an application object id
    400 "The reference target 'Application_<guid>' of type 'Application' is invalid for the
    'owners' reference."

.PARAMETER RequireOwnerAssignment
    Treat a refused -Owner assignment as fatal instead of the default: a warning, with the
    agent identity otherwise left complete and usable.

.PARAMETER Tag
    Optional strings used to categorize the agent identity. The effective tag set is the union of
    these and the blueprint's own tags.

    Applied on create, and PATCHed onto an identity that already exists so that re-running to add
    a tag actually adds it. Tags already present are left alone, and existing tags are never
    removed - the desired set is merged, not replaced.

    The reported tags are read back from the directory afterwards, so they reflect what is on the
    object rather than what was asked for. A tag that the directory accepts but does not persist
    is reported as a warning instead of being silently counted as a success.

.PARAMETER CustomSecurityAttribute
    Optional custom security attributes to assign to the agent identity. Unlike -Tag, these are
    governed directory data: they are defined centrally as attribute sets, can restrict values to a
    predefined list, and are readable only by principals holding the custom security attribute
    permissions. Several attributes across several sets can be assigned in one run.

    A list of "AttributeSet:Attribute:Value" strings, which is the easiest form to type:

        -CustomSecurityAttribute "AgentAttributes:AgentEnvironment:Production",
                                 "AgentAttributes:AgentBusinessUnit:HR",
                                 "AgentAttributes:AgentApprovalStatus:New,In_Review"

    Only the first two colons separate, so a value may itself contain colons (a URL, a timestamp).
    Commas separate the values of a multi-valued attribute. Whether one value means 'New' or
    @('New') is decided from the attribute's own definition, so no extra syntax is needed for a
    single-element collection. An empty value ("AgentAttributes:AgentEnvironment:") removes the
    assignment.

    Or a nested hashtable of attribute set -> attribute name -> value, which is the escape hatch
    for a value that itself contains a comma:

        -CustomSecurityAttribute @{
            AgentAttributes = @{
                AgentEnvironment    = 'Production'         # single-valued
                AgentBusinessUnit   = 'HR'
                AgentApprovalStatus = @('New','In_Review') # multi-valued
            }
            Engineering = @{ ProjectDate = '2026-08-21' }
        }

    Both forms may be mixed in one call. In the hashtable form, pass a single value for a
    single-valued attribute and an array for a multi-valued one; the script checks each against the
    attribute's definition and reports a mismatch before writing.

    Set names, attribute names and predefined values are all CASE-SENSITIVE in Graph, and a
    mismatch produces errors that name the wrong thing - a misspelt attribute is reported as
    "Custom Security attributes not found on tenant", which reads like the tenant has none at all.
    So the tenant schema is read first and the request is validated against it, naming the exact
    set, attribute and permitted values. Use -SkipCustomSecurityAttributeValidation to bypass that
    pre-flight.

    Applied in the create call, and PATCHed onto an identity that already exists. Graph merges at
    the ATTRIBUTE level, so attributes that are not mentioned are left alone; a multi-valued
    attribute that IS mentioned has its whole value replaced. Assignments are read back from the
    directory afterwards, so the summary shows what the object carries rather than what was asked
    for.

    Requires CustomSecAttributeAssignment.ReadWrite.All to assign and
    CustomSecAttributeDefinition.Read.All to validate. These are NOT held by default: a delegated
    caller needs the Attribute Assignment Administrator role (Global Administrator does not include
    it), and an app-only caller needs both application roles granted explicitly.

.PARAMETER SkipCustomSecurityAttributeValidation
    Sends -CustomSecurityAttribute to Graph without first reading the tenant's attribute
    definitions. Use when the caller may assign attributes but not read their definitions. Graph
    still enforces the rules; only the clearer error message is lost.

.PARAMETER Disabled
    Creates the agent identity, then disables it (accountEnabled = false) so it cannot obtain tokens
    until you explicitly enable it. Useful for staged rollouts.

.PARAMETER RequiredPermission
    Optional per-identity permissions, granted only when -GrantAdminConsent is also passed. Normally
    unnecessary: permissions usually flow from the blueprint through inheritable permissions. Use
    this only when a single agent identity needs something its siblings must not have.
    Array of hashtables:
        @{ ResourceAppId = '<guid>'; DelegatedScopes = @('Scope.Name'); AppRoles = @('Role.Name') }

.PARAMETER GrantAdminConsent
    Grants -RequiredPermission directly on this agent identity as tenant-wide admin consent.

.PARAMETER OutputJsonPath
    Optional path to write the result summary as JSON.

.PARAMETER Update
    Update an agent identity that already exists instead of creating one. Requires
    -AgentIdentityId.

    Only the attributes supplied on the command line are written. The object is located
    through the agentIdentity type-cast URI, which doubles as a type check, so a plain
    service principal is refused rather than modified.

    Update-A365AgentIdentity.ps1 is the dedicated entry point for this and is usually the
    clearer way to call it. Both run exactly the same code.

.PARAMETER AgentIdentityId
    With -Update, the object id (service principal id) of the agent identity to change.

.PARAMETER LogPath
    Write a timestamped log of this run. A path that names an existing directory (or ends in
    a separator) gets a generated file name inside it; anything else is used as the exact
    file name. Omit it to log nothing.

.PARAMETER LogIncludeSecrets
    Allow plain-string client secrets and passwords in the log. SecureString values and bearer
    tokens remain redacted. Only meaningful with -LogPath.

.PARAMETER LogCorrelationId
    Correlation id written into the log, shared with any calling script's own log so a run can
    be traced across scripts. Generated automatically when omitted.

.EXAMPLE
    # Unattended, running as an application with a client secret from the environment.
    $env:A365_CLIENT_SECRET = '<secret>'
    .\New-A365AgentIdentity.ps1 -TenantId <tenant> -ClientId <automation-app-id> `
        -BlueprintAppId 00001111-aaaa-2222-bbbb-3333cccc4444 `
        -DisplayName 'Expense Agent - North America' `
        -Sponsor alice@contoso.com

.EXAMPLE
    # Unattended, running as an application with a certificate.
    .\New-A365AgentIdentity.ps1 -TenantId <tenant> -ClientId <automation-app-id> `
        -CertificateThumbprint A1B2C3... `
        -BlueprintDisplayName 'Contoso Expense Agent' `
        -DisplayName 'Expense Agent - EMEA' `
        -Sponsor 'Finance Team' -Owner alice@contoso.com, bob@contoso.com `
        -Tag 'emea', 'production'

.EXAMPLE
    # Signed in as a user instead of as an application, previewing without writing.
    .\New-A365AgentIdentity.ps1 -Interactive -TenantId <tenant> `
        -BlueprintDisplayName 'Contoso Expense Agent' `
        -DisplayName 'Expense Agent - EMEA' -Sponsor 'Finance Team' -WhatIf

.EXAMPLE
    # Provision a fleet from one blueprint, unattended.
    'NA', 'EMEA', 'APAC' | ForEach-Object {
        .\New-A365AgentIdentity.ps1 -TenantId <tenant> -ClientId <automation-app-id> `
            -CertificateThumbprint A1B2C3... `
            -BlueprintAppId 00001111-aaaa-2222-bbbb-3333cccc4444 `
            -DisplayName "Expense Agent - $_" -Sponsor alice@contoso.com
    }

.EXAMPLE
    # Mint with the blueprint's own credential, so the caller does not need
    # AgentIdentity.Create.All. Reads still run under the automation app.
    $env:A365_BLUEPRINT_CLIENT_SECRET = '<blueprint-secret>'
    .\New-A365AgentIdentity.ps1 -TenantId <tenant> `
        -ClientId <automation-app-id> -ClientSecret $env:A365_CLIENT_SECRET `
        -BlueprintAppId 00001111-aaaa-2222-bbbb-3333cccc4444 `
        -DisplayName 'Expense Agent - EMEA' -Sponsor alice@contoso.com `
        -MintWithBlueprintCredential

.NOTES
    Requires PowerShell 7+ and the Microsoft.Graph.Authentication module (used purely as a token
    provider and REST transport).

    An agent identity's object ID and app ID always have the same value, so the id returned by this
    script is what you pass as fmi_path when the blueprint requests a token for the agent.

    Two different credentials can be in play. Reads (blueprint validation, sponsor and owner
    resolution, the idempotency probe) need directory permissions and run under the caller. The
    create call needs AgentIdentity.Create.All or AgentIdentity.CreateAsManager and can optionally
    run under the blueprint itself via -MintWithBlueprintCredential. Do not "simplify" this by
    connecting the whole script as the blueprint: its token holds one role and no read access.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '')]
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ByAppId')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'CertificatePassword',
    Justification = 'Deliberately accepts a plain string for usability; ConvertTo-SecureStringValue converts it immediately.')]
param(
    [Parameter(Mandatory)][string] $TenantId,

    # Mandatory when creating. Optional under -Update, where passing it RENAMES the identity and
    # omitting it leaves the current name alone.
    [Parameter(Mandatory, ParameterSetName = 'ByAppId')]
    [Parameter(Mandatory, ParameterSetName = 'ByName')]
    [Parameter(ParameterSetName = 'Update')]
    [string] $DisplayName,

    [Parameter(Mandatory, ParameterSetName = 'ByAppId')][string] $BlueprintAppId,
    [Parameter(Mandatory, ParameterSetName = 'ByName')][string]  $BlueprintDisplayName,

    # --- update mode ---------------------------------------------------------
    # Targets an agent identity that already exists and changes ONLY the attributes whose
    # parameters were passed on this command line. Nothing is created. The blueprint is read
    # back from the identity rather than supplied, so no blueprint parameter applies here.
    [Parameter(Mandatory, ParameterSetName = 'Update')][switch] $Update,
    [Parameter(Mandatory, ParameterSetName = 'Update')][string] $AgentIdentityId,

    # Mandatory when creating: POST is the only delegated opportunity to set sponsors, because
    # the sponsors/$ref API is application-permission only. Optional under -Update.
    [Parameter(Mandatory, ParameterSetName = 'ByAppId')]
    [Parameter(Mandatory, ParameterSetName = 'ByName')]
    [Parameter(ParameterSetName = 'Update')]
    [string[]] $Sponsor,

    # --- authentication: app-only (default) or -Interactive ------------------
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

    # --- minting with the blueprint's own credential --------------------------
    # A blueprint app is auto-granted AgentIdentity.CreateAsManager, so it can mint identities
    # from itself. That is the ONLY thing it can do: its token carries that single role and no
    # directory-read roles at all, so connecting as the blueprint would 403 on every surrounding
    # read. These switches keep the session on the caller credential and borrow a blueprint token
    # for the create call alone.
    [switch]       $MintWithBlueprintCredential,
    [object]       $BlueprintClientSecret,

    [string[]] $Owner,
    [switch]   $RequireOwnerAssignment,
    [string[]] $Tag,
    [object[]] $CustomSecurityAttribute,
    [switch]    $SkipCustomSecurityAttributeValidation,

    [switch]   $Disabled,

    [hashtable[]] $RequiredPermission,
    [switch]      $GrantAdminConsent,

    [string]   $OutputJsonPath,

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
$null = Initialize-A365Log -Path $LogPath -ScriptName 'New-A365AgentIdentity.ps1' `
    -BoundParameters $PSBoundParameters -IncludeSecrets:$LogIncludeSecrets -CorrelationId $LogCorrelationId
if ($script:LogFile) { Write-Host "  Log file           : $($script:LogFile)" -ForegroundColor DarkGray }

trap {
    Write-A365Log -Level ERROR -Message "UNHANDLED: $($_.Exception.Message)" -Detail $_.ScriptStackTrace
    Complete-A365Log -Outcome 'Failed'
    break
}

$script:GraphRoot = 'https://graph.microsoft.com/v1.0'
$script:OwnerAssignmentDenied = $false

# ---------------------------------------------------------------------------
# Consent / permission-assignment failure advice
# ---------------------------------------------------------------------------
# A refused grant leaves the permission DECLARED but unconsented, so the agent gets no
# claims from it while everything still looks created. The caller usually cannot fix
# that themselves - it needs a directory role they do not hold - so failures are
# collected here and the run ends by handing them a link to pass to an administrator.
$script:GraphAppId      = '00000003-0000-0000-c000-000000000000'
$script:ConsentFailures = @()

function Add-ConsentFailure {
    param(
        [Parameter(Mandatory)] [ValidateSet('delegated', 'app role')] [string] $Kind,
        [string] $ResourceName,
        [string] $ResourceAppId,
        [string] $Permission,
        [string] $Message
    )
    # Only Microsoft Graph APPLICATION permissions are out of reach for an Application
    # Administrator, so the distinction is recorded per failure rather than guessed later.
    $script:ConsentFailures += [pscustomobject]@{
        Kind           = $Kind
        ResourceName   = $ResourceName
        ResourceAppId  = $ResourceAppId
        Permission     = $Permission
        Message        = $Message
        IsGraphAppRole = ($Kind -eq 'app role' -and $ResourceAppId -eq $script:GraphAppId)
    }
}

function Get-AdminConsentUrl {
    param([string] $TenantId, [string] $ClientAppId)
    if ([string]::IsNullOrWhiteSpace($TenantId) -or [string]::IsNullOrWhiteSpace($ClientAppId)) { return $null }
    "https://login.microsoftonline.com/$TenantId/adminconsent?client_id=$ClientAppId"
}

function Get-PortalPermissionsUrl {
    param([string] $ClientObjectId, [string] $ClientAppId)
    if ([string]::IsNullOrWhiteSpace($ClientObjectId) -or [string]::IsNullOrWhiteSpace($ClientAppId)) { return $null }
    "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/ManagedAppMenuBlade/~/Permissions/objectId/$ClientObjectId/appId/$ClientAppId"
}

function Write-ConsentActionRequired {
    param(
        [string]   $TenantId,
        [string]   $ClientAppId,
        [string]   $ClientObjectId,
        [string]   $DisplayName,
        [object[]] $Failures
    )
    $items = @($Failures)
    if ($items.Count -eq 0) { return }

    Write-Host ''
    Write-Host 'ACTION REQUIRED - an administrator must finish granting these permissions.' -ForegroundColor Red
    foreach ($f in $items) {
        Write-Host ("    {0,-9} {1} on {2}" -f $f.Kind, $f.Permission, $f.ResourceName) -ForegroundColor Gray
        if ($f.Message) { Write-Host ("              {0}" -f $f.Message) -ForegroundColor DarkGray }
    }
    Write-Host '  They are declared on the app but grant no claims until consent succeeds.' -ForegroundColor Gray

    $consentUrl = Get-AdminConsentUrl -TenantId $TenantId -ClientAppId $ClientAppId
    if ($consentUrl) {
        Write-Host ''
        Write-Host '  Send this link to an administrator. Opening it and approving grants every' -ForegroundColor Yellow
        Write-Host '  permission this app declares, tenant-wide, in one step:' -ForegroundColor Yellow
        Write-Host "    $consentUrl" -ForegroundColor Cyan
    }

    $portalUrl = Get-PortalPermissionsUrl -ClientObjectId $ClientObjectId -ClientAppId $ClientAppId
    if ($portalUrl) {
        Write-Host ''
        Write-Host '  Or grant it in the Microsoft Entra admin center:' -ForegroundColor Yellow
        Write-Host "    $portalUrl" -ForegroundColor Cyan
        Write-Host ("    Enterprise applications > {0} > Security > Permissions > Grant admin consent" -f $(
            if ([string]::IsNullOrWhiteSpace($DisplayName)) { 'this app' } else { $DisplayName })) -ForegroundColor DarkGray
    }

    Write-Host ''
    if (@($items | Where-Object { $_.IsGraphAppRole }).Count -gt 0) {
        # Documented Entra limit, not a guess: Application Administrator and Cloud
        # Application Administrator may consent any permission for any API EXCEPT
        # Microsoft Graph app roles. Naming the wrong role sends the request to
        # someone who will find the consent button greyed out.
        Write-Host '  Required role: Privileged Role Administrator, or Global Administrator.' -ForegroundColor Yellow
        Write-Host '    Application Administrator and Cloud Application Administrator are NOT' -ForegroundColor Gray
        Write-Host '    enough for this run: they cannot consent Microsoft Graph app roles' -ForegroundColor Gray
        Write-Host '    (application permissions), which is what failed above.' -ForegroundColor Gray
    }
    else {
        Write-Host '  Required role: Application Administrator, Cloud Application Administrator,' -ForegroundColor Yellow
        Write-Host '    Privileged Role Administrator, or Global Administrator.' -ForegroundColor Yellow
    }
}
# Populated by Get-MissingAppRole; stays an empty array when the pre-flight is skipped or fails,
# so readers under StrictMode never hit an undefined variable.
$script:LastGrantedAppRoles = @()

# ---------------------------------------------------------------------------
# Graph REST helpers - retries, OData-Version header, structured errors
# ---------------------------------------------------------------------------

# Member enumeration ($collection.Prop) throws under StrictMode when the collection is empty.
function Get-PropertyValue {
    param($Collection, [Parameter(Mandatory)][string] $Property)
    @(@($Collection) | Where-Object { $null -ne $_ } | ForEach-Object { $_.$Property })
}

# `.PSObject.Properties.Name` is member enumeration, which throws under StrictMode when the object
# has zero properties - and Graph does return empty bodies. foreach over an empty collection is safe.
function Test-HasProperty {
    param($Object, [Parameter(Mandatory)][string] $Name)
    if ($null -eq $Object) { return $false }
    $properties = $Object.PSObject.Properties
    if ($null -eq $properties) { return $false }
    foreach ($property in $properties) {
        if ($property.Name -eq $Name) { return $true }
    }
    return $false
}

# Reading an agent identity's tags returns the UNION of its own tags and its blueprint's, so the
# blueprint's have to be subtracted before the set is written back. Writing the union back
# verbatim would pin a private copy of the blueprint's tags onto the identity, which would then
# stop tracking the blueprint. Dropping an inherited tag from the identity's own set cannot change
# the effective value, because inheritance still supplies it.
function Get-AgentIdentityOwnTag {
    param($Identity, [string[]] $InheritedTag)
    $effective = @()
    if (Test-HasProperty $Identity 'tags') { $effective = @($Identity.tags) }
    $inherited = [System.Collections.Generic.HashSet[string]]::new([string[]]@($InheritedTag), [StringComparer]::OrdinalIgnoreCase)
    @($effective | Where-Object { $_ -and -not $inherited.Contains($_) })
}

# ---------------------------------------------------------------------------
# Custom security attributes
#
# These are governed directory data rather than free-form labels, and Graph is strict about them
# in ways it does not explain well. Verified live against the directory:
#
#   * Attribute set names, attribute names and predefined values are all CASE-SENSITIVE.
#   * A misspelt ATTRIBUTE is reported as "Custom Security attributes not found on tenant",
#     which reads like the tenant has no attributes at all rather than naming the typo.
#   * A misspelt SET is reported as "AttributeSet ... does not exist".
#   * A single-valued attribute rejects an array ("Non empty string value expected") and a
#     multi-valued one rejects a bare string ("Collection of values expected").
#   * Graph merges at the ATTRIBUTE level: PATCHing one attribute leaves the others in the same
#     set untouched. A multi-valued attribute that is written is replaced wholesale.
#
# So the tenant schema is read once and the request validated against it before anything is sent.
# ---------------------------------------------------------------------------

# Flattens the tenant's attribute sets, definitions and allowed values into a lookup keyed by set
# name then attribute name. Kept separate from validation so the validator is a pure function that
# can be exercised without a directory.
function Get-CustomSecurityAttributeSchema {
    [CmdletBinding()]
    param()

    $schema = @{}

    $setResponse = Invoke-Graph -Method GET -Uri '/directory/attributeSets'
    $setNames = @()
    if (Test-HasProperty $setResponse 'value') {
        $setNames = @($setResponse.value | ForEach-Object { [string]$_.id })
    }
    foreach ($name in $setNames) {
        $schema[$name] = @{ Name = $name; Attributes = @{} }
    }

    $defResponse = Invoke-Graph -Method GET -Uri '/directory/customSecurityAttributeDefinitions'
    $definitions = @()
    if (Test-HasProperty $defResponse 'value') { $definitions = @($defResponse.value) }

    foreach ($definition in $definitions) {
        # Definition ids are "<set>_<attribute>". The attribute name itself may contain an
        # underscore, so split on the FIRST one only.
        $definitionId = [string]$definition.id
        $split = $definitionId.IndexOf('_')
        if ($split -lt 1) { continue }
        $setName  = $definitionId.Substring(0, $split)
        $attrName = $definitionId.Substring($split + 1)

        if (-not $schema.ContainsKey($setName)) {
            $schema[$setName] = @{ Name = $setName; Attributes = @{} }
        }

        $allowed = @()
        $predefinedOnly = $false
        if (Test-HasProperty $definition 'usePreDefinedValuesOnly') {
            $predefinedOnly = [bool]$definition.usePreDefinedValuesOnly
        }
        if ($predefinedOnly) {
            # Only the ACTIVE values may be assigned; an inactive one is refused with the same
            # message as a value that was never defined.
            $valueResponse = Invoke-Graph -Method GET `
                -Uri "/directory/customSecurityAttributeDefinitions/$definitionId/allowedValues" -TolerateNotFound
            if ($valueResponse -and (Test-HasProperty $valueResponse 'value')) {
                $allowed = @($valueResponse.value |
                    Where-Object { -not (Test-HasProperty $_ 'isActive') -or $_.isActive } |
                    ForEach-Object { [string]$_.id })
            }
        }

        $schema[$setName].Attributes[$attrName] = @{
            Name              = $attrName
            Type              = [string]$definition.type
            IsCollection      = [bool]$definition.isCollection
            Status            = [string]$definition.status
            UsePredefinedOnly = $predefinedOnly
            AllowedValues     = $allowed
        }
    }

    $schema
}

# Validates a requested assignment against the schema and builds the Graph payload.
# Pure: takes the schema as data and returns a result object, so it is testable offline.
# Pass an empty schema with -SkipValidation to build the payload without checking anything.
# Turns the compact "Set:Attribute:Value" command-line form into the nested hashtable that
# Resolve-CustomSecurityAttributePayload consumes, so both spellings share one validation path.
#
# The schema is passed in because it is the only thing that can decide whether
# "AgentAttributes:AgentApprovalStatus:New" means the string 'New' or the one-element collection
# @('New'). The string form has no syntax for that distinction and Graph rejects the wrong one.
function ConvertFrom-CustomSecurityAttributeSpec {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][AllowNull()][object[]] $Spec,
        [hashtable] $Schema
    )

    $problems = @()
    $entries  = @()
    $form     = "Expected 'AttributeSet:Attribute:Value', for example " +
                "'AgentAttributes:AgentEnvironment:Production', or " +
                "'AgentAttributes:AgentApprovalStatus:New,In_Review' for a multi-valued attribute."

    foreach ($item in $Spec) {
        if ($null -eq $item) { continue }

        # A hashtable stays supported, and is the escape hatch for a value that itself contains a
        # comma. Its values are taken verbatim; only the string form is parsed.
        if ($item -is [System.Collections.IDictionary]) {
            foreach ($setName in @($item.Keys)) {
                $setValue = $item[$setName]
                if ($setValue -isnot [System.Collections.IDictionary]) {
                    # Not something this function can flatten. Pass it through so the resolver can
                    # explain the required shape, which it already words well.
                    $entries += [pscustomobject]@{ Set = [string]$setName; Attribute = $null; Value = $setValue }
                    continue
                }
                foreach ($attrName in @($setValue.Keys)) {
                    $entries += [pscustomobject]@{ Set = [string]$setName; Attribute = [string]$attrName; Value = $setValue[$attrName] }
                }
            }
            continue
        }

        if ($item -isnot [string]) {
            $problems += "Custom security attribute entry of type $($item.GetType().Name) is not supported. $form"
            continue
        }

        $text = $item.Trim()
        if ($text -eq '') { continue }

        # Only the first two colons separate, so a value may itself contain colons (a URL, a time).
        $parts = $text.Split([char[]]':', 3)
        if ($parts.Count -lt 3) {
            $problems += "'$item' is not a valid custom security attribute. $form"
            continue
        }

        $setName  = $parts[0].Trim()
        $attrName = $parts[1].Trim()
        $rawValue = $parts[2].Trim()

        if ($setName -eq '' -or $attrName -eq '') {
            $problems += "'$item' is missing the attribute set or the attribute name. $form"
            continue
        }

        # An empty value clears the assignment, matching what $null does in the hashtable form.
        if ($rawValue -eq '') {
            $entries += [pscustomobject]@{ Set = $setName; Attribute = $attrName; Value = $null }
            continue
        }

        $pieces = @($rawValue.Split(',') | ForEach-Object { $_.Trim() })
        if (@($pieces | Where-Object { $_ -eq '' }).Count -gt 0) {
            $problems += "'$item' has an empty value between commas. Remove the stray comma, or use the hashtable form if the value itself contains one."
            continue
        }

        # Look the definition up case-sensitively. A near miss is deliberately NOT reported here:
        # the resolver words the case-sensitivity advice properly and will catch it a moment later.
        $attrSchema = $null
        if ($Schema -and $Schema.Count -gt 0) {
            $exactSet = @($Schema.Keys | Where-Object { $_ -ceq $setName })
            if ($exactSet.Count -ge 1) {
                $setSchema = $Schema[$exactSet[0]]
                $exactAttr = @($setSchema.Attributes.Keys | Where-Object { $_ -ceq $attrName })
                if ($exactAttr.Count -ge 1) { $attrSchema = $setSchema.Attributes[$exactAttr[0]] }
            }
        }

        # Everything typed on a command line arrives as a string, so an Integer or Boolean
        # attribute has to be converted here or it would be rejected for the wrong reason.
        $attrType = if ($attrSchema -and $attrSchema.Type) { $attrSchema.Type } else { 'String' }
        $typed    = @()
        $badValue = $false
        foreach ($piece in $pieces) {
            switch ($attrType) {
                'Integer' {
                    $parsedInt = 0
                    if ([int]::TryParse($piece, [ref]$parsedInt)) { $typed += $parsedInt }
                    else {
                        $problems += "Value '$piece' for '$setName/$attrName' is not a whole number, and that attribute is an Integer attribute."
                        $badValue = $true
                    }
                }
                'Boolean' {
                    $parsedBool = $false
                    if ([bool]::TryParse($piece, [ref]$parsedBool)) { $typed += $parsedBool }
                    else {
                        $problems += "Value '$piece' for '$setName/$attrName' is not true or false, and that attribute is a Boolean attribute."
                        $badValue = $true
                    }
                }
                default { $typed += $piece }
            }
        }
        if ($badValue) { continue }

        if ($attrSchema -and -not $attrSchema.IsCollection -and $typed.Count -gt 1) {
            $problems += "'$setName/$attrName' is single-valued, but '$rawValue' supplies $($typed.Count) comma-separated values. If the value itself contains a comma, pass that attribute as a hashtable instead."
            continue
        }

        # The definition decides the cardinality. With no definition the only signal available is
        # whether a comma was used, which is why -SkipCustomSecurityAttributeValidation cannot
        # express a single-element collection in this form.
        $isCollection = if ($attrSchema) { [bool]$attrSchema.IsCollection } else { $typed.Count -gt 1 }

        # Assigned with a plain statement rather than $(if ...), because a subexpression enumerates
        # its output and would quietly turn a one-element collection back into a scalar.
        $entryValue = $null
        if ($isCollection) { $entryValue = @($typed) } else { $entryValue = $typed[0] }

        $entries += [pscustomobject]@{
            Set = $setName; Attribute = $attrName
            Value = $entryValue
        }
    }

    # Build the nested hashtable. PowerShell hashtables match keys case-insensitively, so two
    # spellings of one name would silently collapse and the wrong-case one would never be
    # reported - exactly the failure this feature exists to prevent. Detect it explicitly.
    $requested  = @{}
    $setCasing  = @{}
    $attrCasing = @{}

    foreach ($entry in $entries) {
        $setName = $entry.Set
        $setKey  = $setName.ToLowerInvariant()

        if ($setCasing.ContainsKey($setKey)) {
            if ($setCasing[$setKey] -cne $setName) {
                $problems += "Attribute set '$setName' and '$($setCasing[$setKey])' differ only in case. Attribute set names are case-sensitive; use one spelling."
                continue
            }
        }
        else { $setCasing[$setKey] = $setName }

        if ($null -eq $entry.Attribute) {
            $requested[$setName] = $entry.Value
            continue
        }

        if (-not $requested.ContainsKey($setName)) { $requested[$setName] = @{} }

        $attrKey = "$setKey/$($entry.Attribute.ToLowerInvariant())"
        if ($attrCasing.ContainsKey($attrKey)) {
            if ($attrCasing[$attrKey] -cne $entry.Attribute) {
                $problems += "Attribute '$setName/$($entry.Attribute)' and '$setName/$($attrCasing[$attrKey])' differ only in case. Attribute names are case-sensitive; use one spelling."
            }
            else {
                $problems += "Attribute '$setName/$($entry.Attribute)' is assigned more than once. Give it a single entry, using commas for a multi-valued attribute."
            }
            continue
        }
        $attrCasing[$attrKey] = $entry.Attribute

        $requested[$setName][$entry.Attribute] = $entry.Value
    }

    [pscustomobject]@{
        Requested = $requested
        Problem   = @($problems)
        IsValid   = (@($problems).Count -eq 0)
    }
}

function Resolve-CustomSecurityAttributePayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable] $Requested,
        [hashtable] $Schema,
        [switch]    $SkipValidation
    )

    $problems = @()
    $warnings = @()
    $payload  = @{}
    $flat     = @()

    foreach ($setName in @($Requested.Keys | Sort-Object)) {
        $setValue = $Requested[$setName]
        if ($setValue -isnot [hashtable] -and $setValue -isnot [System.Collections.IDictionary]) {
            $problems += "Attribute set '$setName' must be a hashtable of attribute name to value, for example @{ $setName = @{ MyAttribute = 'Value' } }."
            continue
        }

        $setSchema = $null
        if (-not $SkipValidation) {
            # A PowerShell hashtable compares keys case-INSENSITIVELY, so ContainsKey would accept
            # 'agentattributes' and then send that exact spelling to Graph, which rejects it. The
            # lookup therefore has to be case-sensitive regardless of how the schema was built.
            $exactSet = @($Schema.Keys | Where-Object { $_ -ceq $setName })
            if ($exactSet.Count -ge 1) {
                $setSchema = $Schema[$exactSet[0]]
            }
            else {
                # Case is the overwhelmingly likely cause, so say so rather than "does not exist".
                $near = @($Schema.Keys | Where-Object { $_ -ieq $setName })
                if ($near.Count -gt 0) {
                    $problems += "Attribute set '$setName' does not exist, but '$($near[0])' does. Attribute set names are case-sensitive."
                }
                else {
                    $known = if ($Schema.Keys.Count) { ($Schema.Keys | Sort-Object) -join ', ' } else { '(none defined in this tenant)' }
                    $problems += "Attribute set '$setName' does not exist in this tenant. Defined sets: $known."
                }
                continue
            }
        }

        $entry = @{ '@odata.type' = '#Microsoft.DirectoryServices.CustomSecurityAttributeValue' }
        $wrote = $false

        foreach ($attrName in @($setValue.Keys | Sort-Object)) {
            $raw = $setValue[$attrName]

            # A null clears the assignment; there is nothing to validate.
            if ($null -eq $raw) {
                $entry[$attrName] = $null
                $flat += [pscustomobject]@{ Set = $setName; Attribute = $attrName; Value = $null; IsCollection = $false }
                $wrote = $true
                continue
            }

            $isArray = $raw -is [System.Array] -or ($raw -is [System.Collections.IList] -and $raw -isnot [string])
            $values  = @($raw)

            $attrSchema = $null
            if (-not $SkipValidation) {
                # Case-sensitive for the same reason as the set name above.
                $exactAttr = @($setSchema.Attributes.Keys | Where-Object { $_ -ceq $attrName })
                if ($exactAttr.Count -ge 1) {
                    $attrSchema = $setSchema.Attributes[$exactAttr[0]]
                }
                else {
                    $near = @($setSchema.Attributes.Keys | Where-Object { $_ -ieq $attrName })
                    if ($near.Count -gt 0) {
                        $problems += "Attribute '$setName/$attrName' does not exist, but '$setName/$($near[0])' does. Attribute names are case-sensitive."
                    }
                    else {
                        $known = if ($setSchema.Attributes.Keys.Count) { ($setSchema.Attributes.Keys | Sort-Object) -join ', ' } else { '(none)' }
                        $problems += "Attribute '$attrName' does not exist in set '$setName'. Attributes in that set: $known."
                    }
                    continue
                }

                if ($attrSchema.Status -and $attrSchema.Status -ne 'Available') {
                    $warnings += "Attribute '$setName/$attrName' is $($attrSchema.Status); assigning it may be refused or may stop working."
                }

                # Cardinality. Graph reports these as type errors that name a .NET type, so
                # translate into the vocabulary the caller actually used.
                if ($attrSchema.IsCollection -and -not $isArray) {
                    $problems += "Attribute '$setName/$attrName' is multi-valued, so it needs an array: @{ $attrName = @('$raw') }."
                    continue
                }
                if (-not $attrSchema.IsCollection -and $isArray) {
                    $problems += "Attribute '$setName/$attrName' is single-valued, so it takes one value, not an array of $($values.Count)."
                    continue
                }

                foreach ($value in $values) {
                    switch ($attrSchema.Type) {
                        'Integer' {
                            if ($value -isnot [int] -and $value -isnot [long]) {
                                $problems += "Attribute '$setName/$attrName' is an Integer attribute; '$value' is $($value.GetType().Name)."
                            }
                        }
                        'Boolean' {
                            if ($value -isnot [bool]) {
                                $problems += "Attribute '$setName/$attrName' is a Boolean attribute; '$value' is $($value.GetType().Name)."
                            }
                        }
                        default {
                            if ($value -isnot [string]) {
                                $problems += "Attribute '$setName/$attrName' is a String attribute; '$value' is $($value.GetType().Name)."
                            }
                        }
                    }
                }

                if ($attrSchema.UsePredefinedOnly) {
                    foreach ($value in $values) {
                        if (@($attrSchema.AllowedValues) -ccontains [string]$value) { continue }
                        $ci = @($attrSchema.AllowedValues | Where-Object { $_ -ieq [string]$value })
                        if ($ci.Count -gt 0) {
                            $problems += "Value '$value' for '$setName/$attrName' is not allowed, but '$($ci[0])' is. Predefined values are case-sensitive."
                        }
                        else {
                            $allowedList = if (@($attrSchema.AllowedValues).Count) { (@($attrSchema.AllowedValues) | Sort-Object) -join ', ' } else { '(none active)' }
                            $problems += "Value '$value' is not an active predefined value for '$setName/$attrName'. Allowed: $allowedList."
                        }
                    }
                }
            }

            $treatAsCollection = if ($attrSchema) { [bool]$attrSchema.IsCollection } else { $isArray }
            if ($treatAsCollection) {
                # The annotation is what tells Graph to treat a one-element array as a collection
                # rather than a scalar, so it is always sent for a multi-valued attribute.
                $odataType = switch ($(if ($attrSchema) { $attrSchema.Type } else { 'String' })) {
                    'Integer' { '#Collection(Int32)' }
                    'Boolean' { '#Collection(Boolean)' }
                    default   { '#Collection(String)' }
                }
                $entry["$attrName@odata.type"] = $odataType
                $entry[$attrName] = @($values)
            }
            else {
                $entry[$attrName] = $values[0]
            }

            # Same reason as above: $(if ...) would enumerate a one-element array back to a scalar,
            # and the read-back comparison would then be measuring the wrong shape.
            $flatValue = $null
            if ($treatAsCollection) { $flatValue = @($values) } else { $flatValue = $values[0] }

            $flat += [pscustomobject]@{
                Set = $setName; Attribute = $attrName
                Value = $flatValue
                IsCollection = $treatAsCollection
            }
            $wrote = $true
        }

        if ($wrote) { $payload[$setName] = $entry }
    }

    [pscustomobject]@{
        Payload    = $payload
        Assignment = @($flat)
        Problem    = @($problems)
        Warning    = @($warnings)
        IsValid    = (@($problems).Count -eq 0)
    }
}

# Reads what is actually on the object and reports which requested assignments are missing, so a
# run never claims an assignment the directory did not keep.
function Compare-CustomSecurityAttribute {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Requested,
        $Actual
    )

    $missing = @()
    foreach ($item in $Requested) {
        $set  = $item.Set
        $name = $item.Attribute

        if ($null -eq $Actual -or -not (Test-HasProperty $Actual $set)) {
            if ($null -ne $item.Value) { $missing += "$set/$name" }
            continue
        }
        $setValue = $Actual.$set
        if (-not (Test-HasProperty $setValue $name)) {
            if ($null -ne $item.Value) { $missing += "$set/$name" }
            continue
        }

        $onObject = $setValue.$name
        if ($null -eq $item.Value) {
            # A null was a deliberate removal, so anything still present is a failure to remove.
            if ($null -ne $onObject) { $missing += "$set/$name (still present)" }
            continue
        }

        if ($item.IsCollection) {
            $want = @($item.Value | ForEach-Object { [string]$_ } | Sort-Object)
            $have = @(@($onObject) | ForEach-Object { [string]$_ } | Sort-Object)
            if (($want -join "`u{241F}") -cne ($have -join "`u{241F}")) { $missing += "$set/$name" }
        }
        elseif ([string]$onObject -cne [string]$item.Value) {
            $missing += "$set/$name"
        }
    }
    @($missing)
}

function Get-GraphErrorInfo {
    param($ErrorRecord)

    # Graph error codes that map deterministically to an HTTP status.
    $codeToStatus = @{
        'Request_ResourceNotFound'                = 404
        'ResourceNotFound'                        = 404
        'itemNotFound'                            = 404
        'Request_BadRequest'                      = 400
        'badRequest'                              = 400
        'Authorization_RequestDenied'             = 403
        'accessDenied'                            = 403
        'InvalidAuthenticationToken'              = 401
        'Request_MultipleObjectsWithSameKeyValue' = 409
        'activityLimitReached'                    = 429
        'serviceNotAvailable'                     = 503
    }

    $rawMessage = ''
    if ($ErrorRecord.Exception) { $rawMessage = [string]$ErrorRecord.Exception.Message }

    # 1) Structured status straight off the exception, when the transport exposes one.
    $status = $null
    foreach ($prop in 'StatusCode', 'Response') {
        if ($null -ne $status) { break }
        try {
            if (-not (Test-HasProperty $ErrorRecord.Exception $prop)) { continue }
            $candidate = $ErrorRecord.Exception.$prop
            if ($null -eq $candidate) { continue }
            if ($prop -eq 'Response') {
                if (-not (Test-HasProperty $candidate 'StatusCode')) { continue }
                $candidate = $candidate.StatusCode
            }
            $status = [int]$candidate
        } catch { $status = $null }
    }

    # 2) Parse the Graph error body for the code and human-readable message.
    $code    = $null
    $message = $rawMessage
    if ($ErrorRecord.ErrorDetails -and $ErrorRecord.ErrorDetails.Message) {
        $details = [string]$ErrorRecord.ErrorDetails.Message
        try {
            $parsed = $details | ConvertFrom-Json -ErrorAction Stop
            if (Test-HasProperty $parsed 'error') {
                if (Test-HasProperty $parsed.error 'code')    { $code    = [string]$parsed.error.code }
                if (Test-HasProperty $parsed.error 'message') { $message = [string]$parsed.error.message }
            }
        } catch {
            $message = $details
        }
    }

    # 3) Scrape the ORIGINAL exception text - the parsed body above no longer carries the status.
    if ($null -eq $status) {
        foreach ($pattern in 'status code does not indicate success:\s*(\d{3})',
                             '\((\d{3})\)',
                             '\bHTTP[/ ]?[\d.]*\s+(\d{3})\b',
                             '\bstatus(?:Code)?[:= ]+(\d{3})\b',
                             # Invoke-Graph rethrows as "... failed [403 Code]: message". Callers
                             # that catch Invoke-Graph and re-parse (the agent identity denial
                             # advice) only see that string, so the status must be recoverable
                             # from it - otherwise the advice silently never fires.
                             'failed \[(\d{3})[\s\]]') {
            if ($rawMessage -match $pattern) { $status = [int]$Matches[1]; break }
        }
    }

    # 4) Last resort: infer from the Graph error code.
    if ($null -eq $status -and $code -and $codeToStatus.ContainsKey($code)) {
        $status = $codeToStatus[$code]
    }

    [pscustomobject]@{ Status = $status; Code = $code; Message = $message }
}

# Client-credentials token for a specific app, used to borrow the blueprint's identity for the
# single call that requires it. Deliberately separate from Connect-GraphSession: Connect-MgGraph
# is process-wide, so switching the session to the blueprint would take every read down with it.
function Get-AppOnlyGraphToken {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string] $TenantId,
        [Parameter(Mandatory)][string] $ClientId,
        # Reject null at this boundary before building the token request.
        [Parameter(Mandatory)][ValidateNotNull()][object] $ClientSecret
    )

    $ownsSecure = $ClientSecret -is [string]
    $secure     = ConvertTo-SecureStringValue -Value $ClientSecret -Name 'ClientSecret'
    $plain      = [Net.NetworkCredential]::new('', $secure).Password
    $body       = @{
        client_id     = $ClientId
        client_secret = $plain
        scope         = 'https://graph.microsoft.com/.default'
        grant_type    = 'client_credentials'
    }

    try {
        $response = Invoke-RestMethod -Method POST -ErrorAction Stop `
            -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" -Body $body
    }
    catch {
        $detail = ''
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $detail = " $($_.ErrorDetails.Message)" }
        throw "Could not get a token for app $ClientId in tenant ${TenantId}: $($_.Exception.Message)$detail"
    }
    finally {
        $body.client_secret = $null
        $plain = $null
        if ($ownsSecure) { $secure.Dispose() }
    }

    if (-not (Test-HasProperty $response 'access_token')) {
        throw "Token endpoint returned no access_token for app $ClientId."
    }
    [string]$response.access_token
}

function Invoke-Graph {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PATCH', 'PUT', 'DELETE')][string] $Method,
        [Parameter(Mandatory)][string] $Uri,
        $Body,
        [hashtable] $ExtraHeader,
        [int]    $MaxAttempts = 6,
        [switch] $TolerateNotFound,
        [switch] $TolerateConflict,
        [switch] $TolerateBadRequest,
        [switch] $TolerateForbidden,
        [switch] $RetryOnNotFound,  # for replication lag right after a create
        [switch] $RetryOnBlueprintNotReady,  # A365 has not yet observed a just-created principal
        # Sends this one call with an explicit bearer token instead of the process-wide
        # Connect-MgGraph context, so a narrowly-scoped credential can be borrowed for a single
        # request without disturbing the session every other call depends on.
        [string] $BearerToken
    )

    if ($Uri -notmatch '^https?://') { $Uri = "$script:GraphRoot$Uri" }

    $json = $null
    if ($null -ne $Body) {
        $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 25 }
    }

    $headers = @{ 'OData-Version' = '4.0' }
    if ($ExtraHeader) { foreach ($k in $ExtraHeader.Keys) { $headers[$k] = $ExtraHeader[$k] } }

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            if ($BearerToken) {
                # Invoke-MgGraphRequest always uses the connected session, so an explicit token
                # has to go out over the raw REST client. Invoke-RestMethod parses JSON into the
                # same PSObject shape -OutputType PSObject produces, and populates
                # ErrorDetails.Message / Response.StatusCode, which Get-GraphErrorInfo reads.
                $webParams = @{
                    Method      = $Method
                    Uri         = $Uri
                    Headers     = $headers + @{ Authorization = "Bearer $BearerToken" }
                    ErrorAction = 'Stop'
                }
                if ($json) {
                    $webParams.Body        = $json
                    $webParams.ContentType = 'application/json'
                }
                return Invoke-RestMethod @webParams
            }

            $reqParams = @{
                Method      = $Method
                Uri         = $Uri
                Headers     = $headers
                OutputType  = 'PSObject'
                ErrorAction = 'Stop'
            }
            if ($json) {
                $reqParams.Body        = $json
                $reqParams.ContentType = 'application/json'
            }
            Write-A365LogGraphRequest -Method $Method -Uri $Uri -Body $Body -Attempt $attempt -MaxAttempts $MaxAttempts
            $swGraph = [Diagnostics.Stopwatch]::StartNew()
            $graphResult = Invoke-MgGraphRequest @reqParams
            $swGraph.Stop()
            Write-A365LogGraphResponse -Method $Method -Uri $Uri -Response $graphResult -DurationMs ([int]$swGraph.ElapsedMilliseconds)
            return $graphResult
        }
        catch {
            $info = Get-GraphErrorInfo -ErrorRecord $_
            # Logged as a warning, not a failure: the caller may tolerate this status or retry
            # it. Only the final throw below counts as a failed Graph call.
            Write-A365Log -Level WARN -Message ("<-- HTTP {0} on {1} {2}" -f $info.Status, $Method, $Uri) -Detail $info.Message

            # Creating an agent identity moments after its blueprint principal was created is
            # refused with a 403 Authorization_RequestDenied whose message says the blueprint
            # principal "does not exist". It does exist - the A365 backend simply has not observed
            # it yet. This is propagation delay wearing an authorization error's clothes, so it is
            # retried rather than surfaced as a permission problem.
            $blueprintNotReady = $RetryOnBlueprintNotReady -and $info.Status -eq 403 -and
                                 ([string]$info.Message) -match '(?i)agent blueprint principal.*does not exist'

            if ($info.Status -eq 404 -and $TolerateNotFound -and -not $RetryOnNotFound) { return $null }
            if ($info.Status -eq 400 -and $TolerateBadRequest) { return $null }
            if ($info.Status -in 401, 403 -and $TolerateForbidden -and -not $blueprintNotReady) {
                Write-Verbose "$($info.Status) tolerated for $Method $Uri"
                return $null
            }
            if ($info.Status -eq 409 -and $TolerateConflict) {
                Write-Verbose "409 Conflict tolerated for $Method $Uri"
                return $null
            }

            $transient = ($info.Status -in 429, 500, 502, 503, 504) -or
                         ($info.Status -eq 404 -and $RetryOnNotFound) -or
                         $blueprintNotReady

            if ($transient -and $attempt -lt $MaxAttempts) {
                $delay = [Math]::Min([Math]::Pow(2, $attempt), 30)
                if ($blueprintNotReady) {
                    Write-Host "  Blueprint principal not visible to Agent 365 yet - retry $attempt/$MaxAttempts in ${delay}s" -ForegroundColor Yellow
                }
                else {
                    Write-Verbose "Transient $($info.Status) on $Method $Uri - retry $attempt/$MaxAttempts in ${delay}s"
                }
                Start-Sleep -Seconds $delay
                continue
            }

            if ($blueprintNotReady) {
                throw ('Agent 365 still cannot see the blueprint principal after ' +
                       "$MaxAttempts attempts. The principal does exist in the directory, so this is a " +
                       'propagation delay, not a permission problem - wait a minute and re-run. ' +
                       "Original error: $($info.Message)")
            }

            Write-A365LogGraphResponse -Method $Method -Uri $Uri -Status $info.Status -AsFailure -ErrorText $info.Message
            throw "Graph $Method $Uri failed [$($info.Status) $($info.Code)]: $($info.Message)"
        }
    }
}

function Write-Step {
    param([int]$Number, [string]$Text)
    Write-Host ''
    Write-Host "=== Step $Number : $Text" -ForegroundColor Cyan
}

# Printed once, at the end of a run, when any owner write was refused.
function Get-OwnerDenialAdvice {
    $lines = @(
        'Adding an owner needs more than the AgentIdentity.* roles:'
        '  Application permissions : Application.ReadWrite.All (or Application.ReadWrite.OwnedBy)'
        '                            AND Directory.Read.All'
        '  Delegated permissions   : Application.ReadWrite.All AND Directory.Read.All, and the'
        '                            signed-in user must hold Application Administrator or'
        '                            Cloud Application Administrator in Microsoft Entra.'
        'Re-run New-A365AutomationApp.ps1 to add the application roles, or assign the directory'
        'role, then re-run this script - it will add the missing owners without duplicating anything.'
    )
    return ($lines -join [Environment]::NewLine)
}
function Test-IsGuid {
    param([string] $Value)
    return $Value -match '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$'
}

function ConvertTo-ODataLiteral {
    param([string] $Value)
    return ($Value -replace "'", "''")
}

# ---------------------------------------------------------------------------
# Principal resolution
# ---------------------------------------------------------------------------

# Sponsors may be users, Microsoft 365 groups or dynamic-membership groups. Static security groups
# and role-assignable groups are rejected by the service, so they are caught here with a clear
# message instead of surfacing as an opaque 400 halfway through provisioning.
function Resolve-SponsorPrincipal {
    param([Parameter(Mandatory)][string] $Identifier)

    $groupSelect = 'id,displayName,groupTypes,securityEnabled,mailEnabled,isAssignableToRole'

    function Assert-SponsorGroup {
        param($Group, [string] $Original)

        $groupTypes = @()
        if (Test-HasProperty $Group 'groupTypes') { $groupTypes = @($Group.groupTypes) }

        if ((Test-HasProperty $Group 'isAssignableToRole') -and $Group.isAssignableToRole -eq $true) {
            throw "Sponsor '$Original' is a role-assignable group, which Entra ID does not accept as a sponsor. Use a user, a Microsoft 365 group, or a dynamic-membership group."
        }
        if (($groupTypes -contains 'Unified') -or ($groupTypes -contains 'DynamicMembership')) { return }

        throw "Sponsor '$Original' is a static security group, which Entra ID does not accept as a sponsor. Use a user, a Microsoft 365 group, or a dynamic-membership group."
    }

    if (Test-IsGuid $Identifier) {
        $u = Invoke-Graph -Method GET -Uri "/users/$Identifier`?`$select=id,userPrincipalName" -TolerateNotFound
        if ($u) { return [pscustomobject]@{ Id = $u.id; Segment = 'users'; Type = 'user'; Display = $u.userPrincipalName } }

        $g = Invoke-Graph -Method GET -Uri "/groups/$Identifier`?`$select=$groupSelect" -TolerateNotFound
        if ($g) {
            Assert-SponsorGroup -Group $g -Original $Identifier
            return [pscustomobject]@{ Id = $g.id; Segment = 'groups'; Type = 'group'; Display = $g.displayName }
        }
        throw "Could not resolve sponsor '$Identifier' to a user or group in tenant $TenantId."
    }

    $encoded = [uri]::EscapeDataString($Identifier)
    $literal = ConvertTo-ODataLiteral $Identifier

    $user = Invoke-Graph -Method GET -Uri "/users/$encoded`?`$select=id,userPrincipalName" -TolerateNotFound
    if ($user) { return [pscustomobject]@{ Id = $user.id; Segment = 'users'; Type = 'user'; Display = $user.userPrincipalName } }

    $filter  = "displayName eq '$literal' or mailNickname eq '$literal'"
    $groups  = Invoke-Graph -Method GET `
        -Uri "/groups?`$select=$groupSelect&`$filter=$([uri]::EscapeDataString($filter))" -TolerateNotFound
    $matched = @()
    if (Test-HasProperty $groups 'value') { $matched = @($groups.value) }

    if ($matched.Count -gt 1) {
        throw "Sponsor '$Identifier' matched $($matched.Count) groups. Pass the object ID instead."
    }
    if ($matched.Count -eq 1) {
        Assert-SponsorGroup -Group $matched[0] -Original $Identifier
        return [pscustomobject]@{ Id = $matched[0].id; Segment = 'groups'; Type = 'group'; Display = $matched[0].displayName }
    }

    throw "Could not resolve sponsor '$Identifier' to a user or group in tenant $TenantId."
}

# Owners must be users or service principals; Entra ID rejects groups as owners.
function Resolve-OwnerPrincipal {
    param([Parameter(Mandatory)][string] $Identifier)

    if (Test-IsGuid $Identifier) {
        # A GUID is ambiguous. owners/$ref needs a directoryObject id, but the GUID people have to
        # hand - the one shown as "Application (client) ID" in the portal and passed to -ClientId -
        # is the appId, which is a DIFFERENT value. Sending an appId to owners/$ref fails with
        #   404 Request_ResourceNotFound: Resource '<guid>' does not exist
        # which names the GUID but never explains that the wrong kind of GUID was used.
        $probe = Invoke-Graph -Method GET -Uri "/directoryObjects/$Identifier" -TolerateNotFound -TolerateForbidden
        if ($probe) {
            $display = $Identifier
            if ((Test-HasProperty $probe 'displayName') -and $probe.displayName) { $display = [string]$probe.displayName }
            $probeType = ''
            if (Test-HasProperty $probe '@odata.type') { $probeType = [string]$probe.'@odata.type' }

            # An application object IS a directory object, so the probe above succeeds - but Entra ID
            # still refuses it as an owner:
            #   400 The reference target 'Application_<guid>' of type 'Application' is invalid for
            #       the 'owners' reference.
            # An app registration has THREE ids (appId, application object id, service principal
            # object id) and only the last one can own anything, so translate rather than fail.
            if ($probeType -eq '#microsoft.graph.application') {
                $ownerAppId = ''
                if (Test-HasProperty $probe 'appId') { $ownerAppId = [string]$probe.appId }

                $spForApp = @()
                if ($ownerAppId) {
                    $spLookup = Invoke-Graph -Method GET `
                        -Uri "/servicePrincipals?`$select=id,displayName,appId&`$filter=appId eq '$(ConvertTo-ODataLiteral $ownerAppId)'" `
                        -TolerateNotFound -TolerateForbidden
                    if (Test-HasProperty $spLookup 'value') { $spForApp = @($spLookup.value) }
                }

                if ($spForApp.Count -eq 1) {
                    Write-Host "  Owner '$Identifier' is an application object, which cannot be an owner - using its service principal $($spForApp[0].id) ($display)." -ForegroundColor DarkGray
                    return [pscustomobject]@{ Id = $spForApp[0].id; Type = 'servicePrincipal'; Display = $display }
                }

                throw @"
Owner '$Identifier' is the APPLICATION object for '$display' (appId $ownerAppId), and Entra ID
rejects an application as an owner:

    400 The reference target 'Application_$Identifier' of type 'Application' is invalid for the 'owners' reference.

An owner must be a user or a service principal. That application has no service principal in tenant
$TenantId, so create one first:

    New-MgServicePrincipal -AppId $ownerAppId

then re-run. You may pass the appId, the service principal object id, or a UPN - this script
resolves all three.
"@
            }

            return [pscustomobject]@{ Id = $Identifier; Type = 'directoryObject'; Display = $display }
        }

        # Not usable as an object id. Try it as an application's appId before giving up.
        $byAppId = Invoke-Graph -Method GET `
            -Uri "/servicePrincipals?`$select=id,displayName,appId&`$filter=appId eq '$(ConvertTo-ODataLiteral $Identifier)'" `
            -TolerateNotFound -TolerateForbidden
        $appMatched = @()
        $appIdQueryWorked = Test-HasProperty $byAppId 'value'
        if ($appIdQueryWorked) { $appMatched = @($byAppId.value) }

        if ($appMatched.Count -eq 1) {
            Write-Host "  Owner '$Identifier' is an appId, not an object id - using its service principal $($appMatched[0].id) ($($appMatched[0].displayName))." -ForegroundColor DarkGray
            return [pscustomobject]@{ Id = $appMatched[0].id; Type = 'servicePrincipal'; Display = [string]$appMatched[0].displayName }
        }

        # Only complain when directory reads demonstrably work, so a token that simply cannot read
        # is not accused of passing a bad id.
        if ($appIdQueryWorked) {
            throw @"
Owner '$Identifier' is neither a directory object id nor an application appId in tenant $TenantId.

owners/`$ref needs the OBJECT id. If you copied this GUID from the app registration blade it is
probably the "Application (client) ID" (appId), and the object id is a different GUID. Find it with:

    Get-MgServicePrincipal -Filter "appId eq '$Identifier'" | Select-Object Id, DisplayName

or pass the owner by display name or UPN and let this script resolve it.
"@
        }

        # Could not verify either way - send it unchanged, as before.
        return [pscustomobject]@{ Id = $Identifier; Type = 'directoryObject'; Display = $Identifier }
    }

    $encoded = [uri]::EscapeDataString($Identifier)
    $literal = ConvertTo-ODataLiteral $Identifier

    $user = Invoke-Graph -Method GET -Uri "/users/$encoded`?`$select=id,userPrincipalName" -TolerateNotFound
    if ($user) { return [pscustomobject]@{ Id = $user.id; Type = 'user'; Display = $user.userPrincipalName } }

    $spFilter = "displayName eq '$literal' or appId eq '$literal'"
    $sps = Invoke-Graph -Method GET `
        -Uri "/servicePrincipals?`$select=id,displayName,appId&`$filter=$([uri]::EscapeDataString($spFilter))" `
        -TolerateNotFound
    $spMatched = @()
    if (Test-HasProperty $sps 'value') { $spMatched = @($sps.value) }
    if ($spMatched.Count -eq 1) {
        return [pscustomobject]@{ Id = $spMatched[0].id; Type = 'servicePrincipal'; Display = $spMatched[0].displayName }
    }
    if ($spMatched.Count -gt 1) {
        throw "Owner '$Identifier' matched $($spMatched.Count) service principals. Pass the object ID instead."
    }

    $groupFilter = "displayName eq '$literal' or mailNickname eq '$literal'"
    $groups = Invoke-Graph -Method GET `
        -Uri "/groups?`$select=id,displayName&`$filter=$([uri]::EscapeDataString($groupFilter))" -TolerateNotFound
    if ((Test-HasProperty $groups 'value') -and @($groups.value).Count -gt 0) {
        throw "Owner '$Identifier' resolves to a group. Entra ID does not accept groups as owners of an agent identity - pass a user UPN or a service principal instead. (Groups are only valid for -Sponsor.)"
    }

    throw "Could not resolve owner '$Identifier' to a user or service principal in tenant $TenantId."
}

# ---------------------------------------------------------------------------
# Authentication - application (client credentials) or delegated
#
#   Application : unattended. Permissions come from Microsoft Graph APPLICATION app roles granted
#                 to the app registration, so -Scopes is neither sent nor honoured. Create that
#                 app registration with New-A365AutomationApp.ps1.
#   Delegated   : -Interactive signs a human in and consents the delegated scopes.
#
# A method must be chosen explicitly so an unattended run can never silently block on a prompt.
# ---------------------------------------------------------------------------
$script:MicrosoftGraphAppId = '00000003-0000-0000-c000-000000000000'

function Get-MissingAppRole {
    # Compares the app roles actually granted to the running application against the ones this
    # script needs, so a missing grant surfaces here rather than as a 403 halfway through.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]   $ClientId,
        [Parameter(Mandatory)][string[]] $RequiredAppRole
    )

    $sp = Invoke-Graph -Method GET -Uri "/servicePrincipals(appId='$ClientId')?`$select=id" `
        -TolerateNotFound -MaxAttempts 2
    if (-not (Test-HasProperty $sp 'id')) { throw "No service principal found for appId $ClientId." }

    $graphSp = Invoke-Graph -Method GET `
        -Uri "/servicePrincipals(appId='$script:MicrosoftGraphAppId')?`$select=id,appRoles" -MaxAttempts 2

    $roleNameById = @{}
    if (Test-HasProperty $graphSp 'appRoles') {
        foreach ($role in @($graphSp.appRoles)) {
            if ((Test-HasProperty $role 'id') -and (Test-HasProperty $role 'value')) {
                $roleNameById[[string]$role.id] = [string]$role.value
            }
        }
    }

    $assigned = Invoke-Graph -Method GET `
        -Uri "/servicePrincipals/$($sp.id)/appRoleAssignments?`$select=appRoleId&`$top=999" `
        -TolerateNotFound -MaxAttempts 2

    $granted = @()
    if (Test-HasProperty $assigned 'value') {
        foreach ($a in @($assigned.value)) {
            if (-not (Test-HasProperty $a 'appRoleId')) { continue }
            $name = $roleNameById[[string]$a.appRoleId]
            if ($name) { $granted += $name }
        }
    }

    # Callers need to know what IS granted, not just what is missing: a role that was never in the
    # required set (AgentIdentity.CreateAsManager) can still make a warning about a required one
    # wrong. Only the missing list is returned, so publish the granted set alongside it.
    $script:LastGrantedAppRoles = @($granted)

    # Graph's ReadWrite roles subsume their Read counterparts, and Read subsumes ReadBasic. Without
    # this, an app holding AgentIdentity.ReadWrite.All is reported as missing AgentIdentity.Read.All -
    # a false alarm that buries whichever role is genuinely absent. Only the Read/ReadWrite direction
    # is treated as equivalent; .OwnedBy is never accepted in place of .All.
    $isSatisfied = {
        param([string] $Required)

        if ($granted -contains $Required) { return $true }

        $alternates = @()
        if ($Required -match '^(?<res>.+)\.Read\.(?<scope>All|OwnedBy)$') {
            $alternates += ('{0}.ReadWrite.{1}' -f $Matches['res'], $Matches['scope'])
        }
        elseif ($Required -match '^(?<res>.+)\.ReadBasic\.(?<scope>All|OwnedBy)$') {
            $alternates += ('{0}.Read.{1}'      -f $Matches['res'], $Matches['scope'])
            $alternates += ('{0}.ReadWrite.{1}' -f $Matches['res'], $Matches['scope'])
        }

        foreach ($alt in $alternates) { if ($granted -contains $alt) { return $true } }
        return $false
    }

    @($RequiredAppRole | Where-Object { -not (& $isSatisfied $_) })
}

# Secrets may be supplied as a SecureString, a plain string or a PSCredential. PowerShell will not
# coerce string -> SecureString on its own, and forcing every caller through ConvertTo-SecureString
# for a one-off run is friction with no security benefit once the value is already in the session.
# Plain strings do land in shell history and transcripts, so $env:A365_CLIENT_SECRET remains the
# better habit - hence the warning rather than silent acceptance.
function ConvertTo-SecureStringValue {
    [CmdletBinding()]
    [OutputType([securestring])]
    param(
        [object] $Value,
        [Parameter(Mandatory)][string] $Name
    )

    if ($null -eq $Value) { return $null }

    if ($Value -is [securestring]) {
        if ($Value.Length -eq 0) { throw "-$Name is an empty SecureString. Supply the actual secret value." }
        return $Value
    }
    if ($Value -is [pscredential]) { return $Value.Password }

    if ($Value -isnot [string]) {
        throw "-$Name must be a SecureString, a string or a PSCredential, but was [$($Value.GetType().FullName)]."
    }
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "-$Name was supplied but is empty. Omit the parameter, or pass the actual secret value."
    }

    # Built char by char so the plain text is never handed to ConvertTo-SecureString -AsPlainText.
    $secure = [securestring]::new()
    foreach ($char in $Value.ToCharArray()) { $secure.AppendChar($char) }
    $secure.MakeReadOnly()
    return $secure
}

function Get-AgentIdentityDenialAdvice {
    <#
    .SYNOPSIS
        Turns a bare "Insufficient privileges" 403 from the agent identity create into advice
        that names the role actually required.
    .DESCRIPTION
        Graph answers a missing AgentIdentity.Create.All with the generic
        Authorization_RequestDenied / "Insufficient privileges to complete the operation."
        text, which names neither the permission nor the object. Confirmed live: an application
        holding only AgentIdentity.Create.All can create an agent identity from any blueprint in
        the tenant - it does not need to own or sponsor the blueprint, and no other role is
        involved. So for this endpoint a generic denial means the role is absent from the token,
        and nothing else.

        Newly granted app roles are only present in tokens issued after the grant, so a run
        started seconds before an admin consented fails exactly like a run with no grant at all.
        That case is called out because it is otherwise indistinguishable and the fix is simply
        to wait and re-run.

        Returns $null when the failure is not this denial, so callers can rethrow untouched.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [int]    $StatusCode,
        [string] $Message,
        [string] $ErrorCode,
        [bool]   $IsAppOnly,
        [string] $ClientId,
        [string[]] $MissingAppRole = @()
    )

    if ($StatusCode -ne 403) { return $null }

    # The propagation 403 ("...Blueprint Principal ... does not exist") is a different failure with
    # its own retry path; never relabel it as a permission problem.
    if ($Message -match 'agent blueprint principal.*does not exist') { return $null }

    $isDenial = ($ErrorCode -eq 'Authorization_RequestDenied') -or ($Message -match 'Insufficient privileges')
    if (-not $isDenial) { return $null }

    $required = 'AgentIdentity.Create.All'
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("The caller is not authorized to create an agent identity. This endpoint requires $required.")

    if ($MissingAppRole -contains $required) {
        $lines.Add("The pre-flight check already reported $required as NOT granted to $ClientId - that is the cause.")
    }
    elseif ($IsAppOnly) {
        $lines.Add("Application $ClientId must hold the $required application role with admin consent granted.")
        $lines.Add('If it was granted only moments ago, the token for this run predates the grant: app role')
        $lines.Add('changes appear only in tokens issued afterwards. Wait ~2 minutes and re-run before')
        $lines.Add('changing anything else.')
    }
    else {
        $lines.Add("The signed-in user's client application must be consented for the $required delegated scope.")
    }

    $lines.Add('')
    $lines.Add('Grant it with:')
    if ($IsAppOnly) {
        $lines.Add("  .\New-A365AutomationApp.ps1 -Scenario AgentIdentity -AppId $ClientId")
    }
    else {
        $lines.Add('  .\New-A365AutomationApp.ps1 -Scenario AgentIdentity')
    }
    $lines.Add('')
    $lines.Add('Not the cause, and not worth investigating: the blueprint does not need the caller as an')
    $lines.Add('owner or sponsor, and no additional Agent* role is required for this call.')

    return ($lines -join [Environment]::NewLine)
}

function Connect-GraphSession {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $TenantId,
        [string]       $ClientId,
        [object]       $ClientSecret,
        [string]       $CertificateThumbprint,
        [System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
        [string]       $CertificatePath,
        [object]       $CertificatePassword,
        [switch]       $UseManagedIdentity,
        [object]       $AccessToken,
        [switch]       $Interactive,
        [string[]]     $DelegatedScope  = @(),
        [string[]]     $RequiredAppRole = @(),
        [switch]       $SkipPermissionCheck
    )

    if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Authentication)) {
        throw 'Microsoft.Graph.Authentication is not installed. Run: Install-Module Microsoft.Graph.Authentication -Scope CurrentUser'
    }
    # The module's own Update-TypeData calls honour $WhatIfPreference, so under -WhatIf they emit a
# dozen "What if: Performing the operation Update TypeData" lines before any real output.
# Import-Module has no -WhatIf to suppress that, so drop the preference for the import only.
$previousWhatIfPreference = $WhatIfPreference
try {
    $WhatIfPreference = $false
    Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
}
finally {
    $WhatIfPreference = $previousWhatIfPreference
}

    # Accept plain strings as well as SecureStrings, and warn about the trade-off once.
    $secretWasPlainText = $ClientSecret -is [string]
    $ClientSecret = ConvertTo-SecureStringValue -Value $ClientSecret -Name 'ClientSecret'
    $AccessToken  = ConvertTo-SecureStringValue -Value $AccessToken  -Name 'AccessToken'
    $CertificatePassword = ConvertTo-SecureStringValue -Value $CertificatePassword -Name 'CertificatePassword'

    # Keeps the secret out of command lines, shell history and transcripts.
    if ((-not $ClientSecret) -and $env:A365_CLIENT_SECRET) {
        $ClientSecret = ConvertTo-SecureStringValue -Value $env:A365_CLIENT_SECRET -Name 'ClientSecret'
        Write-Verbose 'Client secret read from $env:A365_CLIENT_SECRET.'
    }
    elseif ($secretWasPlainText) {
        Write-Warning 'A plain-text -ClientSecret was passed on the command line, where it is visible to shell history and transcripts. Prefer $env:A365_CLIENT_SECRET or a SecureString.'
    }

    $modes = @()
    if ($Interactive)        { $modes += 'Interactive' }
    if ($AccessToken)        { $modes += 'AccessToken' }
    if ($UseManagedIdentity) { $modes += 'ManagedIdentity' }
    if ($CertificateThumbprint -or $Certificate -or $CertificatePath) { $modes += 'Certificate' }
    if ($ClientSecret)       { $modes += 'ClientSecret' }

    if ($modes.Count -gt 1) {
        throw "Conflicting authentication options ($($modes -join ', ')). Supply exactly one of -ClientSecret, -CertificateThumbprint/-Certificate/-CertificatePath, -UseManagedIdentity, -AccessToken or -Interactive."
    }
    if ($modes.Count -eq 0) {
        $lead = if ($ClientId) { '-ClientId was supplied without a credential.' } else { 'No authentication method was specified.' }
        throw "$lead To run as an application pass -ClientId with -ClientSecret, -CertificateThumbprint, -Certificate or -CertificatePath (or use -UseManagedIdentity / -AccessToken). To sign in as a user pass -Interactive."
    }

    $mode = $modes[0]
    if (($mode -in @('ClientSecret', 'Certificate')) -and (-not $ClientId)) {
        throw "-ClientId is required for $mode authentication."
    }

    $connect = @{ NoWelcome = $true; ErrorAction = 'Stop' }
    switch ($mode) {
        'ClientSecret' {
            $connect.TenantId               = $TenantId
            $connect.ClientSecretCredential = [pscredential]::new($ClientId, $ClientSecret)
        }
        'Certificate' {
            $connect.TenantId = $TenantId
            $connect.ClientId = $ClientId
            if ($Certificate) {
                $connect.Certificate = $Certificate
            }
            elseif ($CertificatePath) {
                if (-not (Test-Path -LiteralPath $CertificatePath)) {
                    throw "Certificate file not found: $CertificatePath"
                }
                $pfx = (Resolve-Path -LiteralPath $CertificatePath).ProviderPath
                $connect.Certificate = if ($CertificatePassword) {
                    [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($pfx, $CertificatePassword)
                }
                else {
                    [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($pfx)
                }
            }
            else {
                $connect.CertificateThumbprint = $CertificateThumbprint
            }
        }
        'ManagedIdentity' {
            $connect.Identity = $true
            if ($ClientId) { $connect.ClientId = $ClientId }   # user-assigned identity
        }
        'AccessToken' {
            $connect.AccessToken = $AccessToken
        }
        'Interactive' {
            $connect.TenantId = $TenantId
            if ($ClientId)                   { $connect.ClientId = $ClientId }
            if ($DelegatedScope.Count -gt 0) { $connect.Scopes   = $DelegatedScope }
        }
    }

    Connect-MgGraph @connect

    $mg = Get-MgContext
    if (-not $mg) { throw 'Connect-MgGraph did not establish a Graph context.' }

    $authType   = if (Test-HasProperty $mg 'AuthType') { [string]$mg.AuthType } else { '' }
    $ctxClient  = if (Test-HasProperty $mg 'ClientId') { [string]$mg.ClientId } else { $ClientId }
    $ctxAccount = if (Test-HasProperty $mg 'Account')  { [string]$mg.Account }  else { '' }
    $appName    = if (Test-HasProperty $mg 'AppName')  { [string]$mg.AppName }  else { '' }
    $ctxTenant  = $TenantId
    if ((Test-HasProperty $mg 'TenantId') -and $mg.TenantId) { $ctxTenant = [string]$mg.TenantId }

    $isAppOnly = ($authType -eq 'AppOnly')
    if (($mode -in @('ClientSecret', 'Certificate', 'ManagedIdentity')) -and (-not $isAppOnly)) {
        throw "$mode authentication did not yield an app-only token (Get-MgContext reports AuthType '$authType')."
    }

    if ($isAppOnly) {
        $label = if ($appName) { "$appName ($ctxClient)" } else { $ctxClient }
        Write-Host "  Connected app-only as $label in tenant $ctxTenant [$mode]" -ForegroundColor Green
    }
    else {
        Write-Host "  Connected as $ctxAccount in tenant $ctxTenant [delegated, $mode]" -ForegroundColor Green
    }

    $missingRoles = @()
    if ($isAppOnly -and $RequiredAppRole.Count -gt 0) {
        if ($SkipPermissionCheck) {
            Write-Verbose 'App role pre-flight check skipped by request.'
        }
        else {
            try {
                $missingRoles = @(Get-MissingAppRole -ClientId $ctxClient -RequiredAppRole $RequiredAppRole)
                if ($missingRoles.Count -eq 0) {
                    Write-Host "  Verified $($RequiredAppRole.Count) Microsoft Graph app role(s) granted." -ForegroundColor Green
                }
                else {
                    Write-Warning "Missing Microsoft Graph app role(s): $($missingRoles -join ', ')"
                    Write-Warning 'Grant them with New-A365AutomationApp.ps1, or re-run with -SkipPermissionCheck to try anyway.'
                }
            }
            catch {
                Write-Verbose "App role pre-flight check could not run: $($_.Exception.Message)"
            }
        }
    }

    [pscustomobject]@{
        Mode            = $mode
        IsAppOnly       = $isAppOnly
        AuthType        = $authType
        TenantId        = $ctxTenant
        ClientId        = $ctxClient
        Account         = $ctxAccount
        MissingAppRoles = $missingRoles
        GrantedAppRoles = @($script:LastGrantedAppRoles)
    }
}

# ---------------------------------------------------------------------------
# Step 1 - connect
# ---------------------------------------------------------------------------

# Normalize at script scope so every consumer receives a SecureString.
$BlueprintClientSecret = ConvertTo-SecureStringValue -Value $BlueprintClientSecret -Name 'BlueprintClientSecret'

if ((-not $BlueprintClientSecret) -and $env:A365_BLUEPRINT_CLIENT_SECRET) {
    $BlueprintClientSecret = ConvertTo-SecureStringValue -Value $env:A365_BLUEPRINT_CLIENT_SECRET -Name 'BlueprintClientSecret'
    Write-Verbose 'Blueprint client secret read from $env:A365_BLUEPRINT_CLIENT_SECRET.'
}

if ($MintWithBlueprintCredential) {
    if ($Update) {
        throw '-MintWithBlueprintCredential applies to creating an agent identity, which -Update never does. Drop the switch.'
    }
    if (-not $BlueprintClientSecret) {
        throw '-MintWithBlueprintCredential needs the blueprint''s own client secret. Pass -BlueprintClientSecret or set $env:A365_BLUEPRINT_CLIENT_SECRET, or drop the switch to mint with the caller credential.'
    }
    if ($PSCmdlet.ParameterSetName -ne 'ByAppId') {
        throw '-MintWithBlueprintCredential requires -BlueprintAppId. The blueprint credential cannot read the directory, so a display name cannot be resolved to an appId under it.'
    }
}

Write-Step 1 'Connecting to Microsoft Graph'

# Delegated scopes are only sent with -Interactive. User.Read and User.ReadBasic.All have no
# application equivalent, so app-only runs use User.Read.All to resolve sponsors and owners.
$delegatedScopes = [System.Collections.Generic.List[string]]::new()
$delegatedScopes.AddRange([string[]]@(
    'AgentIdentity.Create.All',          # POST /servicePrincipals/microsoft.graph.agentIdentity
    'AgentIdentity.Read.All',            # idempotency probe
    'AgentIdentity.ReadWrite.All',       # owners
    'AgentIdentityBlueprint.Read.All',   # validate the blueprint
    'Application.Read.All',
    'User.Read',
    'User.ReadBasic.All',
    'Group.Read.All'
))

$appRoles = [System.Collections.Generic.List[string]]::new()
$appRoles.AddRange([string[]]@(
    'AgentIdentity.Create.All',          # POST /servicePrincipals/microsoft.graph.agentIdentity
    'AgentIdentity.Read.All',            # idempotency probe
    'AgentIdentity.ReadWrite.All',       # owners AND sponsors (sponsors are application-only)
    'AgentIdentityBlueprint.Read.All',   # validate the blueprint
    'Application.Read.All',
    'User.Read.All',
    'Group.Read.All'
))

if ($Disabled) {
    $delegatedScopes.Add('AgentIdentity.EnableDisable.All')
    $appRoles.Add('AgentIdentity.EnableDisable.All')
}

# When the blueprint mints the identity, the create right comes from ITS token
# (AgentIdentity.CreateAsManager, granted automatically to every blueprint app). Leaving
# AgentIdentity.Create.All in the caller's required set would report a missing permission the
# caller genuinely does not need, which is what -SkipPermissionCheck used to be needed to silence.
if ($MintWithBlueprintCredential) {
    [void]$delegatedScopes.Remove('AgentIdentity.Create.All')
    [void]$appRoles.Remove('AgentIdentity.Create.All')
}

# Owner writes are NOT covered by the AgentIdentity.* roles - they need
# Application.ReadWrite.All (or .OwnedBy) plus Directory.Read.All. Only ask for them when owners
# were actually requested, so the common no-owner run keeps the narrow permission set.
if ($Owner -and $Owner.Count -gt 0) {
    $ownerPerms = [string[]]@('Application.ReadWrite.All', 'Directory.Read.All')
    $delegatedScopes.AddRange($ownerPerms)
    $appRoles.AddRange($ownerPerms)
}
if ($GrantAdminConsent) {
    $consentPerms = [string[]]@('DelegatedPermissionGrant.ReadWrite.All', 'AppRoleAssignment.ReadWrite.All')
    $delegatedScopes.AddRange($consentPerms)
    $appRoles.AddRange($consentPerms)
}
# Custom security attributes are gated separately from every other directory permission: no
# Application.* or Directory.* role grants access to them, and a Global Administrator does not
# hold them by default either. Only ask when the run actually assigns some.
if ($CustomSecurityAttribute -and $CustomSecurityAttribute.Count -gt 0) {
    $csaPerms = [System.Collections.Generic.List[string]]::new()
    $csaPerms.Add('CustomSecAttributeAssignment.ReadWrite.All')
    if (-not $SkipCustomSecurityAttributeValidation) { $csaPerms.Add('CustomSecAttributeDefinition.Read.All') }
    $delegatedScopes.AddRange([string[]]$csaPerms)
    $appRoles.AddRange([string[]]$csaPerms)
}

$ctx = Connect-GraphSession -TenantId $TenantId `
    -ClientId $ClientId -ClientSecret $ClientSecret `
    -CertificateThumbprint $CertificateThumbprint -Certificate $Certificate `
    -CertificatePath $CertificatePath -CertificatePassword $CertificatePassword `
    -UseManagedIdentity:$UseManagedIdentity -AccessToken $AccessToken `
    -Interactive:$Interactive -SkipPermissionCheck:$SkipPermissionCheck `
    -DelegatedScope $delegatedScopes -RequiredAppRole $appRoles

# AgentIdentity.Create.All is the single role this script cannot proceed without, and Graph reports
# its absence only as a generic "Insufficient privileges" 403 at step 5. Saying so up front turns a
# guaranteed late failure into an actionable one. AgentIdentity.CreateAsManager is an accepted
# substitute - it is what the Agent 365 CLI relies on - so its presence suppresses the warning.
if ((@($ctx.MissingAppRoles) -contains 'AgentIdentity.Create.All') -and
    (@($ctx.GrantedAppRoles) -notcontains 'AgentIdentity.CreateAsManager')) {
    Write-Warning 'AgentIdentity.Create.All is NOT granted - creating the agent identity WILL fail with'
    Write-Warning '"Insufficient privileges to complete the operation." Grant it before continuing:'
    Write-Warning "  .\New-A365AutomationApp.ps1 -Scenario AgentIdentity -AppId $($ctx.ClientId)"
    Write-Warning 'Alternatively pass -MintWithBlueprintCredential -BlueprintClientSecret <secret> to mint'
    Write-Warning 'the identity with the blueprint app itself, which always holds AgentIdentity.CreateAsManager.'
    Write-Warning 'Newly granted roles only appear in tokens issued afterwards - allow ~2 minutes, then re-run.'
}

# ---------------------------------------------------------------------------
# Custom security attributes are resolved and validated BEFORE anything is created, so that a
# misspelt attribute fails on a run that has written nothing rather than leaving a live agent
# identity behind that is missing its governance data.
# ---------------------------------------------------------------------------
$csaResolved   = $null
$csaSchemaRead = $false
if ($CustomSecurityAttribute -and $CustomSecurityAttribute.Count -gt 0) {
    $csaSchema = @{}
    if (-not $SkipCustomSecurityAttributeValidation) {
        try {
            $csaSchema = Get-CustomSecurityAttributeSchema
            $csaSchemaRead = $true
        }
        catch {
            # Being unable to READ the definitions must not block a caller who is allowed to
            # ASSIGN them; Graph still enforces every rule, only the better message is lost.
            $info = Get-GraphErrorInfo -ErrorRecord $_
            # Logged as a warning, not a failure: the caller may tolerate this status or retry
            # it. Only the final throw below counts as a failed Graph call.
            Write-A365Log -Level WARN -Message ("<-- HTTP {0} on {1} {2}" -f $info.Status, $Method, $Uri) -Detail $info.Message
            Write-Warning "Could not read the custom security attribute definitions, so the request cannot be checked before it is sent: $($info.Message)"
            Write-Warning 'Grant CustomSecAttributeDefinition.Read.All, or pass -SkipCustomSecurityAttributeValidation to silence this.'
        }
    }

    # The compact "Set:Attribute:Value" strings are expanded first, using the schema to decide
    # cardinality, and the result then goes through exactly the same validation as a hashtable.
    $csaSpec = ConvertFrom-CustomSecurityAttributeSpec -Spec $CustomSecurityAttribute -Schema $csaSchema
    if (-not $csaSpec.IsValid) {
        $detail = (@($csaSpec.Problem) | ForEach-Object { "  * $_" }) -join [Environment]::NewLine
        throw @"
-CustomSecurityAttribute could not be read:
$detail

Nothing has been created. Fix the request and re-run.
"@
    }

    $csaResolved = Resolve-CustomSecurityAttributePayload -Requested $csaSpec.Requested `
        -Schema $csaSchema -SkipValidation:(-not $csaSchemaRead)

    foreach ($warning in @($csaResolved.Warning)) { Write-Warning $warning }

    if (-not $csaResolved.IsValid) {
        $detail = (@($csaResolved.Problem) | ForEach-Object { "  * $_" }) -join [Environment]::NewLine
        throw @"
-CustomSecurityAttribute does not match this tenant's attribute definitions:
$detail

Nothing has been created. Fix the request and re-run.
"@
    }
}

# ---------------------------------------------------------------------------
# Step 2 - resolve and validate the blueprint
# ---------------------------------------------------------------------------
Write-Step 2 'Validating the agent identity blueprint'

$blueprintSelect = 'id,appId,displayName,signInAudience,tags'
$updateIdentity  = $null

if ($Update) {
    # In update mode the identity is the anchor, so it is read FIRST and the blueprint is
    # discovered from it. Reading through the agentIdentity type cast also proves the id belongs
    # to an agent identity rather than a plain service principal, which would otherwise fail
    # several steps later with an opaque error.
    if (-not (Test-IsGuid $AgentIdentityId)) {
        throw "-AgentIdentityId '$AgentIdentityId' is not a GUID. Pass the agent identity's OBJECT id (its servicePrincipal id), not its display name."
    }

    $updateSelect   = 'id,appId,displayName,agentIdentityBlueprintId,accountEnabled,tags'
    $updateIdentity = Invoke-Graph -Method GET `
        -Uri "/servicePrincipals/$AgentIdentityId/microsoft.graph.agentIdentity?`$select=$updateSelect" `
        -TolerateNotFound -TolerateBadRequest

    if (-not $updateIdentity) {
        # Distinguish "no such object" from "wrong kind of object". Both surface as a 404 on the
        # cast URI, and telling someone their id does not exist when it does sends them looking
        # in the wrong place.
        $plainProbe = Invoke-Graph -Method GET `
            -Uri "/servicePrincipals/$AgentIdentityId`?`$select=id,displayName,servicePrincipalType" -TolerateNotFound
        if (-not $plainProbe) {
            throw "No service principal with object id '$AgentIdentityId' exists in tenant $TenantId. -AgentIdentityId takes the agent identity's OBJECT id, not its appId and not its display name."
        }
        throw "Object '$AgentIdentityId' ($($plainProbe.displayName)) exists but is not an agent identity, so -Update has nothing to change on it."
    }

    $identityBpAppId = ''
    if (Test-HasProperty $updateIdentity 'agentIdentityBlueprintId') { $identityBpAppId = [string]$updateIdentity.agentIdentityBlueprintId }
    if (-not $identityBpAppId) {
        throw "Agent identity '$AgentIdentityId' reports no agentIdentityBlueprintId, so its blueprint cannot be resolved."
    }

    Write-Host "  Updating  : $($updateIdentity.displayName)" -ForegroundColor Yellow
    Write-Host "  objectId  : $($updateIdentity.id)"
    $bpFilter = "appId eq '$(ConvertTo-ODataLiteral $identityBpAppId)'"
}
elseif ($PSCmdlet.ParameterSetName -eq 'ByAppId') {
    if (-not (Test-IsGuid $BlueprintAppId)) {
        throw "-BlueprintAppId '$BlueprintAppId' is not a GUID. Pass the blueprint's appId (client ID), not its display name or object ID."
    }
    $bpFilter = "appId eq '$(ConvertTo-ODataLiteral $BlueprintAppId)'"
}
else {
    $bpFilter = "displayName eq '$(ConvertTo-ODataLiteral $BlueprintDisplayName)'"
}

# Querying the agentIdentityBlueprint type-cast collection also proves the app really is a blueprint
# and not an ordinary app registration, which would otherwise fail later with an opaque error.
$bpResponse = Invoke-Graph -Method GET `
    -Uri "/applications/microsoft.graph.agentIdentityBlueprint?`$filter=$([uri]::EscapeDataString($bpFilter))&`$select=$blueprintSelect" `
    -TolerateNotFound

$bpMatched = @()
if (Test-HasProperty $bpResponse 'value') { $bpMatched = @($bpResponse.value) }

if ($bpMatched.Count -eq 0) {
    if ($Update) {
        # A blueprint that has since been deleted must not block an update: the identity still
        # exists and its attributes are still writable. Only the inherited-tag subtraction and
        # the summary need the blueprint, so a stub keeps both honest rather than absent.
        Write-Warning "  The blueprint appId $identityBpAppId referenced by this agent identity was not found. Continuing, but blueprint-inherited tags cannot be subtracted and the blueprint name is reported as unknown."
        $bpMatched = @([pscustomobject]@{ id = $null; appId = $identityBpAppId; displayName = '(blueprint not found)'; tags = @() })
    }
    else {
        $what = if ($PSCmdlet.ParameterSetName -eq 'ByAppId') { "appId '$BlueprintAppId'" } else { "display name '$BlueprintDisplayName'" }
        throw "No agent identity blueprint found with $what in tenant $TenantId. Create one first (see New-A365AgentBlueprint.ps1), or check that the object is a blueprint rather than a plain app registration."
    }
}
if ($bpMatched.Count -gt 1) {
    throw "Blueprint display name '$BlueprintDisplayName' matched $($bpMatched.Count) blueprints. Re-run with -BlueprintAppId to disambiguate."
}

$blueprint         = $bpMatched[0]
$blueprintObjectId = $blueprint.id
$resolvedBpAppId   = $blueprint.appId
Write-Host "  Blueprint : $($blueprint.displayName)"
Write-Host "  appId     : $resolvedBpAppId" -ForegroundColor Green
Write-Host "  objectId  : $blueprintObjectId"

# The blueprint principal is what holds consented permissions that agent identities inherit.
# A principal created moments ago can still be missing from this lookup, so a not-found result is
# re-checked a few times before it is reported - otherwise a freshly created blueprint draws a
# misleading "no principal exists" warning.
$bpPrincipal = $null
foreach ($bpLookupAttempt in 1..4) {
    $bpPrincipal = Invoke-Graph -Method GET `
        -Uri "/servicePrincipals(appId='$resolvedBpAppId')?`$select=id,appId,accountEnabled" -TolerateNotFound
    if ($bpPrincipal -or $bpLookupAttempt -eq 4) { break }
    Write-Verbose "Blueprint principal for $resolvedBpAppId not visible yet - re-checking ($bpLookupAttempt/4)"
    Start-Sleep -Seconds (3 * $bpLookupAttempt)
}
if ($bpPrincipal) {
    Write-Host "  Principal : $($bpPrincipal.id)"

    # The lookup above is untyped, so a plain servicePrincipal sharing the appId also matches.
    # Neither the entity-level type cast nor that cast followed by /owners discriminates - both were
    # verified live to answer 200 for a PLAIN principal. Asking for the object with full OData
    # metadata does: Graph stamps a concrete '@odata.type' on the response.
    $bpTypeProbe = Invoke-Graph -Method GET -Uri "/servicePrincipals/$($bpPrincipal.id)`?`$select=id" `
        -ExtraHeader @{ Accept = 'application/json;odata.metadata=full' } `
        -TolerateNotFound -TolerateBadRequest -TolerateForbidden

    # Default to trusting the object: a false 'plain' verdict tells the user to delete a good
    # principal, which is far more destructive than staying quiet.
    $bpIsTyped = $true
    if (Test-HasProperty $bpTypeProbe '@odata.type') {
        $bpIsTyped = ([string]$bpTypeProbe.'@odata.type') -like '*agentIdentityBlueprintPrincipal'
    }

    if (-not $bpIsTyped) {
        Write-Warning ("  The service principal for appId $resolvedBpAppId is a PLAIN servicePrincipal, not an " +
                       'agentIdentityBlueprintPrincipal. Agent identities cannot be created from it. Delete it ' +
                       "(Remove-MgServicePrincipal -ServicePrincipalId $($bpPrincipal.id)) and re-run " +
                       'New-A365AgentBlueprint.ps1 to create the principal correctly.')
    }
    elseif ((Test-HasProperty $bpPrincipal 'accountEnabled') -and $bpPrincipal.accountEnabled -eq $false) {
        Write-Warning '  The blueprint principal is disabled. Agent identities created from it will not be able to obtain tokens until it is enabled.'
    }
}
else {
    Write-Warning "  No blueprint principal exists for appId $resolvedBpAppId. The agent identity can be created, but it will not inherit any permissions and the blueprint cannot mint tokens for it until the principal is created."
}

# ---------------------------------------------------------------------------
# Step 3 - resolve sponsors and owners
# ---------------------------------------------------------------------------
Write-Step 3 'Resolving sponsors and owners'

$sponsorPrincipals = @()
foreach ($s in @($Sponsor | Where-Object { $_ })) {
    $resolvedSponsor = Resolve-SponsorPrincipal -Identifier $s
    $sponsorPrincipals += $resolvedSponsor
    Write-Host "  Sponsor : $s -> $($resolvedSponsor.Id) [$($resolvedSponsor.Type)]"
}
$sponsorPrincipals = @($sponsorPrincipals | Group-Object -Property Id | ForEach-Object { $_.Group[0] })
if ($sponsorPrincipals.Count -eq 0 -and -not $Update) {
    throw 'At least one valid sponsor is required by the create API.'
}

$ownerPrincipals = @()
foreach ($o in @($Owner | Where-Object { $_ })) {
    $resolvedOwner = Resolve-OwnerPrincipal -Identifier $o
    $ownerPrincipals += $resolvedOwner
    Write-Host "  Owner   : $o -> $($resolvedOwner.Id) [$($resolvedOwner.Type)]"
}
# De-duplicate so a UPN and its object ID passed together do not produce two POSTs.
$ownerPrincipals = @($ownerPrincipals | Group-Object -Property Id | ForEach-Object { $_.Group[0] })
if ($ownerPrincipals.Count -eq 0 -and -not $Update) {
    Write-Host '  Owner   : none specified; Entra ID assigns the calling principal as owner on create.' -ForegroundColor Yellow
}

# Resolve requested per-identity permissions before any write, so a bad name fails early.
$resolvedResources = @()
if ($RequiredPermission) {
    foreach ($req in $RequiredPermission) {
        $resourceAppId = [string]$req.ResourceAppId
        if (-not (Test-IsGuid $resourceAppId)) {
            throw "RequiredPermission.ResourceAppId '$resourceAppId' is not a GUID."
        }

        $sp = Invoke-Graph -Method GET `
            -Uri "/servicePrincipals(appId='$resourceAppId')?`$select=id,appId,displayName,appRoles,oauth2PermissionScopes" `
            -TolerateNotFound
        if (-not $sp) {
            Write-Warning "  No service principal for resource app $resourceAppId in this tenant. Skipping."
            continue
        }

        $spScopes = @(); if (Test-HasProperty $sp 'oauth2PermissionScopes') { $spScopes = @($sp.oauth2PermissionScopes) }
        $spRoles  = @(); if (Test-HasProperty $sp 'appRoles')               { $spRoles  = @($sp.appRoles) }

        $scopeEntries = @()
        foreach ($name in @($req.DelegatedScopes | Where-Object { $_ })) {
            $hit = @($spScopes | Where-Object { $_.value -eq $name })
            if ($hit.Count -eq 0) { Write-Warning "  Delegated scope '$name' not found on '$($sp.displayName)'. Skipping."; continue }
            $scopeEntries += [pscustomobject]@{ Name = $name; Id = $hit[0].id }
        }

        $roleEntries = @()
        foreach ($name in @($req.AppRoles | Where-Object { $_ })) {
            $hit = @($spRoles | Where-Object { $_.value -eq $name -and @($_.allowedMemberTypes) -contains 'Application' })
            if ($hit.Count -eq 0) { Write-Warning "  App role '$name' not found on '$($sp.displayName)'. Skipping."; continue }
            $roleEntries += [pscustomobject]@{ Name = $name; Id = $hit[0].id }
        }

        $resolvedResources += [pscustomobject]@{
            ResourceAppId   = $resourceAppId
            ResourceSpId    = $sp.id
            ResourceName    = $sp.displayName
            DelegatedScopes = $scopeEntries
            AppRoles        = $roleEntries
        }
        Write-Host "  Perms   : $($sp.displayName) -> $($scopeEntries.Count) scope(s), $($roleEntries.Count) role(s)"
    }
}

# ---------------------------------------------------------------------------
# Step 4 - idempotency probe
# ---------------------------------------------------------------------------
Write-Step 4 'Checking for an existing agent identity'

$agentIdentity = $null
$wasCreated    = $false

if ($Update) {
    # Step 2 already fetched it by object id. Update mode addresses the identity directly, so
    # there is no name search to run and no duplicate-name ambiguity to resolve.
    $agentIdentity = $updateIdentity
    Write-Host "  Located by object id: $($agentIdentity.id)" -ForegroundColor Green
}
else {
    $aiSelect   = 'id,displayName,agentIdentityBlueprintId,accountEnabled,servicePrincipalType,tags'
    $nameFilter = "displayName eq '$(ConvertTo-ODataLiteral $DisplayName)'"
    $aiUri      = "/servicePrincipals/microsoft.graph.agentIdentity?`$filter=$([uri]::EscapeDataString($nameFilter))&`$select=$aiSelect"

    $aiResponse = Invoke-Graph -Method GET -Uri $aiUri -TolerateNotFound -TolerateBadRequest
    if ($null -eq $aiResponse) {
        # Some directory filters are only served with the advanced-query headers.
        Write-Verbose 'Plain $filter rejected; retrying with ConsistencyLevel=eventual and $count=true.'
        $aiResponse = Invoke-Graph -Method GET -Uri "$aiUri&`$count=true" `
            -ExtraHeader @{ ConsistencyLevel = 'eventual' } -TolerateNotFound
    }

    $sameName = @()
    if (Test-HasProperty $aiResponse 'value') { $sameName = @($aiResponse.value) }

    # Display names are not unique tenant-wide, so scope the match to this blueprint.
    $existingMatches = @($sameName | Where-Object {
        (Test-HasProperty $_ 'agentIdentityBlueprintId') -and $_.agentIdentityBlueprintId -eq $resolvedBpAppId
    })

    if ($existingMatches.Count -gt 1) {
        throw "Found $($existingMatches.Count) agent identities named '$DisplayName' under blueprint $resolvedBpAppId. Resolve the duplicates before re-running."
    }

    if ($existingMatches.Count -eq 1) {
        $agentIdentity = $existingMatches[0]
        Write-Host "  Reusing existing agent identity $($agentIdentity.id)" -ForegroundColor Yellow
    }
    elseif ($sameName.Count -gt 0) {
        $otherBps = @(Get-PropertyValue $sameName 'agentIdentityBlueprintId') -join ', '
        Write-Host "  A different agent identity named '$DisplayName' exists under blueprint(s) [$otherBps]; creating a new one under $resolvedBpAppId." -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------------
# Step 5 - create the agent identity
# ---------------------------------------------------------------------------
Write-Step 5 'Creating the agent identity'

if ($Update) {
    Write-Host '  Skipped: -Update changes an existing agent identity and never creates one.' -ForegroundColor DarkGray
}
elseif ($agentIdentity) {
    Write-Host '  Skipped (already exists).' -ForegroundColor Yellow
}
elseif ($PSCmdlet.ShouldProcess($DisplayName, 'POST /servicePrincipals/microsoft.graph.agentIdentity')) {
    # Sponsors must be supplied here: the sponsors/$ref API is application-permission only, so a
    # delegated caller gets exactly one chance to set them - at creation.
    $body = [ordered]@{
        displayName              = $DisplayName
        agentIdentityBlueprintId = $resolvedBpAppId
        'sponsors@odata.bind'    = @($sponsorPrincipals | ForEach-Object { "$script:GraphRoot/$($_.Segment)/$($_.Id)" })
    }
    if ($Tag) { $body.tags = @($Tag) }
    if ($csaResolved) { $body.customSecurityAttributes = $csaResolved.Payload }

    # A blueprint principal created seconds ago is not yet visible to the Agent 365 backend, which
    # reports that as a 403 "does not exist". MaxAttempts 8 gives roughly two minutes of backoff.
    $mintSplat = @{}
    if ($MintWithBlueprintCredential) {
        # Borrow the blueprint's identity for this single call. Its token carries
        # AgentIdentity.CreateAsManager and nothing else, so it is deliberately NOT used to connect
        # the session - every read around this call would 403 under it.
        Write-Host "  Minting as the blueprint app $resolvedBpAppId (AgentIdentity.CreateAsManager)." -ForegroundColor DarkGray
        $mintSplat.BearerToken = Get-AppOnlyGraphToken -TenantId $TenantId `
            -ClientId $resolvedBpAppId -ClientSecret $BlueprintClientSecret
    }

    try {
        $agentIdentity = Invoke-Graph -Method POST -Uri '/servicePrincipals/microsoft.graph.agentIdentity' -Body $body `
            -RetryOnBlueprintNotReady -MaxAttempts 8 @mintSplat
    }
    catch {
        # A bare "Insufficient privileges" here names neither the permission nor the object, which
        # sends users hunting through blueprint ownership for a missing app role.
        $info   = Get-GraphErrorInfo -ErrorRecord $_
        $status = if ($null -eq $info.Status) { 0 } else { [int]$info.Status }
        $advice = Get-AgentIdentityDenialAdvice -StatusCode $status -Message ([string]$info.Message) `
            -ErrorCode ([string]$info.Code) -IsAppOnly ([bool]$ctx.IsAppOnly) -ClientId ([string]$ctx.ClientId) `
            -MissingAppRole @($ctx.MissingAppRoles)
        if ($advice) { throw "$advice$([Environment]::NewLine)$([Environment]::NewLine)Original error: $($info.Message)" }
        throw
    }
    $wasCreated    = $true
    Write-Host "  Created: $($agentIdentity.id)" -ForegroundColor Green
}
else {
    Write-Host '  [WhatIf] Agent identity creation skipped; remaining steps cannot run.' -ForegroundColor Yellow
    return
}

$agentIdentityId = $agentIdentity.id
$agentIdentityUri = "/servicePrincipals/$agentIdentityId/microsoft.graph.agentIdentity"

# ---------------------------------------------------------------------------
# Rename (update mode only)
#
# In create mode -DisplayName is how the identity is located, so a "rename" there would simply
# address a different object. Under -Update the identity is addressed by id, which is what makes
# renaming meaningful. The comparison is case-SENSITIVE: changing only the casing of a name is a
# real change that Graph will accept, and -eq would silently discard it.
# ---------------------------------------------------------------------------
$displayNameWritten = $false
if ($Update) {
    $currentName = ''
    if (Test-HasProperty $agentIdentity 'displayName') { $currentName = [string]$agentIdentity.displayName }

    if (-not $PSBoundParameters.ContainsKey('DisplayName')) {
        # Every downstream ShouldProcess target and message names the identity. Without
        # -DisplayName the parameter is empty and they would all render blank.
        $DisplayName = $currentName
    }
    elseif ($currentName -ceq $DisplayName) {
        Write-Host "  Display name is already '$DisplayName'." -ForegroundColor DarkGray
    }
    elseif ($PSCmdlet.ShouldProcess($currentName, "PATCH displayName -> $DisplayName")) {
        Invoke-Graph -Method PATCH -Uri $agentIdentityUri -Body @{ displayName = $DisplayName } -RetryOnNotFound | Out-Null
        $displayNameWritten = $true
        Write-Host "  Renamed: '$currentName' -> '$DisplayName'" -ForegroundColor Green
    }
}

# ---------------------------------------------------------------------------
# Tags
#
# tags is only honoured on the create call above, so an identity that already existed would
# silently ignore -Tag and the run would still report success. That is repaired here with a
# PATCH.
#
# Two things make this more than a one-line write:
#
#   1. PATCH REPLACES a collection, so the whole desired set has to be sent. Sending only the
#      new tags would delete every tag already there.
#   2. Reading tags returns the UNION of the identity's own tags and its blueprint's
#      (documented behaviour). Writing that union straight back would pin a private copy of
#      the blueprint's tags onto the identity, which then stops tracking the blueprint. So the
#      blueprint's tags are subtracted first to recover the identity's OWN tags.
#
# Removing an inherited tag from the identity's own set never changes the effective value,
# because inheritance still supplies it.
# ---------------------------------------------------------------------------
$blueprintTags = @()
if (Test-HasProperty $blueprint 'tags') { $blueprintTags = @($blueprint.tags) }

$tagsWritten = $false
if ($Tag -and -not $wasCreated) {
    $ownTags = @(Get-AgentIdentityOwnTag -Identity $agentIdentity -InheritedTag $blueprintTags)

    # Order-preserving union: keep what is already there, append only what is genuinely new.
    $seenTag  = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $desired  = @(@($ownTags) + @($Tag) | Where-Object { $_ } | Where-Object { $seenTag.Add($_) })
    $missing  = @($Tag | Where-Object { $_ -and $_ -notin $ownTags -and $_ -notin $blueprintTags })

    if ($missing.Count -eq 0) {
        Write-Host "  Tags already present: $(@($Tag) -join ', ')" -ForegroundColor DarkGray
    }
    elseif ($PSCmdlet.ShouldProcess($DisplayName, "PATCH tags -> $($desired -join ', ')")) {
        try {
            Invoke-Graph -Method PATCH -Uri $agentIdentityUri -Body @{ tags = @($desired) } | Out-Null
            $tagsWritten = $true
            Write-Host "  Tags applied to the existing identity: $($missing -join ', ')" -ForegroundColor Green
        }
        catch {
            # An identity that is otherwise complete must not be failed over a categorisation
            # label, but the run must not claim the tags either - the read-back below reports
            # what is really there.
            Write-Warning "  Could not apply tags to the existing agent identity: $($_.Exception.Message)"
        }
    }
}

# Read the tags back rather than echoing the -Tag parameter. Reporting the request as though it
# were the result is how a silently dropped write turns into a run that looks successful. This
# runs unconditionally: without -Tag the identity may still carry tags from an earlier run or
# from its blueprint, and reporting an empty set would be just as wrong.
$effectiveTags = @()
$tagProbe = Invoke-Graph -Method GET -Uri "$agentIdentityUri`?`$select=id,tags" -TolerateNotFound -TolerateBadRequest
if (-not $tagProbe) {
    $tagProbe = Invoke-Graph -Method GET -Uri "/servicePrincipals/$agentIdentityId`?`$select=id,tags" -TolerateNotFound
}
if (Test-HasProperty $tagProbe 'tags') { $effectiveTags = @($tagProbe.tags | Where-Object { $_ }) }

if ($Tag) {
    $absent = @($Tag | Where-Object { $_ -and $_ -notin $effectiveTags })
    if ($absent.Count -gt 0) {
        Write-Warning ("  These tags were requested but are NOT on the agent identity: $($absent -join ', '). " +
                       'The directory accepted the call without persisting them.')
    }
}

# ---------------------------------------------------------------------------
# Custom security attributes
#
# The create call above carries them, so only a reused identity needs the PATCH. Graph merges at
# the attribute level, so the payload is sent as-is: attributes in the same set that this run did
# not mention are left alone, and there is no read-modify-write to get wrong.
#
# The assignments are then read back unconditionally - including after a create - because the
# only trustworthy report of what is on the object is the object itself.
# ---------------------------------------------------------------------------
$csaActual        = $null
$csaWritten       = $false
$csaAlreadyMatched = $false
if ($csaResolved) {
    if (-not $wasCreated) {
        $csaLabel = @($csaResolved.Assignment | ForEach-Object { "$($_.Set)/$($_.Attribute)" }) -join ', '

        # Read before writing so an unchanged re-run neither issues a pointless write nor reports
        # a change that did not happen. If the read is refused the PATCH still goes ahead: being
        # unable to confirm the current values is not evidence that they already match.
        $csaPre = Invoke-Graph -Method GET -Uri "/servicePrincipals/$agentIdentityId`?`$select=id,customSecurityAttributes" `
            -TolerateNotFound -TolerateForbidden -RetryOnNotFound
        if ($null -ne $csaPre -and (Test-HasProperty $csaPre 'customSecurityAttributes')) {
            $csaPending = @(Compare-CustomSecurityAttribute -Requested @($csaResolved.Assignment) -Actual $csaPre.customSecurityAttributes)
            if ($csaPending.Count -eq 0) { $csaAlreadyMatched = $true }
        }

        if ($csaAlreadyMatched) {
            Write-Host "  Custom security attributes already match: $csaLabel" -ForegroundColor DarkGray
        }
        elseif ($PSCmdlet.ShouldProcess($DisplayName, "PATCH customSecurityAttributes -> $csaLabel")) {
            try {
                Invoke-Graph -Method PATCH -Uri $agentIdentityUri -Body @{ customSecurityAttributes = $csaResolved.Payload } | Out-Null
                $csaWritten = $true
                Write-Host "  Custom security attributes applied to the existing identity: $csaLabel" -ForegroundColor Green
            }
            catch {
                $info = Get-GraphErrorInfo -ErrorRecord $_
            # Logged as a warning, not a failure: the caller may tolerate this status or retry
            # it. Only the final throw below counts as a failed Graph call.
            Write-A365Log -Level WARN -Message ("<-- HTTP {0} on {1} {2}" -f $info.Status, $Method, $Uri) -Detail $info.Message
                $status = if ($null -eq $info.Status) { 0 } else { [int]$info.Status }
                if ($status -eq 403) {
                    Write-Warning "  Not authorized to assign custom security attributes: $($info.Message)"
                    Write-Warning '  Assigning them needs CustomSecAttributeAssignment.ReadWrite.All. It is granted by no other'
                    Write-Warning '  directory permission: a delegated caller needs the Attribute Assignment Administrator role'
                    Write-Warning '  (Global Administrator does NOT include it), and an app-only caller needs the application role.'
                }
                else {
                    Write-Warning "  Could not assign custom security attributes: $($info.Message)"
                }
            }
        }
    }

    $csaProbe = Invoke-Graph -Method GET -Uri "/servicePrincipals/$agentIdentityId`?`$select=id,customSecurityAttributes" `
        -TolerateNotFound -TolerateForbidden -RetryOnNotFound
    if (Test-HasProperty $csaProbe 'customSecurityAttributes') { $csaActual = $csaProbe.customSecurityAttributes }

    if ($null -eq $csaProbe) {
        # Reading assignments is a separate permission from writing them, so a caller can succeed
        # at the write and still see nothing here. Saying "not persisted" would be wrong.
        Write-Warning '  Could not read custom security attributes back, so this run cannot confirm them. Reading them needs CustomSecAttributeAssignment.Read.All.'
    }
    else {
        $csaMissing = @(Compare-CustomSecurityAttribute -Requested @($csaResolved.Assignment) -Actual $csaActual)
        if ($csaMissing.Count -gt 0) {
            Write-Warning ("  These custom security attributes were requested but are NOT on the agent identity: " +
                           "$($csaMissing -join ', '). The directory accepted the call without persisting them.")
        }
        elseif (-not $csaWritten -and -not $wasCreated -and -not $csaAlreadyMatched) {
            Write-Host '  Custom security attributes already match.' -ForegroundColor DarkGray
        }
    }
}

# ---------------------------------------------------------------------------
# Step 6 - owners
# ---------------------------------------------------------------------------
Write-Step 6 'Assigning owners'

if ($ownerPrincipals.Count -eq 0) {
    Write-Host '  No owners requested.' -ForegroundColor Yellow
}
else {
    # Writing an owners collection needs Application.ReadWrite.All (or .OwnedBy) plus
    # Directory.Read.All - wider than the AgentIdentity.* roles the rest of this script uses -
    # and, when delegated, an Application Administrator style directory role. A denial here must
    # not destroy an agent identity that is otherwise complete, so it degrades to a warning
    # unless -RequireOwnerAssignment was passed.
    $existingOwners = Invoke-Graph -Method GET -Uri "$agentIdentityUri/owners?`$select=id" `
        -TolerateNotFound -TolerateForbidden -RetryOnNotFound
    $existingOwnerIds = @()
    if (Test-HasProperty $existingOwners 'value') { $existingOwnerIds = @(Get-PropertyValue $existingOwners.value 'id') }

    foreach ($o in $ownerPrincipals) {
        if ($existingOwnerIds -contains $o.Id) {
            Write-Host "  Already an owner: $($o.Display)" -ForegroundColor Yellow
            continue
        }
        if ($PSCmdlet.ShouldProcess($o.Display, "POST $agentIdentityUri/owners/`$ref")) {
            try {
                # 409 - or a 400 "already exist" - on a directory race is benign: the owner ends
                # up assigned either way.
                Invoke-Graph -Method POST -Uri "$agentIdentityUri/owners/`$ref" `
                    -Body @{ '@odata.id' = "$script:GraphRoot/directoryObjects/$($o.Id)" } `
                    -TolerateConflict -RetryOnNotFound | Out-Null
                Write-Host "  Owner added: $($o.Display) [$($o.Type)]" -ForegroundColor Green
            }
            catch {
                # Graph answers a duplicate owner with 400 "object references already exist",
                # not 409, so -TolerateConflict does not cover it. The owner is present either
                # way, which is the desired end state - do not fail the run over it.
                if ($_.Exception.Message -match 'object references already exist') {
                    Write-Host "  Already an owner: $($o.Display) [$($o.Type)]" -ForegroundColor Green
                    continue
                }
                if ($_.Exception.Message -notmatch '\[(401|403)\b') { throw }
                $script:OwnerAssignmentDenied = $true
                Write-Warning "Owner '$($o.Display)' was refused (403 Authorization_RequestDenied)."
                if ($RequireOwnerAssignment) {
                    throw "Owner assignment failed and -RequireOwnerAssignment was specified. $(Get-OwnerDenialAdvice)"
                }
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Step 7 - sponsor reconciliation (repair path for pre-existing identities)
# ---------------------------------------------------------------------------
Write-Step 7 'Verifying sponsors'

if ($wasCreated) {
    Write-Host "  Set at creation: $(@(Get-PropertyValue $sponsorPrincipals 'Display') -join ', ')" -ForegroundColor Green
}
elseif ($sponsorPrincipals.Count -eq 0) {
    # Update mode with no -Sponsor: saying "all requested sponsors are assigned" when none were
    # requested reads as confirmation that sponsorship was checked, which it was not.
    Write-Host '  No sponsors requested; the current sponsors are left unchanged.' -ForegroundColor DarkGray
}
else {
    # Reading sponsors is itself application-only, so a delegated caller gets 403 here. Tolerate it
    # and fall through to the guidance below rather than dying with a raw Graph error.
    $existingSponsors = Invoke-Graph -Method GET -Uri "$agentIdentityUri/sponsors?`$select=id" -TolerateNotFound -TolerateForbidden
    $existingSponsorIds = @()
    if (Test-HasProperty $existingSponsors 'value') { $existingSponsorIds = @(Get-PropertyValue $existingSponsors.value 'id') }

    $missingSponsors = @($sponsorPrincipals | Where-Object { $existingSponsorIds -notcontains $_.Id })
    if ($missingSponsors.Count -eq 0) {
        Write-Host '  All requested sponsors already assigned.' -ForegroundColor Green
    }
    elseif (-not $ctx.IsAppOnly) {
        # POST .../sponsors/$ref has no delegated permission at all - the docs list Delegated as
        # "Not supported". Don't even try; say what to do instead.
        Write-Warning "  Sponsor(s) missing from the existing agent identity: $(@(Get-PropertyValue $missingSponsors 'Display') -join ', ')"
        Write-Warning '  Adding sponsors to an EXISTING agent identity has no delegated equivalent. Re-run this script app-only (-ClientId with -ClientSecret or a certificate) using an app granted AgentIdentity.ReadWrite.All.'
    }
    else {
        foreach ($s in $missingSponsors) {
            if (-not $PSCmdlet.ShouldProcess($s.Display, "POST $agentIdentityUri/sponsors/`$ref")) { continue }
            # Invoke-Graph raises a terminating error, so catch it rather than relying on
            # -ErrorAction, and keep going with the remaining sponsors.
            try {
                Invoke-Graph -Method POST -Uri "$agentIdentityUri/sponsors/`$ref" `
                    -Body @{ '@odata.id' = "$script:GraphRoot/directoryObjects/$($s.Id)" } `
                    -TolerateConflict -MaxAttempts 1 | Out-Null
                Write-Host "  Sponsor added: $($s.Display)" -ForegroundColor Green
            }
            catch {
                Write-Warning "  Could not add sponsor '$($s.Display)': $($_.Exception.Message)"
                Write-Warning '  This operation needs the application permission AgentIdentity.ReadWrite.All.'
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Step 8 - optional disable
# ---------------------------------------------------------------------------
Write-Step 8 'Applying account state'

$accountEnabled = $true
if (Test-HasProperty $agentIdentity 'accountEnabled') { $accountEnabled = [bool]$agentIdentity.accountEnabled }

# -Disabled is read through PSBoundParameters rather than as a plain switch so that an explicit
# -Disabled:$false can re-ENABLE an identity. Read as a bare switch it could only ever turn an
# identity off, leaving update mode with no way back short of a hand-written Graph call.
$desiredEnabled = $null
if ($PSBoundParameters.ContainsKey('Disabled')) { $desiredEnabled = -not $Disabled.IsPresent }

if ($null -eq $desiredEnabled) {
    Write-Host "  Left as-is (accountEnabled = $accountEnabled)."
}
elseif ($desiredEnabled -eq $accountEnabled) {
    $stateWord = if ($accountEnabled) { 'enabled' } else { 'disabled' }
    Write-Host "  Already $stateWord." -ForegroundColor Yellow
}
elseif ($PSCmdlet.ShouldProcess($DisplayName, "PATCH $agentIdentityUri (accountEnabled = $desiredEnabled)")) {
    Invoke-Graph -Method PATCH -Uri $agentIdentityUri -Body @{ accountEnabled = $desiredEnabled } -RetryOnNotFound | Out-Null
    $accountEnabled = $desiredEnabled
    if ($desiredEnabled) { Write-Host '  Enabled.' -ForegroundColor Green }
    else { Write-Host '  Disabled. Re-enable with -Disabled:$false.' -ForegroundColor Green }
}

# ---------------------------------------------------------------------------
# Step 9 - optional per-identity admin consent
# ---------------------------------------------------------------------------
Write-Step 9 'Granting per-identity admin consent'

if (-not $GrantAdminConsent) {
    Write-Host '  Skipped. Agent identities normally inherit permissions consented on the blueprint principal.' -ForegroundColor Yellow
}
elseif ($resolvedResources.Count -eq 0) {
    Write-Host '  Nothing to grant: pass -RequiredPermission to consent permissions on this identity alone.' -ForegroundColor Yellow
}
else {
    $assignments = Invoke-Graph -Method GET -Uri "/servicePrincipals/$agentIdentityId/appRoleAssignments" `
        -TolerateNotFound -RetryOnNotFound
    $assignedRoleIds = @()
    if (Test-HasProperty $assignments 'value') { $assignedRoleIds = @(Get-PropertyValue $assignments.value 'appRoleId') }

    foreach ($r in $resolvedResources) {
        if ($r.DelegatedScopes.Count -gt 0) {
            $scopeText = @(Get-PropertyValue $r.DelegatedScopes 'Name') -join ' '
            $gFilter   = "clientId eq '$agentIdentityId' and resourceId eq '$($r.ResourceSpId)'"
            $grants    = Invoke-Graph -Method GET `
                -Uri "/oauth2PermissionGrants?`$filter=$([uri]::EscapeDataString($gFilter))" -TolerateNotFound

            $existingGrant = $null
            if ((Test-HasProperty $grants 'value') -and @($grants.value).Count -gt 0) { $existingGrant = @($grants.value)[0] }

            if ($existingGrant) {
                $have = @()
                if (Test-HasProperty $existingGrant 'scope') { $have = @([string]$existingGrant.scope -split '\s+' | Where-Object { $_ }) }
                $want    = @(Get-PropertyValue $r.DelegatedScopes 'Name')
                $missing = @($want | Where-Object { $have -notcontains $_ })
                if ($missing.Count -eq 0) {
                    Write-Host "  Delegated scopes already granted on $($r.ResourceName)." -ForegroundColor Yellow
                }
                elseif ($PSCmdlet.ShouldProcess($r.ResourceName, "PATCH /oauth2PermissionGrants/$($existingGrant.id)")) {
                    try {
                        Invoke-Graph -Method PATCH -Uri "/oauth2PermissionGrants/$($existingGrant.id)" `
                            -Body @{ scope = (@($have + $missing) -join ' ') } | Out-Null
                        Write-Host "  Merged delegated scopes on $($r.ResourceName): $($missing -join ', ')" -ForegroundColor Green
                    }
                    catch {
                        # The identity is already created and usable; a refused grant is collected
                        # and reported with a link rather than aborting the run.
                        Add-ConsentFailure -Kind 'delegated' -ResourceName $r.ResourceName `
                            -ResourceAppId $r.ResourceAppId -Permission ($missing -join ' ') -Message $_.Exception.Message
                        Write-Warning "  Could not merge delegated scopes on $($r.ResourceName): $($_.Exception.Message)"
                    }
                }
            }
            elseif ($PSCmdlet.ShouldProcess($r.ResourceName, 'POST /oauth2PermissionGrants')) {
                try {
                    Invoke-Graph -Method POST -Uri '/oauth2PermissionGrants' -RetryOnNotFound -Body ([ordered]@{
                        clientId    = $agentIdentityId
                        consentType = 'AllPrincipals'
                        resourceId  = $r.ResourceSpId
                        scope       = $scopeText
                    }) | Out-Null
                    Write-Host "  Granted delegated scopes on $($r.ResourceName): $scopeText" -ForegroundColor Green
                }
                catch {
                    Add-ConsentFailure -Kind 'delegated' -ResourceName $r.ResourceName `
                        -ResourceAppId $r.ResourceAppId -Permission $scopeText -Message $_.Exception.Message
                    Write-Warning "  Could not grant delegated scopes on $($r.ResourceName): $($_.Exception.Message)"
                }
            }
        }

        foreach ($role in $r.AppRoles) {
            if ($assignedRoleIds -contains $role.Id) {
                Write-Host "  App role already assigned: $($role.Name)" -ForegroundColor Yellow
                continue
            }
            if ($PSCmdlet.ShouldProcess("$($r.ResourceName)/$($role.Name)", "POST /servicePrincipals/$agentIdentityId/appRoleAssignments")) {
                try {
                    Invoke-Graph -Method POST -Uri "/servicePrincipals/$agentIdentityId/appRoleAssignments" `
                        -TolerateConflict -RetryOnNotFound -Body ([ordered]@{
                            principalId = $agentIdentityId
                            resourceId  = $r.ResourceSpId
                            appRoleId   = $role.Id
                        }) | Out-Null
                    Write-Host "  Assigned app role: $($role.Name) on $($r.ResourceName)" -ForegroundColor Green
                }
                catch {
                    Add-ConsentFailure -Kind 'app role' -ResourceName $r.ResourceName `
                        -ResourceAppId $r.ResourceAppId -Permission $role.Name -Message $_.Exception.Message
                    Write-Warning "  Could not assign app role '$($role.Name)' on $($r.ResourceName): $($_.Exception.Message)"
                }
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
# A subexpression writes to the output stream, which UNROLLS a collection - so building this
# with $(if ...) turned a single requested attribute into a bare object instead of a one-element
# array, and any consumer iterating the JSON silently saw the wrong shape. Assigning to a
# variable first does not unroll.
$csaRequestedSummary = @()
if ($csaResolved) {
    $csaRequestedSummary = @($csaResolved.Assignment | ForEach-Object {
        [ordered]@{ set = $_.Set; attribute = $_.Attribute; value = $_.Value }
    })
}

$summary = [ordered]@{
    tenantId                 = $ctx.TenantId
    displayName              = $DisplayName
    # For an agent identity the object ID and the app ID are always the same value.
    agentIdentityId          = $agentIdentityId
    agentIdentityClientId    = $agentIdentityId
    fmiPath                  = $agentIdentityId
    agentIdentityBlueprintId = $resolvedBpAppId
    blueprintDisplayName     = $blueprint.displayName
    blueprintObjectId        = $blueprintObjectId
    blueprintPrincipalId     = if ($bpPrincipal) { $bpPrincipal.id } else { $null }
    accountEnabled           = $accountEnabled
    updateMode               = [bool]$Update
    createdByThisRun         = $wasCreated
    displayNameUpdatedByThisRun = $displayNameWritten
    sponsors                 = @($sponsorPrincipals | ForEach-Object { [ordered]@{ id = $_.Id; type = $_.Type; display = $_.Display } })
    owners                   = @($ownerPrincipals   | ForEach-Object { [ordered]@{ id = $_.Id; type = $_.Type; display = $_.Display } })
    ownerAssignmentDenied    = $script:OwnerAssignmentDenied
    tags                     = @($effectiveTags)
    tagsRequested            = @($Tag)
    tagsUpdatedByThisRun     = $tagsWritten
    customSecurityAttributes = $csaActual
    customSecurityAttributesRequested = $csaRequestedSummary
    customSecurityAttributesUpdatedByThisRun = $csaWritten
    perIdentityGrants        = @($resolvedResources | ForEach-Object {
                                    [ordered]@{
                                        resourceAppId   = $_.ResourceAppId
                                        resourceName    = $_.ResourceName
                                        delegatedScopes = @(Get-PropertyValue $_.DelegatedScopes 'Name')
                                        appRoles        = @(Get-PropertyValue $_.AppRoles 'Name')
                                    }
                                })
    adminConsentGranted      = [bool]$GrantAdminConsent
    consentFailures          = @($script:ConsentFailures | ForEach-Object {
        [ordered]@{
            kind           = $_.Kind
            resourceName   = $_.ResourceName
            resourceAppId  = $_.ResourceAppId
            permission     = $_.Permission
            message        = $_.Message
            isGraphAppRole = $_.IsGraphAppRole
        }
    })
    # For an agent identity the object id and the app id are the same value, so both links
    # are built from it. Always present; only ACTIONABLE when consentFailures is non-empty.
    adminConsentUrl          = (Get-AdminConsentUrl -TenantId $ctx.TenantId -ClientAppId $agentIdentityId)
    portalPermissionsUrl     = (Get-PortalPermissionsUrl -ClientObjectId $agentIdentityId -ClientAppId $agentIdentityId)
}

Write-Host ''
Write-Host '=== Agent identity ready ===' -ForegroundColor Cyan
$summary.GetEnumerator() | ForEach-Object {
    # Custom security attributes come back as a nested object, which renders as the useless
    # "@{AgentAttributes=}" unless it is explicitly serialised.
    $isStructured = ($_.Value -is [System.Management.Automation.PSCustomObject]) -or
                    ($_.Value -is [System.Collections.IDictionary]) -or
                    (($_.Value -is [System.Collections.IEnumerable]) -and ($_.Value -isnot [string]))
    $v = if ($null -ne $_.Value -and $isStructured) { ($_.Value | ConvertTo-Json -Compress -Depth 6) } else { $_.Value }
    Write-Host ("  {0,-26} {1}" -f $_.Key, $v)
}

if ($script:OwnerAssignmentDenied) {
    Write-Host ''
    Write-Host 'One or more owners could not be assigned. The agent identity itself is complete and usable.' -ForegroundColor Yellow
    Write-Host (Get-OwnerDenialAdvice) -ForegroundColor Gray
}

Write-ConsentActionRequired -TenantId $ctx.TenantId -ClientAppId $agentIdentityId `
    -ClientObjectId $agentIdentityId -DisplayName $DisplayName -Failures $script:ConsentFailures

Write-Host ''
Write-Host 'Acquiring a token AS this agent identity:' -ForegroundColor Cyan
Write-Host '  The agent identity has no credentials of its own. The BLUEPRINT authenticates and names'
Write-Host '  the agent identity in fmi_path:'
Write-Host ''
Write-Host "    POST https://login.microsoftonline.com/$($ctx.TenantId)/oauth2/v2.0/token"
Write-Host '    Content-Type: application/x-www-form-urlencoded'
Write-Host ''
Write-Host "      client_id=$resolvedBpAppId"
Write-Host '      scope=https://graph.microsoft.com/.default'
Write-Host '      grant_type=client_credentials'
Write-Host '      client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
Write-Host '      client_assertion=<managed-identity-or-certificate-assertion>'
Write-Host "      fmi_path=$agentIdentityId"
Write-Host ''
Write-Host 'Next steps (outside this script):' -ForegroundColor Cyan
Write-Host '  * Consent the blueprint principal so every agent identity inherits its permissions:'
Write-Host "      POST /v1.0/oauth2PermissionGrants   (clientId = $(if ($bpPrincipal) { $bpPrincipal.id } else { '<blueprint principal id>' }))"
Write-Host '  * Optionally give the agent a user account (mailbox, Teams presence):'
Write-Host '      POST /v1.0/users/microsoft.graph.agentUser'
Write-Host '  * Delete the identity when the agent is decommissioned:'
Write-Host "      DELETE /v1.0/servicePrincipals/$agentIdentityId"

if ($OutputJsonPath) {
    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path $OutputJsonPath -Encoding utf8
    Write-Host ''
    Write-Host "Summary written to $OutputJsonPath" -ForegroundColor Green
}

[pscustomobject]$summary

Complete-A365Log -Outcome 'Succeeded'