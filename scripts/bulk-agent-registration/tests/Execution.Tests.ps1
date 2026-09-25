# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    End-to-end tests for A365-BulkOnboarding.ps1 itself, run against the fake
    orchestrator in tests\fixtures (never against A365-AutomationOrchestrator.ps1 or Graph).

    A365-BulkOnboarding.ps1 ends several paths with a bare `exit`. Calling it here with
    the call operator (`&`), never by dot-sourcing, is deliberate and load-bearing: `exit`
    inside a script invoked with `&` unwinds only that script's own scope and returns control
    to the caller with $LASTEXITCODE set - it does not stop this test file, Run-Tests.ps1, or
    the session running them. Dot-sourcing would share scope with the exit call and end the
    whole run instead, which is exactly the failure mode this file is designed to avoid.

    Because the fixture orchestrator runs in the very same runspace as this test (nested `&`
    calls do not start a new process), a $global: list set up before the call is still visible
    afterwards - proving both that dependency ordering/failure propagation behave correctly
    and that a live object (not a copy) reaches the fixture on every row.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'TestHelpers.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..' 'A365-BulkOnboardingCsv.psm1') -Force

$script:WrapperPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'A365-BulkOnboarding.ps1')).ProviderPath
$script:FixturesDir  = (Resolve-Path (Join-Path $PSScriptRoot 'fixtures')).ProviderPath

function New-A365TestCsvRow {
    <#
    .SYNOPSIS
        Builds one CSV row as a PSCustomObject with every known header present (blank
        unless overridden), so Export-Csv always writes a well-formed, fully aligned row.
    #>
    param([hashtable] $Values)
    $row = [ordered]@{}
    foreach ($h in (Get-A365BulkOnboardingKnownHeaders)) {
        $row[$h] = if ($Values.ContainsKey($h)) { [string]$Values[$h] } else { '' }
    }
    [pscustomobject]$row
}

function New-A365TestCsvFile {
    param([Parameter(Mandatory)][object[]] $Rows)
    $path = Join-Path ([IO.Path]::GetTempPath()) "a365-bulk-exec-$([guid]::NewGuid()).csv"
    $Rows | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding utf8
    return $path
}

# ---------------------------------------------------------------------------
# Ordering + failure propagation + independent continuation + object identity +
# correlation id sharing, all in one run: two independent trees, one fails partway.
# ---------------------------------------------------------------------------

Write-Host 'Execution: dependency ordering and failure propagation' -ForegroundColor Cyan

$rows = @(
    New-A365TestCsvRow @{ ObjectType = 'Blueprint'; Key = 'BP-A'; DisplayName = 'BP Alpha'; Sponsor = 'sponsor@contoso.com' }
    New-A365TestCsvRow @{ ObjectType = 'AgentIdentity'; Key = 'AI-A1'; ParentKey = 'BP-A'; DisplayName = 'AI Alpha FAIL'; Sponsor = 'sponsor@contoso.com' }
    New-A365TestCsvRow @{ ObjectType = 'AgentUser'; Key = 'AU-A1U'; ParentKey = 'AI-A1'; PrincipalName = 'au-a1u@contoso.com' }
    New-A365TestCsvRow @{ ObjectType = 'AgentRegistration'; Key = 'AR-A1R'; ParentKey = 'AI-A1'; DisplayName = 'AR Alpha'; Auth = 'Same' }
    New-A365TestCsvRow @{ ObjectType = 'Blueprint'; Key = 'BP-B'; DisplayName = 'BP Beta'; Sponsor = 'sponsor@contoso.com' }
    New-A365TestCsvRow @{ ObjectType = 'AgentIdentity'; Key = 'AI-B1'; ParentKey = 'BP-B'; DisplayName = 'AI Beta'; Sponsor = 'sponsor@contoso.com' }
    New-A365TestCsvRow @{ ObjectType = 'AgentUser'; Key = 'AU-B1U'; ParentKey = 'AI-B1'; PrincipalName = 'fail@contoso.com'; ManagerUpn = 'manager@contoso.com'; UsageLocation = 'US'; AssignLicense = 'false' }
    New-A365TestCsvRow @{ ObjectType = 'AgentRegistration'; Key = 'AR-B1R'; ParentKey = 'AI-B1'; DisplayName = 'AR Beta'; Auth = 'Same' }
)
$csvPath    = New-A365TestCsvFile -Rows $rows
$reportPath = Join-Path ([IO.Path]::GetTempPath()) "a365-bulk-exec-report-$([guid]::NewGuid()).json"

