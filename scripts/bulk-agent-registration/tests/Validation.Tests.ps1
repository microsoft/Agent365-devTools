# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    Tests for ConvertTo-A365BulkOnboardingPlan: header/field validation, case-insensitivity,
    ExistingId/ParentKey legality, required-for-create fields, the manager xor rule, and the
    AssignLicense/licence-SKU rule that mirrors A365-AutomationOrchestrator.ps1's own
    precondition. Run via Run-Tests.ps1.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'TestHelpers.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..' 'A365-BulkOnboardingCsv.psm1') -Force

Write-Host 'Plan validation' -ForegroundColor Cyan

function New-Row {
    <# Builds a raw CSV-shaped row (PSCustomObject of strings) with every field from
       $Values, defaulting anything unset to ''. Mirrors what Import-Csv would hand back. #>
    param([hashtable] $Values = @{})
    $row = [ordered]@{}
    foreach ($h in (Get-A365BulkOnboardingKnownHeaders)) {
        $row[$h] = if ($Values.ContainsKey($h)) { [string]$Values[$h] } else { '' }
    }
    [pscustomobject]$row
}

Test-Case 'Empty row set is a validation error, not silently zero rows' {
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows @()
    Assert-Count $plan.Errors 1
    Assert-True ($plan.Errors[0].Message -match 'no data rows')
}

Test-Case 'An unknown header is rejected' {
    $rows = @([pscustomobject]@{ ObjectType = 'Blueprint'; Key = 'bp1'; Bogus = 'x' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Message -match "Unknown header 'Bogus'" }).Count -gt 0)
}

Test-Case 'A missing required header (Key) is rejected' {
    $rows = @([pscustomobject]@{ ObjectType = 'Blueprint' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Message -match "Required header 'Key' is missing" }).Count -gt 0)
}

Test-Case 'ObjectType is matched case-insensitively' {
    $rows = @(New-Row @{ ObjectType = 'blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-Count $plan.Errors 0
    Assert-Equal 'Blueprint' $plan.Nodes[0].ObjectType
}

Test-Case 'An unrecognised ObjectType is rejected' {
    $rows = @(New-Row @{ ObjectType = 'Widget'; Key = 'w1' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'ObjectType' }).Count -gt 0)
}

Test-Case 'Duplicate keys are rejected case-insensitively' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'BP1'; DisplayName = 'BP'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP2'; Sponsor = 'a@x.com' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Message -match 'Duplicate key' }).Count -gt 0)
}

Test-Case 'A Key is required' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'Key' -and $_.Message -match 'required' }).Count -gt 0)
}

Test-Case 'ExistingId is rejected on a type that does not allow it (AgentUser)' {
    $rows = @(New-Row @{ ObjectType = 'AgentUser'; Key = 'u1'; ExistingId = [guid]::NewGuid().ToString(); ParentKey = '' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'ExistingId' -and $_.Message -match 'not valid for AgentUser' }).Count -gt 0)
}

Test-Case 'A malformed ExistingId is rejected' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; ExistingId = 'not-a-guid' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'ExistingId' -and $_.Message -match 'not a valid GUID' }).Count -gt 0)
}

Test-Case 'ParentKey is illegal alongside ExistingId' {
    $rows = @(New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ExistingId = [guid]::NewGuid().ToString(); ParentKey = 'bp1' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'ParentKey' -and $_.Message -match 'must be blank on a row that references an ExistingId' }).Count -gt 0)
}

Test-Case 'ParentKey is required for a create row of a dependent type' {
    $rows = @(New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; DisplayName = 'AI'; Sponsor = 'a@x.com' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'ParentKey' -and $_.Message -match 'must set ParentKey' }).Count -gt 0)
}

Test-Case 'A root type (Blueprint) cannot have a ParentKey' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; ParentKey = 'somethingElse'; DisplayName = 'BP'; Sponsor = 'a@x.com' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'ParentKey' -and $_.Message -match 'root of the dependency tree' }).Count -gt 0)
}

Test-Case 'A column that does not apply to the ObjectType must be blank' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com'; PrincipalName = 'x@y.com' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'PrincipalName' -and $_.Message -match 'does not apply to Blueprint' }).Count -gt 0)
}

Test-Case 'Columns must be blank on a row that references an ExistingId' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; ExistingId = [guid]::NewGuid().ToString(); DisplayName = 'BP' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'DisplayName' -and $_.Message -match 'must be blank on a row that references an ExistingId' }).Count -gt 0)
}

Test-Case 'A required-for-create field is enforced (Blueprint needs Sponsor)' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'Sponsor' -and $_.Message -match 'required to create' }).Count -gt 0)
}

Test-Case 'An invalid GUID column is rejected' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com'; ManagedIdentityPrincipalId = 'nope' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'ManagedIdentityPrincipalId' -and $_.Message -match 'not a valid GUID' }).Count -gt 0)
}

