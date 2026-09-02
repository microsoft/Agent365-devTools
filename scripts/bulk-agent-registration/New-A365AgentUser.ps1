<#
.SYNOPSIS
    Creates a Microsoft Agent 365 Agent User (and optionally an Agent Identity) using only
    Microsoft Graph REST API calls, authenticating app-only (application permissions).

.DESCRIPTION
    Replicates the Graph API calls the Agent 365 CLI ("a365 setup createinstance") performs,
    without any dependency on the CLI, the Microsoft.Graph PowerShell SDK, or Azure CLI.

    Two mutually exclusive target modes:

      -AgentIdentityId <spObjectId>   Use an EXISTING Agent Identity. The script only creates
                                      the Agent User bound to it.

      -BlueprintAppId <appId>         Create a NEW Agent Identity from the Agent Blueprint,
                                      then create the Agent User bound to the new identity.

    Licensing is opt-in: no license is assigned unless -AssignLicense is specified.

    Authentication is application-only (client credentials). Supported methods:
      1.  Client secret                      -ClientSecret
      2.  Certificate from cert store        -CertificateThumbprint [-CertificateStoreLocation]
      3.  Certificate from PFX file          -CertificatePath [-CertificatePassword]
      4.  Certificate object                 -Certificate
      5.  Managed identity (system-assigned) -UseManagedIdentity
      6.  Managed identity (user-assigned)   -UseManagedIdentity -ManagedIdentityClientId
      7.  Workload identity federation       -FederatedTokenFile  (AKS / Kubernetes)
      8.  Federated client assertion         -ClientAssertion     (GitHub OIDC, any OIDC IdP)
      9.  Azure CLI service principal token  -UseAzureCli
      10. Azure PowerShell (Az) token        -UseAzPowerShell
      11. Pre-acquired raw token             -AccessToken

    When no auth parameter is supplied, the script auto-detects, in order: AZURE_CLIENT_SECRET,
    AZURE_CLIENT_CERTIFICATE_PATH, AZURE_FEDERATED_TOKEN_FILE, GitHub Actions OIDC, then IMDS
    managed identity. This makes the script drop-in compatible with standard Azure SDK
    environment-variable conventions.

.PARAMETER ConfigurePermissions
    Configures the Entra application registration with every Graph application permission this
    script requires: adds them to the app manifest (requiredResourceAccess) and creates the
    corresponding appRoleAssignments (tenant admin consent) on the app's service principal.

    Permissions are organised into capability groups. Within each group the roles are ordered
    from least to most privileged, and the script grants the first one the tenant's Microsoft
    Graph service principal actually exposes:

      AgentIdentityCreate  AgentIdentity.Create.All | .CreateAsManager | .ReadWrite.All
      AgentIdentityRead    AgentIdentity.Read.All | .ReadWrite.All | Application.Read.All
      AgentUserWrite       AgentIdUser.ReadWrite.All | .IdentityParentedBy | User.ReadWrite.All
      UserWrite            User.ReadWrite.All
      LicenseAssign        LicenseAssignment.ReadWrite.All | AgentIdUser.ReadWrite.All | User.ReadWrite.All
      SubscribedSkuRead    Organization.Read.All | Directory.Read.All
      DirectoryRead        User.Read.All | Directory.Read.All
      BlueprintAccess      Application.ReadWrite.All | Application.ReadWrite.OwnedBy
                           (this group gates agent identity creation: ReadWrite.All allows ANY
                            blueprint, ReadWrite.OwnedBy only blueprints the app owns)

    One further group is OPT-IN and is skipped unless you ask for it:

      AgentIdentityOwnerWrite  AgentIdentity.ReadWrite.All   (-IncludeOwnerWritePermission)
                           Only needed to add NON-USER owners (e.g. a service principal) to an
                           agent identity after it has been created. User owners are bound inline
                           during creation and need nothing extra.

    The credential used for this step must itself hold Application.ReadWrite.All AND
    AppRoleAssignment.ReadWrite.All, and be Application Administrator, Cloud Application
    Administrator or Global Administrator. Run it once with an elevated credential, then run the
    agent-user creation with the configured app.

    If -ConfigurePermissions is supplied WITHOUT -AgentIdentityId or -BlueprintAppId, the script
    only configures permissions and exits.

.PARAMETER GrantAllAlternatives
    Grant every resolvable role in each capability group rather than only the least privileged
    one. Useful when you want the app to keep working as beta roles change.

.PARAMETER IncludeBootstrapRoles
    Additionally grant AppRoleAssignment.ReadWrite.All so the configured application can run
    -ConfigurePermissions itself later. Highly privileged, therefore opt-in.

.PARAMETER IncludeOwnerWritePermission
    Additionally grant AgentIdentity.ReadWrite.All, which is what Microsoft Graph actually
    requires to add an owner to an agent identity AFTER it has been created.

    You only need this to set a NON-USER owner (typically a service principal). Directory users
    are bound inline in the creation request itself and require no extra permission.

    Opt-in because AgentIdentity.ReadWrite.All also grants update and delete over every agent
    identity in the tenant.

    Note: Application.ReadWrite.All does NOT cover this - an agentIdentity is a protected
    servicePrincipal subtype and returns 403. Adding Directory.Read.All does not help either,
    despite what the generic "servicePrincipal: Add owner" documentation implies.

.PARAMETER AgentIdentityOwnerId
    Object IDs of the directory users to set as owners of a NEWLY created agent identity.
    Accepts multiple values. Ignored on the -AgentIdentityId path, which reuses an existing
    identity.

.PARAMETER AgentIdentityOwnerUpn
    Same as -AgentIdentityOwnerId but accepts user principal names / mail addresses, which are
    resolved to object IDs. Combine freely with -AgentIdentityOwnerId.

.PARAMETER NoDefaultOwner
    Suppresses the default behaviour of making the sponsor an owner of a newly created agent
    identity, leaving it with no owner unless -AgentIdentityOwnerId/-AgentIdentityOwnerUpn is
    given.

.PARAMETER AddClientAsBlueprintOwner
    Adds the calling application's service principal as an owner of the blueprint application
    before creating the agent identity.

    Only needed when the app holds Application.ReadWrite.OwnedBy instead of
    Application.ReadWrite.All, since that permission restricts it to blueprints it owns. Adding
    the owner itself requires Application.ReadWrite.All or an existing owner/administrator.

.PARAMETER NoOwnershipSelfHeal
    Disables the automatic take-ownership-and-retry recovery that runs when creation is rejected
    with "Request principal is not the owner". Use it to keep provisioning strictly read-only with
    respect to the blueprint's owner list.

.PARAMETER Update
    Update an agent user that already exists instead of creating one. Requires -AgentUserId.

    Only the attributes supplied on the command line are written. This matters here more than
    elsewhere: -UsageLocation defaults to US and -DisplayName is derived from the user
    principal name, so writing "whatever the variable holds" would silently rewrite a live
    account. Both are sent only when explicitly passed.

    The update refuses to touch an account that is not an agent user, so an ordinary
    employee's account cannot be modified by mistake.

    Update-A365AgentUser.ps1 is the dedicated entry point for this and is usually the clearer
    way to call it. Both run exactly the same code.

.PARAMETER AgentUserId
    With -Update, the object id or user principal name of the agent user to change. The user
    principal name itself cannot be changed after creation.

.EXAMPLE
    # One-time: grant the app the permissions it needs (elevated credential required)
    .\New-A365AgentUser.ps1 -TenantId $tid -ClientId $cid -ClientSecret $sec -ConfigurePermissions

.EXAMPLE
    # Existing Agent Identity, no license
    .\New-A365AgentUser.ps1 -TenantId $tid -ClientId $cid -ClientSecret $sec `
        -AgentIdentityId '8f1c...' -UserPrincipalName 'aria@contoso.com' -DisplayName 'Aria'

.EXAMPLE
    # New Agent Identity from a blueprint, with a license and a manager.
    # NOTE: -ClientId is the MANAGEMENT app doing the work; -BlueprintAppId is only the blueprint
    # the new identity is minted from. They are deliberately different applications.
    .\New-A365AgentUser.ps1 -TenantId $tid -ClientId $cid -CertificateThumbprint $thumb `
        -BlueprintAppId $blueprintAppId -AgentIdentityDisplayName 'Aria Identity' `
        -UserPrincipalName 'aria@contoso.com' -DisplayName 'Aria' -UsageLocation 'US' `
        -SponsorUpn 'owner@contoso.com' -ManagerUpn 'manager@contoso.com' -AssignLicense

.EXAMPLE
    # Give the new agent identity explicit owners instead of defaulting to the sponsor
    .\New-A365AgentUser.ps1 -TenantId $tid -ClientId $cid -ClientSecret $sec `
        -BlueprintAppId $blueprintAppId -AgentIdentityDisplayName 'Aria Identity' `
        -SponsorUpn 'sponsor@contoso.com' `
        -AgentIdentityOwnerUpn 'owner1@contoso.com','owner2@contoso.com' `
        -UserPrincipalName 'aria@contoso.com' -DisplayName 'Aria'

.EXAMPLE
    # Blueprint path where the app only holds Application.ReadWrite.OwnedBy, so it must own the
    # blueprint it mints identities from
    .\New-A365AgentUser.ps1 -TenantId $tid -ClientId $cid -ClientSecret $sec `
        -BlueprintAppId $blueprintAppId -AddClientAsBlueprintOwner `
        -AgentIdentityDisplayName 'Aria Identity' -SponsorUpn 'owner@contoso.com' `
        -UserPrincipalName 'aria@contoso.com' -DisplayName 'Aria'

.EXAMPLE
    # Workload identity federation inside AKS, US Government cloud
    .\New-A365AgentUser.ps1 -TenantId $tid -ClientId $cid -FederatedTokenFile $env:AZURE_FEDERATED_TOKEN_FILE `
        -Environment AzureUSGovernment -BlueprintAppId $bp -AgentIdentityDisplayName 'Aria Identity' `
        -UserPrincipalName 'aria@contoso.us' -DisplayName 'Aria'

.NOTES
    The calling application (-ClientId) is always the identity that authenticates to Graph. For
    the -BlueprintAppId path the blueprint is only DATA in the request body: the blueprint's own
    credentials are never needed.

    IMPORTANT - which Application.* permission the calling app holds decides whether blueprints
    have to be owned:

      Application.ReadWrite.All      Any blueprint in the tenant may be used. Nothing else needed.
                                     This is the simplest configuration and the default choice of
                                     -ConfigurePermissions.

      Application.ReadWrite.OwnedBy  Only blueprints the calling app OWNS may be used. Any other
                                     blueprint fails with HTTP 403 "Request principal is not the
                                     owner", even when AgentIdentity.Create.All is granted. Pair
                                     it with -AddClientAsBlueprintOwner, or let the script take
                                     ownership automatically and retry.

    An agentIdentityBlueprint is a subtype of application whose object id and appId are the SAME
    GUID, so -BlueprintAppId accepts either interchangeably.

    Microsoft documents a sponsor reference as required when creating an agent identity, so pass
    -SponsorUpn or -SponsorUserId on the blueprint path.

    OWNERS OF THE NEW AGENT IDENTITY
    The Agent 365 CLI makes the signed-in user both the sponsor and an owner of each agent
    identity it creates, binding 'owners@odata.bind' in the same POST. This script is app-only and
    has no signed-in user, so the sponsor is used as the default owner. Override with
    -AgentIdentityOwnerUpn/-AgentIdentityOwnerId, or opt out with -NoDefaultOwner.

    Owners that are directory USERS are bound inline in the creation request itself and need no
    extra permission - this is the normal path and is what the CLI does. Anything else (e.g. a
    service principal) cannot be bound inline and must be added afterwards via
    POST /beta/servicePrincipals/{id}/owners/$ref, which requires AgentIdentity.ReadWrite.All
    (grant it with -ConfigurePermissions -IncludeOwnerWritePermission). Application.ReadWrite.All
    is NOT sufficient for that call.

    AgentIdentity.* and AgentIdUser.* are Agent 365 beta permissions. They are granted here as
    normal application appRoleAssignments. Beware: pressing "Grant admin consent" in the Entra
    portal afterwards can silently drop beta DELEGATED scopes, though application role
    assignments created by this script are unaffected.
#>

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    # ---------- Tenant / application ----------
    # Required for confidential-client flows (secret, certificate, federated credential, assertion).
    # Optional for -AccessToken (read from the token's 'tid' claim), managed identity, az CLI and Az PowerShell.
    [Parameter()]
    [string] $TenantId,

    [Parameter()]
    [string] $ClientId,

    # ---------- Authentication methods (supply exactly one) ----------
    [Parameter()] [object] $ClientSecret,                       # string or SecureString
    [Parameter()] [string] $CertificateThumbprint,
    [Parameter()] [ValidateSet('CurrentUser', 'LocalMachine')]
    [string] $CertificateStoreLocation = 'CurrentUser',
    [Parameter()] [string] $CertificatePath,
    [Parameter()] [object] $CertificatePassword,                # string or SecureString
    [Parameter()] [System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
    [Parameter()] [switch] $UseManagedIdentity,
    [Parameter()] [string] $ManagedIdentityClientId,
    [Parameter()] [string] $FederatedTokenFile,
    [Parameter()] [string] $ClientAssertion,
    [Parameter()] [switch] $UseAzureCli,
    [Parameter()] [switch] $UseAzPowerShell,
    [Parameter()] [object] $AccessToken,                        # string or SecureString

    # ---------- Cloud ----------
    [Parameter()]
    [ValidateSet('AzurePublic', 'AzureUSGovernment', 'AzureChina')]
    [string] $Environment = 'AzurePublic',
    [Parameter()] [string] $GraphBaseUrl,
    [Parameter()] [string] $AuthorityHost,

    # ---------- Target: existing identity OR blueprint ----------
    [Parameter()] [string] $AgentIdentityId,
    [Parameter()] [string] $BlueprintAppId,
    [Parameter()] [string] $AgentIdentityDisplayName,

    # ---- Update an existing agent user ----------------------------------------------------
    # Only the properties explicitly supplied on the command line are written; everything else
    # on the account is left exactly as it is.
    [Parameter()] [switch] $Update,
    [Parameter()] [string] $AgentUserId,

    # ---------- Agent user ----------
    [Parameter()] [string] $UserPrincipalName,
    [Parameter()] [string] $DisplayName,
    [Parameter()] [string] $MailNickname,
    [Parameter()] [string] $UsageLocation = 'US',
    [Parameter()] [string] $SponsorUserId,
    [Parameter()] [string] $SponsorUpn,
    [Parameter()] [string] $ManagerUserId,
    [Parameter()] [string] $ManagerUpn,

    # ---------- Agent identity owners ----------
    [Parameter()] [string[]] $AgentIdentityOwnerId,
    [Parameter()] [string[]] $AgentIdentityOwnerUpn,
    [Parameter()] [switch] $NoDefaultOwner,

    # ---------- Licensing (opt-in) ----------
    [Parameter()] [switch] $AssignLicense,
    [Parameter()] [string] $LicenseSkuId = '304b93a3-b1f1-427f-aa02-da21e7c7d675',  # Microsoft Agent 365 Tier 3
    [Parameter()] [string] $LicenseSkuPartNumber,
    [Parameter()] [string[]] $DisabledPlans = @(),

    # ---------- App permission configuration ----------
    [Parameter()] [switch] $ConfigurePermissions,
    [Parameter()] [string] $ConfigureAppId,
    # Grant every alternative role in each capability group instead of just the least privileged one.
    [Parameter()] [switch] $GrantAllAlternatives,
    # Also grant AppRoleAssignment.ReadWrite.All so the configured app can self-configure later.
    [Parameter()] [switch] $IncludeBootstrapRoles,
    # Also grant AgentIdentity.ReadWrite.All, required ONLY to add non-user owners to an agent
    # identity after it exists. Opt-in because it also confers update/delete over every agent
    # identity in the tenant. Not needed for the default (user) owner path, which binds inline.
    [Parameter()] [switch] $IncludeOwnerWritePermission,
    # Add the calling app's service principal as an owner of the blueprint application. Owners may
    # create agent identities for blueprints they own without holding an Agent ID directory role.
    [Parameter()] [switch] $AddClientAsBlueprintOwner,
    # Disable the automatic "take ownership and retry" recovery when the service rejects agent
    # identity creation with "Request principal is not the owner".
    [Parameter()] [switch] $NoOwnershipSelfHeal,

    # ---------- Behaviour ----------
    [Parameter()] [int] $MaxRetries = 8,
    [Parameter()] [int] $RetryDelaySeconds = 5,
    [Parameter()] [switch] $PassThru,

    # =====================================================================
    # LOGGING
    # =====================================================================
    [string] $LogPath,
    [switch] $LogIncludeSecrets,
    [string] $LogCorrelationId
)

# Version 1.0 catches typo'd/uninitialised variables but stays tolerant of absent properties on
# deserialised Graph JSON, which legitimately omit fields depending on the response shape.
Set-StrictMode -Version 1.0

# $IsWindows only exists on PowerShell 6+; compute it once so the rest of the script is version safe.
$script:OnWindows = if ($PSVersionTable.PSVersion.Major -lt 6) { $true } else { [bool](Get-Variable -Name IsWindows -ValueOnly -ErrorAction SilentlyContinue) }
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
$null = Initialize-A365Log -Path $LogPath -ScriptName 'New-A365AgentUser.ps1' `
    -BoundParameters $PSBoundParameters -IncludeSecrets:$LogIncludeSecrets -CorrelationId $LogCorrelationId
