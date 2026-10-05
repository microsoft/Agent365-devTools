# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    Deterministic stand-in for New-A365AgentBlueprint.ps1's update mode, used only by
    SecureAuthenticationInput.Tests.ps1 (via Update-A365Blueprint.ps1 -ScriptRoot pointing
    here). Records enough about -ClientSecret, -CertificatePassword and -AccessToken to prove
    that Update-A365Blueprint.ps1 forwards the exact object it was given - never a copy, a
    re-typed value, or a stringified one - without making any Graph call.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [switch] $Update,
    [Parameter(Mandatory)][string] $TenantId,
    [Parameter(Mandatory)][string] $BlueprintId,

    [string]   $DisplayName,
    [string]   $Description,
    [string[]] $Sponsor,
    [string[]] $Owner,
    [object[]] $RequiredPermission,
    [switch]   $RequireOwnerAssignment,
    [switch]   $NewClientSecret,
    [switch]   $GrantAdminConsent,
    [switch]   $SkipInheritablePermissions,
    [string]   $ManagedIdentityPrincipalId,

    [string] $ClientId,
    [object] $ClientSecret,
    [string] $CertificateThumbprint,
    [object] $Certificate,
    [string] $CertificatePath,
    [object] $CertificatePassword,
    [switch] $UseManagedIdentity,
    [object] $AccessToken,
    [switch] $Interactive,
    [switch] $SkipPermissionCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# See tests/fixtures/A365-AutomationOrchestrator.ps1's header for why Get-Variable (not a bare
# $global: reference) is required under Set-StrictMode, and why hash codes - not equality -
# are what proves object identity survived the round trip through the wrapper unchanged.
$callCapture = Get-Variable -Name 'A365UpdateWrapperFixtureCalls' -Scope Global -ErrorAction SilentlyContinue
if ($callCapture -and $null -ne $callCapture.Value) {
    $callCapture.Value.Add([pscustomobject]@{
        Script                    = 'Blueprint'
        Update                    = [bool]$Update
        ClientSecretIdentity      = if ($null -ne $ClientSecret) { [System.Runtime.CompilerServices.RuntimeHelpers]::GetHashCode($ClientSecret) } else { $null }
        ClientSecretType          = if ($null -ne $ClientSecret) { $ClientSecret.GetType().FullName } else { $null }
        CertificatePasswordIdentity = if ($null -ne $CertificatePassword) { [System.Runtime.CompilerServices.RuntimeHelpers]::GetHashCode($CertificatePassword) } else { $null }
        CertificatePasswordType   = if ($null -ne $CertificatePassword) { $CertificatePassword.GetType().FullName } else { $null }
        AccessTokenIdentity       = if ($null -ne $AccessToken) { [System.Runtime.CompilerServices.RuntimeHelpers]::GetHashCode($AccessToken) } else { $null }
        AccessTokenType           = if ($null -ne $AccessToken) { $AccessToken.GetType().FullName } else { $null }
    })
}

[pscustomobject]@{ blueprintAppId = $BlueprintId; updated = $true }
