# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    Pure parsing, validation, dependency-ordering and row-mapping helpers for
    A365-BulkOnboarding.ps1. Nothing in this module calls Microsoft Graph or the
    orchestrator - every function is deterministic on its inputs, which is what makes it
    possible to unit test the bulk-onboarding logic without a tenant.

    CSV shape
    ---------
    One row per object. ObjectType is one of Blueprint, AgentIdentity, AgentUser or
    AgentRegistration. Key uniquely names the row (case-insensitive) so other rows can
    reference it as their ParentKey. ExistingId anchors a row to an object that already
    exists (Blueprint appId or AgentIdentity objectId) instead of creating one; existing
    rows are reference-only and are never sent to the orchestrator.

    Blueprint is the root of the dependency tree; AgentIdentity rows parent to a Blueprint;
    AgentUser and AgentRegistration rows parent to an AgentIdentity and are siblings of each
    other, exactly as the orchestrator treats phases 3 and 4.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Schema
# ---------------------------------------------------------------------------

# The complete set of CSV headers this tool understands. A header outside this list is
# rejected outright so a typo in a column name fails fast instead of being silently ignored.
$script:KnownHeaders = @(
    'ObjectType', 'Key', 'ParentKey', 'ExistingId',
    'DisplayName', 'Description', 'Owner',
    'Sponsor', 'RequireOwnerAssignment', 'RequiredPermissionJson', 'GrantAdminConsent',
    'SkipInheritablePermissions', 'NewClientSecret', 'KeyVaultName', 'KeyVaultSecretName', 'ManagedIdentityPrincipalId',
    'Tag', 'CustomSecurityAttributeJson', 'SkipCustomSecurityAttributeValidation',
    'PrincipalName', 'MailNickname', 'ManagerUserId', 'ManagerUpn', 'UsageLocation',
    'AssignLicense', 'LicenseSkuId', 'LicenseSkuPartNumber',
    'OwnerId', 'Auth',
    'ParameterJson'
)

$script:ObjectTypes = @('Blueprint', 'AgentIdentity', 'AgentUser', 'AgentRegistration')

# Column kinds drive how a cell's text is turned into an orchestrator parameter value:
#   string       passed through verbatim
#   guid         validated as a GUID, then passed through as a string
#   stringarray  split on ';'
#   bool         strict true/false
#   permjson     ConvertFrom-Json -AsHashtable, must yield an array of hashtables
#   csajson      ConvertFrom-Json -AsHashtable, string array or nested hashtable (or both)
#   json         ConvertFrom-Json -AsHashtable, merged into the phase's -*Parameter hashtable
#   enum:Same,Interactive  one of a fixed, case-insensitive set
#
# One entry per (ObjectType, CSV column). Column names not listed here are illegal for that
# ObjectType and must be blank on that row.
$script:TypeSchema = [ordered]@{
    Blueprint = [ordered]@{
        Switch            = 'NewBlueprint'
        ParentType        = $null
        ParentRefParam    = $null
        AllowsExistingId  = $true
        ExistingIdKind    = 'guid'     # appId
        RequiredForCreate = @('DisplayName', 'Sponsor')
        Columns           = [ordered]@{
            DisplayName                 = @{ Param = 'BlueprintDisplayName';                 Kind = 'string' }
            Description                 = @{ Param = 'BlueprintDescription';                 Kind = 'string' }
            Owner                       = @{ Param = 'BlueprintOwner';                       Kind = 'stringarray' }
            Sponsor                     = @{ Param = 'BlueprintSponsor';                     Kind = 'stringarray' }
            RequireOwnerAssignment      = @{ Param = 'BlueprintRequireOwnerAssignment';      Kind = 'bool' }
            RequiredPermissionJson      = @{ Param = 'BlueprintRequiredPermission';          Kind = 'permjson' }
            GrantAdminConsent           = @{ Param = 'BlueprintGrantAdminConsent';           Kind = 'bool' }
            SkipInheritablePermissions  = @{ Param = 'BlueprintSkipInheritablePermissions';  Kind = 'bool' }
            NewClientSecret             = @{ Param = 'BlueprintNewClientSecret';             Kind = 'bool' }
            KeyVaultName                = @{ Param = 'BlueprintKeyVaultName';                Kind = 'string' }
            KeyVaultSecretName          = @{ Param = 'BlueprintKeyVaultSecretName';          Kind = 'string' }
            ManagedIdentityPrincipalId  = @{ Param = 'BlueprintManagedIdentityPrincipalId';  Kind = 'guid' }
            ParameterJson               = @{ Param = 'BlueprintParameter';                   Kind = 'json' }
        }
    }
    AgentIdentity = [ordered]@{
        Switch            = 'NewAgentIdentity'
        ParentType        = 'Blueprint'
        ParentRefParam    = 'UseExistingBlueprint'
        AllowsExistingId  = $true
        ExistingIdKind    = 'guid'     # objectId
        RequiredForCreate = @('DisplayName', 'Sponsor')
        Columns           = [ordered]@{
            DisplayName                            = @{ Param = 'AgentIdentityDisplayName';                            Kind = 'string' }
            Owner                                   = @{ Param = 'AgentIdentityOwner';                                   Kind = 'stringarray' }
            Sponsor                                 = @{ Param = 'AgentIdentitySponsor';                                 Kind = 'stringarray' }
            RequireOwnerAssignment                  = @{ Param = 'AgentIdentityRequireOwnerAssignment';                  Kind = 'bool' }
            Tag                                     = @{ Param = 'AgentIdentityTag';                                     Kind = 'stringarray' }
            CustomSecurityAttributeJson             = @{ Param = 'AgentIdentityCustomSecurityAttribute';                Kind = 'csajson' }
            SkipCustomSecurityAttributeValidation   = @{ Param = 'AgentIdentitySkipCustomSecurityAttributeValidation';   Kind = 'bool' }
            RequiredPermissionJson                  = @{ Param = 'AgentIdentityRequiredPermission';                     Kind = 'permjson' }
            GrantAdminConsent                       = @{ Param = 'AgentIdentityGrantAdminConsent';                      Kind = 'bool' }
            ParameterJson                            = @{ Param = 'AgentIdentityParameter';                              Kind = 'json' }
        }
    }
    AgentUser = [ordered]@{
        Switch            = 'NewAgentUser'
        ParentType        = 'AgentIdentity'
        ParentRefParam    = 'UseExistingAgentIdentity'
        AllowsExistingId  = $false
        ExistingIdKind    = $null
        RequiredForCreate = @('PrincipalName')
        Columns           = [ordered]@{
            DisplayName            = @{ Param = 'AgentUserDisplayName';           Kind = 'string' }
            PrincipalName          = @{ Param = 'AgentUserPrincipalName';         Kind = 'upn' }
            MailNickname           = @{ Param = 'AgentUserMailNickname';          Kind = 'string' }
            ManagerUserId          = @{ Param = 'AgentUserManagerUserId';         Kind = 'guid' }
            ManagerUpn             = @{ Param = 'AgentUserManagerUpn';            Kind = 'upn' }
            UsageLocation          = @{ Param = 'AgentUserUsageLocation';         Kind = 'string' }
            AssignLicense          = @{ Param = 'AgentUserAssignLicense';         Kind = 'bool' }
            LicenseSkuId           = @{ Param = 'AgentUserLicenseSkuId';          Kind = 'guid' }
            LicenseSkuPartNumber   = @{ Param = 'AgentUserLicenseSkuPartNumber'; Kind = 'string' }
            ParameterJson          = @{ Param = 'AgentUserParameter';             Kind = 'json' }
        }
    }
    AgentRegistration = [ordered]@{
        Switch            = 'NewAgentRegistration'
        ParentType        = 'AgentIdentity'
        ParentRefParam    = 'UseExistingAgentIdentity'
        AllowsExistingId  = $false
        ExistingIdKind    = $null
        # New-A365AgentRegistration.ps1 requires -DisplayName; the orchestrator's own fallback
        # to the identity's display name only applies when -NewAgentIdentity runs in the SAME
        # call, which bulk onboarding never does (each row is its own orchestrator call).
        RequiredForCreate = @('DisplayName')
        Columns           = [ordered]@{
            DisplayName   = @{ Param = 'AgentRegistrationDisplayName'; Kind = 'string' }
            Description   = @{ Param = 'AgentRegistrationDescription'; Kind = 'string' }
            Owner         = @{ Param = 'AgentRegistrationOwner';       Kind = 'stringarray' }
            OwnerId       = @{ Param = 'AgentRegistrationOwnerId';     Kind = 'guid' }
            Auth          = @{ Param = 'AgentRegistrationAuth';       Kind = 'enum:Same,Interactive' }
            ParameterJson = @{ Param = 'AgentRegistrationParameter'; Kind = 'json' }
        }
    }
}