if ($script:LogFile) { Write-Host "  Log file           : $($script:LogFile)" -ForegroundColor DarkGray }

trap {
    Write-A365Log -Level ERROR -Message "UNHANDLED: $($_.Exception.Message)" -Detail $_.ScriptStackTrace
    Complete-A365Log -Outcome 'Failed'
    break
}

if ($PSVersionTable.PSVersion.Major -lt 6) {
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }
}

#region ------------------------------------------------------------------ Constants

$script:GraphResourceAppId = '00000003-0000-0000-c000-000000000000'

$script:CloudMap = @{
    AzurePublic       = @{ Graph = 'https://graph.microsoft.com';              Authority = 'https://login.microsoftonline.com' }
    AzureUSGovernment = @{ Graph = 'https://graph.microsoft.us';               Authority = 'https://login.microsoftonline.us' }
    AzureChina        = @{ Graph = 'https://microsoftgraph.chinacloudapi.cn';  Authority = 'https://login.chinacloudapi.cn' }
}

# Application (app-only) Microsoft Graph permissions required by this script, grouped by capability.
#
# Sources (Microsoft Learn, beta):
#   POST /servicePrincipals/microsoft.graph.agentIdentity -> Application: AgentIdentity.Create.All
#        (higher: AgentIdentity.CreateAsManager, AgentIdentity.ReadWrite.All)
#   POST /users (microsoft.graph.agentUser)              -> Application: AgentIdUser.ReadWrite.IdentityParentedBy
#        (higher: AgentIdUser.ReadWrite.All, User.ReadWrite.All)
#   POST /users/{id}/assignLicense                        -> Application: LicenseAssignment.ReadWrite.All
#        (higher: AgentIdUser.ReadWrite.All, Directory.ReadWrite.All, User.ReadWrite.All)
#
# Within a group the roles are listed from LEAST privileged to most privileged. -ConfigurePermissions
# grants the first role the tenant's Microsoft Graph service principal actually exposes, so the app
# registration ends up with least privilege while still tolerating tenants where a newer beta role
# has not yet rolled out. Use -GrantAllAlternatives to grant every resolvable role in each group.
$script:AppRoleGroups = @(
    [pscustomobject]@{
        Group = 'AgentIdentityCreate'; Required = $true
        Purpose = 'Create an Agent Identity from an agent identity blueprint'
        Roles = @('AgentIdentity.Create.All', 'AgentIdentity.CreateAsManager', 'AgentIdentity.ReadWrite.All')
    }
    [pscustomobject]@{
        Group = 'AgentIdentityRead'; Required = $true
        Purpose = 'Read and validate Agent Identity service principals'
        Roles = @('AgentIdentity.Read.All', 'AgentIdentity.ReadWrite.All', 'Application.Read.All')
    }
    [pscustomobject]@{
        Group = 'AgentUserWrite'; Required = $true
        Purpose = 'Create the agent user account bound to the Agent Identity'
        Roles = @('AgentIdUser.ReadWrite.All', 'AgentIdUser.ReadWrite.IdentityParentedBy', 'User.ReadWrite.All')
    }
    [pscustomobject]@{
        Group = 'UserWrite'; Required = $true
        Purpose = 'Set usageLocation and assign the agent user''s manager'
        Roles = @('User.ReadWrite.All')
    }
    [pscustomobject]@{
        Group = 'LicenseAssign'; Required = $true
        Purpose = 'Assign the Microsoft Agent 365 license to the agent user'
        Roles = @('LicenseAssignment.ReadWrite.All', 'AgentIdUser.ReadWrite.All', 'User.ReadWrite.All')
    }
    [pscustomobject]@{
        Group = 'SubscribedSkuRead'; Required = $true
        Purpose = 'Read subscribedSkus to validate license seat availability'
        Roles = @('Organization.Read.All', 'Directory.Read.All')
    }
    [pscustomobject]@{
        Group = 'DirectoryRead'; Required = $true
        Purpose = 'Resolve sponsor and manager users by UPN'
        Roles = @('User.Read.All', 'Directory.Read.All')
    }
    [pscustomobject]@{
        Group = 'AgentIdentityOwnerWrite'; Required = $false; OptIn = $true
        Purpose = 'Add owners to an existing agent identity (only needed for non-user owners)'
        # VERIFIED AGAINST PRODUCTION GRAPH, not inferred from docs.
        #
        # An agentIdentity is a PROTECTED servicePrincipal subtype: writes to it are gated by the
        # AgentIdentity.* write role, NOT by the generic servicePrincipal permissions. Measured:
        #
        #   Application.ReadWrite.All                        -> 403
        #   Application.ReadWrite.All + Directory.Read.All   -> 403   (what Learn's "servicePrincipal:
        #                                                              Add owner" page implies; wrong
        #                                                              for this subtype)
        #   AgentIdentity.ReadWrite.All                      -> 204   <-- the actual requirement
        #
        # This matches the deletion behaviour: DELETE on an agentIdentity is likewise 403 with
        # Application.ReadWrite.All and needs AgentIdentity.ReadWrite.All.
        #
        # Deliberately OPT-IN: AgentIdentity.ReadWrite.All also confers update/delete over every
        # agent identity in the tenant, which is far broader than this script needs. The default
        # owner path binds users inline at creation and requires NONE of this.
        Roles = @('AgentIdentity.ReadWrite.All')
    }
    [pscustomobject]@{
        Group = 'BlueprintAccess'; Required = $false
        Purpose = 'Access the blueprint application when creating an agent identity from it'
        # This group is what actually gates agent identity creation, and the choice is meaningful:
        #
        #   Application.ReadWrite.All      -> the caller may use ANY blueprint in the tenant.
        #   Application.ReadWrite.OwnedBy  -> the caller may only use blueprints it OWNS; using any
        #                                     other blueprint fails with HTTP 403
        #                                     "Request principal is not the owner".
        #
        # ReadWrite.All is therefore listed first even though it is the more privileged role. Keep
        # ReadWrite.OwnedBy only if you intend to scope the app to blueprints it owns, and pair it
        # with -AddClientAsBlueprintOwner (or the automatic ownership self-heal).
        Roles = @('Application.ReadWrite.All', 'Application.ReadWrite.OwnedBy')
    }
)

# Granted only when -IncludeBootstrapRoles is supplied: these let the configured app itself run
# -ConfigurePermissions later. They are highly privileged, so they are opt-in.
$script:BootstrapRoleGroup = [pscustomobject]@{
    Group = 'PermissionBootstrap'; Required = $false
    Purpose = 'Allow this app to configure and consent Graph permissions itself'
    Roles = @('AppRoleAssignment.ReadWrite.All')
}

# Roles the CREDENTIAL running -ConfigurePermissions must already hold.
$script:BootstrapRoles = @('Application.ReadWrite.All', 'AppRoleAssignment.ReadWrite.All')

#endregion

#region ------------------------------------------------------------------ Utilities

function Write-Step {
    param([string] $Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Detail {
    param([string] $Message, [ConsoleColor] $Color = [ConsoleColor]::Gray)
    Write-Host "    $Message" -ForegroundColor $Color
}

function Write-Ok {
    param([string] $Message)
    Write-Host "    $Message" -ForegroundColor Green
}

function ConvertTo-PlainText {
    param([object] $Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Security.SecureString]) {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
        try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }
    return [string] $Value
}

function ConvertTo-Base64Url {
    param([byte[]] $Bytes)
    return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-ErrorResponseBody {
    param($ErrorRecord)
    # PowerShell 6+ surfaces the response body in ErrorDetails; 5.1 requires reading the stream.
    if ($ErrorRecord.ErrorDetails -and $ErrorRecord.ErrorDetails.Message) {
        return $ErrorRecord.ErrorDetails.Message
    }
    try {
        $response = $ErrorRecord.Exception.Response
        if ($null -eq $response) { return $null }
        if ($response.PSObject.Properties.Name -contains 'GetResponseStream') {
            $stream = $response.GetResponseStream()
            if ($null -ne $stream) {
                $reader = New-Object System.IO.StreamReader($stream)
                try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
        }
    } catch { }
    return $null
}

function Get-ErrorStatusCode {
    param($ErrorRecord)
    try {
        $response = $ErrorRecord.Exception.Response
        if ($null -eq $response) { return 0 }
        if ($response.PSObject.Properties.Name -contains 'StatusCode') {
            return [int] $response.StatusCode
        }
    } catch { }
    return 0
}

function Get-GraphErrorMessage {
    param([string] $Body)
    if ([string]::IsNullOrWhiteSpace($Body)) { return $null }
    try {
        $parsed = $Body | ConvertFrom-Json
        if ($parsed.PSObject.Properties.Name -contains 'error') {
            $err = $parsed.error
            if ($err -is [string]) { return $err }
            $code = if ($err.PSObject.Properties.Name -contains 'code') { $err.code } else { $null }
            $msg = if ($err.PSObject.Properties.Name -contains 'message') { $err.message } else { $null }
            return (@($code, $msg) | Where-Object { $_ }) -join ': '
        }
    } catch { }
    return $Body
}

#endregion

#region ------------------------------------------------------------------ Authentication

function Resolve-CloudEndpoints {
    $cloud = $script:CloudMap[$Environment]
    $script:Graph = if ($GraphBaseUrl) { $GraphBaseUrl.TrimEnd('/') } else { $cloud.Graph }
    $script:Authority = if ($AuthorityHost) { $AuthorityHost.TrimEnd('/') } else { $cloud.Authority }
    $script:GraphScope = "$($script:Graph)/.default"
}

function Get-TokenEndpoint {
    # Built lazily because -TenantId may be discovered after the auth method is resolved.
    if ([string]::IsNullOrWhiteSpace($script:TenantId)) {
        throw "-TenantId is required for the '$($script:AuthMethod)' authentication method."
    }
    return "$($script:Authority)/$($script:TenantId)/oauth2/v2.0/token"
}

function Get-TokenTenantClaim {
    # Extracts the 'tid' claim so -AccessToken callers do not have to repeat the tenant id.
    param([string] $Token)
    try {
        $parts = $Token.Split('.')
        if ($parts.Count -lt 2) { return $null }
        $p = $parts[1].Replace('-', '+').Replace('_', '/')
        switch ($p.Length % 4) { 2 { $p += '==' } 3 { $p += '=' } }
        $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p)) | ConvertFrom-Json
        if ($claims.PSObject.Properties.Name -contains 'tid') { return $claims.tid }
    } catch { Write-Verbose "Could not read the 'tid' claim from the supplied access token." }
    return $null
}

function Assert-TenantRequirement {
    <#
        Confidential-client flows must target a specific tenant authority. The other flows either
        carry the tenant inside the token or inherit it from the ambient sign-in context.
    #>
    $tenantRequired = @('ClientSecret', 'CertificateThumbprint', 'CertificatePath', 'Certificate',
                        'FederatedTokenFile', 'ClientAssertion', 'GitHubOidc')

    if ($script:AuthMethod -eq 'AccessToken' -and [string]::IsNullOrWhiteSpace($script:TenantId)) {
        $script:TenantId = Get-TokenTenantClaim (ConvertTo-PlainText $AccessToken)
    }

    if ($script:AuthMethod -in $tenantRequired -and [string]::IsNullOrWhiteSpace($script:TenantId)) {
        throw "-TenantId is required for the '$($script:AuthMethod)' authentication method."
    }
}

function Resolve-AuthMethod {
    <#
        Determines which credential type to use. Explicit parameters win; otherwise fall back to
        the standard Azure SDK environment variables so the script works unchanged in CI.
    #>
    $explicit = @()
    if ($ClientSecret)                        { $explicit += 'ClientSecret' }
    if ($CertificateThumbprint)               { $explicit += 'CertificateThumbprint' }
    if ($CertificatePath)                     { $explicit += 'CertificatePath' }
    if ($Certificate)                         { $explicit += 'Certificate' }
    if ($UseManagedIdentity)                  { $explicit += 'ManagedIdentity' }
    if ($FederatedTokenFile)                  { $explicit += 'FederatedTokenFile' }
    if ($ClientAssertion)                     { $explicit += 'ClientAssertion' }
    if ($UseAzureCli)                         { $explicit += 'AzureCli' }
    if ($UseAzPowerShell)                     { $explicit += 'AzPowerShell' }
    if ($AccessToken)                         { $explicit += 'AccessToken' }

    if ($explicit.Count -gt 1) {
        throw "Specify exactly one authentication method. Found: $($explicit -join ', ')."
    }
    if ($explicit.Count -eq 1) { return $explicit[0] }

    # Auto-detect from environment (Azure SDK conventions).
    if ($env:AZURE_CLIENT_SECRET)           { $script:ClientSecret = $env:AZURE_CLIENT_SECRET;            return 'ClientSecret' }
    if ($env:AZURE_CLIENT_CERTIFICATE_PATH) { $script:CertificatePath = $env:AZURE_CLIENT_CERTIFICATE_PATH; return 'CertificatePath' }
    if ($env:AZURE_FEDERATED_TOKEN_FILE)    { $script:FederatedTokenFile = $env:AZURE_FEDERATED_TOKEN_FILE; return 'FederatedTokenFile' }
    if ($env:ACTIONS_ID_TOKEN_REQUEST_URL -and $env:ACTIONS_ID_TOKEN_REQUEST_TOKEN) { return 'GitHubOidc' }
    if ($env:IDENTITY_ENDPOINT -or $env:MSI_ENDPOINT)                               { return 'ManagedIdentity' }

    throw @'
No authentication method supplied and none could be auto-detected.

Provide one of:
  -ClientSecret <secret>            -CertificateThumbprint <thumbprint>
  -CertificatePath <pfx>            -Certificate <X509Certificate2>
  -UseManagedIdentity               -FederatedTokenFile <path>
  -ClientAssertion <jwt>            -UseAzureCli
  -UseAzPowerShell                  -AccessToken <token>

Or set AZURE_CLIENT_SECRET / AZURE_CLIENT_CERTIFICATE_PATH / AZURE_FEDERATED_TOKEN_FILE.
'@
}

function Get-AuthCertificate {
    if ($Certificate) { return $Certificate }

    if ($CertificatePath) {
        if (-not (Test-Path -LiteralPath $CertificatePath)) {
            throw "Certificate file not found: $CertificatePath"
        }
        $certPassword = ConvertTo-PlainText $CertificatePassword
        $flags = if ($script:OnWindows) {
            # EphemeralKeySet cannot be used with PFX files that require a machine key store on Windows PS 5.1.
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
        } else {
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
        }
        if ([string]::IsNullOrEmpty($certPassword)) {
            return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertificatePath, '', $flags)
        }
        return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertificatePath, $certPassword, $flags)
    }

    if ($CertificateThumbprint) {
        $clean = $CertificateThumbprint -replace '[^0-9a-fA-F]', ''
        foreach ($storeName in @('My')) {
            $path = "Cert:\$CertificateStoreLocation\$storeName\$clean"
            if (Test-Path -LiteralPath $path) { return Get-Item -LiteralPath $path }
        }
        throw "Certificate with thumbprint '$clean' not found in Cert:\$CertificateStoreLocation\My."
    }

    throw 'No certificate source specified.'
}

