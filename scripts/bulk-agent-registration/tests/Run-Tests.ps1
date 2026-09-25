# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
    Runs every dependency-free test file for the bulk onboarding CSV tool and reports a
    combined pass/fail summary.

.DESCRIPTION
    Discovers every *.Tests.ps1 file in this directory, runs each with the call operator,
    and collects the [pscustomobject] results each one prints as its own last pipeline
    output (see TestHelpers.psm1's Get-A365TestResults). There is no external test
    framework dependency - this is a plain script, runnable anywhere PowerShell 7 runs:

        pwsh -File tests/Run-Tests.ps1

    Exits 0 when every test case passed, 1 otherwise (including when a test file itself
    throws instead of completing) - so this can gate a CI step the same way a proper test
    runner would.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testFiles = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.Tests.ps1' | Sort-Object Name
if (@($testFiles).Count -eq 0) {
    Write-Warning "No *.Tests.ps1 files were found in '$PSScriptRoot'."
    exit 1
}

$allResults = [System.Collections.Generic.List[object]]::new()
$fileFailures = [System.Collections.Generic.List[object]]::new()

foreach ($file in $testFiles) {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor DarkCyan
    Write-Host "=== $($file.Name) ===" -ForegroundColor DarkCyan
    Write-Host ('=' * 78) -ForegroundColor DarkCyan
    try {
        # The call operator, not dot-sourcing: a test file that invokes
        # A365-BulkOnboarding.ps1 relies on `&` here to keep that script's own `exit`
        # calls scoped to it rather than to this runner (see Execution.Tests.ps1's header).
        $results = & $file.FullName
        foreach ($r in $results) { $allResults.Add($r) }
    }
    catch {
        Write-Host "  [FILE ERROR] $($file.Name): $($_.Exception.Message)" -ForegroundColor Red
        $fileFailures.Add([pscustomobject]@{ File = $file.Name; Error = $_.Exception.Message })
    }
}

Write-Host ''
Write-Host ('=' * 78) -ForegroundColor Cyan
Write-Host '=== Summary ===' -ForegroundColor Cyan
Write-Host ('=' * 78) -ForegroundColor Cyan

$failed = @($allResults | Where-Object { -not $_.Passed })
$passed = @($allResults | Where-Object { $_.Passed })

Write-Host ("Test cases : {0} passed, {1} failed, {2} total" -f $passed.Count, $failed.Count, $allResults.Count)
if ($fileFailures.Count -gt 0) {
    Write-Host ("Test files that errored outright: {0}" -f $fileFailures.Count) -ForegroundColor Red
    foreach ($f in $fileFailures) { Write-Host "  $($f.File): $($f.Error)" -ForegroundColor Red }
}
if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Host 'Failed cases:' -ForegroundColor Red
    foreach ($f in $failed) { Write-Host "  [FAIL] $($f.Name): $($f.Error)" -ForegroundColor Red }
}

if ($failed.Count -gt 0 -or $fileFailures.Count -gt 0) {
    exit 1
}
exit 0
