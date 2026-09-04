# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
    Provisions many Microsoft Agent 365 agents from a single CSV file by driving
    A365-AutomationOrchestrator.ps1 once per row, in dependency order.

.DESCRIPTION
    A365-AutomationOrchestrator.ps1 provisions ONE agent per invocation. This script reads a
    CSV describing many blueprints, agent identities, agent users and registrations, works
    out which rows depend on which, and calls the orchestrator once per row that needs
    creating - in an order where every parent runs before its children - threading the id
    each parent produces into the children that need it.

    The orchestrator is invoked IN-PROCESS with the PowerShell call operator, in the same
    runspace as this script. No new process is started and nothing is serialized: a
    SecureString client secret or an X509Certificate2 passed to -ClientSecret /
    -Certificate stays the exact same live object for every row that uses it.

    CSV SHAPE

    One row per object, with these columns always present (values are blank when not
    applicable to that row):

        ObjectType   Blueprint | AgentIdentity | AgentUser | AgentRegistration
        Key          Unique name for this row (case-insensitive), referenced by children
        ParentKey    The Key of this row's parent. Blank for Blueprint (the root); required
                     for AgentIdentity (parent is a Blueprint row) and for AgentUser /
                     AgentRegistration (parent is an AgentIdentity row) unless ExistingId is set
        ExistingId   Blueprint appId or AgentIdentity objectId of an object that already
                     exists. Marks the row as a reference only - nothing is created or
                     changed for it, and ParentKey must be blank. Not valid on AgentUser or
                     AgentRegistration rows.

    Every other column maps to one -Blueprint*/-AgentIdentity*/-AgentUser*/
    -AgentRegistration* parameter of the orchestrator; see the CSV column reference below.
    A column that does not apply to a row's ObjectType must be left blank - it is rejected,
    not silently ignored, so a value never disappears without a warning.

        Column                                    Applies to                Orchestrator parameter
        --------------------------------------    ------------------------  ---------------------------------------
        DisplayName                                Blueprint, AgentIdentity,  *DisplayName
                                                    AgentUser, AgentRegistration
        Description                                Blueprint, AgentRegistration  *Description
        Owner (semicolon-separated)                 Blueprint, AgentIdentity,  *Owner
                                                    AgentRegistration
        Sponsor (semicolon-separated)                Blueprint, AgentIdentity   *Sponsor            (required to create)
        RequireOwnerAssignment (true/false)          Blueprint, AgentIdentity   *RequireOwnerAssignment
        RequiredPermissionJson (JSON array)           Blueprint, AgentIdentity   *RequiredPermission
        GrantAdminConsent (true/false)                Blueprint, AgentIdentity   *GrantAdminConsent
        SkipInheritablePermissions (true/false)       Blueprint                  BlueprintSkipInheritablePermissions
        NewClientSecret (true/false)                  Blueprint                  BlueprintNewClientSecret
        KeyVaultName                                 Blueprint                  BlueprintKeyVaultName
        KeyVaultSecretName                            Blueprint                  BlueprintKeyVaultSecretName
        ManagedIdentityPrincipalId (GUID)              Blueprint                  BlueprintManagedIdentityPrincipalId
        Tag (semicolon-separated)                     AgentIdentity              AgentIdentityTag
        CustomSecurityAttributeJson (JSON)             AgentIdentity              AgentIdentityCustomSecurityAttribute
        SkipCustomSecurityAttributeValidation (bool)   AgentIdentity              AgentIdentitySkipCustomSecurityAttributeValidation
        PrincipalName                                 AgentUser                  AgentUserPrincipalName      (required, valid UPN)
        MailNickname                                  AgentUser                  AgentUserMailNickname
        ManagerUserId (GUID) / ManagerUpn (xor)        AgentUser                  AgentUserManagerUserId / AgentUserManagerUpn
        UsageLocation                                 AgentUser                  AgentUserUsageLocation      (required if AssignLicense)
        AssignLicense (true/false)                     AgentUser                  AgentUserAssignLicense
        LicenseSkuId (GUID) / LicenseSkuPartNumber     AgentUser                  AgentUserLicenseSkuId / AgentUserLicenseSkuPartNumber (one required if AssignLicense)
        OwnerId (GUID)                                 AgentRegistration          AgentRegistrationOwnerId
        Auth (Same | Interactive)                      AgentRegistration          AgentRegistrationAuth
        ParameterJson (JSON object)                    all types                  merged into *Parameter

    -ParameterJson is an allowlisted escape hatch for these advanced leaf-script parameters:
    Blueprint: ClientSecretLifetimeDays, ExposedScopeValue, FederatedCredentialName;
    AgentIdentity: Disabled; AgentUser: DisabledPlans, MaxRetries, RetryDelaySeconds,
    NoDefaultOwner, NoOwnershipSelfHeal; AgentRegistration: ManagedByAppId,
    RolePropagationDelaySeconds, SkipDisplayNameNormalization. Names must be complete, not
    abbreviated. Tenant, authentication, credential, parent, action, output and logging
    parameters are rejected.

    VALIDATION RUNS BEFORE ANY GRAPH CALL

    The whole file is checked - required headers, non-empty data, a recognised ObjectType,
    unique keys, legal ExistingId/ParentKey shapes, valid GUIDs, the fields required to
    create each type, a parent of the right type, no row parented to an AgentUser or
    AgentRegistration row (they are leaves), and no dependency cycle - and every error found
    is reported together, with its row and column, before a single row is executed.

    DEPENDENCY ORDER AND FAILURE HANDLING

    Rows run in an order where every parent completes before its children, preserving the
    CSV's own order among rows that do not depend on each other. A row whose parent did not
    resolve to a live id is never sent to Graph: it is marked SkippedDependency, and the
    independent trees elsewhere in the file still run. A row that itself fails is marked
    Failed and its descendants are then skipped the same way. The script's own exit code is
    non-zero whenever any row ends Failed or SkippedDependency, but only after the console
    summary and -OutputJsonPath report have both been written.

