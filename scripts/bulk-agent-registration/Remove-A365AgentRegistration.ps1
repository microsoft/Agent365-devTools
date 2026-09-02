<#
.SYNOPSIS
    Unregisters a Microsoft Agent 365 agent so it stops appearing in the Microsoft 365 admin
    center, using Microsoft Graph only. No a365 CLI.

.DESCRIPTION
    SINGLE RESPONSIBILITY. This script removes the agent REGISTRATION and nothing else. It does
    not delete the agent identity, the agent user, or the blueprint. Each of those is a separate
    directory object with its own script:

        Remove-A365AgentIdentity.ps1      the agent identity service principal
        Remove-A365AgentUser.ps1          the agent user (mailbox, UPN)
        Remove-A365Blueprint.ps1          the blueprint, shared by every identity under it

    Two registration surfaces exist, depending on which API registered the agent, and both are
    handled here because both are the same thing - the agent's entry in the admin center:

        1. Agent registration  DELETE /beta/copilot/agentRegistrations/{registrationId}
                               The usual case, and what `a365 setup all` creates.
        2. Agent instance      DELETE /beta/agentRegistry/agentInstances/{instanceId}
                               Only exists when the agent was registered through the Agent
                               Registry API instead of the copilot API.

    UNREGISTERING IS NOT THE WHOLE JOB. Removing the registration takes the agent out of the
    admin center inventory, but the identity and the agent user survive it: the agent user keeps
    its licence, its mailbox and its user principal name. To retire an agent completely, run the
    other scripts too, and in this order:

        .\Remove-A365AgentRegistration.ps1 -RegistrationId T_...
        .\Remove-A365AgentUser.ps1         -AgentUserId ...
        .\Remove-A365AgentIdentity.ps1     -AgentIdentityId ...

    The agent user comes before the identity because it is discovered THROUGH the identity;
    delete the identity first and the user is orphaned.

    NEITHER ID IS DISCOVERABLE. /beta/copilot/agentRegistrations has no collection GET - a list
    request answers 404, verified against the live service - and the Agent Registry has no
    readable GET for a single instance in this tenant shape. Keep the id that registration
    returned, or read it from the admin center. Deleting identities and users alone leaves a
    stale entry behind, which is why this script exists separately.

.PARAMETER RegistrationId
    One or more agent registration ids to delete. This is the id the SERVICE returned when the
    agent was registered, usually 'T_' prefixed. Both the prefixed and the bare GUID spelling
    are accepted and resolved automatically.

.PARAMETER AgentInstanceId
    One or more Agent Registry instance ids, for agents registered through
    POST /beta/agentRegistry/agentInstances.

.PARAMETER Agent
    Hashtable array for bulk removal, one entry per agent. Recognised keys: DisplayName,
    RegistrationId, AgentInstanceId. DisplayName is used only for reporting.

.PARAMETER InspectOnly
    Resolve and report every target, then exit without deleting anything.

.PARAMETER ContinueOnError
    Keep going after a failed delete instead of stopping. Failures are collected and reported,
    and the script still exits non-zero.

.PARAMETER Force
    Suppress confirmation prompts. Required for unattended runs.

.PARAMETER TenantId
    Directory (tenant) id. Optional for -Interactive / -UseDeviceCode; required for application
    authentication.

.EXAMPLE
    .\Remove-A365AgentRegistration.ps1 -RegistrationId T_f9955348-7fb4-6143-49fb-a0f695211ff4 `
        -Interactive -InspectOnly

    Report what would be removed, and remove nothing.

.EXAMPLE
    .\Remove-A365AgentRegistration.ps1 -RegistrationId T_f9955348-7fb4-6143-49fb-a0f695211ff4 `
        -Interactive -Force

    Unregister one agent. Its identity and agent user are left in place.

.EXAMPLE
    .\Remove-A365AgentRegistration.ps1 -Agent @(
        @{ DisplayName = 'Finance Agent'; RegistrationId = 'T_1111...' },
        @{ DisplayName = 'HR Agent';      RegistrationId = 'T_2222...' }
    ) -Force -ContinueOnError -Interactive

    Bulk unregistration. -ContinueOnError keeps going past a failure and reports at the end.

.NOTES
    The registry APIs read ownerIds and createdBy from /me, so an app-only token often cannot
    drive them. Use -Interactive if an app-only run is refused.

    A DELETE for a registration that does not exist returns success, so a clean exit is not by
    itself proof that anything was removed. -InspectOnly reports what could actually be read.

    Throttling from this API arrives disguised as HTTP 500 with "Too Many Requests" in the body.
#>

