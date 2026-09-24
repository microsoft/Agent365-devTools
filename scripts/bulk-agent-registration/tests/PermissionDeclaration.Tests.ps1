# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    Verifies that automation-app permissions are declared in the shape required for portal-based
    admin consent, without making live Microsoft Graph calls.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'TestHelpers.psm1') -Force

function Get-A365ExtractedFunctionSource {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string[]] $FunctionName
    )

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref] $tokens, [ref] $parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw "'$Path' has $($parseErrors.Count) parse error(s); cannot extract permission helpers."
    }

    $functions = $ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $FunctionName -contains $node.Name
        }, $true)

    foreach ($name in $FunctionName) {
        if (@($functions | Where-Object Name -eq $name).Count -eq 0) {
            throw "Function '$name' was not found in '$Path'."
        }
    }

    return ($functions | ForEach-Object { $_.Extent.Text }) -join "`n`n"
}

function New-A365Permission {
    param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][string] $Id)
    return [pscustomobject]@{ Name = $Name; Id = $Id }
}

$script:AutomationAppPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'New-A365AutomationApp.ps1')).ProviderPath
$functionSource = Get-A365ExtractedFunctionSource -Path $script:AutomationAppPath `
    -FunctionName @('Test-HasProperty', 'Merge-GraphRequiredResourceAccess', 'Test-PermissionDeclarationApproval')
. ([scriptblock]::Create($functionSource))
$script:NativeDeclarationApproval = (Get-Command Test-PermissionDeclarationApproval).ScriptBlock

# Keep the real entry point and replace only the external I/O and approval boundaries in memory.
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($script:AutomationAppPath, [ref]$tokens, [ref]$parseErrors)
$testSource = $ast.Extent.Text
$stubbedFunctions = @('Connect-GraphSession', 'Invoke-Graph', 'Test-PermissionDeclarationApproval')
foreach ($definition in @($ast.EndBlock.Statements | Where-Object {
    $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -in $stubbedFunctions
} | Sort-Object { $_.Extent.StartOffset } -Descending)) {
    $testSource = $testSource.Remove($definition.Extent.StartOffset, $definition.Extent.EndOffset - $definition.Extent.StartOffset)
}
$script:AutomationAppUnderTest = [scriptblock]::Create($testSource)

$graphAppId = '00000003-0000-0000-c000-000000000000'

function Assert-A365PermissionNames {
    param([object[]] $Expected, $Actual)
    Assert-NotNull $Actual 'Permission name arrays must not become null.'
    Assert-Count $Actual $Expected.Count
    Assert-Equal ($Expected -join '|') ($Actual -join '|') 'Permission names and order must match.'
}

function Assert-A365Access {
    param([object[]] $Expected, [object[]] $Actual)
    Assert-Count $Actual $Expected.Count 'Resource entries must be preserved.'
    for ($i = 0; $i -lt $Expected.Count; $i++) {
        Assert-Equal $Expected[$i].resourceAppId $Actual[$i].resourceAppId
        Assert-Count $Actual[$i].resourceAccess $Expected[$i].resourceAccess.Count
        for ($j = 0; $j -lt $Expected[$i].resourceAccess.Count; $j++) {
            Assert-True ($Expected[$i].resourceAccess[$j].id -ceq $Actual[$i].resourceAccess[$j].id) 'Keep the first id spelling and entry order.'
            Assert-True ($Expected[$i].resourceAccess[$j].type -ceq $Actual[$i].resourceAccess[$j].type) 'Keep the first type spelling and entry order.'
        }
    }
}

function New-A365DeclarationFixture {
    $roleNames = @('CustomSecAttributeAssignment.ReadWrite.All', 'CustomSecAttributeDefinition.Read.All')
    return [pscustomobject]@{
        RoleNames = $roleNames
        ScopeNames = $roleNames
        Graph = [pscustomobject]@{
            id = 'graph-sp'
            appRoles = @(
                @{ id = 'role-a'; value = $roleNames[0]; allowedMemberTypes = @('Application') }
                @{ id = 'role-b'; value = $roleNames[1]; allowedMemberTypes = @('Application') }
            )
            oauth2PermissionScopes = @(
                @{ id = 'scope-a'; value = $roleNames[0] }
                @{ id = 'scope-b'; value = $roleNames[1] }
            )
        }
        ExistingAccess = @(
            @{ resourceAppId = $graphAppId; resourceAccess = @(
                @{ id = 'ROLE-A'; type = 'role' }
                @{ id = 'scope-a'; type = 'Scope' }
                @{ id = 'unselected'; type = 'Role' }
            ) }
            @{ resourceAppId = 'other-api'; resourceAccess = @(
                @{ id = 'role-b'; type = 'Role' }
                @{ id = 'scope-b'; type = 'Scope' }
                @{ id = 'scope-b'; type = 'Scope' }
            ) }
        )
        Calls = [System.Collections.Generic.List[object]]::new()
        Approvals = [System.Collections.Generic.List[object]]::new()
        Console = [System.Collections.Generic.List[string]]::new()
        Reports = [System.Collections.Generic.List[string]]::new()
        ReportAttempts = 0
        HeldIds = [System.Collections.Generic.List[string]]::new()
        Decline = $false
        PatchFails = $false
        MissingApplication = $false
        MissingPrincipal = $false
    }
}

function Invoke-A365DeclarationScenario {
    param(
        [Parameter(Mandatory)] $State,
        [switch] $DryRun,
        [bool] $SkipConsent = $true,
        [switch] $WriteReport
    )

    function Connect-GraphSession {
        param($TenantId, $DelegatedScope)
        return [pscustomobject]@{ TenantId = $TenantId }
    }

    function Invoke-Graph {
        param($Method, $Uri, $Body, [switch] $TolerateNotFound, [switch] $TolerateConflict)
        $State.Calls.Add([pscustomobject]@{ Method = $Method; Uri = $Uri; Body = $Body })
        if ($Method -eq 'GET') {
            switch -Exact ($Uri) {
                "/servicePrincipals(appId='$graphAppId')?`$select=id,appRoles,oauth2PermissionScopes" { return $State.Graph }
                "/applications(appId='automation-app')?`$select=id,appId,displayName" {
                    return [pscustomobject]@{ id = 'app-object'; appId = 'automation-app'; displayName = 'Test automation' }
                }
                "/applications/app-object?`$select=requiredResourceAccess" {
                    return [pscustomobject]@{ requiredResourceAccess = $State.ExistingAccess }
                }
                "/servicePrincipals(appId='automation-app')?`$select=id,appId,displayName" {
                    if ($State.MissingPrincipal) { return $null }
                    return [pscustomobject]@{ id = 'app-sp'; appId = 'automation-app'; displayName = 'Test automation' }
                }
                "/servicePrincipals/app-sp/appRoleAssignments?`$select=appRoleId,resourceId&`$top=999" {
                    return @{ value = @($State.HeldIds | ForEach-Object { @{ appRoleId = $_; resourceId = 'graph-sp' } }) }
                }
                "/servicePrincipals/app-sp/appRoleAssignments?`$select=appRoleId&`$top=999" {
                    return @{ value = @($State.HeldIds | ForEach-Object { @{ appRoleId = $_ } }) }
                }
            }
            if ($State.MissingApplication -and $Uri.StartsWith('/applications?$filter=')) { return @{ value = @() } }
        }
        if ($Method -eq 'PATCH' -and $Uri -eq '/applications/app-object') {
            if ($State.PatchFails) { throw 'Mock declaration PATCH failed.' }
            return $null
        }
        if ($Method -eq 'POST' -and $Uri -eq '/servicePrincipals/graph-sp/appRoleAssignedTo') {
            $State.HeldIds.Add($Body.appRoleId)
            return @{ id = 'mock-assignment' }
        }
        throw "Unexpected Graph call: $Method $Uri"
    }

    function Test-PermissionDeclarationApproval {
        param($Command, $Target, $Action)
        $State.Approvals.Add(@{ Target = $Target; Action = $Action })
        if ($State.Decline) { return $false }
        return & $script:NativeDeclarationApproval -Command $Command -Target $Target -Action $Action
    }

    function Write-Host {
        param($Object, $ForegroundColor)
        $State.Console.Add([string]$Object)
    }

    function Write-Warning {
        param($Message)
        $State.Console.Add([string]$Message)
    }

    function Set-Content {
        [CmdletBinding(SupportsShouldProcess)]
        param([Parameter(ValueFromPipeline)] $Value, $LiteralPath, $Encoding)
        process {
            $State.ReportAttempts++
            if ($PSCmdlet.ShouldProcess($LiteralPath, 'Write test summary')) {
                $State.Reports.Add([string]$Value)
            }
        }
    }

    $arguments = @{
        TenantId = 'test-tenant'
        AppId = 'automation-app'
        DisplayName = 'Test automation'
        Scenario = 'AgentIdentity'
        SkipAppRole = @(
            'AgentIdentity.Create.All', 'AgentIdentity.Read.All', 'AgentIdentity.ReadWrite.All',
            'AgentIdentityBlueprint.Read.All', 'Application.Read.All', 'User.Read.All',
            'Group.Read.All', 'Application.ReadWrite.All', 'Directory.Read.All'
        )
        SkipGrant = $SkipConsent
        Confirm = $false
        WhatIf = [bool]$DryRun
    }
    if ($State.MissingApplication) { $arguments.Remove('AppId') }
    if ($WriteReport) { $arguments.OutputPath = 'mock-permission-summary.json' }

    $summary = & $script:AutomationAppUnderTest @arguments
    return [pscustomobject]@{
        Summary = $summary
        Json = ($summary | ConvertTo-Json -Depth 25)
        State = $State
        Console = ($State.Console -join "`n")
    }
}