function New-ClientAssertionJwt {
    <#
        Builds and signs an RS256 client assertion JWT for certificate-based client credentials.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '',
        Justification = 'Builds an in-memory JWT string; changes no system state.')]
    param([System.Security.Cryptography.X509Certificates.X509Certificate2] $Cert)

    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Cert)
    if ($null -eq $rsa) {
        throw "The certificate '$($Cert.Subject)' does not expose an RSA private key. Certificate authentication requires the private key."
    }

    $now = [DateTimeOffset]::UtcNow
    $header = @{
        alg = 'RS256'
        typ = 'JWT'
        x5t = ConvertTo-Base64Url $Cert.GetCertHash()
    }
    $payload = @{
        aud = $script:TokenEndpoint
        iss = $ClientId
        sub = $ClientId
        jti = [guid]::NewGuid().ToString()
        nbf = $now.AddMinutes(-5).ToUnixTimeSeconds()
        exp = $now.AddMinutes(10).ToUnixTimeSeconds()
    }

    $encHeader = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes(($header | ConvertTo-Json -Compress)))
    $encPayload = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress)))
    $signingInput = "$encHeader.$encPayload"

    $signature = $rsa.SignData(
        [Text.Encoding]::UTF8.GetBytes($signingInput),
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

    return "$signingInput.$(ConvertTo-Base64Url $signature)"
}