#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string[]] $RegistrationId,
    [string[]] $AgentInstanceId,
    [hashtable[]] $Agent,


    [switch]   $InspectOnly,
    [switch]   $ContinueOnError,
    [switch]   $Force,

    [string]   $TenantId,
    [switch]   $Interactive,
    [switch]   $UseDeviceCode,
    [switch]   $UseExistingConnection,
    [string]   $ClientId,
    [object]   $ClientSecret,
    [string]   $CertificateThumbprint,
    [object]   $AccessToken,

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
$null = Initialize-A365Log -Path $LogPath -ScriptName 'Remove-A365AgentRegistration.ps1' `
    -BoundParameters $PSBoundParameters -IncludeSecrets:$LogIncludeSecrets -CorrelationId $LogCorrelationId
if ($script:LogFile) { Write-Host "  Log file           : $($script:LogFile)" -ForegroundColor DarkGray }

trap {
    Write-A365Log -Level ERROR -Message "UNHANDLED: $($_.Exception.Message)" -Detail $_.ScriptStackTrace
    Complete-A365Log -Outcome 'Failed'
    break
}

# The A365 surfaces (agentRegistrations, agentRegistry, the agentIdentity and agentUser casts)
# are beta-only, so the whole script runs on beta for consistency.
$script:GraphRoot = 'https://graph.microsoft.com/beta'

# Set by Invoke-Graph on every call. LastGraphAuthFailure flags the A365 registration service's
# habit of relaying an authorization refusal as HTTP 500, which callers must not read as a fault.
$script:LastGraphStatus      = $null
$script:LastGraphAuthFailure = $false

# The appId this script is authenticated as, resolved after Connect-MgGraph. Needed to explain a
# registration ownership refusal, which turns on which application is calling.
$script:CallerAppId = ''

if ($Force) { $ConfirmPreference = 'None' }

# Read inside functions, so bind them to script scope explicitly rather than relying on the
# enclosing scope.
$script:ContinueOnError       = [bool]$ContinueOnError

$script:Failures = [System.Collections.Generic.List[object]]::new()

# ---------------------------------------------------------------------------
# Helpers (shared shape with the other A365 scripts)
# ---------------------------------------------------------------------------

# .PSObject.Properties.Name is member enumeration, which throws under StrictMode on an object with
# no properties - and Graph does return empty bodies.
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

function Get-Value {
    param($Object, [Parameter(Mandatory)][string] $Name, $Default = $null)
    if (Test-HasProperty $Object $Name) {
        $value = $Object.$Name
        if ($null -ne $value) { return $value }
    }
    return $Default
}

function Get-JwtPayload {
    <#
    .SYNOPSIS
        Best-effort decode of a JWT payload. Never validates the signature.

    .DESCRIPTION
        Used only to describe the token the caller supplied - which application it belongs to and
        whether it is app-only. Get-MgContext cannot answer either question for a raw -AccessToken:
        it reports AuthType 'Delegated' for every token handed to it, including client-credentials
        tokens that have no user at all. Mislabelling an app-only run as delegated sends the
        permission advice down the wrong branch, so the token itself is the authority here.
    #>
    param([string] $Token)

    if ([string]::IsNullOrWhiteSpace($Token)) { return $null }
    $parts = $Token.Split('.')
    if ($parts.Count -lt 2) { return $null }

    try {
        $payload = $parts[1].Replace('-', '+').Replace('_', '/')
        # base64url drops the padding; restore it before decoding.
        switch ($payload.Length % 4) {
            2 { $payload += '==' }
            3 { $payload += '=' }
            1 { return $null }
        }
        return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Test-JwtIsAppOnly {
    <#
    .SYNOPSIS
        Returns $true / $false when the token says so, $null when it cannot be determined.
    #>
    param($Claims)

    if ($null -eq $Claims) { return $null }

    # idtyp is the explicit answer when Entra emits it.
    $idtyp = [string](Get-Value $Claims 'idtyp' '')
    if ($idtyp -eq 'app')  { return $true }
    if ($idtyp -eq 'user') { return $false }

    # Otherwise: a delegated token always carries scopes; an app-only token carries roles and no
    # user identity. Check for a user first, since a delegated token can carry roles too.
    foreach ($userClaim in 'upn', 'unique_name', 'preferred_username') {
        if (-not [string]::IsNullOrWhiteSpace([string](Get-Value $Claims $userClaim ''))) { return $false }
    }
    if (-not [string]::IsNullOrWhiteSpace([string](Get-Value $Claims 'scp' ''))) { return $false }
    if ($null -ne (Get-Value $Claims 'roles' $null)) { return $true }

    return $null
}

function Get-GraphErrorInfo {
    param($ErrorRecord)

    $codeToStatus = @{
        'Request_ResourceNotFound'    = 404
        'ResourceNotFound'            = 404
        'itemNotFound'                = 404
        'NotFound'                    = 404
        'Request_BadRequest'          = 400
        'badRequest'                  = 400
        'Authorization_RequestDenied' = 403
        'accessDenied'                = 403
        'InvalidAuthenticationToken'  = 401
        'activityLimitReached'        = 429
        'serviceNotAvailable'         = 503
    }

    $rawMessage = ''
    if ($ErrorRecord.Exception) { $rawMessage = [string]$ErrorRecord.Exception.Message }

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

    if ($null -eq $status -and $code -and $codeToStatus.ContainsKey($code)) {
        $status = $codeToStatus[$code]
    }

    [pscustomobject]@{ Status = $status; Code = $code; Message = $message }
}

function Get-ConciseGraphMessage {
    <#
    .SYNOPSIS
        Reduces a Graph failure to the sentence that actually helps.

    .DESCRIPTION
        Invoke-Graph rethrows a string containing the entire HTTP response - request line, every
        response header, then the JSON body. Printed verbatim that is ~20 lines in which the one
        useful sentence, and any advice appended after it, scroll off the top. This keeps the
        service's own error message and discards the transport noise.

        Note the A365 registration endpoints frequently return an EMPTY message with code
        'UnknownError', so an explicit placeholder is returned rather than a blank line - the
        absence of detail is itself worth stating.
    #>
    param([string] $Message)

    if ([string]::IsNullOrWhiteSpace($Message)) { return '(the service returned no error message)' }

    # Prefer the innermost JSON "message" property, which is the service's actual complaint.
    $matchesFound = [regex]::Matches($Message, '"message"\s*:\s*"((?:[^"\\]|\\.)*)"')
    foreach ($m in $matchesFound) {
        $candidate = $m.Groups[1].Value
        # Unescape the common sequences; the payload is sometimes double-encoded JSON.
        $candidate = $candidate -replace '\\"', '"' -replace '\\r', '' -replace '\\n', ' ' -replace '\\\\', '\'
        $candidate = $candidate.Trim()
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }

        # The A365 registration service nests a whole JSON envelope inside Graph's "message", so
        # unescaping yields {"StatusCode":500,"Message":"..."} rather than prose. Printing that raw
        # is exactly the transport noise this function exists to strip, so unwrap to the sentence.
        for ($depth = 0; $depth -lt 3; $depth++) {
            if ($candidate -notmatch '^\s*\{') { break }
            $inner = [regex]::Match($candidate, '"[Mm]essage"\s*:\s*"((?:[^"\\]|\\.)*)"')
            if (-not $inner.Success) { break }
            $unwrapped = $inner.Groups[1].Value -replace '\\"', '"' -replace '\\r', '' -replace '\\n', ' ' -replace '\\\\', '\'
            $unwrapped = $unwrapped.Trim()
            if ([string]::IsNullOrWhiteSpace($unwrapped)) { break }
            $candidate = $unwrapped
        }

        if (-not [string]::IsNullOrWhiteSpace($candidate)) { return $candidate }
    }

    # The A365 registration endpoints routinely answer with an empty message. Say so, and name the
    # error code if one was given, instead of echoing the HTTP trace back at the user.
    if ($Message -match '"code"\s*:\s*"([^"]+)"') {
        return "(the service returned no error message; error code '$($Matches[1])')"
    }

    # No JSON body at all - drop everything from the echoed request line onward, which is where the
    # transport noise starts, and keep whatever prose preceded it.
    $head = ($Message -split '\r?\n')[0]
    $head = ($head -split '\s(?:GET|POST|PATCH|DELETE)\s+https?://')[0]
    if ($head -match '^(.*?)\bfailed \[\d{3}[^\]]*\]:\s*(.*)$') {
        $before = $Matches[1].Trim()
        $after  = $Matches[2].Trim()
        if (-not [string]::IsNullOrWhiteSpace($after)) { return $after }
        if (-not [string]::IsNullOrWhiteSpace($before)) { return "$before failed" }
        return '(the service returned no error message)'
    }

    $head = $head.Trim()
    if ([string]::IsNullOrWhiteSpace($head)) { return '(the service returned no error message)' }
    if ($head.Length -gt 300) { return $head.Substring(0, 300) + '...' }
    return $head
}

function Test-DownstreamAuthFailure {
    <#
    .SYNOPSIS
        Recognises an A365 registration-service authorization refusal disguised as HTTP 500.

    .DESCRIPTION
        /beta/copilot/agentRegistrations is fronted by Graph but served by the AgentX preview
        service. When AgentX refuses a call, Graph relays it as 500 / UnknownError with the real
        complaint nested, double-encoded, in the body:

          {"error":{"code":"UnknownError","message":
            "{\"StatusCode\":500,\"Message\":\"You do not have permission to retrieve this agent
             registration.\"}"}}

        Confirmed live on both the write path ("...to create an agent registration managed by
        another AppId") and the read path. Two consequences, and both bite:

          * It is NOT transient. Retrying with backoff only delays the inevitable, and because the
            status is 500 it escapes every -Tolerate* switch and aborts the run.
          * It is NOT a missing Graph permission. The token already cleared Graph's role check to
            reach AgentX at all, so telling the caller to grant a role sends them in circles.

        Matched on the message because the status code and error code carry no signal here.
    #>
    param($Status, [string] $Message)

    if ($null -eq $Status -or [int]$Status -ne 500) { return $false }
    if ([string]::IsNullOrWhiteSpace($Message))     { return $false }
    return [bool]($Message -match '(?i)you do not have permission')
}

# A registration belongs to the application that created it - the appId stamped into
# managedByAppId at registration time - and no Graph role overrides that, so the generic
# "grant the write permission" advice is actively wrong for this failure.
function Get-RegistrationOwnershipAdvice {
    param([string] $CallerAppId)

    $caller = if ([string]::IsNullOrWhiteSpace($CallerAppId)) { 'the calling application' } else { "'$CallerAppId'" }
    return "Graph accepted the token and the A365 registration service then refused it, so this is " +
           "resource ownership, not a missing role - granting permissions will not help. Every " +
           "registration is scoped to the application that created it (the appId it stamped into " +
           "managedByAppId), and $caller is not that application. Re-run authenticated as the app " +
           "that registered this agent. If the a365 CLI registered it, it is managed by the CLI's " +
           "own AgentX appId 59eca866-2f46-40b8-96ff-63f663121ef9, which a custom application " +
           "cannot claim - that one has to be removed with the CLI."
}

function Invoke-Graph {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PATCH', 'DELETE')][string] $Method,
        [Parameter(Mandatory)][string] $Uri,
        $Body,
        [int]    $MaxAttempts = 5,
        [switch] $TolerateNotFound,
        [switch] $TolerateForbidden,
        [switch] $TolerateBadRequest,
        [switch] $TolerateServerError
    )

    if ($Uri -notmatch '^https?://') { $Uri = "$script:GraphRoot$Uri" }

    $json = $null
    if ($null -ne $Body) {
        $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 25 }
    }

    # A tolerated failure and a successful call that returns no body both come back as $null, so
    # the caller cannot tell "absent" from "removed" by the return value alone. $script:LastGraphStatus
    # records the status of the most recent call so it can. DELETE relies on this to avoid reporting
    # a 404 no-op as a successful removal.
    $script:LastGraphStatus = $null
    $script:LastGraphAuthFailure = $false

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            $statusCode = $null
            $reqParams = @{
                Method             = $Method
                Uri                = $Uri
                Headers            = @{ 'OData-Version' = '4.0' }
                OutputType         = 'PSObject'
                StatusCodeVariable = 'statusCode'
                ErrorAction        = 'Stop'
            }
            if ($json) {
                $reqParams.Body        = $json
                $reqParams.ContentType = 'application/json'
            }
            Write-A365LogGraphRequest -Method $Method -Uri $Uri -Body $Body -Attempt $attempt -MaxAttempts $MaxAttempts
            $swGraph = [Diagnostics.Stopwatch]::StartNew()
            $result = Invoke-MgGraphRequest @reqParams
            $swGraph.Stop()
            Write-A365LogGraphResponse -Method $Method -Uri $Uri -Response $result -DurationMs ([int]$swGraph.ElapsedMilliseconds)
            $script:LastGraphStatus = $statusCode
            return $result
        }
        catch {
            $info = Get-GraphErrorInfo -ErrorRecord $_
            # Logged as a warning, not a failure: the caller may tolerate this status or retry
            # it. Only the final throw below counts as a failed Graph call.
            Write-A365Log -Level WARN -Message ("<-- HTTP {0} on {1} {2}" -f $info.Status, $Method, $Uri) -Detail $info.Message
            $script:LastGraphStatus = $info.Status

            # An AgentX refusal arrives as 500, so it must be classified before the tolerate and
            # retry checks below - otherwise it slips past every -Tolerate* switch, burns the full
            # backoff, and aborts a call the caller explicitly said it could survive.
            $isDisguisedDenial = Test-DownstreamAuthFailure -Status $info.Status -Message ([string]$info.Message)
            if ($isDisguisedDenial) { $script:LastGraphAuthFailure = $true }

            if ($info.Status -eq 404 -and $TolerateNotFound) { return $null }
            if ($info.Status -in 401, 403 -and $TolerateForbidden) {
                Write-Verbose "$($info.Status) tolerated for $Method $Uri"
                return $null
            }
            if ($info.Status -eq 400 -and $TolerateBadRequest) {
                Write-Verbose "400 tolerated for $Method $Uri - $($info.Message)"
                return $null
            }

            # A disguised denial is a 403 wearing a 500, so honour -TolerateForbidden for it too.
            if ($isDisguisedDenial -and $TolerateForbidden) {
                Write-Verbose "500 authorization refusal tolerated for $Method $Uri - $($info.Message)"
                return $null
            }

            if ((-not $isDisguisedDenial) -and $info.Status -in 429, 500, 502, 503, 504 -and $attempt -lt $MaxAttempts) {
                $delay = [Math]::Min([Math]::Pow(2, $attempt), 20)
                Write-Verbose "Transient $($info.Status) on $Method $Uri - retry $attempt/$MaxAttempts in ${delay}s"
                Start-Sleep -Seconds $delay
                continue
            }

            # Checked only after the retries are spent: a 500 is presumed transient until proven
            # otherwise, so tolerating it up front would turn a recoverable blip into a lost read.
            if ($info.Status -eq 500 -and $TolerateServerError) {
                Write-Verbose "500 tolerated for $Method $Uri - $($info.Message)"
                return $null
            }

            Write-A365LogGraphResponse -Method $Method -Uri $Uri -Status $info.Status -AsFailure -ErrorText $info.Message
            throw "Graph $Method $Uri failed [$($info.Status) $($info.Code)]: $($info.Message)"
        }
    }
}

function Get-GraphCollection {
    param([Parameter(Mandatory)][string] $Uri, [switch] $Tolerate)

    $params = @{ Method = 'GET'; Uri = $Uri }
    if ($Tolerate) {
        $params.TolerateForbidden  = $true
        $params.TolerateBadRequest = $true
        $params.TolerateNotFound   = $true
    }
    $response = Invoke-Graph @params

    # $null means the CALL failed and was tolerated; an empty array means the call succeeded and
    # matched nothing. Those are different answers, so the empty array must survive the return -
    # hence the leading comma, which stops PowerShell unrolling it to $null.
    if ($null -eq $response) { return $null }
    if (Test-HasProperty $response 'value') { return , @($response.value) }
    return , @()
}

function ConvertTo-SecureStringValue {
    [CmdletBinding()]
    [OutputType([securestring])]
    param([object] $Value, [Parameter(Mandatory)][string] $Name)

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
    $secure = [securestring]::new()
    foreach ($char in $Value.ToCharArray()) { $secure.AppendChar($char) }
    $secure.MakeReadOnly()
    return $secure
}

function Write-Step {
    param([int] $Number, [string] $Text)
    Write-Host ''
    Write-Host "=== Step $Number : $Text" -ForegroundColor Cyan
}

function Test-Guid {
    param([string] $Value)
    return $Value -match '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$'
}

function Assert-Guid {
    param([string] $Value, [Parameter(Mandatory)][string] $Parameter)
    if (-not (Test-Guid $Value)) {
        throw "'$Value' is not a GUID. -$Parameter takes object ids."
    }
}

# An agent registration id is NOT a directory object id. The service assigns it and returns it
# from POST /beta/copilot/agentRegistrations, and in this tenant shape it comes back tenant-prefixed
# ("T_<guid>") even though the client posts a bare GUID as `id`. The admin center shows the prefixed
# form too. Both forms are therefore legitimate things for a caller to hold, so the id is treated as
# opaque here and the correct form is discovered at resolve time rather than guessed.
function Test-RegistrationId {
    param([string] $Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    # Anything that would change the shape of the request URI is rejected rather than escaped, so a
    # mistyped id can never turn into a request against a different resource.
    if ($Value -match '[\s/\\?#%]') { return $false }
    return $Value -match '^[A-Za-z0-9_.:-]{1,120}$'
}

function Assert-RegistrationId {
    param([string] $Value, [Parameter(Mandatory)][string] $Parameter)
    if (-not (Test-RegistrationId $Value)) {
        throw @"
'$Value' is not a usable agent registration id. -$Parameter takes the id assigned by
POST /beta/copilot/agentRegistrations, which is either a GUID or a tenant-prefixed GUID:
  33333333-4444-5555-6666-777777777777
  T_33333333-4444-5555-6666-777777777777
Both forms are accepted and the script probes for whichever one the service recognises.
This is not a directory object id - the agent identity and agentic user are separate ids
passed through -AgentIdentityId and -AgentUserId.
"@
    }
}

# The two interchangeable spellings of the same registration. Used to recover when the caller holds
# the form the service does not answer on, which would otherwise 404 and be mistaken for "already
# deleted".
function Get-RegistrationIdVariant {
    param([Parameter(Mandatory)][string] $Id)
    if ($Id -match '^([A-Za-z][A-Za-z0-9]*)_(.+)$') { return $Matches[2] }
    if (Test-Guid $Id) { return "T_$Id" }
    return $null
}

# Turns a 403 on a specific surface into the permission that is actually missing, rather than the
# generic "Insufficient privileges" Graph returns.
function Get-DenialAdvice {
    param([Parameter(Mandatory)][string] $Kind, [switch] $AppOnly)

    $needed = switch ($Kind) {
        'Registration'  { 'AgentRegistration.ReadWrite.All' }
        'Instance'      { "AgentInstance.ReadWrite.All, plus the 'Agent Registry Administrator' Entra role" }
        'Purge'         { 'Application.ReadWrite.All' }
        default         { 'the corresponding write permission' }
    }
    $kindLabel = if ($AppOnly) { 'application role' } else { 'delegated scope' }
    return "Requires the $kindLabel $needed. If it was granted in the last few minutes, the token predates the grant - reconnect and retry."
}

# ---------------------------------------------------------------------------
# Argument validation
# ---------------------------------------------------------------------------

# @($null) is a one-element array containing $null, so an omitted [string[]] parameter would
# otherwise be validated as an empty id.
function Select-NonEmptyId {
    param([string[]] $Value)
    return , @(@($Value) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
}

$RegistrationId  = Select-NonEmptyId $RegistrationId
$AgentInstanceId = Select-NonEmptyId $AgentInstanceId

foreach ($id in $RegistrationId)  { Assert-RegistrationId $id 'RegistrationId' }
foreach ($id in $AgentInstanceId) { Assert-Guid $id 'AgentInstanceId' }

$recognisedAgentKeys = 'DisplayName', 'RegistrationId', 'AgentInstanceId'
if ($Agent) {
    for ($i = 0; $i -lt $Agent.Count; $i++) {
        $unknown = @($Agent[$i].Keys | Where-Object { $recognisedAgentKeys -notcontains $_ })
        if ($unknown.Count -gt 0) {
            throw "-Agent entry #$($i + 1) has unrecognised key(s): $($unknown -join ', '). Recognised keys: $($recognisedAgentKeys -join ', ')."
        }
        $hasTarget = @('RegistrationId', 'AgentInstanceId') |
                     Where-Object { $Agent[$i].ContainsKey($_) -and -not [string]::IsNullOrWhiteSpace([string]$Agent[$i][$_]) }
        if (-not $hasTarget) {
            throw "-Agent entry #$($i + 1) has no id to act on. Supply at least one of RegistrationId, AgentInstanceId, AgentIdentityId or AgentUserId."
        }
    }
}

$hasTargeting = $RegistrationId -or $AgentInstanceId -or $Agent
if (-not $hasTargeting) {
    throw 'Nothing to do. Supply -RegistrationId, -AgentInstanceId, -AgentIdentityId, -AgentUserId, -BlueprintAppId or -Agent.'
}

# ---------------------------------------------------------------------------
# Connect
# ---------------------------------------------------------------------------
Write-Step 1 'Connecting to Microsoft Graph'

if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Authentication)) {
    throw 'Microsoft.Graph.Authentication is not installed. Run: Install-Module Microsoft.Graph.Authentication -Scope CurrentUser'
}
# The module's own Update-TypeData calls honour $WhatIfPreference, so under -WhatIf they emit a
# dozen "What if: Performing the operation Update TypeData" lines ahead of the removal plan.
# Import-Module has no -WhatIf to suppress that, so drop the preference for the import only.
$previousWhatIfPreference = $WhatIfPreference
try {
    $WhatIfPreference = $false
    Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
}
finally {
    $WhatIfPreference = $previousWhatIfPreference
}

# Entra rejects the WHOLE scope string with AADSTS70011 if a single scope is unknown, and the
# Agent 365 scopes are preview - so ask for the full set first and degrade.
$delegatedScopeSets = @()
if ($InspectOnly) {
    $delegatedScopeSets += , @('AgentIdentity.Read.All', 'AgentRegistration.Read.All',
                               'User.Read.All', 'Directory.Read.All')
    $delegatedScopeSets += , @('User.Read.All', 'Directory.Read.All')
}
else {
    $delegatedScopeSets += , @('AgentRegistration.ReadWrite.All', 'AgentIdentity.DeleteRestore.All',
                               'AgentInstance.ReadWrite.All', 'AgentIdentity.Read.All',
                               'User.ReadWrite.All', 'Application.ReadWrite.All', 'Directory.Read.All')
    $delegatedScopeSets += , @('AgentRegistration.ReadWrite.All', 'AgentIdentity.DeleteRestore.All',
                               'User.ReadWrite.All', 'Application.ReadWrite.All', 'Directory.Read.All')
    $delegatedScopeSets += , @('User.ReadWrite.All', 'Application.ReadWrite.All', 'Directory.Read.All')
}

$ClientSecret = ConvertTo-SecureStringValue -Value $ClientSecret -Name 'ClientSecret'
$AccessToken  = ConvertTo-SecureStringValue -Value $AccessToken  -Name 'AccessToken'

$modes = @()
if ($UseExistingConnection) { $modes += 'ExistingConnection' }
if ($Interactive)           { $modes += 'Interactive' }
if ($UseDeviceCode)         { $modes += 'DeviceCode' }
if ($AccessToken)           { $modes += 'AccessToken' }
if ($CertificateThumbprint) { $modes += 'Certificate' }
if ($ClientSecret)          { $modes += 'ClientSecret' }

if ($modes.Count -gt 1) {
    throw "Conflicting authentication options ($($modes -join ', ')). Supply exactly one."
}
if ($modes.Count -eq 0) {
    throw 'No authentication method specified. Pass -Interactive (recommended), -UseDeviceCode, -UseExistingConnection, or -ClientId with -ClientSecret / -CertificateThumbprint.'
}
$mode = $modes[0]

if ($mode -in @('ClientSecret', 'Certificate')) {
    if (-not $ClientId) { throw "-ClientId is required for $mode authentication." }
    if (-not $TenantId) { throw "-TenantId is required for $mode authentication." }
}

if ($mode -ne 'ExistingConnection') {
    $connect = @{ NoWelcome = $true; ErrorAction = 'Stop' }
    switch ($mode) {
        'Interactive' {
            if ($TenantId) { $connect.TenantId = $TenantId }
            if ($ClientId) { $connect.ClientId = $ClientId }
        }
        'DeviceCode' {
            $connect.UseDeviceCode = $true
            if ($TenantId) { $connect.TenantId = $TenantId }
            if ($ClientId) { $connect.ClientId = $ClientId }
        }
        'AccessToken'  { $connect.AccessToken = $AccessToken }
        'ClientSecret' {
            $connect.TenantId               = $TenantId
            $connect.ClientSecretCredential = [pscredential]::new($ClientId, $ClientSecret)
        }
        'Certificate'  {
            $connect.TenantId              = $TenantId
            $connect.ClientId              = $ClientId
            $connect.CertificateThumbprint = $CertificateThumbprint
        }
    }

    if ($mode -in @('Interactive', 'DeviceCode')) {
        $connected = $false
        for ($setIndex = 0; $setIndex -lt $delegatedScopeSets.Count; $setIndex++) {
            $connect.Scopes = $delegatedScopeSets[$setIndex]
            try {
                Connect-MgGraph @connect
                $connected = $true
                break
            }
            catch {
                $isBadScope = "$($_.Exception.Message)" -match 'AADSTS70011|input parameter .scope. is not valid|does not exist'
                if (-not $isBadScope -or $setIndex -eq $delegatedScopeSets.Count - 1) { throw }
                Write-Warning "Entra rejected the scope set [$($connect.Scopes -join ', ')]. Retrying with a reduced set."
            }
        }
        if (-not $connected) { throw 'Could not negotiate a usable set of delegated scopes.' }
    }
    else {
        Connect-MgGraph @connect
    }
}

$mg = Get-MgContext
if (-not $mg) {
    throw 'Connect-MgGraph did not establish a Graph context. If you used -Interactive from a non-interactive host, the browser flow cannot complete - use -UseDeviceCode instead.'
}

$authType  = [string](Get-Value $mg 'AuthType' '')
$isAppOnly = ($authType -eq 'AppOnly')
$ctxTenant = [string](Get-Value $mg 'TenantId' $TenantId)
$callerId  = [string](Get-Value $mg 'ClientId' '')
$script:CallerAppId = $callerId

# A raw -AccessToken is always reported as 'Delegated' by Get-MgContext, even for a
# client-credentials token with no user. Ask the token instead.
if ($mode -eq 'AccessToken') {
    $tokenPlain = ''
    try {
        $tokenPlain = [Net.NetworkCredential]::new('', $AccessToken).Password
    }
    catch {
        $tokenPlain = ''
    }
    $claims    = Get-JwtPayload -Token $tokenPlain
    $fromToken = Test-JwtIsAppOnly -Claims $claims
    if ($null -ne $fromToken) { $isAppOnly = [bool]$fromToken }
    if ([string]::IsNullOrWhiteSpace($callerId) -and $null -ne $claims) {
        $callerId = [string](Get-Value $claims 'appid' ([string](Get-Value $claims 'azp' '')))
        $script:CallerAppId = $callerId
    }
    if ($null -ne $claims) {
        $tokenTenant = [string](Get-Value $claims 'tid' '')
        if (-not [string]::IsNullOrWhiteSpace($tokenTenant)) { $ctxTenant = $tokenTenant }
    }
}

if ($isAppOnly) {
    $who = if ([string]::IsNullOrWhiteSpace($callerId)) { '(unknown application)' } else { $callerId }
    Write-Host "  Connected app-only as $who in tenant $ctxTenant [$mode]" -ForegroundColor Green
}
else {
    $who = [string](Get-Value $mg 'Account' '')
    if ([string]::IsNullOrWhiteSpace($who)) { $who = '(unknown account)' }
    Write-Host "  Connected as $who in tenant $ctxTenant [delegated, $mode]" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Resolve targets
#
# Everything is resolved and named BEFORE anything is deleted, so the plan can be reviewed and
# nothing is deleted blind.
# ---------------------------------------------------------------------------
Write-Step 2 'Resolving targets'

# Ordered so the admin-center inventory entry goes first.
$kindOrder = @{ Registration = 0; Instance = 1 }

$plan = [System.Collections.Generic.List[object]]::new()

function Add-PlanItem {
    param(
        [Parameter(Mandatory)][ValidateSet('Registration', 'Instance')][string] $Kind,
        [AllowEmptyString()][string] $Id,
        [string] $Label = '',
        [string] $Detail = '',
        [string] $State = 'Present',
        [string] $Note = ''
    )

    # A tolerated query can yield rows without ids; those are not actionable.
    if ([string]::IsNullOrWhiteSpace($Id)) { return }

    foreach ($existing in $plan) {
        if ($existing.Kind -eq $Kind -and $existing.Id -eq $Id) { return }
    }
    $plan.Add([pscustomobject]@{
        Kind   = $Kind
        Id     = $Id
        Label  = $Label
        Detail = $Detail
        State  = $State      # Present | Missing | Unknown | Skipped
        Note   = $Note
        Result = 'Pending'
    })
}

# Finds the agentic user attached to an agent identity. identityParentId is the link, verified
# against a live tenant.
function Resolve-RegistrationTarget {
    param([Parameter(Mandatory)][string] $Id, [string] $Label = '')

    # A GET here is a courtesy probe. It is allowed to fail: the delete tolerates 404, so an
    # unreadable registration must still be attempted rather than skipped.
    #
    # It also settles which spelling of the id the service answers on. DELETE treats an unknown id
    # as success, so sending the wrong variant would report "Deleted" and leave the entry in the
    # admin center - the exact outcome this script exists to prevent. When the first form 404s, the
    # other one is tried before concluding anything.
    $candidates = @($Id)
    $variant    = Get-RegistrationIdVariant -Id $Id
    if ($variant) { $candidates += $variant }

    $reg        = $null
    $resolvedId = $Id
    $forbidden  = $false

    foreach ($candidate in $candidates) {
        $reg = Invoke-Graph -Method GET -Uri "/copilot/agentRegistrations/$([uri]::EscapeDataString($candidate))" `
                 -TolerateNotFound -TolerateForbidden -TolerateBadRequest -TolerateServerError
        if ($null -ne $reg) { $resolvedId = $candidate; break }

        # A 401/403 says nothing about whether the id is right, so trying the other spelling would
        # be noise. Stop and let the delete proceed with what the caller supplied. The registration
        # service spells its refusals 500, so that counts here too.
        if (($script:LastGraphStatus -in 401, 403) -or $script:LastGraphAuthFailure) { $forbidden = $true; break }
    }

    if ($null -eq $reg) {
        $note = if ($script:LastGraphAuthFailure) {
            'The registration service refused to return this registration. Registrations are scoped to the application that created them, so a different app cannot read one it did not register. Delete will be attempted anyway and is expected to be refused the same way.'
        }
        elseif ($forbidden) {
            'The token cannot read agent registrations (AgentRegistration.Read.All), so the id could not be confirmed. Delete will be attempted with the id as supplied.'
        }
        elseif ($variant) {
            "Not found as '$Id' or as '$variant'. It may already be gone, the id may belong to another tenant, or it may exist but be invisible to this caller - registrations are only readable by the app that owns them or by a user in their ownerIds. Delete will be attempted anyway."
        }
        else {
            'Could not read the registration (it may be absent, or the token may lack AgentRegistration.Read.All). Delete will be attempted anyway.'
        }
        Add-PlanItem -Kind 'Registration' -Id $Id -Label $Label -State 'Unknown' -Note $note
        return
    }

    if ($resolvedId -ne $Id) {
        Write-Verbose "Registration '$Id' resolves as '$resolvedId'; using that form for the delete."
    }

    # Prefer the id the service reports over the one that happened to answer, so the delete and the
    # verification both address the registration by its canonical name.
    $canonical = [string](Get-Value $reg 'id' $resolvedId)
    if ([string]::IsNullOrWhiteSpace($canonical)) { $canonical = $resolvedId }

    Add-PlanItem -Kind 'Registration' -Id $canonical `
        -Label ([string](Get-Value $reg 'displayName' $Label)) `
        -Detail ("agent identity $([string](Get-Value $reg 'agentIdentityId' 'n/a'))")
}

