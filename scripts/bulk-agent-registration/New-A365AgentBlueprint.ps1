<#
.SYNOPSIS
    Creates and fully configures a Microsoft Entra Agent ID / Agent 365 agent identity blueprint
    using ONLY Microsoft Graph REST API calls. Does not use the Agent 365 CLI.

.DESCRIPTION
    Performs the complete blueprint provisioning sequence:

      1. Connect to Microsoft Graph as an application (client credentials) or as a user.
      2. Resolve sponsor / owner principals to directory object IDs.
      3. Resolve requested permission names to their scope / app-role GUIDs on each resource app.
      4. POST   /applications/microsoft.graph.agentIdentityBlueprint          (create blueprint)
         POST   /applications/{id}/microsoft.graph.agentIdentityBlueprint/owners/$ref   (assign owners)
      5. POST   /applications/{id}/microsoft.graph.agentIdentityBlueprint/federatedIdentityCredentials
         or POST /applications/{id}/microsoft.graph.agentIdentityBlueprint/addPassword (dev-only secret)
      6. PATCH  /applications/{id}/microsoft.graph.agentIdentityBlueprint     (identifierUris + exposed
                                                                              scope + requiredResourceAccess)
      7. POST   /servicePrincipals/microsoft.graph.agentIdentityBlueprintPrincipal
         POST   /servicePrincipals/{id}/microsoft.graph.agentIdentityBlueprintPrincipal/owners/$ref
      8. POST   /applications/microsoft.graph.agentIdentityBlueprint/{id}/inheritablePermissions
      9. POST   /oauth2PermissionGrants  +  /servicePrincipals/{id}/appRoleAssignments   (admin consent)

    Every call is issued through Invoke-MgGraphRequest so the wire request is a literal Graph REST call.
    The script is re-runnable: existing objects are detected and reused rather than duplicated.

    AUTHENTICATION
    The script is built to run unattended as an application. Pass -ClientId together with one of
    -ClientSecret, -CertificateThumbprint, -Certificate or -CertificatePath, or use
    -UseManagedIdentity / -AccessToken. In that mode permissions come from Microsoft Graph
    APPLICATION app roles granted to the app registration - delegated scopes are neither requested
    nor honoured - and the script verifies up front that every role it needs is actually granted.
    Use New-A365AutomationApp.ps1 to create that app registration.

    To sign in as a human instead, pass -Interactive. An authentication method must be chosen
    explicitly so an unattended run can never silently block on a sign-in prompt.

    Application (app role) permissions required on Microsoft Graph:
        AgentIdentityBlueprint.Create               AgentIdentityBlueprintPrincipal.Create
        AgentIdentityBlueprint.Read.All             AgentIdentityBlueprintPrincipal.Read.All
        AgentIdentityBlueprint.ReadWrite.All        Application.Read.All
        AgentIdentityBlueprint.AddRemoveCreds.All   User.Read.All, Group.Read.All
    plus DelegatedPermissionGrant.ReadWrite.All and AppRoleAssignment.ReadWrite.All when
    -GrantAdminConsent is used, and when -Owner is supplied:
        Application.ReadWrite.All  + Directory.Read.All        (owners on the APPLICATION)
        AgentIdentityBlueprintPrincipal.ReadWrite.All          (owners on the PRINCIPAL)
    Those two are not interchangeable - Application.ReadWrite.All does not authorize an owner
    write on the blueprint principal.

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

.PARAMETER DisplayName
    Display name of the agent identity blueprint. Also used as the idempotency key on re-run.

.PARAMETER Sponsor
    Sponsor UPN(s) or object ID(s). At least one is REQUIRED by the create API, which accepts a
    collection - verified against the live API, where two sponsors passed to sponsors@odata.bind
    both persisted. Users, dynamic-membership groups and Microsoft 365 groups are supported;
    security groups and role-assignable groups are not.

    Sponsors can only be set on the CREATE call, so re-running against an existing blueprint
    reconciles them separately through sponsors/$ref - verified live that this works delegated
    and that the added sponsor persists.

.PARAMETER Owner
    Optional owner UPN(s), service principal name(s) or object ID(s) to assign as blueprint owners.
    Owners are assigned through the dedicated owners/$ref collection on both the blueprint and its
    blueprint principal, so re-running the script repairs missing owners.
    Owners must be users or service principals - Entra ID rejects groups as application owners
    (unlike -Sponsor, which does accept Microsoft 365 and dynamic groups).

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
    The principal that creates the blueprint is always added as an owner by Entra ID automatically.
    Owners can create and modify agent identities under the blueprint without an Agent ID role.

.PARAMETER ManagedIdentityPrincipalId
    Principal (object) ID of a user-assigned managed identity to register as a federated identity
    credential. This is the recommended production credential.

.PARAMETER NewClientSecret
    Development only. Adds a client secret via addPassword and prints it once.

.PARAMETER ExposedScopeValue
    Name of the delegated scope the blueprint exposes so an agent front end can call the agent
    back end (required for interactive / OBO agents). Defaults to 'access_agent'.
    Pass an empty string to skip exposing a scope.

.PARAMETER RequiredPermission
    Array of hashtables describing the APIs the agent needs, by permission NAME (GUIDs are resolved
    at run time from each resource service principal):

        @{ ResourceAppId = '<guid>'; DelegatedScopes = @('Name1'); AppRoles = @('Name2') }

.PARAMETER GrantAdminConsent
    Grants tenant-wide admin consent on the blueprint principal for every resolved permission.
    Required before agents minted from the blueprint receive scp / roles claims.

.PARAMETER SkipInheritablePermissions
    Skips step 8. By default the script marks every requested resource app as allAllowed so agent
    identities inherit the blueprint's grants without extra consent.

.PARAMETER Update
    Update a blueprint that already exists instead of creating one. Requires -BlueprintId.

    Only the attributes supplied on the command line are written; anything omitted is left
    exactly as it is. -DisplayName and -Sponsor stop being mandatory in this mode.

    Update-A365Blueprint.ps1 is the dedicated entry point for this and is usually the clearer
    way to call it. Both run exactly the same code.

.PARAMETER BlueprintId
    With -Update, the blueprint to change. Accepts either the application (client) id or the
    object id; the script works out which it was given.

.PARAMETER KeyVaultName
    Save the new client secret to this Azure Key Vault instead of relying on the one-time
    console output. Accepts a vault name ("contoso-kv") or the full
    https://contoso-kv.vault.azure.net URI.

    Key Vault is NOT part of Microsoft Graph and cannot be: a Graph token presented to the
    vault is rejected with HTTP 401, because a token is bound to its audience. The write
    therefore goes to the Key Vault data plane on a second token for
    https://vault.azure.net, obtained from the SAME credential used for Graph.

    The caller needs a Key Vault DATA-plane role. Owner and Contributor are NOT enough -
    they carry no dataActions and cannot read or write secrets. Grant "Key Vault Secrets
    Officer" on the vault.

.PARAMETER KeyVaultSecretName
    Name to store the secret under. Defaults to the display name folded to the characters
    Key Vault allows (0-9, a-z, A-Z and '-'). Writing an existing name adds a new VERSION
    rather than overwriting, so history is preserved.

.PARAMETER KeyVaultAccessToken
    A bearer token for https://vault.azure.net, for the cases where one cannot be derived
    from the Graph credential: -AccessToken (a Graph token is audience-bound and cannot be
    exchanged) and -Interactive without a signed-in Azure session.

.EXAMPLE
    # Unattended, running as an application with a client secret from the environment.
    $env:A365_CLIENT_SECRET = '<secret>'
    .\New-A365AgentBlueprint.ps1 -TenantId <tenant> -ClientId <automation-app-id> `
        -DisplayName 'Contoso Expense Agent' -Sponsor alice@contoso.com `
        -ManagedIdentityPrincipalId <mi-principal-id> -GrantAdminConsent

.EXAMPLE
    # Unattended, running as an application with a certificate.
    .\New-A365AgentBlueprint.ps1 -TenantId <tenant> -ClientId <automation-app-id> `
        -CertificateThumbprint A1B2C3... `
        -DisplayName 'Contoso Expense Agent' -Sponsor alice@contoso.com

.EXAMPLE
    # Signed in as a user instead of as an application.
    .\New-A365AgentBlueprint.ps1 -Interactive -TenantId <tenant> -DisplayName 'Dev Agent' `
        -Sponsor alice@contoso.com -NewClientSecret -WhatIf

.EXAMPLE
    # Assign owners alongside the sponsor. Re-running with a longer -Owner list adds only the
    # missing owners; existing owners are left untouched.
    .\New-A365AgentBlueprint.ps1 -TenantId <tenant> -ClientId <automation-app-id> `
        -DisplayName 'Contoso Expense Agent' `
        -Sponsor finance-team@contoso.com `
        -Owner alice@contoso.com, bob@contoso.com, 1511d5e7-c324-4362-ad4b-16c20076e5aa

.NOTES
    Requires PowerShell 7+ and the Microsoft.Graph.Authentication module (used purely as a token
    provider and REST transport).
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'FederatedCredentialName',
    Justification = 'This is the NAME of a federated identity credential, not a password.')]
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'Create')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'CertificatePassword',
    Justification = 'Deliberately accepts a plain string for usability; ConvertTo-SecureStringValue converts it immediately.')]
