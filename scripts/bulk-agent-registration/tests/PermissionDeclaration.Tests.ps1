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
    -FunctionName @('Test-HasProperty', 'Merge-GraphRequiredResourceAccess')
. ([scriptblock]::Create($functionSource))

$graphAppId = '00000003-0000-0000-c000-000000000000'

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

Test-Case '-SkipGrant declaration has a structural guard before the direct grant branch' {
    $source = Get-Content -LiteralPath $script:AutomationAppPath -Raw
    $declarationIndex = $source.IndexOf('$permissionMerge = Merge-GraphRequiredResourceAccess')
    $skipGrantIndex = $source.IndexOf('if ($SkipGrant)', $declarationIndex)

    Assert-True ($declarationIndex -ge 0) 'The script must invoke the requiredResourceAccess merge.'
    Assert-True ($skipGrantIndex -gt $declarationIndex) `
        'Structural proxy: permission declaration must appear before -SkipGrant bypasses direct app-role assignment.'
}

Get-A365TestResults
