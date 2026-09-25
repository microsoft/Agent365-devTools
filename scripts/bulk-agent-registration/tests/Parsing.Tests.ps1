# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    Tests for the small pure parsing helpers in A365-BulkOnboardingCsv.psm1: booleans,
    semicolon arrays, JSON, GUIDs and UPNs. Run via Run-Tests.ps1.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'TestHelpers.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..' 'A365-BulkOnboardingCsv.psm1') -Force

Write-Host 'Parsing helpers' -ForegroundColor Cyan

Test-Case 'Test-A365BulkOnboardingGuid accepts a well-formed GUID' {
    Assert-True (Test-A365BulkOnboardingGuid -Value ([guid]::NewGuid().ToString()))
}
Test-Case 'Test-A365BulkOnboardingGuid rejects non-GUID text' {
    Assert-False (Test-A365BulkOnboardingGuid -Value 'not-a-guid')
}
Test-Case 'Test-A365BulkOnboardingGuid rejects blank' {
    Assert-False (Test-A365BulkOnboardingGuid -Value '')
}

Test-Case 'Test-A365BulkOnboardingUpn accepts a well-formed UPN' {
    Assert-True (Test-A365BulkOnboardingUpn -Value 'agent@contoso.com')
}
Test-Case 'Test-A365BulkOnboardingUpn rejects text with no @' {
    Assert-False (Test-A365BulkOnboardingUpn -Value 'agent.contoso.com')
}
Test-Case 'Test-A365BulkOnboardingUpn rejects text with no domain dot' {
    Assert-False (Test-A365BulkOnboardingUpn -Value 'agent@contoso')
}

Test-Case 'ConvertTo-A365BulkOnboardingBool: blank means not present, value false' {
    $r = ConvertTo-A365BulkOnboardingBool -Value ''
    Assert-True $r.IsValid
    Assert-False $r.IsPresent
    Assert-False $r.Value
}
Test-Case 'ConvertTo-A365BulkOnboardingBool: true (any case) parses true' {
    $r = ConvertTo-A365BulkOnboardingBool -Value 'TRUE'
    Assert-True $r.IsValid
    Assert-True $r.IsPresent
    Assert-True $r.Value
}
Test-Case 'ConvertTo-A365BulkOnboardingBool: false parses false and IsPresent' {
    $r = ConvertTo-A365BulkOnboardingBool -Value 'false'
    Assert-True $r.IsValid
    Assert-True $r.IsPresent
    Assert-False $r.Value
}
Test-Case 'ConvertTo-A365BulkOnboardingBool: anything else is strictly rejected' {
    $r = ConvertTo-A365BulkOnboardingBool -Value 'yes'
    Assert-False $r.IsValid
    Assert-NotNull $r.ErrorMessage
}

Test-Case 'ConvertTo-A365BulkOnboardingArray: blank yields an empty array, not $null' {
    $r = ConvertTo-A365BulkOnboardingArray -Value ''
    Assert-NotNull $r
    Assert-Count $r 0
}
Test-Case 'ConvertTo-A365BulkOnboardingArray: single value still yields an array (no pipeline unwrap)' {
    $r = ConvertTo-A365BulkOnboardingArray -Value 'ana@contoso.com'
    Assert-Equal 'System.Object[]' $r.GetType().FullName
    Assert-Count $r 1
    Assert-Equal 'ana@contoso.com' $r[0]
}
Test-Case 'ConvertTo-A365BulkOnboardingArray: splits on semicolon and trims whitespace' {
    $r = ConvertTo-A365BulkOnboardingArray -Value 'a@x.com;  b@x.com ;c@x.com'
    Assert-Count $r 3
    Assert-Equal 'a@x.com' $r[0]
    Assert-Equal 'b@x.com' $r[1]
    Assert-Equal 'c@x.com' $r[2]
}

Test-Case 'ConvertFrom-A365BulkOnboardingJson: blank is valid and null' {
    $r = ConvertFrom-A365BulkOnboardingJson -Value ''
    Assert-True $r.IsValid
    Assert-Null $r.Value
}
Test-Case 'ConvertFrom-A365BulkOnboardingJson: valid JSON object parses to a hashtable' {
    $r = ConvertFrom-A365BulkOnboardingJson -Value '{"a":1}'
    Assert-True $r.IsValid
    # ConvertFrom-Json -AsHashtable returns OrderedHashtable on some PowerShell 7 minor
    # versions and plain Hashtable on others; both satisfy -is [hashtable], which is the
    # only contract A365-BulkOnboardingCsv.psm1 relies on.
    Assert-True ($r.Value -is [hashtable])
}
Test-Case 'ConvertFrom-A365BulkOnboardingJson: malformed JSON is reported, not thrown' {
    $r = ConvertFrom-A365BulkOnboardingJson -Value '{not json'
    Assert-False $r.IsValid
    Assert-NotNull $r.ErrorMessage
}

