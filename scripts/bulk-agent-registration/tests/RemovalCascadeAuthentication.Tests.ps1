# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    Regression tests for authentication values forwarded by Remove-A365Blueprint.ps1 when
    -Force delegates dependent cleanup to the AgentUser and AgentIdentity removal scripts.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'TestHelpers.psm1') -Force

$script:BlueprintRemovalPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Remove-A365Blueprint.ps1')).ProviderPath
$script:ResolvedTenantId = 'resolved-tenant-id'

function New-A365CascadeFixture {
    param(
        [switch] $IncludeAgentUser,
        [switch] $IncludeAgentIdentity
    )

    $root = Join-Path ([IO.Path]::GetTempPath()) "a365-cascade-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $capturePath = Join-Path $root 'calls.jsonl'

    $childScript = @'
param(
    [string[]] $AgentUserId,
    [string[]] $AgentIdentityId,
    [switch] $Force,
    [string] $TenantId,
    [string] $ClientId,
    [switch] $Interactive
)

[pscustomobject]@{
    Script = [IO.Path]::GetFileName($PSCommandPath)
    TenantId = $TenantId
    ClientId = $ClientId
    Interactive = [bool]$Interactive
    AgentUserId = @($AgentUserId)
    AgentIdentityId = @($AgentIdentityId)
} | ConvertTo-Json -Compress | Add-Content -LiteralPath $env:A365_CASCADE_CAPTURE
'@

    Set-Content -LiteralPath (Join-Path $root 'Remove-A365AgentUser.ps1') -Value $childScript
    Set-Content -LiteralPath (Join-Path $root 'Remove-A365AgentIdentity.ps1') -Value $childScript

    $global:A365CascadeFixture = [pscustomobject]@{
        IncludeAgentUser = [bool]$IncludeAgentUser
        IncludeAgentIdentity = [bool]$IncludeAgentIdentity
    }
    $env:A365_CASCADE_CAPTURE = $capturePath

    [pscustomobject]@{
        Root = $root
        CapturePath = $capturePath
    }
}

function Remove-A365CascadeFixture {
    param($Fixture)

    Remove-Item Env:A365_CASCADE_CAPTURE -ErrorAction SilentlyContinue
    Remove-Variable -Name A365CascadeFixture -Scope Global -ErrorAction SilentlyContinue
    if ($Fixture -and (Test-Path -LiteralPath $Fixture.Root)) {
        Remove-Item -LiteralPath $Fixture.Root -Recurse -Force
    }
}

function Get-A365CascadeCalls {
    param([Parameter(Mandatory)] $Fixture)

    if (-not (Test-Path -LiteralPath $Fixture.CapturePath)) { return , @() }
    return , @(Get-Content -LiteralPath $Fixture.CapturePath | ForEach-Object { $_ | ConvertFrom-Json })
}

function global:Connect-MgGraph {
    param(
        [string] $TenantId,
        [string] $ClientId,
        [string[]] $Scopes,
        [switch] $NoWelcome,
        [string] $ErrorAction
    )
}

function global:Get-MgContext {
    [pscustomobject]@{
        AuthType = 'Delegated'
        Account = 'operator@contoso.com'
        ClientId = 'graph-sdk-default-client'
        TenantId = 'resolved-tenant-id'
    }
}

function global:Invoke-MgGraphRequest {
    param(
        [Parameter(Mandatory)][string] $Method,
        [Parameter(Mandatory)][string] $Uri,
        $Headers,
        [string] $OutputType,
        $Body,
        [string] $ContentType
    )

    if ($Method -eq 'GET' -and $Uri -match "/applications\(appId='blueprint-app'\)$") {
        return [pscustomobject]@{
            id = 'blueprint-object'
            appId = 'blueprint-app'
            displayName = 'Test blueprint'
            '@odata.type' = '#microsoft.graph.agentIdentityBlueprint'
        }
    }
    if ($Method -eq 'GET' -and $Uri -match 'servicePrincipals/microsoft\.graph\.agentIdentity') {
        $value = if ($global:A365CascadeFixture.IncludeAgentIdentity) {
            @([pscustomobject]@{ id = 'identity-1'; displayName = 'Identity one' })
        }
        else {
            @()
        }
        return [pscustomobject]@{ value = $value }
    }
    if ($Method -eq 'GET' -and $Uri -match 'users/microsoft\.graph\.agentUser') {
        $value = if ($global:A365CascadeFixture.IncludeAgentUser) {
            @([pscustomobject]@{
                    id = 'user-1'
                    displayName = 'User one'
                    userPrincipalName = 'user-one@contoso.com'
                })
        }
        else {
            @()
        }
        return [pscustomobject]@{ value = $value }
    }
    if ($Method -eq 'GET' -and $Uri -match '/servicePrincipals\?') {
        return [pscustomobject]@{ value = @() }
    }
    if ($Method -eq 'DELETE' -and $Uri -match '/applications/blueprint-object$') {
        return $null
    }
    if ($Method -eq 'GET' -and $Uri -match '/applications/blueprint-object$') {
        throw 'HTTP 404 Not Found'
    }

    throw "Unexpected Graph request: $Method $Uri"
}

