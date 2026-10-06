# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    Verifies that routine interactive AgentUser provisioning does not request tenant-wide
    permission-management scopes that are needed only for permission bootstrap.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'TestHelpers.psm1') -Force

function Get-A365FunctionSource {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $FunctionName
    )

    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$errors
    )
    if ($errors.Count -gt 0) {
        throw "Could not parse '$Path': $($errors[0].Message)"
    }

    $definition = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $FunctionName
        }, $true)
    if (-not $definition) {
        throw "Function '$FunctionName' was not found in '$Path'."
    }

    return $definition.Extent.Text
}

$agentUserScript = (Resolve-Path (Join-Path $PSScriptRoot '..' 'New-A365AgentUser.ps1')).ProviderPath
. ([scriptblock]::Create((Get-A365FunctionSource -Path $agentUserScript -FunctionName 'Get-InteractiveDelegatedScopes')))

Test-Case 'Routine interactive AgentUser scopes exclude privileged permission-management scopes' {
    $script:ConfigurePermissions = $false
    $scopes = @(Get-InteractiveDelegatedScopes)

    Assert-False ($scopes -contains 'DelegatedPermissionGrant.ReadWrite.All') `
        'Routine provisioning never writes delegated grants and must not request tenant-wide delegated-grant management.'
    Assert-False ($scopes -contains 'AppRoleAssignment.ReadWrite.All') `
        'Routine provisioning does not create app-role assignments and must not require permission-bootstrap consent.'
    Assert-False ($scopes -contains 'Application.ReadWrite.All') `
        'Routine provisioning must not request application-write access used only by permission bootstrap.'
    Assert-True ($scopes -contains 'AgentIdUser.ReadWrite.All') `
        'The least-privilege change must preserve the delegated scope required to create and update AgentUsers.'
}

Test-Case 'ConfigurePermissions requests only the permission-management scopes it uses' {
    $script:ConfigurePermissions = $true
    $scopes = @(Get-InteractiveDelegatedScopes)

    Assert-True ($scopes -contains 'Application.ReadWrite.All') `
        'Permission bootstrap writes requiredResourceAccess on the target application.'
    Assert-True ($scopes -contains 'AppRoleAssignment.ReadWrite.All') `
        'Permission bootstrap reads and creates appRoleAssignments for the target service principal.'
    Assert-False ($scopes -contains 'DelegatedPermissionGrant.ReadWrite.All') `
        'Permission bootstrap creates app-role assignments, not delegated permission grants.'
}

Remove-Variable -Name ConfigurePermissions -Scope Script -ErrorAction SilentlyContinue

Get-A365TestResults