Test-Case 'Test-A365BulkOnboardingParameterJsonKeys: finds a case-insensitive collision' {
    $collisions = Test-A365BulkOnboardingParameterJsonKeys -Hashtable @{ TenantId = 'x' } -BlockedKeys @('tenantid')
    Assert-Count $collisions 1
    Assert-Contains $collisions 'TenantId'
}
Test-Case 'Test-A365BulkOnboardingParameterJsonKeys: no collision returns an empty array' {
    $collisions = Test-A365BulkOnboardingParameterJsonKeys -Hashtable @{ Foo = 'x' } -BlockedKeys @('TenantId')
    Assert-NotNull $collisions
    Assert-Count $collisions 0
}

Test-Case 'Get-A365BulkOnboardingKnownHeaders includes the always-present columns' {
    $headers = Get-A365BulkOnboardingKnownHeaders
    Assert-Contains $headers 'ObjectType'
    Assert-Contains $headers 'Key'
    Assert-Contains $headers 'ParentKey'
    Assert-Contains $headers 'ExistingId'
}

Test-Case 'Import-A365BulkOnboardingCsv throws for a missing file' {
    Assert-Throws { Import-A365BulkOnboardingCsv -Path (Join-Path ([IO.Path]::GetTempPath()) "does-not-exist-$([guid]::NewGuid()).csv") } 'was not found'
}

Test-Case 'Import-A365BulkOnboardingCsv rejects a path without a .csv extension' {
    $path = Join-Path ([IO.Path]::GetTempPath()) "a365-wrong-ext-$([guid]::NewGuid()).txt"
    Set-Content -Path $path -Value 'ObjectType,Key' -NoNewline
    try {
        Assert-Throws { Import-A365BulkOnboardingCsv -Path $path } 'must have a \.csv extension'
    } finally {
        Remove-Item $path -ErrorAction SilentlyContinue
    }
}

Test-Case 'Import-A365BulkOnboardingCsv rejects a blank path' {
    Assert-Throws { Import-A365BulkOnboardingCsv -Path '   ' }
}

Test-Case 'Import-A365BulkOnboardingCsv throws for a genuinely empty file' {
    $path = Join-Path ([IO.Path]::GetTempPath()) "a365-empty-$([guid]::NewGuid()).csv"
    Set-Content -Path $path -Value '' -NoNewline
    try {
        Assert-Throws { Import-A365BulkOnboardingCsv -Path $path } 'is empty'
    } finally {
        Remove-Item $path -ErrorAction SilentlyContinue
    }
}

Test-Case 'Import-A365BulkOnboardingCsv returns an array even for a single data row' {
    $path = Join-Path ([IO.Path]::GetTempPath()) "a365-onerow-$([guid]::NewGuid()).csv"
    "ObjectType,Key,Sponsor,DisplayName`nBlueprint,bp1,ana@contoso.com,BP One" | Set-Content -Path $path -Encoding utf8
    try {
        $rows = Import-A365BulkOnboardingCsv -Path $path
        Assert-Equal 'System.Object[]' $rows.GetType().FullName
        Assert-Count $rows 1
    } finally {
        Remove-Item $path -ErrorAction SilentlyContinue
    }
}

Test-Case 'Import-A365BulkOnboardingCsv reads a path containing literal [ ] characters (proves -LiteralPath, not wildcard expansion)' {
    # A bracketed name is a valid wildcard expression to Get-Content/Test-Path/Resolve-Path
    # unless -LiteralPath is used throughout; a plain -Path would silently fail to match.
    $path = Join-Path ([IO.Path]::GetTempPath()) "a365-[bracket]-test-$([guid]::NewGuid()).csv"
    "ObjectType,Key,Sponsor,DisplayName`nBlueprint,bp1,ana@contoso.com,BP One" | Set-Content -LiteralPath $path -Encoding utf8
    try {
        $rows = Import-A365BulkOnboardingCsv -Path $path
        Assert-Count $rows 1
        Assert-Equal 'bp1' $rows[0].Key
    } finally {
        Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue
    }
}

Get-A365TestResults