function Invoke-TokenEndpoint {
    param([hashtable] $Body)
    $endpoint = Get-TokenEndpoint
    try {
        $response = Invoke-RestMethod -Method Post -Uri $endpoint -Body $Body `
            -ContentType 'application/x-www-form-urlencoded' -ErrorAction Stop
        return $response.access_token, ([DateTimeOffset]::UtcNow.AddSeconds([int] $response.expires_in))
    } catch {
        $errBody = Get-ErrorResponseBody $_
        $msg = Get-GraphErrorMessage $errBody
        throw "Token acquisition failed against $($endpoint): $(if ($msg) { $msg } else { $_.Exception.Message })"
    }
}

function Get-ManagedIdentityToken {
    <#
        Supports both the App Service / Container Apps / Functions IDENTITY_ENDPOINT protocol and
        the Azure VM / VMSS IMDS protocol.
    #>
    $resource = $script:Graph

    if ($env:IDENTITY_ENDPOINT -and $env:IDENTITY_HEADER) {
        $uri = "$($env:IDENTITY_ENDPOINT)?api-version=2019-08-01&resource=$([uri]::EscapeDataString($resource))"
        if ($ManagedIdentityClientId) { $uri += "&client_id=$([uri]::EscapeDataString($ManagedIdentityClientId))" }
        $headers = @{ 'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER }
        $r = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers -ErrorAction Stop
    }
    elseif ($env:MSI_ENDPOINT -and $env:MSI_SECRET) {
        $uri = "$($env:MSI_ENDPOINT)?api-version=2017-09-01&resource=$([uri]::EscapeDataString($resource))"
        if ($ManagedIdentityClientId) { $uri += "&clientid=$([uri]::EscapeDataString($ManagedIdentityClientId))" }
        $r = Invoke-RestMethod -Method Get -Uri $uri -Headers @{ Secret = $env:MSI_SECRET } -ErrorAction Stop
    }
    else {
        $uri = "http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=$([uri]::EscapeDataString($resource))"
        if ($ManagedIdentityClientId) { $uri += "&client_id=$([uri]::EscapeDataString($ManagedIdentityClientId))" }
        $r = Invoke-RestMethod -Method Get -Uri $uri -Headers @{ Metadata = 'true' } -TimeoutSec 10 -ErrorAction Stop
    }

    $expires = if ($r.PSObject.Properties.Name -contains 'expires_on') {
        try { [DateTimeOffset]::FromUnixTimeSeconds([int64] $r.expires_on) }
        catch { [DateTimeOffset]::UtcNow.AddMinutes(55) }
    } else { [DateTimeOffset]::UtcNow.AddMinutes(55) }

    return $r.access_token, $expires
}

function Get-GitHubOidcAssertion {
    $uri = "$($env:ACTIONS_ID_TOKEN_REQUEST_URL)&audience=api://AzureADTokenExchange"
    $headers = @{ Authorization = "Bearer $($env:ACTIONS_ID_TOKEN_REQUEST_TOKEN)" }
    $r = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers -ErrorAction Stop
    return $r.value
}

function Get-AzureCliToken {
    $az = Get-Command az -ErrorAction SilentlyContinue
    if (-not $az) { throw "Azure CLI ('az') was not found on PATH. Install it or choose a different authentication method." }
    $azArgs = @('account', 'get-access-token', '--resource', $script:Graph, '--output', 'json')
    if (-not [string]::IsNullOrWhiteSpace($script:TenantId)) { $azArgs += @('--tenant', $script:TenantId) }
    $raw = & az @azArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "az account get-access-token failed: $raw" }
    $parsed = ($raw | Out-String) | ConvertFrom-Json
    $expires = try { [DateTimeOffset]::Parse($parsed.expiresOn) } catch { [DateTimeOffset]::UtcNow.AddMinutes(55) }
    return $parsed.accessToken, $expires
}

function Get-AzPowerShellToken {
    if (-not (Get-Command Get-AzAccessToken -ErrorAction SilentlyContinue)) {
        throw "Get-AzAccessToken was not found. Install the Az.Accounts module or choose a different authentication method."
    }
    $azParams = @{ ResourceUrl = $script:Graph; ErrorAction = 'Stop' }
    if (-not [string]::IsNullOrWhiteSpace($script:TenantId)) { $azParams['TenantId'] = $script:TenantId }
    $t = Get-AzAccessToken @azParams
    # Az 14+ returns the token as a SecureString.
    $token = ConvertTo-PlainText $t.Token
    $expires = try { [DateTimeOffset] $t.ExpiresOn } catch { [DateTimeOffset]::UtcNow.AddMinutes(55) }
    return $token, $expires
}

function Get-GraphToken {
    <#
        Returns a cached bearer token, refreshing it when fewer than 5 minutes remain
        (matching the Agent 365 CLI's TokenExpirationBufferMinutes).
    #>
    param([switch] $Force)

    if (-not $Force -and $script:CachedToken -and $script:CachedTokenExpiry -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        return $script:CachedToken
    }

    $needsClientId = @('ClientSecret', 'CertificateThumbprint', 'CertificatePath', 'Certificate',
                       'FederatedTokenFile', 'ClientAssertion', 'GitHubOidc')
    if ($script:AuthMethod -in $needsClientId -and [string]::IsNullOrWhiteSpace($ClientId)) {
        throw "-ClientId is required for the '$($script:AuthMethod)' authentication method."
    }

    switch ($script:AuthMethod) {
        'ClientSecret' {
            $token, $exp = Invoke-TokenEndpoint @{
                client_id     = $ClientId
                client_secret = (ConvertTo-PlainText $ClientSecret)
                scope         = $script:GraphScope
                grant_type    = 'client_credentials'
            }
        }
        { $_ -in 'CertificateThumbprint', 'CertificatePath', 'Certificate' } {
            $cert = Get-AuthCertificate
            Write-Verbose "Using certificate: $($cert.Subject) (thumbprint $($cert.Thumbprint))"
            $token, $exp = Invoke-TokenEndpoint @{
                client_id             = $ClientId
                scope                 = $script:GraphScope
                grant_type            = 'client_credentials'
                client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
                client_assertion      = (New-ClientAssertionJwt -Cert $cert)
            }
        }
        { $_ -in 'FederatedTokenFile', 'ClientAssertion', 'GitHubOidc' } {
            $assertion = switch ($script:AuthMethod) {
                'FederatedTokenFile' {
                    if (-not (Test-Path -LiteralPath $FederatedTokenFile)) {
                        throw "Federated token file not found: $FederatedTokenFile"
                    }
                    (Get-Content -LiteralPath $FederatedTokenFile -Raw).Trim()
                }
                'ClientAssertion' { $ClientAssertion }
                'GitHubOidc'      { Get-GitHubOidcAssertion }
            }
            $token, $exp = Invoke-TokenEndpoint @{
                client_id             = $ClientId
                scope                 = $script:GraphScope
                grant_type            = 'client_credentials'
                client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
                client_assertion      = $assertion
            }
        }
        'ManagedIdentity' { $token, $exp = Get-ManagedIdentityToken }
        'AzureCli'        { $token, $exp = Get-AzureCliToken }
        'AzPowerShell'    { $token, $exp = Get-AzPowerShellToken }
        'AccessToken'     { $token = ConvertTo-PlainText $AccessToken; $exp = [DateTimeOffset]::UtcNow.AddMinutes(55) }
        default           { throw "Unsupported authentication method '$($script:AuthMethod)'." }
    }

    if ([string]::IsNullOrWhiteSpace($token)) { throw 'Token acquisition returned an empty access token.' }

    $script:CachedToken = $token
    $script:CachedTokenExpiry = $exp
    return $token
}

function Show-TokenIdentity {
    <#
        Decodes the token payload for diagnostics: identifies app-only vs delegated mode and shows
        the effective permissions the token actually carries.
    #>
    param([string] $Token)
    try {
        $parts = $Token.Split('.')
        if ($parts.Count -lt 2) { return }
        $p = $parts[1].Replace('-', '+').Replace('_', '/')
        switch ($p.Length % 4) { 2 { $p += '==' } 3 { $p += '=' } }
        $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p)) | ConvertFrom-Json

        $appId = if ($claims.PSObject.Properties.Name -contains 'appid') { $claims.appid } elseif ($claims.PSObject.Properties.Name -contains 'azp') { $claims.azp } else { '(unknown)' }
        $roles = if ($claims.PSObject.Properties.Name -contains 'roles') { @($claims.roles) } else { @() }
        $scp = if ($claims.PSObject.Properties.Name -contains 'scp') { $claims.scp } else { $null }
        $upn = if ($claims.PSObject.Properties.Name -contains 'upn') { $claims.upn }
               elseif ($claims.PSObject.Properties.Name -contains 'preferred_username') { $claims.preferred_username }
               else { $null }

        Write-Detail "Token audience : $(if ($claims.PSObject.Properties.Name -contains 'aud') { $claims.aud } else { '(unknown)' })"
        Write-Detail "Token appid    : $appId"
        if ($upn) { Write-Detail "Signed-in user : $upn" }
        if ($roles.Count -gt 0) { Write-Detail "Token roles    : $($roles -join ', ')" }
        if ($scp) { Write-Detail "Token scopes   : $scp" }

        $script:TokenIsDelegated = [bool]$scp

        if ($scp) {
            # A delegated token from an admin user is the RECOMMENDED way to bootstrap
            # -ConfigurePermissions, because the app has no permissions of its own yet.
            if ($ConfigurePermissions) {
                Write-Detail "Delegated token detected; permissions will be configured as the signed-in user." ([ConsoleColor]::DarkCyan)
            }
            else {
                Write-Warning "The token is delegated ('scp'), not application-only. Agent provisioning calls expect an app-only token; they will run as the signed-in user and may be denied."
            }
        }
        elseif ($roles.Count -eq 0) {
            if ($ConfigurePermissions) {
                Write-Warning @"
This app-only token carries no application permissions ('roles' claim is absent), so it cannot
configure permissions either - the first Graph call will be denied.

Bootstrap it one of these ways instead:
  1. Sign in as an admin USER and use that delegated token (simplest):
       az login --tenant <tenant> --allow-no-subscriptions
       .\New-A365AgentUser.ps1 -UseAzureCli -ConfigurePermissions -ConfigureAppId $appId
  2. Have an admin consent Application.ReadWrite.All + AppRoleAssignment.ReadWrite.All to this
     app once in the Entra portal, then re-run app-only.
"@
            }
            else {
                Write-Warning "The token contains no 'roles' claim. Graph calls will likely fail with 403. Grant this app permissions first (see -ConfigurePermissions)."
            }
        }
        $script:TokenRoles = $roles
    } catch {
        Write-Verbose "Could not decode token claims: $($_.Exception.Message)"
    }
}

#endregion

#region ------------------------------------------------------------------ Graph plumbing

function Invoke-GraphApi {
    <#
        Single choke point for every Graph call: applies the bearer token, retries transient
        failures (429/5xx and optional caller-supplied predicates), and normalises errors.
    #>
    [CmdletBinding()]
    param(
        [ValidateSet('GET', 'POST', 'PATCH', 'PUT', 'DELETE')]
        [string] $Method = 'GET',
        [Parameter(Mandatory = $true)] [string] $Path,
        [object] $Body,
        [switch] $AllowNotFound,
        [scriptblock] $RetryOn,
        [int] $Retries = -1,
        [int] $DelaySeconds = -1,
        [string] $Activity
    )

    if ($Retries -lt 0) { $Retries = $MaxRetries }
    if ($DelaySeconds -lt 0) { $DelaySeconds = $RetryDelaySeconds }

    $uri = if ($Path -like 'http*') { $Path } else { "$($script:Graph)$Path" }
    $jsonBody = if ($null -ne $Body) {
        if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 10 -Compress }
    } else { $null }

    for ($attempt = 0; $attempt -le $Retries; $attempt++) {
        $headers = @{
            Authorization    = "Bearer $(Get-GraphToken)"
            ConsistencyLevel = 'eventual'
            'client-request-id' = [guid]::NewGuid().ToString()
        }

        try {
            $params = @{
                Method      = $Method
                Uri         = $uri
                Headers     = $headers
                ErrorAction = 'Stop'
            }
            if ($null -ne $jsonBody) {
                $params.Body = [Text.Encoding]::UTF8.GetBytes($jsonBody)
                $params.ContentType = 'application/json; charset=utf-8'
            }

            $result = Invoke-RestMethod @params
            return [pscustomobject]@{ Success = $true; StatusCode = 200; Content = $result; Error = $null }
        }
        catch {
            $status = Get-ErrorStatusCode $_
            $raw = Get-ErrorResponseBody $_
            $message = Get-GraphErrorMessage $raw
            if (-not $message) { $message = $_.Exception.Message }

            if ($status -eq 404 -and $AllowNotFound) {
                return [pscustomobject]@{ Success = $false; StatusCode = 404; Content = $null; Error = $message }
            }

            $isLast = ($attempt -eq $Retries)
            $shouldRetry = $false

            if ($status -eq 429 -or $status -ge 500) { $shouldRetry = $true }
            if ($RetryOn) {
                try { if (& $RetryOn $status $raw) { $shouldRetry = $true } } catch { }
            }

            if ($shouldRetry -and -not $isLast) {
                $wait = $DelaySeconds
                try {
                    $retryAfter = $_.Exception.Response.Headers['Retry-After']
                    if ($retryAfter) { $wait = [int] $retryAfter }
                } catch { }
                $label = if ($Activity) { $Activity } else { "$Method $Path" }
                Write-Detail "$label -> HTTP $status; retrying in ${wait}s (attempt $($attempt + 1)/$Retries)..." ([ConsoleColor]::DarkYellow)
                Start-Sleep -Seconds $wait
                continue
            }

            return [pscustomobject]@{ Success = $false; StatusCode = $status; Content = $null; Error = $message }
        }
    }

    return [pscustomobject]@{ Success = $false; StatusCode = 0; Content = $null; Error = 'Retry attempts exhausted.' }
}

function Assert-GraphSuccess {
    param($Response, [string] $Operation)
    if (-not $Response.Success) {
        throw "$Operation failed (HTTP $($Response.StatusCode)): $($Response.Error)"
    }
    return $Response.Content
}

function Resolve-UserObjectId {
    <#
        Accepts either an object ID or a UPN/mail address and returns the object ID.
    #>
    param([string] $UserIdOrUpn, [string] $Label)

    if ([string]::IsNullOrWhiteSpace($UserIdOrUpn)) { return $null }

    if ($UserIdOrUpn -match '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') {
        return $UserIdOrUpn
    }

    $r = Invoke-GraphApi -Method GET -Path "/v1.0/users/$([uri]::EscapeDataString($UserIdOrUpn))?`$select=id,displayName,userPrincipalName" -AllowNotFound -Activity "Resolve $Label"
    if ($r.Success) {
        Write-Detail "$Label resolved: $($r.Content.displayName) <$($r.Content.userPrincipalName)>"
        return $r.Content.id
    }

    # Fall back to a mail-address filter (the CLI uses this pattern for managers).
    $filter = "/v1.0/users?`$filter=mail eq '$($UserIdOrUpn.Replace("'", "''"))'&`$select=id,displayName,userPrincipalName"
    $r2 = Invoke-GraphApi -Method GET -Path $filter -AllowNotFound -Activity "Resolve $Label by mail"
    if ($r2.Success -and $r2.Content.value -and @($r2.Content.value).Count -gt 0) {
        $u = @($r2.Content.value)[0]
        Write-Detail "$Label resolved: $($u.displayName) <$($u.userPrincipalName)>"
        return $u.id
    }

    throw "Could not resolve $Label '$UserIdOrUpn' to a directory user."
}

#endregion

#region ------------------------------------------------------------------ Permission configuration

function Invoke-ConfigurePermissions {
    <#
        Adds every required Graph application permission to the app registration manifest and
        creates the matching appRoleAssignments (tenant-wide admin consent for app-only access).
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([string] $AppId)

    Write-Step "Configuring Graph application permissions for app $AppId"

    # 1. Microsoft Graph service principal (source of appRole IDs).
    $graphSpResp = Invoke-GraphApi -Method GET `
        -Path "/v1.0/servicePrincipals?`$filter=appId eq '$($script:GraphResourceAppId)'&`$select=id,appRoles" `
        -Activity 'Look up Microsoft Graph service principal'

    if (-not $graphSpResp.Success -and $graphSpResp.StatusCode -eq 403) {
        $who = if ($script:TokenIsDelegated) { 'The signed-in user' } else { "The calling application ($($script:ClientId))" }
        throw @"
Cannot read the Microsoft Graph service principal (HTTP 403): $($graphSpResp.Error)

$who lacks the privileges needed to CONFIGURE permissions. This step is a bootstrap: it cannot
grant itself the rights it needs, so the credential running -ConfigurePermissions must already be
privileged.

Use ONE of the following:

  1. Delegated admin user (simplest - no pre-existing app permissions required):
       az login --tenant $($script:TenantId) --allow-no-subscriptions
       .\New-A365AgentUser.ps1 -UseAzureCli -ConfigurePermissions -ConfigureAppId $AppId
     The signed-in account must hold Application Administrator, Cloud Application Administrator
     or Global Administrator.

  2. A separate, already-privileged bootstrap application:
       .\New-A365AgentUser.ps1 -TenantId $($script:TenantId) -ClientId <adminAppId> -ClientSecret <secret> ``
           -ConfigurePermissions -ConfigureAppId $AppId
     That app needs Application.ReadWrite.All AND AppRoleAssignment.ReadWrite.All, admin-consented.

  3. One-time manual consent in the Entra portal for app $AppId, then re-run app-only:
       Entra ID > App registrations > $AppId > API permissions > Add a permission >
       Microsoft Graph > Application permissions, then "Grant admin consent".

Note: Agent ID Administrator is NOT sufficient - it cannot write appRoleAssignments.
"@
    }

    $graphSp = Assert-GraphSuccess $graphSpResp 'Microsoft Graph service principal lookup'

    if (-not $graphSp.value -or @($graphSp.value).Count -eq 0) {
        throw 'Microsoft Graph service principal was not found in this tenant.'
    }
    $graphSpObj = @($graphSp.value)[0]
    $graphSpId = $graphSpObj.id
    Write-Detail "Microsoft Graph SP object ID: $graphSpId"

    $roleIdByValue = @{}
    foreach ($role in $graphSpObj.appRoles) {
        if ($role.value -and $role.id) { $roleIdByValue[$role.value] = $role.id }
    }

    # 2. Pick the least-privileged resolvable role in each capability group.
    $groups = @($script:AppRoleGroups)
    if ($IncludeBootstrapRoles) { $groups += $script:BootstrapRoleGroup }

    $resolved = @()
    $resolvedValues = @()
    $missingRequired = @()
    $missingOptional = @()

    foreach ($g in $groups) {
        # Opt-in groups are skipped unless the caller explicitly asked for them, so that broad
        # roles are never granted as a side effect of a routine -ConfigurePermissions run.
        if ($g.PSObject.Properties['OptIn'] -and $g.OptIn -and -not $IncludeOwnerWritePermission) {
            Write-Detail ("{0,-20} -> skipped (opt-in; pass -IncludeOwnerWritePermission to grant [{1}])" -f $g.Group, ($g.Roles -join ' | '))
            continue
        }

        $picks = @()
        foreach ($roleName in $g.Roles) {
            if (-not $roleIdByValue.ContainsKey($roleName)) { continue }
            $picks += $roleName
            if (-not $GrantAllAlternatives) { break }
        }

        if ($picks.Count -eq 0) {
            if ($g.Required) { $missingRequired += $g } else { $missingOptional += $g }
            continue
        }

        foreach ($pick in $picks) {
            if ($resolvedValues -contains $pick) { continue }
            $resolvedValues += $pick
            $resolved += [pscustomobject]@{
                Value   = $pick
                Id      = $roleIdByValue[$pick]
                Group   = $g.Group
                Purpose = $g.Purpose
            }
        }
        Write-Detail ("{0,-20} -> {1}" -f $g.Group, ($picks -join ', '))
    }

    if ($missingRequired.Count -gt 0) {
        $detail = ($missingRequired | ForEach-Object { "  - $($_.Group): none of [$($_.Roles -join ', ')] exist" }) -join "`n"
        throw @"
The Microsoft Graph service principal in tenant $($script:TenantId) does not expose any role for
these required capabilities:

$detail

Agent 365 roles (AgentIdentity.*, AgentIdUser.*) ship with the Agent 365 beta. If the tenant is not
yet onboarded, or Microsoft Graph's service principal is stale, refresh it and retry:

  Update-MgApplication, or:
  Invoke-MgGraphRequest -Method PATCH -Uri 'https://graph.microsoft.com/v1.0/servicePrincipals(appId=''00000003-0000-0000-c000-000000000000'')'

Use -GrantAllAlternatives to see every role the tenant does expose.
"@
    }

    foreach ($m in $missingOptional) {
        Write-Warning ("Optional capability '{0}' could not be configured: none of [{1}] are exposed by this tenant's Microsoft Graph service principal. Purpose: {2}" -f
            $m.Group, ($m.Roles -join ', '), $m.Purpose)
    }

    Write-Detail "Selected $($resolved.Count) application permission(s) across $($groups.Count) capability group(s)."

    # 3. Application object (manifest update so permissions show in the portal).
    $appLookup = Invoke-GraphApi -Method GET -Path "/v1.0/applications?`$filter=appId eq '$AppId'&`$select=id,displayName,requiredResourceAccess" -Activity 'Look up application'
    $appObj = Assert-GraphSuccess $appLookup 'Application lookup'
    if (-not $appObj.value -or @($appObj.value).Count -eq 0) {
        throw "Application registration with appId '$AppId' was not found in tenant $TenantId."
    }
    $app = @($appObj.value)[0]
    Write-Detail "Application: $($app.displayName) (object ID $($app.id))"

    # Merge with existing requiredResourceAccess so nothing already configured is lost.
    $rra = @()
    if ($app.PSObject.Properties.Name -contains 'requiredResourceAccess' -and $app.requiredResourceAccess) {
        $rra = @($app.requiredResourceAccess)
    }

    $graphEntry = $rra | Where-Object { $_.resourceAppId -eq $script:GraphResourceAppId } | Select-Object -First 1
    $otherEntries = @($rra | Where-Object { $_.resourceAppId -ne $script:GraphResourceAppId })

    $existingAccess = @()
    if ($graphEntry -and $graphEntry.resourceAccess) {
        $existingAccess = @($graphEntry.resourceAccess | ForEach-Object { @{ id = $_.id; type = $_.type } })
    }

    $existingIds = @($existingAccess | ForEach-Object { $_.id })
    foreach ($r in $resolved) {
        if ($existingIds -notcontains $r.Id) {
            $existingAccess += @{ id = $r.Id; type = 'Role' }
        }
    }

    $newRra = @()
    $newRra += @{ resourceAppId = $script:GraphResourceAppId; resourceAccess = $existingAccess }
    foreach ($o in $otherEntries) {
        $newRra += @{
            resourceAppId  = $o.resourceAppId
            resourceAccess = @($o.resourceAccess | ForEach-Object { @{ id = $_.id; type = $_.type } })
        }
    }

    if ($PSCmdlet.ShouldProcess("application $($app.displayName)", 'Update requiredResourceAccess (API permissions)')) {
        $patch = Invoke-GraphApi -Method PATCH -Path "/v1.0/applications/$($app.id)" -Body @{ requiredResourceAccess = $newRra } -Activity 'Update app manifest'
        if ($patch.Success) {
            Write-Ok 'App manifest updated with required API permissions.'
        } else {
            Write-Warning "Could not update the app manifest (HTTP $($patch.StatusCode)): $($patch.Error). Permissions may not be visible in the portal, but consent grants below still apply."
        }
    }

    # 4. Ensure the client service principal exists.
    $spLookup = Invoke-GraphApi -Method GET -Path "/v1.0/servicePrincipals?`$filter=appId eq '$AppId'&`$select=id,displayName" -Activity 'Look up client service principal'
    $spObj = Assert-GraphSuccess $spLookup 'Client service principal lookup'

    $clientSpId = $null
    if ($spObj.value -and @($spObj.value).Count -gt 0) {
        $clientSpId = @($spObj.value)[0].id
    } elseif ($PSCmdlet.ShouldProcess("appId $AppId", 'Create service principal')) {
        $created = Assert-GraphSuccess (Invoke-GraphApi -Method POST -Path '/v1.0/servicePrincipals' -Body @{ appId = $AppId } -Activity 'Create service principal') 'Service principal creation'
        $clientSpId = $created.id
        Write-Ok "Created service principal $clientSpId"
    }
    if (-not $clientSpId) { throw "Service principal for appId '$AppId' could not be resolved or created." }
    Write-Detail "Client SP object ID: $clientSpId"

    # 5. Existing assignments (idempotency).
    $existing = Invoke-GraphApi -Method GET -Path "/v1.0/servicePrincipals/$clientSpId/appRoleAssignments" -Activity 'Read existing app role assignments'
    $assignedRoleIds = @()
    if ($existing.Success -and $existing.Content.value) {
        $assignedRoleIds = @($existing.Content.value |
            Where-Object { $_.resourceId -eq $graphSpId } |
            ForEach-Object { $_.appRoleId })
    }

    # 6. Grant each missing role.
    $granted = 0; $already = 0; $failed = @()
    foreach ($r in $resolved) {
        if ($assignedRoleIds -contains $r.Id) {
            Write-Detail "already granted : $($r.Value)"
            $already++
            continue
        }
        if (-not $PSCmdlet.ShouldProcess($r.Value, 'Grant application permission')) { continue }

        $assign = Invoke-GraphApi -Method POST -Path "/v1.0/servicePrincipals/$clientSpId/appRoleAssignments" -Body @{
            principalId = $clientSpId
            resourceId  = $graphSpId
            appRoleId   = $r.Id
        } -RetryOn { param($code, $body) $code -eq 404 } -Activity "Grant $($r.Value)"

        if ($assign.Success) {
            Write-Ok "granted         : $($r.Value)"
            $granted++
        } else {
            Write-Warning "Failed to grant '$($r.Value)' (HTTP $($assign.StatusCode)): $($assign.Error)"
            $failed += $r.Value
        }
    }

    Write-Host ''
    Write-Detail "Permissions summary: $granted newly granted, $already already present, $($failed.Count) failed."
    if ($failed.Count -gt 0) {
        Write-Warning @"
Some permissions could not be granted. The credential running -ConfigurePermissions must hold:
  $($script:BootstrapRoles -join ', ')
and the caller must be Application Administrator, Cloud Application Administrator or Global
Administrator (Agent ID Administrator is NOT sufficient to create appRoleAssignments).
Alternatively grant them in the Entra admin center: App registrations > API permissions >
Add a permission > Microsoft Graph > Application permissions, then 'Grant admin consent'.
Failed: $($failed -join ', ')
"@
    } else {
        Write-Ok 'All required application permissions are configured and consented.'
        Write-Detail 'Note: app role assignments can take a few minutes to propagate.' ([ConsoleColor]::DarkYellow)
    }

    return [pscustomobject]@{
        AppId               = $AppId
        ApplicationObjectId = $app.id
        ServicePrincipalId  = $clientSpId
        Granted             = $granted
        AlreadyPresent      = $already
        Failed              = $failed
        RolesConfigured     = @($resolved | ForEach-Object { $_.Value })
        SkippedGroups       = @($missingOptional | ForEach-Object { $_.Group })
    }
}