.PARAMETER CsvPath
    Path to the bulk onboarding CSV.

.PARAMETER TenantId
    The Microsoft Entra tenant every row is provisioned into.

.PARAMETER ScriptRoot
    Directory containing A365-AutomationOrchestrator.ps1. Defaults to this script's own
    directory - keep the two files together, or pass this explicitly.

.PARAMETER OutputJsonPath
    Write one aggregate JSON report (kind A365BulkProvisioningRunReport) covering every row.
    A365-AutomationOrchestrator.ps1 is never given its own -OutputJsonPath, so exactly one
    report file is produced for the whole run, not one per row.

.PARAMETER IncludeBlueprintSecretsInOutput
    Include any blueprint client secret in the aggregate report. Off by default, exactly
    like the orchestrator's own switch of the same name. Authentication inputs (client
    secret, certificate, access token, and so on) are never written to the report, with or
    without this switch.

.PARAMETER ClientId
.PARAMETER ClientSecret
.PARAMETER CertificateThumbprint
.PARAMETER Certificate
.PARAMETER CertificatePath
.PARAMETER CertificatePassword
.PARAMETER UseManagedIdentity
.PARAMETER AccessToken
.PARAMETER Interactive
.PARAMETER SkipPermissionCheck
    Authentication, forwarded verbatim to every row's orchestrator call. Exactly one method
    must be supplied - see A365-AutomationOrchestrator.ps1's own help for the full
    description of each.

.PARAMETER LogPath
.PARAMETER LogIncludeSecrets
.PARAMETER LogCorrelationId
    Logging, forwarded to every row's orchestrator call so every log file produced by this
    run - the orchestrator's and every step script's - shares one correlation id. A
    correlation id is generated when omitted, exactly like the orchestrator.