Test-Case 'An invalid UPN column is rejected' {
    $rows = @(New-Row @{ ObjectType = 'AgentUser'; Key = 'u1'; ParentKey = 'ai1'; PrincipalName = 'not-a-upn' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'PrincipalName' -and $_.Message -match 'not a valid user principal name' }).Count -gt 0)
}

Test-Case 'A strict bool column rejects a non-bool value' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com'; RequireOwnerAssignment = 'yep' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'RequireOwnerAssignment' }).Count -gt 0)
}

Test-Case 'An enum column is matched case-insensitively and rejects an unlisted value' {
    $good = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentRegistration'; Key = 'r1'; ParentKey = 'ai1'; DisplayName = 'R'; Auth = 'interactive' }
    )
    $planGood = ConvertTo-A365BulkOnboardingPlan -Rows $good
    Assert-Count $planGood.Errors 0
    $regNode = @($planGood.Nodes | Where-Object { $_.Key -eq 'r1' })[0]
    Assert-Equal 'Interactive' $regNode.ParamValues['Auth']

    $bad = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentRegistration'; Key = 'r1'; ParentKey = 'ai1'; DisplayName = 'R'; Auth = 'Sometimes' }
    )
    $planBad = ConvertTo-A365BulkOnboardingPlan -Rows $bad
    Assert-True (@($planBad.Errors | Where-Object { $_.Column -eq 'Auth' }).Count -gt 0)
}

Test-Case 'A malformed permjson column is rejected' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com'; RequiredPermissionJson = '{not json' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'RequiredPermissionJson' }).Count -gt 0)
}

Test-Case 'A permjson column that is not an array of objects is rejected' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com'; RequiredPermissionJson = '"just a string"' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'RequiredPermissionJson' -and $_.Message -match 'JSON array' }).Count -gt 0)
}

Test-Case 'ParameterJson rejects a key already controlled by an explicit column (case-insensitive)' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com'; ParameterJson = '{"blueprintdisplayname":"x"}' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'ParameterJson' -and $_.Message -match 'BlueprintDisplayName' }).Count -gt 0)
}

Test-Case 'ParameterJson rejects the tenant id and a credential' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com'; ParameterJson = '{"TenantId":"x","ClientSecret":"y"}' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    $err = @($plan.Errors | Where-Object { $_.Column -eq 'ParameterJson' })[0]
    Assert-NotNull $err
    Assert-True ($err.Message -match 'TenantId')
    Assert-True ($err.Message -match 'ClientSecret')
}

Test-Case 'ParameterJson accepts an exact allowlisted advanced parameter' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com'; ParameterJson = '{"ExposedScopeValue":"access_as_user"}' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-Count $plan.Errors 0
}

Test-Case 'ParameterJson rejects unknown and abbreviated parameter names before PowerShell can bind them' {
    foreach ($parameterJson in @(
            '{"Blueprint":"11111111-1111-4111-8111-111111111111"}',
            '{"ClientSec":"secret"}',
            '{"GraphBaseUr":"https://example.invalid"}',
            '{"AuthorityHos":"https://example.invalid"}'
        )) {
        $rows = @(New-Row @{
                ObjectType = 'AgentRegistration'
                Key = 'r1'
                ParentKey = 'ai1'
                DisplayName = 'R'
                ParameterJson = $parameterJson
            })
        $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
        Assert-True (
            @($plan.Errors | Where-Object {
                $_.Column -eq 'ParameterJson' -and $_.Message -match 'unsupported or abbreviated'
            }).Count -gt 0
        ) "ParameterJson '$parameterJson' must be rejected before splatting."
    }
}

# ---------------------------------------------------------------------------
# ParameterJson blocklist: parent-id / anchor-id bypass keys. Without these, a row could set
# -*Parameter to point a create at a different parent than the one this tool resolved and
# validated, silently bypassing the whole dependency graph.
# ---------------------------------------------------------------------------

Test-Case 'ParameterJson rejects BlueprintAppId on an AgentIdentity row (parent-id bypass)' {
    $rows = @(New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com'; ParameterJson = '{"BlueprintAppId":"11111111-1111-4111-8111-111111111111"}' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'ParameterJson' -and $_.Message -match 'BlueprintAppId' }).Count -gt 0)
}

Test-Case 'ParameterJson rejects AgentIdentityId on an AgentRegistration row (parent-id bypass)' {
    $rows = @(New-Row @{ ObjectType = 'AgentRegistration'; Key = 'r1'; ParentKey = 'ai1'; DisplayName = 'R'; ParameterJson = '{"AgentIdentityId":"22222222-2222-4222-8222-222222222222"}' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'ParameterJson' -and $_.Message -match 'AgentIdentityId' }).Count -gt 0)
}

