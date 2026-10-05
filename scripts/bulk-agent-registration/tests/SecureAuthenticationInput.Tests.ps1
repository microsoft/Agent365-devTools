# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    Verifies that update wrappers preserve string and SecureString credential inputs, and that
    the internal token helper normalizes both forms without making a live network request.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'TestHelpers.psm1') -Force

function New-A365SecureStringMarker {
    <#
    .SYNOPSIS
        Builds a SecureString from a plain string, the same char-by-char way
        ConvertTo-SecureStringValue does, without ever calling ConvertTo-SecureString
        -AsPlainText (which this suite has no need for, and which some hosts restrict).
    #>
    param([Parameter(Mandatory)][string] $Value)
    $secure = [securestring]::new()
    foreach ($char in $Value.ToCharArray()) { $secure.AppendChar($char) }
    $secure.MakeReadOnly()
    return $secure
}

# Verify that every update wrapper preserves credential type and object identity.

Write-Host 'Secure authentication input: Update-A365*.ps1 wrapper forwarding' -ForegroundColor Cyan

$script:UpdateWrapperFixturesDir = (Resolve-Path (Join-Path $PSScriptRoot 'fixtures' 'update-wrappers')).ProviderPath

$wrapperCases = @(
    @{ Name = 'Update-A365Blueprint.ps1';         Path = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Update-A365Blueprint.ps1')).ProviderPath;         IdParam = 'BlueprintId';     IdValue = 'bp-fixture-1' }
    @{ Name = 'Update-A365AgentIdentity.ps1';     Path = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Update-A365AgentIdentity.ps1')).ProviderPath;     IdParam = 'AgentIdentityId'; IdValue = 'ai-fixture-1' }
    @{ Name = 'Update-A365AgentUser.ps1';         Path = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Update-A365AgentUser.ps1')).ProviderPath;         IdParam = 'AgentUserId';     IdValue = 'au-fixture-1@contoso.com' }
    @{ Name = 'Update-A365AgentRegistration.ps1'; Path = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Update-A365AgentRegistration.ps1')).ProviderPath; IdParam = 'RegistrationId';  IdValue = 'T_reg-fixture-1' }
)

foreach ($case in $wrapperCases) {

    Test-Case "$($case.Name): a SecureString -ClientSecret/-CertificatePassword/-AccessToken all reach the step script as the exact live objects bound on the command line" {
        $global:A365UpdateWrapperFixtureCalls = [System.Collections.Generic.List[object]]::new()
        $clientSecret = $null
        $certificatePassword = $null
        $accessToken = $null
        try {
            $clientSecret = New-A365SecureStringMarker -Value 'client-secret-marker'
            $certificatePassword = New-A365SecureStringMarker -Value 'cert-password-marker'
            $accessToken = New-A365SecureStringMarker -Value 'access-token-marker'
            $splat = @{
                TenantId             = 'tenant-id'
                ScriptRoot           = $script:UpdateWrapperFixturesDir
                ClientId             = 'test-client'
                ClientSecret         = $clientSecret
                CertificatePassword  = $certificatePassword
                AccessToken          = $accessToken
                Confirm              = $false
            }
            $splat[$case.IdParam] = $case.IdValue

            & $case.Path @splat | Out-Null

            Assert-Equal 1 $global:A365UpdateWrapperFixtureCalls.Count "$($case.Name): exactly one call must reach the fixture step script per invocation."
            $call = $global:A365UpdateWrapperFixtureCalls[0]

            # SecureString inputs must remain SecureString values at the step boundary.
            Assert-Equal 'System.Security.SecureString' $call.ClientSecretType "$($case.Name): -ClientSecret must still be a SecureString when it reaches the step script."
            Assert-Equal 'System.Security.SecureString' $call.CertificatePasswordType "$($case.Name): -CertificatePassword must still be a SecureString when it reaches the step script."
            Assert-Equal 'System.Security.SecureString' $call.AccessTokenType "$($case.Name): -AccessToken must still be a SecureString when it reaches the step script."

            # Wrappers must forward the exact live credential objects.
            Assert-Equal ([System.Runtime.CompilerServices.RuntimeHelpers]::GetHashCode($clientSecret)) $call.ClientSecretIdentity "$($case.Name): -ClientSecret must reach the step script as the identical object instance, not a copy."
            Assert-Equal ([System.Runtime.CompilerServices.RuntimeHelpers]::GetHashCode($certificatePassword)) $call.CertificatePasswordIdentity "$($case.Name): -CertificatePassword must reach the step script as the identical object instance, not a copy."
            Assert-Equal ([System.Runtime.CompilerServices.RuntimeHelpers]::GetHashCode($accessToken)) $call.AccessTokenIdentity "$($case.Name): -AccessToken must reach the step script as the identical object instance, not a copy."
        }
        finally {
            Remove-Variable -Name A365UpdateWrapperFixtureCalls -Scope Global -ErrorAction SilentlyContinue
            if ($clientSecret) { $clientSecret.Dispose() }
            if ($certificatePassword) { $certificatePassword.Dispose() }
            if ($accessToken) { $accessToken.Dispose() }
        }
    }

    Test-Case "$($case.Name): a plain string -ClientSecret/-CertificatePassword/-AccessToken remain plain strings at the step script (backward compatibility)" {
        $global:A365UpdateWrapperFixtureCalls = [System.Collections.Generic.List[object]]::new()
        try {
            $splat = @{
                TenantId             = 'tenant-id'
                ScriptRoot           = $script:UpdateWrapperFixturesDir
                ClientId             = 'test-client'
                ClientSecret         = 'plain-client-secret'
                CertificatePassword  = 'plain-cert-password'
                AccessToken          = 'plain-access-token'
                Confirm              = $false
            }
            $splat[$case.IdParam] = $case.IdValue

            & $case.Path @splat | Out-Null

            Assert-Equal 1 $global:A365UpdateWrapperFixtureCalls.Count "$($case.Name): exactly one call must reach the fixture step script per invocation."
            $call = $global:A365UpdateWrapperFixtureCalls[0]

            # Plain-string authentication remains backward compatible.
            Assert-Equal 'System.String' $call.ClientSecretType "$($case.Name): a plain string -ClientSecret must still be forwarded as a plain string."
            Assert-Equal 'System.String' $call.CertificatePasswordType "$($case.Name): a plain string -CertificatePassword must still be forwarded as a plain string."
            Assert-Equal 'System.String' $call.AccessTokenType "$($case.Name): a plain string -AccessToken must still be forwarded as a plain string."
        }
        finally {
            Remove-Variable -Name A365UpdateWrapperFixtureCalls -Scope Global -ErrorAction SilentlyContinue
        }
    }
}

# Verify token-helper normalization without a live network call.

Write-Host 'Secure authentication input: Get-AppOnlyGraphToken (internal helper) accepts string and SecureString' -ForegroundColor Cyan

function Get-A365ExtractedFunctionSource {
    <#
    .SYNOPSIS
        Extracts the source text of one or more named functions out of a .ps1 file using the
        PowerShell AST, without executing anything else in that file.
    .DESCRIPTION
        Avoids executing the step script's top-level Graph operations.
    #>
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string[]] $FunctionName
    )
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref] $tokens, [ref] $parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw "'$Path' has $($parseErrors.Count) parse error(s); cannot safely extract functions from it."
    }
    $found = $ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $FunctionName -contains $node.Name
        }, $true)
    $foundNames = @($found | ForEach-Object { $_.Name })
    foreach ($name in $FunctionName) {
        if ($foundNames -notcontains $name) {
            throw "Function '$name' was not found in '$Path'. It may have been renamed or removed; this test's seam extraction needs updating to match."
        }
    }
    return ($found | ForEach-Object { $_.Extent.Text }) -join "`n`n"
}