# Parameters that -*ParameterJson must never carry: the tenant, every authentication and
# credential input, the parent-id/anchor-id parameters this tool (and the orchestrator's own
# update paths) resolve and supply automatically, every create/update/removal switch,
# run/output/logging settings, common risk parameters, and (per object type) whatever the
# row's own explicit CSV columns already control.
$script:GlobalBlockedParameterJsonKeys = @(
    'TenantId', 'ScriptRoot', 'OutputJsonPath', 'IncludeBlueprintSecretsInOutput',
    'ClientId', 'ClientSecret', 'CertificateThumbprint', 'Certificate', 'CertificatePath',
    'CertificatePassword', 'UseManagedIdentity', 'AccessToken', 'Interactive', 'SkipPermissionCheck',
    'MintWithBlueprintCredential', 'BlueprintClientSecret', 'KeyVaultAccessToken',
    'ManagedIdentityClientId', 'FederatedTokenFile', 'ClientAssertion', 'UseAzureCli',
    'UseAzPowerShell', 'Environment', 'GraphBaseUrl', 'AuthorityHost', 'CertificateStoreLocation',
    'LogPath', 'LogIncludeSecrets', 'LogCorrelationId',
    'NewBlueprint', 'NewAgentIdentity', 'NewAgentRegistration', 'NewAgentUser',
    'UpdateBlueprint', 'UpdateAgentIdentity', 'UpdateAgentUser', 'UpdateAgentRegistration', 'Update',
    'RemoveBlueprint', 'RemoveAgentIdentity', 'RemoveAgentUser', 'RemoveAgentRegistration',
    'RemovePermanent', 'RemoveInspectOnly', 'RemoveForce',
    'UseExistingBlueprint', 'UseExistingAgentIdentity', 'AgentRegistrationIdentityId',
    'BlueprintAppId', 'AgentIdentityId', 'AgentUserId', 'RegistrationId',
    'WhatIf', 'Confirm', 'ErrorAction', 'ErrorVariable', 'WarningAction', 'WarningVariable',
    'InformationAction', 'InformationVariable', 'OutVariable', 'OutBuffer', 'PipelineVariable',
    'Verbose', 'Debug'
)

# ParameterJson is deliberately an allowlist, not an open splat. PowerShell accepts
# unambiguous parameter-name prefixes, so merely blocking exact sensitive names would still
# allow values such as "ClientSec" or "GraphBaseUr" to bind to credential/endpoint parameters.
$script:AllowedParameterJsonKeysByType = @{
    Blueprint         = @('ClientSecretLifetimeDays', 'ExposedScopeValue', 'FederatedCredentialName')
    AgentIdentity     = @('Disabled')
    AgentUser         = @('DisabledPlans', 'MaxRetries', 'RetryDelaySeconds', 'NoDefaultOwner', 'NoOwnershipSelfHeal')
    AgentRegistration = @('ManagedByAppId', 'RolePropagationDelaySeconds', 'SkipDisplayNameNormalization')
}

# ---------------------------------------------------------------------------
# Small pure helpers - each independently unit testable
# ---------------------------------------------------------------------------

function Get-A365BulkOnboardingKnownHeaders {
    <#
    .SYNOPSIS
        Returns the full set of CSV headers A365-BulkOnboarding.ps1 recognises.
    #>
    [CmdletBinding()]
    param()
    # The unary comma keeps this an array through the pipeline; without it PowerShell
    # unwraps a single-element (or empty) array return into a bare scalar / $null.
    return , @($script:KnownHeaders)
}

function Get-A365BulkOnboardingTypeSchema {
    <#
    .SYNOPSIS
        Returns the column/parameter mapping schema for one or all object types.

    .PARAMETER ObjectType
        The object type to return, or omit it to return every type schema.
    #>
    [CmdletBinding()]
    param([ValidateSet('Blueprint', 'AgentIdentity', 'AgentUser', 'AgentRegistration')] [string] $ObjectType)
    if ($ObjectType) { return $script:TypeSchema[$ObjectType] }
    return $script:TypeSchema
}