Test-Case 'ParameterJson rejects AgentIdentityId on a Blueprint row too - the blocklist is global, not type-scoped' {
    $rows = @(New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com'; ParameterJson = '{"AgentIdentityId":"x"}' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'ParameterJson' -and $_.Message -match 'AgentIdentityId' }).Count -gt 0)
}

Test-Case 'ParameterJson rejects RegistrationId on an AgentUser row (defensive bypass key)' {
    $rows = @(New-Row @{ ObjectType = 'AgentUser'; Key = 'u1'; ParentKey = 'ai1'; PrincipalName = 'u@x.com'; ParameterJson = '{"RegistrationId":"x"}' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'ParameterJson' -and $_.Message -match 'RegistrationId' }).Count -gt 0)
}

Test-Case 'ParameterJson rejects AgentUserId on an AgentRegistration row (defensive bypass key)' {
    $rows = @(New-Row @{ ObjectType = 'AgentRegistration'; Key = 'r1'; ParentKey = 'ai1'; DisplayName = 'R'; ParameterJson = '{"AgentUserId":"x"}' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'ParameterJson' -and $_.Message -match 'AgentUserId' }).Count -gt 0)
}

Test-Case 'ParameterJson rejects the Update bypass switch on every object type' {
    foreach ($values in @(
            @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' },
            @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' },
            @{ ObjectType = 'AgentUser'; Key = 'u1'; ParentKey = 'ai1'; PrincipalName = 'u@x.com' },
            @{ ObjectType = 'AgentRegistration'; Key = 'r1'; ParentKey = 'ai1'; DisplayName = 'R' }
        )) {
        $values['ParameterJson'] = '{"Update":true}'
        $rows = @(New-Row $values)
        $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
        Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'ParameterJson' -and $_.Message -match 'Update' }).Count -gt 0) "$($values.ObjectType) ParameterJson must reject 'Update'."
    }
}

Test-Case 'ParameterJson rejects leaf-script authentication overrides' {
    $cases = @(
        @{
            Values = @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' }
            Json = '{"KeyVaultAccessToken":"token"}'
            Key = 'KeyVaultAccessToken'
        },
        @{
            Values = @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' }
            Json = '{"MintWithBlueprintCredential":true,"BlueprintClientSecret":"secret"}'
            Key = 'MintWithBlueprintCredential'
        },
        @{
            Values = @{ ObjectType = 'AgentUser'; Key = 'u1'; ParentKey = 'ai1'; PrincipalName = 'u@x.com' }
            Json = '{"ClientAssertion":"assertion","UseAzureCli":true}'
            Key = 'ClientAssertion'
        },
        @{
            Values = @{ ObjectType = 'AgentUser'; Key = 'u2'; ParentKey = 'ai1'; PrincipalName = 'u2@x.com' }
            Json = '{"FederatedTokenFile":"token.txt","ManagedIdentityClientId":"client"}'
            Key = 'FederatedTokenFile'
        },
        @{
            Values = @{ ObjectType = 'AgentUser'; Key = 'u3'; ParentKey = 'ai1'; PrincipalName = 'u3@x.com' }
            Json = '{"GraphBaseUrl":"https://example.invalid","AuthorityHost":"https://example.invalid"}'
            Key = 'GraphBaseUrl'
        },
        @{
            Values = @{ ObjectType = 'AgentUser'; Key = 'u4'; ParentKey = 'ai1'; PrincipalName = 'u4@x.com' }
            Json = '{"Environment":"AzureUSGovernment"}'
            Key = 'Environment'
        }
    )

    foreach ($case in $cases) {
        $case.Values['ParameterJson'] = $case.Json
        $plan = ConvertTo-A365BulkOnboardingPlan -Rows @(New-Row $case.Values)
        Assert-True (
            @($plan.Errors | Where-Object {
                $_.Column -eq 'ParameterJson' -and $_.Message -match [regex]::Escape($case.Key)
            }).Count -gt 0
        ) "$($case.Values.ObjectType) ParameterJson must reject '$($case.Key)'."
    }
}

Test-Case 'Manager xor: both ManagerUserId and ManagerUpn is rejected' {
    $rows = @(New-Row @{ ObjectType = 'AgentUser'; Key = 'u1'; ParentKey = 'ai1'; PrincipalName = 'u@x.com'; ManagerUserId = [guid]::NewGuid().ToString(); ManagerUpn = 'm@x.com' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Message -match 'not both' }).Count -gt 0)
}

Test-Case 'AssignLicense requires UsageLocation' {
    $rows = @(New-Row @{ ObjectType = 'AgentUser'; Key = 'u1'; ParentKey = 'ai1'; PrincipalName = 'u@x.com'; AssignLicense = 'true'; LicenseSkuId = [guid]::NewGuid().ToString() })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'UsageLocation' }).Count -gt 0)
}

