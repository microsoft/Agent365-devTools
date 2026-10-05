# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
    Deterministic stand-in for A365-AutomationOrchestrator.ps1, used only by
    Execution.Tests.ps1 (via A365-BulkOnboarding.ps1 -ScriptRoot pointing here).
    Mimics just enough of the real contract - the create switches, the parent-id
    parameters, and the summary.identifiers / summary.failedSteps shapes documented in
    A365-BulkOnboardingCsv.psm1's Resolve-A365BulkOnboardingRowOutcome - to prove
    dependency ordering, failure propagation and in-process call semantics without Graph
    or a live tenant.

    The output below deliberately mirrors the real orchestrator's own shape rather than a
    convenient all-PSCustomObject one: only the outermost object gets [pscustomobject]$result
    treatment at the very end of the real script, so summary, summary.identifiers and each
    summary.failedSteps entry stay raw [ordered]@{} (IDictionary) values, not PSCustomObjects.
    Building this fixture as all-PSCustomObject once let a Get-Member-based dictionary-key
    lookup bug in Resolve-A365BulkOnboardingRowOutcome go undetected; keep it this way so a
    regression there is caught here again.

    Failure is triggered by a marker in the row's own display name / principal name so a
    test CSV can pick, per row, whether that row throws (Blueprint / AgentIdentity, which
    the real orchestrator also does not recover from) or reports a non-throwing failure via
    summary.failedSteps (AgentUser / AgentRegistration, exactly like the real orchestrator):

        *FAIL*   in a Blueprint/AgentIdentity display name - throws
        fail@... AgentUserPrincipalName prefix               - failedSteps, no throw
        *FAIL*   in an AgentRegistration display name        - failedSteps, no throw
#>