function Test-A365BulkOnboardingGuid {
    <#
    .SYNOPSIS
        True when Value parses as a GUID. Blank is not a valid GUID - callers check
        required-ness separately.

    .PARAMETER Value
        The CSV value to validate as a GUID.
    #>
    [CmdletBinding()]
    param([string] $Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    $parsed = [guid]::Empty
    return [guid]::TryParse($Value, [ref] $parsed)
}

function Test-A365BulkOnboardingUpn {
    <#
    .SYNOPSIS
        True when Value has the shape of a user principal name. Mirrors the check
        New-A365AgentUser.ps1 applies to -UserPrincipalName.

    .PARAMETER Value
        The CSV value to validate as a user principal name.
    #>
    [CmdletBinding()]
    param([string] $Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    return $Value -match '^[^@\s]+@[^@\s]+\.[^@\s]+$'
}

function ConvertTo-A365BulkOnboardingBool {
    <#
    .SYNOPSIS
        Strictly parses a CSV cell as a boolean. Blank means "not specified" (false, and
        $IsPresent is $false); anything other than true/false (case-insensitive) is an error.

    .PARAMETER Value
        The CSV value to parse as true, false, or blank.
    #>
    [CmdletBinding()]
    param([string] $Value)
    $trimmed = if ($null -eq $Value) { '' } else { $Value.Trim() }
    if ($trimmed -eq '') { return [pscustomobject]@{ IsValid = $true; Value = $false; IsPresent = $false; ErrorMessage = $null } }
    if ($trimmed -eq 'true')  { return [pscustomobject]@{ IsValid = $true; Value = $true;  IsPresent = $true;  ErrorMessage = $null } }
    if ($trimmed -eq 'false') { return [pscustomobject]@{ IsValid = $true; Value = $false; IsPresent = $true;  ErrorMessage = $null } }
    return [pscustomobject]@{
        IsValid = $false; Value = $false; IsPresent = $false
        ErrorMessage = "must be 'true', 'false', or blank; found '$Value'."
    }
}

function ConvertTo-A365BulkOnboardingArray {
    <#
    .SYNOPSIS
        Splits a semicolon-separated CSV cell into a trimmed, non-empty string array.

    .PARAMETER Value
        The semicolon-separated CSV value to split.
    #>
    [CmdletBinding()]
    param([string] $Value)
    # The unary comma keeps this an array through the pipeline; without it PowerShell
    # unwraps a single-element (or empty) array return into a bare scalar / $null.
    if ([string]::IsNullOrWhiteSpace($Value)) { return , @() }
    return , @($Value -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
}

function ConvertFrom-A365BulkOnboardingJson {
    <#
    .SYNOPSIS
        Parses a JSON CSV cell with ConvertFrom-Json -AsHashtable, reporting failure instead
        of throwing, so the caller can add a row/column-scoped validation error.

    .PARAMETER Value
        The JSON CSV value to parse, or blank for no value.
    #>
    [CmdletBinding()]
    param([string] $Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return [pscustomobject]@{ IsValid = $true; Value = $null; ErrorMessage = $null }
    }
    try {
        $parsed = ConvertFrom-Json -InputObject $Value -AsHashtable -ErrorAction Stop
        return [pscustomobject]@{ IsValid = $true; Value = $parsed; ErrorMessage = $null }
    }
    catch {
        return [pscustomobject]@{ IsValid = $false; Value = $null; ErrorMessage = "is not valid JSON: $($_.Exception.Message)" }
    }
}

function Test-A365BulkOnboardingParameterJsonKeys {
    <#
    .SYNOPSIS
        Returns the keys of Hashtable (case-insensitive) that collide with BlockedKeys.

    .PARAMETER Hashtable
        The parsed -*ParameterJson value for one row/column.

    .PARAMETER BlockedKeys
        The names it must not contain: $script:GlobalBlockedParameterJsonKeys plus every
        -*Param name the row's own explicit CSV columns already control.
    #>
    [CmdletBinding()]
    param([hashtable] $Hashtable, [string[]] $BlockedKeys)
    # The unary comma keeps this an array through the pipeline; without it PowerShell
    # unwraps a single-element (or empty) array return into a bare scalar / $null.
    if (-not $Hashtable) { return , @() }
    $blockedLower = @($BlockedKeys | ForEach-Object { $_.ToLowerInvariant() })
    return , @($Hashtable.Keys | Where-Object { $blockedLower -contains $_.ToString().ToLowerInvariant() })
}

# ---------------------------------------------------------------------------
# CSV import
# ---------------------------------------------------------------------------

function Import-A365BulkOnboardingCsv {
    <#
    .SYNOPSIS
        Reads the bulk onboarding CSV from disk. Only I/O-level problems (missing or
        genuinely empty file, wrong extension) throw; everything else becomes a validation
        error from ConvertTo-A365BulkOnboardingPlan so the whole file can be checked at once.

    .PARAMETER Path
        The path to the bulk onboarding CSV file.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateNotNullOrWhiteSpace()][string] $Path)

    # -LiteralPath below already takes the string exactly as given (no wildcard expansion),
    # so the remaining file-path risk is a caller pointing this at something that is not a
    # CSV at all; reject that before any content is read.
    if ([IO.Path]::GetExtension($Path) -notlike '.csv') {
        throw "CSV file '$Path' must have a .csv extension."
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "CSV file '$Path' was not found."
    }
    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    $text = Get-Content -LiteralPath $resolved -Raw -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "CSV file '$resolved' is empty."
    }
    # The unary comma keeps this an array through the pipeline; without it PowerShell
    # unwraps a single-data-row CSV into a bare scalar PSCustomObject instead of an array.
    return , @(Import-Csv -LiteralPath $resolved)
}

# ---------------------------------------------------------------------------
# Validation + dependency ordering
# ---------------------------------------------------------------------------

function New-A365BulkOnboardingValidationError {
    param([int] $Row, [string] $Column, [string] $Message)
    [pscustomobject]@{ Row = $Row; Column = $Column; Message = $Message }
}

