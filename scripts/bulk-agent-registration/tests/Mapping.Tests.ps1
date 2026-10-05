# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    Tests for the row -> orchestrator argument mapping, the state map / readiness gate, and
    child-result interpretation (Resolve-A365BulkOnboardingRowOutcome) in
    A365-BulkOnboardingCsv.psm1. Run via Run-Tests.ps1.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'TestHelpers.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..' 'A365-BulkOnboardingCsv.psm1') -Force

Write-Host 'Row mapping, state and outcome resolution' -ForegroundColor Cyan

function New-Row {
    param([hashtable] $Values = @{})
    $row = [ordered]@{}
    foreach ($h in (Get-A365BulkOnboardingKnownHeaders)) {
        $row[$h] = if ($Values.ContainsKey($h)) { [string]$Values[$h] } else { '' }
    }
    [pscustomobject]$row
}

# ---------------------------------------------------------------------------
# New-A365BulkOnboardingOrchestratorArguments
# ---------------------------------------------------------------------------

Test-Case 'Blueprint arguments: switch set, TenantId forwarded, blank/false/empty skipped' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com'; RequireOwnerAssignment = 'false' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-Count $plan.Errors 0
    $args = New-A365BulkOnboardingOrchestratorArguments -Node $plan.Nodes[0] -TenantId 'tid' -ParentResolvedId $null
    Assert-Equal 'tid' $args['TenantId']
    Assert-Equal $true $args['NewBlueprint']
    Assert-Equal 'BP' $args['BlueprintDisplayName']
    Assert-False $args.ContainsKey('BlueprintRequireOwnerAssignment') 'A false bool must not be forwarded (mirrors an omitted switch).'
    Assert-False $args.ContainsKey('BlueprintDescription') 'A blank column must not be forwarded.'
    Assert-False $args.ContainsKey('UseExistingBlueprint') 'Blueprint has no parent-ref parameter.'
}

Test-Case 'AgentIdentity arguments: parent-ref parameter carries the resolved parent id' {
    $rows = @(New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    $args = New-A365BulkOnboardingOrchestratorArguments -Node $plan.Nodes[0] -TenantId 'tid' -ParentResolvedId 'blueprint-app-id'
    Assert-Equal $true $args['NewAgentIdentity']
    Assert-Equal 'blueprint-app-id' $args['UseExistingBlueprint']
}

Test-Case 'A missing resolved parent id throws instead of sending an incomplete call' {
    $rows = @(New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-Throws { New-A365BulkOnboardingOrchestratorArguments -Node $plan.Nodes[0] -TenantId 'tid' -ParentResolvedId $null } 'no resolved parent id'
}

Test-Case 'An explicit empty ParameterJson object ({}) is still forwarded' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com'; ParameterJson = '{}' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-Count $plan.Errors 0
    $args = New-A365BulkOnboardingOrchestratorArguments -Node $plan.Nodes[0] -TenantId 'tid' -ParentResolvedId $null
    Assert-True $args.ContainsKey('BlueprintParameter')
    Assert-Equal 0 $args['BlueprintParameter'].Count
}

Test-Case 'A stringarray column with values is forwarded as an array' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com;b@x.com'; Owner = 'o@x.com' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    $args = New-A365BulkOnboardingOrchestratorArguments -Node $plan.Nodes[0] -TenantId 'tid' -ParentResolvedId $null
    Assert-Count $args['BlueprintSponsor'] 2
    Assert-Count $args['BlueprintOwner'] 1
}

Test-Case 'TenantId is omitted entirely when not supplied' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    $args = New-A365BulkOnboardingOrchestratorArguments -Node $plan.Nodes[0] -TenantId '' -ParentResolvedId $null
    Assert-False $args.ContainsKey('TenantId')
}

# ---------------------------------------------------------------------------
# State map + readiness
# ---------------------------------------------------------------------------

Test-Case 'State map seeds existing rows as Existing and create rows as Pending' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bpAnchor'; ExistingId = [guid]::NewGuid().ToString() }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bpAnchor'; DisplayName = 'AI'; Sponsor = 'a@x.com' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    $state = New-A365BulkOnboardingStateMap -Plan $plan
    Assert-Equal 'Existing' $state['bpAnchor'].Status
    Assert-Equal 'Pending' $state['ai1'].Status
}