function Invoke-A365BlueprintCascade {
    param(
        [Parameter(Mandatory)] $Fixture,
        [string] $ClientId
    )

    $arguments = @{
        BlueprintId = 'blueprint-app'
        Interactive = $true
        Force = $true
        ScriptRoot = $Fixture.Root
        Confirm = $false
    }
    if ($ClientId) { $arguments.ClientId = $ClientId }

    & $script:BlueprintRemovalPath @arguments
}

try {
    Test-Case 'Interactive AgentUser cascade rejects a missing caller-controlled ClientId before child deletion' {
        $fixture = New-A365CascadeFixture -IncludeAgentUser -IncludeAgentIdentity
        try {
            Assert-Throws {
                Invoke-A365BlueprintCascade -Fixture $fixture
            } 'ClientId.*interactive blueprint cascade.*AgentUsers'
            Assert-Count (Get-A365CascadeCalls -Fixture $fixture) 0 `
                'No child deletion may start before the interactive AgentUser ClientId requirement is satisfied.'
        }
        finally {
            Remove-A365CascadeFixture -Fixture $fixture
        }
    }

    Test-Case 'Interactive AgentUser cascade forwards resolved tenant and caller-controlled ClientId' {
        $fixture = New-A365CascadeFixture -IncludeAgentUser -IncludeAgentIdentity
        try {
            Invoke-A365BlueprintCascade -Fixture $fixture -ClientId 'caller-public-client'
            $calls = Get-A365CascadeCalls -Fixture $fixture

            Assert-Count $calls 2 'Both dependent object types must be delegated to their removal scripts.'
            foreach ($call in $calls) {
                Assert-Equal $script:ResolvedTenantId $call.TenantId `
                    'Child removers must receive the tenant resolved from the active Graph context.'
                Assert-Equal 'caller-public-client' $call.ClientId `
                    'Interactive child removers must receive the caller-controlled public-client ID.'
                Assert-True $call.Interactive 'The cascade must preserve interactive authentication mode.'
            }
        }
        finally {
            Remove-A365CascadeFixture -Fixture $fixture
        }
    }

    Test-Case 'Identity-only interactive cascade remains valid without ClientId and receives resolved tenant' {
        $fixture = New-A365CascadeFixture -IncludeAgentIdentity
        try {
            Invoke-A365BlueprintCascade -Fixture $fixture
            $calls = Get-A365CascadeCalls -Fixture $fixture

            Assert-Count $calls 1 'An identity-only cascade must invoke only the AgentIdentity remover.'
            Assert-Equal 'Remove-A365AgentIdentity.ps1' $calls[0].Script
            Assert-Equal $script:ResolvedTenantId $calls[0].TenantId `
                'The identity remover must receive the tenant resolved from the active Graph context.'
            Assert-Equal '' $calls[0].ClientId `
                'Identity-only interactive removal does not require a caller-controlled public client.'
            Assert-True $calls[0].Interactive 'The cascade must preserve interactive authentication mode.'
        }
        finally {
            Remove-A365CascadeFixture -Fixture $fixture
        }
    }
}
finally {
    Remove-Item Function:\global:Connect-MgGraph -ErrorAction SilentlyContinue
    Remove-Item Function:\global:Get-MgContext -ErrorAction SilentlyContinue
    Remove-Item Function:\global:Invoke-MgGraphRequest -ErrorAction SilentlyContinue
    Remove-Item Env:A365_CASCADE_CAPTURE -ErrorAction SilentlyContinue
    Remove-Variable -Name A365CascadeFixture -Scope Global -ErrorAction SilentlyContinue
}

Get-A365TestResults