function ConvertTo-A365BulkOnboardingPlan {
    <#
    .SYNOPSIS
        Validates every row of a bulk onboarding CSV and produces an execution plan:
        create rows in dependency order (parents before children, original CSV order
        preserved among independent rows), existing rows to seed the state map with, and
        every validation error found - the whole file is checked before any row is treated
        as runnable.

    .PARAMETER Rows
        Raw rows as returned by Import-A365BulkOnboardingCsv (or any array of PSCustomObject/
        hashtable with the same column names - this makes the function easy to unit test
        without touching disk).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Rows)

    $errors = [System.Collections.Generic.List[object]]::new()

    if (@($Rows).Count -eq 0) {
        $errors.Add((New-A365BulkOnboardingValidationError -Row 0 -Column '' -Message 'The CSV file has a header row but no data rows.'))
        return [pscustomobject]@{ Errors = @($errors); Nodes = @(); ExistingNodes = @(); Keys = @{} }
    }

    # Header validation. Import-Csv exposes headers as the properties of row 0; every row is
    # assumed to share the same header set, which Import-Csv itself guarantees.
    $headers = @($Rows[0].PSObject.Properties.Name)
    $unknownHeaders = @($headers | Where-Object { $script:KnownHeaders -notcontains $_ })
    foreach ($h in $unknownHeaders) {
        $errors.Add((New-A365BulkOnboardingValidationError -Row 0 -Column $h -Message "Unknown header '$h'. Remove it or fix the spelling."))
    }
    foreach ($required in @('ObjectType', 'Key')) {
        if ($headers -notcontains $required) {
            $errors.Add((New-A365BulkOnboardingValidationError -Row 0 -Column $required -Message "Required header '$required' is missing."))
        }
    }
    if ($errors.Count -gt 0) {
        # A header problem makes every row unreliable to interpret further (a mistyped
        # header silently reads as blank) - stop before per-row validation compounds it.
        return [pscustomobject]@{ Errors = @($errors); Nodes = @(); ExistingNodes = @(); Keys = @{} }
    }

    # ------------------------------------------------------------------
    # Pass 1: per-row structural + field validation. Independent of every other row except
    # for the duplicate-key check, so every row is fully evaluated even when an earlier one
    # is invalid.
    # ------------------------------------------------------------------

    $keySeen = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $parsedRows = [System.Collections.Generic.List[object]]::new()

    $rowNumber = 1
    foreach ($raw in $Rows) {
        $rowNumber++   # row 1 is the header; the first data row is CSV row 2
        $get = { param($name) if ($raw.PSObject.Properties.Name -contains $name) { [string]$raw.$name } else { '' } }

        $objectTypeRaw = (& $get 'ObjectType').Trim()
        $key           = (& $get 'Key').Trim()
        $parentKey     = (& $get 'ParentKey').Trim()
        $existingId    = (& $get 'ExistingId').Trim()

        if ($key -eq '') {
            $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column 'Key' -Message 'Key is required.'))
            continue
        }
        if ($keySeen.ContainsKey($key)) {
            $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column 'Key' -Message "Duplicate key '$key' (first seen on row $($keySeen[$key])). Keys are case-insensitive and must be unique."))
            continue
        }
        $keySeen[$key] = $rowNumber

        $objectType = @($script:ObjectTypes | Where-Object { $_ -eq $objectTypeRaw }) | Select-Object -First 1
        if (-not $objectType) {
            $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column 'ObjectType' -Message "ObjectType must be one of $($script:ObjectTypes -join ', '); found '$objectTypeRaw'."))
            continue
        }

        $schema = $script:TypeSchema[$objectType]

        # ExistingId legality.
        if ($existingId -ne '' -and -not $schema.AllowsExistingId) {
            $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column 'ExistingId' -Message "ExistingId is not valid for $objectType rows. Only Blueprint and AgentIdentity rows may reference an existing object."))
            $existingId = ''
        }
        if ($existingId -ne '' -and -not (Test-A365BulkOnboardingGuid -Value $existingId)) {
            $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column 'ExistingId' -Message "ExistingId '$existingId' is not a valid GUID."))
        }
        $isExisting = ($existingId -ne '')

        # ParentKey legality: root types take none, dependent types require one; existing
        # rows are anchors, not creates, and so take none either.
        if ($schema.ParentType) {
            if ($isExisting -and $parentKey -ne '') {
                $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column 'ParentKey' -Message 'ParentKey must be blank on a row that references an ExistingId; it is not being created.'))
            }
            elseif (-not $isExisting -and $parentKey -eq '') {
                $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column 'ParentKey' -Message "$objectType rows must set ParentKey to the Key of their parent $($schema.ParentType) row."))
            }
        }
        elseif ($parentKey -ne '') {
            $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column 'ParentKey' -Message "$objectType is the root of the dependency tree and cannot have a ParentKey."))
        }

        # Column legality + per-column parsing for this ObjectType. Columns not defined for
        # this type must be blank; the reverse (columns defined but blank) is fine.
        $paramValues = [ordered]@{}
        foreach ($columnName in ($script:KnownHeaders | Where-Object { $_ -notin @('ObjectType', 'Key', 'ParentKey', 'ExistingId') })) {
            $cell = (& $get $columnName).Trim()
            $columnDef = $schema.Columns[$columnName]
            if (-not $columnDef) {
                if ($cell -ne '') {
                    $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $columnName -Message "Column '$columnName' does not apply to $objectType rows and must be blank."))
                }
                continue
            }
            if ($isExisting -and $cell -ne '') {
                $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $columnName -Message "Column '$columnName' must be blank on a row that references an ExistingId; it is not being created."))
                continue
            }
            if ($isExisting) { continue }
            if ($cell -eq '') { $paramValues[$columnName] = $null; continue }

            switch -Regex ($columnDef.Kind) {
                '^string$' { $paramValues[$columnName] = $cell }
                '^guid$' {
                    if (-not (Test-A365BulkOnboardingGuid -Value $cell)) {
                        $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $columnName -Message "'$cell' is not a valid GUID."))
                    }
                    $paramValues[$columnName] = $cell
                }
                '^upn$' {
                    if (-not (Test-A365BulkOnboardingUpn -Value $cell)) {
                        $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $columnName -Message "'$cell' is not a valid user principal name."))
                    }
                    $paramValues[$columnName] = $cell
                }
                '^stringarray$' { $paramValues[$columnName] = ConvertTo-A365BulkOnboardingArray -Value $cell }
                '^bool$' {
                    $b = ConvertTo-A365BulkOnboardingBool -Value $cell
                    if (-not $b.IsValid) { $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $columnName -Message $b.ErrorMessage)) }
                    $paramValues[$columnName] = $b.Value
                }
                '^enum:' {
                    $allowed = @(($columnDef.Kind -replace '^enum:', '') -split ',')
                    $match = @($allowed | Where-Object { $_ -eq $cell }) | Select-Object -First 1
                    if (-not $match) {
                        $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $columnName -Message "must be one of $($allowed -join ', '); found '$cell'."))
                    }
                    $paramValues[$columnName] = if ($match) { $match } else { $cell }
                }
                '^permjson$' {
                    $j = ConvertFrom-A365BulkOnboardingJson -Value $cell
                    if (-not $j.IsValid) {
                        $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $columnName -Message $j.ErrorMessage))
                    }
                    elseif ($j.Value -isnot [System.Collections.IEnumerable] -or $j.Value -is [string] -or $j.Value -is [hashtable]) {
                        $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $columnName -Message 'must be a JSON array of permission objects, e.g. [{"ResourceAppId":"...","DelegatedScopes":["..."]}].'))
                    }
                    else {
                        $paramValues[$columnName] = @($j.Value)
                    }
                }
                '^csajson$' {
                    $j = ConvertFrom-A365BulkOnboardingJson -Value $cell
                    if (-not $j.IsValid) {
                        $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $columnName -Message $j.ErrorMessage))
                    }
                    elseif ($j.Value -is [hashtable]) {
                        $paramValues[$columnName] = @($j.Value)
                    }
                    elseif ($j.Value -is [System.Collections.IEnumerable] -and $j.Value -isnot [string]) {
                        $paramValues[$columnName] = @($j.Value)
                    }
                    else {
                        $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $columnName -Message 'must be a JSON array of "Set:Attribute:Value" strings, or a JSON object of attribute set -> attribute -> value.'))
                    }
                }
                '^json$' {
                    $j = ConvertFrom-A365BulkOnboardingJson -Value $cell
                    if (-not $j.IsValid) {
                        $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $columnName -Message $j.ErrorMessage))
                    }
                    elseif ($j.Value -isnot [hashtable]) {
                        $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $columnName -Message 'must be a JSON object, e.g. {"Key":"Value"}.'))
                    }
                    else {
                        $allowed = @($script:AllowedParameterJsonKeysByType[$objectType])
                        $unsupported = @($j.Value.Keys | Where-Object { $allowed -notcontains [string]$_ })
                        if ($unsupported.Count -gt 0) {
                            $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $columnName -Message (
                                "contains unsupported or abbreviated parameter name(s): $($unsupported -join ', '). " +
                                "Supported parameters for $objectType are: $($allowed -join ', ').")))
                        }
                        $blocked = @($script:GlobalBlockedParameterJsonKeys) + @($schema.Columns.Keys | ForEach-Object { $schema.Columns[$_].Param })
                        $collisions = Test-A365BulkOnboardingParameterJsonKeys -Hashtable $j.Value -BlockedKeys $blocked
                        if ($collisions.Count -gt 0) {
                            $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $columnName -Message "must not set $($collisions -join ', '); each is already controlled by an explicit CSV column, authentication, or run setting."))
                        }
                        $paramValues[$columnName] = $j.Value
                    }
                }
            }
        }

        # Required-for-create fields.
        if (-not $isExisting) {
            foreach ($req in $schema.RequiredForCreate) {
                $val = $paramValues[$req]
                $missing = ($null -eq $val) -or ($val -is [string] -and $val -eq '') -or ($val -is [array] -and $val.Count -eq 0)
                if ($missing) {
                    $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column $req -Message "$req is required to create a $objectType."))
                }
            }
            if ($objectType -eq 'AgentUser') {
                $hasManagerId  = ($paramValues['ManagerUserId'] -and $paramValues['ManagerUserId'] -ne '')
                $hasManagerUpn = ($paramValues['ManagerUpn'] -and $paramValues['ManagerUpn'] -ne '')
                if ($hasManagerId -and $hasManagerUpn) {
                    $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column 'ManagerUserId' -Message 'Specify ManagerUserId or ManagerUpn, not both.'))
                }
                # AssignLicense needs a usage location: New-A365AgentUser.ps1 requires one on the
                # create path and cannot assign a licence without it.
                if ($paramValues['AssignLicense'] -eq $true -and [string]::IsNullOrWhiteSpace([string]$paramValues['UsageLocation'])) {
                    $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column 'UsageLocation' -Message 'UsageLocation is required when AssignLicense is true.'))
                }
                # The orchestrator's own -AgentUserLicenseSkuId carries no default (unlike
                # New-A365AgentUser.ps1's), so a SKU column is mandatory here even though the
                # step script would otherwise fall back to the Agent 365 Tier 3 SKU.
                $hasSkuId          = -not [string]::IsNullOrWhiteSpace([string]$paramValues['LicenseSkuId'])
                $hasSkuPartNumber  = -not [string]::IsNullOrWhiteSpace([string]$paramValues['LicenseSkuPartNumber'])
                if ($paramValues['AssignLicense'] -eq $true -and -not $hasSkuId -and -not $hasSkuPartNumber) {
                    $errors.Add((New-A365BulkOnboardingValidationError -Row $rowNumber -Column 'LicenseSkuId' -Message 'LicenseSkuId or LicenseSkuPartNumber is required when AssignLicense is true.'))
                }
            }
        }

        $parsedRows.Add([pscustomobject]@{
            RowNumber   = $rowNumber
            Key         = $key
            ObjectType  = $objectType
            ParentKey   = $parentKey
            ExistingId  = $existingId
            IsExisting  = $isExisting
            ParamValues = $paramValues
        })
    }

    # ------------------------------------------------------------------
    # Pass 2: cross-row checks that need the full key set - parent references, parent/type
    # legality, "a leaf cannot parent", and cycles.
    # ------------------------------------------------------------------

    $byKey = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($r in $parsedRows) { $byKey[$r.Key] = $r }

    foreach ($r in $parsedRows) {
        if ($r.ParentKey -eq '') { continue }
        if (-not $byKey.ContainsKey($r.ParentKey)) {
            $errors.Add((New-A365BulkOnboardingValidationError -Row $r.RowNumber -Column 'ParentKey' -Message "ParentKey '$($r.ParentKey)' does not match any Key in the file."))
            continue
        }
        $parent = $byKey[$r.ParentKey]
        $expectedParentType = $script:TypeSchema[$r.ObjectType].ParentType
        if ($parent.ObjectType -ne $expectedParentType) {
            $errors.Add((New-A365BulkOnboardingValidationError -Row $r.RowNumber -Column 'ParentKey' -Message "ParentKey '$($r.ParentKey)' is a $($parent.ObjectType) row; $($r.ObjectType) rows require a $expectedParentType parent."))
        }
    }

    # "A leaf cannot parent": nothing may reference an AgentUser or AgentRegistration row.
    $leafKeys = @($parsedRows | Where-Object { $_.ObjectType -in @('AgentUser', 'AgentRegistration') } | ForEach-Object { $_.Key })
    foreach ($r in $parsedRows) {
        if ($r.ParentKey -ne '' -and $leafKeys -contains $r.ParentKey) {
            $errors.Add((New-A365BulkOnboardingValidationError -Row $r.RowNumber -Column 'ParentKey' -Message "ParentKey '$($r.ParentKey)' names an AgentUser or AgentRegistration row, which cannot be a parent."))
        }
    }

    # ------------------------------------------------------------------
    # Pass 3: dependency ordering. Only create rows participate; existing rows are
    # already-resolved anchors. Kahn's algorithm, always advancing the lowest original row
    # number among currently-ready rows, so independent rows keep their CSV order.
    # ------------------------------------------------------------------

    $createRows  = @($parsedRows | Where-Object { -not $_.IsExisting })
    $existingRows = @($parsedRows | Where-Object { $_.IsExisting })

    $indegree  = @{}
    $children  = @{}
    foreach ($r in $createRows) {
        $indegree[$r.Key] = 0
        $children[$r.Key] = [System.Collections.Generic.List[string]]::new()
    }
    foreach ($r in $createRows) {
        if ($r.ParentKey -eq '' -or -not $byKey.ContainsKey($r.ParentKey)) { continue }
        $parent = $byKey[$r.ParentKey]
        if ($parent.IsExisting) { continue }   # already resolved; does not gate ordering
        $indegree[$r.Key]++
        $children[$parent.Key].Add($r.Key)
    }

    $remaining = [System.Collections.Generic.List[object]]::new()
    foreach ($r in $createRows) { $remaining.Add($r) }
    $ordered = [System.Collections.Generic.List[object]]::new()

    while ($remaining.Count -gt 0) {
        $ready = @($remaining | Where-Object { $indegree[$_.Key] -eq 0 } | Sort-Object RowNumber)
        if ($ready.Count -eq 0) {
            $stuck = @($remaining | Sort-Object RowNumber | ForEach-Object { $_.Key })
            $errors.Add((New-A365BulkOnboardingValidationError -Row 0 -Column 'ParentKey' -Message "A dependency cycle involves: $($stuck -join ', ')."))
            break
        }
        $next = $ready[0]
        $ordered.Add($next)
        [void]$remaining.Remove($next)
        foreach ($childKey in $children[$next.Key]) { $indegree[$childKey]-- }
    }

    $nodes = @($ordered | ForEach-Object {
        $schema = $script:TypeSchema[$_.ObjectType]
        [pscustomobject]@{
            RowNumber      = $_.RowNumber
            Key            = $_.Key
            ObjectType     = $_.ObjectType
            ParentKey      = $_.ParentKey
            Switch         = $schema.Switch
            ParentRefParam = $schema.ParentRefParam
            ParamValues    = $_.ParamValues
        }
    })

    $existingNodes = @($existingRows | ForEach-Object {
        [pscustomobject]@{
            RowNumber  = $_.RowNumber
            Key        = $_.Key
            ObjectType = $_.ObjectType
            ExistingId = $_.ExistingId
        }
    })

    return [pscustomobject]@{
        Errors        = @($errors)
        Nodes         = $nodes
        ExistingNodes = $existingNodes
        Keys          = $byKey
    }
}

