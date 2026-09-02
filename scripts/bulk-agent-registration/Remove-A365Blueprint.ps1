<#
.SYNOPSIS
    Deletes a Microsoft Agent 365 blueprint application and its blueprint principal.

.DESCRIPTION
    THE MOST DESTRUCTIVE OPERATION IN THE SUITE. A blueprint is shared by every agent identity
    built from it, so deleting one invalidates all of them at once - which is why the other
    removal scripts deliberately never touch blueprints.

    Because of that, this script REFUSES BY DEFAULT when any agent identity still exists under
    the blueprint. It lists what it found and prints the exact command to remove them first.
    Pass -Force to cascade instead, deleting those agent identities and their agent users
    before the blueprint itself.

    The cascade is not reimplemented here: it is delegated to Remove-A365AgentRegistration.ps1,
    which already handles agent identity and agent user deletion, the ordering between them,
    and the soft-delete purge. One implementation, so a fix applies everywhere.

    Deleting an application is a SOFT delete: the object moves to /directory/deletedItems and is
    restorable for 30 days. Use -Permanent to purge it, which is IRREVERSIBLE.

    Deleting the application cascades to its service principal, so the blueprint principal does
    not need deleting separately - but it is reported so the outcome is visible rather than
    assumed.

.PARAMETER BlueprintId
    The blueprint to delete. Accepts either the application (client) id or the object id.

.PARAMETER Force
    Cascade: delete the agent identities under this blueprint, and their agent users, before
    deleting the blueprint. Without it, a blueprint that still has agent identities is refused.

    Also suppresses confirmation prompts, as elsewhere in the suite.

.PARAMETER Permanent
    After the soft delete, also purge the objects from /directory/deletedItems. IRREVERSIBLE.

.PARAMETER InspectOnly
    Resolve and report the blueprint and everything built from it, then exit without deleting
    anything. Safe to run against production.

.PARAMETER AllowPlainApplication
    Permit deleting an application that is NOT typed as an agentIdentityBlueprint. Off by
    default, so pointing this script at an ordinary app registration by mistake is refused.

.PARAMETER TenantId
    Directory (tenant) id. Optional for -Interactive / -UseDeviceCode; required for application
    authentication.

.PARAMETER Interactive
    Sign in as a user in the browser.

.PARAMETER UseDeviceCode
    Sign in as a user using the device code flow, for hosts with no browser.

.PARAMETER UseExistingConnection
    Reuse the Connect-MgGraph session already established in this PowerShell process.

.EXAMPLE
    .\Remove-A365Blueprint.ps1 -BlueprintId 0bc41111-35c2-45c4-bbeb-981f0ee9e9e5 -Interactive -InspectOnly

    Report the blueprint and every agent identity and agent user built from it, deleting
    nothing. Run this first.

.EXAMPLE
    .\Remove-A365Blueprint.ps1 -BlueprintId 0bc41111-35c2-45c4-bbeb-981f0ee9e9e5 -Interactive

    Delete a blueprint that has no agent identities left. Refused if any still exist.

.EXAMPLE
    .\Remove-A365Blueprint.ps1 -BlueprintId $bp -Interactive -Force -Permanent

    Delete the blueprint together with every agent identity and agent user built from it, and
    purge them all from deleted items. IRREVERSIBLE.

.NOTES
    Graph endpoints used:

      GET    /v1.0/applications(appId='{appId}')
      GET    /v1.0/applications/{objectId}
      GET    /beta/servicePrincipals/microsoft.graph.agentIdentity?$filter=agentIdentityBlueprintId eq '{appId}'
      GET    /beta/users/microsoft.graph.agentUser?$filter=agentIdentityBlueprintId eq '{appId}'
      GET    /v1.0/servicePrincipals?$filter=appId eq '{appId}'
      DELETE /v1.0/applications/{objectId}
      DELETE /v1.0/directory/deletedItems/{objectId}      (with -Permanent)

    Requires Application.ReadWrite.All plus the Application Administrator or Cloud Application
    Administrator role. The cascade additionally requires whatever
    Remove-A365AgentRegistration.ps1 needs, including AgentIdUser.ReadWrite.All for agent users.
#>