Test-Case 'State map lookup is case-insensitive' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'BpMixedCase'; DisplayName = 'BP'; Sponsor = 'a@x.com' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    $state = New-A365BulkOnboardingStateMap -Plan $plan
    Assert-True $state.ContainsKey('bpmixedcase')
}

Test-Case 'A root node (no ParentKey) is always ready' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    $state = New-A365BulkOnboardingStateMap -Plan $plan
    Assert-True (Test-A365BulkOnboardingRowReady -Node $plan.Nodes[0] -StateMap $state)
}

Test-Case 'A node is ready once its parent has Succeeded' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    $state = New-A365BulkOnboardingStateMap -Plan $plan
    $childNode = @($plan.Nodes | Where-Object { $_.Key -eq 'ai1' })[0]
    Assert-False (Test-A365BulkOnboardingRowReady -Node $childNode -StateMap $state) 'Parent is still Pending.'
    $state['bp1'].Status = 'Succeeded'
    Assert-True (Test-A365BulkOnboardingRowReady -Node $childNode -StateMap $state)
}

Test-Case 'A node is not ready when its parent Failed or was SkippedDependency' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    $state = New-A365BulkOnboardingStateMap -Plan $plan
    $childNode = @($plan.Nodes | Where-Object { $_.Key -eq 'ai1' })[0]
    $state['bp1'].Status = 'Failed'
    Assert-False (Test-A365BulkOnboardingRowReady -Node $childNode -StateMap $state)
    $state['bp1'].Status = 'SkippedDependency'
    Assert-False (Test-A365BulkOnboardingRowReady -Node $childNode -StateMap $state)
}

Test-Case 'A 3-generation dependency skip cascades: grandparent Failed -> parent SkippedDependency -> grandchild not ready' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentRegistration'; Key = 'r1'; ParentKey = 'ai1'; DisplayName = 'R' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-Count $plan.Errors 0
    $state = New-A365BulkOnboardingStateMap -Plan $plan
    $aiNode = @($plan.Nodes | Where-Object { $_.Key -eq 'ai1' })[0]
    $rNode  = @($plan.Nodes | Where-Object { $_.Key -eq 'r1' })[0]

    # bp1 fails, so ai1 (its direct child) is not ready and would be marked SkippedDependency
    # by the caller - exactly what A365-BulkOnboarding.ps1 does in its own loop.
    $state['bp1'].Status = 'Failed'
    Assert-False (Test-A365BulkOnboardingRowReady -Node $aiNode -StateMap $state) 'ai1 is not ready: its parent bp1 failed.'
    $state['ai1'].Status = 'SkippedDependency'

    # r1 is two generations removed from the original failure (bp1 -> ai1 -> r1) and must
    # still be refused: it is ready only when its own direct parent is Existing/Succeeded,
    # and ai1 is neither.
    Assert-False (Test-A365BulkOnboardingRowReady -Node $rNode -StateMap $state) 'r1 must not be ready: its parent ai1 was itself skipped, two generations removed from the original failure.'
}

# ---------------------------------------------------------------------------
# Get-A365BulkOnboardingMember
# ---------------------------------------------------------------------------

Write-Host 'Get-A365BulkOnboardingMember: dictionary/PSCustomObject-safe member lookup' -ForegroundColor Cyan

