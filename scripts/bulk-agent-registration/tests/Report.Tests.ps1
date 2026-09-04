# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    Tests for ConvertTo-A365BulkOnboardingRedactedValue: the recursive redaction applied to
    every row's child result before it is written to the aggregate JSON report.
    Run via Run-Tests.ps1.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'TestHelpers.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..' 'A365-BulkOnboardingCsv.psm1') -Force

Write-Host 'Report redaction' -ForegroundColor Cyan

Test-Case '$null passes through unchanged' {
    Assert-Null (ConvertTo-A365BulkOnboardingRedactedValue -Value $null)
}

Test-Case 'A primitive value passes through unchanged' {
    Assert-Equal 42 (ConvertTo-A365BulkOnboardingRedactedValue -Value 42)
    Assert-Equal 'hello' (ConvertTo-A365BulkOnboardingRedactedValue -Value 'hello')
}

Test-Case 'A SecureString is always redacted, even with -IncludeSecrets' {
    $secure = ConvertTo-SecureString 'p@ssw0rd' -AsPlainText -Force
    Assert-Equal '(redacted)' (ConvertTo-A365BulkOnboardingRedactedValue -Value $secure)
    Assert-Equal '(redacted)' (ConvertTo-A365BulkOnboardingRedactedValue -Value $secure -IncludeSecrets)
}

Test-Case 'A hashtable key named clientSecret is redacted by default' {
    $result = ConvertTo-A365BulkOnboardingRedactedValue -Value @{ clientSecret = 'abc123'; other = 'kept' }
    Assert-Equal '(redacted)' $result['clientSecret']
    Assert-Equal 'kept' $result['other']
}

Test-Case 'A sensitive key is matched case-insensitively (ClientSecret vs clientSecret)' {
    $result = ConvertTo-A365BulkOnboardingRedactedValue -Value @{ ClientSecret = 'abc123' }
    Assert-Equal '(redacted)' $result['ClientSecret']
}

Test-Case '-IncludeSecrets keeps a sensitive value' {
    $result = ConvertTo-A365BulkOnboardingRedactedValue -Value @{ clientSecret = 'abc123' } -IncludeSecrets
    Assert-Equal 'abc123' $result['clientSecret']
}

Test-Case 'Redaction recurses into a nested PSCustomObject' {
    $value = [pscustomobject]@{ outer = [pscustomobject]@{ Password = 'hunter2'; Name = 'kept' } }
    $result = ConvertTo-A365BulkOnboardingRedactedValue -Value $value
    Assert-Equal '(redacted)' $result.outer.Password
    Assert-Equal 'kept' $result.outer.Name
}

Test-Case 'Redaction recurses into an array of objects' {
    $value = @(
        [pscustomobject]@{ Secret = 's1' }
        [pscustomobject]@{ Secret = 's2' }
    )
    $result = ConvertTo-A365BulkOnboardingRedactedValue -Value $value
    Assert-Count $result 2
    Assert-Equal '(redacted)' $result[0].Secret
    Assert-Equal '(redacted)' $result[1].Secret
}

Test-Case 'Other sensitive names are covered: AccessToken, CertificatePassword, privateKey' {
    $result = ConvertTo-A365BulkOnboardingRedactedValue -Value @{ AccessToken = 't'; CertificatePassword = 'p'; privateKey = 'k'; keep = 'v' }
    Assert-Equal '(redacted)' $result['AccessToken']
    Assert-Equal '(redacted)' $result['CertificatePassword']
    Assert-Equal '(redacted)' $result['privateKey']
    Assert-Equal 'v' $result['keep']
}

Test-Case '-IncludeSecrets unlocks only the blueprint client secret, never a credential like AccessToken/CertificatePassword/Authorization' {
    # -IncludeBlueprintSecretsInOutput's own help text promises authentication inputs are
    # "never written to the report, with or without this switch" - this is the test that
    # would catch a regression collapsing the two redaction tiers back into one list/switch.
    $result = ConvertTo-A365BulkOnboardingRedactedValue -IncludeSecrets -Value @{
        clientSecret        = 'the-blueprint-secret'
        AccessToken         = 'a-bearer-token'
        CertificatePassword = 'a-cert-password'
        Authorization       = 'a-header-value'
        refreshToken        = 'a-refresh-token'
    }
    Assert-Equal 'the-blueprint-secret' $result['clientSecret'] '-IncludeSecrets must unlock the one thing it documents: the blueprint client secret.'
    Assert-Equal '(redacted)' $result['AccessToken']
    Assert-Equal '(redacted)' $result['CertificatePassword']
    Assert-Equal '(redacted)' $result['Authorization']
    Assert-Equal '(redacted)' $result['refreshToken']
}

Test-Case 'A SwitchParameter value is converted to a plain bool, not redacted' {
    function Test-SwitchCapture { param([switch] $Flag) $Flag }
    $switchValue = Test-SwitchCapture -Flag
    $result = ConvertTo-A365BulkOnboardingRedactedValue -Value @{ flag = $switchValue }
    Assert-Equal $true $result['flag']
}

Test-Case 'Recursion stops at a safe depth rather than looping forever' {
    $deep = $null
    for ($i = 0; $i -lt 20; $i++) { $deep = @{ inner = $deep; level = $i } }
    # Must not throw (stack overflow / infinite recursion) - the exact depth cutoff value is
    # an implementation detail, only termination is being asserted here.
    $result = ConvertTo-A365BulkOnboardingRedactedValue -Value $deep
    Assert-NotNull $result
}

Get-A365TestResults