#requires -Version 7

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '',
    Justification = 'Credentials are converted to SecureString immediately by ConvertTo-SecureStringValue.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '',
    Justification = 'No credential pair is declared; these are Graph authentication parameters.')]
param(
    [Parameter(Mandatory)][string] $BlueprintId,

    [switch]   $Force,
    [switch]   $Permanent,
    [switch]   $InspectOnly,
    [switch]   $AllowPlainApplication,
    [switch]   $PassThru,

    [string]   $TenantId,
    [switch]   $Interactive,
    [switch]   $UseDeviceCode,
    [switch]   $UseExistingConnection,
    [string]   $ClientId,
    # [object], not [string]: an omitted [string] binds to '' , which ConvertTo-SecureStringValue
    # reports as "supplied but empty". [object] also lets a SecureString or PSCredential through
    # unconverted, which a [string] would silently stringify to its type name.
    [object]   $ClientSecret,
    [string]   $CertificateThumbprint,
    [object]   $AccessToken,

    [string]   $ScriptRoot,

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
$null = Initialize-A365Log -Path $LogPath -ScriptName 'Remove-A365Blueprint.ps1' `
    -BoundParameters $PSBoundParameters -IncludeSecrets:$LogIncludeSecrets -CorrelationId $LogCorrelationId
if ($script:LogFile) { Write-Host "  Log file           : $($script:LogFile)" -ForegroundColor DarkGray }

trap {
    Write-A365Log -Level ERROR -Message "UNHANDLED: $($_.Exception.Message)" -Detail $_.ScriptStackTrace
    Complete-A365Log -Outcome 'Failed'
    break
}

$script:GraphRoot = 'https://graph.microsoft.com/v1.0'

if ($Force) { $ConfirmPreference = 'None' }

# ---------------------------------------------------------------------------
# Helpers (copied verbatim from Remove-A365StalePrincipal.ps1 so retry, throttle
# and StrictMode behaviour stay identical across the suite)
# ---------------------------------------------------------------------------

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

function Get-GraphErrorInfo {
    param($ErrorRecord)

    $codeToStatus = @{
        'Request_ResourceNotFound'    = 404
        'ResourceNotFound'            = 404
        'itemNotFound'                = 404
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
        [switch] $RetryOnNotFound
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
            if ($info.Status -eq 400 -and $TolerateBadRequest) {
                Write-Verbose "400 tolerated for $Method $Uri - $($info.Message)"
                return $null
            }

            $transient = ($info.Status -in 429, 500, 502, 503, 504) -or
                         ($info.Status -eq 404 -and $RetryOnNotFound)

            if ($transient -and $attempt -lt $MaxAttempts) {
                $delay = [Math]::Min([Math]::Pow(2, $attempt), 20)
                Write-Verbose "Transient $($info.Status) on $Method $Uri - retry $attempt/$MaxAttempts in ${delay}s"
                Start-Sleep -Seconds $delay
                continue
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

# ---------------------------------------------------------------------------
# Step 1 - connect
# ---------------------------------------------------------------------------
Write-Step 1 'Connecting to Microsoft Graph'

$delegatedScopeSets = @(
    @('Application.ReadWrite.All', 'Directory.ReadWrite.All', 'AgentIdentity.ReadWrite.All', 'AgentIdUser.ReadWrite.All'),
    @('Application.ReadWrite.All', 'Directory.ReadWrite.All'),
    @('Application.ReadWrite.All')
)

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
        # The Agent 365 delegated scopes are preview and do not exist in every tenant. Entra rejects
        # the WHOLE scope string with AADSTS70011 if any single scope is unknown, so fall back to a
        # universally valid set rather than failing outright. Scope validation happens before a
        # device code is issued, so a failed attempt does not burn a code.
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

$authType    = [string](Get-Value $mg 'AuthType' '')
$ctxAccount  = [string](Get-Value $mg 'Account' '')
$ctxClient   = [string](Get-Value $mg 'ClientId' '')
$ctxTenant   = [string](Get-Value $mg 'TenantId' $TenantId)
$isAppOnly   = ($authType -eq 'AppOnly')

if ($isAppOnly) {
    Write-Host "  Connected app-only as $ctxClient in tenant $ctxTenant" -ForegroundColor Green
    Write-Warning 'App-only deletion requires Application.ReadWrite.All (or Application.ReadWrite.OwnedBy on an object the app owns). Permanently deleting from deletedItems is documented only for Application.ReadWrite.OwnedBy, so -Permanent may be denied.'
}
else {
    Write-Host "  Connected as $ctxAccount in tenant $ctxTenant [delegated, $mode]" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Step 2 - resolve the blueprint
# ---------------------------------------------------------------------------
Write-Step 2 'Resolving the blueprint'

# Accept either form of id. The appId lookup is tried first because that is what the rest of the
# suite calls a "blueprint id", but the object id is what DELETE needs.
$app = $null
$byAppId = Invoke-Graph -Method GET -Uri "/applications(appId='$BlueprintId')" -TolerateNotFound
if ($byAppId) {
    $app = $byAppId
}
else {
    $byObjectId = Invoke-Graph -Method GET -Uri "/applications/$BlueprintId" -TolerateNotFound
    if ($byObjectId) { $app = $byObjectId }
}

if (-not $app) {
    throw @"
No application was found for '$BlueprintId'.

Pass either the blueprint application (client) id or its object id. If the blueprint was already
deleted it may still be in deleted items for 30 days:
  GET /v1.0/directory/deletedItems/microsoft.graph.application
"@
}

$appObjectId = [string](Get-Value $app 'id' '')
$appAppId    = [string](Get-Value $app 'appId' '')
$appName     = [string](Get-Value $app 'displayName' '(unnamed)')
$appType     = [string](Get-Value $app '@odata.type' '')

Write-Host "  Blueprint    : $appName"
Write-Host "  objectId     : $appObjectId"
Write-Host "  appId        : $appAppId"
Write-Host "  type         : $(if ($appType) { $appType } else { '(untyped application)' })"

# Refuse an ordinary app registration unless explicitly allowed. Deleting the wrong application
# is unrecoverable in practice, and the type is the only reliable signal.
$isBlueprint = $appType -match 'agentIdentityBlueprint'
if (-not $isBlueprint -and -not $AllowPlainApplication) {
    throw @"
'$appName' ($appAppId) is not typed as an agentIdentityBlueprint - it reports '$(if ($appType) { $appType } else { 'a plain application' })'.

This script deletes Agent 365 blueprints. Deleting an ordinary application by mistake is not
recoverable once purged, so it is refused by default.

If you are certain, re-run with -AllowPlainApplication.
"@
}

# ---------------------------------------------------------------------------
# Step 3 - find everything built from this blueprint
# ---------------------------------------------------------------------------
Write-Step 3 'Finding agent identities and agent users built from this blueprint'

$identityFilter = "agentIdentityBlueprintId eq '$appAppId'"

# Plain assignment, NOT @(Get-GraphCollection ...). The helper returns ", @(...)" so that an
# empty result survives as an empty array rather than collapsing to $null; wrapping that in @()
# re-wraps it, giving a one-element array whose single element is the real collection. That
# silently reports "1 identity" with a blank id no matter how many there really are.
$identities = Get-GraphCollection -Uri ("https://graph.microsoft.com/beta/servicePrincipals/microsoft.graph.agentIdentity?`$filter=" + [uri]::EscapeDataString($identityFilter) + '&$select=id,displayName')
if ($null -eq $identities) { $identities = @() }

# Agent users are filtered by the blueprint directly, which catches any whose parent identity has
# already gone - those would otherwise be missed and left orphaned.
$agentUsers = @()
$userUri = "https://graph.microsoft.com/beta/users/microsoft.graph.agentUser?`$filter=" + [uri]::EscapeDataString($identityFilter) + '&$select=id,displayName,userPrincipalName'
try {
    $agentUsers = Get-GraphCollection -Uri $userUri
    if ($null -eq $agentUsers) { $agentUsers = @() }
}
catch { Write-Warning "  Could not enumerate agent users for this blueprint: $($_.Exception.Message)" }

Write-Host "  Agent identities : $($identities.Count)"
foreach ($i in $identities) {
    Write-Host ("     {0}  {1}" -f [string](Get-Value $i 'id' ''), [string](Get-Value $i 'displayName' '(unnamed)')) -ForegroundColor Gray
}
Write-Host "  Agent users      : $($agentUsers.Count)"
foreach ($u in $agentUsers) {
    Write-Host ("     {0}  {1}" -f [string](Get-Value $u 'id' ''), [string](Get-Value $u 'userPrincipalName' '(no upn)')) -ForegroundColor Gray
}

# The blueprint principal. Deleting the application cascades to it, but report it so the outcome
# is visible rather than assumed.
$principalId = ''
$principals = Get-GraphCollection -Uri ("/servicePrincipals?`$filter=" + [uri]::EscapeDataString("appId eq '$appAppId'") + '&$select=id,displayName')
if ($null -eq $principals) { $principals = @() }
if ($principals.Count -gt 0) { $principalId = [string](Get-Value $principals[0] 'id' '') }
Write-Host "  Blueprint principal : $(if ($principalId) { $principalId } else { '(none)' })"

if ($InspectOnly) {
    Write-Host ''
    Write-Host 'Inspect only - nothing was deleted.' -ForegroundColor Yellow
    if ($PassThru) {
        [pscustomobject]@{
            BlueprintAppId      = $appAppId
            BlueprintObjectId   = $appObjectId
            DisplayName         = $appName
            IsBlueprintType     = $isBlueprint
            BlueprintPrincipalId = $principalId
            AgentIdentityIds    = @($identities | ForEach-Object { [string](Get-Value $_ 'id' '') })
            AgentUserIds        = @($agentUsers  | ForEach-Object { [string](Get-Value $_ 'id' '') })
            Deleted             = $false
            InspectOnly         = $true
        }
    }
    return
}

# ---------------------------------------------------------------------------
# Step 4 - refuse, or cascade
# ---------------------------------------------------------------------------
Write-Step 4 'Checking what depends on this blueprint'

$dependents = $identities.Count + $agentUsers.Count
if ($dependents -gt 0 -and -not $Force) {
    $idList = (@($identities | ForEach-Object { "'" + [string](Get-Value $_ 'id' '') + "'" }) -join ', ')
    throw @"
Refusing to delete blueprint '$appName': $($identities.Count) agent identity/identities and $($agentUsers.Count) agent user(s) are still built from it.

A blueprint is shared by every agent identity created from it, so deleting it would invalidate
all of them at once.

Remove them first:
  .\Remove-A365AgentUser.ps1     -AgentUserId <each agent user id> -Force
  .\Remove-A365AgentIdentity.ps1 -AgentIdentityId $idList -Force

Then re-run this script. Or cascade in one step:
  .\Remove-A365Blueprint.ps1 -BlueprintId $BlueprintId -Force

Run with -InspectOnly to see the full list without deleting anything.
"@
}

$cascadeRemoved = @()
if ($dependents -gt 0) {
    Write-Host "  -Force: cascading to $($identities.Count) agent identity/identities and their agent users." -ForegroundColor Yellow

    $root = if ($ScriptRoot) { $ScriptRoot } elseif ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).ProviderPath }

    # Each object type has its own script, so the cascade calls two of them rather than one
    # combined remover. AGENT USERS FIRST: an agent user is discovered through its identity, so
    # deleting the identity first would orphan the user - still holding its licence and UPN, but
    # no longer reachable from the identity that owned it.
    $userScript     = Join-Path $root 'Remove-A365AgentUser.ps1'
    $identityScript = Join-Path $root 'Remove-A365AgentIdentity.ps1'
    foreach ($needed in @($userScript, $identityScript)) {
        if (-not (Test-Path -LiteralPath $needed)) {
            throw "The cascade needs $([IO.Path]::GetFileName($needed)), which was not found at '$root'. Remove the agent identities and their agent users first, or pass -ScriptRoot."
        }
    }

    $commonArgs = @{ Force = $true }
    if ($Permanent)         { $commonArgs.Permanent         = $true }
    if ($TenantId)          { $commonArgs.TenantId          = $TenantId }
    # The cascade runs inside this script's Graph session, so it must not try to sign in again.
    $commonArgs.UseExistingConnection = $true
    # Each cascaded script writes its own log file beside this one, under the same correlation id.
    if ($LogPath)           { $commonArgs.LogPath           = $LogPath }
    if ($LogIncludeSecrets) { $commonArgs.LogIncludeSecrets = $true }
    if ($LogCorrelationId)  { $commonArgs.LogCorrelationId  = $LogCorrelationId }

    $identityIds = @($identities | ForEach-Object { [string](Get-Value $_ 'id' '') } | Where-Object { $_ })
    $userIds     = @($agentUsers | ForEach-Object { [string](Get-Value $_ 'id' '') } | Where-Object { $_ })

    if ($PSCmdlet.ShouldProcess("$($identities.Count) agent identities and $($agentUsers.Count) agent users under '$appName'", 'Delete')) {
        if ($userIds.Count -gt 0) {
            & $userScript @commonArgs -AgentUserId $userIds
        }
        if ($identityIds.Count -gt 0) {
            & $identityScript @commonArgs -AgentIdentityId $identityIds
        }
        $cascadeRemoved = $identityIds
    }
}

