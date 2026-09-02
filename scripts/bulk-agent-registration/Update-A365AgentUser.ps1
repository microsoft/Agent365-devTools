<#
.SYNOPSIS
    Updates an existing Microsoft Agent 365 agent user, writing only the attributes supplied.

.DESCRIPTION
    A dedicated entry point for changing an agent user that already exists, so updating is a
    separate, explicit command rather than a flag on the create script.

    ONLY THE ATTRIBUTES YOU PASS ARE WRITTEN. This matters more here than anywhere else in
    the suite: on the create path -UsageLocation defaults to US and -DisplayName is derived
    from the user principal name, so forwarding on "has a value" rather than "was supplied"
    would silently rewrite a live account on every run. Nothing is forwarded unless it was
    bound on the command line.

    The update refuses to touch an account that is not an agent user, so an ordinary
    employee's account cannot be modified by mistake.

    The user principal name cannot be changed after creation and is therefore not accepted
    here; the account is identified by -AgentUserId instead.

    APP-ONLY AUTHENTICATION ONLY. New-A365AgentUser.ps1 has no -Interactive and no
    -SkipPermissionCheck, so those parameters are deliberately absent from this script too
    rather than being accepted and then failing downstream.

    The Graph work itself is done by New-A365AgentUser.ps1, which this script invokes in its
    update mode. That script reports failure by exiting with a non-zero code instead of
    throwing, so this script checks the exit code - a try/catch alone never fires.

.PARAMETER AgentUserId
    Object id or user principal name of the agent user to update.

.PARAMETER DisplayName
    New display name.

.PARAMETER MailNickname
    New mail nickname.

.PARAMETER UsageLocation
    Two-letter usage location. Only written when supplied; it is NOT defaulted here, because
    defaulting it would overwrite the stored value on every update.

.PARAMETER ManagerUserId
    Object id of the manager to assign. Mutually exclusive with -ManagerUpn.

.PARAMETER ManagerUpn
    User principal name of the manager to assign. Mutually exclusive with -ManagerUserId.

.PARAMETER AssignLicense
    Assign a licence. Needs -LicenseSkuId or -LicenseSkuPartNumber, and the account must have
    a usage location either already set or supplied in the same run.

.PARAMETER LicenseSkuId
    SKU id of the licence to assign.

.PARAMETER LicenseSkuPartNumber
    SKU part number of the licence to assign, for example MICROSOFT_AGENT_365_TIER_3.

.PARAMETER TenantId
    Directory (tenant) ID or verified domain.

.EXAMPLE
    .\Update-A365AgentUser.ps1 -TenantId $tid -ClientId $app -ClientSecret $env:A365_CLIENT_SECRET `
        -AgentUserId 'research.assistant@contoso.com' -DisplayName 'Research Assistant'

    Rename the account. The usage location and manager are not passed, so they are not written.

.EXAMPLE
    .\Update-A365AgentUser.ps1 -TenantId $tid -ClientId $app -ClientSecret $env:A365_CLIENT_SECRET `
        -AgentUserId $id -AssignLicense -LicenseSkuPartNumber MICROSOFT_AGENT_365_TIER_3

    Assign a licence to an agent user that already has a usage location.

.NOTES
    Requires New-A365AgentUser.ps1 beside this script, and app-only authentication.
#>

#requires -Version 7

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'CertificatePassword',
    Justification = 'Forwarded verbatim to the step script, which converts it immediately.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '',
    Justification = 'No credential pair is declared; these are Graph auth parameters forwarded unchanged.')]
param(
    [Parameter(Mandatory)][string] $TenantId,

    [Parameter(Mandatory)][string] $AgentUserId,

    # Attributes. Anything not supplied is not forwarded, and therefore not written.
    [string] $DisplayName,
    [string] $MailNickname,
    [string] $UsageLocation,
    [string] $ManagerUserId,
    [string] $ManagerUpn,
    [switch] $AssignLicense,
    [string] $LicenseSkuId,
    [string] $LicenseSkuPartNumber,
    [string[]] $DisabledPlans,

    # Authentication. No -Interactive or -SkipPermissionCheck: the step script has neither.
    [string] $ClientId,
    [string] $ClientSecret,
    [string] $CertificateThumbprint,
    [string] $CertificateStoreLocation,
    [object] $Certificate,
    [string] $CertificatePath,
    [string] $CertificatePassword,
    [switch] $UseManagedIdentity,
    [string] $ManagedIdentityClientId,
    [string] $AccessToken,

    # Escape hatch for anything this wrapper does not surface.
    [hashtable] $StepParameter = @{},

    [string] $ScriptRoot
)

$ErrorActionPreference = 'Stop'

# The step script prefers ManagerUserId when both are given, silently ignoring the UPN.
# Silent precedence between two parameters that mean different things is worth refusing.
if ($PSBoundParameters.ContainsKey('ManagerUserId') -and $PSBoundParameters.ContainsKey('ManagerUpn')) {
    throw 'Specify -ManagerUserId or -ManagerUpn, not both. New-A365AgentUser.ps1 would silently ignore the UPN.'
}
if ($AssignLicense -and -not ($PSBoundParameters.ContainsKey('LicenseSkuId') -or $PSBoundParameters.ContainsKey('LicenseSkuPartNumber'))) {
    throw '-AssignLicense needs -LicenseSkuId or -LicenseSkuPartNumber.'
}

$root = if ($ScriptRoot) { $ScriptRoot } elseif ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).ProviderPath }
$step = Join-Path $root 'New-A365AgentUser.ps1'
if (-not (Test-Path -LiteralPath $step)) {
    throw "New-A365AgentUser.ps1 was not found at '$root'. It does the Graph work for this script; keep the two together, or pass -ScriptRoot."
}
$step = (Resolve-Path -LiteralPath $step).ProviderPath

# The whole contract of this script: forward exactly what was asked for and nothing else.
# ContainsKey, not truthiness - UsageLocation and DisplayName have create-path defaults that
# would otherwise rewrite a live account on every run.
$forwardable = @(
    'DisplayName', 'MailNickname', 'UsageLocation', 'ManagerUserId', 'ManagerUpn'
    'AssignLicense', 'LicenseSkuId', 'LicenseSkuPartNumber', 'DisabledPlans'
    'ClientId', 'ClientSecret', 'CertificateThumbprint', 'CertificateStoreLocation'
    'Certificate', 'CertificatePath', 'CertificatePassword', 'UseManagedIdentity'
    'ManagedIdentityClientId', 'AccessToken'
)

$forward = @{
    TenantId    = $TenantId
    Update      = $true
    AgentUserId = $AgentUserId
    PassThru    = $true   # without it the step script returns nothing at all
}
foreach ($name in $forwardable) {
    if ($PSBoundParameters.ContainsKey($name)) { $forward[$name] = $PSBoundParameters[$name] }
}

foreach ($k in $StepParameter.Keys) {
    if ($k -in @('Update', 'AgentUserId', 'TenantId', 'UserPrincipalName')) {
        throw "Do not pass '$k' through -StepParameter; it is supplied by this script, and the user principal name cannot be changed after creation."
    }
    $forward[$k] = $StepParameter[$k]
}

$global:LASTEXITCODE = 0
& $step @forward -WhatIf:$WhatIfPreference

# New-A365AgentUser.ps1 signals failure by exiting non-zero rather than throwing, so a
# try/catch around the call above would never fire. Surface it as a terminating error here so
# a caller cannot mistake a failed update for a successful one.
if ($LASTEXITCODE -ne 0) {
    throw "New-A365AgentUser.ps1 exited with code $LASTEXITCODE. Its own error output above has the detail."
}