# ---------------------------------------------------------------------------
# Row -> orchestrator argument mapping
# ---------------------------------------------------------------------------

function New-A365BulkOnboardingOrchestratorArguments {
    <#
    .SYNOPSIS
        Builds the hashtable to splat at A365-AutomationOrchestrator.ps1 for one create
        node, given the resolved id of its parent (or $null for a Blueprint / root row).

    .PARAMETER Node
        One entry from $Plan.Nodes (ConvertTo-A365BulkOnboardingPlan): a create row with
        its ObjectType, Switch, ParentRefParam and parsed ParamValues.

    .PARAMETER TenantId
        Forwarded as -TenantId when supplied; omitted entirely from the splat otherwise.

    .PARAMETER ParentResolvedId
        The live id this node's parent already resolved to, or $null for a root
        (ParentKey-less) node. Required (and must be non-blank) whenever the node's schema
        defines a ParentRefParam - the caller is expected to have skipped a node whose
        parent did not resolve, rather than call this with a blank id for it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Node,
        [string] $TenantId,
        [string] $ParentResolvedId
    )

    $schema = $script:TypeSchema[$Node.ObjectType]
    $orchestratorArgs = [ordered]@{}
    if ($TenantId) { $orchestratorArgs['TenantId'] = $TenantId }
    $orchestratorArgs[$Node.Switch] = $true

    if ($schema.ParentRefParam) {
        if ([string]::IsNullOrWhiteSpace($ParentResolvedId)) {
            throw "Row '$($Node.Key)' ($($Node.ObjectType)) has no resolved parent id for '$($Node.ParentKey)'. This indicates the parent row did not complete successfully and the row should have been skipped instead of executed."
        }
        $orchestratorArgs[$schema.ParentRefParam] = $ParentResolvedId
    }

    foreach ($columnName in $schema.Columns.Keys) {
        $paramName = $schema.Columns[$columnName].Param
        $value = $Node.ParamValues[$columnName]
        if ($null -eq $value) { continue }
        if ($value -is [array] -and $value.Count -eq 0) { continue }
        if ($value -is [bool] -and $value -eq $false) { continue }
        # -*Parameter is a hashtable merged verbatim; forward it even if an explicit {} (an
        # empty-but-present hashtable) was not skipped above - it is meaningful on its own:
        # "no extra parameters".
        $orchestratorArgs[$paramName] = $value
    }

    return [hashtable]$orchestratorArgs
}

