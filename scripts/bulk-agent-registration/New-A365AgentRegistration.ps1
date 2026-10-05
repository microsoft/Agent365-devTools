<#
.SYNOPSIS
    Registers Agent 365 agents in the Microsoft Agent Registry using only Microsoft Graph REST
    calls - the same endpoints and payloads the `a365` CLI issues.

.DESCRIPTION
    `a365 setup all` finishes by registering the agent so it becomes visible to Agent 365
    management surfaces (Microsoft 365 admin center, Defender, Purview). This script performs
    that final step on its own, without the CLI.

    Contracts here were taken from the shipped CLI assembly
    (Microsoft.Agents.A365.DevTools.Cli 1.1.214, GraphApiService), because these endpoints are
    /beta and are NOT published in the public Microsoft Graph documentation. Treat them as
    preview surface that can change without notice.

    TWO REGISTRATION APIS EXIST. The CLI ships both; only the first is on its live code path.

      -Api CopilotAgentRegistrations   (default, CLI "V2", the one `a365 setup all` calls)
          POST   /beta/copilot/agentRegistrations
          GET    /beta/copilot/agentRegistrations/{id}
          DELETE /beta/copilot/agentRegistrations/{id}
        Token must carry AgentRegistration.ReadWrite.All (delegated or application). The CLI
        acquires it as `.default` against a custom client app. Backed by the AgentX private-preview
        service (app id 59eca866-2f46-40b8-96ff-63f663121ef9), which the CLI stamps into every
        payload as `managedByAppId` because that is the CLI's own appId. This script stamps ITS
        OWN caller appId instead - see -ManagedByAppId.

      -Api AgentRegistry               (CLI "V1", legacy; the CLI still deletes through it)
          POST   /beta/agentRegistry/agentInstances
          DELETE /beta/agentRegistry/agentInstances/{id}
        Token must carry AgentInstance.ReadWrite.All, and the caller needs the
        "Agent Registry Administrator" Entra role.

    Behaviour mirrored from the CLI:
      * The registration `id` is generated client-side, so a retry after an ambiguous failure
        reuses the same GUID instead of creating a duplicate.
      * 409 Conflict means the agent was already registered (duplicate `sourceAgentId`). The
        existing id is read from the response when present; the CLI treats this as success.
      * 502/503/504 are retried (3 attempts, 2s apart).
      * 403 on the AgentRegistry API is retried once after 30s, because the
        "Agent Registry Administrator" role can take 5-15 minutes to propagate.
      * A display name ending in " Identity" is rewritten to " Agent"
        ("Contoso Helpdesk Identity" -> "Contoso Helpdesk Agent").
      * An existing registration id is probed first (GET). 200 = keep, 404 = re-register,
        anything else = inconclusive, so the stored id is kept rather than risking a duplicate.

    AUTHENTICATION
    Same model as New-A365AgentBlueprint.ps1 and New-A365AgentIdentity.ps1: pick exactly one of
    -ClientSecret, -CertificateThumbprint/-Certificate/-CertificatePath, -UseManagedIdentity,
    -AccessToken or -Interactive. There is no implicit fallback, so an unattended run can never
    stall on a sign-in prompt.

    APPLICATION (APP-ONLY) PERMISSIONS ARE SUPPORTED.
    Both registration permissions are published as application roles on Microsoft Graph - verified
    against the live service principal for appId 00000003-0000-0000-c000-000000000000:
      AgentRegistration.ReadWrite.All   39fb8c64-7bd3-4107-8515-14d6e55ddda4
      AgentInstance.ReadWrite.All       07abdd95-78dc-4353-bd32-09f880ea43d0
    Grant them with New-A365AutomationApp.ps1 -Scenario Registration (or All), then run this script
    with -ClientId/-ClientSecret, a certificate, or a managed identity.

    Two things differ under app-only:
      * There is no `/me`, so `ownerIds` and `createdBy` cannot be inferred. Pass -Owner (UPNs,
        mail addresses or display names, resolved through Graph) or -OwnerId (a user object id).
        One of them is required.
      * `managedByAppId` is stamped with THIS application's appId. The service refuses any other
        value with HTTP 500 "You do not have permission to create an agent registration managed by
        another AppId", so do not pass -ManagedByAppId. The a365 CLI hardcodes the AgentX
        first-party appId 59eca866-2f46-40b8-96ff-63f663121ef9 there because that is its own appId;
        a custom application cannot claim it.

    Also note that the stock Microsoft Graph PowerShell client app has no consent for these
    preview scopes. For -Interactive you will almost certainly need -ClientId pointing at your own
    app registration that has AgentRegistration.ReadWrite.All (or AgentInstance.ReadWrite.All)
    consented - which is exactly why the CLI carries its own "custom client app" plumbing.

.PARAMETER TenantId
    Directory (tenant) ID or verified domain to authenticate against. Required.

.PARAMETER DisplayName
    Display name for a single agent registration. A trailing " Identity" is rewritten to " Agent"
    to match CLI behaviour; use -SkipDisplayNameNormalization to keep the name verbatim.

.PARAMETER Description
    Optional description for the registration. Omitted from the payload when empty, exactly as
    the CLI does.

.PARAMETER AgentIdentityId
    Object id of the agent identity service principal (created by New-A365AgentIdentity.ps1).
    Becomes `agentIdentityId` and, preferentially, `sourceAgentId` - the field the service uses to
    detect duplicates.

.PARAMETER BlueprintAppId
    Application (client) ID of the agent identity blueprint. Becomes `agentIdentityBlueprintId`,
    and is the fallback `sourceAgentId` when -AgentIdentityId is absent. Mandatory with
    -FromBlueprint.

.PARAMETER ClientAppId
    Client app id used to acquire the registration token. Accepted for parity with the CLI, which
    takes it as a parameter but does not place it in the request body.

.PARAMETER RegistrationId
    An existing registration id. Probed before registering so a re-run is idempotent, and required
    with -Unregister.

.PARAMETER Agent
    Array of hashtables for bulk registration. Recognised keys: DisplayName (required),
    AgentIdentityId, BlueprintAppId, Description, RegistrationId.

.PARAMETER FromBlueprint
    Enumerate every agent identity under -BlueprintAppId and register each one. Uses
    GET /beta/servicePrincipals/microsoft.graph.agentIdentity?$filter=agentIdentityBlueprintId eq '...'
    which is the same query the CLI runs.

.PARAMETER Unregister
    Delete the registration in -RegistrationId instead of creating one. 404 counts as success.

.PARAMETER OwnerId
    Object id (or UPN) of the single user to record as owner/creator. Takes precedence over
    -Owner. Defaults to the signed-in user via GET /v1.0/me.

.PARAMETER Owner
    One or more owners to resolve to user object ids through Graph, for callers that have a
    list of people rather than object ids. Accepts an object id, a UPN, a mail address or a
    display name; each is resolved with GET /users/{id-or-upn} and, failing that, a $filter
    over mail/userPrincipalName/displayName.

    All resolved ids go into `ownerIds`; the first also becomes `createdBy`.

    This is the app-only answer to `/me`: pass the same -Owner list used for the blueprint and
    the agent identity and the registration owner is worked out for you. Entries that are not
    users - a service principal owner is legitimate on a blueprint but cannot own a
    registration - are skipped with a warning.

    Resolving another user needs User.ReadBasic.All (delegated) or User.Read.All (app-only);
    both are requested automatically when -Owner is used.

.PARAMETER Api
    Which registration API to use. CopilotAgentRegistrations (default) or AgentRegistry.

.PARAMETER ManagedByAppId
    Overrides `managedByAppId`. Leave unset: it defaults to the calling application's own appId,
    which is the only value the service accepts. Passing another application's appId - including
    the a365 CLI's AgentX appId 59eca866-2f46-40b8-96ff-63f663121ef9, which used to be the default
    here - fails with HTTP 500 "You do not have permission to create an agent registration managed
    by another AppId." CopilotAgentRegistrations only.

.PARAMETER Force
    Register even when -RegistrationId already resolves, and ignore an inconclusive probe.

