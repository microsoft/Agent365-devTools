# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    Minimal, dependency-free test harness for the bulk-onboarding tests. There is no Pester
    convention elsewhere in this repository and this suite intentionally adds no new test
    dependency: every assertion is a plain function that throws on failure, and Test-Case
    catches that per test case so one failure does not stop the rest of the suite.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:A365TestResults = [System.Collections.Generic.List[object]]::new()

function Test-Case {
    <#
    .SYNOPSIS
        Runs one named test case. Failures (a thrown exception, including from an Assert-*
        helper) are caught and recorded rather than stopping the suite.
    #>
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][scriptblock] $Test
    )
    try {
        & $Test
        $script:A365TestResults.Add([pscustomobject]@{ Name = $Name; Passed = $true; Error = $null })
        Write-Host "  [PASS] $Name" -ForegroundColor Green
    }
    catch {
        $script:A365TestResults.Add([pscustomobject]@{ Name = $Name; Passed = $false; Error = $_.Exception.Message })
        Write-Host "  [FAIL] $Name" -ForegroundColor Red
        Write-Host "         $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Assert-True {
    param([Parameter(Mandatory)] $Condition, [string] $Message = 'Expected condition to be true.')
    if (-not $Condition) { throw $Message }
}

function Assert-False {
    param([Parameter(Mandatory)] $Condition, [string] $Message = 'Expected condition to be false.')
    if ($Condition) { throw $Message }
}

function Assert-Equal {
    param($Expected, $Actual, [string] $Message)
    if ($Expected -ne $Actual) {
        $text = if ($Message) { $Message } else { "Expected '$Expected' but got '$Actual'." }
        throw $text
    }
}

function Assert-Null {
    param($Value, [string] $Message = 'Expected value to be null.')
    if ($null -ne $Value) { throw $Message }
}

function Assert-NotNull {
    param($Value, [string] $Message = 'Expected value to be non-null.')
    if ($null -eq $Value) { throw $Message }
}

function Assert-Count {
    param([Parameter(Mandatory)] $Collection, [Parameter(Mandatory)][int] $Expected, [string] $Message)
    $actual = @($Collection).Count
    if ($actual -ne $Expected) {
        $text = if ($Message) { $Message } else { "Expected $Expected item(s) but got $actual." }
        throw $text
    }
}

function Assert-Contains {
    param([Parameter(Mandatory)] $Collection, $Value, [string] $Message)
    if (@($Collection) -notcontains $Value) {
        $text = if ($Message) { $Message } else { "Expected collection to contain '$Value'." }
        throw $text
    }
}

function Assert-Throws {
    param([Parameter(Mandatory)][scriptblock] $ScriptBlock, [string] $ExpectedMessagePattern, [string] $Message)
    $threw = $false
    $caught = $null
    try { & $ScriptBlock } catch { $threw = $true; $caught = $_ }
    if (-not $threw) {
        $text = if ($Message) { $Message } else { 'Expected the script block to throw, but it did not.' }
        throw $text
    }
    if ($ExpectedMessagePattern -and $caught.Exception.Message -notmatch $ExpectedMessagePattern) {
        throw "Expected exception message to match '$ExpectedMessagePattern' but got '$($caught.Exception.Message)'."
    }
}

function Reset-A365TestResults {
    $script:A365TestResults.Clear()
}

function Get-A365TestResults {
    return , @($script:A365TestResults)
}

Export-ModuleMember -Function @(
    'Test-Case', 'Assert-True', 'Assert-False', 'Assert-Equal', 'Assert-Null', 'Assert-NotNull',
    'Assert-Count', 'Assert-Contains', 'Assert-Throws', 'Reset-A365TestResults', 'Get-A365TestResults'
)