function Assert-A365SummarySchema {
    param([Parameter(Mandatory)] $Run)
    $json = $Run.Json | ConvertFrom-Json -AsHashtable
    foreach ($name in @(
        'appRolesDeclared', 'appRolesAddedToRequest', 'appRolesPlannedToAdd', 'appRolesGranted',
        'appRolesAlreadyHeld', 'appRolesVerified', 'appRolesUnresolved', 'delegatedScopesDeclared',
        'delegatedScopesRequested', 'delegatedScopesAddedToRequest', 'delegatedScopesPlannedToAdd', 'consentFailures'
    )) {
        Assert-True ($json[$name] -is [array]) "$name must serialize as a JSON array, including empty/singleton values."
    }
    Assert-True ($json.grantSkipped -is [bool]) 'grantSkipped must remain a JSON boolean.'
    Assert-True ($json.permissionDeclarationStatus -is [string]) 'The declaration status must serialize as a string.'
    Assert-A365PermissionNames $Run.State.ScopeNames $json.delegatedScopesRequested
    Assert-Equal 'https://login.microsoftonline.com/test-tenant/adminconsent?client_id=automation-app' $json.adminConsentUrl
    Assert-Equal 'https://entra.microsoft.com/#view/Microsoft_AAD_IAM/ManagedAppMenuBlade/~/Permissions/objectId/app-sp/appId/automation-app' $json.portalPermissionsUrl
    return $json
}