# ---------------------------------------------------------------------------
# State map
# ---------------------------------------------------------------------------

function New-A365BulkOnboardingStateMap {
    <#
    .SYNOPSIS
        Builds the case-insensitive Key -> row-state map used to drive execution: existing
        rows are seeded as already resolved, create rows start Pending.

    .PARAMETER Plan
        The object returned by ConvertTo-A365BulkOnboardingPlan (its Nodes and
        ExistingNodes are read; Errors is not consulted here - the caller is expected to
        have already stopped on a plan with validation errors).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Plan)

    $map = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($n in $Plan.ExistingNodes) {
        $map[$n.Key] = [pscustomobject]@{
            Key         = $n.Key
            ObjectType  = $n.ObjectType
            Status      = 'Existing'
            ResolvedId  = $n.ExistingId
            Error       = $null
            ChildResult = $null
        }
    }
    foreach ($n in $Plan.Nodes) {
        $map[$n.Key] = [pscustomobject]@{
            Key         = $n.Key
            ObjectType  = $n.ObjectType
            Status      = 'Pending'
            ResolvedId  = $null
            Error       = $null
            ChildResult = $null
        }
    }
    return $map
}

function Test-A365BulkOnboardingRowReady {
    <#
    .SYNOPSIS
        True when a node's parent (if any) is in a state that lets the node proceed:
        resolved (Existing / Succeeded) rather than Pending, Failed or SkippedDependency.

    .PARAMETER Node
        The candidate node (from $Plan.Nodes) to check.

    .PARAMETER StateMap
        The case-insensitive Key -> row-state map from New-A365BulkOnboardingStateMap,
        updated as earlier rows in the run complete.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Node, [Parameter(Mandatory)] $StateMap)
    if (-not $Node.ParentKey) { return $true }
    if (-not $StateMap.ContainsKey($Node.ParentKey)) { return $false }
    return $StateMap[$Node.ParentKey].Status -in @('Existing', 'Succeeded')
}