$script:AgentIdentityStepScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'New-A365AgentIdentity.ps1')).ProviderPath

# Load only the helper and its direct dependencies into the test scope.
$extractedFunctionSource = Get-A365ExtractedFunctionSource -Path $script:AgentIdentityStepScriptPath `
    -FunctionName @('Get-AppOnlyGraphToken', 'ConvertTo-SecureStringValue', 'Test-HasProperty')
. ([scriptblock]::Create($extractedFunctionSource))

Test-Case 'Get-AppOnlyGraphToken accepts a plain string -ClientSecret and sends its own plaintext value to the token endpoint' {
    $global:A365GraphTokenMockRequestBody = $null
    try {
        function Invoke-RestMethod {
            # Shadow the cmdlet so the test never makes a network request.
            param($Method, $Uri, $Body, $ErrorAction)
            $global:A365GraphTokenMockRequestBody = $Body.Clone()
            [pscustomobject]@{ access_token = 'fake-token-for-plain-string-secret' }
        }

        $token = Get-AppOnlyGraphToken -TenantId 'tenant-id' -ClientId 'client-id' -ClientSecret 'plain-secret-value'

        Assert-Equal 'fake-token-for-plain-string-secret' $token 'Get-AppOnlyGraphToken must return the (mocked) token endpoint''s access_token unchanged.'
        Assert-NotNull $global:A365GraphTokenMockRequestBody 'The mocked Invoke-RestMethod must have been called exactly once, with a captured request body.'
        Assert-Equal 'plain-secret-value' $global:A365GraphTokenMockRequestBody.client_secret "Requirement: changing -ClientSecret from [securestring] to [object] must not change behavior for a plain string - it must still reach the token request body as its own plaintext value."
    }
    finally {
        Remove-Item -Path Function:\Invoke-RestMethod -ErrorAction SilentlyContinue
        Remove-Variable -Name A365GraphTokenMockRequestBody -Scope Global -ErrorAction SilentlyContinue
    }
}

Test-Case 'Get-AppOnlyGraphToken accepts a SecureString -ClientSecret and sends its decrypted plaintext value to the token endpoint' {
    $global:A365GraphTokenMockRequestBody = $null
    $secure = $null
    try {
        function Invoke-RestMethod {
            param($Method, $Uri, $Body, $ErrorAction)
            $global:A365GraphTokenMockRequestBody = $Body.Clone()
            [pscustomobject]@{ access_token = 'fake-token-for-securestring-secret' }
        }

        $secure = New-A365SecureStringMarker -Value 'secure-secret-value'
        $token = Get-AppOnlyGraphToken -TenantId 'tenant-id' -ClientId 'client-id' -ClientSecret $secure

        Assert-Equal 'fake-token-for-securestring-secret' $token 'Get-AppOnlyGraphToken must return the (mocked) token endpoint''s access_token for a SecureString secret too.'
        Assert-NotNull $global:A365GraphTokenMockRequestBody 'The mocked Invoke-RestMethod must have been called exactly once, with a captured request body.'
        Assert-Equal 'secure-secret-value' $global:A365GraphTokenMockRequestBody.client_secret "Requirement: a SecureString -ClientSecret must be decrypted to its plaintext value before being placed in the OAuth token request body - this is the one boundary in the suite where unwrapping a SecureString is correct, and it must still happen now that the parameter is typed [object] instead of [securestring]."
    }
    finally {
        Remove-Item -Path Function:\Invoke-RestMethod -ErrorAction SilentlyContinue
        Remove-Variable -Name A365GraphTokenMockRequestBody -Scope Global -ErrorAction SilentlyContinue
        if ($secure) { $secure.Dispose() }
    }
}

Test-Case 'Get-AppOnlyGraphToken rejects an empty SecureString -ClientSecret via the same validation New-A365AgentIdentity.ps1 relies on elsewhere' {
    $empty = $null
    function Invoke-RestMethod {
        param($Method, $Uri, $Body, $ErrorAction)
        throw 'Get-AppOnlyGraphToken must not reach the token endpoint at all when -ClientSecret fails normalization.'
    }
    try {
        $empty = [securestring]::new()
        $empty.MakeReadOnly()
        Assert-Throws -ScriptBlock {
            Get-AppOnlyGraphToken -TenantId 'tenant-id' -ClientId 'client-id' -ClientSecret $empty
        } -ExpectedMessagePattern 'empty SecureString' `
            -Message 'An empty SecureString must be refused by ConvertTo-SecureStringValue before any network call is attempted - normalizing via [object] must not silently accept an unusable secret.'
    }
    finally {
        Remove-Item -Path Function:\Invoke-RestMethod -ErrorAction SilentlyContinue
        if ($empty) { $empty.Dispose() }
    }
}

Test-Case 'Get-AppOnlyGraphToken rejects an explicit null -ClientSecret before any network call' {
    function Invoke-RestMethod {
        param($Method, $Uri, $Body, $ErrorAction)
        throw 'Get-AppOnlyGraphToken must not reach the token endpoint for a null secret.'
    }
    try {
        Assert-Throws -ScriptBlock {
            Get-AppOnlyGraphToken -TenantId 'tenant-id' -ClientId 'client-id' -ClientSecret $null
        } -Message 'An explicit null -ClientSecret must be rejected by parameter validation before any network call.'
    }
    finally {
        Remove-Item -Path Function:\Invoke-RestMethod -ErrorAction SilentlyContinue
    }
}

Get-A365TestResults