function Assert-A365DeclarationPatch {
    param($State, [object[]] $Expected)
    $writes = @($State.Calls | Where-Object Method -ne 'GET')
    Assert-Count $writes 1 'Declaration reconciliation must make exactly one write when consent is skipped.'
    Assert-Equal 'PATCH' $writes[0].Method
    Assert-Equal '/applications/app-object' $writes[0].Uri
    $body = $writes[0].Body | ConvertTo-Json -Depth 25 | ConvertFrom-Json -AsHashtable
    Assert-Count $body.Keys 1
    Assert-True ($body.Keys -ccontains 'requiredResourceAccess') 'PATCH must use the exact Graph property name.'
    Assert-A365Access $Expected $body.requiredResourceAccess
    foreach ($resource in $body.requiredResourceAccess) {
        Assert-Count $resource.Keys 2
        Assert-True ($resource.Keys -ccontains 'resourceAppId')
        Assert-True ($resource.Keys -ccontains 'resourceAccess')
        foreach ($access in $resource.resourceAccess) {
            Assert-Count $access.Keys 2
            Assert-True ($access.Keys -ccontains 'id')
            Assert-True ($access.Keys -ccontains 'type')
        }
    }
}

function Assert-A365UnappliedDeclarations {
    param($Run, [string] $Status)
    $json = Assert-A365SummarySchema $Run
    Assert-Equal $Status $json.permissionDeclarationStatus
    Assert-A365PermissionNames @($Run.State.RoleNames[0]) $json.appRolesDeclared
    Assert-A365PermissionNames @($Run.State.ScopeNames[0]) $json.delegatedScopesDeclared
    Assert-A365PermissionNames @($Run.State.RoleNames[1]) $json.appRolesPlannedToAdd
    Assert-A365PermissionNames @($Run.State.ScopeNames[1]) $json.delegatedScopesPlannedToAdd
    Assert-A365PermissionNames @() $json.appRolesAddedToRequest
    Assert-A365PermissionNames @() $json.delegatedScopesAddedToRequest
    Assert-Count (@($Run.State.Calls | Where-Object Method -ne 'GET')) 0 'An unapplied reconciliation must not write to Graph.'
    Assert-Count $Run.State.Approvals 1 'A proposed change must have only one declaration approval boundary.'
    Assert-True ($Run.Console -match "update not applied \($Status\)")
    Assert-True ($Run.Console -match 'Some selected permissions are not declared')
    Assert-True ($Run.Console -match 'Re-run and approve the declaration update')
    Assert-True ($Run.Console -match 'Application roles not declared: CustomSecAttributeDefinition.Read.All')
    Assert-True ($Run.Console -match 'Application roles declared; consent not confirmed by this run: CustomSecAttributeAssignment.ReadWrite.All')
    Assert-False ($Run.Console -match 'Declared application permission\(s\):|Requested delegated scope\(s\):|Removed \d+ duplicate')
    Assert-False ($Run.Console -match 'selected permissions are declared|Permissions were declared|without adding permissions manually')
    Assert-False ($Run.Console -match 'Application roles granted:|all an UNATTENDED') 'Unapproved changes must not be described as granted.'
}

Test-Case 'Test-HasProperty supports Graph objects and dictionary-shaped merge output' {
    Assert-False (Test-HasProperty $null 'value') 'Null must not report any property.'
    Assert-True (Test-HasProperty ([pscustomobject]@{ value = 1 }) 'value') 'Graph response objects must expose their properties.'
    Assert-False (Test-HasProperty ([pscustomobject]@{ value = 1 }) 'missing') 'Missing Graph object properties must return false.'
    Assert-True (Test-HasProperty @{ value = 1 } 'value') 'Dictionary output from the merge helper must expose its keys.'
    Assert-False (Test-HasProperty @{ value = 1 } 'missing') 'Missing dictionary keys must return false.'
}