param(
    [Parameter(Mandatory)][string] $TenantId,

    # Mandatory when creating, where it is also how an existing blueprint is matched. Optional
    # under -Update, where passing it RENAMES the blueprint.
    [Parameter(Mandatory, ParameterSetName = 'Create')]
    [Parameter(ParameterSetName = 'Update')]
    [string] $DisplayName,

    # Mandatory when creating: sponsors ride the create body. Optional under -Update.
    [Parameter(Mandatory, ParameterSetName = 'Create')]
    [Parameter(ParameterSetName = 'Update')]
    [string[]] $Sponsor,

    # --- update mode ---------------------------------------------------------
    # Changes an existing blueprint, addressed by id, and touches ONLY the attributes whose
    # parameters were passed. Nothing is created.
    [Parameter(Mandatory, ParameterSetName = 'Update')][switch] $Update,
    [Parameter(Mandatory, ParameterSetName = 'Update')][string] $BlueprintId,

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

    [string[]] $Owner,
    [switch]   $RequireOwnerAssignment,
    [string]   $Description,

    [string]   $ManagedIdentityPrincipalId,
    [string]   $FederatedCredentialName    = 'a365-managed-identity',

    [switch]   $NewClientSecret,
    [ValidateRange(1, 730)][int] $ClientSecretLifetimeDays = 180,

    [AllowEmptyString()][string] $ExposedScopeValue = 'access_agent',

    [hashtable[]] $RequiredPermission,

    [switch]   $GrantAdminConsent,
    [switch]   $SkipInheritablePermissions,

    [string]   $OutputJsonPath,

    # =====================================================================
    # KEY VAULT
    # =====================================================================
    [string] $KeyVaultName,
    [string] $KeyVaultSecretName,
    [object] $KeyVaultAccessToken,

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
$null = Initialize-A365Log -Path $LogPath -ScriptName 'New-A365AgentBlueprint.ps1' `
    -BoundParameters $PSBoundParameters -IncludeSecrets:$LogIncludeSecrets -CorrelationId $LogCorrelationId
if ($script:LogFile) { Write-Host "  Log file           : $($script:LogFile)" -ForegroundColor DarkGray }

trap {
    Write-A365Log -Level ERROR -Message "UNHANDLED: $($_.Exception.Message)" -Detail $_.ScriptStackTrace
    Complete-A365Log -Outcome 'Failed'
    break
}

# ---------------------------------------------------------------------------
# Well-known resource app IDs
# ---------------------------------------------------------------------------
$script:WellKnown = @{
    MicrosoftGraph          = '00000003-0000-0000-c000-000000000000'
    Agent365Observability   = '9b975845-388f-4429-889e-eab1ef63949c'
    Agent365ToolingGateway  = 'ea9ffc3e-8a23-4a7d-836d-234d7c7565c1'
    SharePointOnline        = '00000003-0000-0ff1-ce00-000000000000'
}

if (-not $PSBoundParameters.ContainsKey('RequiredPermission')) {
    # Default: Agent 365 observability ingestion (both flows) + minimal Graph delegated access.
    $RequiredPermission = @(
        @{
            ResourceAppId   = $script:WellKnown.Agent365Observability
            DelegatedScopes = @('Agent365.Observability.OtelWrite')   # OBO  -> scp claim
            AppRoles        = @('Agent365.Observability.OtelWrite')   # S2S  -> roles claim
        },
        @{
            ResourceAppId   = $script:WellKnown.MicrosoftGraph
            DelegatedScopes = @('User.Read')
            AppRoles        = @()
        }
    )
}

# ---------------------------------------------------------------------------
# Graph REST helper - retries, OData-Version header, structured errors
# ---------------------------------------------------------------------------
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
# Resource appIds whose inheritablePermissions entry could not be confirmed present after writing.
# The service accepts these writes with 201 and can still drop them, so success is never assumed.
$script:InheritanceMissing = @()
# Which owner collection was refused. The blueprint APPLICATION and the blueprint PRINCIPAL are
# governed by different permissions, so the closing advice has to know which one failed.
$script:OwnerDenialScope = [System.Collections.Generic.HashSet[string]]::new()

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

function Get-GraphErrorInfo {
    param($ErrorRecord)

    # Graph error codes that map deterministically to an HTTP status.
    $codeToStatus = @{
        'Request_ResourceNotFound'               = 404
        'ResourceNotFound'                       = 404
        'itemNotFound'                           = 404
        'Request_BadRequest'                     = 400
        'badRequest'                             = 400
        'Authorization_RequestDenied'            = 403
        'accessDenied'                           = 403
        'InvalidAuthenticationToken'             = 401
        'Request_MultipleObjectsWithSameKeyValue'= 409
        'activityLimitReached'                   = 429
        'serviceNotAvailable'                    = 503
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
                             # Invoke-Graph throws "... failed [403 Code]: message". A caller that
                             # catches and re-parses that string must still recover the status.
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

function Invoke-Graph {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PATCH', 'PUT', 'DELETE')][string] $Method,
        [Parameter(Mandatory)][string] $Uri,
        $Body,
        [int]    $MaxAttempts    = 6,
        [string] $Accept,                # e.g. full OData metadata, to force @odata.type into the body
        [switch] $TolerateNotFound,
        [switch] $TolerateConflict,
        [switch] $TolerateForbidden,
        [switch] $TolerateBadRequest,
        [switch] $RetryOnNotFound,  # for replication lag right after a create
        [switch] $RetryOnAppPropagation
    )

    if ($Uri -notmatch '^https?://') { $Uri = "$script:GraphRoot$Uri" }

    $json = $null
    if ($null -ne $Body) {
        $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 25 }
    }

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            $reqParams = @{
                Method      = $Method
                Uri         = $Uri
                Headers     = @{ 'OData-Version' = '4.0' }
                OutputType  = 'PSObject'
                ErrorAction = 'Stop'
            }
            if ($Accept) { $reqParams.Headers['Accept'] = $Accept }
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

            if ($info.Status -eq 404 -and $TolerateNotFound -and -not $RetryOnNotFound) { return $null }
            if ($info.Status -in 401, 403 -and $TolerateForbidden) {
                Write-Verbose "$($info.Status) tolerated for $Method $Uri"
                return $null
            }
            if ($info.Status -eq 409 -and $TolerateConflict) {
                Write-Verbose "409 Conflict tolerated for $Method $Uri"
                return $null
            }
            # Used to probe a type cast: Graph answers 400 "is not an AgentIdentityBlueprint
            # Principal" when the object exists but is not of the cast type.
            if ($info.Status -eq 400 -and $TolerateBadRequest) {
                Write-Verbose "400 tolerated for $Method $Uri - $($info.Message)"
                return $null
            }

            # Creating the blueprint principal seconds after its application can fail with
            # 400 "does not reference a valid application object" - the application exists, the
            # servicePrincipal API just has not observed it yet. Reproduced twice app-only.
            $appNotPropagated = $info.Status -eq 400 -and $RetryOnAppPropagation -and
                                $info.Message -match 'does not reference a valid application object'

            $transient = ($info.Status -in 429, 500, 502, 503, 504) -or
                         ($info.Status -eq 404 -and $RetryOnNotFound) -or
                         $appNotPropagated

            if ($transient -and $attempt -lt $MaxAttempts) {
                $delay = [Math]::Min([Math]::Pow(2, $attempt), 30)
                Write-Verbose "Transient $($info.Status) on $Method $Uri - retry $attempt/$MaxAttempts in ${delay}s"
                Start-Sleep -Seconds $delay
                continue
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

# Member enumeration ($collection.Prop) throws under StrictMode when the collection is empty.
function Resolve-DirectoryPrincipal {
    param([Parameter(Mandatory)][string] $Identifier)

    if ($Identifier -match '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') {
        # Same appId-vs-object-id trap as Resolve-OwnerPrincipal, but a sponsor must be a user or a
        # group, so there is nothing to translate an appId into - just fail clearly instead of
        # letting it surface later as an unexplained 404 on the sponsor write.
        $probe = Invoke-Graph -Method GET -Uri "/directoryObjects/$Identifier" -TolerateNotFound -TolerateForbidden
        if ($probe) {
            $probeType = ''
            if (Test-HasProperty $probe '@odata.type') { $probeType = [string]$probe.'@odata.type' }
            if ($probeType -eq '#microsoft.graph.application') {
                $sponsorName = $Identifier
                if ((Test-HasProperty $probe 'displayName') -and $probe.displayName) { $sponsorName = [string]$probe.displayName }
                throw "Sponsor '$Identifier' is the application object for '$sponsorName', which cannot be a sponsor. A sponsor must be a user or a group - pass a UPN, a group name, or the object id of one."
            }
            return [pscustomobject]@{ Id = $Identifier; Segment = 'directoryObjects'; Display = $Identifier }
        }

        $byAppId = Invoke-Graph -Method GET `
            -Uri "/servicePrincipals?`$select=id,displayName,appId&`$filter=appId eq '$($Identifier -replace "'", "''")'" `
            -TolerateNotFound -TolerateForbidden

        if (Test-HasProperty $byAppId 'value') {
            $hit = @($byAppId.value)
            if ($hit.Count -ge 1) {
                throw "Sponsor '$Identifier' is an application appId ($($hit[0].displayName)), not a directory object id. A sponsor must be a user or a group - pass a UPN, a group name, or the object id of one."
            }
            throw "Sponsor '$Identifier' does not exist in tenant $TenantId. Pass a user UPN, a group display name, or a directory object id."
        }

        # Could not verify - send it unchanged, as before.
        return [pscustomobject]@{ Id = $Identifier; Segment = 'directoryObjects'; Display = $Identifier }
    }

    $encoded = [uri]::EscapeDataString($Identifier)

    $user = Invoke-Graph -Method GET -Uri "/users/$encoded`?`$select=id,userPrincipalName" -TolerateNotFound
    if ($user) { return [pscustomobject]@{ Id = $user.id; Segment = 'users'; Display = $Identifier } }

    $filter = "displayName eq '$($Identifier -replace "'", "''")' or mailNickname eq '$($Identifier -replace "'", "''")'"
    $groups = Invoke-Graph -Method GET -Uri "/groups?`$select=id,displayName&`$filter=$([uri]::EscapeDataString($filter))" -TolerateNotFound
    $matched = @()
    if (Test-HasProperty $groups 'value') { $matched = @($groups.value) }
    if ($matched.Count -eq 1) { return [pscustomobject]@{ Id = $matched[0].id; Segment = 'groups'; Display = $Identifier } }
    if ($matched.Count -gt 1) {
        throw "'$Identifier' matched $($matched.Count) groups. Pass the object ID instead."
    }

    throw "Could not resolve '$Identifier' to a user or group in tenant $TenantId."
}

# Blueprint owners must be users or service principals. Entra ID rejects groups as application
# owners, so this resolver deliberately does NOT fall back to /groups the way sponsors do.
function Resolve-OwnerPrincipal {
    param([Parameter(Mandatory)][string] $Identifier)

    if ($Identifier -match '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') {
        # A GUID is ambiguous. owners/$ref needs a directoryObject id, but the GUID people have to
        # hand - the one shown as "Application (client) ID" in the portal and passed to -ClientId -
        # is the appId, which is a DIFFERENT value. Sending an appId to owners/$ref fails with
        #   404 Request_ResourceNotFound: Resource '<guid>' does not exist
        # which names the GUID but never explains that the wrong kind of GUID was used. Resolve it
        # here instead of letting that surface later as an unexplained 404.
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
                        -Uri "/servicePrincipals?`$select=id,displayName,appId&`$filter=appId eq '$($ownerAppId -replace "'", "''")'" `
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
            -Uri "/servicePrincipals?`$select=id,displayName,appId&`$filter=appId eq '$($Identifier -replace "'", "''")'" `
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
    $literal = $Identifier -replace "'", "''"

    $user = Invoke-Graph -Method GET -Uri "/users/$encoded`?`$select=id,userPrincipalName" -TolerateNotFound
    if ($user) {
        return [pscustomobject]@{ Id = $user.id; Type = 'user'; Display = $user.userPrincipalName }
    }

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

    # Give a precise error when the caller passed a group, which is a common and silent mistake.
    $groupFilter = "displayName eq '$literal' or mailNickname eq '$literal'"
    $groups = Invoke-Graph -Method GET `
        -Uri "/groups?`$select=id,displayName&`$filter=$([uri]::EscapeDataString($groupFilter))" `
        -TolerateNotFound
    if ((Test-HasProperty $groups 'value') -and @($groups.value).Count -gt 0) {
        throw "Owner '$Identifier' resolves to a group. Entra ID does not accept groups as blueprint owners - pass a user UPN or a service principal instead. (Groups are only valid for -Sponsor.)"
    }

    throw "Could not resolve owner '$Identifier' to a user or service principal in tenant $TenantId."
}

# Owner assignment. Deliberately write-only: the owners collection is never read back.
#
# Reading it meant a GET on the type-cast navigation path, which fails with
# "ServicePrincipal is not an AgentIdentityBlueprint Principal" whenever the underlying object is
# not really a blueprint principal - taking down a run that had already done all its real work.
# Owners come from -Owner, so there is nothing to discover: POST each reference and let Graph
# decide. A 409 - or, in practice, a 400 "object references already exist" - simply means the owner
# was already there.
function Add-OwnerAssignment {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string] $BaseUri,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Owner,
        [Parameter(Mandatory)][string] $Label
    )

    if ($Owner.Count -eq 0) { return @() }

    # Writing an owners collection is NOT covered by the AgentIdentityBlueprint.* roles, and the two
    # targets differ:
    #   Blueprint APPLICATION -> Application.ReadWrite.All (or .OwnedBy) plus Directory.Read.All.
    #   Blueprint PRINCIPAL   -> AgentIdentityBlueprintPrincipal.ReadWrite.All. Verified live:
    #                            Application.ReadWrite.All is NOT sufficient here and an app holding
    #                            it is still refused with a bare "Insufficient privileges".
    # A denial must not destroy an otherwise working blueprint, so it degrades to a warning unless
    # -RequireOwnerAssignment was passed.
    $assigned = @()
    foreach ($o in $Owner) {
        if ($PSCmdlet.ShouldProcess($o.Display, "POST $BaseUri/owners/`$ref")) {
            try {
                # 409 means "already an owner" - the desired end state either way.
                Invoke-Graph -Method POST -Uri "$BaseUri/owners/`$ref" `
                    -Body @{ '@odata.id' = "$script:GraphRoot/directoryObjects/$($o.Id)" } `
                    -TolerateConflict -RetryOnNotFound | Out-Null
                Write-Host "  $Label owner assigned: $($o.Display) [$($o.Type)]" -ForegroundColor Green
                $assigned += $o.Id
            }
            catch {
                $msg = $_.Exception.Message
                # Graph does NOT return 409 for a duplicate owner on owners/$ref - it returns
                #   400 Request_BadRequest: One or more added object references already exist
                #   for the following modified properties: 'owners'.
                # so -TolerateConflict never sees it. Verified live: an app holding ONLY
                # AgentIdentityBlueprintPrincipal.ReadWrite.All + Directory.Read.All gets exactly
                # this 400 for an owner that is already present, which is the desired end state.
                # Treating it as a denial made every idempotent re-run report a false permission
                # failure and tell the user to grant roles they already had.
                if ($msg -match 'object references already exist') {
                    Write-Host "  $Label owner already assigned: $($o.Display) [$($o.Type)]" -ForegroundColor Green
                    $assigned += $o.Id
                    continue
                }
                if ($msg -match 'is not an AgentIdentityBlueprint Principal') {
                    $script:OwnerAssignmentDenied = $true
                    [void] $script:OwnerDenialScope.Add($Label)
                    Write-Warning ("$Label owner '$($o.Display)' could not be assigned: the target is not an " +
                                   'agentIdentityBlueprintPrincipal. See the guidance at the end of this run.')
                    continue
                }
                if ($msg -notmatch '\[(400|401|403)\b') { throw }
                $script:OwnerAssignmentDenied = $true
                [void] $script:OwnerDenialScope.Add($Label)
                $needed = if ($Label -eq 'Principal') { 'AgentIdentityBlueprintPrincipal.ReadWrite.All' }
                          else { 'Application.ReadWrite.All (or .OwnedBy) plus Directory.Read.All' }
                Write-Warning "$Label owner '$($o.Display)' was refused - the caller is missing $needed."
                Write-Verbose "Full error: $msg"
                if ($RequireOwnerAssignment) {
                    throw "Owner assignment failed and -RequireOwnerAssignment was specified. $(Get-OwnerDenialAdvice)"
                }
            }
        }
    }
    return $assigned
}