#endregion

#region ------------------------------------------------------------------ Agent Identity

function Resolve-BlueprintApplication {
    <#
        Resolves the agent identity blueprint's application object. The creation payload's
        'agentIdentityBlueprintId' is accepted as either the blueprint appId or its object id
        depending on tenant/API build, so both are returned and tried in turn.
    #>
    param([Parameter(Mandatory = $true)] [string] $BlueprintAppIdOrObjectId)

    $result = [pscustomobject]@{
        AppId       = $BlueprintAppIdOrObjectId
        ObjectId    = $null
        DisplayName = $null
    }

    $byAppId = Invoke-GraphApi -Method GET `
        -Path "/v1.0/applications?`$filter=appId eq '$BlueprintAppIdOrObjectId'&`$select=id,appId,displayName" `
        -Activity 'Look up blueprint application'

    if ($byAppId.Success -and $byAppId.Content.value -and @($byAppId.Content.value).Count -gt 0) {
        $b = @($byAppId.Content.value)[0]
        $result.AppId = $b.appId
        $result.ObjectId = $b.id
        $result.DisplayName = $b.displayName
        Write-Detail "Blueprint    : $($b.displayName) (appId $($b.appId), object $($b.id))"
        return $result
    }

    # The value may already be an object id, or the caller may lack Application.Read.All.
    $byObjectId = Invoke-GraphApi -Method GET -Path "/v1.0/applications/$BlueprintAppIdOrObjectId`?`$select=id,appId,displayName" `
        -AllowNotFound -Activity 'Look up blueprint application by object id'

    if ($byObjectId.Success) {
        $result.AppId = $byObjectId.Content.appId
        $result.ObjectId = $byObjectId.Content.id
        $result.DisplayName = $byObjectId.Content.displayName
        Write-Detail "Blueprint    : $($byObjectId.Content.displayName) (appId $($result.AppId), object $($result.ObjectId))"
        return $result
    }

    Write-Warning "Could not read the blueprint application '$BlueprintAppIdOrObjectId' (this needs Application.Read.All). Proceeding with the supplied value as the blueprint id."
    return $result
}

function Add-BlueprintOwner {
    <#
        Adds the calling application's service principal as an owner of the blueprint application.
        Per Microsoft Learn, owners can create agent identities for blueprints they own without
        being assigned an Agent ID directory role - this is what lets a separate management app
        (rather than the blueprint app itself) create agent identities.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)] [string] $BlueprintObjectId,
        [Parameter(Mandatory = $true)] [string] $OwnerObjectId
    )

    Write-Step 'Adding the calling app as an owner of the blueprint'

    $existing = Invoke-GraphApi -Method GET -Path "/v1.0/applications/$BlueprintObjectId/owners?`$select=id" -Activity 'Read blueprint owners'
    if ($existing.Success -and $existing.Content.value) {
        $ownerIds = @($existing.Content.value | ForEach-Object { $_.id })
        if ($ownerIds -contains $OwnerObjectId) {
            Write-Ok 'Calling app is already an owner of the blueprint.'
            return 'AlreadyOwner'
        }
    }

    if (-not $PSCmdlet.ShouldProcess($BlueprintObjectId, "Add owner $OwnerObjectId")) { return 'Skipped' }

    $add = Invoke-GraphApi -Method POST -Path "/v1.0/applications/$BlueprintObjectId/owners/`$ref" `
        -Body @{ '@odata.id' = "$($script:Graph)/v1.0/directoryObjects/$OwnerObjectId" } `
        -Activity 'Add blueprint owner'

    if ($add.Success) { Write-Ok 'Calling app added as a blueprint owner.'; return 'Added' }

    if ($add.StatusCode -eq 403) {
        $hasAll = $script:TokenRoles -contains 'Application.ReadWrite.All'
        $cause = if ($hasAll) {
@"
The token already carries Application.ReadWrite.All, so this is not a missing app role: Entra also
requires the caller to be an existing owner (or hold an Application/Cloud Application/Global
Administrator directory role) before it may change a blueprint's owner list.
"@
        } else {
@"
Application.ReadWrite.OwnedBy only permits managing applications the caller ALREADY owns, so it can
never establish first ownership.
"@
        }

        Write-Warning @"
Could not add the calling app as a blueprint owner (HTTP 403): $($add.Error)

$cause
Note this step is only needed when the app cannot be granted Application.ReadWrite.All, which by
itself allows any blueprint to be used with no ownership at all. Otherwise, fix it once:

  A. Have an existing owner (or an Application/Cloud Application/Global Administrator) add it -
     this needs no extra app permissions:
       Connect-MgGraph -TenantId $($script:TenantId) -Scopes Application.ReadWrite.All
       Invoke-MgGraphRequest -Method POST ``
         -Uri 'https://graph.microsoft.com/v1.0/applications/$BlueprintObjectId/owners/`$ref' ``
         -Body @{ '@odata.id' = 'https://graph.microsoft.com/v1.0/directoryObjects/$OwnerObjectId' }

  B. Grant the calling app Application.ReadWrite.All and drop -AddClientAsBlueprintOwner entirely:
       .\New-A365AgentUser.ps1 -TenantId $($script:TenantId) -ClientId <adminApp> <credential> ``
           -ConfigurePermissions -ConfigureAppId $($script:ClientId)
"@
        return 'Failed'
    }

    Write-Warning "Could not add the calling app as a blueprint owner (HTTP $($add.StatusCode)): $($add.Error)"
    return 'Failed'
}

function Get-CallerServicePrincipalId {
    param([Parameter(Mandatory = $true)] [string] $AppId)
    $sp = Invoke-GraphApi -Method GET -Path "/v1.0/servicePrincipals?`$filter=appId eq '$AppId'&`$select=id" -Activity 'Look up calling service principal'
    if ($sp.Success -and $sp.Content.value -and @($sp.Content.value).Count -gt 0) { return @($sp.Content.value)[0].id }
    return $null
}

function Resolve-OwnerPrincipal {
    <#
        Resolves an owner reference to an object id plus its directory type.

        Type matters because 'owners@odata.bind' is bound as /v1.0/users/{id} (the shape the
        Agent 365 CLI uses). Binding a non-user through that path makes Graph return 404, so
        anything that is not a user is routed to the post-creation $ref call instead.
    #>
    param([Parameter(Mandatory = $true)] [string] $OwnerRef)

    if ($OwnerRef -notmatch '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') {
        # Not a GUID, so it can only be a user principal name / mail address.
        return [pscustomobject]@{ Id = (Resolve-UserObjectId -UserIdOrUpn $OwnerRef -Label 'Owner'); Type = 'user' }
    }

    $r = Invoke-GraphApi -Method GET -Path "/v1.0/directoryObjects/$OwnerRef" -AllowNotFound -Activity 'Resolve owner principal'
    if (-not $r.Success) {
        Write-Warning "Owner '$OwnerRef' could not be read from the directory; it will be set after creation rather than inline."
        return [pscustomobject]@{ Id = $OwnerRef; Type = 'unknown' }
    }

    $odataType = if ($r.Content.PSObject.Properties.Name -contains '@odata.type') { $r.Content.'@odata.type' } else { '' }
    $type = if ($odataType -match 'graph\.user$') { 'user' } elseif ($odataType) { $odataType -replace '^#microsoft\.graph\.', '' } else { 'unknown' }

    $label = if ($r.Content.PSObject.Properties.Name -contains 'userPrincipalName' -and $r.Content.userPrincipalName) { $r.Content.userPrincipalName } else { $r.Content.displayName }
    Write-Detail "Owner resolved: $label [$type]"
    return [pscustomobject]@{ Id = $r.Content.id; Type = $type }
}

function Add-AgentIdentityOwner {
    <#
        Adds owners to an agent identity service principal after creation.

        Used as the fallback when 'owners@odata.bind' cannot be supplied inline. The $ref shape
        binds through /directoryObjects, so it accepts users and service principals alike.
        Requires Application.ReadWrite.All (or Directory.ReadWrite.All) on the calling app.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)] [string] $IdentityId,
        [Parameter(Mandatory = $true)] [string[]] $OwnerObjectIds
    )

    Write-Step 'Setting owners on the Agent Identity'

    $current = @()
    $existing = Invoke-GraphApi -Method GET -Path "/beta/servicePrincipals/$IdentityId/owners?`$select=id" -AllowNotFound -Activity 'Read Agent Identity owners'
    if ($existing.Success -and $existing.Content.value) {
        $current = @($existing.Content.value | ForEach-Object { $_.id })
    }

    $added = @()
    $failed = @()

    foreach ($ownerId in ($OwnerObjectIds | Select-Object -Unique)) {
        if ($current -contains $ownerId) {
            Write-Detail "Already an owner: $ownerId"
            $added += $ownerId
            continue
        }

        if (-not $PSCmdlet.ShouldProcess($IdentityId, "Add owner $ownerId")) { continue }

        $r = Invoke-GraphApi -Method POST -Path "/beta/servicePrincipals/$IdentityId/owners/`$ref" `
            -Body @{ '@odata.id' = "$($script:Graph)/v1.0/directoryObjects/$ownerId" } `
            -RetryOn {
                param($code, $body)
                # The identity's directory object may not have replicated yet.
                $code -eq 404 -or ($code -eq 400 -and $body -and ($body -match 'does not exist|not exist|ResourceNotFound'))
            } `
            -Activity 'Add Agent Identity owner'

        if ($r.Success) {
            Write-Ok "Owner added: $ownerId"
            $added += $ownerId
        } else {
            Write-Warning "Could not add owner '$ownerId' (HTTP $($r.StatusCode)): $($r.Error)"
            $failed += $ownerId
        }
    }

    if ($failed.Count -gt 0) {
        Write-Warning @"
$($failed.Count) owner(s) could not be set on Agent Identity $IdentityId. The identity itself was
created successfully, so this is not fatal.

An agent identity is a protected object: adding an owner AFTER creation requires the app-only role
AgentIdentity.ReadWrite.All. Application.ReadWrite.All is NOT sufficient and returns 403 (adding
Directory.Read.All does not help either - that requirement applies to ordinary service principals,
not to this subtype). Grant it explicitly, or add the owners as an administrator:

  .\New-A365AgentUser.ps1 -TenantId <tenant> -ClientId <clientId> <credential> ``
      -ConfigurePermissions -IncludeOwnerWritePermission


  Invoke-MgGraphRequest -Method POST ``
    -Uri '$($script:Graph)/beta/servicePrincipals/$IdentityId/owners/`$ref' ``
    -Body @{ '@odata.id' = '$($script:Graph)/v1.0/directoryObjects/<ownerObjectId>' }

Note that owners which are directory USERS are normally bound inline during creation and do not
need this permission at all.
"@
    }

    return [pscustomobject]@{ Added = $added; Failed = $failed }
}