Test-Case 'Permission declaration adds application roles and delegated scopes with their correct types' {
    $result = Merge-GraphRequiredResourceAccess -ResourceAppId $graphAppId `
        -ApplicationRoles @(New-A365Permission -Name 'Application.Read.All' -Id 'role-1') `
        -DelegatedScopes @(New-A365Permission -Name 'User.Read' -Id 'scope-1')

    Assert-True $result.Changed 'A new role and scope must require a requiredResourceAccess update.'
    Assert-Count $result.RolesAdded 1 'The application role must be reported as newly declared.'
    Assert-Count $result.ScopesAdded 1 'The delegated scope must be reported as newly declared.'
    Assert-Count $result.RequiredResourceAccess 1 'Microsoft Graph must produce one resource entry.'

    $access = @($result.RequiredResourceAccess[0].resourceAccess)
    $roleEntry = $access | Where-Object { $_.id -eq 'role-1' -and $_.type -eq 'Role' }
    $scopeEntry = $access | Where-Object { $_.id -eq 'scope-1' -and $_.type -eq 'Scope' }
    Assert-True (@($roleEntry).Count -eq 1) `
        "Application permissions must be declared with type 'Role' so the portal can consent them."
    Assert-True (@($scopeEntry).Count -eq 1) `
        "Delegated permissions must be declared with type 'Scope' so the portal can consent them."
    Assert-True ($roleEntry.Keys -ccontains 'id') "The Graph PATCH contract requires the lowercase JSON key 'id'."
    Assert-True ($roleEntry.Keys -ccontains 'type') "The Graph PATCH contract requires the lowercase JSON key 'type'."
}

Test-Case 'Permission declaration preserves existing Graph and non-Graph permissions' {
    $existing = @(
        [pscustomobject]@{
            resourceAppId = $graphAppId
            resourceAccess = @([pscustomobject]@{ id = 'existing-role'; type = 'Role' })
        },
        [pscustomobject]@{
            resourceAppId = '11111111-2222-3333-4444-555555555555'
            resourceAccess = @([pscustomobject]@{ id = 'external-scope'; type = 'Scope' })
        }
    )

    $result = Merge-GraphRequiredResourceAccess -ExistingAccess $existing -ResourceAppId $graphAppId `
        -ApplicationRoles @(
            (New-A365Permission -Name 'Existing' -Id 'existing-role'),
            (New-A365Permission -Name 'New' -Id 'new-role')
        )

    $graph = $result.RequiredResourceAccess | Where-Object resourceAppId -eq $graphAppId
    $external = $result.RequiredResourceAccess | Where-Object resourceAppId -eq '11111111-2222-3333-4444-555555555555'

    Assert-Count $graph.resourceAccess 2 'Existing Graph permissions must remain while missing roles are added.'
    Assert-Count $external.resourceAccess 1 'Permissions for other resource APIs must be preserved.'
    Assert-Equal 'external-scope' $external.resourceAccess[0].id 'The existing non-Graph permission must remain unchanged.'
    Assert-Count $result.RolesAdded 1 'Only the missing application role should be reported as added.'
}

Test-Case 'Permission declaration is idempotent' {
    $first = Merge-GraphRequiredResourceAccess -ResourceAppId $graphAppId `
        -ApplicationRoles @(New-A365Permission -Name 'Application.Read.All' -Id 'role-1') `
        -DelegatedScopes @(New-A365Permission -Name 'User.Read' -Id 'scope-1')

    $second = Merge-GraphRequiredResourceAccess -ExistingAccess $first.RequiredResourceAccess `
        -ResourceAppId $graphAppId `
        -ApplicationRoles @(New-A365Permission -Name 'Application.Read.All' -Id 'role-1') `
        -DelegatedScopes @(New-A365Permission -Name 'User.Read' -Id 'scope-1')

    Assert-False $second.Changed 'A rerun must not PATCH requiredResourceAccess when every permission is already declared.'
    Assert-Count $second.RolesAdded 0 'A rerun must not duplicate application roles.'
    Assert-Count $second.ScopesAdded 0 'A rerun must not duplicate delegated scopes.'
    Assert-Count $second.RequiredResourceAccess[0].resourceAccess 2 'A rerun must preserve exactly one copy of each permission.'
}