# Printed once, at the end of a run, when any owner write was refused. The advice is scoped, because
# granting Application.ReadWrite.All for a PRINCIPAL denial - the obvious guess, and what an earlier
# version of this script recommended - does not help at all.
function Get-OwnerDenialAdvice {
    $scopes = @($script:OwnerDenialScope)
    $wantApp   = ($scopes.Count -eq 0) -or ($scopes -contains 'Blueprint')
    $wantPrin  = ($scopes.Count -eq 0) -or ($scopes -contains 'Principal')

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('Owner writes are not covered by the AgentIdentityBlueprint.* roles.')

    if ($wantApp) {
        $lines.Add('')
        $lines.Add('Owners on the blueprint APPLICATION:')
        $lines.Add('  Application permissions : Application.ReadWrite.All (or Application.ReadWrite.OwnedBy)')
        $lines.Add('                            AND Directory.Read.All')
        $lines.Add('  Delegated permissions   : the same two, and the signed-in user must hold Application')
        $lines.Add('                            Administrator or Cloud Application Administrator in Entra.')
    }
    if ($wantPrin) {
        $lines.Add('')
        $lines.Add('Owners on the blueprint PRINCIPAL (the servicePrincipal):')
        $lines.Add('  Application permissions : AgentIdentityBlueprintPrincipal.ReadWrite.All')
        $lines.Add('  Delegated permissions   : the same scope, or a directory role that confers it.')
        $lines.Add('  Application.ReadWrite.All does NOT authorize this call - verified against the live')
        $lines.Add('  service. An app holding it is still refused with "Insufficient privileges", so')
        $lines.Add('  granting more Application.* permissions will not fix a principal owner denial.')
    }

    $lines.Add('')
    $lines.Add('Grant the missing roles and re-run this script - it adds only the missing owners:')
    $lines.Add('  .\New-A365AutomationApp.ps1 -Scenario Blueprint')
    $lines.Add('Newly granted app roles only appear in tokens issued afterwards, so allow ~2 minutes.')

    return ($lines -join [Environment]::NewLine)
}

# Determines whether the service principal backing a blueprint really is an
# agentIdentityBlueprintPrincipal.
#
# Two checks that look reasonable are NOT reliable and must not be used:
#   * GET /servicePrincipals/{id}/microsoft.graph.agentIdentityBlueprintPrincipal - Graph answers
#     this happily for a plain servicePrincipal.
#   * ...the same cast followed by /owners - verified live to return 200 for a PLAIN principal too,
#     so a 400 from it is not a dependable negative either.
#
# What IS dependable is asking for the object directly with full OData metadata: Graph then always
# stamps a concrete '@odata.type' on the response - the derived type for a blueprint principal, and
# '#microsoft.graph.servicePrincipal' for a plain one. That is a single-object read-your-write, so
# unlike a collection query it is not subject to index replication lag.
#
# The typed COLLECTION filter is kept only as a fallback, and it is retried: right after a create
# the new object is often missing from the collection index for a few seconds, and treating that
# transient empty result as proof of the wrong type is exactly the false negative this function
# used to produce.
#
# Returns 'Verified', 'Plain', or 'Unknown' when the check itself could not be completed.
function Test-BlueprintPrincipalType {
    param(
        [Parameter(Mandatory)][string] $AppId,
        [Parameter(Mandatory)][string] $ExpectedId,
        [int] $MaxAttempts = 5
    )

    # Primary: the object's own declared type.
    # -RetryOnNotFound absorbs read-your-write lag, but it also means a persistent 404 ends in a
    # throw rather than $null, so the probe is wrapped: an unreadable object must degrade to the
    # fallback, never take the caller's run down.
    $direct = $null
    try {
        $direct = Invoke-Graph -Method GET -Uri "/servicePrincipals/$ExpectedId`?`$select=id" `
            -Accept 'application/json;odata.metadata=full' -MaxAttempts 4 `
            -RetryOnNotFound -TolerateNotFound -TolerateBadRequest -TolerateForbidden
    }
    catch {
        Write-Verbose "Direct type probe for $ExpectedId failed: $($_.Exception.Message)"
    }

    if (Test-HasProperty $direct '@odata.type') {
        switch -Wildcard ([string]$direct.'@odata.type') {
            '*agentIdentityBlueprintPrincipal' { return 'Verified' }
            '*#microsoft.graph.servicePrincipal' { return 'Plain' }
        }
    }

    # Fallback: the typed collection, retried so replication lag is never mistaken for 'Plain'.
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $typed = Invoke-Graph -Method GET `
            -Uri "/servicePrincipals/microsoft.graph.agentIdentityBlueprintPrincipal?`$filter=appId eq '$AppId'&`$select=id" `
            -TolerateBadRequest -TolerateNotFound -TolerateForbidden

        if (-not (Test-HasProperty $typed 'value')) { return 'Unknown' }
        if (@(Get-PropertyValue $typed.value 'id') -contains $ExpectedId) { return 'Verified' }

        if ($attempt -lt $MaxAttempts) {
            Write-Verbose "Blueprint principal $ExpectedId not in the typed collection yet - retry $attempt/$MaxAttempts"
            Start-Sleep -Seconds (2 * $attempt)
        }
    }

    # Every avenue agreed the object is absent from the blueprint-principal collection, and the
    # direct read did not label it either. Report 'Unknown' rather than condemning it: a false
    # 'Plain' tells the user to delete a perfectly good principal.
    return 'Unknown'
}