.PARAMETER OrchestratorInvoker
    Testing seam. A scriptblock that replaces the real call to
    A365-AutomationOrchestrator.ps1: it receives the row's argument hashtable as its only
    positional argument and must return an object shaped like the orchestrator's own
    $result (or throw, to simulate a hard failure). Not used in normal operation - omit it
    to invoke the real orchestrator script.

.EXAMPLE
    .\A365-BulkOnboarding.ps1 -CsvPath .\sample-bulk-onboarding.csv -TenantId <tid> `
        -ClientId <appId> -ClientSecret $env:A365_CLIENT_SECRET -WhatIf

    Validates the CSV and prints the full dependency plan without calling Graph.

.EXAMPLE
    .\A365-BulkOnboarding.ps1 -CsvPath .\sample-bulk-onboarding.csv -TenantId <tid> `
        -ClientId <appId> -ClientSecret $env:A365_CLIENT_SECRET `
        -LogPath 'C:\A365\Logs' -OutputJsonPath 'C:\A365\bulk-run.json'

    Provisions every row, sharing one log correlation id across every child script, and
    writes one aggregate report.
#>

#requires -Version 7

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)][ValidateNotNullOrWhiteSpace()][string] $CsvPath,
    [Parameter(Mandatory)][ValidateNotNullOrWhiteSpace()][string] $TenantId,

    [string] $ScriptRoot,
    [string] $OutputJsonPath,
    [switch] $IncludeBlueprintSecretsInOutput,

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

    [string] $LogPath,
    [switch] $LogIncludeSecrets,
    [string] $LogCorrelationId,

    [scriptblock] $OrchestratorInvoker
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'A365-BulkOnboardingCsv.psm1') -Force

# ---------------------------------------------------------------------------
# Report writing - JSON + best-effort Windows ACL tightening, mirroring the orchestrator's
# own Write-RunReport / Protect-ReportFile so a report containing a secret is never left
# world-readable, and so -WhatIf still leaves a report behind (Set-Content and New-Item
# implement ShouldProcess and would otherwise refuse to write under -WhatIf, even though
# producing the report is not the mutating operation being previewed).
# ---------------------------------------------------------------------------

function Protect-A365BulkReportFile {
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

function Write-A365BulkReport {
    param([Parameter(Mandatory)] $Report, [Parameter(Mandatory)][string] $Path, [switch] $WithSecrets)

    $previousWhatIfPreference = $WhatIfPreference
    try {
        $WhatIfPreference = $false

        $directory = Split-Path -Parent $Path
        if ($directory -and -not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }
        if ($WithSecrets) {
            New-Item -ItemType File -Path $Path -Force | Out-Null
            Protect-A365BulkReportFile -Path $Path
        }
        $Report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
    }
    finally {
        $WhatIfPreference = $previousWhatIfPreference
    }

    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    Write-Host ''
    if ($WithSecrets) {
        Write-Host "Bulk run report written to $resolved" -ForegroundColor Green
        Write-Host '  This file CONTAINS the blueprint client secret(s) in plaintext. Treat it as a credential.' -ForegroundColor Yellow
    }
    else {
        Write-Host "Bulk run report written to $resolved (secrets redacted)" -ForegroundColor Green
    }
    return $resolved
}

function New-A365BulkReportRowEntry {
    # $ErrorMessage (not $Error) - $Error is PowerShell's automatic error-history variable,
    # and a parameter of the same name shadows it for the whole function body.
    param([int] $Row, [string] $Key, [string] $ObjectType, [string] $ParentKey, [string] $Status,
          [string] $ResolvedId, [string] $ErrorMessage, $ChildResult)
    [ordered]@{
        row         = $Row
        key         = $Key
        objectType  = $ObjectType
        parentKey   = $(if ($ParentKey) { $ParentKey } else { $null })
        status      = $Status
        resolvedId  = $(if ($ResolvedId) { $ResolvedId } else { $null })
        error       = $(if ($ErrorMessage) { $ErrorMessage } else { $null })
        childResult = $ChildResult
    }
}

# ---------------------------------------------------------------------------
# Resolve the orchestrator location (skipped when a test invoker replaces the real call).
# ---------------------------------------------------------------------------

$root = if ($ScriptRoot) { $ScriptRoot } elseif ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).ProviderPath }
try {
    if (-not (Test-Path -LiteralPath $root)) { throw "-ScriptRoot '$root' does not exist." }
    $root = (Resolve-Path -LiteralPath $root).ProviderPath
    $orchestratorPath = Join-Path $root 'A365-AutomationOrchestrator.ps1'
    if (-not $OrchestratorInvoker -and -not (Test-Path -LiteralPath $orchestratorPath)) {
        throw "A365-AutomationOrchestrator.ps1 was not found in '$root'. Keep it together with A365-BulkOnboarding.ps1, or pass -ScriptRoot."
    }
}
catch {
    # -ErrorAction Continue is load-bearing, not decoration: under this script's own
    # $ErrorActionPreference = 'Stop', a plain Write-Error is itself a terminating error
    # that would propagate past the exit below and unwind whatever script called this one
    # (it is designed to be invoked in-process with the call operator, possibly for more
    # than one CSV/tenant in the same run). The explicit override keeps the failure - and
    # its exit code - scoped to this invocation only.
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 1
}

if ([string]::IsNullOrWhiteSpace($LogCorrelationId)) { $LogCorrelationId = [guid]::NewGuid().ToString('N').Substring(0, 8) }

Write-Host ''
Write-Host '=== Agent 365 bulk CSV onboarding ===' -ForegroundColor Cyan
Write-Host ("  CSV file           : {0}" -f $CsvPath)
Write-Host ("  Tenant             : {0}" -f $TenantId)
Write-Host ("  Correlation id     : {0}" -f $LogCorrelationId)

# ---------------------------------------------------------------------------
# Read + validate the whole file before any Graph work happens.
# ---------------------------------------------------------------------------

try {
    $rawRows = Import-A365BulkOnboardingCsv -Path $CsvPath
}
catch {
    # See the -ErrorAction Continue note above: without it, this Write-Error would itself
    # be a terminating error under $ErrorActionPreference = 'Stop' and the exit below would
    # never run, propagating the failure into whatever script called this one.
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 1
}

$plan = ConvertTo-A365BulkOnboardingPlan -Rows $rawRows

$reportBase = [ordered]@{
    schemaVersion = '1.0'
    kind          = 'A365BulkProvisioningRunReport'
    generatedUtc  = [DateTimeOffset]::UtcNow.ToString('o')
    source        = [ordered]@{
        csvPath       = $CsvPath
        tenantId      = $TenantId
        correlationId = $LogCorrelationId
        logPath       = $(if ($LogPath) { $LogPath } else { $null })
    }
}

if ($plan.Errors.Count -gt 0) {
    Write-Host ''
    Write-Host 'Validation failed - fix the CSV and re-run. No Graph calls were made.' -ForegroundColor Red
    foreach ($e in ($plan.Errors | Sort-Object Row, Column)) {
        Write-Host ("  Row {0,-4} {1,-38} {2}" -f $e.Row, $e.Column, $e.Message) -ForegroundColor Red
    }

    $report = [ordered]@{}
    foreach ($k in $reportBase.Keys) { $report[$k] = $reportBase[$k] }
    $report.mode             = 'ValidationFailed'
    $report.totals           = [ordered]@{ total = 0; existing = 0; planned = 0; succeeded = 0; failed = 0; skippedDependency = 0; validationErrors = $plan.Errors.Count }
    $report.rows             = @()
    $report.validationErrors = @($plan.Errors | ForEach-Object { [ordered]@{ row = $_.Row; column = $_.Column; message = $_.Message } })

    if ($OutputJsonPath) {
        # Never let a reporting problem mask the real validation result (mirrors the
        # orchestrator's own Write-RunReport guard).
        try { Write-A365BulkReport -Report $report -Path $OutputJsonPath | Out-Null }
        catch { Write-Warning "Could not write the run report to '$OutputJsonPath': $($_.Exception.Message)" }
    }
    exit 1
}
Write-Host ("  Existing anchors   : {0}" -f $plan.ExistingNodes.Count)

# ---------------------------------------------------------------------------
# One outer confirmation for the whole run. Under -WhatIf (or a declined -Confirm), the
# plan is validated and printed in full, and no child script is invoked.
# ---------------------------------------------------------------------------

$shouldRun = $PSCmdlet.ShouldProcess(
    "$($plan.Nodes.Count) row(s) from '$CsvPath'",
    "Provision Agent 365 objects in tenant '$TenantId'")

if (-not $shouldRun) {
    $rowsReport = [System.Collections.Generic.List[object]]::new()
    foreach ($n in $plan.ExistingNodes) {
        $rowsReport.Add((New-A365BulkReportRowEntry -Row $n.RowNumber -Key $n.Key -ObjectType $n.ObjectType -ParentKey $null -Status 'Existing' -ResolvedId $n.ExistingId))
    }
    foreach ($n in $plan.Nodes) {
        $rowsReport.Add((New-A365BulkReportRowEntry -Row $n.RowNumber -Key $n.Key -ObjectType $n.ObjectType -ParentKey $n.ParentKey -Status 'Planned'))
    }

    Write-Host ''
    Write-Host '[WhatIf] Dependency plan (no Graph calls were made):' -ForegroundColor Yellow
    $rowsReport | ForEach-Object { [pscustomobject]@{ Row = $_.row; Key = $_.key; ObjectType = $_.objectType; ParentKey = $_.parentKey; Status = $_.status } } |
        Sort-Object Row | Format-Table -AutoSize | Out-Host

    $report = [ordered]@{}
    foreach ($k in $reportBase.Keys) { $report[$k] = $reportBase[$k] }
    $report.mode             = 'WhatIf'
    $report.totals           = [ordered]@{
        total = $rowsReport.Count
        existing = $plan.ExistingNodes.Count
        planned  = $plan.Nodes.Count
        succeeded = 0; failed = 0; skippedDependency = 0; validationErrors = 0
    }
    $report.rows             = @($rowsReport)
    $report.validationErrors = @()

    if ($OutputJsonPath) {
        try { Write-A365BulkReport -Report $report -Path $OutputJsonPath | Out-Null }
        catch { Write-Warning "Could not write the run report to '$OutputJsonPath': $($_.Exception.Message)" }
    }
    exit 0
}

# ---------------------------------------------------------------------------
# Real run. Exactly one authentication method is required up front - failing every row
# with the identical error the orchestrator would give is no more informative than failing
# once here, and this way nothing is attempted at all. Skipped for the test invoker, which
# supplies its own fake authentication surface.
# ---------------------------------------------------------------------------

$authSplat = @{}
foreach ($k in 'ClientId', 'ClientSecret', 'CertificateThumbprint', 'Certificate', 'CertificatePath',
               'CertificatePassword', 'UseManagedIdentity', 'AccessToken', 'Interactive', 'SkipPermissionCheck') {
    if ($PSBoundParameters.ContainsKey($k)) { $authSplat[$k] = $PSBoundParameters[$k] }
}
if (-not $OrchestratorInvoker) {
    # Raised as Write-Error + exit 1 (not a bare throw): this script is designed to be
    # called in-process with the call operator, possibly in a loop over several CSVs/
    # tenants by another script, and an uncaught terminating error here would propagate
    # through every level of that caller instead of stopping at this invocation.
    try {
        $authModes = @()
        if ($Interactive) { $authModes += 'Interactive' }
        if ($AccessToken) { $authModes += 'AccessToken' }
        if ($UseManagedIdentity) { $authModes += 'ManagedIdentity' }
        if ($CertificateThumbprint -or $Certificate -or $CertificatePath) { $authModes += 'Certificate' }
        if ($ClientSecret -or $env:A365_CLIENT_SECRET) { $authModes += 'ClientSecret' }
        if ($authModes.Count -eq 0) {
            throw 'No authentication method was specified. Pass -ClientId with -ClientSecret, -CertificateThumbprint, -Certificate or -CertificatePath (or use -UseManagedIdentity / -AccessToken), or pass -Interactive to sign in as a user.'
        }
        if ($authModes.Count -gt 1) {
            throw "Conflicting authentication options ($($authModes -join ', ')). Supply exactly one."
        }
        # New-A365AgentUser.ps1 is client-credentials only (mirrors the orchestrator's own
        # precondition). Every AgentUser row would fail identically, so refuse once, up front,
        # instead of once per row.
        $isAppOnly = $authModes[0] -in @('ClientSecret', 'Certificate', 'ManagedIdentity')
        if (-not $isAppOnly -and @($plan.Nodes | Where-Object { $_.ObjectType -eq 'AgentUser' }).Count -gt 0) {
            throw "The CSV has AgentUser row(s), which require app-only authentication, but this run authenticates as '$($authModes[0])'. Re-run with -ClientId plus -ClientSecret / -CertificateThumbprint / -UseManagedIdentity."
        }
    }
    catch {
        # See the -ErrorAction Continue note above: required here too, for the same reason.
        Write-Error $_.Exception.Message -ErrorAction Continue
        exit 1
    }
}

$logSplat = @{}
if ($PSBoundParameters.ContainsKey('LogPath')) {
    $logSplat['LogPath'] = $LogPath
    $logSplat['LogCorrelationId'] = $LogCorrelationId
    if ($LogIncludeSecrets) { $logSplat['LogIncludeSecrets'] = $true }
}

$stateMap = New-A365BulkOnboardingStateMap -Plan $plan

Write-Host ''
Write-Host 'Provisioning rows in dependency order:' -ForegroundColor Cyan
foreach ($node in $plan.Nodes) {
    $rowState = $stateMap[$node.Key]

    if (-not (Test-A365BulkOnboardingRowReady -Node $node -StateMap $stateMap)) {
        $rowState.Status = 'SkippedDependency'
        $rowState.Error  = "Parent row '$($node.ParentKey)' did not complete successfully."
        Write-Host ("  [row {0}] {1} ({2}) - SKIPPED: {3}" -f $node.RowNumber, $node.Key, $node.ObjectType, $rowState.Error) -ForegroundColor Yellow
        continue
    }

    $parentResolvedId = if ($node.ParentKey -and $stateMap.ContainsKey($node.ParentKey)) { $stateMap[$node.ParentKey].ResolvedId } else { $null }

    Write-Host ("  [row {0}] {1} ({2}) ..." -f $node.RowNumber, $node.Key, $node.ObjectType) -ForegroundColor Cyan

    # Argument building and the orchestrator call share one try/catch: a failure in either
    # is this row's failure, never an uncaught error that would unwind whatever invoked this
    # script (it is designed to be called in-process with the call operator, possibly for
    # more than one CSV/tenant in the same run).
    #
    # In-process call: the same runspace, no new pwsh.exe, so a SecureString or
    # X509Certificate2 in $rowArgs stays the exact live object for every row.
    try {
        $rowArgs = New-A365BulkOnboardingOrchestratorArguments -Node $node -TenantId $TenantId -ParentResolvedId $parentResolvedId
        foreach ($k in $authSplat.Keys) { $rowArgs[$k] = $authSplat[$k] }
        foreach ($k in $logSplat.Keys)  { $rowArgs[$k] = $logSplat[$k] }

        if ($OrchestratorInvoker) {
            $childResult = & $OrchestratorInvoker $rowArgs
        }
        else {
            $childResult = & $orchestratorPath @rowArgs
        }
    }
    catch {
        $rowState.Status = 'Failed'
        $rowState.Error  = $_.Exception.Message
        Write-Warning ("  Row '{0}' failed: {1}" -f $node.Key, $_.Exception.Message)
        continue
    }

    $rowState.ChildResult = $childResult
    $outcome = Resolve-A365BulkOnboardingRowOutcome -ObjectType $node.ObjectType -ChildResult $childResult
    if ($outcome.Success) {
        $rowState.Status     = 'Succeeded'
        $rowState.ResolvedId = $outcome.ResolvedId
        Write-Host ("  -> {0}" -f $outcome.ResolvedId) -ForegroundColor Green
    }
    else {
        $rowState.Status = 'Failed'
        $rowState.Error  = $outcome.Error
        Write-Warning ("  Row '{0}' did not complete: {1}" -f $node.Key, $outcome.Error)
    }
}

# ---------------------------------------------------------------------------
# Aggregate report - one file for the whole run, never one per row.
# ---------------------------------------------------------------------------

$rowsReport = [System.Collections.Generic.List[object]]::new()
foreach ($n in $plan.ExistingNodes) {
    $rowsReport.Add((New-A365BulkReportRowEntry -Row $n.RowNumber -Key $n.Key -ObjectType $n.ObjectType -ParentKey $null -Status 'Existing' -ResolvedId $n.ExistingId))
}
foreach ($node in $plan.Nodes) {
    $s = $stateMap[$node.Key]
    $redactedChild = if ($s.ChildResult) { ConvertTo-A365BulkOnboardingRedactedValue -Value $s.ChildResult -IncludeSecrets:$IncludeBlueprintSecretsInOutput } else { $null }
    $rowsReport.Add((New-A365BulkReportRowEntry -Row $node.RowNumber -Key $node.Key -ObjectType $node.ObjectType -ParentKey $node.ParentKey `
            -Status $s.Status -ResolvedId $s.ResolvedId -ErrorMessage $s.Error -ChildResult $redactedChild))
}