.PARAMETER SkipDisplayNameNormalization
    Keep display names verbatim instead of rewriting a trailing " Identity" to " Agent".

.PARAMETER RolePropagationDelaySeconds
    Seconds to wait before the single 403 retry on the AgentRegistry API. Defaults to 30, the
    CLI's default.

.PARAMETER OutputPath
    Writes a JSON summary of every registration to this path.

.PARAMETER ClientId
    Application (client) ID to authenticate as, or the client app id for -Interactive.

.PARAMETER ClientSecret
    Client secret, as a SecureString or a plain string. Also read from $env:A365_CLIENT_SECRET,
    which is preferred because it keeps the value out of shell history.

.PARAMETER CertificateThumbprint
    Thumbprint of a certificate in the current user's or machine's store.

.PARAMETER Certificate
    An already-loaded X509Certificate2.

.PARAMETER CertificatePath
    Path to a .pfx file, unlocked with -CertificatePassword when needed.

.PARAMETER CertificatePassword
    Password for -CertificatePath, as a SecureString or a plain string.

.PARAMETER UseManagedIdentity
    Authenticate with the host's managed identity. Pass -ClientId for a user-assigned identity.

.PARAMETER AccessToken
    A pre-acquired Graph access token, as a SecureString or a plain string.

.PARAMETER Interactive
    Sign in as a user. Pair with -ClientId for an app that has the preview scopes consented.

.PARAMETER SkipPermissionCheck
    Skips the app role pre-flight check under app-only auth.

.PARAMETER Update
    Update a registration that already exists instead of creating one. Requires
    -RegistrationId.

    Only the attributes supplied on the command line are written. Leaving out -Owner and
    -OwnerId preserves the existing owners rather than falling back to the signed-in user.
    Because this API returns HTTP 200 on writes it silently discards, the object is read back
    afterwards and any property that did not persist is reported.

    Update-A365AgentRegistration.ps1 is the dedicated entry point for this and is usually the
    clearer way to call it. Both run exactly the same code.

.PARAMETER RegistrationId
    With -Update, the registration to change. This is the id the SERVICE returned at
    registration time, which starts with 'T_'. The bare GUID form is also accepted and
    resolved automatically.

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
    # Register one agent as the signed-in user, through your own client app.
    .\New-A365AgentRegistration.ps1 -TenantId contoso.onmicrosoft.com -Interactive `
        -ClientId 8a1f... -DisplayName 'Contoso Helpdesk Agent' `
        -AgentIdentityId 11111111-2222-3333-4444-555555555555 `
        -BlueprintAppId  aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee

.EXAMPLE
    # Register every agent identity that belongs to a blueprint.
    .\New-A365AgentRegistration.ps1 -TenantId contoso.onmicrosoft.com -Interactive -ClientId 8a1f... `
        -FromBlueprint -BlueprintAppId aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee -OutputPath .\registrations.json

.EXAMPLE
    # Bulk register from a list.
    $agents = @(
        @{ DisplayName = 'Finance Agent'; AgentIdentityId = '1111...' }
        @{ DisplayName = 'HR Agent';      AgentIdentityId = '2222...' }
    )
    .\New-A365AgentRegistration.ps1 -TenantId contoso.onmicrosoft.com -Interactive -ClientId 8a1f... -Agent $agents

.EXAMPLE
    # Legacy Agent Registry API (needs the "Agent Registry Administrator" Entra role).
    .\New-A365AgentRegistration.ps1 -TenantId contoso.onmicrosoft.com -Interactive -ClientId 8a1f... `
        -Api AgentRegistry -DisplayName 'Contoso Helpdesk Agent' -BlueprintAppId aaaa...

.EXAMPLE
    # Unattended, app-only. -OwnerId is mandatory because /me does not resolve.
    $env:A365_CLIENT_SECRET = '...'
    .\New-A365AgentRegistration.ps1 -TenantId contoso.onmicrosoft.com -ClientId 8a1f... `
        -OwnerId 99999999-8888-7777-6666-555555555555 `
        -DisplayName 'Contoso Helpdesk Agent' -AgentIdentityId 1111...

.EXAMPLE
    # Same, but let the script resolve the owner object ids from UPNs via Graph.
    $env:A365_CLIENT_SECRET = '...'
    .\New-A365AgentRegistration.ps1 -TenantId contoso.onmicrosoft.com -ClientId 8a1f... `
        -Owner ana@contoso.com, sam@contoso.com `
        -DisplayName 'Contoso Helpdesk Agent' -AgentIdentityId 1111...

.EXAMPLE
    # Remove a registration.
    .\New-A365AgentRegistration.ps1 -TenantId contoso.onmicrosoft.com -Interactive -ClientId 8a1f... `
        -Unregister -RegistrationId 33333333-4444-5555-6666-777777777777

.NOTES
    Requires the Microsoft.Graph.Authentication module:
        Install-Module Microsoft.Graph.Authentication -Scope CurrentUser

    Prerequisites: a blueprint (New-A365AgentBlueprint.ps1) and an agent identity
    (New-A365AgentIdentity.ps1).

    These endpoints are /beta and undocumented. Every detail here was recovered from the shipped
    CLI assembly, so verify against your tenant before relying on it in production.
#>

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'Single')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'CertificatePassword',
    Justification = 'Deliberately accepts a plain string for usability; ConvertTo-SecureStringValue converts it immediately.')]
