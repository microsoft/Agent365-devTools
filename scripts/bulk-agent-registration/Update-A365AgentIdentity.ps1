<#
.SYNOPSIS
    Updates an existing Microsoft Agent 365 agent identity, writing only the attributes supplied.

.DESCRIPTION
    A dedicated entry point for changing an agent identity that already exists, so updating is
    a separate, explicit command rather than a flag on the create script.

    ONLY THE ATTRIBUTES YOU PASS ARE WRITTEN. A parameter left off the command line is not
    forwarded at all, so it cannot overwrite what is already stored.

    The delegated update implementation preserves values that are not named:

        -CustomSecurityAttribute   merges per attribute; attributes you do not name survive
        -Tag                       adds tags to an order-preserving, case-insensitive union

    Passing one tag does not remove existing tags. Passing one custom security attribute does
    not remove other attributes.

    The Graph work itself is done by New-A365AgentIdentity.ps1, which this script invokes in
    its update mode. Keeping one implementation means a fix applies to both entry points at
    once; this script owns the interface, not the behaviour.

.PARAMETER AgentIdentityId
    Object id (the service principal id) of the agent identity to update. The object is
    located through the agentIdentity type-cast URI, so a plain service principal is refused
    rather than modified.

.PARAMETER DisplayName
    New display name. Compared case-sensitively, so a change of casing alone is applied.

.PARAMETER Tag
    Tags to add. Merged with the identity's existing non-inherited tags as an order-preserving,
    case-insensitive union; existing tags are never removed. Tags are not visible in the Entra
    portal.

.PARAMETER CustomSecurityAttribute
    Custom security attributes, either as "AttributeSet:Attribute:Value" strings or as a
    nested hashtable. Merged per attribute. An empty value removes an assignment. Set,
    attribute and value names are all case-sensitive.

    Requires CustomSecAttributeAssignment.ReadWrite.All, which no Directory.* or Application.*
    role grants and which Global Administrator does not include.

.PARAMETER SkipCustomSecurityAttributeValidation
    Send the attributes without checking them against the tenant schema first.

.PARAMETER Sponsor
    Sponsors to assign. Accepts user principal names or object ids.

.PARAMETER Owner
    Owners to assign. Accepts user principal names or object ids.

.PARAMETER Disabled
    Disable the agent identity. Pass -Disabled:$false to re-enable one that was disabled.

.PARAMETER RequiredPermission
    Permission set to apply, in the same shape the create script accepts.

.PARAMETER RequireOwnerAssignment
    Treat a refused -Owner assignment as fatal instead of the default: a warning, with the
    update otherwise applied.

.PARAMETER GrantAdminConsent
    Consent -RequiredPermission on the agent identity's own service principal. See
    New-A365AgentIdentity.ps1's help for how this differs from blueprint-level consent.

.PARAMETER OutputJsonPath
    Write a JSON summary of the update to this path.

.PARAMETER TenantId
    Directory (tenant) ID or verified domain.

.PARAMETER ClientId
    Application (client) ID to authenticate as. Forwarded unchanged to
    New-A365AgentIdentity.ps1 - see its help for the full authentication reference.

.PARAMETER ClientSecret
    Client secret, forwarded unchanged. Also read from $env:A365_CLIENT_SECRET by the step
    script when omitted.

.PARAMETER CertificateThumbprint
    Certificate thumbprint, forwarded unchanged.

.PARAMETER Certificate
    An X509Certificate2 to authenticate with, forwarded unchanged.

.PARAMETER CertificatePath
    Path to a .pfx file, forwarded unchanged.

.PARAMETER CertificatePassword
    Password for -CertificatePath, forwarded unchanged.

.PARAMETER UseManagedIdentity
    Authenticate with the host's managed identity, forwarded unchanged.

.PARAMETER AccessToken
    A pre-acquired Graph access token, forwarded unchanged.

.PARAMETER Interactive
    Sign in as a user instead of running as an application, forwarded unchanged.

.PARAMETER SkipPermissionCheck
    Skip the app role pre-flight check, forwarded unchanged.

