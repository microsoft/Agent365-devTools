# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    Validates the shipped sample-bulk-onboarding.csv: it must parse and validate cleanly,
    with the expected node/anchor counts, so it stays a trustworthy starting point for
    anyone trying the tool for the first time. Run via Run-Tests.ps1.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'TestHelpers.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..' 'A365-BulkOnboardingCsv.psm1') -Force

Write-Host 'Sample CSV validation' -ForegroundColor Cyan

$samplePath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'sample-bulk-onboarding.csv')).ProviderPath

Test-Case 'sample-bulk-onboarding.csv parses without an I/O error' {
    $rows = Import-A365BulkOnboardingCsv -Path $samplePath
    Assert-True ($rows.Count -gt 0)
}

Test-Case 'sample-bulk-onboarding.csv validates with zero errors' {
    $rows = Import-A365BulkOnboardingCsv -Path $samplePath
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    if ($plan.Errors.Count -gt 0) {
        $detail = ($plan.Errors | ForEach-Object { "Row $($_.Row) $($_.Column): $($_.Message)" }) -join '; '
        throw "Expected zero validation errors, found $($plan.Errors.Count): $detail"
    }
}

Test-Case 'sample-bulk-onboarding.csv has the expected create/anchor split and root ordering' {
    $rows = Import-A365BulkOnboardingCsv -Path $samplePath
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-Count $plan.ExistingNodes 1
    Assert-True ($plan.Nodes.Count -gt 0)
    Assert-Equal 'ExpenseBlueprint' $plan.Nodes[0].Key 'The first Blueprint row has no dependency, so it is ordered first.'
    Assert-Equal 'Blueprint' $plan.Nodes[0].ObjectType
}

Test-Case 'sample-bulk-onboarding.csv every create node has a resolvable parent (or is a root)' {
    $rows = Import-A365BulkOnboardingCsv -Path $samplePath
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    $knownKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($n in $plan.ExistingNodes) { [void]$knownKeys.Add($n.Key) }
    foreach ($n in $plan.Nodes) {
        if ($n.ParentKey) {
            Assert-True $knownKeys.Contains($n.ParentKey) "Row '$($n.Key)' names ParentKey '$($n.ParentKey)', which must already be resolvable by the time it runs."
        }
        [void]$knownKeys.Add($n.Key)
    }
}

Get-A365TestResults