Test-Case 'Permission declaration distinguishes Role and Scope entries even when their ids match' {
    $existing = @([pscustomobject]@{
        resourceAppId = $graphAppId
        resourceAccess = @([pscustomobject]@{ id = 'shared-id'; type = 'Role' })
    })

    $result = Merge-GraphRequiredResourceAccess -ExistingAccess $existing -ResourceAppId $graphAppId `
        -DelegatedScopes @(New-A365Permission -Name 'Shared.Scope' -Id 'shared-id')

    $access = @($result.RequiredResourceAccess[0].resourceAccess)
    Assert-True $result.Changed 'Role and Scope are distinct requiredResourceAccess entries.'
    Assert-Count (@($access | Where-Object { $_.id -eq 'shared-id' -and $_.type -eq 'Role' })) 1 `
        'The existing Role entry must remain present exactly once.'
    Assert-Count (@($access | Where-Object { $_.id -eq 'shared-id' -and $_.type -eq 'Scope' })) 1 `
        'The Scope entry must be added even when a Role has the same id.'
}

Test-Case 'Existing Graph duplicates alone require cleanup, preserving first spelling and Role versus Scope' {
    $existing = @(
        @{ resourceAppId = $graphAppId; resourceAccess = @(
            @{ id = 'Shared-ID'; type = 'Role' }
            @{ id = 'shared-id'; type = 'role' }
            @{ id = 'SHARED-ID'; type = 'Scope' }
            @{ id = 'shared-id'; type = 'SCOPE' }
        ) }
        @{ resourceAppId = $graphAppId.ToUpperInvariant(); resourceAccess = @(
            @{ id = 'SHARED-ID'; type = 'ROLE' }
            @{ id = 'another'; type = 'Scope' }
        ) }
    )
    $before = ConvertTo-Json -InputObject $existing -Depth 25
    $result = Merge-GraphRequiredResourceAccess -ExistingAccess $existing -ResourceAppId $graphAppId
    Assert-True $result.Changed 'Duplicate removal alone must trigger reconciliation.'
    Assert-Equal 3 $result.DuplicatesRemoved
    Assert-Count $result.RolesAdded 0
    Assert-Count $result.ScopesAdded 0
    Assert-A365Access @(@{ resourceAppId = $graphAppId; resourceAccess = @(
        @{ id = 'Shared-ID'; type = 'Role' }
        @{ id = 'SHARED-ID'; type = 'Scope' }
        @{ id = 'another'; type = 'Scope' }
    ) }) $result.RequiredResourceAccess
    Assert-Equal $before (ConvertTo-Json -InputObject $existing -Depth 25) 'Cleanup must not mutate its input.'

    $second = Merge-GraphRequiredResourceAccess -ExistingAccess $result.RequiredResourceAccess -ResourceAppId $graphAppId
    Assert-False $second.Changed
    Assert-Equal 0 $second.DuplicatesRemoved
    Assert-Count $second.RolesAdded 0
    Assert-Count $second.ScopesAdded 0
    Assert-A365Access $result.RequiredResourceAccess $second.RequiredResourceAccess
}

Test-Case 'Existing duplicate cleanup and new requests share case-insensitive identity semantics' {
    $existing = @(@{ resourceAppId = $graphAppId; resourceAccess = @(
        @{ id = 'same'; type = 'Role' }
        @{ id = 'SAME'; type = 'ROLE' }
        @{ id = 'scope'; type = 'Scope' }
        @{ id = 'SCOPE'; type = 'scope' }
    ) })
    $roles = @(
        (New-A365Permission 'Existing' 'SAME')
        (New-A365Permission 'New' 'new')
        (New-A365Permission 'NewAgain' 'NEW')
    )
    $scopes = @(
        (New-A365Permission 'ExistingScope' 'SCOPE')
        (New-A365Permission 'SameIdDifferentType' 'same')
        (New-A365Permission 'ScopeAgain' 'SAME')
    )
    $before = ConvertTo-Json -InputObject @($existing, $roles, $scopes) -Depth 25
    $result = Merge-GraphRequiredResourceAccess -ExistingAccess $existing -ResourceAppId $graphAppId -ApplicationRoles $roles -DelegatedScopes $scopes
    Assert-True $result.Changed
    Assert-Equal 2 $result.DuplicatesRemoved 'Only duplicate existing declarations count as cleanup.'
    Assert-A365PermissionNames @('New') @($result.RolesAdded.Name)
    Assert-A365PermissionNames @('SameIdDifferentType') @($result.ScopesAdded.Name)
    Assert-A365Access @(@{ resourceAppId = $graphAppId; resourceAccess = @(
        @{ id = 'same'; type = 'Role' }
        @{ id = 'scope'; type = 'Scope' }
        @{ id = 'new'; type = 'Role' }
        @{ id = 'same'; type = 'Scope' }
    ) }) $result.RequiredResourceAccess
    Assert-Equal $before (ConvertTo-Json -InputObject @($existing, $roles, $scopes) -Depth 25)
}

Test-Case 'Cleanup preserves unique Graph permissions and non-Graph duplicates without sharing mutable declarations' {
    $existing = @(
        [pscustomobject]@{ resourceAppId = $graphAppId; resourceAccess = @(
            [pscustomobject]@{ id = 'keep'; type = 'Role' }
            [pscustomobject]@{ id = 'KEEP'; type = 'Role' }
            [pscustomobject]@{ id = 'unselected'; type = 'Scope' }
        ) }
        @{ resourceAppId = 'other-api'; resourceAccess = @(
            @{ id = 'duplicate'; type = 'Role' }
            @{ id = 'duplicate'; type = 'Role' }
        ) }
        @{ resourceAppId = 'other-api'; resourceAccess = @(@{ id = 'duplicate'; type = 'Role' }) }
    )
    $before = ConvertTo-Json -InputObject $existing -Depth 25
    $result = Merge-GraphRequiredResourceAccess -ExistingAccess $existing -ResourceAppId $graphAppId
    Assert-Equal 1 $result.DuplicatesRemoved 'Non-Graph duplicates must not count as cleanup.'
    Assert-A365Access @(
        $existing[1]
        $existing[2]
        @{ resourceAppId = $graphAppId; resourceAccess = @($existing[0].resourceAccess[0], $existing[0].resourceAccess[2]) }
    ) $result.RequiredResourceAccess
    $result.RequiredResourceAccess[0].resourceAccess[0].id = 'changed-external'
    $result.RequiredResourceAccess[1].resourceAppId = 'changed-resource'
    $result.RequiredResourceAccess[2].resourceAccess[0].id = 'changed-graph'
    Assert-Equal $before (ConvertTo-Json -InputObject $existing -Depth 25) 'Returned declarations must not alias input declarations.'
}

Test-Case 'Accepted declaration update PATCHes the exact merged payload and serializes actual plus planned state' {
    $state = New-A365DeclarationFixture
    $state.ExistingAccess[0].resourceAccess += @{ id = 'role-a'; type = 'ROLE' }
    $before = ConvertTo-Json -InputObject $state.ExistingAccess -Depth 25
    $run = Invoke-A365DeclarationScenario -State $state -WriteReport
    $json = Assert-A365SummarySchema $run
    Assert-A365DeclarationPatch $state @(
        $state.ExistingAccess[1]
        @{ resourceAppId = $graphAppId; resourceAccess = @(
            @{ id = 'ROLE-A'; type = 'role' }
            @{ id = 'scope-a'; type = 'Scope' }
            @{ id = 'unselected'; type = 'Role' }
            @{ id = 'role-b'; type = 'Role' }
            @{ id = 'scope-b'; type = 'Scope' }
        ) }
    )
    Assert-Count $state.Approvals 1
    Assert-Equal 'Test automation' $state.Approvals[0].Target
    Assert-Equal 'PATCH requiredResourceAccess (+1 application permission(s), +1 delegated scope(s), remove 1 duplicate declaration(s))' $state.Approvals[0].Action
    Assert-Equal 'Applied' $json.permissionDeclarationStatus
    Assert-A365PermissionNames $state.RoleNames $json.appRolesDeclared
    Assert-A365PermissionNames $state.ScopeNames $json.delegatedScopesDeclared
    Assert-A365PermissionNames @($state.RoleNames[1]) $json.appRolesAddedToRequest
    Assert-A365PermissionNames @($state.ScopeNames[1]) $json.delegatedScopesAddedToRequest
    Assert-A365PermissionNames @($state.RoleNames[1]) $json.appRolesPlannedToAdd
    Assert-A365PermissionNames @($state.ScopeNames[1]) $json.delegatedScopesPlannedToAdd
    Assert-True $json.grantSkipped
    foreach ($field in 'appRolesGranted', 'appRolesAlreadyHeld', 'appRolesVerified', 'consentFailures') {
        Assert-Count $json[$field] 0 'Declaring permissions must not be reported as granting consent.'
    }
    Assert-Count $state.Reports 1
    Assert-Equal $run.Json $state.Reports[0] 'The report must serialize the actual returned summary.'
    Assert-Equal $before (ConvertTo-Json -InputObject $state.ExistingAccess -Depth 25)
    Assert-True ($run.Console -match 'Removed 1 duplicate Graph')
    Assert-True ($run.Console -match 'Currently declared: 2/2 selected application permission')
    Assert-True ($run.Console -match 'without adding permissions manually')
    Assert-False ($run.Console -match 'Application roles granted:|all an UNATTENDED')
}

Test-Case 'Accepted duplicate-only cleanup is Applied and a cleaned second run performs no PATCH or approval' {
    $state = New-A365DeclarationFixture
    $state.ExistingAccess[0].resourceAccess += @(
        @{ id = 'role-b'; type = 'Role' }
        @{ id = 'scope-b'; type = 'Scope' }
        @{ id = 'role-a'; type = 'Role' }
        @{ id = 'SCOPE-B'; type = 'scope' }
    )
    $run = Invoke-A365DeclarationScenario -State $state
    $json = Assert-A365SummarySchema $run
    $expected = @(
        $state.ExistingAccess[1]
        @{ resourceAppId = $graphAppId; resourceAccess = @($state.ExistingAccess[0].resourceAccess[0..4]) }
    )
    Assert-A365DeclarationPatch $state $expected
    Assert-Count $state.Approvals 1
    Assert-Equal 'PATCH requiredResourceAccess (+0 application permission(s), +0 delegated scope(s), remove 2 duplicate declaration(s))' $state.Approvals[0].Action
    Assert-Equal 'Applied' $json.permissionDeclarationStatus
    Assert-A365PermissionNames $state.RoleNames $json.appRolesDeclared
    Assert-A365PermissionNames $state.ScopeNames $json.delegatedScopesDeclared
    foreach ($field in 'appRolesAddedToRequest', 'delegatedScopesAddedToRequest', 'appRolesPlannedToAdd', 'delegatedScopesPlannedToAdd') {
        Assert-Count $json[$field] 0
    }
    Assert-True ($run.Console -match 'Removed 2 duplicate Graph')
    Assert-False ($run.Console -match 'Declared application permission\(s\):|Requested delegated scope\(s\):')

    $next = New-A365DeclarationFixture
    $next.ExistingAccess = ($state.Calls | Where-Object Method -eq 'PATCH').Body.requiredResourceAccess
    $rerun = Invoke-A365DeclarationScenario -State $next
    $rerunJson = Assert-A365SummarySchema $rerun
    Assert-Equal 'Unchanged' $rerunJson.permissionDeclarationStatus
    Assert-Count (@($next.Calls | Where-Object Method -ne 'GET')) 0
    Assert-Count $next.Approvals 0
    Assert-A365PermissionNames $next.RoleNames $rerunJson.appRolesDeclared
    Assert-A365PermissionNames $next.ScopeNames $rerunJson.delegatedScopesDeclared
}

Test-Case 'Native WhatIf keeps existing declarations and planned additions without PATCH or report-file write' {
    $state = New-A365DeclarationFixture
    $run = Invoke-A365DeclarationScenario -State $state -DryRun -WriteReport
    Assert-A365UnappliedDeclarations $run 'WhatIf'
    Assert-Equal 1 $state.ReportAttempts 'The normal report path must still respect native WhatIf.'
    Assert-Count $state.Reports 0 'WhatIf must not be bypassed to write a report.'
    Assert-True ($run.Console -match 'Summary not written \(-WhatIf\)')
    Assert-False ($run.Console -match 'Summary written to')
    Assert-True ($run.Console -match 'Currently declared: 1/2 selected application permission')
}

Test-Case 'Declined confirmation serializes existing declarations and plans without claiming approval or consent' {
    $state = New-A365DeclarationFixture
    $state.Decline = $true
    $run = Invoke-A365DeclarationScenario -State $state -WriteReport
    Assert-A365UnappliedDeclarations $run 'Declined'
    Assert-Count $state.Reports 1
    Assert-Equal $run.Json $state.Reports[0]
}

Test-Case 'Skipped duplicate-only cleanup retains actual declarations but still reports the unapplied reconciliation' {
    foreach ($status in 'WhatIf', 'Declined') {
        $state = New-A365DeclarationFixture
        $state.ExistingAccess[0].resourceAccess += @(
            @{ id = 'role-b'; type = 'Role' }
            @{ id = 'scope-b'; type = 'Scope' }
            @{ id = 'ROLE-B'; type = 'role' }
        )
        $state.Decline = $status -eq 'Declined'
        $run = Invoke-A365DeclarationScenario -State $state -DryRun:($status -eq 'WhatIf')
        $json = Assert-A365SummarySchema $run
        Assert-Equal $status $json.permissionDeclarationStatus
        Assert-A365PermissionNames $state.RoleNames $json.appRolesDeclared
        Assert-A365PermissionNames $state.ScopeNames $json.delegatedScopesDeclared
        foreach ($field in 'appRolesAddedToRequest', 'delegatedScopesAddedToRequest', 'appRolesPlannedToAdd', 'delegatedScopesPlannedToAdd') {
            Assert-Count $json[$field] 0
        }
        Assert-Count (@($state.Calls | Where-Object Method -ne 'GET')) 0
        Assert-Count $state.Approvals 1
        Assert-True ($state.Approvals[0].Action -match 'remove 1 duplicate declaration')
        Assert-True ($run.Console -match "update not applied \($status\)")
        Assert-True ($run.Console -match 'without adding permissions manually') 'Existing complete declarations are still ready for portal consent.'
        Assert-False ($run.Console -match 'Removed 1 duplicate|Some selected permissions are not declared')
    }
}

Test-Case 'Wrong-type Graph entries and matching non-Graph ids cannot inflate actual declared permissions' {
    $state = New-A365DeclarationFixture
    $state.ExistingAccess[0].resourceAccess = @(
        @{ id = 'role-a'; type = 'Scope' }
        @{ id = 'role-b'; type = 'Scope' }
        @{ id = 'scope-a'; type = 'Role' }
        @{ id = 'scope-b'; type = 'Role' }
    )
    $run = Invoke-A365DeclarationScenario -State $state -DryRun
    $json = Assert-A365SummarySchema $run
    Assert-Equal 'WhatIf' $json.permissionDeclarationStatus
    Assert-A365PermissionNames @() $json.appRolesDeclared
    Assert-A365PermissionNames @() $json.delegatedScopesDeclared
    Assert-A365PermissionNames @() $json.appRolesAddedToRequest
    Assert-A365PermissionNames @() $json.delegatedScopesAddedToRequest
    Assert-A365PermissionNames $state.RoleNames $json.appRolesPlannedToAdd
    Assert-A365PermissionNames $state.ScopeNames $json.delegatedScopesPlannedToAdd
    Assert-Count (@($state.Calls | Where-Object Method -ne 'GET')) 0
    Assert-True ($run.Console -match 'Currently declared: 0/2 selected application permission')
    Assert-False ($run.Console -match 'Application roles declared;|Application roles granted:|without adding permissions manually')
}

Test-Case 'Unchanged declarations stay actual under WhatIf, with no approval or planned additions' {
    $state = New-A365DeclarationFixture
    $state.ExistingAccess[0].resourceAccess += @(
        @{ id = 'role-b'; type = 'Role' }
        @{ id = 'scope-b'; type = 'Scope' }
    )
    $run = Invoke-A365DeclarationScenario -State $state -DryRun
    $json = Assert-A365SummarySchema $run
    Assert-Equal 'Unchanged' $json.permissionDeclarationStatus
    Assert-Count $state.Approvals 0
    Assert-Count (@($state.Calls | Where-Object Method -ne 'GET')) 0
    Assert-A365PermissionNames $state.RoleNames $json.appRolesDeclared
    Assert-A365PermissionNames $state.ScopeNames $json.delegatedScopesDeclared
    foreach ($field in 'appRolesAddedToRequest', 'delegatedScopesAddedToRequest', 'appRolesPlannedToAdd', 'delegatedScopesPlannedToAdd') {
        Assert-Count $json[$field] 0
    }
    Assert-True ($run.Console -match 'already declared on the application')
    Assert-False ($run.Console -match 'update not applied|Some selected permissions are not declared|Application roles granted:')
}

Test-Case 'PATCH failure propagates with no summary, report, or declaration success messages' {
    $state = New-A365DeclarationFixture
    $state.PatchFails = $true
    $outputs = [System.Collections.Generic.List[object]]::new()
    Assert-Throws {
        Invoke-A365DeclarationScenario -State $state -WriteReport | ForEach-Object { $outputs.Add($_) }
    } 'Mock declaration PATCH failed'
    Assert-Count (@($state.Calls | Where-Object Method -eq 'PATCH')) 1
    Assert-Count $state.Approvals 1
    Assert-Count $outputs 0 'A failed PATCH must not return a success-shaped result.'
    Assert-Count $state.Reports 0
    Assert-Equal 0 $state.ReportAttempts
    Assert-False (($state.Console -join "`n") -match 'Declared application permission\(s\):|Requested delegated scope\(s\):|Removed \d+ duplicate|Permission declarations : Applied|Done\.')
}