# ---------------------------------------------------------------------------
# Child-result interpretation
# ---------------------------------------------------------------------------

# Which identifier in $result.summary.identifiers proves this ObjectType's row actually
# produced (or already had) a live object.
$script:ResolvedIdKeyByType = @{
    Blueprint         = 'blueprintAppId'
    AgentIdentity     = 'agentIdentityId'
    AgentUser         = 'agentUserId'
    AgentRegistration = 'registrationId'
}

function Get-A365BulkOnboardingMember {
    <#
    .SYNOPSIS
        Safely reads one member from a value that may be a PSCustomObject, an IDictionary
        (Hashtable / [ordered]@{}), or $null, and reports whether it was present.

    .DESCRIPTION
        A365-AutomationOrchestrator.ps1's own [pscustomobject]$result cast at the end of the
        script is shallow: $result.summary, $result.summary.identifiers and each entry of
        $result.summary.failedSteps stay [ordered]@{} (an IDictionary), never becoming
        PSCustomObjects themselves. Get-Member never reports an IDictionary's keys - only
        its fixed .NET members (Keys, Values, Add, ...) - so code that used Get-Member to
        test whether a key was present before reading it silently treated every real
        dictionary value as absent. This is the one place that distinction is handled so
        every caller (Resolve-A365BulkOnboardingRowOutcome and any future one) gets correct,
        identical behaviour regardless of which shape it receives - a PSCustomObject (as
        every existing unit test double happens to build), or the raw dictionaries the real
        orchestrator actually returns.

    .PARAMETER InputObject
        The value to read from. May be $null.

    .PARAMETER Name
        The member/key name to look up (case-insensitive for dictionaries; PowerShell's own
        Get-Member lookup is already case-insensitive for PSCustomObject properties).

    .OUTPUTS
        [pscustomobject] with Found (bool) and Value.
    #>
    [CmdletBinding()]
    param([Parameter()] $InputObject, [Parameter(Mandatory)][string] $Name)

    if ($null -eq $InputObject) {
        return [pscustomobject]@{ Found = $false; Value = $null }
    }
    if ($InputObject -is [System.Collections.IDictionary]) {
        foreach ($key in $InputObject.Keys) {
            if ($key -is [string] -and $key.Equals($Name, [System.StringComparison]::OrdinalIgnoreCase)) {
                return [pscustomobject]@{ Found = $true; Value = $InputObject[$key] }
            }
        }
        return [pscustomobject]@{ Found = $false; Value = $null }
    }
    if (Get-Member -InputObject $InputObject -Name $Name -ErrorAction SilentlyContinue) {
        return [pscustomobject]@{ Found = $true; Value = $InputObject.$Name }
    }
    return [pscustomobject]@{ Found = $false; Value = $null }
}

function Resolve-A365BulkOnboardingRowOutcome {
    <#
    .SYNOPSIS
        Interprets the object A365-AutomationOrchestrator.ps1 returns for a single-scenario
        call and decides whether the row succeeded. Blueprint and AgentIdentity phases throw
        on failure (caught by the caller before this function runs); AgentUser and
        AgentRegistration report failure through summary.outcome/failedSteps instead, so both
        shapes are handled here identically by object type.

    .PARAMETER ObjectType
        The row's object type; selects which identifier proves success.

    .PARAMETER ChildResult
        The [pscustomobject] returned by the orchestrator (its $result / [pscustomobject]$result
        pipeline output), or an equivalent shape from a test double. summary, identifiers and
        each failedSteps entry may themselves be PSCustomObjects or raw IDictionary values -
        see Get-A365BulkOnboardingMember.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Blueprint', 'AgentIdentity', 'AgentUser', 'AgentRegistration')] [string] $ObjectType,
        [Parameter(Mandatory)] $ChildResult
    )

    $idKey = $script:ResolvedIdKeyByType[$ObjectType]
    $summary = (Get-A365BulkOnboardingMember -InputObject $ChildResult -Name 'summary').Value
    $identifiers = (Get-A365BulkOnboardingMember -InputObject $summary -Name 'identifiers').Value

    $idLookup = Get-A365BulkOnboardingMember -InputObject $identifiers -Name $idKey
    $resolvedId = if ($idLookup.Found) { $idLookup.Value } else { $null }

    if ($resolvedId) {
        return [pscustomobject]@{ Success = $true; ResolvedId = [string]$resolvedId; Error = $null }
    }

    $detail = $null
    $failedStepsLookup = Get-A365BulkOnboardingMember -InputObject $summary -Name 'failedSteps'
    if ($failedStepsLookup.Found -and @($failedStepsLookup.Value).Count -gt 0) {
        $detail = (@($failedStepsLookup.Value) | ForEach-Object {
                $step = (Get-A365BulkOnboardingMember -InputObject $_ -Name 'step').Value
                $stepDetail = (Get-A365BulkOnboardingMember -InputObject $_ -Name 'detail').Value
                "$step`: $stepDetail"
            }) -join '; '
    }
    if (-not $detail) { $detail = 'The orchestrator did not report a resolved identifier for this row.' }
    return [pscustomobject]@{ Success = $false; ResolvedId = $null; Error = $detail }
}