param(
    [Parameter(Mandatory)][string] $TenantId,

    [Parameter(Mandatory, ParameterSetName = 'Single')]
    [Parameter(ParameterSetName = 'Update')]
    [string] $DisplayName,

    [Parameter(ParameterSetName = 'Single')]
    [Parameter(ParameterSetName = 'Update')]
    [string] $AgentIdentityId,

    [string] $Description,

    [Parameter(ParameterSetName = 'Single')]
    [Parameter(ParameterSetName = 'Bulk')]
    [Parameter(ParameterSetName = 'Update')]
    [Parameter(Mandatory, ParameterSetName = 'FromBlueprint')]
    [string] $BlueprintAppId,    [Parameter(Mandatory, ParameterSetName = 'Bulk')][hashtable[]] $Agent,

    [Parameter(Mandatory, ParameterSetName = 'FromBlueprint')][switch] $FromBlueprint,

    [Parameter(Mandatory, ParameterSetName = 'Unregister')][switch] $Unregister,

    # Update an existing registration in place. Only the properties supplied on the command line
    # are written; PATCH was verified live to merge per-property rather than replace the object.
    [Parameter(Mandatory, ParameterSetName = 'Update')][switch] $Update,

    [Parameter(ParameterSetName = 'Single')]
    [Parameter(Mandatory, ParameterSetName = 'Unregister')]
    [Parameter(Mandatory, ParameterSetName = 'Update')]
    [string] $RegistrationId,

    [string] $ClientAppId,
    [string] $OwnerId,
    [string[]] $Owner,

    [ValidateSet('CopilotAgentRegistrations', 'AgentRegistry')]
    [string] $Api = 'CopilotAgentRegistrations',

    [string] $ManagedByAppId,

    [switch] $Force,
    [switch] $SkipDisplayNameNormalization,
    [ValidateRange(0, 900)][int] $RolePropagationDelaySeconds = 30,
    [string] $OutputPath,

    # --- authentication -----------------------------------------------------
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
$null = Initialize-A365Log -Path $LogPath -ScriptName 'New-A365AgentRegistration.ps1' `
    -BoundParameters $PSBoundParameters -IncludeSecrets:$LogIncludeSecrets -CorrelationId $LogCorrelationId
if ($script:LogFile) { Write-Host "  Log file           : $($script:LogFile)" -ForegroundColor DarkGray }

trap {
    Write-A365Log -Level ERROR -Message "UNHANDLED: $($_.Exception.Message)" -Detail $_.ScriptStackTrace
    Complete-A365Log -Outcome 'Failed'
    break
}

$script:GraphHost            = 'https://graph.microsoft.com'
$script:GraphRoot            = "$script:GraphHost/v1.0"
$script:MicrosoftGraphAppId  = '00000003-0000-0000-c000-000000000000'

# Recovered from Microsoft.Agents.A365.DevTools.Cli 1.1.214 (GraphApiService / AuthenticationConstants).
$script:CopilotRegistrationsPath = '/beta/copilot/agentRegistrations'
$script:AgentInstancesPath       = '/beta/agentRegistry/agentInstances'

# The appId of the application this script is authenticated as. Set by Connect-GraphSession and
# used as the default managedByAppId, because the service only accepts its own caller there.
# The a365 CLI's AgentX appId (59eca866-2f46-40b8-96ff-63f663121ef9) is deliberately NOT a default.
$script:CallerAppId = ''

# ---------------------------------------------------------------------------
# Graph REST helpers
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
    $bodyObj = $null
    if ($ErrorRecord.ErrorDetails -and $ErrorRecord.ErrorDetails.Message) {
        $details = [string]$ErrorRecord.ErrorDetails.Message
        try {
            $parsed  = $details | ConvertFrom-Json -ErrorAction Stop
            $bodyObj = $parsed
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

    [pscustomobject]@{ Status = $status; Code = $code; Message = $message; Body = $bodyObj }
}

function Resolve-GraphUri {
    param([Parameter(Mandatory)][string] $Uri)
    if ($Uri -match '^https?://')    { return $Uri }
    if ($Uri -match '^/(beta|v1\.0)/') { return "$script:GraphHost$Uri" }
    return "$script:GraphRoot$Uri"
}

function Invoke-Graph {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PATCH', 'PUT', 'DELETE')][string] $Method,
        [Parameter(Mandatory)][string] $Uri,
        $Body,
        [int]    $MaxAttempts = 6,
        [switch] $TolerateNotFound
    )

    $Uri = Resolve-GraphUri -Uri $Uri

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
            if ($info.Status -eq 404 -and $TolerateNotFound) { return $null }

            if (($info.Status -in 429, 500, 502, 503, 504) -and $attempt -lt $MaxAttempts) {
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

# The registration APIs need the status code and the raw body of *failed* responses - a 409 carries
# the id of the pre-existing registration. This mirrors the CLI's GraphResponse type: it never
# throws on an HTTP error, it reports one.
function Invoke-GraphResponse {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PATCH', 'PUT', 'DELETE')][string] $Method,
        [Parameter(Mandatory)][string] $Uri,
        $Body
    )

    $Uri = Resolve-GraphUri -Uri $Uri

    $json = $null
    if ($null -ne $Body) {
        $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 25 }
    }

    try {
        $reqParams = @{
            Method      = $Method
            Uri         = $Uri
            Headers     = @{ 'OData-Version' = '4.0' }
            OutputType  = 'PSObject'
            ErrorAction = 'Stop'
        }
        if ($json) {
            $reqParams.Body        = $json
            $reqParams.ContentType = 'application/json'
        }
        $result = Invoke-MgGraphRequest @reqParams
        return [pscustomobject]@{
            IsSuccess = $true
            Status    = 200
            Body      = $result
            Code      = $null
            Message   = $null
        }
    }
    catch {
        $info = Get-GraphErrorInfo -ErrorRecord $_
        return [pscustomobject]@{
            IsSuccess = $false
            Status    = $(if ($null -ne $info.Status) { [int]$info.Status } else { 0 })
            Body      = $info.Body
            Code      = $info.Code
            Message   = $info.Message
        }
    }
}

function Write-Step {
    param([int]$Number, [string]$Text)
    Write-Host ''
    Write-Host "=== Step $Number : $Text" -ForegroundColor Cyan
}

function Test-IsGuid {
    param([string] $Value)
    return $Value -match '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$'
}

function ConvertTo-ODataLiteral {
    param([string] $Value)
    return ($Value -replace "'", "''")
}

# The service hands back a tenant-prefixed registration id ("T_<guid>") while the client posts a
# bare GUID as `id`, so a caller can legitimately hold either spelling. DELETE answers 404 for an
# unknown id, which is indistinguishable from "already gone" - so the other form is tried before
# concluding the registration was absent.
function Get-RegistrationIdVariant {
    param([Parameter(Mandatory)][string] $Id)
    if ($Id -match '^([A-Za-z][A-Za-z0-9]*)_(.+)$') { return $Matches[2] }
    if (Test-IsGuid $Id) { return "T_$Id" }
    return $null
}

# The service returns the registration object on 200 and, on 409, the object that already exists.
# Either way the id we want sits at the root.
function Get-IdFromBody {
    param($ResponseBody)
    if ($null -eq $ResponseBody) { return $null }
    if (-not (Test-HasProperty $ResponseBody 'id')) { return $null }
    $value = [string]$ResponseBody.id
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    return $value
}

# CLI rule: "Contoso Helpdesk Identity" -> "Contoso Helpdesk Agent". The registry lists agents,
# not identities, so the suffix would read wrong in the admin surfaces.
function ConvertTo-RegistrationDisplayName {
    param([Parameter(Mandatory)][string] $Name)
    if ($SkipDisplayNameNormalization) { return $Name }
    if ($Name -notmatch '\s[Ii]dentity$') { return $Name }
    return ($Name.Substring(0, $Name.Length - ' Identity'.Length).TrimEnd() + ' Agent')
}

# `ownerIds` / `createdBy` are user object ids - the CLI always stamps `me.id`. Under app-only
# there is no `/me`, so -Owner is resolved here instead. Accepts an object id, a UPN, a mail
# address or a display name.
#
# Returns $null rather than throwing when an entry simply is not a user: a service principal is a
# valid blueprint owner, so the same -Owner list can be passed to every step of the pipeline and
# the entries that cannot own a registration are skipped.
function Resolve-OwnerUser {
    param([Parameter(Mandatory)][string] $Identifier)

    $trimmed = $Identifier.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) { return $null }

    $encoded = [uri]::EscapeDataString($trimmed)

    # One call covers both object ids and UPNs.
    $user = Invoke-Graph -Method GET `
        -Uri "/users/$encoded`?`$select=id,userPrincipalName,displayName" -TolerateNotFound -MaxAttempts 3
    if ($user -and (Test-HasProperty $user 'id')) {
        return [pscustomobject]@{
            Id      = [string]$user.id
            Display = if (Test-HasProperty $user 'userPrincipalName') { [string]$user.userPrincipalName }
                      else { [string]$user.id }
        }
    }

    # A GUID that is not a user cannot be anything else here - name the type so the warning is
    # actionable instead of just "not found".
    if (Test-IsGuid $trimmed) {
        $obj  = Invoke-Graph -Method GET -Uri "/directoryObjects/$encoded" -TolerateNotFound -MaxAttempts 2
        $kind = 'an object of an unknown type'
        if ($obj -and (Test-HasProperty $obj '@odata.type')) {
            $kind = "a $(([string]$obj.'@odata.type') -replace '^#microsoft\.graph\.', '')"
        }
        elseif (-not $obj) {
            $kind = 'not present in this tenant'
        }
        Write-Warning "Owner '$trimmed' is $kind, not a user - skipped. A registration owner must be a user."
        return $null
    }

    $literal = ConvertTo-ODataLiteral $trimmed
    $filter  = "mail eq '$literal' or userPrincipalName eq '$literal' or displayName eq '$literal'"
    $found   = Invoke-Graph -Method GET `
        -Uri "/users?`$select=id,userPrincipalName,displayName&`$filter=$([uri]::EscapeDataString($filter))" `
        -TolerateNotFound -MaxAttempts 3

    $matched = @()
    if (Test-HasProperty $found 'value') { $matched = @($found.value) }

    if ($matched.Count -eq 1) {
        return [pscustomobject]@{
            Id      = [string]$matched[0].id
            Display = if (Test-HasProperty $matched[0] 'userPrincipalName') { [string]$matched[0].userPrincipalName }
                      else { [string]$matched[0].id }
        }
    }
    if ($matched.Count -gt 1) {
        Write-Warning "Owner '$trimmed' matched $($matched.Count) users - skipped. Pass the object id or the UPN instead."
        return $null
    }

    Write-Warning "Owner '$trimmed' could not be resolved to a user in tenant $TenantId - skipped."
    return $null
}

