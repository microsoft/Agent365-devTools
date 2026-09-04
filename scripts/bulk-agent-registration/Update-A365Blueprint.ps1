<#
.SYNOPSIS
    Updates an existing Microsoft Agent 365 blueprint, writing only the attributes supplied.

.DESCRIPTION
    A dedicated entry point for changing a blueprint that already exists, so updating is a
    separate, explicit command rather than a flag on the create script.

    ONLY THE ATTRIBUTES YOU PASS ARE WRITTEN. A parameter left off the command line is not
    forwarded at all, so it cannot overwrite what is already stored. That is enforced by
    forwarding on "was this parameter bound", never on "does this variable have a value" -
    the distinction matters because a parameter with a default value would otherwise be
    written on every run.

    The Graph work itself is done by New-A365AgentBlueprint.ps1, which this script invokes in
    its update mode. Keeping one implementation means a fix applies to both entry points at
    once; this script owns the interface, not the behaviour.

.PARAMETER BlueprintId
    The blueprint to update. Accepts either the application (client) id or the object id.

.PARAMETER DisplayName
    New display name. Compared case-sensitively, so a change of casing alone is applied
    rather than silently discarded.

.PARAMETER Description
    New description.

.PARAMETER Sponsor
    Sponsors to assign. Accepts user principal names or object ids. Existing sponsors that
    are already assigned are left alone.

.PARAMETER Owner
    Owners to assign. Accepts user principal names or object ids.

.PARAMETER RequiredPermission
    Permission set to apply, in the same @{ ResourceAppId; DelegatedScopes; AppRoles } shape
    the create script accepts. requiredResourceAccess is merged rather than replaced.

.PARAMETER RequireOwnerAssignment
    Fail if an owner cannot be assigned, instead of reporting it and continuing.

.PARAMETER NewClientSecret
    Add a new client secret to the blueprint.

.PARAMETER GrantAdminConsent
    Grant tenant admin consent for the requested permissions.

.PARAMETER SkipInheritablePermissions
    Do not touch inheritable permissions.

.PARAMETER ManagedIdentityPrincipalId
    Principal (object) id of a managed identity to register as a federated identity
    credential on the blueprint, so the agent can get tokens with no stored secret.

.PARAMETER TenantId
    Directory (tenant) ID or verified domain.

.PARAMETER ClientId
    Application (client) ID to authenticate as. Forwarded unchanged to
    New-A365AgentBlueprint.ps1 - see its help for the full authentication reference.

.PARAMETER ClientSecret
    Client secret. Accepts a string or a SecureString and is forwarded unchanged - the exact
    object handed in is the exact object New-A365AgentBlueprint.ps1 receives, never
    re-typed, copied or stringified along the way.

.PARAMETER CertificateThumbprint
    Certificate thumbprint, forwarded unchanged.

.PARAMETER Certificate
    An X509Certificate2 to authenticate with, forwarded unchanged.

.PARAMETER CertificatePath
    Path to a .pfx file, forwarded unchanged.

.PARAMETER CertificatePassword
    Password for -CertificatePath. Accepts a string or a SecureString and is forwarded
    unchanged, for the same reason as -ClientSecret above.

.PARAMETER UseManagedIdentity
    Authenticate with the host's managed identity, forwarded unchanged.

.PARAMETER AccessToken
    A pre-acquired Graph access token. Accepts a string or a SecureString and is forwarded
    unchanged, for the same reason as -ClientSecret above.

.PARAMETER Interactive
    Sign in as a user instead of running as an application, forwarded unchanged.

.PARAMETER SkipPermissionCheck
    Skip the app role pre-flight check, forwarded unchanged.

.PARAMETER StepParameter
    Escape hatch: a hashtable splatted into New-A365AgentBlueprint.ps1 for parameters this
    wrapper does not surface. Keys are the step script's own parameter names and must not
    include 'Update', 'BlueprintId' or 'TenantId', which this script already supplies.

.PARAMETER ScriptRoot
    Directory containing New-A365AgentBlueprint.ps1, which performs the actual Graph work.
    Defaults to this script's own directory - keep the two files together, or pass this
    explicitly.

.EXAMPLE
    .\Update-A365Blueprint.ps1 -TenantId $tid -Interactive `
        -BlueprintId 0bc41111-35c2-45c4-bbeb-981f0ee9e9e5 `
        -Description 'Weather MCP blueprint - production'

    Change only the description. The display name is not passed, so it is not written.

.EXAMPLE
    .\Update-A365Blueprint.ps1 -TenantId $tid -ClientId $app -ClientSecret $env:A365_CLIENT_SECRET `
        -BlueprintId 0bc41111-35c2-45c4-bbeb-981f0ee9e9e5 `
        -Sponsor 'andthom@contoso.com','anwoodru@contoso.com'

    Assign sponsors, leaving every other attribute untouched.

.EXAMPLE
    .\Update-A365Blueprint.ps1 -TenantId $tid -Interactive `
        -BlueprintId $bp -Description 'Q3 refresh' -WhatIf

    Show what would change without writing anything.

.NOTES
    Requires New-A365AgentBlueprint.ps1 beside this script.
#>

#requires -Version 7

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'CertificatePassword',
    Justification = 'Forwarded verbatim to the step script, which converts it immediately.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '',
    Justification = 'No credential pair is declared; these are Graph auth parameters forwarded unchanged.')]
param(
    [Parameter(Mandatory)][string] $TenantId,

    [Parameter(Mandatory)][string] $BlueprintId,

    # Attributes. Anything not supplied is not forwarded, and therefore not written.
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

    # Forward credential objects unchanged so SecureString values are not stringified.
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

    # Escape hatch for anything this wrapper does not surface.
    [hashtable] $StepParameter = @{},

    [string] $ScriptRoot
)

$ErrorActionPreference = 'Stop'

$root = if ($ScriptRoot) { $ScriptRoot } elseif ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).ProviderPath }
$step = Join-Path $root 'New-A365AgentBlueprint.ps1'
if (-not (Test-Path -LiteralPath $step)) {
    throw "New-A365AgentBlueprint.ps1 was not found at '$root'. It does the Graph work for this script; keep the two together, or pass -ScriptRoot."
}
$step = (Resolve-Path -LiteralPath $step).ProviderPath

# The whole contract of this script: forward exactly what was asked for and nothing else.
# ContainsKey, not truthiness - a parameter that defaults to a value would otherwise be
# written on every run and silently overwrite the stored one.
$forwardable = @(
    'DisplayName', 'Description', 'Sponsor', 'Owner', 'RequiredPermission'
    'RequireOwnerAssignment', 'NewClientSecret', 'GrantAdminConsent'
    'SkipInheritablePermissions', 'ManagedIdentityPrincipalId'
    'ClientId', 'ClientSecret', 'CertificateThumbprint', 'Certificate', 'CertificatePath'
    'CertificatePassword', 'UseManagedIdentity', 'AccessToken', 'Interactive', 'SkipPermissionCheck'
)

$forward = @{
    TenantId    = $TenantId
    Update      = $true
    BlueprintId = $BlueprintId
}
foreach ($name in $forwardable) {
    if ($PSBoundParameters.ContainsKey($name)) { $forward[$name] = $PSBoundParameters[$name] }
}

foreach ($k in $StepParameter.Keys) {
    if ($k -in @('Update', 'BlueprintId', 'TenantId')) {
        throw "Do not pass '$k' through -StepParameter; it is supplied by this script."
    }
    $forward[$k] = $StepParameter[$k]
}

& $step @forward -WhatIf:$WhatIfPreference