function New-AgentIdentity {
    <#
        POST /beta/servicePrincipals/Microsoft.Graph.AgentIdentity

        The documented contract (Microsoft Learn, beta) is:
            { displayName, agentIdentityBlueprintId, sponsors@odata.bind }
        The Agent 365 CLI's app-only path instead sends 'agentAppId'. Tenants differ on whether
        'agentIdentityBlueprintId' expects the blueprint appId or its object id, so this function
        walks the documented shape first and falls back through the remaining permutations.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)] [string] $BlueprintApplicationId,
        [Parameter(Mandatory = $true)] [string] $IdentityDisplayName,
        [string] $SponsorObjectId,
        [string[]] $OwnerObjectIds,
        # Subset of $OwnerObjectIds that are directory users and may therefore be bound inline.
        [string[]] $InlineOwnerIds,
        [string] $BlueprintObjectId,
        # Supplied so that a "not the owner" rejection can be self-healed when the calling app
        # holds Application.ReadWrite.All.
        [string] $CallerSpObjectId,
        [switch] $NoOwnershipSelfHeal
    )

    Write-Step "Creating Agent Identity from blueprint $BlueprintApplicationId"
    Write-Detail "Display name : $IdentityDisplayName"
    if ($SponsorObjectId) {
        Write-Detail "Sponsor      : $SponsorObjectId"
    } else {
        Write-Warning 'No sponsor supplied. Microsoft documents a sponsor reference as required when creating an agent identity; pass -SponsorUpn or -SponsorUserId if creation fails.'
    }

    $ownerIds = @($OwnerObjectIds | Where-Object { $_ } | Select-Object -Unique)
    $hasOwners = $ownerIds.Count -gt 0
    # Only users can be bound inline; everything else is added after creation.
    $inlineIds = @($InlineOwnerIds | Where-Object { $_ -and $ownerIds -contains $_ } | Select-Object -Unique)
    $canBindInline = $inlineIds.Count -gt 0
    if ($hasOwners) { Write-Detail "Owner(s)     : $($ownerIds -join ', ')" }

    if (-not $PSCmdlet.ShouldProcess($IdentityDisplayName, 'Create Agent Identity')) { return $null }

    # (key, value) pairs ordered by how well documented / likely to succeed they are.
    $keyValuePairs = @(
        @{ Key = 'agentIdentityBlueprintId'; Value = $BlueprintApplicationId }
    )
    if ($BlueprintObjectId -and $BlueprintObjectId -ne $BlueprintApplicationId) {
        $keyValuePairs += @{ Key = 'agentIdentityBlueprintId'; Value = $BlueprintObjectId }
    }
    $keyValuePairs += @{ Key = 'agentAppId'; Value = $BlueprintApplicationId }

    # The Agent 365 CLI binds owners as /v1.0/users/{id} in the same POST, so that exact shape is
    # attempted first. If a tenant rejects it the body degrades and owners are added afterwards.
    $ownerBind = @($inlineIds | ForEach-Object { "$($script:Graph)/v1.0/users/$_" })

    $newBody = {
        param($key, $value, $withSponsor, $withOwners)
        $b = [ordered]@{ displayName = $IdentityDisplayName; "$key" = $value }
        if ($withSponsor) { $b['sponsors@odata.bind'] = @("$($script:Graph)/v1.0/users/$SponsorObjectId") }
        if ($withOwners)  { $b['owners@odata.bind']   = $ownerBind }
        return $b
    }

    # Ordered most-complete first, then degrading one binding at a time.
    $shapes = @()
    if ($SponsorObjectId -and $canBindInline) { $shapes += , @{ S = $true;  O = $true  } }
    if ($SponsorObjectId)                     { $shapes += , @{ S = $true;  O = $false } }
    if ($canBindInline)                       { $shapes += , @{ S = $false; O = $true  } }
    $shapes += , @{ S = $false; O = $false }

    $bodyVariants = @()
    foreach ($kv in $keyValuePairs) {
        foreach ($shape in $shapes) {
            $bodyVariants += , @{
                Key = $kv.Key; IdValue = $kv.Value; Sponsor = $shape.S; Owners = $shape.O
                Body = (& $newBody $kv.Key $kv.Value $shape.S $shape.O)
            }
        }
    }

    $lastError = $null
    $selfHealAttempted = $false

    # Outer pass exists so a "not the owner" rejection can be recovered from once: the calling app
    # takes ownership of the blueprint (requires Application.ReadWrite.All) and the whole variant
    # sequence is replayed.
    for ($pass = 0; $pass -lt 2; $pass++) {
    $replayAfterOwnershipFix = $false
    foreach ($variant in $bodyVariants) {
        Write-Verbose "Attempting Agent Identity creation with '$($variant.Key)' = $($variant.IdValue) (sponsor: $($variant.Sponsor), owners: $($variant.Owners))."

        # A variant carrying optional bindings must not spend the full replication-lag retry
        # budget: a rejected binding also surfaces as 404, and a simpler variant follows.
        $hasBindings = $variant.Sponsor -or $variant.Owners
        $isLastVariant = ($variant -eq $bodyVariants[-1])
        $variantRetries = if ($hasBindings -and -not $isLastVariant) { 1 } else { -1 }

        $response = Invoke-GraphApi -Method POST -Path '/beta/servicePrincipals/Microsoft.Graph.AgentIdentity' `
            -Body $variant.Body `
            -Retries $variantRetries `
            -RetryOn {
                param($code, $body)
                # Blueprint app / credential replication lag.
                $code -eq 404 -or ($code -eq 400 -and $body -and ($body -match 'AADSTS7000215|AADSTS700016|not yet|replicat'))
            } `
            -Activity 'Create Agent Identity'

        if ($response.Success) {
            $id = $response.Content.id
            Write-Ok "Agent Identity created: $id"
            Write-Detail "Body key used: $($variant.Key)"
            if ($hasOwners) {
                Write-Detail "Owners set inline: $($variant.Owners)"
            }

            $ownersApplied = @()
            $ownersFailed  = @()
            if ($hasOwners) {
                $pending = if ($variant.Owners) {
                    $ownersApplied = $inlineIds
                    @($ownerIds | Where-Object { $inlineIds -notcontains $_ })
                } else {
                    $ownerIds
                }
                if ($pending.Count -gt 0) {
                    $ownerResult = Add-AgentIdentityOwner -IdentityId $id -OwnerObjectIds $pending
                    $ownersApplied += $ownerResult.Added
                    $ownersFailed   = $ownerResult.Failed
                }
            }

            return [pscustomobject]@{
                Id           = $id
                DisplayName  = $response.Content.displayName
                AppId        = if ($response.Content.PSObject.Properties.Name -contains 'appId') { $response.Content.appId } else { $null }
                Owners       = $ownersApplied
                OwnersFailed = $ownersFailed
                Raw          = $response.Content
            }
        }

        $lastError = "HTTP $($response.StatusCode): $($response.Error)"

        if ($response.StatusCode -eq 403) {
            # Empirically, the A365 service enforces blueprint OWNERSHIP in addition to the app
            # role: a caller holding AgentIdentity.Create.All is still rejected with
            # "Request principal is not the owner" when it does not own the blueprint.
            if ($response.Error -match 'not the owner|not an owner') {
                # Self-heal: take ownership of the blueprint, then replay. Requires
                # Application.ReadWrite.All on the calling app.
                if (-not $selfHealAttempted -and -not $NoOwnershipSelfHeal -and $CallerSpObjectId -and $BlueprintObjectId) {
                    $selfHealAttempted = $true
                    Write-Detail 'Blueprint ownership is required; attempting to take ownership automatically...' ([ConsoleColor]::DarkCyan)

                    $healState = Add-BlueprintOwner -BlueprintObjectId $BlueprintObjectId -OwnerObjectId $CallerSpObjectId
                    if ($healState -eq 'Added' -or $healState -eq 'AlreadyOwner') {
                        if ($healState -eq 'Added') {
                            Write-Detail 'Waiting 10s for the ownership change to propagate...' ([ConsoleColor]::DarkYellow)
                            Start-Sleep -Seconds 10
                        }
                        $replayAfterOwnershipFix = $true
                        break   # replay the variant sequence on the next outer pass
                    }
                }
                throw @"
Agent Identity creation was denied (HTTP 403): the calling application may only use blueprints it
OWNS.

Graph response: $($response.Error)

This is caused by the calling app holding Application.ReadWrite.OwnedBy rather than
Application.ReadWrite.All. AgentIdentity.Create.All does NOT override it - the blueprint
application itself is what the caller cannot access. Fix it either way:

  A. Grant Application.ReadWrite.All, after which ANY blueprint may be used and no ownership is
     needed (recommended, and what -ConfigurePermissions selects by default):
       .\New-A365AgentUser.ps1 -TenantId $($script:TenantId) -ClientId <adminApp> <credential> ``
           -ConfigurePermissions -ConfigureAppId $ClientId

  B. Keep Application.ReadWrite.OwnedBy and make the app an owner of this blueprint. An existing
     owner or an Application/Cloud Application/Global Administrator can do it:
       Connect-MgGraph -TenantId $($script:TenantId) -Scopes Application.ReadWrite.All
       Invoke-MgGraphRequest -Method POST ``
         -Uri 'https://graph.microsoft.com/v1.0/applications/$BlueprintApplicationId/owners/`$ref' ``
         -Body @{ '@odata.id' = 'https://graph.microsoft.com/v1.0/directoryObjects/$CallerSpObjectId' }
"@
            }

            throw @"
Agent Identity creation was denied (HTTP 403).

The CALLING application ($ClientId) needs ONE of the following Microsoft Graph APPLICATION
permissions (least privileged first):
  - AgentIdentity.Create.All
  - AgentIdentity.CreateAsManager
  - AgentIdentity.ReadWrite.All

It also needs access to the blueprint application itself, via Application.ReadWrite.All (any
blueprint) or Application.ReadWrite.OwnedBy plus ownership of this blueprint.

Grant them with:
  .\New-A365AgentUser.ps1 -TenantId <tenant> -ClientId <clientId> <credential> -ConfigurePermissions

Graph response: $($response.Error)
"@
        }

        # Degrade to the next (simpler) variant on a rejected request shape. A bad optional
        # binding surfaces as 400 or 404, so both mean "try the simpler body".
        if ($response.StatusCode -ne 400 -and -not ($response.StatusCode -eq 404 -and $hasBindings)) { break }
        Write-Detail "Variant rejected (HTTP $($response.StatusCode)): $($response.Error)" ([ConsoleColor]::DarkYellow)
    }

    if (-not $replayAfterOwnershipFix) { break }
    }

    throw "Failed to create Agent Identity. Last error: $lastError"
}

function Get-AgentIdentity {
    param([Parameter(Mandatory = $true)] [string] $IdentityId)

    Write-Step "Validating existing Agent Identity $IdentityId"
    $r = Invoke-GraphApi -Method GET -Path "/beta/servicePrincipals/$IdentityId" -AllowNotFound -Activity 'Read Agent Identity'
    if (-not $r.Success) {
        throw "Agent Identity '$IdentityId' was not found (HTTP $($r.StatusCode)). Pass the service principal OBJECT ID of the Agent Identity."
    }

    $sp = $r.Content
    Write-Ok "Found: $($sp.displayName)"
    $odataType = if ($sp.PSObject.Properties.Name -contains '@odata.type') { $sp.'@odata.type' } else { $null }
    if ($odataType) {
        Write-Detail "Type         : $odataType"
        if ($odataType -notmatch 'agentIdentity') {
            Write-Warning "Service principal '$IdentityId' does not report an agentIdentity type ('$odataType'). Agent user creation may fail."
        }
    }
    return [pscustomobject]@{ Id = $sp.id; DisplayName = $sp.displayName; Raw = $sp }
}

#endregion

#region ------------------------------------------------------------------ Agent User

function Assert-AgentUserPrincipalNameAvailable {
    <#
        Fails fast when the requested UPN is already taken, BEFORE any agent identity is minted.
        Without this pre-flight the blueprint path creates an identity, then discovers the user
        already exists, and abandons the identity - leaving an orphan behind.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Upn
    )

    Write-Step "Checking that $Upn is available"

    $existing = Invoke-GraphApi -Method GET -Path "/beta/users/$([uri]::EscapeDataString($Upn))" `
        -AllowNotFound -Activity 'Check for existing user'

    if (-not $existing.Success) {
        Write-Ok 'The user principal name is available.'
        return
    }

    $boundTo = Get-IdentityParentId -User $existing.Content
    $boundText = if ($boundTo) { "It is bound to Agent Identity $boundTo." } else { 'It is not an agent user.' }

    throw @"
The user principal name '$Upn' is already taken, so a NEW Agent Identity must not be created.

Existing account : $($existing.Content.displayName) <$($existing.Content.userPrincipalName)>
Object id        : $($existing.Content.id)
$boundText

An agent user is permanently bound to one agent identity at creation time, so the existing account
cannot be re-pointed at a newly created identity. Creating the identity anyway would leave it
orphaned. Choose one of:

  A. Use a different -UserPrincipalName for the new agent.

  B. Reuse the identity that account is already bound to, instead of creating another one:
       .\New-A365AgentUser.ps1 ... -AgentIdentityId $(if ($boundTo) { $boundTo } else { '<identityId>' })

  C. Delete the existing account first, if it is no longer wanted:
       DELETE $($script:Graph)/v1.0/users/$($existing.Content.id)
"@
}

function Get-IdentityParentId {
    <# Reads the agent identity an existing user is bound to, tolerating either payload shape. #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $User)

    if ($User.identityParentId) { return [string]$User.identityParentId }
    if ($User.identityParent -and $User.identityParent.id) { return [string]$User.identityParent.id }
    return $null
}

function New-AgentUser {
    <#
        POST /beta/users with @odata.type = microsoft.graph.agentUser.
        'identityParent' binds the mailbox-bearing user account to the Agent Identity.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)] [string] $IdentityId,
        [Parameter(Mandatory = $true)] [string] $Upn,
        [Parameter(Mandatory = $true)] [string] $UserDisplayName,
        [string] $Nickname,
        [string] $Location,
        # Set when the agent identity was created during this run: reusing a pre-existing account
        # is then never correct, because that account belongs to a different identity.
        [switch] $RequireNewUser
    )

    Write-Step "Creating Agent User $Upn"

    # Idempotency probe (the CLI performs the same GET before creating).
    $existing = Invoke-GraphApi -Method GET -Path "/beta/users/$([uri]::EscapeDataString($Upn))" -AllowNotFound -Activity 'Check for existing user'
    if ($existing.Success) {
        $boundTo = Get-IdentityParentId -User $existing.Content

        if ($RequireNewUser) {
            throw @"
A NEW Agent Identity ($IdentityId) was just created, but the user principal name '$Upn' already
exists. Refusing to reuse that account.

Existing account : $($existing.Content.displayName) <$($existing.Content.userPrincipalName)>
Object id        : $($existing.Content.id)
Bound identity   : $(if ($boundTo) { $boundTo } else { '(not an agent user)' })

An agent user is bound to its identity at creation time and cannot be re-pointed, so reusing this
account would silently leave the new identity orphaned. Re-run with a different
-UserPrincipalName, or with -AgentIdentityId to target an existing identity.
"@
        }

        if ($boundTo -and $boundTo -ne $IdentityId) {
            throw @"
The user '$Upn' already exists but is bound to a DIFFERENT Agent Identity.

Requested identity : $IdentityId
Actual identity    : $boundTo
User object id     : $($existing.Content.id)

That binding is fixed at creation time and cannot be changed. Re-run with
-AgentIdentityId $boundTo to manage the existing pairing, or choose another -UserPrincipalName.
"@
        }

        Write-Ok "User already exists: $($existing.Content.displayName) <$($existing.Content.userPrincipalName)>"
        if ($boundTo) {
            Write-Detail "Already bound to the requested Agent Identity ($boundTo); reusing it."
        } else {
            Write-Detail 'Reusing the existing account instead of creating a new one.'
        }
        return [pscustomobject]@{ Id = $existing.Content.id; AlreadyExisted = $true; Raw = $existing.Content }
    }

    if (-not $Nickname) { $Nickname = $Upn.Split('@')[0] }
    $resolvedLocation = if ($Location) { $Location } else { 'US' }

    Write-Detail "Display name  : $UserDisplayName"
    Write-Detail "Mail nickname : $Nickname"
    Write-Detail "UsageLocation : $resolvedLocation"
    Write-Detail "IdentityParent: $IdentityId"

    if (-not $PSCmdlet.ShouldProcess($Upn, 'Create Agent User')) { return $null }

    # Microsoft Learn documents the scalar 'identityParentId'; the Agent 365 CLI sends the
    # 'identityParent' navigation property. Try the documented shape first, then the CLI shape.
    $bodyVariants = @(
        @{ Key = 'identityParentId'; Body = [ordered]@{
                '@odata.type'     = 'microsoft.graph.agentUser'
                displayName       = $UserDisplayName
                userPrincipalName = $Upn
                mailNickname      = $Nickname
                accountEnabled    = $true
                usageLocation     = $resolvedLocation
                identityParentId  = $IdentityId
            } }
        @{ Key = 'identityParent'; Body = [ordered]@{
                '@odata.type'     = 'microsoft.graph.agentUser'
                displayName       = $UserDisplayName
                userPrincipalName = $Upn
                mailNickname      = $Nickname
                accountEnabled    = $true
                usageLocation     = $resolvedLocation
                identityParent    = @{ id = $IdentityId }
            } }
    )

    $response = $null
    foreach ($variant in $bodyVariants) {
        Write-Verbose "Attempting Agent User creation with '$($variant.Key)'."

        $response = Invoke-GraphApi -Method POST -Path '/beta/users' -Body $variant.Body `
            -RetryOn {
                param($code, $bodyText)
                if (-not $bodyText) { return $false }
                # A rejected request shape is deterministic; fall straight through to the next variant.
                if ($bodyText -match 'not recognis|not recogniz|unrecognized|unsupported propert|invalid propert') { return $false }
                # The Agent Identity SP may still be replicating; the CLI retries this 3 times.
                ($code -eq 400 -or $code -eq 404) -and ($bodyText -match 'identityParent|ResourceNotFound|does not exist|not found')
            } `
            -Retries 4 -DelaySeconds 10 -Activity 'Create Agent User'

        if ($response.Success) {
            Write-Detail "Body key used : $($variant.Key)"
            break
        }

        # Only a rejected request shape is worth retrying with the alternative key.
        if ($response.StatusCode -ne 400) { break }
        Write-Detail "Variant '$($variant.Key)' rejected (HTTP 400): $($response.Error)" ([ConsoleColor]::DarkYellow)
    }

    if (-not $response.Success) {
        if ($response.StatusCode -eq 403) {
            throw @"
Agent User creation was denied (HTTP 403).

The calling application needs ONE of these Microsoft Graph APPLICATION permissions:
  - AgentIdUser.ReadWrite.IdentityParentedBy  (least privileged)
  - AgentIdUser.ReadWrite.All
  - User.ReadWrite.All

Run this script with -ConfigurePermissions using an elevated credential, then retry.
Graph response: $($response.Error)
"@
        }
        throw "Agent User creation failed (HTTP $($response.StatusCode)): $($response.Error)"
    }

    Write-Ok "Agent User created: $($response.Content.id)"
    return [pscustomobject]@{ Id = $response.Content.id; AlreadyExisted = $false; Raw = $response.Content }
}

function Set-AgentUserManager {
    [CmdletBinding(SupportsShouldProcess)]
    param([string] $UserId, [string] $ManagerId)

    Write-Step 'Assigning manager'
    if (-not $PSCmdlet.ShouldProcess($UserId, "Set manager to $ManagerId")) { return $false }

    $r = Invoke-GraphApi -Method PUT -Path "/v1.0/users/$UserId/manager/`$ref" `
        -Body @{ '@odata.id' = "$($script:Graph)/v1.0/users/$ManagerId" } -Activity 'Assign manager'

    if ($r.Success) { Write-Ok 'Manager assigned.'; return $true }
    Write-Warning "Failed to assign manager (HTTP $($r.StatusCode)): $($r.Error)"
    return $false
}