Test-Case 'Finds a key on a Hashtable case-insensitively' {
    $r = Get-A365BulkOnboardingMember -InputObject @{ Foo = 'bar' } -Name 'foo'
    Assert-True $r.Found
    Assert-Equal 'bar' $r.Value
}
Test-Case 'Finds a key on an [ordered] dictionary' {
    $r = Get-A365BulkOnboardingMember -InputObject ([ordered]@{ Foo = 'bar' }) -Name 'Foo'
    Assert-True $r.Found
    Assert-Equal 'bar' $r.Value
}
Test-Case 'Reports not-found for a missing dictionary key' {
    $r = Get-A365BulkOnboardingMember -InputObject @{ Foo = 'bar' } -Name 'Missing'
    Assert-False $r.Found
}
Test-Case 'Finds a property on a PSCustomObject' {
    $r = Get-A365BulkOnboardingMember -InputObject ([pscustomobject]@{ Foo = 'bar' }) -Name 'Foo'
    Assert-True $r.Found
    Assert-Equal 'bar' $r.Value
}
Test-Case 'Reports not-found for a missing PSCustomObject property' {
    $r = Get-A365BulkOnboardingMember -InputObject ([pscustomobject]@{ Foo = 'bar' }) -Name 'Missing'
    Assert-False $r.Found
}
Test-Case '$null input reports not-found and never throws' {
    $r = Get-A365BulkOnboardingMember -InputObject $null -Name 'Anything'
    Assert-False $r.Found
    Assert-Null $r.Value
}

# ---------------------------------------------------------------------------
# Resolve-A365BulkOnboardingRowOutcome
# ---------------------------------------------------------------------------

Test-Case 'Blueprint outcome resolves success from summary.identifiers.blueprintAppId' {
    $child = [pscustomobject]@{ summary = [pscustomobject]@{ identifiers = [pscustomobject]@{ blueprintAppId = 'app-1' } } }
    $outcome = Resolve-A365BulkOnboardingRowOutcome -ObjectType 'Blueprint' -ChildResult $child
    Assert-True $outcome.Success
    Assert-Equal 'app-1' $outcome.ResolvedId
}

Test-Case 'AgentIdentity outcome resolves success from summary.identifiers.agentIdentityId' {
    $child = [pscustomobject]@{ summary = [pscustomobject]@{ identifiers = [pscustomobject]@{ agentIdentityId = 'id-1' } } }
    $outcome = Resolve-A365BulkOnboardingRowOutcome -ObjectType 'AgentIdentity' -ChildResult $child
    Assert-True $outcome.Success
    Assert-Equal 'id-1' $outcome.ResolvedId
}

Test-Case 'AgentUser outcome resolves success from summary.identifiers.agentUserId' {
    $child = [pscustomobject]@{ summary = [pscustomobject]@{ identifiers = [pscustomobject]@{ agentUserId = 'user-1' } } }
    $outcome = Resolve-A365BulkOnboardingRowOutcome -ObjectType 'AgentUser' -ChildResult $child
    Assert-True $outcome.Success
    Assert-Equal 'user-1' $outcome.ResolvedId
}

Test-Case 'AgentRegistration outcome resolves success from summary.identifiers.registrationId' {
    $child = [pscustomobject]@{ summary = [pscustomobject]@{ identifiers = [pscustomobject]@{ registrationId = 'T_123' } } }
    $outcome = Resolve-A365BulkOnboardingRowOutcome -ObjectType 'AgentRegistration' -ChildResult $child
    Assert-True $outcome.Success
    Assert-Equal 'T_123' $outcome.ResolvedId
}

Test-Case 'A non-throwing failure (failedSteps present, no id) surfaces its detail text' {
    $child = [pscustomobject]@{
        summary = [pscustomobject]@{
            identifiers = [pscustomobject]@{ agentUserId = $null }
            failedSteps = @([pscustomobject]@{ step = 'AgentUser'; status = 'Failed'; detail = 'Insufficient privileges' })
        }
    }
    $outcome = Resolve-A365BulkOnboardingRowOutcome -ObjectType 'AgentUser' -ChildResult $child
    Assert-False $outcome.Success
    Assert-True ($outcome.Error -match 'Insufficient privileges')
}

Test-Case 'No identifier and no failedSteps still produces a non-null error message' {
    $child = [pscustomobject]@{ summary = [pscustomobject]@{ identifiers = [pscustomobject]@{} } }
    $outcome = Resolve-A365BulkOnboardingRowOutcome -ObjectType 'AgentRegistration' -ChildResult $child
    Assert-False $outcome.Success
    Assert-NotNull $outcome.Error
}