# ---------------------------------------------------------------------------
# Authentication
# ---------------------------------------------------------------------------

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

# Reads the appid (v1 tokens) or azp (v2 tokens) claim out of a JWT access token. Returns '' for
# anything that is not a readable JWT - this is a best-effort convenience, never a security check,
# and the signature is deliberately NOT validated because the token is one we already hold.
function Get-JwtAppId {
    [OutputType([string])]
    param([object] $Token)

    if ($null -eq $Token) { return '' }

    $raw = ''
    if ($Token -is [securestring]) {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Token)
        try   { $raw = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }
    else { $raw = [string]$Token }

    if ([string]::IsNullOrWhiteSpace($raw)) { return '' }

    $parts = $raw.Split('.')
    if ($parts.Count -lt 2) { return '' }

    try {
        $segment = $parts[1].Replace('-', '+').Replace('_', '/')
        switch ($segment.Length % 4) {
            2 { $segment += '==' }
            3 { $segment += '=' }
            1 { return '' }
        }
        $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($segment)) | ConvertFrom-Json
        foreach ($name in 'appid', 'azp') {
            if ((Test-HasProperty $claims $name) -and $claims.$name) { return [string]$claims.$name }
        }
    }
    catch {
        Write-Verbose "Could not decode the access token to read its appid claim: $($_.Exception.Message)"
    }

    return ''
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

    # The caller's own appId is the default for managedByAppId, so resolve it as reliably as
    # possible. Get-MgContext reports it for every mode except a raw -AccessToken, where the token's
    # own appid/azp claim is the only source.
    $script:CallerAppId = $ctxClient
    if ([string]::IsNullOrWhiteSpace($script:CallerAppId) -and $mode -eq 'AccessToken') {
        $script:CallerAppId = Get-JwtAppId -Token $AccessToken
        if ($script:CallerAppId) { $ctxClient = $script:CallerAppId }
    }
    if ([string]::IsNullOrWhiteSpace($script:CallerAppId)) {
        Write-Warning 'Could not determine the calling application id; managedByAppId will be omitted unless -ManagedByAppId is supplied.'
    }
    else {
        Write-Verbose "Caller appId resolved to $script:CallerAppId (used as managedByAppId)."
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
    }
}

# ---------------------------------------------------------------------------
# Registration primitives (contracts taken from the CLI assembly)
# ---------------------------------------------------------------------------

function Get-RegistrationBasePath {
    if ($Api -eq 'AgentRegistry') { return $script:AgentInstancesPath }
    return $script:CopilotRegistrationsPath
}

# GET {base}/{id}. Returns $true (exists), $false (404) or $null (inconclusive).
# The CLI is explicit that callers must NOT treat $null as "gone": re-registering on a transient
# read failure is how you end up with duplicates. Deliberately Get-* rather than Test-*, because
# the result is tri-state, not boolean.
function Get-RegistrationState {
    param([Parameter(Mandatory)][string] $Id)

    $uri = '{0}/{1}' -f (Get-RegistrationBasePath), [uri]::EscapeDataString($Id)
    $response = Invoke-GraphResponse -Method GET -Uri $uri

    if ($response.IsSuccess)     { return $true }
    if ($response.Status -eq 404) { return $false }

    Write-Verbose "Could not verify registration $Id (HTTP $($response.Status)); treating as unknown."
    return $null
}

function ConvertTo-RegistrationPayload {
    param(
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string[]] $OwnerIds,
        [string] $AgentDescription,
        [string] $Blueprint,
        [string] $Identity
    )

    if ($Api -eq 'AgentRegistry') {
        # POST /beta/agentRegistry/agentInstances - the service assigns the id here.
        $payload = [ordered]@{
            ownerIds    = @($OwnerIds)
            displayName = $Name
        }
        if (-not [string]::IsNullOrWhiteSpace($Blueprint)) {
            $payload['agentIdentityBlueprintId'] = $Blueprint
        }
        return $payload
    }

    # POST /beta/copilot/agentRegistrations - the client assigns the id, which is what makes a
    # retry after an ambiguous failure safe.
    $now = [DateTimeOffset]::UtcNow.ToString('o')
    $payload = [ordered]@{
        id                       = $Id
        displayName              = $Name
        ownerIds                 = @($OwnerIds)
        createdBy                = $OwnerIds[0]
        sourceCreatedDateTime    = $now
        sourceLastModifiedDateTime = $now
    }
    if (-not [string]::IsNullOrWhiteSpace($AgentDescription)) {
        $payload['description'] = $AgentDescription
    }
    if (-not [string]::IsNullOrWhiteSpace($Blueprint)) {
        $payload['agentIdentityBlueprintId'] = $Blueprint
    }

    # sourceAgentId is the service's duplicate key: agent identity first, blueprint as fallback.
    $payload['sourceAgentId'] = if (-not [string]::IsNullOrWhiteSpace($Identity)) { $Identity }
                                elseif (-not [string]::IsNullOrWhiteSpace($Blueprint)) { $Blueprint }
                                else { '' }

    if (-not [string]::IsNullOrWhiteSpace($Identity)) {
        $payload['agentIdentityId'] = $Identity
    }
    # managedByAppId records which application owns this registration. The service refuses a value
    # that is not the caller's own appId:
    #   HTTP 500 "You do not have permission to create an agent registration managed by another AppId."
    # (Graph wraps the downstream 403 as a 500 with code UnknownError.) The a365 CLI can pass the
    # AgentX first-party appId 59eca866-2f46-40b8-96ff-63f663121ef9 because that IS its own appId;
    # a custom automation app cannot, which is why that value is no longer the default. It falls back
    # to the caller's appId, resolved from the token/context at connect time.
    $managedBy = if (-not [string]::IsNullOrWhiteSpace($ManagedByAppId)) { $ManagedByAppId }
                 else { $script:CallerAppId }
    if (-not [string]::IsNullOrWhiteSpace($managedBy)) {
        $payload['managedByAppId'] = $managedBy
    }

    return $payload
}

