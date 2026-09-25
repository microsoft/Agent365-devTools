# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    Tests for the dependency-ordering pass of ConvertTo-A365BulkOnboardingPlan: parents
    always precede children, independent rows keep their original CSV order, an existing
    anchor does not gate ordering, and a cycle is refused rather than silently dropped.
    Run via Run-Tests.ps1.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'TestHelpers.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..' 'A365-BulkOnboardingCsv.psm1') -Force

Write-Host 'Dependency ordering' -ForegroundColor Cyan

function New-Row {
    param([hashtable] $Values = @{})
    $row = [ordered]@{}
    foreach ($h in (Get-A365BulkOnboardingKnownHeaders)) {
        $row[$h] = if ($Values.ContainsKey($h)) { [string]$Values[$h] } else { '' }
    }
    [pscustomobject]$row
}

Test-Case 'A parent always precedes its child in the ordered plan' {
    $rows = @(
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-Count $plan.Errors 0
    $keys = @($plan.Nodes | ForEach-Object { $_.Key })
    Assert-True (([array]::IndexOf($keys, 'bp1')) -lt ([array]::IndexOf($keys, 'ai1'))) 'bp1 (the parent) must be ordered before ai1 (its child), even though the CSV lists ai1 first.'
}

Test-Case 'Independent trees keep their original CSV row order' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bpB'; DisplayName = 'BPB'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bpA'; DisplayName = 'BPA'; Sponsor = 'a@x.com' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-Count $plan.Errors 0
    $keys = @($plan.Nodes | ForEach-Object { $_.Key })
    Assert-Equal 'bpB' $keys[0] 'bpB was the first row in the CSV and has no dependency on bpA, so it keeps its place.'
    Assert-Equal 'bpA' $keys[1]
}

Test-Case 'A tree parented to an existing anchor is ready immediately, but still yields to an earlier ready root' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bpNew'; DisplayName = 'New'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bpAnchor'; ExistingId = [guid]::NewGuid().ToString() }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'aiOnAnchor'; ParentKey = 'bpAnchor'; DisplayName = 'AI'; Sponsor = 'a@x.com' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-Count $plan.Errors 0
    Assert-Count $plan.ExistingNodes 1
    $keys = @($plan.Nodes | ForEach-Object { $_.Key })
    Assert-Equal 'bpNew' $keys[0] 'bpNew is ready at indegree 0 and has the lower row number, so it goes first.'
    Assert-Equal 'aiOnAnchor' $keys[1] 'aiOnAnchor is ready immediately too (its parent is an existing anchor), so it is next.'
}

Test-Case 'A self-referencing row is its own cycle' {
    $rows = @(New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'ai1'; DisplayName = 'AI'; Sponsor = 'a@x.com' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Message -match 'dependency cycle' }).Count -gt 0)
}

Test-Case 'A three-way sibling ordering (identity -> user, identity -> registration) preserves CSV order among siblings' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentRegistration'; Key = 'r1'; ParentKey = 'ai1'; DisplayName = 'R' }
        New-Row @{ ObjectType = 'AgentUser'; Key = 'u1'; ParentKey = 'ai1'; PrincipalName = 'u@x.com' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-Count $plan.Errors 0
    $keys = @($plan.Nodes | ForEach-Object { $_.Key })
    Assert-Equal 'bp1,ai1,r1,u1' ($keys -join ',')
}

Get-A365TestResults