# --- explicit ids -----------------------------------------------------------
foreach ($id in @($RegistrationId | Select-Object -Unique)) { Resolve-RegistrationTarget -Id $id }

foreach ($id in @($AgentInstanceId | Select-Object -Unique)) {
    # The Agent Registry has no readable GET for a single instance in this tenant shape, so the
    # instance is planned unconditionally and the delete tolerates 404.
    Add-PlanItem -Kind 'Instance' -Id $id -State 'Unknown' `
        -Note 'Agent Registry instances cannot be read back; delete will be attempted.'
}

# --- bulk entries -----------------------------------------------------------
foreach ($entry in @($Agent | Where-Object { $null -ne $_ })) {
    $label = if ($entry.ContainsKey('DisplayName')) { [string]$entry['DisplayName'] } else { '' }

    if ($entry.ContainsKey('RegistrationId') -and -not [string]::IsNullOrWhiteSpace([string]$entry['RegistrationId'])) {
        Assert-RegistrationId ([string]$entry['RegistrationId']) 'Agent.RegistrationId'
        Resolve-RegistrationTarget -Id ([string]$entry['RegistrationId']) -Label $label
    }
    if ($entry.ContainsKey('AgentInstanceId') -and -not [string]::IsNullOrWhiteSpace([string]$entry['AgentInstanceId'])) {
        Assert-Guid ([string]$entry['AgentInstanceId']) 'Agent.AgentInstanceId'
        Add-PlanItem -Kind 'Instance' -Id ([string]$entry['AgentInstanceId']) -Label $label -State 'Unknown' `
            -Note 'Agent Registry instances cannot be read back; delete will be attempted.'
    }
}