.PARAMETER StepParameter
    Escape hatch: a hashtable splatted into New-A365AgentIdentity.ps1 for parameters this
    wrapper does not surface. Keys are the step script's own parameter names and must not
    include 'Update', 'AgentIdentityId' or 'TenantId', which this script already supplies.

.PARAMETER ScriptRoot
    Directory containing New-A365AgentIdentity.ps1, which performs the actual Graph work.
    Defaults to this script's own directory - keep the two files together, or pass this
    explicitly.

.EXAMPLE
    .\Update-A365AgentIdentity.ps1 -TenantId $tid -Interactive `
        -AgentIdentityId 1111aaaa-2222-bbbb-3333-cccc4444dddd `
        -CustomSecurityAttribute 'AgentAttributes:AgentApprovalStatus:HR_Approved,IT_Approved'

    Change one custom security attribute. Other attributes in the set are untouched, and the
    display name and tags are not written because they were not passed.

.EXAMPLE
    .\Update-A365AgentIdentity.ps1 -TenantId $tid -Interactive `
        -AgentIdentityId $id -Tag 'env:prod','team:hr'

    Replace the tag collection. Any tag not listed here is removed.

.EXAMPLE
    .\Update-A365AgentIdentity.ps1 -TenantId $tid -Interactive `
        -AgentIdentityId $id -Disabled:$false

    Re-enable an agent identity that was previously disabled.

.NOTES
    Requires New-A365AgentIdentity.ps1 beside this script.
#>

#requires -Version 7

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'CertificatePassword',
    Justification = 'Forwarded verbatim to the step script, which converts it immediately.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '',
    Justification = 'No credential pair is declared; these are Graph auth parameters forwarded unchanged.')]
param(
    [Parameter(Mandatory)][string] $TenantId,

    [Parameter(Mandatory)][string] $AgentIdentityId,

    # Attributes. Anything not supplied is not forwarded, and therefore not written.
    [string]   $DisplayName,
    [string[]] $Tag,
    [object[]] $CustomSecurityAttribute,
    [switch]   $SkipCustomSecurityAttributeValidation,
    [string[]] $Sponsor,
    [string[]] $Owner,
    [switch]   $Disabled,
    [object[]] $RequiredPermission,
    [switch]   $RequireOwnerAssignment,
    [switch]   $GrantAdminConsent,
    [string]   $OutputJsonPath,

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
$step = Join-Path $root 'New-A365AgentIdentity.ps1'
if (-not (Test-Path -LiteralPath $step)) {
    throw "New-A365AgentIdentity.ps1 was not found at '$root'. It does the Graph work for this script; keep the two together, or pass -ScriptRoot."
}
$step = (Resolve-Path -LiteralPath $step).ProviderPath

# The whole contract of this script: forward exactly what was asked for and nothing else.
# ContainsKey, not truthiness - note -Disabled in particular, where $false is a meaningful
# value that must be forwarded when supplied and omitted when not.
$forwardable = @(
    'DisplayName', 'Tag', 'CustomSecurityAttribute', 'SkipCustomSecurityAttributeValidation'
    'Sponsor', 'Owner', 'Disabled', 'RequiredPermission', 'RequireOwnerAssignment'
    'GrantAdminConsent', 'OutputJsonPath'
    'ClientId', 'ClientSecret', 'CertificateThumbprint', 'Certificate', 'CertificatePath'
    'CertificatePassword', 'UseManagedIdentity', 'AccessToken', 'Interactive', 'SkipPermissionCheck'
)

$forward = @{
    TenantId        = $TenantId
    Update          = $true
    AgentIdentityId = $AgentIdentityId
}
foreach ($name in $forwardable) {
    if ($PSBoundParameters.ContainsKey($name)) { $forward[$name] = $PSBoundParameters[$name] }
}

foreach ($k in $StepParameter.Keys) {
    if ($k -in @('Update', 'AgentIdentityId', 'TenantId')) {
        throw "Do not pass '$k' through -StepParameter; it is supplied by this script."
    }
    $forward[$k] = $StepParameter[$k]
}

& $step @forward -WhatIf:$WhatIfPreference