Test-Case 'AssignLicense requires a SKU (LicenseSkuId or LicenseSkuPartNumber), matching the orchestrator precondition' {
    $rows = @(New-Row @{ ObjectType = 'AgentUser'; Key = 'u1'; ParentKey = 'ai1'; PrincipalName = 'u@x.com'; AssignLicense = 'true'; UsageLocation = 'US' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Column -eq 'LicenseSkuId' -and $_.Message -match 'required when AssignLicense' }).Count -gt 0)
}

Test-Case 'AssignLicense with UsageLocation and a SkuPartNumber (no SkuId) is accepted' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentUser'; Key = 'u1'; ParentKey = 'ai1'; PrincipalName = 'u@x.com'; AssignLicense = 'true'; UsageLocation = 'US'; LicenseSkuPartNumber = 'Microsoft_Agent_365_Tier3' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-Count $plan.Errors 0
}

Test-Case 'AssignLicense false needs neither UsageLocation nor a SKU' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentUser'; Key = 'u1'; ParentKey = 'ai1'; PrincipalName = 'u@x.com'; AssignLicense = 'false' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-Count $plan.Errors 0
}

Test-Case 'A ParentKey that matches nothing in the file is rejected' {
    $rows = @(New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'noSuchKey'; DisplayName = 'AI'; Sponsor = 'a@x.com' })
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Message -match 'does not match any Key' }).Count -gt 0)
}

Test-Case 'A ParentKey of the wrong type is rejected' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentUser'; Key = 'u1'; ParentKey = 'bp1'; PrincipalName = 'u@x.com' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Message -match 'require a AgentIdentity parent' }).Count -gt 0)
}

Test-Case 'A leaf (AgentUser/AgentRegistration) cannot be a parent' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentUser'; Key = 'u1'; ParentKey = 'ai1'; PrincipalName = 'u@x.com' }
        New-Row @{ ObjectType = 'AgentRegistration'; Key = 'r1'; ParentKey = 'u1'; DisplayName = 'R' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Message -match 'cannot be a parent' }).Count -gt 0)
}

Test-Case 'A dependency cycle is detected and reported' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; ExistingId = [guid]::NewGuid().ToString() }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'ai2'; DisplayName = 'AI1'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai2'; ParentKey = 'ai1'; DisplayName = 'AI2'; Sponsor = 'a@x.com' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True (@($plan.Errors | Where-Object { $_.Message -match 'dependency cycle' }).Count -gt 0)
}

Test-Case 'A fully valid file produces zero errors and the expected node/anchor split' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai1'; ParentKey = 'bp1'; DisplayName = 'AI'; Sponsor = 'a@x.com' }
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp2'; ExistingId = [guid]::NewGuid().ToString() }
        New-Row @{ ObjectType = 'AgentIdentity'; Key = 'ai2'; ParentKey = 'bp2'; DisplayName = 'AI2'; Sponsor = 'a@x.com' }
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-Count $plan.Errors 0
    Assert-Count $plan.Nodes 3
    Assert-Count $plan.ExistingNodes 1
}

Test-Case 'Independent validation errors on different rows are all aggregated together, not just the first' {
    $rows = @(
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP' }                                         # row 2: missing Sponsor
        New-Row @{ ObjectType = 'Blueprint'; Key = 'bp1'; DisplayName = 'BP2'; Sponsor = 'a@x.com' }                    # row 3: duplicate key
        New-Row @{ ObjectType = 'AgentUser'; Key = 'u1'; ParentKey = 'noSuchKey'; PrincipalName = 'not-a-upn' }         # row 4: bad ParentKey + bad UPN
    )
    $plan = ConvertTo-A365BulkOnboardingPlan -Rows $rows
    Assert-True ($plan.Errors.Count -ge 4) "Expected at least 4 aggregated errors, got $($plan.Errors.Count): $(($plan.Errors | ForEach-Object { "$($_.Row)/$($_.Column): $($_.Message)" }) -join '; ')"
    Assert-True (@($plan.Errors | Where-Object { $_.Row -eq 2 -and $_.Column -eq 'Sponsor' }).Count -gt 0) 'Row 2''s missing Sponsor must be reported.'
    Assert-True (@($plan.Errors | Where-Object { $_.Row -eq 3 -and $_.Message -match 'Duplicate key' }).Count -gt 0) 'Row 3''s duplicate key must be reported.'
    Assert-True (@($plan.Errors | Where-Object { $_.Row -eq 4 -and $_.Column -eq 'ParentKey' }).Count -gt 0) 'Row 4''s unresolved ParentKey must be reported.'
    Assert-True (@($plan.Errors | Where-Object { $_.Row -eq 4 -and $_.Column -eq 'PrincipalName' }).Count -gt 0) 'Row 4''s invalid UPN must be reported alongside its ParentKey error.'
}

Get-A365TestResults