Test-Case 'A real run: independent trees, one failing branch, live object identity, shared correlation id' {
    $global:A365BulkExecFixtureCalls = [System.Collections.Generic.List[object]]::new()
    $secretMarker = [pscustomobject]@{ Marker = [guid]::NewGuid() }
    $logDir = Join-Path ([IO.Path]::GetTempPath()) "a365-bulk-exec-log-$([guid]::NewGuid())"
    try {
        & $script:WrapperPath -CsvPath $csvPath -TenantId 'tenant-id' -ScriptRoot $script:FixturesDir `
            -ClientId 'test-client' -ClientSecret $secretMarker `
            -LogPath $logDir -LogCorrelationId 'exec-test-corr' -OutputJsonPath $reportPath `
            *> $null
        $exitCode = $LASTEXITCODE

        Assert-Equal 1 $exitCode 'A run with a failed row must exit 1.'
        Assert-Equal 6 $global:A365BulkExecFixtureCalls.Count 'Only the 6 non-skipped rows should reach the fixture (2 are SkippedDependency).'

        $identities = @($global:A365BulkExecFixtureCalls | ForEach-Object { $_.ClientSecretIdentity } | Select-Object -Unique)
        Assert-Count $identities 1 'Every row must receive the exact same ClientSecret object (in-process, no copy).'

        $corrIds = @($global:A365BulkExecFixtureCalls | ForEach-Object { $_.LogCorrelationId } | Select-Object -Unique)
        Assert-Count $corrIds 1 'Every row must share one correlation id.'
        Assert-Equal 'exec-test-corr' $corrIds[0]

        Assert-True (Test-Path -LiteralPath $reportPath) 'The aggregate report file must be written even though a row failed.'
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $byKey = @{}
        foreach ($r in $report.rows) { $byKey[$r.key] = $r }

        Assert-Equal 'Succeeded' $byKey['BP-A'].status
        Assert-Equal 'Failed' $byKey['AI-A1'].status
        Assert-Equal 'SkippedDependency' $byKey['AU-A1U'].status
        Assert-Equal 'SkippedDependency' $byKey['AR-A1R'].status
        Assert-Equal 'Succeeded' $byKey['BP-B'].status
        Assert-Equal 'Succeeded' $byKey['AI-B1'].status
        Assert-Equal 'Failed' $byKey['AU-B1U'].status
        Assert-Equal 'Succeeded' $byKey['AR-B1R'].status 'AgentRegistration is a sibling of AgentUser, not its dependent, so it must still run.'

        Assert-Equal 2 $report.totals.failed
        Assert-Equal 2 $report.totals.skippedDependency
        Assert-Equal 4 $report.totals.succeeded
    }
    finally {
        Remove-Variable -Name A365BulkExecFixtureCalls -Scope Global -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $csvPath -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $reportPath -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $logDir -Recurse -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# -WhatIf: validated and printed, but the fixture is never invoked.
# ---------------------------------------------------------------------------

Write-Host 'Execution: -WhatIf makes no invocations' -ForegroundColor Cyan

Test-Case '-WhatIf plans the run without calling the orchestrator' {
    $whatIfRows = @(
        New-A365TestCsvRow @{ ObjectType = 'Blueprint'; Key = 'BP-WI'; DisplayName = 'WhatIf Blueprint'; Sponsor = 'sponsor@contoso.com' }
        New-A365TestCsvRow @{ ObjectType = 'AgentIdentity'; Key = 'AI-WI'; ParentKey = 'BP-WI'; DisplayName = 'WhatIf Identity'; Sponsor = 'sponsor@contoso.com' }
    )
    $whatIfCsv    = New-A365TestCsvFile -Rows $whatIfRows
    $whatIfReport = Join-Path ([IO.Path]::GetTempPath()) "a365-bulk-exec-whatif-$([guid]::NewGuid()).json"
    $global:A365BulkExecFixtureCalls = [System.Collections.Generic.List[object]]::new()
    try {
        & $script:WrapperPath -CsvPath $whatIfCsv -TenantId 'tenant-id' -ScriptRoot $script:FixturesDir `
            -ClientId 'test-client' -ClientSecret 'unused-under-whatif' `
            -OutputJsonPath $whatIfReport -WhatIf `
            *> $null
        $exitCode = $LASTEXITCODE

        Assert-Equal 0 $exitCode '-WhatIf must exit 0.'
        Assert-Equal 0 $global:A365BulkExecFixtureCalls.Count 'No row may reach the orchestrator under -WhatIf.'

        $report = Get-Content -LiteralPath $whatIfReport -Raw | ConvertFrom-Json
        Assert-Equal 'WhatIf' $report.mode
        Assert-Equal 2 $report.totals.planned
    }
    finally {
        Remove-Variable -Name A365BulkExecFixtureCalls -Scope Global -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $whatIfCsv -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $whatIfReport -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# Auth preflight: refused up front, before any row is attempted, never per-row.
# ---------------------------------------------------------------------------

Write-Host 'Execution: auth preflight checks run before any row' -ForegroundColor Cyan

function New-A365AuthPreflightCsv {
    # A single AgentUser row is enough to exercise the AgentUser-vs-app-only-auth rule;
    # it also must never validation-fail on its own, so every other requirement is met.
    $rows = @(
        New-A365TestCsvRow @{ ObjectType = 'Blueprint'; Key = 'BP-Auth'; DisplayName = 'Auth Blueprint'; Sponsor = 'sponsor@contoso.com' }
        New-A365TestCsvRow @{ ObjectType = 'AgentIdentity'; Key = 'AI-Auth'; ParentKey = 'BP-Auth'; DisplayName = 'Auth Identity'; Sponsor = 'sponsor@contoso.com' }
        New-A365TestCsvRow @{ ObjectType = 'AgentUser'; Key = 'AU-Auth'; ParentKey = 'AI-Auth'; PrincipalName = 'au-auth@contoso.com' }
    )
    New-A365TestCsvFile -Rows $rows
}

Test-Case 'No authentication method specified is refused before any row runs' {
    $csv = New-A365AuthPreflightCsv
    $global:A365BulkExecFixtureCalls = [System.Collections.Generic.List[object]]::new()
    # A previously-set A365_CLIENT_SECRET in the host environment must not make this test
    # pass for the wrong reason (the wrapper treats it as a valid ClientSecret auth mode) -
    # clear it for the duration of the test and always restore whatever was there before.
    $savedClientSecret = $env:A365_CLIENT_SECRET
    Remove-Item -Path Env:\A365_CLIENT_SECRET -ErrorAction SilentlyContinue
    try {
        & $script:WrapperPath -CsvPath $csv -TenantId 'tenant-id' -ScriptRoot $script:FixturesDir -Confirm:$false *> $null
        $exitCode = $LASTEXITCODE
        Assert-Equal 1 $exitCode
        Assert-Equal 0 $global:A365BulkExecFixtureCalls.Count 'Nothing may reach the orchestrator when auth cannot be resolved.'
    }
    finally {
        if ($null -ne $savedClientSecret) { $env:A365_CLIENT_SECRET = $savedClientSecret }
        else { Remove-Item -Path Env:\A365_CLIENT_SECRET -ErrorAction SilentlyContinue }
        Remove-Variable -Name A365BulkExecFixtureCalls -Scope Global -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $csv -ErrorAction SilentlyContinue
    }
}

Test-Case 'Conflicting authentication options are refused before any row runs' {
    $csv = New-A365AuthPreflightCsv
    $global:A365BulkExecFixtureCalls = [System.Collections.Generic.List[object]]::new()
    try {
        & $script:WrapperPath -CsvPath $csv -TenantId 'tenant-id' -ScriptRoot $script:FixturesDir `
            -ClientId 'test-client' -ClientSecret 'a-secret' -Interactive -Confirm:$false `
            *> $null
        $exitCode = $LASTEXITCODE
        Assert-Equal 1 $exitCode
        Assert-Equal 0 $global:A365BulkExecFixtureCalls.Count 'Nothing may reach the orchestrator when auth options conflict.'
    }
    finally {
        Remove-Variable -Name A365BulkExecFixtureCalls -Scope Global -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $csv -ErrorAction SilentlyContinue
    }
}

Test-Case 'An AgentUser row with non-app-only auth (-Interactive) is refused before any row runs' {
    $csv = New-A365AuthPreflightCsv
    $global:A365BulkExecFixtureCalls = [System.Collections.Generic.List[object]]::new()
    try {
        & $script:WrapperPath -CsvPath $csv -TenantId 'tenant-id' -ScriptRoot $script:FixturesDir `
            -Interactive -Confirm:$false `
            *> $null
        $exitCode = $LASTEXITCODE
        Assert-Equal 1 $exitCode 'The AgentUser/app-only-auth mismatch must be refused, not attempted.'
        Assert-Equal 0 $global:A365BulkExecFixtureCalls.Count 'This must be a true preflight: not even the Blueprint/AgentIdentity rows may run first.'
    }
    finally {
        Remove-Variable -Name A365BulkExecFixtureCalls -Scope Global -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $csv -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# Validation failure: refused before any row is attempted, and every error across every row
# is reported together in the JSON report - not just the first one found.
# ---------------------------------------------------------------------------

Write-Host 'Execution: CSV validation failure end-to-end' -ForegroundColor Cyan

Test-Case 'A CSV with validation errors is refused before any row runs, reporting every error via mode=ValidationFailed' {
    $rows = @(
        New-A365TestCsvRow @{ ObjectType = 'Blueprint'; Key = 'BP-Bad'; DisplayName = 'BP' }                                  # missing Sponsor
        New-A365TestCsvRow @{ ObjectType = 'AgentUser'; Key = 'AU-Bad'; ParentKey = 'noSuchKey'; PrincipalName = 'not-a-upn' } # bad ParentKey + bad UPN
    )
    $csv        = New-A365TestCsvFile -Rows $rows
    $reportPath = Join-Path ([IO.Path]::GetTempPath()) "a365-bulk-exec-badplan-$([guid]::NewGuid()).json"
    $global:A365BulkExecFixtureCalls = [System.Collections.Generic.List[object]]::new()
    try {
        & $script:WrapperPath -CsvPath $csv -TenantId 'tenant-id' -ScriptRoot $script:FixturesDir `
            -ClientId 'test-client' -ClientSecret 'unused' -OutputJsonPath $reportPath -Confirm:$false `
            *> $null
        $exitCode = $LASTEXITCODE

        Assert-Equal 1 $exitCode 'A validation failure must exit 1.'
        Assert-Equal 0 $global:A365BulkExecFixtureCalls.Count 'No row may reach the orchestrator when validation fails.'

        Assert-True (Test-Path -LiteralPath $reportPath) 'A report must still be written for a validation failure.'
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Assert-Equal 'ValidationFailed' $report.mode
        Assert-Count $report.rows 0
        Assert-True ($report.validationErrors.Count -ge 3) "Expected every error across every row (missing Sponsor, bad ParentKey, bad UPN), got $($report.validationErrors.Count)."
        Assert-Equal $report.validationErrors.Count $report.totals.validationErrors
        Assert-Equal 0 $report.totals.succeeded
        Assert-Equal 0 $report.totals.failed
    }
    finally {
        Remove-Variable -Name A365BulkExecFixtureCalls -Scope Global -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $csv -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $reportPath -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# Existing anchors: a Blueprint/AgentIdentity row with ExistingId resolves its children's
# parent id without ever invoking the orchestrator for the anchor row itself.
# ---------------------------------------------------------------------------

Write-Host 'Execution: existing Blueprint/AgentIdentity anchors' -ForegroundColor Cyan

Test-Case 'Existing Blueprint and AgentIdentity anchors resolve their children''s parent id, and the anchor rows never call the orchestrator' {
    $bpAnchorId = [guid]::NewGuid().ToString()
    $aiAnchorId = [guid]::NewGuid().ToString()
    $rows = @(
        New-A365TestCsvRow @{ ObjectType = 'Blueprint'; Key = 'BP-Anchor'; ExistingId = $bpAnchorId }
        New-A365TestCsvRow @{ ObjectType = 'AgentIdentity'; Key = 'AI-OnBpAnchor'; ParentKey = 'BP-Anchor'; DisplayName = 'AI On Anchor'; Sponsor = 'sponsor@contoso.com' }
        New-A365TestCsvRow @{ ObjectType = 'AgentIdentity'; Key = 'AI-Anchor'; ExistingId = $aiAnchorId }
        New-A365TestCsvRow @{ ObjectType = 'AgentRegistration'; Key = 'AR-OnAiAnchor'; ParentKey = 'AI-Anchor'; DisplayName = 'AR On Anchor'; Auth = 'Same' }
    )
    $csv        = New-A365TestCsvFile -Rows $rows
    $reportPath = Join-Path ([IO.Path]::GetTempPath()) "a365-bulk-exec-anchors-$([guid]::NewGuid()).json"
    $global:A365BulkExecFixtureCalls = [System.Collections.Generic.List[object]]::new()
    try {
        & $script:WrapperPath -CsvPath $csv -TenantId 'tenant-id' -ScriptRoot $script:FixturesDir `
            -ClientId 'test-client' -ClientSecret 'unused' -OutputJsonPath $reportPath -Confirm:$false `
            *> $null
        $exitCode = $LASTEXITCODE

        Assert-Equal 0 $exitCode 'Both create rows on existing anchors must succeed.'
        Assert-Equal 2 $global:A365BulkExecFixtureCalls.Count 'Only the 2 create rows may reach the orchestrator; the 2 anchor rows must not.'
        Assert-Equal $bpAnchorId $global:A365BulkExecFixtureCalls[0].UseExistingBlueprint 'The AgentIdentity row must resolve its parent id from the Blueprint anchor.'
        Assert-Equal $aiAnchorId $global:A365BulkExecFixtureCalls[1].UseExistingAgentIdentity 'The AgentRegistration row must resolve its parent id from the AgentIdentity anchor.'

        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $byKey = @{}
        foreach ($r in $report.rows) { $byKey[$r.key] = $r }
        Assert-Equal 'Existing' $byKey['BP-Anchor'].status
        Assert-Equal $bpAnchorId $byKey['BP-Anchor'].resolvedId
        Assert-Equal 'Existing' $byKey['AI-Anchor'].status
        Assert-Equal $aiAnchorId $byKey['AI-Anchor'].resolvedId
        Assert-Equal 'Succeeded' $byKey['AI-OnBpAnchor'].status
        Assert-Equal 'Succeeded' $byKey['AR-OnAiAnchor'].status
    }
    finally {
        Remove-Variable -Name A365BulkExecFixtureCalls -Scope Global -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $csv -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $reportPath -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# ScriptRoot resolution: refused before any row is attempted, never per-row.
# ---------------------------------------------------------------------------

Write-Host 'Execution: -ScriptRoot / orchestrator-not-found preflight' -ForegroundColor Cyan

Test-Case 'A -ScriptRoot that does not exist is refused before any row runs' {
    $rows = @(New-A365TestCsvRow @{ ObjectType = 'Blueprint'; Key = 'BP-Root'; DisplayName = 'BP'; Sponsor = 'sponsor@contoso.com' })
    $csv = New-A365TestCsvFile -Rows $rows
    $missingRoot = Join-Path ([IO.Path]::GetTempPath()) "a365-bulk-missing-root-$([guid]::NewGuid())"
    try {
        & $script:WrapperPath -CsvPath $csv -TenantId 'tenant-id' -ScriptRoot $missingRoot `
            -ClientId 'test-client' -ClientSecret 'unused' -Confirm:$false `
            *> $null
        Assert-Equal 1 $LASTEXITCODE 'A nonexistent -ScriptRoot must be refused.'
    }
    finally {
        Remove-Item -LiteralPath $csv -ErrorAction SilentlyContinue
    }
}

Test-Case 'A -ScriptRoot without A365-AutomationOrchestrator.ps1 is refused before any row runs' {
    $rows = @(New-A365TestCsvRow @{ ObjectType = 'Blueprint'; Key = 'BP-Root'; DisplayName = 'BP'; Sponsor = 'sponsor@contoso.com' })
    $csv = New-A365TestCsvFile -Rows $rows
    $emptyRoot = Join-Path ([IO.Path]::GetTempPath()) "a365-bulk-empty-root-$([guid]::NewGuid())"
    New-Item -ItemType Directory -Path $emptyRoot -Force | Out-Null
    try {
        & $script:WrapperPath -CsvPath $csv -TenantId 'tenant-id' -ScriptRoot $emptyRoot `
            -ClientId 'test-client' -ClientSecret 'unused' -Confirm:$false `
            *> $null
        Assert-Equal 1 $LASTEXITCODE 'A -ScriptRoot missing the orchestrator script must be refused.'
    }
    finally {
        Remove-Item -LiteralPath $csv -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $emptyRoot -Recurse -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# Non-throwing failure (summary.failedSteps, no throw) end-to-end for AgentRegistration -
# proves the wrapper reports it as Failed rather than treating the absence of a thrown
# exception as success.
# ---------------------------------------------------------------------------

Write-Host 'Execution: non-throwing failedSteps failure end-to-end (AgentRegistration)' -ForegroundColor Cyan

Test-Case 'An AgentRegistration failedSteps failure (no throw) is reported as Failed, not silently treated as Succeeded' {
    $rows = @(
        New-A365TestCsvRow @{ ObjectType = 'Blueprint'; Key = 'BP-Reg'; DisplayName = 'Reg Blueprint'; Sponsor = 'sponsor@contoso.com' }
        New-A365TestCsvRow @{ ObjectType = 'AgentIdentity'; Key = 'AI-Reg'; ParentKey = 'BP-Reg'; DisplayName = 'Reg Identity'; Sponsor = 'sponsor@contoso.com' }
        New-A365TestCsvRow @{ ObjectType = 'AgentRegistration'; Key = 'AR-Reg'; ParentKey = 'AI-Reg'; DisplayName = 'Reg Agent FAIL'; Auth = 'Same' }
    )
    $csv        = New-A365TestCsvFile -Rows $rows
    $reportPath = Join-Path ([IO.Path]::GetTempPath()) "a365-bulk-exec-regfail-$([guid]::NewGuid()).json"
    try {
        & $script:WrapperPath -CsvPath $csv -TenantId 'tenant-id' -ScriptRoot $script:FixturesDir `
            -ClientId 'test-client' -ClientSecret 'unused' -OutputJsonPath $reportPath -Confirm:$false `
            *> $null
        Assert-Equal 1 $LASTEXITCODE 'A non-throwing failedSteps failure must still fail the run.'

        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $row = @($report.rows | Where-Object { $_.key -eq 'AR-Reg' })[0]
        Assert-Equal 'Failed' $row.status
        Assert-True ($row.error -match 'Registration')
        # The identity row must still succeed: a non-throwing sibling failure must not mark it
        # SkippedDependency or Failed.
        $identityRow = @($report.rows | Where-Object { $_.key -eq 'AI-Reg' })[0]
        Assert-Equal 'Succeeded' $identityRow.status
    }
    finally {
        Remove-Item -LiteralPath $csv -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $reportPath -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# Aggregate report redaction: a secret the fixture returns is redacted by default and
# restored only with -IncludeBlueprintSecretsInOutput, exactly once for the whole run.
# ---------------------------------------------------------------------------

Write-Host 'Execution: aggregate report redaction' -ForegroundColor Cyan

function Invoke-A365SecretReportRun {
    param([switch] $IncludeSecrets)
    $rows = @(New-A365TestCsvRow @{ ObjectType = 'Blueprint'; Key = 'BP-Secret'; DisplayName = 'Secret Blueprint'; Sponsor = 'sponsor@contoso.com'; NewClientSecret = 'true' })
    $csv    = New-A365TestCsvFile -Rows $rows
    $report = Join-Path ([IO.Path]::GetTempPath()) "a365-bulk-exec-secret-$([guid]::NewGuid()).json"
    try {
        & $script:WrapperPath -CsvPath $csv -TenantId 'tenant-id' -ScriptRoot $script:FixturesDir `
            -ClientId 'test-client' -ClientSecret 'unused' -OutputJsonPath $report -IncludeBlueprintSecretsInOutput:$IncludeSecrets `
            *> $null
        return (Get-Content -LiteralPath $report -Raw | ConvertFrom-Json)
    }
    finally {
        Remove-Item -LiteralPath $csv -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $report -ErrorAction SilentlyContinue
    }
}

Test-Case 'Aggregate report redacts a client secret by default' {
    $report = Invoke-A365SecretReportRun
    $row = @($report.rows | Where-Object { $_.key -eq 'BP-Secret' })[0]
    Assert-Equal '(redacted)' $row.childResult.secrets.clientSecret
}

Test-Case 'Aggregate report keeps the client secret with -IncludeBlueprintSecretsInOutput' {
    $report = Invoke-A365SecretReportRun -IncludeSecrets
    $row = @($report.rows | Where-Object { $_.key -eq 'BP-Secret' })[0]
    Assert-Equal 'super-secret-value' $row.childResult.secrets.clientSecret
}

Get-A365TestResults