function Invoke-RegistrationPost {
    param(
        [Parameter(Mandatory)]$Payload,
        [Parameter(Mandatory)][string] $FallbackId
    )

    $uri = Get-RegistrationBasePath

    # 502/503/504 are retried 3 times, 2s apart - the CLI's transient policy.
    $response      = $null
    $maxAttempts   = 3
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        $response = Invoke-GraphResponse -Method POST -Uri $uri -Body $Payload
        if ($response.Status -notin 502, 503, 504) { break }
        if ($attempt -eq $maxAttempts) { break }
        Write-Warning "  Registration returned HTTP $($response.Status) (transient); retrying..."
        Start-Sleep -Seconds 2
    }

    # The AgentRegistry API is gated on the "Agent Registry Administrator" Entra role, which can
    # take 5-15 minutes to propagate. The CLI retries a 403 exactly once after a delay.
    if ($Api -eq 'AgentRegistry' -and $response.Status -eq 403 -and $RolePropagationDelaySeconds -gt 0) {
        Write-Warning "  403 Forbidden. The 'Agent Registry Administrator' role may not have propagated yet."
        Write-Host   "  Waiting $RolePropagationDelaySeconds seconds before one retry..." -ForegroundColor Yellow
        Start-Sleep -Seconds $RolePropagationDelaySeconds
        $response = Invoke-GraphResponse -Method POST -Uri $uri -Body $Payload
    }

    # The service refuses managedByAppId from a caller it does not recognise as an agent-managing
    # application - including when the value sent IS the caller's own appId, which makes the error
    # text ("managed by another AppId") actively misleading. Verified live: the identical payload
    # with the property omitted is accepted. Retry once without it rather than leaving the caller
    # in a loop, because the default for that property is the very value being rejected.
    if (-not $response.IsSuccess -and
        $response.Message -match '(?i)permission to create an agent registration managed by another appid' -and
        $Payload -is [System.Collections.IDictionary] -and $Payload.Contains('managedByAppId')) {

        $rejected = [string]$Payload['managedByAppId']
        Write-Warning "  The service rejected managedByAppId '$rejected'; retrying without it."

        $retryPayload = [ordered]@{}
        foreach ($key in $Payload.Keys) {
            if ($key -eq 'managedByAppId') { continue }
            $retryPayload[$key] = $Payload[$key]
        }

        $retryResponse = Invoke-GraphResponse -Method POST -Uri $uri -Body $retryPayload
        if ($retryResponse.IsSuccess) {
            Write-Host '  Accepted without managedByAppId. The registration is not bound to a managing application.' -ForegroundColor Yellow
            $response = $retryResponse
        }
    }

    if ($response.IsSuccess) {
        $id = Get-IdFromBody -ResponseBody $response.Body
        if (-not $id) {
            # The copilot API echoes the client-generated id back; fall back to it when the
            # response body is empty. The AgentRegistry API has no fallback - it owns the id.
            if ($Api -eq 'AgentRegistry') {
                Write-Warning '  Registration succeeded but the response contained no id.'
            }
            else {
                $id = $FallbackId
            }
        }
        return [pscustomobject]@{ Id = $id; AlreadyExisted = $false; Status = $response.Status; Message = $null }
    }

    if ($response.Status -eq 409) {
        # Duplicate sourceAgentId. The body carries the pre-existing registration.
        $existing = Get-IdFromBody -ResponseBody $response.Body
        if ($existing) {
            return [pscustomobject]@{ Id = $existing; AlreadyExisted = $true; Status = 409; Message = $null }
        }
        return [pscustomobject]@{
            Id             = $null
            AlreadyExisted = $true
            Status         = 409
            Message        = "Already registered, but the 409 response did not include an 'id'. Find the registration id in the Microsoft 365 admin center and record it as 'agentRegistrationId'."
        }
    }

    $message = if ($response.Status -eq 403) {
        if ($Api -eq 'AgentRegistry') {
            "403 Forbidden. Ensure the caller holds the 'Agent Registry Administrator' Entra role and the token carries AgentInstance.ReadWrite.All."
        }
        else {
            "403 Forbidden. Ensure the token carries AgentRegistration.ReadWrite.All, the caller holds the required Entra role, and the tenant is enrolled in the required preview program. $($response.Message)"
        }
    }
    elseif ($response.Message -match '(?i)permission to create an agent registration managed by another appid') {
        # Graph reports this downstream authorization failure as a 500/UnknownError, so it must be
        # matched on the message rather than the status code.
        $sent = if ($Payload -is [System.Collections.IDictionary] -and $Payload.Contains('managedByAppId')) { [string]$Payload['managedByAppId'] } else { '(none)' }
        $caller = if ($script:CallerAppId) { $script:CallerAppId } else { 'unknown' }

        if ($sent -ceq $caller) {
            # The message says "another AppId" but the value sent was the caller's own, so the real
            # constraint is that this application may not claim managedByAppId at all. Saying
            # "re-run without -ManagedByAppId" here would be useless: the default IS this value.
            "HTTP $($response.Status). The service refused managedByAppId '$sent' even though that is this caller's own appId, and the retry without the property also failed. This application is not permitted to manage agent registrations. Register from an application that is (the a365 CLI, or an app onboarded for agent management), or omit the managing application entirely."
        }
        else {
            $advice = "The service only accepts a registration whose managedByAppId is the calling application's own appId. Sent managedByAppId '$sent'; the caller is '$caller'."
            if ($sent -eq '59eca866-2f46-40b8-96ff-63f663121ef9') {
                $advice += " That value is the a365 CLI's own first-party AgentX appId and cannot be claimed by a custom application."
            }
            "HTTP $($response.Status). $advice Re-run without -ManagedByAppId so it defaults to the caller."
        }
    }
    else {
        "HTTP $($response.Status). $($response.Message)"
    }

    return [pscustomobject]@{ Id = $null; AlreadyExisted = $false; Status = $response.Status; Message = $message }
}

# ---------------------------------------------------------------------------
# Step 1 - connect
# ---------------------------------------------------------------------------
Write-Step 1 'Connecting to Microsoft Graph'

# The registration scopes are preview surface and are NOT consented on the stock Microsoft Graph
# PowerShell client app - pair -Interactive with -ClientId for an app that has them.
$delegatedScopes = [System.Collections.Generic.List[string]]::new()
if ($Api -eq 'AgentRegistry') {
    $delegatedScopes.Add('AgentInstance.ReadWrite.All')      # POST /beta/agentRegistry/agentInstances
}
else {
    $delegatedScopes.Add('AgentRegistration.ReadWrite.All')  # POST /beta/copilot/agentRegistrations
}
$delegatedScopes.AddRange([string[]]@(
    'User.Read',                # GET /v1.0/me -> ownerIds / createdBy
    'AgentIdentity.Read.All'    # -FromBlueprint enumeration
))
if ($Owner -and -not $OwnerId) {
    # Reading a user other than the caller needs more than User.Read.
    $delegatedScopes.Add('User.ReadBasic.All')   # GET /users/{upn} -> owner object ids
}

# The registration permissions ARE published as application roles on Microsoft Graph - verified
# against the live servicePrincipal for appId 00000003-0000-0000-c000-000000000000:
#   AgentRegistration.ReadWrite.All   39fb8c64-7bd3-4107-8515-14d6e55ddda4
#   AgentInstance.ReadWrite.All       07abdd95-78dc-4353-bd32-09f880ea43d0
# so app-only is a first-class path and the required role is pre-flighted like any other.
$appRoles = [System.Collections.Generic.List[string]]::new()
$appRoles.Add($(if ($Api -eq 'AgentRegistry') { 'AgentInstance.ReadWrite.All' } else { 'AgentRegistration.ReadWrite.All' }))
$appRoles.AddRange([string[]]@(
    'AgentIdentity.Read.All',
    'User.Read.All'
))

$connectArgs = @{
    TenantId            = $TenantId
    DelegatedScope      = $delegatedScopes.ToArray()
    RequiredAppRole     = $appRoles.ToArray()
    SkipPermissionCheck = $SkipPermissionCheck
}
foreach ($name in 'ClientId', 'ClientSecret', 'CertificateThumbprint', 'Certificate',
                  'CertificatePath', 'CertificatePassword', 'UseManagedIdentity',
                  'AccessToken', 'Interactive') {
    if ($PSBoundParameters.ContainsKey($name)) { $connectArgs[$name] = $PSBoundParameters[$name] }
}

$ctx = Connect-GraphSession @connectArgs

Write-Host "  Registration API : $Api" -ForegroundColor Gray
Write-Host "  Endpoint         : $script:GraphHost$(Get-RegistrationBasePath)" -ForegroundColor Gray

if ($ctx.IsAppOnly) {
    Write-Host "  managedByAppId   : $(if ($ManagedByAppId) { "$ManagedByAppId (explicit)" } else { "$($script:CallerAppId) (caller)" })" -ForegroundColor Gray
    if (-not $OwnerId -and -not $Owner -and -not $Unregister) {
        Write-Warning 'App-only runs cannot call /me, so pass -Owner (UPNs, mail addresses or display names, resolved through Graph) or -OwnerId (a user object id).'
    }
}
else {
    Write-Host "  managedByAppId   : $(if ($ManagedByAppId) { "$ManagedByAppId (explicit)" } else { "$($script:CallerAppId) (caller)" })" -ForegroundColor Gray
}

if ($ManagedByAppId -and $script:CallerAppId -and $ManagedByAppId -ne $script:CallerAppId) {
    Write-Warning "-ManagedByAppId ($ManagedByAppId) differs from the calling application ($($script:CallerAppId)). The service rejects registrations managed by another appId with HTTP 500 'You do not have permission to create an agent registration managed by another AppId.' Omit -ManagedByAppId unless this app is genuinely privileged to claim that value."
}