# ---------------------------------------------------------------------------
# Report redaction
# ---------------------------------------------------------------------------

# Two mechanisms, mirroring the orchestrator's own ConvertTo-ReportValue / Write-RunReport
# split (A365-AutomationOrchestrator.ps1's $script:SensitiveParameterNames vs its narrow
# clientSecret/secretText-only file gate): these keys are credentials, never something this
# run created, so -IncludeSecrets must never unlock them.
# -eq/-contains are case-insensitive by default in PowerShell, so one casing per name covers
# every variant the orchestrator or a step script might emit (clientSecret, ClientSecret, ...).
$script:AlwaysRedactedReportKeys = @(
    'CertificatePassword', 'AccessToken', 'Secret', 'Password', 'privateKey', 'proof',
    'assertion', 'client_assertion', 'pwd', 'accessToken', 'access_token', 'refreshToken',
    'refresh_token', 'idToken', 'id_token', 'Authorization'
)

# The blueprint client secret this run created - the only thing -IncludeSecrets unlocks.
$script:ConditionallyRedactedReportKeys = @('clientSecret', 'secretText', 'client_secret')

function ConvertTo-A365BulkOnboardingRedactedValue {
    <#
    .SYNOPSIS
        Recursively redacts secret-shaped values out of an object bound for the JSON
        report, mirroring the orchestrator's own report redaction. Blueprint client secrets
        are kept only when IncludeSecrets is set; every other credential-shaped key is
        always redacted, because it is a credential rather than something the run created,
        and -IncludeSecrets must never unlock it.

    .PARAMETER Value
        The value to redact: a scalar, a SecureString, a SwitchParameter, an IDictionary, a
        PSCustomObject, or an IEnumerable of any of these. Recursed into for the latter two.

    .PARAMETER IncludeSecrets
        Keep the blueprint client secret ($script:ConditionallyRedactedReportKeys) instead of
        redacting it. Every key in $script:AlwaysRedactedReportKeys is redacted regardless.

    .PARAMETER Depth
        Recursion guard, incremented on every nested call. Not meant to be set by callers -
        it exists so a pathologically deep or self-referential object cannot recurse forever;
        past depth 8 the remaining value is stringified instead of walked further.
    #>
    [CmdletBinding()]
    param($Value, [switch] $IncludeSecrets, [int] $Depth = 0)

    if ($null -eq $Value) { return $null }
    if ($Depth -gt 8) { return [string]$Value }
    if ($Value -is [System.Security.SecureString]) { return '(redacted)' }
    if ($Value -is [System.Management.Automation.SwitchParameter]) { return [bool]$Value.IsPresent }
    if ($Value -is [string] -or $Value.GetType().IsPrimitive -or $Value -is [datetime]) { return $Value }

    if ($Value -is [System.Collections.IDictionary]) {
        $map = [ordered]@{}
        foreach ($key in $Value.Keys) {
            $keyName = [string]$key
            if ($script:AlwaysRedactedReportKeys -contains $keyName) { $map[$keyName] = '(redacted)' }
            elseif (-not $IncludeSecrets -and ($script:ConditionallyRedactedReportKeys -contains $keyName)) { $map[$keyName] = '(redacted)' }
            else { $map[$keyName] = ConvertTo-A365BulkOnboardingRedactedValue -Value $Value[$key] -IncludeSecrets:$IncludeSecrets -Depth ($Depth + 1) }
        }
        return $map
    }
    if ($Value -is [psobject] -and $Value.PSObject.Properties) {
        $map = [ordered]@{}
        $any = $false
        foreach ($property in $Value.PSObject.Properties) {
            $any = $true
            if ($script:AlwaysRedactedReportKeys -contains $property.Name) { $map[$property.Name] = '(redacted)' }
            elseif (-not $IncludeSecrets -and ($script:ConditionallyRedactedReportKeys -contains $property.Name)) { $map[$property.Name] = '(redacted)' }
            else { $map[$property.Name] = ConvertTo-A365BulkOnboardingRedactedValue -Value $property.Value -IncludeSecrets:$IncludeSecrets -Depth ($Depth + 1) }
        }
        if ($any) { return $map }
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        # The unary comma keeps this an array through the pipeline; without it PowerShell
        # unwraps a single-element array return into a bare scalar.
        return , @(foreach ($item in $Value) { ConvertTo-A365BulkOnboardingRedactedValue -Value $item -IncludeSecrets:$IncludeSecrets -Depth ($Depth + 1) })
    }
    return $Value
}

Export-ModuleMember -Function @(
    'Get-A365BulkOnboardingKnownHeaders',
    'Get-A365BulkOnboardingTypeSchema',
    'Test-A365BulkOnboardingGuid',
    'Test-A365BulkOnboardingUpn',
    'ConvertTo-A365BulkOnboardingBool',
    'ConvertTo-A365BulkOnboardingArray',
    'ConvertFrom-A365BulkOnboardingJson',
    'Test-A365BulkOnboardingParameterJsonKeys',
    'Import-A365BulkOnboardingCsv',
    'ConvertTo-A365BulkOnboardingPlan',
    'New-A365BulkOnboardingOrchestratorArguments',
    'New-A365BulkOnboardingStateMap',
    'Test-A365BulkOnboardingRowReady',
    'Get-A365BulkOnboardingMember',
    'Resolve-A365BulkOnboardingRowOutcome',
    'ConvertTo-A365BulkOnboardingRedactedValue'
)