$totals = [ordered]@{
    total             = $rowsReport.Count
    existing          = @($rowsReport | Where-Object { $_.status -eq 'Existing' }).Count
    succeeded         = @($rowsReport | Where-Object { $_.status -eq 'Succeeded' }).Count
    failed            = @($rowsReport | Where-Object { $_.status -eq 'Failed' }).Count
    skippedDependency = @($rowsReport | Where-Object { $_.status -eq 'SkippedDependency' }).Count
    validationErrors  = 0
}

Write-Host ''
Write-Host ('=' * 78) -ForegroundColor Cyan
Write-Host '=== Bulk onboarding complete ===' -ForegroundColor Cyan
Write-Host ('=' * 78) -ForegroundColor Cyan
$rowsReport | ForEach-Object { [pscustomobject]@{ Row = $_.row; Key = $_.key; ObjectType = $_.objectType; Status = $_.status; ResolvedId = $_.resolvedId; Error = $_.error } } |
    Sort-Object Row | Format-Table -AutoSize | Out-Host
Write-Host ("Total {0}  existing {1}  succeeded {2}  failed {3}  skipped {4}" -f `
        $totals.total, $totals.existing, $totals.succeeded, $totals.failed, $totals.skippedDependency)

$report = [ordered]@{}
foreach ($k in $reportBase.Keys) { $report[$k] = $reportBase[$k] }
$report.mode             = 'Run'
$report.totals           = $totals
$report.rows             = @($rowsReport)
$report.validationErrors = @()

if ($OutputJsonPath) {
    # Every row's Graph work is already done by this point; a reporting failure here must
    # never mask that real outcome or the exit code below (mirrors the orchestrator's own
    # Write-RunReport guard).
    try { Write-A365BulkReport -Report $report -Path $OutputJsonPath -WithSecrets:$IncludeBlueprintSecretsInOutput | Out-Null }
    catch { Write-Warning "Could not write the run report to '$OutputJsonPath': $($_.Exception.Message)" }
}

if ($totals.failed -gt 0 -or $totals.skippedDependency -gt 0) {
    Write-Host ''
    Write-Host 'One or more rows did not complete. See the table above (and the report, if requested) for detail.' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
exit 0