# ---------------------------------------------------------------------------
# Plan
# ---------------------------------------------------------------------------
Write-Step 3 'Removal plan'

$ordered = @($plan | Sort-Object @{ Expression = { $kindOrder[$_.Kind] } }, Label)

$ordered |
    Select-Object @{ n = 'Order'; e = { $kindOrder[$_.Kind] + 1 } },
                  Kind,
                  @{ n = 'Name'; e = { if ($_.Label) { $_.Label } else { '(unnamed)' } } },
                  Id, State |
    Format-Table -AutoSize | Out-Host

foreach ($item in $ordered) {
    if ($item.Note) {
        Write-Host "  $($item.Kind) $($item.Id): $($item.Note)" -ForegroundColor DarkGray
    }
}

$actionable = @($ordered | Where-Object { $_.State -in 'Present', 'Unknown' })

Write-Host ''
Write-Host "  Objects to delete : $($actionable.Count)" -ForegroundColor $(if ($actionable.Count) { 'Yellow' } else { 'Green' })

if ($InspectOnly) {
    Write-Host ''
    Write-Host 'Inspect only - nothing was deleted.' -ForegroundColor Green
    return
}

if ($actionable.Count -eq 0) {
    Write-Host ''
    Write-Host 'Nothing left to delete.' -ForegroundColor Green
    return
}