Test-Case 'Native WhatIf without SkipGrant never labels custom security attribute roles as granted' {
    $state = New-A365DeclarationFixture
    $run = Invoke-A365DeclarationScenario -State $state -DryRun -SkipConsent:$false
    Assert-A365UnappliedDeclarations $run 'WhatIf'
    $json = $run.Json | ConvertFrom-Json -AsHashtable
    Assert-False $json.grantSkipped 'grantSkipped continues to describe the switch, not WhatIf.'
    Assert-Count $json.appRolesGranted 0
    Assert-Count $json.appRolesAlreadyHeld 0
    Assert-Count $json.appRolesVerified 0
}

Test-Case 'Successful grants retain their existing report semantics and custom security attribute guidance' {
    $state = New-A365DeclarationFixture
    $state.HeldIds.Add('role-a')
    $run = Invoke-A365DeclarationScenario -State $state -SkipConsent:$false
    $json = Assert-A365SummarySchema $run
    Assert-False $json.grantSkipped
    Assert-A365PermissionNames @($state.RoleNames[1]) $json.appRolesGranted
    Assert-A365PermissionNames @($state.RoleNames[0]) $json.appRolesAlreadyHeld
    Assert-A365PermissionNames $state.RoleNames $json.appRolesVerified
    Assert-Count $json.consentFailures 0
    Assert-True ($run.Console -match 'Application roles granted:')
    Assert-True ($run.Console -match 'all an UNATTENDED')
    Assert-False ($run.Console -match 'Application roles not declared:|consent not confirmed by this run:')
}