# ---------------------------------------------------------------------------
# Step 2 - resolve the owner
# ---------------------------------------------------------------------------
Write-Step 2 'Resolving the registration owner'

# ownerIds/createdBy carry user object ids. Precedence: -OwnerId (explicit single owner),
# then -Owner (resolved through Graph), then /me (delegated only).
$ownerObjectIds = @()
$ownerSource    = $null

if ($Unregister) {
    # DELETE carries no payload, so an owner is neither needed nor worth a directory lookup.
    $ownerSource = 'n/a (unregister)'
    Write-Host '  Not required for -Unregister.' -ForegroundColor Gray
}
elseif ($Update -and -not $OwnerId -and -not $Owner) {
    # An update writes only what was asked for, so no owner change means no owner lookup - and no
    # /me fallback, which would otherwise silently rewrite ownerIds to the signed-in user.
    $ownerSource = 'n/a (update; no owner change requested)'
    Write-Host '  Not required: -Update writes only the properties you supply.' -ForegroundColor Gray
}
elseif ($OwnerId) {
    $ownerSource = '-OwnerId'
    if (Test-IsGuid $OwnerId) {
        $ownerObjectIds = @($OwnerId)
    }
    else {
        # Accept a UPN for convenience; the payload requires an object id.
        $user = Invoke-Graph -Method GET -Uri "/users/$([uri]::EscapeDataString($OwnerId))?`$select=id,userPrincipalName" `
            -TolerateNotFound
        if (-not (Test-HasProperty $user 'id')) { throw "Owner '$OwnerId' could not be resolved to a user." }
        $ownerObjectIds = @([string]$user.id)
    }
    Write-Host "  Owner: $($ownerObjectIds[0]) (from -OwnerId)" -ForegroundColor Green

    if ($Owner) {
        Write-Verbose '-OwnerId was supplied, so -Owner is ignored.'
    }
}
elseif ($Owner) {
    $ownerSource = '-Owner'
    Write-Host "  Resolving $($Owner.Count) owner(s) from -Owner through Graph." -ForegroundColor Gray

    foreach ($candidate in $Owner) {
        $resolved = Resolve-OwnerUser -Identifier $candidate
        if (-not $resolved) { continue }
        if ($ownerObjectIds -contains $resolved.Id) {
            Write-Verbose "Owner '$candidate' resolved to $($resolved.Id), which is already in the list."
            continue
        }
        $ownerObjectIds += $resolved.Id
        Write-Host "  Owner: $($resolved.Display) ($($resolved.Id))" -ForegroundColor Green
    }

    if ($ownerObjectIds.Count -eq 0) {
        # Delegated callers still have /me to fall back on; app-only callers do not.
        if ($ctx.IsAppOnly) {
            throw ("None of the -Owner values resolved to a user, and app-only authentication has no " +
                   "/me to fall back on. Pass -OwnerId with a user object id, or check that the token " +
                   "carries User.Read.All so the directory lookups above can succeed.")
        }
        Write-Warning 'None of the -Owner values resolved to a user - falling back to the signed-in user.'
        $ownerSource = '/me (no -Owner value resolved)'
        $me = Invoke-Graph -Method GET -Uri '/me?$select=id,userPrincipalName'
        if (-not (Test-HasProperty $me 'id')) {
            throw 'Failed to retrieve the signed-in user id from GET /v1.0/me - required for agent registration.'
        }
        $ownerObjectIds = @([string]$me.id)
        Write-Host "  Owner: $($ownerObjectIds[0])" -ForegroundColor Green
    }
    elseif ($ownerObjectIds.Count -gt 1) {
        Write-Host "  createdBy: $($ownerObjectIds[0]) (first resolved owner)" -ForegroundColor Gray
    }
}
elseif ($ctx.IsAppOnly) {
    throw ("An owner is required under app-only authentication: GET /v1.0/me does not resolve without a " +
           "signed-in user, and the registration payload requires ownerIds and createdBy. Pass -Owner " +
           "with one or more UPNs, mail addresses or display names to have them resolved through Graph, " +
           "or -OwnerId with a user object id.")
}
else {
    $ownerSource = '/me'
    $me = Invoke-Graph -Method GET -Uri '/me?$select=id,userPrincipalName'
    if (-not (Test-HasProperty $me 'id')) {
        throw 'Failed to retrieve the signed-in user id from GET /v1.0/me - required for agent registration.'
    }
    $ownerObjectIds = @([string]$me.id)
    $upn = if (Test-HasProperty $me 'userPrincipalName') { [string]$me.userPrincipalName } else { $ownerObjectIds[0] }
    Write-Host "  Owner: $upn ($($ownerObjectIds[0]))" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Step 3 - unregister short-circuit
# ---------------------------------------------------------------------------
if ($Unregister) {
    Write-Step 3 'Removing the agent registration'

    $deleteUri = '{0}/{1}' -f (Get-RegistrationBasePath), [uri]::EscapeDataString($RegistrationId)

    if ($PSCmdlet.ShouldProcess($RegistrationId, "DELETE $deleteUri")) {
        $response = Invoke-GraphResponse -Method DELETE -Uri $deleteUri
        $deletedId = $RegistrationId

        # 404 may just mean the id is spelled the other way; try that before reporting absence.
        if ($response.Status -eq 404) {
            $variant = Get-RegistrationIdVariant -Id $RegistrationId
            if ($variant) {
                Write-Verbose "DELETE of '$RegistrationId' returned 404; retrying as '$variant'."
                $variantUri = '{0}/{1}' -f (Get-RegistrationBasePath), [uri]::EscapeDataString($variant)
                $variantResponse = Invoke-GraphResponse -Method DELETE -Uri $variantUri
                if ($variantResponse.IsSuccess) {
                    $response  = $variantResponse
                    $deletedId = $variant
                }
            }
        }

        if ($response.IsSuccess -or $response.Status -eq 404) {
            $note = if ($response.Status -eq 404) { ' (already absent)' } else { '' }
            Write-Host "  Registration $deletedId deleted$note." -ForegroundColor Green
        }
        else {
            throw "Failed to delete registration $RegistrationId [HTTP $($response.Status)]: $($response.Message)"
        }
    }

    Write-Host ''
    Write-Host 'Done.' -ForegroundColor Green
    return
}

# ---------------------------------------------------------------------------
# Step 3 - update an existing registration
# ---------------------------------------------------------------------------
# PATCH semantics were established empirically against the live service, not from documentation
# (this endpoint is undocumented /beta):
#   * PATCH merges per-property - patching displayName leaves description, ownerIds and
#     sourceAgentId untouched. There is no read-modify-write to do.
#   * displayName, description, ownerIds, agentIdentityId, agentIdentityBlueprintId and
#     sourceAgentId all persist. ownerIds replaces the whole array.
#   * managedByAppId is refused (the service only ever accepts the caller's own appId).
if ($Update) {
    Write-Step 3 'Updating the agent registration'

    if ($Api -eq 'AgentRegistry') {
        throw '-Update is only supported for -Api CopilotAgentRegistrations. The AgentRegistry API owns its own object lifecycle.'
    }
    if ($PSBoundParameters.ContainsKey('ManagedByAppId')) {
        throw 'managedByAppId cannot be changed after creation - the service only accepts the calling application''s own appId and rejects the PATCH. Re-register the agent from the application that should manage it.'
    }

    # The service assigns its own 'T_'-prefixed id and ignores the one the client sends, so the
    # id given here may be either form. Try it as supplied, then the other spelling.
    $resolvedRegistrationId = $RegistrationId
    $registrationUri = '{0}/{1}' -f (Get-RegistrationBasePath), [uri]::EscapeDataString($resolvedRegistrationId)
    $current = Invoke-GraphResponse -Method GET -Uri $registrationUri

    if ($current.Status -eq 404) {
        $variant = Get-RegistrationIdVariant -Id $RegistrationId
        if ($variant) {
            Write-Verbose "GET of '$RegistrationId' returned 404; retrying as '$variant'."
            $variantUri = '{0}/{1}' -f (Get-RegistrationBasePath), [uri]::EscapeDataString($variant)
            $variantResponse = Invoke-GraphResponse -Method GET -Uri $variantUri
            if ($variantResponse.IsSuccess) {
                $current = $variantResponse
                $resolvedRegistrationId = $variant
                $registrationUri = $variantUri
            }
        }
    }

    if (-not $current.IsSuccess) {
        # $current.Message carries the full raw HTTP trace, which drowns the guidance below.
        # The status code is the only part that helps here.
        throw @"
Registration '$RegistrationId' could not be read (HTTP $($current.Status)).

The id to pass is the one the service returned when the agent was registered - it starts with 'T_'
(for example T_f9955348-7fb4-6143-49fb-a0f695211ff4), NOT the agent identity or blueprint id. The
service assigns that id itself and ignores any id supplied at creation time, so a client-generated
GUID will never resolve.

List your registrations in the Microsoft 365 admin center to find it.
"@
    }

    $existing = $current.Body
    $readValue = {
        param([string] $Name)
        if (Test-HasProperty $existing $Name) { return $existing.$Name }
        return $null
    }

    Write-Host "  Registration : $resolvedRegistrationId" -ForegroundColor Green
    Write-Host "  Display name : $(& $readValue 'displayName')" -ForegroundColor Gray
    $existingOwners = @(& $readValue 'ownerIds' | Where-Object { $_ })
    Write-Host "  Owners       : $(if ($existingOwners.Count -gt 0) { $existingOwners -join ', ' } else { '(none)' })" -ForegroundColor Gray

    # Only properties actually supplied on the command line are written. Comparisons are
    # case-sensitive so a casing-only change is not silently discarded.
    $registrationPatch = [ordered]@{}

    if ($PSBoundParameters.ContainsKey('DisplayName')) {
        $desiredName = ConvertTo-RegistrationDisplayName -Name $DisplayName
        if ($desiredName -cne [string](& $readValue 'displayName')) { $registrationPatch['displayName'] = $desiredName }
    }
    if ($PSBoundParameters.ContainsKey('Description')) {
        if ($Description -cne [string](& $readValue 'description')) { $registrationPatch['description'] = $Description }
    }
    if ($PSBoundParameters.ContainsKey('AgentIdentityId')) {
        if ($AgentIdentityId -cne [string](& $readValue 'agentIdentityId')) { $registrationPatch['agentIdentityId'] = $AgentIdentityId }
    }
    if ($PSBoundParameters.ContainsKey('BlueprintAppId')) {
        if ($BlueprintAppId -cne [string](& $readValue 'agentIdentityBlueprintId')) { $registrationPatch['agentIdentityBlueprintId'] = $BlueprintAppId }
    }
    if ($ownerObjectIds.Count -gt 0) {
        # ownerIds is replace-not-merge, so this deliberately sets the whole list.
        if (Compare-Object -ReferenceObject @($existingOwners) -DifferenceObject @($ownerObjectIds) -CaseSensitive) {
            $registrationPatch['ownerIds'] = @($ownerObjectIds)
        }
    }

    if ($registrationPatch.Count -eq 0) {
        Write-Host '  Nothing to change - every supplied value already matches.' -ForegroundColor Green
        Write-Host ''
        Write-Host 'Done.' -ForegroundColor Green
        return
    }

    $patchSummary = ($registrationPatch.Keys -join ', ')
    Write-Host "  Updating: $patchSummary" -ForegroundColor Gray

    $updatedProperties = @()
    if ($PSCmdlet.ShouldProcess($resolvedRegistrationId, "PATCH $patchSummary")) {
        $patchResponse = Invoke-GraphResponse -Method PATCH -Uri $registrationUri -Body $registrationPatch
        if (-not $patchResponse.IsSuccess) {
            throw "Failed to update registration $resolvedRegistrationId [HTTP $($patchResponse.Status)]: $($patchResponse.Message)"
        }

        # A 200 from this API is not proof the value stuck - inheritablePermissions in the blueprint
        # script returns 201 on writes that are silently discarded - so read the object back.
        Start-Sleep -Seconds 3
        $verify = Invoke-GraphResponse -Method GET -Uri $registrationUri
        if ($verify.IsSuccess) {
            $after = $verify.Body
            foreach ($key in $registrationPatch.Keys) {
                $expected = @($registrationPatch[$key]) -join ' '
                $actual = if (Test-HasProperty $after $key) { (@($after.$key) -join ' ') } else { '' }
                if ($actual -ceq $expected) {
                    $updatedProperties += $key
                    Write-Host "  Verified $key -> '$actual'" -ForegroundColor Green
                }
                else {
                    Write-Warning "  $key did not persist: expected '$expected' but the service still reports '$actual'."
                }
            }
        }
        else {
            Write-Warning "  Update returned HTTP $($patchResponse.Status) but the registration could not be read back to confirm it [HTTP $($verify.Status)]. Re-run to verify."
        }
    }

    Write-Host ''
    Write-Host '========================================================' -ForegroundColor Green
    Write-Host ' Agent registration update complete' -ForegroundColor Green
    Write-Host '========================================================' -ForegroundColor Green
    Write-Host ("  Registration ID    : {0}" -f $resolvedRegistrationId)
    Write-Host ("  Properties updated : {0}" -f $(if ($updatedProperties.Count -gt 0) { $updatedProperties -join ', ' } else { 'none' }))
    Write-Host ''
    Write-Host 'Done.' -ForegroundColor Green

    # Emit the same shape the create path emits, so callers - the orchestrator in particular -
    # can read RegistrationId/Status from an update exactly as they do from a create.
    $updatedList = @($updatedProperties)
    $finalName = if ($registrationPatch.Contains('displayName')) { $registrationPatch['displayName'] } else { [string](& $readValue 'displayName') }
    [pscustomobject]@{
        RegistrationId     = $resolvedRegistrationId
        DisplayName        = $finalName
        Status             = if ($updatedList.Count -gt 0) { 'Updated' } else { 'NoChange' }
        AgentIdentityId    = [string](& $readValue 'agentIdentityId')
        BlueprintAppId     = [string](& $readValue 'agentIdentityBlueprintId')
        UpdatedProperties  = $updatedList
    }
    return
}

# ---------------------------------------------------------------------------
# Step 3 - build the list of agents to register
# ---------------------------------------------------------------------------
Write-Step 3 'Building the registration list'

$targets = [System.Collections.Generic.List[object]]::new()

if ($FromBlueprint) {
    $filter = "agentIdentityBlueprintId eq '$(ConvertTo-ODataLiteral $BlueprintAppId)'"
    $uri = "/beta/servicePrincipals/microsoft.graph.agentIdentity?`$filter=$([uri]::EscapeDataString($filter))&`$select=id,displayName"

    $found = [System.Collections.Generic.List[object]]::new()
    while ($uri) {
        $page = Invoke-Graph -Method GET -Uri $uri
        if (Test-HasProperty $page 'value') {
            foreach ($identity in @($page.value)) {
                if ($null -ne $identity) { $found.Add($identity) }
            }
        }
        $uri = if (Test-HasProperty $page '@odata.nextLink') { [string]$page.'@odata.nextLink' } else { $null }
    }

    if ($found.Count -eq 0) {
        throw "No agent identities found for blueprint appId '$BlueprintAppId'. Create one with New-A365AgentIdentity.ps1 first."
    }

    foreach ($identity in $found) {
        $identityId   = if (Test-HasProperty $identity 'id') { [string]$identity.id } else { $null }
        if (-not $identityId) { continue }
        $identityName = if (Test-HasProperty $identity 'displayName') { [string]$identity.displayName } else { $identityId }

        $targets.Add([pscustomobject]@{
            DisplayName     = ConvertTo-RegistrationDisplayName -Name $identityName
            Description     = $Description
            AgentIdentityId = $identityId
            BlueprintAppId  = $BlueprintAppId
            RegistrationId  = $null
        })
    }

    Write-Host "  Found $($targets.Count) agent identity/identities under blueprint $BlueprintAppId." -ForegroundColor Green
}
elseif ($Agent) {
    $index = 0
    foreach ($entry in $Agent) {
        $index++
        if (-not $entry.ContainsKey('DisplayName') -or [string]::IsNullOrWhiteSpace([string]$entry['DisplayName'])) {
            throw "-Agent entry #$index is missing a non-empty 'DisplayName' key."
        }
        $recognised = 'DisplayName', 'Description', 'AgentIdentityId', 'BlueprintAppId', 'RegistrationId'
        $unknown = @($entry.Keys | Where-Object { $recognised -notcontains $_ })
        if ($unknown.Count -gt 0) {
            throw "-Agent entry #$index has unrecognised key(s): $($unknown -join ', '). Recognised keys: $($recognised -join ', ')."
        }

        $targets.Add([pscustomobject]@{
            DisplayName     = ConvertTo-RegistrationDisplayName -Name ([string]$entry['DisplayName'])
            Description     = if ($entry.ContainsKey('Description'))     { [string]$entry['Description'] }     else { $null }
            AgentIdentityId = if ($entry.ContainsKey('AgentIdentityId')) { [string]$entry['AgentIdentityId'] } else { $null }
            BlueprintAppId  = if ($entry.ContainsKey('BlueprintAppId'))  { [string]$entry['BlueprintAppId'] }  else { $BlueprintAppId }
            RegistrationId  = if ($entry.ContainsKey('RegistrationId'))  { [string]$entry['RegistrationId'] }  else { $null }
        })
    }
    Write-Host "  $($targets.Count) agent(s) queued from -Agent." -ForegroundColor Green
}
else {
    $normalised = ConvertTo-RegistrationDisplayName -Name $DisplayName
    if ($normalised -ne $DisplayName) {
        Write-Host "  Display name normalised: '$DisplayName' -> '$normalised'" -ForegroundColor Gray
    }
    $targets.Add([pscustomobject]@{
        DisplayName     = $normalised
        Description     = $Description
        AgentIdentityId = $AgentIdentityId
        BlueprintAppId  = $BlueprintAppId
        RegistrationId  = $RegistrationId
    })
    Write-Host "  1 agent queued: $normalised" -ForegroundColor Green
}

if ($Api -eq 'CopilotAgentRegistrations') {
    $noSource = @($targets | Where-Object {
        [string]::IsNullOrWhiteSpace($_.AgentIdentityId) -and [string]::IsNullOrWhiteSpace($_.BlueprintAppId)
    })
    if ($noSource.Count -gt 0) {
        Write-Warning "$($noSource.Count) registration(s) have neither an agent identity nor a blueprint, so 'sourceAgentId' will be empty and the service cannot detect duplicates. Supply -AgentIdentityId or -BlueprintAppId."
    }
}

# ---------------------------------------------------------------------------
# Step 4 - register
# ---------------------------------------------------------------------------
Write-Step 4 'Registering agents'

$results = [System.Collections.Generic.List[object]]::new()

foreach ($target in $targets) {
    Write-Host ''
    Write-Host "  -> $($target.DisplayName)" -ForegroundColor White

    $status         = 'Registered'
    $resolvedId     = $null
    $failureMessage = $null

    # Idempotency probe. Inconclusive (null) keeps the stored id rather than risking a duplicate.
    if ($target.RegistrationId -and -not $Force) {
        $exists = Get-RegistrationState -Id $target.RegistrationId
        if ($exists -eq $true) {
            $resolvedId = $target.RegistrationId
            $status     = 'AlreadyRegistered'
            Write-Host "     Already registered (id: $resolvedId). Skipping." -ForegroundColor Green
        }
        elseif ($exists -eq $false) {
            Write-Host "     Stored registration id $($target.RegistrationId) no longer exists; creating a new one." -ForegroundColor Yellow
        }
        else {
            $resolvedId = $target.RegistrationId
            $status     = 'Unverified'
            Write-Warning "     Could not verify registration $($target.RegistrationId) (auth or transient error); retaining the stored id. Re-run with -Force to register regardless."
        }
    }

    if (-not $resolvedId) {
        $newId   = [guid]::NewGuid().ToString()
        $payload = ConvertTo-RegistrationPayload -Id $newId -Name $target.DisplayName -OwnerIds $ownerObjectIds `
            -AgentDescription $target.Description -Blueprint $target.BlueprintAppId -Identity $target.AgentIdentityId

        $action = "POST $script:GraphHost$(Get-RegistrationBasePath) ($($target.DisplayName))"
        if ($PSCmdlet.ShouldProcess($target.DisplayName, $action)) {
            $outcome = Invoke-RegistrationPost -Payload $payload -FallbackId $newId

            if ($outcome.Id) {
                $resolvedId = $outcome.Id
                if ($outcome.AlreadyExisted) {
                    $status = 'AlreadyRegistered'
                    Write-Host "     Already registered (id: $resolvedId). Skipping." -ForegroundColor Green
                }
                else {
                    Write-Host "     Registered (id: $resolvedId)" -ForegroundColor Green
                }
            }
            elseif ($outcome.AlreadyExisted) {
                $status         = 'AlreadyRegisteredIdUnknown'
                $failureMessage = $outcome.Message
                Write-Warning "     $($outcome.Message)"
            }
            else {
                $status         = 'Failed'
                $failureMessage = $outcome.Message
                Write-Warning "     Registration failed: $($outcome.Message)"
            }
        }
        else {
            $status = 'Skipped (WhatIf)'
        }
    }

    $results.Add([pscustomobject]@{
        DisplayName     = $target.DisplayName
        Description     = $target.Description
        AgentIdentityId = $target.AgentIdentityId
        BlueprintAppId  = $target.BlueprintAppId
        RegistrationId  = $resolvedId
        Status          = $status
        Message         = $failureMessage
    })
}