# ---------------------------------------------------------------------------
# Delete
# ---------------------------------------------------------------------------
Write-Step 4 'Removing'

function Get-TargetUri {
    param([Parameter(Mandatory)][string] $Kind, [Parameter(Mandatory)][string] $Id)
    $escaped = [uri]::EscapeDataString($Id)
    switch ($Kind) {
        'Registration'  { "/copilot/agentRegistrations/$escaped" }
        'Instance'      { "/agentRegistry/agentInstances/$escaped" }
    }
}

function Remove-Target {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][object] $Item)

    $uri = Get-TargetUri -Kind $Item.Kind -Id $Item.Id

    $label  = if ($Item.Label) { $Item.Label } else { $Item.Id }
    $target = "$($Item.Kind) '$label' ($($Item.Id))"

    if (-not $PSCmdlet.ShouldProcess($target, "DELETE $script:GraphRoot$uri")) {
        $Item.Result = 'Skipped (WhatIf)'
        return
    }

    try {
        # 404 means the object is not there. That is the desired end state, but it is NOT the same
        # as having removed something, and saying "Deleted" for it would hide both a mistyped id and
        # a registration that is still listed under its other id form.
        $null = Invoke-Graph -Method DELETE -Uri $uri -TolerateNotFound
        $deleted = $script:LastGraphStatus -ne 404

        # A registration that 404s under one spelling may still exist under the other.
        if (-not $deleted -and $Item.Kind -eq 'Registration') {
            $variant = Get-RegistrationIdVariant -Id $Item.Id
            if ($variant) {
                Write-Verbose "DELETE of '$($Item.Id)' returned 404; retrying as '$variant'."
                $null = Invoke-Graph -Method DELETE -Uri (Get-TargetUri -Kind $Item.Kind -Id $variant) -TolerateNotFound
                if ($script:LastGraphStatus -ne 404) {
                    $Item.Id = $variant
                    $deleted = $true
                }
            }
        }

        if ($deleted) {
            # A 2xx DELETE is NOT proof that anything was removed. This endpoint answers success for
            # ids that GET reported as 404 moments earlier, so the status alone cannot distinguish
            # "removed it" from "accepted a no-op". Only claim a deletion for an object that was
            # actually read during planning.
            if ($Item.State -eq 'Unknown') {
                $Item.Result = 'Deleted (unverified)'
                Write-Host "  DELETE accepted for $($Item.Kind): $label" -ForegroundColor Yellow
                Write-Host '    This object was never readable, so there is no evidence that it' -ForegroundColor Yellow
                Write-Host '    existed or that anything was removed. Confirm as an owner.' -ForegroundColor Yellow
            }
            else {
                $Item.Result = 'Deleted'
                Write-Host "  Deleted $($Item.Kind): $label" -ForegroundColor Green
            }
        }
        else {
            $Item.Result = 'Already absent'
            Write-Host "  $($Item.Kind) already absent: $label" -ForegroundColor DarkGray
        }
    }
    catch {
        $info   = Get-GraphErrorInfo -ErrorRecord $_
        $detail = Get-ConciseGraphMessage -Message ([string]$info.Message)
        $advice = if (Test-DownstreamAuthFailure -Status $info.Status -Message ([string]$info.Message)) {
            Get-RegistrationOwnershipAdvice -CallerAppId $script:CallerAppId
        }
        elseif ($info.Status -in 401, 403) { Get-DenialAdvice -Kind $Item.Kind -AppOnly:$isAppOnly }
        else { '' }
        $Item.Result = "Failed [$($info.Status)]"

        $statusText = if ($info.Status) { "$($info.Status)" } else { 'unknown status' }
        if ($info.Code) { $statusText = "$statusText $($info.Code)" }

        $lines = @(
            "Failed to delete $target"
            "  HTTP    : $statusText"
            "  Request : DELETE $script:GraphRoot$uri"
            "  Detail  : $detail"
        )
        if ($advice) { $lines += "  Fix     : $advice" }

        $script:Failures.Add([pscustomobject]@{ Kind = $Item.Kind; Id = $Item.Id; Label = $label; Status = $info.Status; Message = $detail })

        # Printed rather than thrown: PowerShell's error formatter reflows a multi-line exception
        # message into an unreadable block, which would defeat the point of laying it out.
        Write-Host ''
        foreach ($line in $lines) { Write-Host $line -ForegroundColor Red }
        Write-Host ''

        if (-not $script:ContinueOnError) {
            throw "Failed to delete $($Item.Kind) $($Item.Id) [HTTP $statusText] - see the detail above. Re-run with -ContinueOnError to attempt the remaining objects."
        }
    }
}