[CmdletBinding()]
param(
    [switch] $NewBlueprint,
    [switch] $NewAgentIdentity,
    [switch] $NewAgentUser,
    [switch] $NewAgentRegistration,

    [string] $UseExistingBlueprint,
    [string] $UseExistingAgentIdentity,

    [Parameter(Mandatory)][string] $TenantId,

    [string]      $BlueprintDisplayName,
    [string]      $BlueprintDescription,
    [string[]]    $BlueprintOwner,
    [string[]]    $BlueprintSponsor,
    [switch]      $BlueprintRequireOwnerAssignment,
    [hashtable[]] $BlueprintRequiredPermission,
    [switch]      $BlueprintGrantAdminConsent,
    [switch]      $BlueprintSkipInheritablePermissions,
    [switch]      $BlueprintNewClientSecret,
    [string]      $BlueprintKeyVaultName,
    [string]      $BlueprintKeyVaultSecretName,
    [string]      $BlueprintManagedIdentityPrincipalId,
    [hashtable]   $BlueprintParameter = @{},

    [string]      $AgentIdentityDisplayName,
    [string[]]    $AgentIdentityOwner,
    [string[]]    $AgentIdentitySponsor,
    [switch]      $AgentIdentityRequireOwnerAssignment,
    [string[]]    $AgentIdentityTag,
    [object[]]    $AgentIdentityCustomSecurityAttribute,
    [switch]      $AgentIdentitySkipCustomSecurityAttributeValidation,
    [hashtable[]] $AgentIdentityRequiredPermission,
    [switch]      $AgentIdentityGrantAdminConsent,
    [hashtable]   $AgentIdentityParameter = @{},

    [string]    $AgentUserDisplayName,
    [string]    $AgentUserPrincipalName,
    [string]    $AgentUserMailNickname,
    [string]    $AgentUserManagerUserId,
    [string]    $AgentUserManagerUpn,
    [string]    $AgentUserUsageLocation,
    [switch]    $AgentUserAssignLicense,
    [string]    $AgentUserLicenseSkuId,
    [string]    $AgentUserLicenseSkuPartNumber,
    [hashtable] $AgentUserParameter = @{},

    [string]    $AgentRegistrationDisplayName,
    [string]    $AgentRegistrationDescription,
    [string[]]  $AgentRegistrationOwner,
    [string]    $AgentRegistrationOwnerId,
    [ValidateSet('Same', 'Interactive')]
    [string]    $AgentRegistrationAuth = 'Same',
    [hashtable] $AgentRegistrationParameter = @{},

    [string] $ClientId,
    [object] $ClientSecret,
    [string] $CertificateThumbprint,
    [object] $Certificate,
    [string] $CertificatePath,
    [object] $CertificatePassword,
    [switch] $UseManagedIdentity,
    [object] $AccessToken,
    [switch] $Interactive,
    [switch] $SkipPermissionCheck,

    [string] $LogPath,
    [switch] $LogIncludeSecrets,
    [string] $LogCorrelationId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Visible only to the test that set it up (Execution.Tests.ps1 runs in the same runspace as
# every script it invokes via the call operator, so a $global: list survives the round trip
# and proves the call really happened in-process rather than in a new process).
#
# Get-Variable (not a bare $global: reference) is deliberate: under Set-StrictMode, reading
# an unset variable throws, and this fixture must stay silent - not fail the run - for every
# test that has no reason to set up the capture list.
$callCapture = Get-Variable -Name 'A365BulkExecFixtureCalls' -Scope Global -ErrorAction SilentlyContinue
if ($callCapture -and $null -ne $callCapture.Value) {
    $secretIdentity = if ($null -ne $ClientSecret) { [System.Runtime.CompilerServices.RuntimeHelpers]::GetHashCode($ClientSecret) } else { $null }
    $callCapture.Value.Add([pscustomobject]@{
        NewBlueprint             = [bool]$NewBlueprint
        NewAgentIdentity         = [bool]$NewAgentIdentity
        NewAgentUser             = [bool]$NewAgentUser
        NewAgentRegistration     = [bool]$NewAgentRegistration
        UseExistingBlueprint     = $UseExistingBlueprint
        UseExistingAgentIdentity = $UseExistingAgentIdentity
        DisplayName              = @($BlueprintDisplayName, $AgentIdentityDisplayName, $AgentUserDisplayName, $AgentRegistrationDisplayName) -join ''
        PrincipalName            = $AgentUserPrincipalName
        LogCorrelationId         = $LogCorrelationId
        ClientSecretIdentity     = $secretIdentity
    })
}

$identifiers = [ordered]@{}
$failedSteps = [System.Collections.Generic.List[object]]::new()
$secrets     = $null

if ($NewBlueprint) {
    if ($BlueprintDisplayName -like '*FAIL*') {
        throw "Fixture: blueprint '$BlueprintDisplayName' was told to fail."
    }
    $identifiers.blueprintAppId = ([guid]::NewGuid()).ToString()
    if ($BlueprintNewClientSecret) {
        # A fixed, recognisable value - Report.Tests.ps1 / Execution.Tests.ps1 assert on it
        # being redacted by default and restored only with -IncludeBlueprintSecretsInOutput.
        $secrets = [ordered]@{ clientSecret = 'super-secret-value' }
    }
}

if ($NewAgentIdentity) {
    if ([string]::IsNullOrWhiteSpace($UseExistingBlueprint)) {
        throw 'Fixture: -NewAgentIdentity needs -UseExistingBlueprint.'
    }
    if ($AgentIdentityDisplayName -like '*FAIL*') {
        throw "Fixture: agent identity '$AgentIdentityDisplayName' was told to fail."
    }
    $identifiers.agentIdentityId = ([guid]::NewGuid()).ToString()
}

if ($NewAgentUser) {
    if ([string]::IsNullOrWhiteSpace($UseExistingAgentIdentity)) {
        throw 'Fixture: -NewAgentUser needs -UseExistingAgentIdentity.'
    }
    if ($AgentUserPrincipalName -like 'fail@*') {
        $failedSteps.Add([ordered]@{ step = 'AgentUser'; status = 'Failed'; detail = "Fixture: agent user '$AgentUserPrincipalName' was told to fail." })
    }
    else {
        $identifiers.agentUserId = ([guid]::NewGuid()).ToString()
    }
}

if ($NewAgentRegistration) {
    if ([string]::IsNullOrWhiteSpace($UseExistingAgentIdentity)) {
        throw 'Fixture: -NewAgentRegistration needs -UseExistingAgentIdentity.'
    }
    if ($AgentRegistrationDisplayName -like '*FAIL*') {
        $failedSteps.Add([ordered]@{ step = 'Registration'; status = 'Failed'; detail = "Fixture: registration '$AgentRegistrationDisplayName' was told to fail." })
    }
    else {
        $identifiers.registrationId = "T_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    }
}

[pscustomobject]@{
    # Only this outermost object is a PSCustomObject - see the header comment above. summary,
    # identifiers and each failedSteps entry stay raw [ordered]@{} dictionaries, exactly like
    # A365-AutomationOrchestrator.ps1's own $result.
    summary = [ordered]@{
        outcome     = if ($failedSteps.Count -gt 0) { 'Incomplete' } else { 'Succeeded' }
        identifiers = $identifiers
        failedSteps = @($failedSteps)
    }
    secrets = if ($secrets) { [pscustomobject]$secrets } else { $null }
}