# ---------------------------------------------------------------------------
# Step 5 - summary
# ---------------------------------------------------------------------------
Write-Step 5 'Summary'

$succeeded = @($results | Where-Object { $_.Status -eq 'Registered' })
$existing  = @($results | Where-Object { $_.Status -eq 'AlreadyRegistered' })
$failed    = @($results | Where-Object { $_.Status -eq 'Failed' })

Write-Host ''
Write-Host "  Registered      : $($succeeded.Count)" -ForegroundColor Green
Write-Host "  Already present : $($existing.Count)"  -ForegroundColor Gray
if ($failed.Count -gt 0) {
    Write-Host "  Failed          : $($failed.Count)" -ForegroundColor Red
}

$results | Select-Object DisplayName, RegistrationId, Status | Format-Table -AutoSize | Out-Host

$summary = [ordered]@{
    tenantId       = $ctx.TenantId
    api            = $Api
    endpoint       = "$script:GraphHost$(Get-RegistrationBasePath)"
    authMode       = $ctx.Mode
    ownerId        = if ($ownerObjectIds.Count -gt 0) { $ownerObjectIds[0] } else { $null }
    ownerIds       = @($ownerObjectIds)
    ownerSource    = $ownerSource
    generatedUtc   = [DateTimeOffset]::UtcNow.ToString('o')
    registrations  = @($results)
}

if ($OutputPath) {
    $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    Write-Host "  Summary written to $OutputPath" -ForegroundColor Green
}

if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Host 'Next steps for the failures:' -ForegroundColor Yellow
    Write-Host '  * 403 - confirm the token carries the registration permission and that the caller' -ForegroundColor Gray
    Write-Host '          holds the required Entra role. Role changes can take 5-15 minutes to' -ForegroundColor Gray
    Write-Host '          propagate; wait, then re-run.' -ForegroundColor Gray
    Write-Host '  * 400 - confirm -AgentIdentityId is the agent identity service principal object id' -ForegroundColor Gray
    Write-Host '          and -BlueprintAppId is the blueprint application (client) id.' -ForegroundColor Gray
    Write-Host '  * These endpoints are /beta preview surface and may require tenant enrolment.' -ForegroundColor Gray
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green

$results

Complete-A365Log -Outcome 'Succeeded'