foreach ($item in $actionable) { Remove-Target -Item $item }

# ---------------------------------------------------------------------------

# ---------------------------------------------------------------------------
# Verify
# ---------------------------------------------------------------------------
if (-not $WhatIfPreference) {
    Write-Step 6 'Verifying'

    $stillPresent  = [System.Collections.Generic.List[object]]::new()
    $unverifiable  = [System.Collections.Generic.List[object]]::new()

    foreach ($item in @($actionable | Where-Object { $_.Result -like 'Deleted*' })) {
        # Registry instances have no readable GET, so there is nothing to verify against.
        if ($item.Kind -eq 'Instance') { continue }

        # An object that could not be read BEFORE the delete cannot be verified after it. The probe
        # returns the same "absent" answer either way, so treating that answer as proof would just
        # restate the earlier blind spot as a confirmation. This is exactly how a registration that
        # was never touched can be reported as verified gone.
        if ($item.State -eq 'Unknown') { $unverifiable.Add($item); continue }

        $probe = switch ($item.Kind) {
            'Registration'  { Get-TargetUri -Kind 'Registration' -Id $item.Id }
        }

        $found = Invoke-Graph -Method GET -Uri $probe -TolerateNotFound -TolerateForbidden -TolerateBadRequest
        if ($null -ne $found) {
            $stillPresent.Add($item)
            Write-Warning "  $($item.Kind) $($item.Id) still resolves after deletion. Directory deletes are eventually consistent - re-check in a minute."
        }
    }

    if ($unverifiable.Count -gt 0) {
        Write-Host "  $($unverifiable.Count) object(s) CANNOT be verified from this session:" -ForegroundColor Yellow
        foreach ($item in $unverifiable) {
            Write-Host "    $($item.Kind) $($item.Id)" -ForegroundColor Yellow
        }
        Write-Host '    They were not readable before the delete, so the same query cannot prove' -ForegroundColor Yellow
        Write-Host '    they are gone now. Confirm in the Microsoft 365 admin center, or read them' -ForegroundColor Yellow
        Write-Host '    as an owner:' -ForegroundColor Yellow
        Write-Host '      Connect-MgGraph -Scopes AgentRegistration.ReadWrite.All' -ForegroundColor DarkGray
        Write-Host '      Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/beta/copilot/agentRegistrations/<id>"' -ForegroundColor DarkGray
    }

    if ($stillPresent.Count -eq 0 -and $unverifiable.Count -eq 0) {
        Write-Host '  All deleted objects verified gone.' -ForegroundColor Green
    }
    elseif ($stillPresent.Count -eq 0) {
        Write-Host '  Everything that could be verified is gone.' -ForegroundColor Green
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== Summary' -ForegroundColor Cyan

$ordered |
    Select-Object Kind, @{ n = 'Name'; e = { if ($_.Label) { $_.Label } else { '(unnamed)' } } }, Id, Result |
    Format-Table -AutoSize | Out-Host

$summary = [ordered]@{
    tenantId      = $ctxTenant
    authMode      = $(if ($isAppOnly) { 'AppOnly' } else { "Delegated ($mode)" })
    planned       = $ordered.Count
    deleted       = @($ordered | Where-Object { $_.Result -like 'Deleted*' }).Count
    alreadyAbsent = @($ordered | Where-Object { $_.Result -eq 'Already absent' }).Count
    failed        = $script:Failures.Count
    purged        = @($ordered | Where-Object { $_.Result -eq 'Deleted + purged' }).Count
    objects       = @($ordered | Select-Object Kind, Id, Label, Result)
}

if ($script:Failures.Count -gt 0) {
    Write-Host ''
    Write-Host 'ACTION REQUIRED - some objects were not removed' -ForegroundColor Red
    foreach ($f in $script:Failures) {
        Write-Host "  $($f.Kind) $($f.Id) ($($f.Label)) - HTTP $($f.Status): $($f.Message)" -ForegroundColor Red
    }
    Write-Host '  The agent may still appear in the admin center. Fix the permission and re-run; deleting is idempotent.' -ForegroundColor Red
}

$registrationsRemoved = @($ordered | Where-Object { $_.Kind -eq 'Registration' -and $_.Result -like 'Deleted*' }).Count
$registrationsAbsent  = @($ordered | Where-Object { $_.Kind -eq 'Registration' -and $_.Result -eq 'Already absent' }).Count
if ($registrationsRemoved -eq 0 -and $registrationsAbsent -gt 0 -and -not $WhatIfPreference) {
    Write-Host ''
    Write-Host "  $registrationsAbsent agent registration(s) were already absent - nothing to remove." -ForegroundColor DarkGray
    Write-Host '  If the agent still shows in the admin center, the entry belongs to a different registration id than the one supplied.' -ForegroundColor DarkGray
}
elseif ($registrationsRemoved -eq 0 -and -not $WhatIfPreference) {
    Write-Host ''
    Write-Warning 'No agent registration was deleted in this run. The admin-center inventory entry is created by POST /beta/copilot/agentRegistrations and is only removed by deleting that registration id - removing the identity or the agentic user does not clear it.'
}

Write-Host ''
if ($script:Failures.Count -gt 0) {
    Write-Host 'Completed with errors.' -ForegroundColor Red
}
else {
    Write-Host 'Done.' -ForegroundColor Green
}

[pscustomobject]$summary

if ($script:Failures.Count -gt 0) { exit 1 }

Complete-A365Log -Outcome 'Succeeded'