# ---------------------------------------------------------------------------
# Regression: raw IDictionary shapes (Hashtable / [ordered]@{}) - exactly what
# A365-AutomationOrchestrator.ps1's own shallow [pscustomobject]$result cast actually leaves
# summary / identifiers / failedSteps as (only the outermost object becomes a PSCustomObject).
# Get-Member cannot see a dictionary's keys, so code that used Get-Member to test whether a
# key was present before reading it silently treated every real orchestrator result as having
# no resolved identifier and no failedSteps detail. Every test above used an all-PSCustomObject
# double and could not catch that; these use the real shape instead.
# ---------------------------------------------------------------------------

Write-Host 'Row outcome resolution: raw dictionary shapes (real orchestrator contract)' -ForegroundColor Cyan

Test-Case 'Blueprint outcome resolves success from a raw [ordered] summary/identifiers' {
    $child = [pscustomobject]@{ summary = [ordered]@{ identifiers = [ordered]@{ blueprintAppId = 'app-raw-1' }; failedSteps = @() } }
    $outcome = Resolve-A365BulkOnboardingRowOutcome -ObjectType 'Blueprint' -ChildResult $child
    Assert-True $outcome.Success
    Assert-Equal 'app-raw-1' $outcome.ResolvedId
}

Test-Case 'AgentIdentity outcome resolves success from a raw Hashtable summary/identifiers' {
    $child = [pscustomobject]@{ summary = @{ identifiers = @{ agentIdentityId = 'id-raw-1' }; failedSteps = @() } }
    $outcome = Resolve-A365BulkOnboardingRowOutcome -ObjectType 'AgentIdentity' -ChildResult $child
    Assert-True $outcome.Success
    Assert-Equal 'id-raw-1' $outcome.ResolvedId
}

Test-Case 'AgentUser outcome resolves success from a raw [ordered] summary/identifiers' {
    $child = [pscustomobject]@{ summary = [ordered]@{ identifiers = [ordered]@{ agentUserId = 'user-raw-1' }; failedSteps = @() } }
    $outcome = Resolve-A365BulkOnboardingRowOutcome -ObjectType 'AgentUser' -ChildResult $child
    Assert-True $outcome.Success
    Assert-Equal 'user-raw-1' $outcome.ResolvedId
}

Test-Case 'AgentRegistration outcome resolves success from a raw [ordered] summary/identifiers' {
    $child = [pscustomobject]@{ summary = [ordered]@{ identifiers = [ordered]@{ registrationId = 'T_raw1' }; failedSteps = @() } }
    $outcome = Resolve-A365BulkOnboardingRowOutcome -ObjectType 'AgentRegistration' -ChildResult $child
    Assert-True $outcome.Success
    Assert-Equal 'T_raw1' $outcome.ResolvedId
}

Test-Case 'A raw-dictionary failedSteps entry (no PSCustomObject) surfaces its step and detail text' {
    $child = [pscustomobject]@{
        summary = [ordered]@{
            identifiers = [ordered]@{ agentUserId = $null }
            failedSteps = @([ordered]@{ step = 'AgentUser'; status = 'Failed'; detail = 'Insufficient privileges (raw dictionary)' })
        }
    }
    $outcome = Resolve-A365BulkOnboardingRowOutcome -ObjectType 'AgentUser' -ChildResult $child
    Assert-False $outcome.Success
    Assert-True ($outcome.Error -match 'AgentUser: Insufficient privileges \(raw dictionary\)')
}

Test-Case 'A fully realistic raw-dictionary result (matching the real orchestrator''s own shallow pscustomobject cast) resolves correctly' {
    # $result at the top level is a PSCustomObject ([pscustomobject]$result), but
    # $result.summary and $result.summary.identifiers are still [ordered]@{}, with every
    # identifier key always present - most $null - exactly as the real orchestrator builds it.
    $child = [pscustomobject]@{
        tenantId = 'tid'
        summary  = [ordered]@{
            outcome     = 'Succeeded'
            identifiers = [ordered]@{
                blueprintAppId  = $null
                agentIdentityId = $null
                agentUserId     = $null
                registrationId  = 'T_realistic1'
            }
            failedSteps = @()
        }
    }
    $outcome = Resolve-A365BulkOnboardingRowOutcome -ObjectType 'AgentRegistration' -ChildResult $child
    Assert-True $outcome.Success
    Assert-Equal 'T_realistic1' $outcome.ResolvedId
}

Get-A365TestResults