# ---------------------------------------------------------------------------
# Step 5 - delete the blueprint
# ---------------------------------------------------------------------------
Write-Step 5 'Deleting the blueprint application'

$deleted = $false
if ($PSCmdlet.ShouldProcess("blueprint '$appName' ($appAppId)", 'Delete application')) {
    $null = Invoke-Graph -Method DELETE -Uri "/applications/$appObjectId" -TolerateNotFound
    $deleted = $true
    Write-Host "  Deleted application $appObjectId" -ForegroundColor Green

    # A 204 is not proof on its own; read it back. Deleting an application cascades to its
    # service principal, so check that too rather than assuming the documented behaviour held.
    Start-Sleep -Seconds 2
    $still = Invoke-Graph -Method GET -Uri "/applications/$appObjectId" -TolerateNotFound
    if ($still) {
        Write-Warning '  The application still resolves after the delete. Re-run to confirm, or check directory replication.'
    }
    else {
        Write-Host '  Verified: the application no longer resolves.' -ForegroundColor Green
    }

    if ($principalId) {
        $spStill = Invoke-Graph -Method GET -Uri "/servicePrincipals/$principalId" -TolerateNotFound
        if ($spStill) {
            Write-Warning "  The blueprint principal $principalId still exists; deleting it explicitly."
            $null = Invoke-Graph -Method DELETE -Uri "/servicePrincipals/$principalId" -TolerateNotFound
        }
        else {
            Write-Host '  Verified: the blueprint principal was removed with the application.' -ForegroundColor Green
        }
    }
}

