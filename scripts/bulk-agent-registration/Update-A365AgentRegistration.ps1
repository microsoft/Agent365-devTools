<#
.SYNOPSIS
    Updates an existing Microsoft Agent 365 registration, writing only the attributes supplied.

.DESCRIPTION
    A dedicated entry point for changing a registration that already exists, so updating is a
    separate, explicit command rather than a flag on the create script.

    ONLY THE ATTRIBUTES YOU PASS ARE WRITTEN. A parameter left off the command line is not
    forwarded at all, so it cannot overwrite what is already stored. In particular, leaving
    out -Owner and -OwnerId preserves the existing owners rather than falling back to the
    signed-in user.

    The registration API is preview surface and behaves in ways worth knowing about, all
    established by probing it rather than from documentation:

        * it returns HTTP 200 on writes it silently discards, so the step script reads the
          object back and warns per property that did not persist
        * ownerIds replaces the whole collection, so list every owner you want to keep
        * managedByAppId is rejected outright and is therefore not accepted here
        * throttling arrives disguised as HTTP 500 with "Too Many Requests" in the body

    The Graph work itself is done by New-A365AgentRegistration.ps1, which this script invokes
    in its update mode. Keeping one implementation means a fix applies to both entry points at
    once; this script owns the interface, not the behaviour.

.PARAMETER RegistrationId
    The registration to update. This is the id the SERVICE returned when the agent was
    registered, which starts with 'T_' - not the agent identity id and not the blueprint id.
    The service assigns that id itself and ignores any id supplied at creation time, so a
    client-generated GUID will never resolve. The bare GUID form is also accepted and is
    resolved to the 'T_' form automatically.

.PARAMETER DisplayName
    New display name.

.PARAMETER Description
    New description.

.PARAMETER Owner
    Owners to set, as user principal names or object ids. REPLACES the whole ownerIds
    collection, so list every owner you want to keep.

.PARAMETER OwnerId
    Owners to set, as object ids. REPLACES the whole ownerIds collection.

.PARAMETER AgentIdentityId
    Re-point the registration at a different agent identity.

.PARAMETER BlueprintAppId
    Re-point the registration at a different blueprint.

.PARAMETER TenantId
    Directory (tenant) ID or verified domain.

.EXAMPLE
    .\Update-A365AgentRegistration.ps1 -TenantId $tid -Interactive `
        -RegistrationId T_f9955348-7fb4-6143-49fb-a0f695211ff4 `
        -DisplayName 'HR Agent'

    Rename a registration. The description and owners are not passed, so they are not written.

.EXAMPLE
    .\Update-A365AgentRegistration.ps1 -TenantId $tid -Interactive `
        -RegistrationId $reg -Owner 'anpinto@contoso.com','rowille@contoso.com'

    Set the owner list. Any owner not listed here is removed.

.NOTES
    Requires New-A365AgentRegistration.ps1 beside this script.

    The registry APIs read ownerIds and createdBy from /me, so an app-only token often cannot
    drive them at all. Use -Interactive if an app-only run is refused.
#>

#requires -Version 7

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'CertificatePassword',
    Justification = 'Forwarded verbatim to the step script, which converts it immediately.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '',
    Justification = 'No credential pair is declared; these are Graph auth parameters forwarded unchanged.')]
param(
    [Parameter(Mandatory)][string] $TenantId,

    [Parameter(Mandatory)][string] $RegistrationId,

    # Attributes. Anything not supplied is not forwarded, and therefore not written.
    [string]   $DisplayName,
    [string]   $Description,
    [string[]] $Owner,
    [string[]] $OwnerId,
    [string]   $AgentIdentityId,
    [string]   $BlueprintAppId,
    [switch]   $SkipDisplayNameNormalization,

    # Authentication, forwarded unchanged.
    [string] $ClientId,
    [string] $ClientSecret,
    [string] $CertificateThumbprint,
    [object] $Certificate,
    [string] $CertificatePath,
    [string] $CertificatePassword,
    [switch] $UseManagedIdentity,
    [string] $AccessToken,
    [switch] $Interactive,
    [switch] $SkipPermissionCheck,

    # Escape hatch for anything this wrapper does not surface.
    [hashtable] $StepParameter = @{},

    [string] $ScriptRoot
)

$ErrorActionPreference = 'Stop'

$root = if ($ScriptRoot) { $ScriptRoot } elseif ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).ProviderPath }
$step = Join-Path $root 'New-A365AgentRegistration.ps1'
if (-not (Test-Path -LiteralPath $step)) {
    throw "New-A365AgentRegistration.ps1 was not found at '$root'. It does the Graph work for this script; keep the two together, or pass -ScriptRoot."
}
$step = (Resolve-Path -LiteralPath $step).ProviderPath

# The whole contract of this script: forward exactly what was asked for and nothing else.
$forwardable = @(
    'DisplayName', 'Description', 'Owner', 'OwnerId', 'AgentIdentityId', 'BlueprintAppId'
    'SkipDisplayNameNormalization'
    'ClientId', 'ClientSecret', 'CertificateThumbprint', 'Certificate', 'CertificatePath'
    'CertificatePassword', 'UseManagedIdentity', 'AccessToken', 'Interactive', 'SkipPermissionCheck'
)

$forward = @{
    TenantId       = $TenantId
    Update         = $true
    RegistrationId = $RegistrationId
}
foreach ($name in $forwardable) {
    if ($PSBoundParameters.ContainsKey($name)) { $forward[$name] = $PSBoundParameters[$name] }
}

foreach ($k in $StepParameter.Keys) {
    if ($k -in @('Update', 'RegistrationId', 'TenantId')) {
        throw "Do not pass '$k' through -StepParameter; it is supplied by this script."
    }
    if ($k -eq 'ManagedByAppId') {
        throw 'managedByAppId cannot be changed on an existing registration; the service rejects it on PATCH.'
    }
    $forward[$k] = $StepParameter[$k]
}

& $step @forward -WhatIf:$WhatIfPreference