function Set-AgentUserLicense {
    [CmdletBinding(SupportsShouldProcess)]
    param([string] $UserId, [string] $Location, [string] $SkuId, [string] $SkuPartNumber, [string[]] $Disabled)

    Write-Step 'Assigning license'

    # Resolve the SKU by part number when requested, and validate availability.
    $skus = Invoke-GraphApi -Method GET -Path '/v1.0/subscribedSkus?$select=skuId,skuPartNumber,prepaidUnits,consumedUnits' -Activity 'Read subscribed SKUs'
    if ($skus.Success -and $skus.Content.value) {
        $all = @($skus.Content.value)

        if ($SkuPartNumber) {
            $match = $all | Where-Object { $_.skuPartNumber -eq $SkuPartNumber } | Select-Object -First 1
            if (-not $match) {
                throw "License SKU part number '$SkuPartNumber' was not found in tenant $TenantId. Available: $(($all | ForEach-Object { $_.skuPartNumber }) -join ', ')"
            }
            $SkuId = $match.skuId
            Write-Detail "Resolved '$SkuPartNumber' to skuId $SkuId"
        }

        $target = $all | Where-Object { $_.skuId -eq $SkuId } | Select-Object -First 1
        if (-not $target) {
            Write-Warning "SKU '$SkuId' is not present in this tenant's subscribedSkus. Attempting assignment anyway."
        } else {
            $available = [int] $target.prepaidUnits.enabled - [int] $target.consumedUnits
            Write-Detail "SKU $($target.skuPartNumber): $available of $($target.prepaidUnits.enabled) seats available."
            if ($available -le 0) {
                Write-Warning "No available seats for SKU '$($target.skuPartNumber)'. Assignment will likely fail."
            }
        }
    } else {
        Write-Detail 'Could not read subscribedSkus (Organization.Read.All may be missing); skipping pre-validation.' ([ConsoleColor]::DarkYellow)
    }

    if (-not $PSCmdlet.ShouldProcess($UserId, "Assign license $SkuId")) { return $false }

    # usageLocation is mandatory before any license assignment.
    if ($Location) {
        $patch = Invoke-GraphApi -Method PATCH -Path "/v1.0/users/$UserId" -Body @{ usageLocation = $Location } -Activity 'Set usageLocation'
        if ($patch.Success) { Write-Detail "usageLocation set to $Location" }
        else { Write-Warning "Failed to set usageLocation (HTTP $($patch.StatusCode)): $($patch.Error)" }
    }

    $addLicense = [ordered]@{ skuId = $SkuId }
    if ($Disabled -and $Disabled.Count -gt 0) { $addLicense['disabledPlans'] = @($Disabled) }

    $r = Invoke-GraphApi -Method POST -Path "/v1.0/users/$UserId/assignLicense" `
        -Body @{ addLicenses = @($addLicense); removeLicenses = @() } `
        -RetryOn { param($code, $b) $code -eq 404 } `
        -Activity 'Assign license'

    if ($r.Success) {
        Write-Ok "License $SkuId assigned."
        return $true
    }

    Write-Warning "License assignment failed (HTTP $($r.StatusCode)): $($r.Error)"
    return $false
}

#endregion

#region ------------------------------------------------------------------ Main

$script:CachedToken = $null
$script:CachedTokenExpiry = [DateTimeOffset]::MinValue
$script:TokenRoles = @()
$script:TokenIsDelegated = $false

try {
    Resolve-CloudEndpoints

    # ---- Validate the target selection -------------------------------------------------
    $hasIdentity = -not [string]::IsNullOrWhiteSpace($AgentIdentityId)
    $hasBlueprint = -not [string]::IsNullOrWhiteSpace($BlueprintAppId)
    $configureOnly = $ConfigurePermissions -and -not $hasIdentity -and -not $hasBlueprint -and -not $Update
    $existingLocationRequired = $false

    if ($Update) {
        # An update targets an account that already exists, so the two "which identity" selectors
        # are meaningless here and would silently create a second object if they were honoured.
        if ($hasIdentity -or $hasBlueprint) {
            throw '-Update targets an existing Agent User; do not pass -AgentIdentityId or -BlueprintAppId. Identify the account with -AgentUserId (object id) or -UserPrincipalName.'
        }
        if ([string]::IsNullOrWhiteSpace($AgentUserId) -and [string]::IsNullOrWhiteSpace($UserPrincipalName)) {
            throw '-Update requires -AgentUserId (the agent user object id) or -UserPrincipalName to identify the account to update.'
        }
        if ($PSBoundParameters.ContainsKey('UserPrincipalName') -and
            $UserPrincipalName -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') {
            throw "-UserPrincipalName '$UserPrincipalName' is not a valid UPN."
        }
        # Deliberately NOT defaulting -DisplayName or writing the -UsageLocation default here:
        # both carry defaults that would rewrite the account on every update that did not ask for
        # them. Update mode reads $PSBoundParameters so an unsupplied value stays unsupplied.
        if ($AssignLicense -and -not $PSBoundParameters.ContainsKey('UsageLocation')) {
            $existingLocationRequired = $true   # checked after the account is read
        }
    }
    elseif ($hasIdentity -and $hasBlueprint) {
        throw 'Specify either -AgentIdentityId (use an existing Agent Identity) or -BlueprintAppId (create a new one), not both.'
    }
    if (-not $hasIdentity -and -not $hasBlueprint -and -not $ConfigurePermissions -and -not $Update) {
        throw 'Specify -AgentIdentityId to use an existing Agent Identity, or -BlueprintAppId to create a new one. (Use -ConfigurePermissions alone to only configure app permissions, or -Update to modify an existing Agent User.)'
    }
    if (-not $configureOnly -and -not $Update) {
        if ([string]::IsNullOrWhiteSpace($UserPrincipalName)) { throw '-UserPrincipalName is required when creating an Agent User.' }
        if ($UserPrincipalName -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') { throw "-UserPrincipalName '$UserPrincipalName' is not a valid UPN." }
        if ($hasBlueprint -and [string]::IsNullOrWhiteSpace($AgentIdentityDisplayName)) {
            throw '-AgentIdentityDisplayName is required when creating a new Agent Identity with -BlueprintAppId.'
        }
        if ([string]::IsNullOrWhiteSpace($DisplayName)) {
            $DisplayName = $UserPrincipalName.Split('@')[0]
            Write-Verbose "No -DisplayName supplied; defaulting to '$DisplayName'."
        }
        if ($AssignLicense -and [string]::IsNullOrWhiteSpace($UsageLocation)) {
            throw '-UsageLocation is required when -AssignLicense is specified; Microsoft 365 cannot assign a licence without one.'
        }
    }

    # ---- Authenticate ------------------------------------------------------------------
    $script:AuthMethod = Resolve-AuthMethod
    Assert-TenantRequirement

    Write-Step 'Authenticating to Microsoft Graph (application-only)'
    Write-Detail "Cloud        : $Environment"
    Write-Detail "Graph        : $($script:Graph)"
    Write-Detail "Authority    : $($script:Authority)"
    Write-Detail "Tenant       : $(if ($script:TenantId) { $script:TenantId } else { '(from credential context)' })"
    Write-Detail "Auth method  : $($script:AuthMethod)"

    $token = Get-GraphToken
    Write-Ok 'Access token acquired.'
    Show-TokenIdentity -Token $token

    # ---- Optional: configure application permissions -----------------------------------
    $permissionResult = $null
    if ($ConfigurePermissions) {
        $targetApp = if ($ConfigureAppId) { $ConfigureAppId } else { $ClientId }
        if ([string]::IsNullOrWhiteSpace($targetApp)) {
            throw '-ConfigurePermissions requires -ClientId (or -ConfigureAppId) to identify the application to configure.'
        }
        $permissionResult = Invoke-ConfigurePermissions -AppId $targetApp

        if ($configureOnly) {
            Write-Host ''
            Write-Ok 'Permission configuration complete. Re-run with -AgentIdentityId or -BlueprintAppId to create an Agent User.'
            if ($PassThru) { $permissionResult }
            return
        }
    }

    # ---- Resolve sponsor / manager -----------------------------------------------------
    $sponsorId = $null
    if ($SponsorUserId -or $SponsorUpn) {
        Write-Step 'Resolving sponsor'
        $sponsorId = Resolve-UserObjectId -UserIdOrUpn ($(if ($SponsorUserId) { $SponsorUserId } else { $SponsorUpn })) -Label 'Sponsor'
    }

    $managerId = $null
    if ($ManagerUserId -or $ManagerUpn) {
        Write-Step 'Resolving manager'
        $managerId = Resolve-UserObjectId -UserIdOrUpn ($(if ($ManagerUserId) { $ManagerUserId } else { $ManagerUpn })) -Label 'Manager'
    }

    # ---- Update an existing agent user -------------------------------------------------
    # Nothing here creates anything. Every write is gated on the parameter having actually been
    # supplied, so an update touches only the properties the caller named.
    if ($Update) {
        $userRef = if ($AgentUserId) { $AgentUserId } else { $UserPrincipalName }

        Write-Step "Reading Agent User $userRef"
        $lookup = Invoke-GraphApi -Method GET -Path "/beta/users/$([uri]::EscapeDataString($userRef))" `
            -AllowNotFound -Activity 'Read agent user'

        if (-not $lookup.Success) {
            throw @"
No user was found for '$userRef'.

Pass either the agent user's object id (-AgentUserId) or its user principal name
(-UserPrincipalName). A UPN must be the full address, e.g. my-agent@contoso.onmicrosoft.com.
"@
        }

        $target = $lookup.Content
        $targetId = [string]$target.id
        $boundIdentity = Get-IdentityParentId -User $target
        $targetType = [string]$target.'@odata.type'

        # PATCHing a human being's account by mistake is the worst thing this script could do, so
        # the target must be positively identified as an agent user before anything is written.
        if (-not $boundIdentity -and $targetType -notmatch 'agentUser') {
            throw @"
'$($target.userPrincipalName)' ($targetId) is a directory user but NOT an agent user - it is not
bound to any agent identity. This script refuses to modify it.

Agent users are created by this script with -BlueprintAppId or -AgentIdentityId and are permanently
bound to an agent identity. If you genuinely meant to change an ordinary user account, use
Microsoft Graph or the Entra portal directly.
"@
        }

        Write-Ok "Agent User $targetId <$($target.userPrincipalName)>"
        Write-Detail "Display name   : $($target.displayName)"
        Write-Detail "Usage location : $(if ($target.usageLocation) { $target.usageLocation } else { '(none)' })"
        if ($boundIdentity) { Write-Detail "Agent identity : $boundIdentity" }

        # A licence cannot be assigned without a usageLocation, and update mode never writes the
        # parameter's 'US' default. If the account has none either, say so before assignLicense fails.
        if ($AssignLicense -and $existingLocationRequired -and [string]::IsNullOrWhiteSpace($target.usageLocation)) {
            throw "-AssignLicense needs a usage location, and $($target.userPrincipalName) has none set. Re-run with -UsageLocation (for example -UsageLocation US)."
        }

        # ---- Scalar properties ---------------------------------------------------------
        $userPatch = [ordered]@{}
        # -cne, not -ne: -ne is case-insensitive, so a casing-only rename would be discarded.
        if ($PSBoundParameters.ContainsKey('DisplayName') -and $DisplayName -cne [string]$target.displayName) {
            $userPatch['displayName'] = $DisplayName
        }
        if ($PSBoundParameters.ContainsKey('MailNickname') -and $MailNickname -cne [string]$target.mailNickname) {
            $userPatch['mailNickname'] = $MailNickname
        }
        if ($PSBoundParameters.ContainsKey('UsageLocation') -and $UsageLocation -cne [string]$target.usageLocation) {
            $userPatch['usageLocation'] = $UsageLocation
        }

        $userPropertiesUpdated = @()
        if ($userPatch.Count -gt 0) {
            $patchLabel = ($userPatch.Keys -join ', ')
            Write-Step "Updating $patchLabel"
            if ($PSCmdlet.ShouldProcess($targetId, "Set $patchLabel")) {
                $patchResult = Invoke-GraphApi -Method PATCH -Path "/v1.0/users/$targetId" -Body $userPatch -Activity 'Update agent user'
                if ($patchResult.Success) {
                    $userPropertiesUpdated = @($userPatch.Keys)
                    foreach ($k in $userPatch.Keys) { Write-Ok "$k -> '$($userPatch[$k])'" }
                } else {
                    Write-Warning "Failed to update $patchLabel (HTTP $($patchResult.StatusCode)): $($patchResult.Error)"
                }
            }
        } else {
            Write-Step 'No scalar property changes requested'
            Write-Detail 'Pass -DisplayName, -MailNickname or -UsageLocation to change them.'
        }

        # ---- Manager -------------------------------------------------------------------
        $managerAssigned = $false
        if ($managerId) { $managerAssigned = Set-AgentUserManager -UserId $targetId -ManagerId $managerId }

        # ---- Licence -------------------------------------------------------------------
        $licenseAssigned = $false
        if ($AssignLicense) {
            # Only pass a location when one was explicitly supplied, so the account's existing
            # value is never overwritten by this parameter's default.
            $licenseLocation = if ($PSBoundParameters.ContainsKey('UsageLocation')) { $UsageLocation } else { $null }
            $licenseAssigned = Set-AgentUserLicense -UserId $targetId -Location $licenseLocation `
                -SkuId $LicenseSkuId -SkuPartNumber $LicenseSkuPartNumber -Disabled $DisabledPlans
        }

        Write-Host ''
        Write-Host '========================================================' -ForegroundColor Green
        Write-Host ' Agent User update complete' -ForegroundColor Green
        Write-Host '========================================================' -ForegroundColor Green
        Write-Host ("  Tenant ID          : {0}" -f $TenantId)
        Write-Host ("  Agent User ID      : {0}" -f $targetId)
        Write-Host ("  User principal name: {0}" -f $target.userPrincipalName)
        Write-Host ("  Agent Identity ID  : {0}" -f $(if ($boundIdentity) { $boundIdentity } else { '(unknown)' }))
        Write-Host ("  Properties updated : {0}" -f $(if ($userPropertiesUpdated.Count -gt 0) { $userPropertiesUpdated -join ', ' } else { 'none' }))
        Write-Host ("  Manager assigned   : {0}" -f $(if ($managerId) { $managerAssigned } else { 'not requested' }))
        Write-Host ("  License assigned   : {0}" -f $(if ($AssignLicense) { $licenseAssigned } else { 'not requested' }))
        Write-Host ''

        if ($PassThru) {
            [pscustomobject]@{
                TenantId           = $TenantId
                UpdateMode         = $true
                AgentUserId        = $targetId
                UserPrincipalName  = [string]$target.userPrincipalName
                AgentIdentityId    = $boundIdentity
                PropertiesUpdated  = $userPropertiesUpdated
                ManagerAssigned    = $managerAssigned
                LicenseAssigned    = $licenseAssigned
                LicenseSkuId       = $(if ($AssignLicense) { $LicenseSkuId } else { $null })
                Permissions        = $permissionResult
            }
        }
        return
    }

    # ---- Resolve agent identity owners -------------------------------------------------
    # Mirrors the Agent 365 CLI, which makes the signed-in user both sponsor and owner of a new
    # agent identity. This script is app-only, so the sponsor is the closest equivalent and is
    # used as the default owner unless -NoDefaultOwner or explicit owners are supplied.
    $ownerIds = @()
    $ownerUserIds = @()
    if ($AgentIdentityOwnerId -or $AgentIdentityOwnerUpn) {
        Write-Step 'Resolving agent identity owners'
        foreach ($o in @($AgentIdentityOwnerId) + @($AgentIdentityOwnerUpn)) {
            if ([string]::IsNullOrWhiteSpace($o)) { continue }
            $resolved = Resolve-OwnerPrincipal -OwnerRef $o
            if (-not $resolved.Id) { continue }
            $ownerIds += $resolved.Id
            if ($resolved.Type -eq 'user') { $ownerUserIds += $resolved.Id }
        }
    } elseif ($sponsorId -and -not $NoDefaultOwner) {
        # The sponsor is always a directory user, so it is inline-bindable.
        $ownerIds = @($sponsorId)
        $ownerUserIds = @($sponsorId)
    }
    $ownerIds = @($ownerIds | Where-Object { $_ } | Select-Object -Unique)
    $ownerUserIds = @($ownerUserIds | Where-Object { $_ } | Select-Object -Unique)

    # Non-user owners cannot be bound inline and need a permission the default grant set omits.
    # Say so now, while the caller can still fix it, rather than after the identity exists.
    if (-not $hasIdentity) {
        $nonUserOwners = @($ownerIds | Where-Object { $ownerUserIds -notcontains $_ })
        if ($nonUserOwners.Count -gt 0 -and
            $script:TokenRoles -and $script:TokenRoles -notcontains 'AgentIdentity.ReadWrite.All') {
            Write-Warning ("{0} requested owner(s) are not directory users [{1}]. Those cannot be bound inline at creation and must be added afterwards, which requires AgentIdentity.ReadWrite.All - a role this token does not hold. The agent identity and user will still be created; only the owner assignment will fail. Grant it with: -ConfigurePermissions -IncludeOwnerWritePermission" -f
                $nonUserOwners.Count, ($nonUserOwners -join ', '))
        }
    }

    # ---- Agent Identity: reuse or create -----------------------------------------------
    if ($hasIdentity) {
        $identity = Get-AgentIdentity -IdentityId $AgentIdentityId
        $identityCreated = $false
    } else {
        # Pre-flight BEFORE anything is created: a taken UPN must abort here, otherwise the
        # identity below is minted and then stranded.
        Assert-AgentUserPrincipalNameAvailable -Upn $UserPrincipalName

        Write-Step 'Resolving the agent identity blueprint'
        $blueprint = Resolve-BlueprintApplication -BlueprintAppIdOrObjectId $BlueprintAppId

        # Resolved unconditionally: it is needed both by the explicit -AddClientAsBlueprintOwner
        # step and by New-AgentIdentity's automatic ownership self-heal.
        $callerSpId = $null
        if (-not [string]::IsNullOrWhiteSpace($ClientId)) {
            $callerSpId = Get-CallerServicePrincipalId -AppId $ClientId
        }

        if ($AddClientAsBlueprintOwner) {
            if (-not $blueprint.ObjectId) {
                Write-Warning '-AddClientAsBlueprintOwner was requested but the blueprint application object id could not be resolved. Skipping the ownership step.'
            } elseif ([string]::IsNullOrWhiteSpace($ClientId)) {
                Write-Warning '-AddClientAsBlueprintOwner requires -ClientId so the calling app can be identified. Skipping the ownership step.'
            } else {
                if ($callerSpId) {
                    $ownerState = Add-BlueprintOwner -BlueprintObjectId $blueprint.ObjectId -OwnerObjectId $callerSpId
                    # Only a freshly created ownership needs to replicate before it is honoured.
                    if ($ownerState -eq 'Added') {
                        Write-Detail 'Waiting 10s for the ownership change to propagate...' ([ConsoleColor]::DarkYellow)
                        Start-Sleep -Seconds 10
                    }
                } else {
                    Write-Warning "No service principal was found for -ClientId '$ClientId'. Skipping the ownership step."
                }
            }
        }

        $identity = New-AgentIdentity -BlueprintApplicationId $blueprint.AppId `
            -IdentityDisplayName $AgentIdentityDisplayName -SponsorObjectId $sponsorId `
            -OwnerObjectIds $ownerIds -InlineOwnerIds $ownerUserIds `
            -BlueprintObjectId $blueprint.ObjectId -CallerSpObjectId $callerSpId `
            -NoOwnershipSelfHeal:$NoOwnershipSelfHeal
        $identityCreated = $true

        if ($identity) {
            # Allow the new service principal to replicate before it is referenced as identityParent.
            Write-Detail 'Waiting 15s for the Agent Identity to propagate...' ([ConsoleColor]::DarkYellow)
            Start-Sleep -Seconds 15
        }
    }

    if (-not $identity) {
        Write-Warning 'No Agent Identity available (WhatIf mode or creation skipped). Stopping before Agent User creation.'
        return
    }

    # ---- Agent User --------------------------------------------------------------------
    # A freshly minted identity has no user yet, so an existing account is always the wrong one.
    try {
        $user = New-AgentUser -IdentityId $identity.Id -Upn $UserPrincipalName `
            -UserDisplayName $DisplayName -Nickname $MailNickname -Location $UsageLocation `
            -RequireNewUser:$identityCreated
    } catch {
        if ($identityCreated) {
            Write-Warning @"
Agent User creation failed AFTER Agent Identity $($identity.Id) was created, so that identity is
now orphaned. Delete it once you no longer need it:

  Invoke-MgGraphRequest -Method DELETE -Uri '$($script:Graph)/beta/servicePrincipals/$($identity.Id)'
"@
        }
        throw
    }

    if (-not $user) {
        Write-Warning 'Agent User was not created (WhatIf mode).'
        return
    }

    # ---- Manager -----------------------------------------------------------------------
    $managerAssigned = $false
    if ($managerId) { $managerAssigned = Set-AgentUserManager -UserId $user.Id -ManagerId $managerId }

    # ---- License (opt-in) --------------------------------------------------------------
    $licenseAssigned = $false
    if ($AssignLicense) {
        $licenseAssigned = Set-AgentUserLicense -UserId $user.Id -Location $UsageLocation `
            -SkuId $LicenseSkuId -SkuPartNumber $LicenseSkuPartNumber -Disabled $DisabledPlans
    } else {
        Write-Step 'Skipping license assignment'
        Write-Detail 'Pass -AssignLicense to assign the Microsoft Agent 365 license.'
    }

    # ---- Summary -----------------------------------------------------------------------
    Write-Host ''
    Write-Host '========================================================' -ForegroundColor Green
    Write-Host ' Agent 365 provisioning complete' -ForegroundColor Green
    Write-Host '========================================================' -ForegroundColor Green
    Write-Host ("  Tenant ID          : {0}" -f $TenantId)
    Write-Host ("  Agent Identity ID  : {0}{1}" -f $identity.Id, $(if ($identityCreated) { ' (created)' } else { ' (existing)' }))
    Write-Host ("  Identity name      : {0}" -f $identity.DisplayName)
    $identityOwners = @(if ($identity.PSObject.Properties.Name -contains 'Owners') { $identity.Owners } else { @() })
    $identityOwnersFailed = @(if ($identity.PSObject.Properties.Name -contains 'OwnersFailed') { $identity.OwnersFailed } else { @() })
    if ($identityCreated) {
        $ownerText = if ($identityOwners.Count -gt 0) { $identityOwners -join ', ' } else { 'none' }
        if ($identityOwnersFailed.Count -gt 0) { $ownerText += " (failed: $($identityOwnersFailed -join ', '))" }
        Write-Host ("  Identity owners    : {0}" -f $ownerText)
    }
    if ($hasBlueprint) { Write-Host ("  Blueprint app ID   : {0}" -f $BlueprintAppId) }
    Write-Host ("  Agent User ID      : {0}{1}" -f $user.Id, $(if ($user.AlreadyExisted) { ' (existing)' } else { ' (created)' }))
    Write-Host ("  User principal name: {0}" -f $UserPrincipalName)
    Write-Host ("  Usage location     : {0}" -f $UsageLocation)
    Write-Host ("  Manager assigned   : {0}" -f $(if ($managerId) { $managerAssigned } else { 'not requested' }))
    Write-Host ("  License assigned   : {0}" -f $(if ($AssignLicense) { "$licenseAssigned ($LicenseSkuId)" } else { 'not requested' }))
    Write-Host ''

    if ($PassThru) {
        [pscustomobject]@{
            TenantId          = $TenantId
            AgentIdentityId   = $identity.Id
            AgentIdentityName = $identity.DisplayName
            IdentityCreated   = $identityCreated
            IdentityOwners    = $identityOwners
            IdentityOwnersFailed = $identityOwnersFailed
            BlueprintAppId    = $BlueprintAppId
            AgentUserId       = $user.Id
            UserPrincipalName = $UserPrincipalName
            UserAlreadyExisted = $user.AlreadyExisted
            ManagerAssigned   = $managerAssigned
            LicenseAssigned   = $licenseAssigned
            LicenseSkuId      = $(if ($AssignLicense) { $LicenseSkuId } else { $null })
            Permissions       = $permissionResult
        }
    }
}
catch {
    $msg = $_.Exception.Message
    Write-Host ''
    # Multi-line remediation guidance is unreadable once Write-Error re-wraps it, so render the
    # detail to the host verbatim and keep the error record itself to a single summary line.
    if ($msg -match "`n") {
        $lines = $msg -split "`r?`n"
        Write-Host $lines[0] -ForegroundColor Red
        foreach ($line in $lines[1..($lines.Count - 1)]) { Write-Host $line -ForegroundColor DarkYellow }
        Write-Host ''
        Write-Error -Message $lines[0] -ErrorAction Continue
    }
    else {
        Write-Error -Message $msg -ErrorAction Continue
    }
    if ($_.ScriptStackTrace) { Write-Verbose $_.ScriptStackTrace }
    exit 1
}

#endregion

Complete-A365Log -Outcome 'Succeeded'