# ---------------------------------------------------------------------------
# Step 6 - purge
# ---------------------------------------------------------------------------
$purged = $false
if ($Permanent -and $deleted) {
    Write-Step 6 'Purging from deleted items (IRREVERSIBLE)'
    if ($PSCmdlet.ShouldProcess("blueprint '$appName' in deleted items", 'Purge permanently')) {
        Start-Sleep -Seconds 3
        try {
            $null = Invoke-Graph -Method DELETE -Uri "/directory/deletedItems/$appObjectId" -TolerateNotFound -TolerateForbidden
            Write-Host '  Purge requested.' -ForegroundColor Green
            $purged = $true
        }
        catch {
            Write-Warning "  Purge failed: $($_.Exception.Message). The soft delete has still happened; the object remains in deleted items for 30 days."
        }
    }
}

Write-Host ''
Write-Host '========================================================' -ForegroundColor Green
Write-Host ' Blueprint removal complete' -ForegroundColor Green
Write-Host '========================================================' -ForegroundColor Green
Write-Host ("  Blueprint          : {0} ({1})" -f $appName, $appAppId)
Write-Host ("  Deleted            : {0}" -f $deleted)
Write-Host ("  Cascaded identities: {0}" -f $cascadeRemoved.Count)
Write-Host ("  Purged             : {0}" -f $purged)
Write-Host ''
Write-Host 'Done.' -ForegroundColor Green

if ($PassThru) {
    [pscustomobject]@{
        BlueprintAppId       = $appAppId
        BlueprintObjectId    = $appObjectId
        DisplayName          = $appName
        IsBlueprintType      = $isBlueprint
        BlueprintPrincipalId = $principalId
        AgentIdentityIds     = @($cascadeRemoved)
        AgentUserIds         = @($agentUsers | ForEach-Object { [string](Get-Value $_ 'id' '') })
        Deleted              = $deleted
        Purged               = $purged
        InspectOnly          = $false
    }
}

Complete-A365Log -Outcome 'Succeeded'