Test-Case 'Declining declarations does not change independent grant semantics or hide successful grants' {
    $state = New-A365DeclarationFixture
    $state.Decline = $true
    $run = Invoke-A365DeclarationScenario -State $state -SkipConsent:$false
    $json = Assert-A365SummarySchema $run
    Assert-Equal 'Declined' $json.permissionDeclarationStatus
    Assert-Count (@($state.Calls | Where-Object Method -eq 'PATCH')) 0
    Assert-Count (@($state.Calls | Where-Object Method -eq 'POST')) 2
    Assert-A365PermissionNames @($state.RoleNames[0]) $json.appRolesDeclared
    Assert-A365PermissionNames @($state.RoleNames[1]) $json.appRolesPlannedToAdd
    Assert-A365PermissionNames @() $json.appRolesAddedToRequest
    Assert-A365PermissionNames $state.RoleNames $json.appRolesGranted
    Assert-A365PermissionNames $state.RoleNames $json.appRolesVerified
    Assert-True ($run.Console -match 'Application roles granted:')
    Assert-True ($run.Console -match 'Some selected permissions are not declared')
    Assert-False ($run.Console -match 'without adding permissions manually')
}

Test-Case 'Native WhatIf preserves new application and service principal early returns without fabricating summaries' {
    foreach ($missing in 'MissingApplication', 'MissingPrincipal') {
        $state = New-A365DeclarationFixture
        $state.$missing = $true
        $run = Invoke-A365DeclarationScenario -State $state -DryRun -WriteReport
        Assert-Null $run.Summary 'An uncreated app or principal must not produce a summary.'
        Assert-Count (@($state.Calls | Where-Object Method -ne 'GET')) 0
        Assert-Equal 0 $state.ReportAttempts
        Assert-Count $state.Reports 0
        Assert-True ($run.Console -match 'later steps are skipped')
    }
}

Get-A365TestResults