# Printed when both blueprint-principal create routes are refused. The two routes are authorized
# by different permission families, so the advice has to cover both.
function Get-PrincipalDenialAdvice {
    param([Parameter(Mandatory)][string] $AppId)
    $lines = @(
        "Creating the blueprint principal for appId $AppId was denied on both supported routes."
        ''
        'Route 1 - POST /servicePrincipals/microsoft.graph.agentIdentityBlueprintPrincipal'
        '  Application permissions : AgentIdentityBlueprintPrincipal.Create'
        '                            (or AgentIdentityBlueprintPrincipal.ReadWrite.All)'
        '  Delegated permissions   : the same two scopes.'
        ''
        'Route 2 - POST /servicePrincipals with an @odata.type in the body'
        '  Application permissions : Application.ReadWrite.OwnedBy, but ONLY when the calling app'
        '                            registration is an OWNER of the blueprint application. If it'
        '                            is not, Graph reports "the backing application of the service'
        '                            principal being created must be in the local tenant".'
        '                            Application.ReadWrite.All removes that ownership requirement.'
        '  Delegated permissions   : Application.ReadWrite.All.'
        ''
        'Most likely fix: grant AgentIdentityBlueprintPrincipal.Create to the automation app and'
        'admin-consent it (re-run New-A365AutomationApp.ps1), then re-run this script. The blueprint'
        'application already exists, so pass -DisplayName again and it will be reused, not duplicated.'
    )
    return ($lines -join [Environment]::NewLine)
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

        # Narrow, per-family equivalences established by observation rather than by inference.
        # Live evidence: an app holding AgentIdentityBlueprint.ReadWrite.All - and NOT .Create
        # nor .AddRemoveCreds.All - successfully performed BOTH
        #     POST /applications/microsoft.graph.agentIdentityBlueprint      (blueprint create)
        #     POST /applications/{id}/microsoft.graph.agentIdentityBlueprint/addPassword
        # while this pre-flight was warning that those two roles were missing. The warnings were
        # false alarms that buried the one role that genuinely mattered.
        #
        # This is deliberately NOT generalised to "ReadWrite implies Create", because that is
        # demonstrably false elsewhere in the same API family: AgentIdentity.ReadWrite.All does
        # not authorize POST /servicePrincipals/microsoft.graph.agentIdentity, which fails with a
        # bare "Insufficient privileges" until AgentIdentity.Create.All is granted.
        if ($Required -in @('AgentIdentityBlueprint.Create', 'AgentIdentityBlueprint.AddRemoveCreds.All')) {
            $alternates += 'AgentIdentityBlueprint.ReadWrite.All'
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


# ---------------------------------------------------------------------------
# Azure Key Vault secret storage
# ---------------------------------------------------------------------------
# Key Vault is NOT reachable through Microsoft Graph, and this is not a gap in this
# script - it is architectural. Verified against a live tenant:
#   * every Graph path tried for key vaults or secrets answers 400 (no such segment);
#   * a valid Graph token presented to the vault data plane is rejected with 401,
#     while a vault-audience token on the same request gets 403 - an AUTHORIZATION
#     answer, which proves the 401 was about the audience and not the caller.
# So this block acquires a SECOND token for https://vault.azure.net and calls the
# vault's own REST API. Everything that CAN go through Graph still does.
$script:KeyVaultResource   = 'https://vault.azure.net'
$script:KeyVaultScope      = 'https://vault.azure.net/.default'
$script:KeyVaultApiVersion = '7.4'
$script:KeyVaultResult     = $null

function ConvertFrom-SecureStringValue {
    <#
      SecureString back to plain text, for the one moment a credential has to be placed on
      the wire. Kept as its own function so every such moment is greppable.
    #>
    param([object] $Value)
    if ($null -eq $Value) { return $null }
    if ($Value -isnot [securestring]) { return [string]$Value }
    return [Net.NetworkCredential]::new('', $Value).Password
}

function Resolve-KeyVaultUri {
    <#
      Accepts a bare vault name ("contoso-kv") or a full URI
      ("https://contoso-kv.vault.azure.net/") and returns the normalised base URI.
    #>
    param([Parameter(Mandatory)][string] $NameOrUri)

    $v = $NameOrUri.Trim()
    if ($v -match '^https://') {
        $u = $null
        if (-not [uri]::TryCreate($v, [UriKind]::Absolute, [ref]$u)) {
            throw "-KeyVaultName '$NameOrUri' looks like a URL but is not a valid absolute URI."
        }
        return ('https://' + $u.Host)
    }
    # A vault name is 3-24 chars, alphanumeric and hyphens, and cannot start with a digit
    # or contain consecutive hyphens. Checked here so a typo fails with a clear message
    # instead of an opaque DNS error later.
    if ($v -notmatch '^[A-Za-z][A-Za-z0-9-]{1,22}[A-Za-z0-9]$' -or $v -match '--') {
        throw "-KeyVaultName '$NameOrUri' is not a valid Key Vault name. Use 3-24 characters, letters, digits and single hyphens, starting with a letter; or pass the full https://<vault>.vault.azure.net URI."
    }
    return "https://$v.vault.azure.net"
}

function ConvertTo-KeyVaultSecretName {
    <#
      Key Vault secret names allow only 0-9, a-z, A-Z and '-'. Anything else in a display
      name has to be folded, and an empty or all-invalid result must fail loudly rather
      than silently writing to a name the caller did not intend.
    #>
    param([Parameter(Mandatory)][string] $Candidate)

    $n = [regex]::Replace($Candidate, '[^0-9a-zA-Z-]', '-')
    $n = [regex]::Replace($n, '-{2,}', '-').Trim('-')
    if ($n.Length -gt 127) { $n = $n.Substring(0, 127).Trim('-') }
    if ([string]::IsNullOrWhiteSpace($n)) {
        throw "Cannot derive a Key Vault secret name from '$Candidate'. Pass -KeyVaultSecretName explicitly."
    }
    return $n
}

function New-A365ClientAssertion {
    <#
      Builds the signed JWT that certificate credentials use in place of a client secret.
      Needed because the vault token is a SECOND token: the Graph SDK holds the certificate
      for its own connection, but will not mint a token for another audience.
    #>
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
        [Parameter(Mandatory)][string] $ClientId,
        [Parameter(Mandatory)][string] $TenantId
    )

    if (-not $Certificate.HasPrivateKey) {
        throw 'The certificate has no private key, so it cannot sign a client assertion for the Key Vault token.'
    }
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if (-not $rsa) { throw 'Only RSA certificates can sign a client assertion.' }

    $b64url = {
        param([byte[]] $Bytes)
        [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }
    $now    = [DateTimeOffset]::UtcNow
    $header = [ordered]@{
        alg = 'RS256'
        typ = 'JWT'
        x5t = (& $b64url $Certificate.GetCertHash())
    } | ConvertTo-Json -Compress
    $payload = [ordered]@{
        aud = "https://login.microsoftonline.com/$TenantId/v2.0"
        iss = $ClientId
        sub = $ClientId
        jti = [guid]::NewGuid().ToString()
        nbf = $now.ToUnixTimeSeconds()
        exp = $now.AddMinutes(10).ToUnixTimeSeconds()
    } | ConvertTo-Json -Compress

    $signingInput = (& $b64url ([Text.Encoding]::UTF8.GetBytes($header))) + '.' +
                    (& $b64url ([Text.Encoding]::UTF8.GetBytes($payload)))
    $sig = $rsa.SignData(
        [Text.Encoding]::ASCII.GetBytes($signingInput),
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    return $signingInput + '.' + (& $b64url $sig)
}

function Get-KeyVaultToken {
    <#
      Obtains a token for the Key Vault data plane, using the SAME credential the caller
      gave for Graph wherever that is possible.

      The one case that cannot work is -AccessToken: a Graph access token is issued for the
      Graph audience and the vault rejects it outright, and there is no way to exchange one
      for the other. That mode therefore requires -KeyVaultAccessToken.
    #>
    param(
        [Parameter(Mandatory)][string] $TenantId,
        [string] $AuthMode,
        [string] $ClientId,
        [object] $ClientSecret,
        [System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
        [string] $ManagedIdentityPrincipalId,
        [object] $ExplicitToken
    )

    $tokenUri = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"

    if ($ExplicitToken) {
        $sec = ConvertTo-SecureStringValue -Value $ExplicitToken -Name 'KeyVaultAccessToken'
        return [pscustomobject]@{ Token = (ConvertFrom-SecureStringValue $sec); Source = 'KeyVaultAccessToken' }
    }

    switch ($AuthMode) {
        'ClientSecret' {
            $sec  = ConvertTo-SecureStringValue -Value $ClientSecret -Name 'ClientSecret'
            $body = @{
                client_id     = $ClientId
                client_secret = (ConvertFrom-SecureStringValue $sec)
                scope         = $script:KeyVaultScope
                grant_type    = 'client_credentials'
            }
            # The body carries the client secret, so it is never handed to the logger.
            Write-A365LogGraphRequest -Method 'POST' -Uri $tokenUri -Body '{"grant_type":"client_credentials","scope":"https://vault.azure.net/.default","client_secret":"<redacted:secret>"}'
            $r = Invoke-RestMethod -Method POST -Uri $tokenUri -ContentType 'application/x-www-form-urlencoded' -Body $body -ErrorAction Stop
            Write-A365LogGraphResponse -Method 'POST' -Uri $tokenUri -Response '{"access_token":"<redacted:token>"}' -Status 200
            return [pscustomobject]@{ Token = [string]$r.access_token; Source = 'client_credentials' }
        }
        'Certificate' {
            if (-not $Certificate) {
                throw 'Certificate authentication was used for Graph, but the certificate object is not available to mint a Key Vault token. Pass -KeyVaultAccessToken, or use -CertificatePath / -Certificate so the private key is loaded in-process.'
            }
            $assertion = New-A365ClientAssertion -Certificate $Certificate -ClientId $ClientId -TenantId $TenantId
            $body = @{
                client_id             = $ClientId
                client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
                client_assertion      = $assertion
                scope                 = $script:KeyVaultScope
                grant_type            = 'client_credentials'
            }
            Write-A365LogGraphRequest -Method 'POST' -Uri $tokenUri -Body '{"grant_type":"client_credentials","scope":"https://vault.azure.net/.default","client_assertion":"<redacted:secret>"}'
            $r = Invoke-RestMethod -Method POST -Uri $tokenUri -ContentType 'application/x-www-form-urlencoded' -Body $body -ErrorAction Stop
            Write-A365LogGraphResponse -Method 'POST' -Uri $tokenUri -Response '{"access_token":"<redacted:token>"}' -Status 200
            return [pscustomobject]@{ Token = [string]$r.access_token; Source = 'client_assertion' }
        }
        'ManagedIdentity' {
            # IMDS, and the App Service / Functions variant, which use different headers.
            $q = "api-version=2018-02-01&resource=$([uri]::EscapeDataString($script:KeyVaultResource))"
            if ($ManagedIdentityPrincipalId) { $q += "&principal_id=$([uri]::EscapeDataString($ManagedIdentityPrincipalId))" }
            if ($env:IDENTITY_ENDPOINT -and $env:IDENTITY_HEADER) {
                $uri = "$($env:IDENTITY_ENDPOINT)?api-version=2019-08-01&resource=$([uri]::EscapeDataString($script:KeyVaultResource))"
                if ($ManagedIdentityPrincipalId) { $uri += "&principal_id=$([uri]::EscapeDataString($ManagedIdentityPrincipalId))" }
                $r = Invoke-RestMethod -Uri $uri -Headers @{ 'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER } -ErrorAction Stop
            }
            else {
                $r = Invoke-RestMethod -Uri "http://169.254.169.254/metadata/identity/oauth2/token?$q" `
                        -Headers @{ Metadata = 'true' } -TimeoutSec 10 -ErrorAction Stop
            }
            return [pscustomobject]@{ Token = [string]$r.access_token; Source = 'managed_identity' }
        }
        'Interactive' {
            # Connect-MgGraph yields a Graph token only. Rather than force a second sign-in
            # prompt mid-run, reuse a signed-in Azure session if one is present - the same
            # opt-in fallback this suite already uses for the Agent 365 Tools audience.
            $az = Get-Command az -ErrorAction SilentlyContinue
            if ($az) {
                try {
                    $raw = & az account get-access-token --scope $script:KeyVaultScope --tenant $TenantId -o json 2>$null
                    if ($LASTEXITCODE -eq 0 -and $raw) {
                        $p = ($raw | ConvertFrom-Json)
                        if ($p.accessToken) { return [pscustomobject]@{ Token = [string]$p.accessToken; Source = 'azure-cli' } }
                    }
                }
                catch { Write-Verbose "az token acquisition failed: $($_.Exception.Message)" }
            }
            if (Get-Module -ListAvailable -Name Az.Accounts) {
                try {
                    Import-Module Az.Accounts -ErrorAction Stop
                    if (Get-AzContext -ErrorAction SilentlyContinue) {
                        $t = Get-AzAccessToken -ResourceUrl $script:KeyVaultResource -ErrorAction Stop
                        $plain = if ($t.Token -is [securestring]) { ConvertFrom-SecureStringValue $t.Token } else { [string]$t.Token }
                        if ($plain) { return [pscustomobject]@{ Token = $plain; Source = 'az-powershell' } }
                    }
                }
                catch { Write-Verbose "Az.Accounts token acquisition failed: $($_.Exception.Message)" }
            }
            throw 'Interactive Graph sign-in cannot mint a Key Vault token: Connect-MgGraph issues Graph-audience tokens only. Sign in to Azure first ("az login" or "Connect-AzAccount"), or pass -KeyVaultAccessToken.'
        }
        'AccessToken' {
            throw '-AccessToken supplies a Microsoft Graph token, which the Key Vault data plane rejects (verified: HTTP 401). A token is audience-bound and cannot be exchanged. Pass -KeyVaultAccessToken with a token for https://vault.azure.net, or authenticate with -ClientSecret / -Certificate / -UseManagedIdentity so one can be obtained for you.'
        }
        default {
            throw "Cannot obtain a Key Vault token for authentication mode '$AuthMode'. Pass -KeyVaultAccessToken."
        }
    }
}

function Save-A365SecretToKeyVault {
    <#
      PUTs the secret to the vault data plane and reads it back. The read-back is not
      ceremony: a write that is accepted but not persisted has bitten this suite before on
      other APIs, and a client secret that only appears to be stored is worse than one that
      obviously failed, because the caller stops holding the only copy.
    #>
    param(
        [Parameter(Mandatory)][string] $VaultUri,
        [Parameter(Mandatory)][string] $SecretName,
        [Parameter(Mandatory)][object] $SecretValue,
        [Parameter(Mandatory)][string] $Token,
        [string]   $ContentType,
        [hashtable] $Tags,
        [object]   $ExpiresOn
    )

    $plain = if ($SecretValue -is [securestring]) { ConvertFrom-SecureStringValue $SecretValue } else { [string]$SecretValue }
    if ([string]::IsNullOrEmpty($plain)) { throw 'Refusing to write an empty value to Key Vault.' }

    $uri  = "$VaultUri/secrets/$([uri]::EscapeDataString($SecretName))?api-version=$script:KeyVaultApiVersion"
    $body = [ordered]@{ value = $plain }
    if ($ContentType) { $body.contentType = $ContentType }
    if ($Tags -and $Tags.Count -gt 0) { $body.tags = $Tags }
    # PowerShell unwraps a [Nullable[T]] parameter to T, so there is no .Value to read here.
    $expOffset = $null
    if ($ExpiresOn -is [DateTimeOffset]) { $expOffset = [DateTimeOffset]$ExpiresOn }
    elseif ($ExpiresOn -is [datetime])   { $expOffset = [DateTimeOffset]([datetime]$ExpiresOn) }
    if ($null -ne $expOffset) { $body.attributes = [ordered]@{ exp = $expOffset.ToUnixTimeSeconds() } }

    # The body's "value" IS the secret, and "value" is far too generic to add to the
    # redaction list - every Graph collection response is {"value":[...]}. So the real body
    # is never passed to the logger; a redacted stand-in is logged instead.
    $logBody = [ordered]@{}
    foreach ($k in $body.Keys) { $logBody[$k] = $body[$k] }
    $logBody['value'] = '<redacted:secret>'
    $headers = @{ Authorization = "Bearer $Token"; 'Content-Type' = 'application/json' }

    Write-A365LogGraphRequest -Method 'PUT' -Uri $uri -Body ($logBody | ConvertTo-Json -Depth 6 -Compress)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = Invoke-RestMethod -Method PUT -Uri $uri -Headers $headers `
                    -Body ($body | ConvertTo-Json -Depth 6) -ErrorAction Stop
        $sw.Stop()
    }
    catch {
        $sw.Stop()
        $status = 0
        if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
            try { $status = [int]$_.Exception.Response.StatusCode } catch { $status = 0 }
        }
        Write-A365LogGraphResponse -Method 'PUT' -Uri $uri -Status $status -DurationMs $sw.ElapsedMilliseconds `
            -AsFailure -ErrorText (Protect-LogText $_.Exception.Message)
        throw (New-Object System.Management.Automation.RuntimeException(
            "Key Vault write failed with HTTP $status. $($_.Exception.Message)"))
    }

    # Read back the METADATA only. Asking for the value would pull the secret into the
    # response, and with it into any transcript, for no extra proof.
    $verified = $false
    $version  = $null
    if ($resp -and $resp.id) {
        $version = ($resp.id -split '/')[-1]
        try {
            $check = Invoke-RestMethod -Method GET -Headers @{ Authorization = "Bearer $Token" } `
                        -Uri "$VaultUri/secrets/$([uri]::EscapeDataString($SecretName))/versions?api-version=$script:KeyVaultApiVersion" -ErrorAction Stop
            $verified = @($check.value | Where-Object { ($_.id -split '/')[-1] -eq $version }).Count -gt 0
        }
        catch { Write-Verbose "Key Vault read-back failed: $($_.Exception.Message)" }
    }
    Write-A365LogGraphResponse -Method 'PUT' -Uri $uri -Status 200 -DurationMs $sw.ElapsedMilliseconds `
        -Response ("{`"id`":`"$($resp.id)`",`"verified`":$($verified.ToString().ToLower())}")

    return [pscustomobject]@{
        VaultUri   = $VaultUri
        SecretName = $SecretName
        Version    = $version
        Id         = [string]$resp.id
        Verified   = $verified
        ExpiresOn  = $(if ($null -ne $expOffset) { $expOffset.ToString('o') } else { $null })
    }
}

function Write-KeyVaultActionRequired {
    <#
      Turns a vault failure into the specific thing an operator has to do. The failure modes
      are distinguishable and the remedies are completely different, so they are named
      separately rather than reported as one generic error.
    #>
    param(
        [Parameter(Mandatory)][string] $VaultUri,
        [string] $SecretName,
        [string] $Message,
        [int]    $Status,
        [string] $CallerDisplay
    )

    Write-Host ''
    Write-Host 'ACTION REQUIRED - the secret was NOT saved to Key Vault.' -ForegroundColor Red
    Write-Host ("  vault  : {0}" -f $VaultUri) -ForegroundColor Gray
    if ($SecretName) { Write-Host ("  secret : {0}" -f $SecretName) -ForegroundColor Gray }
    if ($Message)    { Write-Host ("  error  : {0}" -f $Message) -ForegroundColor DarkGray }

    switch ($Status) {
        401 {
            Write-Host '  The vault rejected the token. A Microsoft Graph token does not work here -' -ForegroundColor Yellow
            Write-Host '  Key Vault is a separate audience (https://vault.azure.net).' -ForegroundColor Yellow
            Write-Host '  Pass -KeyVaultAccessToken with a vault-audience token, or authenticate with' -ForegroundColor Gray
            Write-Host '  -ClientSecret / -Certificate / -UseManagedIdentity so one can be obtained.' -ForegroundColor Gray
        }
        403 {
            $who = if ($CallerDisplay) { $CallerDisplay } else { '<the principal this script authenticated as>' }
            Write-Host '  Authenticated, but not authorized on the vault DATA plane.' -ForegroundColor Yellow
            Write-Host '  Owner and Contributor are NOT enough: they grant management-plane rights only' -ForegroundColor Yellow
            Write-Host '  and carry no dataActions, so they cannot read or write secrets.' -ForegroundColor Yellow
            Write-Host '  Grant the data-plane role (RBAC vaults):' -ForegroundColor Gray
            Write-Host ("    az role assignment create --role 'Key Vault Secrets Officer' ``") -ForegroundColor Cyan
            Write-Host ("        --assignee-object-id {0} --assignee-principal-type ServicePrincipal ``" -f $who) -ForegroundColor Cyan
            Write-Host ("        --scope <vault resource id>") -ForegroundColor Cyan
            Write-Host '  If the vault still uses access policies instead of RBAC, grant secret set/get there.' -ForegroundColor Gray
        }
        404 {
            Write-Host '  The vault or the path was not found. Check -KeyVaultName, and that the vault' -ForegroundColor Yellow
            Write-Host '  exists in this tenant and is not soft-deleted.' -ForegroundColor Gray
        }
        default {
            Write-Host '  The secret is still shown above (or in the report) - capture it now; Entra will' -ForegroundColor Yellow
            Write-Host '  not display it again.' -ForegroundColor Yellow
        }
    }
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

    # The Key Vault token is a SECOND token and has to be minted from the same credential.
    # For -CertificateThumbprint the certificate was never loaded, so resolve it from the
    # store; a miss is not fatal here because only a Key Vault write needs it.
    $ctxCertificate = if ($connect.ContainsKey('Certificate')) { $connect.Certificate }
                      elseif ($CertificateThumbprint) {
                          @(
                              "Cert:\CurrentUser\My\$CertificateThumbprint",
                              "Cert:\LocalMachine\My\$CertificateThumbprint"
                          ) | ForEach-Object { Get-Item -LiteralPath $_ -ErrorAction SilentlyContinue } |
                            Select-Object -First 1
                      }
                      else { $null }

    [pscustomobject]@{
        Mode            = $mode
        Certificate     = $ctxCertificate
        IsAppOnly       = $isAppOnly
        AuthType        = $authType
        TenantId        = $ctxTenant
        ClientId        = $ctxClient
        Account         = $ctxAccount
        MissingAppRoles = $missingRoles
    }
}

# ---------------------------------------------------------------------------
# Step 1 - connect
# ---------------------------------------------------------------------------
Write-Step 1 'Connecting to Microsoft Graph'

# Delegated scopes are only sent with -Interactive. User.Read and User.ReadBasic.All have no
# application equivalent, so app-only runs use User.Read.All to resolve sponsors and owners.
$delegatedScopes = [System.Collections.Generic.List[string]]::new()
$delegatedScopes.AddRange([string[]]@(
    'AgentIdentityBlueprint.Create',
    'AgentIdentityBlueprint.ReadWrite.All',
    'AgentIdentityBlueprint.Read.All',
    'AgentIdentityBlueprint.AddRemoveCreds.All',
    'AgentIdentityBlueprintPrincipal.Create',
    'AgentIdentityBlueprintPrincipal.Read.All',
    'Application.Read.All',
    'User.Read',
    'User.ReadBasic.All',
    'Group.Read.All'
))

$appRoles = [System.Collections.Generic.List[string]]::new()
$appRoles.AddRange([string[]]@(
    'AgentIdentityBlueprint.Create',              # POST /applications/microsoft.graph.agentIdentityBlueprint
    'AgentIdentityBlueprint.Read.All',            # idempotency probe
    'AgentIdentityBlueprint.ReadWrite.All',       # owners/$ref, sponsors/$ref, PATCH, inheritablePermissions
    'AgentIdentityBlueprint.AddRemoveCreds.All',  # addPassword / federatedIdentityCredentials
    'AgentIdentityBlueprintPrincipal.Create',     # POST /servicePrincipals/...BlueprintPrincipal
    'AgentIdentityBlueprintPrincipal.Read.All',   # read the principal back
    'Application.Read.All',                       # resolve resource apps for permission GUIDs
    'User.Read.All',                              # resolve sponsor / owner users
    'Group.Read.All'                              # resolve sponsor groups
))

if ($GrantAdminConsent) {
    $consentPerms = [string[]]@('DelegatedPermissionGrant.ReadWrite.All', 'AppRoleAssignment.ReadWrite.All')
    $delegatedScopes.AddRange($consentPerms)
    $appRoles.AddRange($consentPerms)
}

# Owner writes are NOT covered by the AgentIdentityBlueprint.* roles, and the application and the
# principal need different permissions: the application takes Application.ReadWrite.All (or
# .OwnedBy) plus Directory.Read.All, while the principal takes
# AgentIdentityBlueprintPrincipal.ReadWrite.All - verified live, Application.ReadWrite.All alone is
# refused there. Only ask for them when owners were actually requested, so the common no-owner run
# keeps the narrow permission set.
if ($Owner -and $Owner.Count -gt 0) {
    $ownerPerms = [string[]]@(
        'Application.ReadWrite.All',                      # owners on the blueprint application
        'Directory.Read.All',
        'AgentIdentityBlueprintPrincipal.ReadWrite.All'   # owners on the blueprint principal
    )
    $delegatedScopes.AddRange($ownerPerms)
    $appRoles.AddRange($ownerPerms)
}

$ctx = Connect-GraphSession -TenantId $TenantId `
    -ClientId $ClientId -ClientSecret $ClientSecret `
    -CertificateThumbprint $CertificateThumbprint -Certificate $Certificate `
    -CertificatePath $CertificatePath -CertificatePassword $CertificatePassword `
    -UseManagedIdentity:$UseManagedIdentity -AccessToken $AccessToken `
    -Interactive:$Interactive -SkipPermissionCheck:$SkipPermissionCheck `
    -DelegatedScope $delegatedScopes -RequiredAppRole $appRoles

# ---------------------------------------------------------------------------
# Step 2 - resolve sponsors / owners
# ---------------------------------------------------------------------------
Write-Step 2 'Resolving sponsors and owners'

$sponsorPrincipals = @()
foreach ($s in @($Sponsor | Where-Object { $_ })) {
    $resolvedSponsor = Resolve-DirectoryPrincipal -Identifier $s
    $sponsorPrincipals += $resolvedSponsor
    Write-Host "  Sponsor : $s -> $($resolvedSponsor.Id) [$($resolvedSponsor.Segment)]"
}
# De-duplicate so a UPN and its object ID passed together do not bind the same principal twice.
# Group-Object would sort, which would make the "first" sponsor - and so the back-compat
# sponsorId - depend on GUID ordering rather than on what the caller asked for.
$seenSponsor = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$sponsorPrincipals = @($sponsorPrincipals | Where-Object { $seenSponsor.Add($_.Id) })
if ($sponsorPrincipals.Count -eq 0 -and -not $Update) {
    throw 'At least one valid sponsor is required by the create API.'
}
$sponsorIds = @(Get-PropertyValue $sponsorPrincipals 'Id')

$ownerPrincipals = @()
foreach ($o in @($Owner | Where-Object { $_ })) {
    $resolved = Resolve-OwnerPrincipal -Identifier $o
    $ownerPrincipals += $resolved
    Write-Host "  Owner   : $o -> $($resolved.Id) [$($resolved.Type)]"
}
# De-duplicate so a UPN and its object ID passed together do not produce two POSTs.
$ownerPrincipals = @($ownerPrincipals | Group-Object -Property Id | ForEach-Object { $_.Group[0] })
$ownerIds = @(Get-PropertyValue $ownerPrincipals 'Id')

if ($ownerPrincipals.Count -eq 0) {
    Write-Host '  Owner   : none specified; Entra ID assigns the calling principal as owner on create.' -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Step 3 - resolve permission names to GUIDs on each resource app
# ---------------------------------------------------------------------------
Write-Step 3 'Resolving permission names to GUIDs'

$resolvedResources = @()

foreach ($req in $RequiredPermission) {
    $resourceAppId = [string]$req.ResourceAppId
    if ($resourceAppId -notmatch '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') {
        throw "RequiredPermission.ResourceAppId '$resourceAppId' is not a valid GUID (Graph returns 400 otherwise)."
    }

    $sp = Invoke-Graph -Method GET `
        -Uri "/servicePrincipals(appId='$resourceAppId')?`$select=id,appId,displayName,appRoles,oauth2PermissionScopes" `
        -TolerateNotFound

    if (-not $sp) {
        Write-Warning "  Resource app $resourceAppId has no service principal in this tenant. Skipping its permissions."
        continue
    }

    $wantScopes = @()
    if ($req.ContainsKey('DelegatedScopes')) { $wantScopes = @($req.DelegatedScopes | Where-Object { $_ }) }
    $wantRoles = @()
    if ($req.ContainsKey('AppRoles')) { $wantRoles = @($req.AppRoles | Where-Object { $_ }) }

    $spScopes = @()
    if (Test-HasProperty $sp 'oauth2PermissionScopes') { $spScopes = @($sp.oauth2PermissionScopes) }
    $spRoles = @()
    if (Test-HasProperty $sp 'appRoles') { $spRoles = @($sp.appRoles) }

    $scopeEntries = @()
    foreach ($name in $wantScopes) {
        $match = @($spScopes | Where-Object { $_.value -eq $name })
        if ($match.Count -eq 0) {
            Write-Warning "  Delegated scope '$name' not found on '$($sp.displayName)'. Skipping."
            continue
        }
        $scopeEntries += [pscustomobject]@{ Name = $name; Id = $match[0].id }
    }

    $roleEntries = @()
    foreach ($name in $wantRoles) {
        $match = @($spRoles | Where-Object { $_.value -eq $name -and @($_.allowedMemberTypes) -contains 'Application' })
        if ($match.Count -eq 0) {
            Write-Warning "  App role '$name' not found on '$($sp.displayName)'. Skipping."
            continue
        }
        $roleEntries += [pscustomobject]@{ Name = $name; Id = $match[0].id }
    }

    $resolvedResources += [pscustomobject]@{
        ResourceAppId    = $resourceAppId
        ResourceSpId     = $sp.id
        ResourceName     = $sp.displayName
        DelegatedScopes  = $scopeEntries
        AppRoles         = $roleEntries
    }

    Write-Host ("  {0,-40} scopes: {1,-2}  roles: {2}" -f $sp.displayName, $scopeEntries.Count, $roleEntries.Count)
}

$requiredResourceAccess = @()
foreach ($r in $resolvedResources) {
    $access = @()
    foreach ($s in $r.DelegatedScopes) { $access += @{ id = $s.Id; type = 'Scope' } }
    foreach ($a in $r.AppRoles)        { $access += @{ id = $a.Id; type = 'Role'  } }
    if ($access.Count -gt 0) {
        $requiredResourceAccess += @{ resourceAppId = $r.ResourceAppId; resourceAccess = $access }
    }
}

# ---------------------------------------------------------------------------
# Step 4 - create the agent identity blueprint
# ---------------------------------------------------------------------------
Write-Step 4 'Creating the agent identity blueprint'

$blueprint = $null
$reusedExisting = $false

if ($Update) {
    # Update mode addresses the blueprint by id. Both ids are accepted because people reach for
    # whichever one they have to hand, and the two are different values on a real blueprint.
    if (-not [guid]::TryParse($BlueprintId, [ref]([guid]::Empty))) {
        throw "-BlueprintId '$BlueprintId' is not a GUID. Pass the blueprint's object id or its appId."
    }

    $bpSelect  = 'id,appId,displayName,description'
    $blueprint = Invoke-Graph -Method GET `
        -Uri "/applications/$BlueprintId/microsoft.graph.agentIdentityBlueprint?`$select=$bpSelect" `
        -TolerateNotFound -TolerateBadRequest

    if (-not $blueprint) {
        $byAppId = Invoke-Graph -Method GET `
            -Uri "/applications/microsoft.graph.agentIdentityBlueprint?`$filter=$([uri]::EscapeDataString("appId eq '$BlueprintId'"))&`$select=$bpSelect" `
            -TolerateNotFound
        if ($byAppId -and (Test-HasProperty $byAppId 'value') -and @($byAppId.value).Count -gt 0) {
            $blueprint = @($byAppId.value)[0]
        }
    }

    if (-not $blueprint) {
        # Separate "no such object" from "wrong kind of object": both answer 404 on the type-cast
        # URI, and telling someone their id does not exist when it does sends them hunting.
        $plainApp = Invoke-Graph -Method GET -Uri "/applications/$BlueprintId`?`$select=id,displayName" -TolerateNotFound
        if ($plainApp) {
            throw "Application '$BlueprintId' ($($plainApp.displayName)) exists but is not an agent identity blueprint, so -Update has nothing to change on it."
        }
        throw "No agent identity blueprint with id '$BlueprintId' exists in tenant $TenantId."
    }

    $reusedExisting = $true
    Write-Host "  Updating existing blueprint '$($blueprint.displayName)'" -ForegroundColor Yellow
}
else {
    $blueprintFilter = "displayName eq '$($DisplayName -replace "'", "''")'"
    $existing = Invoke-Graph -Method GET `
        -Uri "/applications/microsoft.graph.agentIdentityBlueprint?`$filter=$([uri]::EscapeDataString($blueprintFilter))&`$select=id,appId,displayName,description" `
        -TolerateNotFound

    if ($existing -and (Test-HasProperty $existing 'value') -and @($existing.value).Count -gt 0) {
        $blueprint = @($existing.value)[0]
        $reusedExisting = $true
        Write-Host "  Reusing existing blueprint '$($blueprint.displayName)'" -ForegroundColor Yellow
        Write-Host '  Sponsors are set at creation, so any requested sponsor missing from it is added below.' -ForegroundColor Gray
    }
    elseif ($PSCmdlet.ShouldProcess($DisplayName, 'POST /applications/microsoft.graph.agentIdentityBlueprint')) {
        # The type-cast URI IS the dedicated Create agentIdentityBlueprint API, which is authorized by
        # AgentIdentityBlueprint.Create. Do NOT add an '@odata.type' to this body: that annotation
        # belongs to the generic POST /applications route, and sending it here makes Graph authorize
        # the request as a generic application create (Application.ReadWrite.*) instead.
        $body = [ordered]@{
            displayName           = $DisplayName
            signInAudience        = 'AzureADMyOrg'
            'sponsors@odata.bind' = @($sponsorPrincipals | ForEach-Object { "$script:GraphRoot/$($_.Segment)/$($_.Id)" })
        }
        if ($Description) { $body.description = $Description }
        # NOTE: owners@odata.bind is not supported on this create call - owners are assigned below
        # through the dedicated owners/$ref collection.

        $blueprint = Invoke-Graph -Method POST -Uri '/applications/microsoft.graph.agentIdentityBlueprint' -Body $body
    }
    else {
        Write-Host '  [WhatIf] Blueprint creation skipped; remaining steps cannot run.' -ForegroundColor Yellow
        return
    }
}

$blueprintObjectId = $blueprint.id
$blueprintAppId    = $blueprint.appId
Write-Host "  objectId : $blueprintObjectId" -ForegroundColor Green
Write-Host "  appId    : $blueprintAppId"    -ForegroundColor Green

# Every subsequent write goes through the agentIdentityBlueprint type cast rather than the plain
# /applications/{id} URI. The docs define the blueprint operations on the cast form, and the narrow
# AgentIdentityBlueprint.* app roles are scoped to it - the untyped URI would instead demand
# tenant-wide Application.ReadWrite.All.
$blueprintUri = "/applications/$blueprintObjectId/microsoft.graph.agentIdentityBlueprint"

# ---------------------------------------------------------------------------
# displayName and description reconciliation
#
# Both ride the create body only, so on a run that REUSED a blueprint - and on every -Update run -
# they were accepted on the command line and silently dropped. The comparison is case-SENSITIVE
# because changing only the casing of a name is a real change the directory will accept.
# ---------------------------------------------------------------------------
$blueprintPropertyWrites = @()
if ($reusedExisting) {
    $currentName = ''
    if (Test-HasProperty $blueprint 'displayName') { $currentName = [string]$blueprint.displayName }
    $currentDescription = ''
    if (Test-HasProperty $blueprint 'description') { $currentDescription = [string]$blueprint.description }

    $bpPatch = [ordered]@{}
    if ($PSBoundParameters.ContainsKey('DisplayName') -and $DisplayName -cne $currentName) {
        $bpPatch.displayName = $DisplayName
    }
    if ($PSBoundParameters.ContainsKey('Description') -and $Description -cne $currentDescription) {
        $bpPatch.description = $Description
    }

    if ($bpPatch.Count -gt 0) {
        $bpPatchLabel = ($bpPatch.Keys | ForEach-Object { $_ }) -join ', '
        if ($PSCmdlet.ShouldProcess($currentName, "PATCH $blueprintUri ($bpPatchLabel)")) {
            try {
                Invoke-Graph -Method PATCH -Uri $blueprintUri -Body $bpPatch | Out-Null
                $blueprintPropertyWrites = @($bpPatch.Keys | ForEach-Object { $_ })
                foreach ($k in $blueprintPropertyWrites) {
                    Write-Host "  Updated $k -> '$($bpPatch[$k])'" -ForegroundColor Green
                }
                if ($bpPatch.Contains('displayName')) { $blueprint.displayName = $DisplayName }
            }
            catch {
                # A blueprint that is otherwise correct must not be failed over a label, but the
                # run must not claim the change either.
                Write-Warning "  Could not update $bpPatchLabel on the blueprint: $($_.Exception.Message)"
            }
        }
    }
}

# Downstream ShouldProcess targets and messages all name the blueprint. Under -Update without
# -DisplayName the parameter is empty and every one of them would render blank.
if ($Update -and -not $PSBoundParameters.ContainsKey('DisplayName')) {
    $DisplayName = [string]$blueprint.displayName
}

# Owners are a separate collection, so this also repairs owners on an idempotent re-run.
$assignedOwnerIds = @(Add-OwnerAssignment `
    -BaseUri $blueprintUri `
    -Owner $ownerPrincipals -Label 'Blueprint')

# Sponsors are set in the create body, so a re-run against an EXISTING blueprint would otherwise
# silently ignore every requested sponsor. Verified live that POST sponsors/$ref works delegated
# on a blueprint and the added sponsor persists, so reconcile them the same way owners are.
$assignedSponsorIds = @($sponsorIds)
if ($reusedExisting) {
    $existingSponsors = Invoke-Graph -Method GET -Uri "$blueprintUri/sponsors?`$select=id" -TolerateNotFound -TolerateForbidden
    $existingSponsorIds = @()
    if (Test-HasProperty $existingSponsors 'value') { $existingSponsorIds = @(Get-PropertyValue $existingSponsors.value 'id') }

    $missingSponsors = @($sponsorPrincipals | Where-Object { $existingSponsorIds -notcontains $_.Id })
    if ($missingSponsors.Count -eq 0) {
        Write-Host '  All requested sponsors already assigned.' -ForegroundColor Green
    }
    else {
        foreach ($s in $missingSponsors) {
            if (-not $PSCmdlet.ShouldProcess($s.Display, "POST $blueprintUri/sponsors/`$ref")) { continue }
            try {
                Invoke-Graph -Method POST -Uri "$blueprintUri/sponsors/`$ref" `
                    -Body @{ '@odata.id' = "$script:GraphRoot/$($s.Segment)/$($s.Id)" } `
                    -TolerateConflict | Out-Null
                Write-Host "  Sponsor added: $($s.Display)" -ForegroundColor Green
            }
            catch {
                # One sponsor failing must not abandon the rest, and it must not kill an otherwise
                # working blueprint.
                Write-Warning "  Could not add sponsor '$($s.Display)': $($_.Exception.Message)"
                $assignedSponsorIds = @($assignedSponsorIds | Where-Object { $_ -ne $s.Id })
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Step 5 - credentials
# ---------------------------------------------------------------------------
Write-Step 5 'Configuring blueprint credentials'

$secretValue  = $null
$secretDetail = $null

if ($ManagedIdentityPrincipalId) {
    $ficList = Invoke-Graph -Method GET -Uri "$blueprintUri/federatedIdentityCredentials" -TolerateNotFound
    $already = $false
    if (Test-HasProperty $ficList 'value') {
        $already = @(Get-PropertyValue $ficList.value 'name') -contains $FederatedCredentialName
    }

    if ($already) {
        Write-Host "  FIC '$FederatedCredentialName' already present." -ForegroundColor Yellow
    }
    elseif ($PSCmdlet.ShouldProcess($FederatedCredentialName, "POST $blueprintUri/federatedIdentityCredentials")) {
        $fic = [ordered]@{
            name      = $FederatedCredentialName
            issuer    = "https://login.microsoftonline.com/$($ctx.TenantId)/v2.0"
            subject   = $ManagedIdentityPrincipalId
            audiences = @('api://AzureADTokenExchange')
        }
        Invoke-Graph -Method POST -Uri "$blueprintUri/federatedIdentityCredentials" -Body $fic -RetryOnNotFound | Out-Null
        Write-Host "  Federated identity credential created (managed identity $ManagedIdentityPrincipalId)." -ForegroundColor Green
    }
}

if ($NewClientSecret) {
    if ($PSCmdlet.ShouldProcess($DisplayName, "POST $blueprintUri/addPassword")) {
        $pw = [ordered]@{
            passwordCredential = [ordered]@{
                displayName = "a365-dev-secret"
                endDateTime = (Get-Date).ToUniversalTime().AddDays($ClientSecretLifetimeDays).ToString('yyyy-MM-ddTHH:mm:ssZ')
            }
        }
        $result = Invoke-Graph -Method POST -Uri "$blueprintUri/addPassword" -Body $pw -RetryOnNotFound
        $secretValue = $result.secretText
        # Keep the non-secret half of the credential (which key, when it dies) so a run report can
        # record what was created without necessarily recording the secret itself.
        $secretDetail = [ordered]@{
            keyId         = if (Test-HasProperty $result 'keyId')         { $result.keyId }         else { $null }
            displayName   = if (Test-HasProperty $result 'displayName')   { $result.displayName }   else { $null }
            hint          = if (Test-HasProperty $result 'hint')          { $result.hint }          else { $null }
            startDateTime = if (Test-HasProperty $result 'startDateTime') { $result.startDateTime } else { $null }
            endDateTime   = if (Test-HasProperty $result 'endDateTime')   { $result.endDateTime }   else { $null }
            lifetimeDays  = $ClientSecretLifetimeDays
        }
        Write-Warning '  Client secrets are for local development only - use a managed identity FIC in production.'

        if ($KeyVaultName) {
            # The secret is still printed on failure: a secret that exists in Entra but was
            # not captured anywhere is unrecoverable, so the console copy is the fallback.
            $kvName = if ($KeyVaultSecretName) { $KeyVaultSecretName } else { ConvertTo-KeyVaultSecretName "$DisplayName-clientsecret" }
            $kvUri  = Resolve-KeyVaultUri $KeyVaultName
            Write-Host "  Saving the secret to Key Vault $kvUri (secret '$kvName')..." -ForegroundColor Cyan
            try {
                $kvToken = Get-KeyVaultToken -TenantId $ctx.TenantId -AuthMode $ctx.Mode -ClientId $ClientId `
                    -ClientSecret $ClientSecret -Certificate $ctx.Certificate `
                    -ManagedIdentityPrincipalId $ManagedIdentityPrincipalId -ExplicitToken $KeyVaultAccessToken
                # Expire the vault entry with the credential itself, so a stale secret is not
                # left looking current after Entra has already stopped accepting it.
                $kvExp = [DateTimeOffset]::UtcNow.AddDays($ClientSecretLifetimeDays)
                $script:KeyVaultResult = Save-A365SecretToKeyVault -VaultUri $kvUri -SecretName $kvName `
                    -SecretValue $secretValue -Token $kvToken.Token -ContentType 'application/x-a365-client-secret' `
                    -ExpiresOn $kvExp -Tags @{ a365Object = 'blueprint'; a365AppId = "$blueprintAppId"; a365DisplayName = "$DisplayName" }
                Write-Host ("  Saved. version {0}{1}" -f $script:KeyVaultResult.Version, $(if ($script:KeyVaultResult.Verified) { ', read back OK' } else { ' (NOT confirmed on read-back)' })) -ForegroundColor Green
                Write-Host ("  Retrieve with: az keyvault secret show --vault-name {0} --name {1} --query value -o tsv" -f ($kvUri -replace '^https://', '' -replace '\.vault\.azure\.net$', ''), $kvName) -ForegroundColor Gray
            }
            catch {
                $st = 0
                if ($_.Exception.Message -match 'HTTP (\d{3})') { $st = [int]$Matches[1] }
                $script:KeyVaultResult = [pscustomobject]@{ VaultUri = $kvUri; SecretName = $kvName; Saved = $false; Error = $_.Exception.Message }
                Write-KeyVaultActionRequired -VaultUri $kvUri -SecretName $kvName -Message $_.Exception.Message `
                    -Status $st -CallerDisplay $ctx.ClientId
                Write-Host ''
                Write-Host "  Secret (shown once): $secretValue" -ForegroundColor Magenta
            }
        }
        else {
            Write-Host "  Secret (shown once): $secretValue" -ForegroundColor Magenta
            Write-Host '  Tip: -KeyVaultName <vault> saves it to Azure Key Vault instead of printing it.' -ForegroundColor DarkGray
        }
    }
}

if (-not $ManagedIdentityPrincipalId -and -not $NewClientSecret) {
    Write-Warning '  No credential configured. Add a managed identity FIC or certificate before requesting tokens.'
}

# ---------------------------------------------------------------------------
# Step 6 - identifier URI, exposed scope, required resource access
# ---------------------------------------------------------------------------
Write-Step 6 'Configuring identifier URI, exposed scope and required resource access'

$current = Invoke-Graph -Method GET `
    -Uri "$blueprintUri`?`$select=id,appId,identifierUris,api,requiredResourceAccess" `
    -RetryOnNotFound

$patch = [ordered]@{}

$currentUris = @()
if (Test-HasProperty $current 'identifierUris') { $currentUris = @($current.identifierUris) }

if ($currentUris -notcontains "api://$blueprintAppId") {
    $patch.identifierUris = @("api://$blueprintAppId")
}

if (-not [string]::IsNullOrWhiteSpace($ExposedScopeValue)) {
    $existingScopes = @()
    if ((Test-HasProperty $current 'api') -and (Test-HasProperty $current.api 'oauth2PermissionScopes') -and $current.api.oauth2PermissionScopes) {
        $existingScopes = @($current.api.oauth2PermissionScopes)
    }

    if (@($existingScopes | Where-Object { $_.value -eq $ExposedScopeValue }).Count -eq 0) {
        $newScope = [ordered]@{
            id                      = [guid]::NewGuid().ToString()
            value                   = $ExposedScopeValue
            type                    = 'User'
            isEnabled               = $true
            adminConsentDisplayName = 'Access agent'
            adminConsentDescription = 'Allow the application to access the agent on behalf of the signed-in user.'
            userConsentDisplayName  = 'Access agent'
            userConsentDescription  = 'Allow the application to access the agent on your behalf.'
        }
        $keep = $existingScopes | ForEach-Object {
            [ordered]@{
                id                      = $_.id
                value                   = $_.value
                type                    = $_.type
                isEnabled               = $_.isEnabled
                adminConsentDisplayName = $_.adminConsentDisplayName
                adminConsentDescription = $_.adminConsentDescription
            }
        }
        $patch.api = @{ oauth2PermissionScopes = @(@($keep) + $newScope) }
    }
}

if ($requiredResourceAccess.Count -gt 0) {
    # Only patch when the desired access set actually differs from what is already on the app.
    $currentAccessKeys = @()
    if (Test-HasProperty $current 'requiredResourceAccess') {
        foreach ($entry in @($current.requiredResourceAccess)) {
            foreach ($ra in @($entry.resourceAccess)) {
                $currentAccessKeys += "$($entry.resourceAppId)|$($ra.id)|$($ra.type)"
            }
        }
    }
    $desiredAccessKeys = @()
    foreach ($entry in $requiredResourceAccess) {
        foreach ($ra in $entry.resourceAccess) {
            $desiredAccessKeys += "$($entry.resourceAppId)|$($ra.id)|$($ra.type)"
        }
    }

    $missing = @($desiredAccessKeys | Where-Object { $currentAccessKeys -notcontains $_ })
    if ($missing.Count -gt 0) {
        # requiredResourceAccess is replace-semantics, so merge instead of clobbering existing entries.
        $merged = @{}
        foreach ($entry in @($current.requiredResourceAccess) + $requiredResourceAccess) {
            if (-not $entry) { continue }
            $key = [string]$entry.resourceAppId
            if (-not $merged.ContainsKey($key)) { $merged[$key] = @{} }
            foreach ($ra in @($entry.resourceAccess)) {
                $merged[$key]["$($ra.id)|$($ra.type)"] = @{ id = $ra.id; type = $ra.type }
            }
        }
        $patch.requiredResourceAccess = @(
            $merged.Keys | ForEach-Object {
                @{ resourceAppId = $_; resourceAccess = @($merged[$_].Values) }
            }
        )
    }
}

if ($patch.Count -eq 0) {
    Write-Host '  Nothing to update.' -ForegroundColor Yellow
}
elseif ($PSCmdlet.ShouldProcess($blueprintAppId, "PATCH $blueprintUri")) {
    Invoke-Graph -Method PATCH -Uri $blueprintUri -Body $patch -RetryOnNotFound | Out-Null
    Write-Host "  Patched: $($patch.Keys -join ', ')" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Step 7 - blueprint principal
# ---------------------------------------------------------------------------
Write-Step 7 'Creating the agent identity blueprint principal'

# The lookup is untyped, so it also returns a PLAIN service principal that happens to share the
# blueprint's appId. Writing blueprint-principal operations against one fails with
# "ServicePrincipal is not an AgentIdentityBlueprint Principal", so the type is verified rather
# than assumed.
$bpPrincipal = Invoke-Graph -Method GET -Uri "/servicePrincipals(appId='$blueprintAppId')?`$select=id,appId,displayName" -TolerateNotFound

# 'Verified' | 'Plain' | 'Unknown'. Unknown means the check itself could not run (for example the
# caller cannot list blueprint principals), which must not be reported as a wrong type.
$principalTypeState = 'Unknown'
if ($bpPrincipal) {
    $principalTypeState = Test-BlueprintPrincipalType -AppId $blueprintAppId -ExpectedId $bpPrincipal.id

    switch ($principalTypeState) {
        'Verified' { Write-Host "  Principal already exists: $($bpPrincipal.id)" -ForegroundColor Yellow }
        'Plain'    {
            Write-Warning ("A service principal for appId $blueprintAppId already exists ($($bpPrincipal.id)) but it is a " +
                           'PLAIN servicePrincipal, not an agentIdentityBlueprintPrincipal. It cannot own agent ' +
                           'identities and will reject every blueprint-principal operation.')
            Write-Host '  Remove the plain service principal, then re-run this script:' -ForegroundColor Yellow
            Write-Host "      DELETE $script:GraphRoot/servicePrincipals/$($bpPrincipal.id)" -ForegroundColor Gray
            Write-Host "      Remove-MgServicePrincipal -ServicePrincipalId $($bpPrincipal.id)" -ForegroundColor Gray
        }
        default    { Write-Host "  Principal already exists: $($bpPrincipal.id) (type not verified)" -ForegroundColor Yellow }
    }
}
elseif ($PSCmdlet.ShouldProcess($blueprintAppId, 'POST /servicePrincipals/microsoft.graph.agentIdentityBlueprintPrincipal')) {
    # Two distinct APIs can produce this object, and they are authorized differently:
    #
    #   1. POST /servicePrincipals/microsoft.graph.agentIdentityBlueprintPrincipal  { appId }
    #      -> the dedicated Create API, authorized by AgentIdentityBlueprintPrincipal.Create.
    #         The body must NOT carry an '@odata.type'.
    #   2. POST /servicePrincipals  { '@odata.type', appId }
    #      -> the generic servicePrincipal create, authorized by Application.ReadWrite.OwnedBy /
    #         Application.ReadWrite.All.
    #
    # Route 1 is preferred because it needs only the narrow A365 role. Route 2 is tried afterwards
    # so a tenant that has consented the classic application permissions but not the newer A365
    # role can still get through.
    $bpPrincipal = Invoke-Graph -Method POST `
        -Uri '/servicePrincipals/microsoft.graph.agentIdentityBlueprintPrincipal' `
        -Body ([ordered]@{ appId = $blueprintAppId }) `
        -RetryOnNotFound -RetryOnAppPropagation -TolerateForbidden -MaxAttempts 8

    if ($bpPrincipal) {
        Write-Host "  Principal created: $($bpPrincipal.id)" -ForegroundColor Green
    }
    else {
        Write-Warning ('AgentIdentityBlueprintPrincipal.Create was denied (403). Falling back to the ' +
                       'generic POST /servicePrincipals route, which uses Application.ReadWrite.*.')
        $bpPrincipal = Invoke-Graph -Method POST -Uri '/servicePrincipals' `
            -Body ([ordered]@{
                '@odata.type' = '#microsoft.graph.agentIdentityBlueprintPrincipal'
                appId         = $blueprintAppId
            }) `
            -RetryOnNotFound -RetryOnAppPropagation -TolerateForbidden -MaxAttempts 8

        if ($bpPrincipal) {
            Write-Host "  Principal created via the generic route: $($bpPrincipal.id)" -ForegroundColor Green
        }
        else {
            throw (Get-PrincipalDenialAdvice -AppId $blueprintAppId)
        }
    }

    # Confirm the service really produced a blueprint principal rather than a plain SP.
    $principalTypeState = Test-BlueprintPrincipalType -AppId $blueprintAppId -ExpectedId $bpPrincipal.id
    if ($principalTypeState -eq 'Plain') {
        Write-Warning ('The service principal was created but does not read back as an ' +
                       'agentIdentityBlueprintPrincipal. Agent identity creation will fail against it.')
    }
}

$bpPrincipalId = if ($bpPrincipal) { $bpPrincipal.id } else { $null }

# The blueprint principal carries its own owners collection; keep it aligned with the blueprint.
# Skipped only when the object is KNOWN to be the wrong type - every write would fail, and the
# blueprint itself is still usable, so this must not take the whole run down. When the type could
# not be determined the assignment is attempted anyway and degrades to a warning on failure.
if ($bpPrincipalId -and $principalTypeState -ne 'Plain') {
    Add-OwnerAssignment `
        -BaseUri "/servicePrincipals/$bpPrincipalId/microsoft.graph.agentIdentityBlueprintPrincipal" `
        -Owner $ownerPrincipals -Label 'Principal' | Out-Null
}
elseif ($bpPrincipalId) {
    Write-Warning 'Skipping principal owner assignment: the object is not an agentIdentityBlueprintPrincipal.'
}

# ---------------------------------------------------------------------------
# Step 8 - inheritable permissions
# ---------------------------------------------------------------------------
Write-Step 8 'Configuring inheritable permissions'

if ($SkipInheritablePermissions) {
    Write-Host '  Skipped (-SkipInheritablePermissions).' -ForegroundColor Yellow
}
elseif ($resolvedResources.Count -eq 0) {
    Write-Host '  No resource apps resolved; nothing to configure.' -ForegroundColor Yellow
}
else {
    if ($resolvedResources.Count -gt 50) { throw 'Maximum of 50 resource apps per blueprint exceeded.' }

    $ipUri = "/applications/microsoft.graph.agentIdentityBlueprint/$blueprintObjectId/inheritablePermissions"

    # The kinds mirror what was actually requested for this resource.
    $ipBodyFor = {
        param($r)
        [ordered]@{
            resourceAppId     = $r.ResourceAppId
            inheritableScopes = if ($r.DelegatedScopes.Count -gt 0) {
                @{ '@odata.type' = '#microsoft.graph.allAllowedScopes'; kind = 'allAllowed' }
            } else {
                @{ '@odata.type' = '#microsoft.graph.noScopes';         kind = 'none' }
            }
            inheritableRoles  = if ($r.AppRoles.Count -gt 0) {
                @{ '@odata.type' = '#microsoft.graph.allAllowedRoles';  kind = 'allAllowed' }
            } else {
                @{ '@odata.type' = '#microsoft.graph.noRoles';          kind = 'none' }
            }
        }
    }

    $readIpIds = {
        $cur = Invoke-Graph -Method GET -Uri $ipUri -TolerateNotFound
        if (Test-HasProperty $cur 'value') { @(Get-PropertyValue $cur.value 'resourceAppId') } else { @() }
    }

    $existingIds = & $readIpIds

    foreach ($r in $resolvedResources) {
        $ipBody = & $ipBodyFor $r

        if ($existingIds -contains $r.ResourceAppId) {
            if ($PSCmdlet.ShouldProcess($r.ResourceName, "PATCH $ipUri/$($r.ResourceAppId)")) {
                Invoke-Graph -Method PATCH -Uri "$ipUri/$($r.ResourceAppId)" `
                    -Body @{ inheritableScopes = $ipBody.inheritableScopes; inheritableRoles = $ipBody.inheritableRoles } | Out-Null
                Write-Host "  Requested inheritance update for $($r.ResourceName)." -ForegroundColor Gray
            }
        }
        elseif ($PSCmdlet.ShouldProcess($r.ResourceName, "POST $ipUri")) {
            Invoke-Graph -Method POST -Uri $ipUri -TolerateConflict -RetryOnNotFound -Body $ipBody | Out-Null
            Write-Host "  Requested inheritance for $($r.ResourceName)." -ForegroundColor Gray
        }
    }

    # A 201 here is NOT proof the entry survived. The service applies each write as a
    # read-modify-write over the whole collection, so two writes issued back to back can clobber
    # each other: the second reads a stale (often empty) collection and persists only its own
    # entry. Both calls still return 201, and a GET immediately after a create returns nothing.
    # Reproduced app-only with Microsoft Graph + a second resource; spacing the writes ~25s apart
    # let both survive. So verify what actually landed and re-apply whatever is missing.
    if (-not $WhatIfPreference -and $resolvedResources.Count -gt 0) {
        $desiredIds = @($resolvedResources | ForEach-Object { $_.ResourceAppId })
        $missingIds = @()

        for ($pass = 1; $pass -le 6; $pass++) {
            Start-Sleep -Seconds ([Math]::Min(6 * $pass, 25))

            $presentIds = & $readIpIds
            $missingIds = @($desiredIds | Where-Object { $presentIds -notcontains $_ })
            if ($missingIds.Count -eq 0) { break }

            Write-Verbose "Inheritance verification pass $pass - missing: $($missingIds -join ', ')"
            foreach ($id in $missingIds) {
                $r = @($resolvedResources | Where-Object { $_.ResourceAppId -eq $id })[0]
                # 400 "already exists" means the backend holds it but the directory has not
                # surfaced it yet, so it is tolerated here and settled by the next read.
                Invoke-Graph -Method POST -Uri $ipUri -TolerateConflict -TolerateBadRequest -RetryOnNotFound `
                    -Body (& $ipBodyFor $r) | Out-Null
            }
        }

        if ($missingIds.Count -eq 0) {
            foreach ($r in $resolvedResources) {
                Write-Host "  Verified inheritance for $($r.ResourceName)." -ForegroundColor Green
            }
        }
        else {
            $script:InheritanceMissing = $missingIds
            foreach ($id in $missingIds) {
                $name = @($resolvedResources | Where-Object { $_.ResourceAppId -eq $id })[0].ResourceName
                Write-Warning "  Inheritance for '$name' ($id) was accepted but did not persist."
            }
            Write-Warning '  Agent identities will NOT inherit those permissions. Re-run this script to repair,'
            Write-Warning '  or add the entry manually:'
            Write-Warning "    POST https://graph.microsoft.com/v1.0$ipUri"
            Write-Warning '      { "resourceAppId": "<id>", "inheritableScopes": { "kind": "allAllowed" },'
            Write-Warning '        "inheritableRoles": { "kind": "allAllowed" } }'
        }
    }
}

# ---------------------------------------------------------------------------
# Step 9 - admin consent on the blueprint principal
# ---------------------------------------------------------------------------
Write-Step 9 'Granting tenant admin consent'

if (-not $GrantAdminConsent) {
    Write-Host '  Skipped. Re-run with -GrantAdminConsent, or consent in the Entra admin center.' -ForegroundColor Yellow
}
elseif (-not $bpPrincipalId) {
    Write-Warning '  Blueprint principal unavailable; cannot grant consent.'
}
else {
    $assignments = Invoke-Graph -Method GET -Uri "/servicePrincipals/$bpPrincipalId/appRoleAssignments" -TolerateNotFound -RetryOnNotFound
    $existingAssignments = @()
    if (Test-HasProperty $assignments 'value') { $existingAssignments = @($assignments.value) }

    foreach ($r in $resolvedResources) {

        if ($r.DelegatedScopes.Count -gt 0) {
            $wanted = @($r.DelegatedScopes | ForEach-Object { $_.Name })
            $gFilter = "clientId eq '$bpPrincipalId' and resourceId eq '$($r.ResourceSpId)' and consentType eq 'AllPrincipals'"
            $grants  = Invoke-Graph -Method GET -Uri "/oauth2PermissionGrants?`$filter=$([uri]::EscapeDataString($gFilter))" -TolerateNotFound

            $existingGrant = $null
            if ((Test-HasProperty $grants 'value') -and @($grants.value).Count -gt 0) {
                $existingGrant = @($grants.value)[0]
            }

            if ($existingGrant) {
                $have  = @($existingGrant.scope -split '\s+' | Where-Object { $_ })
                $merged = @($have + $wanted | Select-Object -Unique)
                if (@(Compare-Object $have $merged).Count -gt 0 -and
                    $PSCmdlet.ShouldProcess($r.ResourceName, "PATCH /oauth2PermissionGrants/$($existingGrant.id)")) {
                    try {
                        Invoke-Graph -Method PATCH -Uri "/oauth2PermissionGrants/$($existingGrant.id)" `
                            -Body @{ scope = ($merged -join ' ') } | Out-Null
                        Write-Host "  Updated delegated grant on $($r.ResourceName): $($merged -join ' ')" -ForegroundColor Green
                    }
                    catch {
                        # The blueprint itself is already created and usable, so a refused grant
                        # must not abort the run - it is collected and reported with a link an
                        # administrator can use to finish the job.
                        Add-ConsentFailure -Kind 'delegated' -ResourceName $r.ResourceName `
                            -ResourceAppId $r.ResourceAppId -Permission ($merged -join ' ') -Message $_.Exception.Message
                        Write-Warning "  Could not update the delegated grant on $($r.ResourceName): $($_.Exception.Message)"
                    }
                } else {
                    Write-Host "  Delegated grant on $($r.ResourceName) already current." -ForegroundColor Yellow
                }
            }
            elseif ($PSCmdlet.ShouldProcess($r.ResourceName, 'POST /oauth2PermissionGrants')) {
                try {
                    Invoke-Graph -Method POST -Uri '/oauth2PermissionGrants' -RetryOnNotFound -Body ([ordered]@{
                        clientId    = $bpPrincipalId
                        consentType = 'AllPrincipals'
                        resourceId  = $r.ResourceSpId
                        scope       = ($wanted -join ' ')
                    }) | Out-Null
                    Write-Host "  Granted delegated scopes on $($r.ResourceName): $($wanted -join ' ')" -ForegroundColor Green
                }
                catch {
                    Add-ConsentFailure -Kind 'delegated' -ResourceName $r.ResourceName `
                        -ResourceAppId $r.ResourceAppId -Permission ($wanted -join ' ') -Message $_.Exception.Message
                    Write-Warning "  Could not grant delegated scopes on $($r.ResourceName): $($_.Exception.Message)"
                }
            }
        }

        foreach ($role in $r.AppRoles) {
            $has = @($existingAssignments | Where-Object {
                $_.appRoleId -eq $role.Id -and $_.resourceId -eq $r.ResourceSpId
            }).Count -gt 0

            if ($has) {
                Write-Host "  App role '$($role.Name)' on $($r.ResourceName) already assigned." -ForegroundColor Yellow
            }
            elseif ($PSCmdlet.ShouldProcess("$($r.ResourceName)/$($role.Name)", "POST /servicePrincipals/$bpPrincipalId/appRoleAssignments")) {
                try {
                    Invoke-Graph -Method POST -Uri "/servicePrincipals/$bpPrincipalId/appRoleAssignments" -TolerateConflict -RetryOnNotFound -Body ([ordered]@{
                        principalId = $bpPrincipalId
                        resourceId  = $r.ResourceSpId
                        appRoleId   = $role.Id
                    }) | Out-Null
                    Write-Host "  Assigned app role '$($role.Name)' on $($r.ResourceName)." -ForegroundColor Green
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
$summary = [ordered]@{
    tenantId                 = $ctx.TenantId
    displayName              = $DisplayName
    blueprintObjectId        = $blueprintObjectId
    blueprintAppId           = $blueprintAppId
    identifierUri            = "api://$blueprintAppId"
    exposedScope             = if ([string]::IsNullOrWhiteSpace($ExposedScopeValue)) { $null } else { "api://$blueprintAppId/$ExposedScopeValue" }
    blueprintPrincipalId     = $bpPrincipalId
    blueprintPrincipalIsTyped = switch ($principalTypeState) { 'Verified' { $true } 'Plain' { $false } default { $null } }
    sponsorIds               = $sponsorIds
    assignedSponsorIds       = $assignedSponsorIds
    # Retained so anything written against the old single-sponsor shape keeps working.
    # sponsorIds is the authoritative list.
    sponsorId                = $(if ($sponsorIds.Count -gt 0) { $sponsorIds[0] } else { $null })
    ownerIds                 = $ownerIds
    assignedOwnerIds         = $assignedOwnerIds
    ownerAssignmentDenied    = $script:OwnerAssignmentDenied
    keyVault                 = $script:KeyVaultResult
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
    # Always present, so a caller can surface it without re-deriving the URL. It is only
    # ACTIONABLE when consentFailures is non-empty.
    adminConsentUrl          = (Get-AdminConsentUrl -TenantId $ctx.TenantId -ClientAppId $blueprintAppId)
    portalPermissionsUrl     = (Get-PortalPermissionsUrl -ClientObjectId $bpPrincipalId -ClientAppId $blueprintAppId)
    federatedCredential      = if ($ManagedIdentityPrincipalId) { $FederatedCredentialName } else { $null }
    clientSecret             = $secretValue
    clientSecretDetail       = $secretDetail
    resources                = @($resolvedResources | ForEach-Object {
                                    [ordered]@{
                                        resourceAppId   = $_.ResourceAppId
                                        resourceName    = $_.ResourceName
                                        delegatedScopes = @(Get-PropertyValue $_.DelegatedScopes 'Name')
                                        appRoles        = @(Get-PropertyValue $_.AppRoles 'Name')
                                    }
                                })
    adminConsentGranted      = [bool]$GrantAdminConsent
    inheritanceMissing       = @($script:InheritanceMissing)
}

Write-Host ''
Write-Host '=== Blueprint ready ===' -ForegroundColor Cyan
$summary.GetEnumerator() | Where-Object { $_.Key -ne 'clientSecret' } | ForEach-Object {
    $v = if ($_.Value -is [System.Collections.IEnumerable] -and $_.Value -isnot [string]) { ($_.Value | ConvertTo-Json -Compress -Depth 6) } else { $_.Value }
    Write-Host ("  {0,-22} {1}" -f $_.Key, $v)
}

if ($script:OwnerAssignmentDenied) {
    Write-Host ''
    Write-Host 'One or more owners could not be assigned. The blueprint itself is complete and usable.' -ForegroundColor Yellow
    Write-Host (Get-OwnerDenialAdvice) -ForegroundColor Gray
}

Write-ConsentActionRequired -TenantId $ctx.TenantId -ClientAppId $blueprintAppId `
    -ClientObjectId $bpPrincipalId -DisplayName $DisplayName -Failures $script:ConsentFailures

if (@($script:InheritanceMissing).Count -gt 0) {
    Write-Host ''
    Write-Host 'ACTION REQUIRED - permission inheritance is incomplete.' -ForegroundColor Red
    Write-Host '  These resource apps were accepted by the service but did not persist, so agent' -ForegroundColor Gray
    Write-Host '  identities created from this blueprint will NOT inherit their permissions:' -ForegroundColor Gray
    foreach ($id in $script:InheritanceMissing) { Write-Host "    $id" -ForegroundColor Gray }
    Write-Host '  Simply re-run this script with the same -RequiredPermission to repair it; the' -ForegroundColor Gray
    Write-Host '  blueprint, its principal and its consent grants are already correct.' -ForegroundColor Gray
}

if ($bpPrincipalId -and $principalTypeState -eq 'Plain') {
    Write-Host ''
    Write-Host 'ACTION REQUIRED - the blueprint principal is the wrong type.' -ForegroundColor Red
    Write-Host "  Service principal $bpPrincipalId carries the blueprint's appId but is a plain" -ForegroundColor Gray
    Write-Host '  servicePrincipal, so agent identities cannot be created from it.' -ForegroundColor Gray
    Write-Host '  Delete it and re-run this script, which will recreate it with the correct type:' -ForegroundColor Gray
    Write-Host "      Remove-MgServicePrincipal -ServicePrincipalId $bpPrincipalId" -ForegroundColor Gray
}

Write-Host ''
Write-Host 'Next steps (outside this script):' -ForegroundColor Cyan
Write-Host '  * Create agent identities from this blueprint (see New-A365AgentIdentity.ps1):'
Write-Host '      POST /v1.0/servicePrincipals/microsoft.graph.agentIdentity'
Write-Host '        { "displayName": "...", "agentIdentityBlueprintId": "<blueprint appId>",'
Write-Host '          "sponsors@odata.bind": [ ".../users/<id>" ] }'
Write-Host '  * Optionally create the agent user:'
Write-Host '      POST /v1.0/users/microsoft.graph.agentUser'
Write-Host '  * Register the agent card in the Agent 365 registry (Agent Registry API) so it appears'
Write-Host '    in the Microsoft 365 admin center.'
Write-Host '  * Request a blueprint token with fmi_path=<agent-identity-client-id> against'
Write-Host "      POST https://login.microsoftonline.com/$($ctx.TenantId)/oauth2/v2.0/token"

if ($OutputJsonPath) {
    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path $OutputJsonPath -Encoding utf8
    Write-Host ''
    Write-Host "Summary written to $OutputJsonPath" -ForegroundColor Green
}

[pscustomobject]$summary

Complete-A365Log -Outcome 'Succeeded'