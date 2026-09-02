<#
.SYNOPSIS
    Creates the Entra ID application registration that New-A365AgentBlueprint.ps1,
    New-A365AgentIdentity.ps1 and New-A365AgentRegistration.ps1 run as, and grants it every
    Microsoft Graph application permission those scripts need.

.DESCRIPTION
    Run this once, as a Global Administrator or Privileged Role Administrator. Afterwards the
    three provisioning scripts can run unattended with -ClientId / -ClientSecret (or a
    certificate, or a managed identity) instead of an interactive sign-in.

    What it does, all through Microsoft Graph REST calls:
      1. Resolves the Microsoft Graph service principal and builds a name -> GUID map from its
         published appRoles. Nothing is hardcoded, so a tenant that has not yet surfaced a preview
         permission fails with a clear message instead of a mystery 400.
      2. Creates (or reuses) the application registration.
      3. Creates (or reuses) its service principal - the app role grants attach to the SP, not the
         application.
      4. Adds credentials: a client secret, a certificate, and/or a federated identity credential
         for workload identity federation (GitHub Actions, Azure DevOps, Kubernetes).
      5. Grants each app role via POST /servicePrincipals/{graphSpId}/appRoleAssignedTo, which is
         admin consent. Already-granted roles are skipped, so re-running is safe.
      6. Reads the grants back and prints ready-to-paste invocations for the other scripts.

    Every step is idempotent: re-running reconciles rather than duplicates.

    PERMISSION SETS (-Scenario)
      Blueprint      - what New-A365AgentBlueprint.ps1 checks for
      AgentIdentity  - what New-A365AgentIdentity.ps1 checks for
      Registration   - what New-A365AgentRegistration.ps1 can pre-flight
      All (default)  - the union, so one app drives the whole pipeline

    A NOTE ABOUT AGENT REGISTRATION
    The two agent registration endpoints are /beta, but both permissions ARE published as
    application roles on Microsoft Graph (verified against the live Graph service principal):
      AgentRegistration.ReadWrite.All   39fb8c64-7bd3-4107-8515-14d6e55ddda4
      AgentInstance.ReadWrite.All       07abdd95-78dc-4353-bd32-09f880ea43d0
    so this script grants them as app roles and unattended, app-only registration works. They are
    ALSO added as delegated scopes (-AddRegistrationDelegatedScopes, on by default for the
    Registration and All scenarios) so the same app can act as the client of an interactive run;
    that delegated consent still has to be granted, and this script prints the consent URL.

    An app-only registration run must pass -Owner or -OwnerId, because there is no /me to read
    ownerIds and createdBy from, and it must leave -ManagedByAppId unset so the registration is
    stamped with this application's own appId.

.PARAMETER TenantId
    Directory (tenant) ID or verified domain. Required.

.PARAMETER DisplayName
    Display name of the application registration. Also the idempotency key when -AppId is absent.

.PARAMETER AppId
    Reuse an existing application by its application (client) ID instead of matching on name.

.PARAMETER Scenario
    Which permission set to grant: Blueprint, AgentIdentity, Registration, AgentUser or All
    (default).

    AgentUser covers the agent user phase of the orchestrator: creating the agent user and
    setting its manager, usage location, licence and per-identity consent. Its key role is
    AgentIdUser.ReadWrite.All, which authorizes POST /beta/users for an agentUser;
    User.ReadWrite.All does NOT authorize that call, so a run without it fails the agent user
    phase with a bare "Insufficient privileges" 403.

.PARAMETER AdditionalAppRole
    Extra Microsoft Graph application permissions to grant, by name.

.PARAMETER SkipAppRole
    Application permissions to leave out of the chosen scenario, by name.

.PARAMETER NewClientSecret
    Adds a client secret and prints it once. It cannot be retrieved again.

.PARAMETER SecretValidityMonths
    Lifetime of the generated secret in months. Default 6, maximum 24.

.PARAMETER CertificateThumbprint
    Thumbprint of a certificate in the current user's or machine's store to upload as a
    credential. Certificates are preferred over secrets for unattended automation.

.PARAMETER CertificatePath
    Path to a .cer/.crt public certificate file to upload as a credential.

.PARAMETER FederatedCredential
    One or more hashtables describing federated identity credentials, so the app can be used from
    GitHub Actions or Azure DevOps with no stored secret at all. Keys: Name (required),
    Issuer (required), Subject (required), Audience (defaults to api://AzureADTokenExchange),
    Description.

.PARAMETER AddRegistrationDelegatedScopes
    Adds AgentRegistration.ReadWrite.All and AgentInstance.ReadWrite.All as delegated scopes on the
    application, for use as the client app of an interactive registration run. Defaults to on for
    the Registration and All scenarios.

.PARAMETER SkipGrant
    Create the app and credentials but do not grant the app roles.

.PARAMETER OutputPath
    Writes a JSON summary to this path. The client secret is NOT written to it.

.PARAMETER ClientId
    Application (client) ID to authenticate as, or the client app id for -Interactive.

.PARAMETER ClientSecret
    Client secret, as a SecureString or a plain string. Also read from $env:A365_CLIENT_SECRET,
    which is preferred because it keeps the value out of shell history.

.PARAMETER AuthCertificateThumbprint
    Thumbprint of the certificate used to AUTHENTICATE this script (distinct from
    -CertificateThumbprint, which is the credential being added to the new app).

.PARAMETER UseManagedIdentity
    Authenticate with the host's managed identity.

.PARAMETER AccessToken
    A pre-acquired Graph access token, as a SecureString or a plain string.

.PARAMETER Interactive
    Sign in as a user. This is the normal choice for a one-time bootstrap.

.EXAMPLE
    # Bootstrap the whole pipeline with a client secret.
    .\New-A365AutomationApp.ps1 -TenantId contoso.onmicrosoft.com -Interactive `
        -DisplayName 'A365 Provisioning Automation' -NewClientSecret

.EXAMPLE
    # Certificate-based, blueprint permissions only.
    .\New-A365AutomationApp.ps1 -TenantId contoso.onmicrosoft.com -Interactive `
        -Scenario Blueprint -CertificateThumbprint A1B2C3D4E5F60718293A4B5C6D7E8F9012345678

.EXAMPLE
    # Secretless CI from GitHub Actions.
    .\New-A365AutomationApp.ps1 -TenantId contoso.onmicrosoft.com -Interactive -FederatedCredential @(
        @{ Name='github-main'; Issuer='https://token.actions.githubusercontent.com'
           Subject='repo:contoso/agents:ref:refs/heads/main' }
    )

.EXAMPLE
    # See what would change without touching the tenant.
    .\New-A365AutomationApp.ps1 -TenantId contoso.onmicrosoft.com -Interactive -WhatIf

.NOTES
    Requires the Microsoft.Graph.Authentication module:
        Install-Module Microsoft.Graph.Authentication -Scope CurrentUser

    The signed-in user needs Application.ReadWrite.All and AppRoleAssignment.ReadWrite.All, which
    in practice means Global Administrator or Privileged Role Administrator - granting application
    permissions IS admin consent.
#>

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string] $TenantId,

    [string] $DisplayName = 'A365 Provisioning Automation',
    [string] $AppId,

    [ValidateSet('Blueprint', 'AgentIdentity', 'Registration', 'AgentUser', 'All')]
    [string] $Scenario = 'All',

    [string[]] $AdditionalAppRole = @(),
    [string[]] $SkipAppRole       = @(),

    [switch] $NewClientSecret,
    [ValidateRange(1, 24)][int] $SecretValidityMonths = 6,
    [string] $CertificateThumbprint,
    [string] $CertificatePath,
    [hashtable[]] $FederatedCredential = @(),

    [bool]   $AddRegistrationDelegatedScopes = $true,
    [switch] $SkipGrant,
    [string] $OutputPath,

    # --- Key Vault ----------------------------------------------------------
    [string] $KeyVaultName,
    [string] $KeyVaultSecretName,
    [object] $KeyVaultAccessToken,

    # --- authentication -----------------------------------------------------
    [string]       $ClientId,
    [object]       $ClientSecret,
    [string]       $AuthCertificateThumbprint,
    [switch]       $UseManagedIdentity,
    [object]       $AccessToken,
    [switch]       $Interactive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:GraphRoot           = 'https://graph.microsoft.com/v1.0'
$script:MicrosoftGraphAppId = '00000003-0000-0000-c000-000000000000'

# ---------------------------------------------------------------------------
# Graph REST helpers
# ---------------------------------------------------------------------------

# `.PSObject.Properties.Name` is member enumeration, which throws under StrictMode when the object
# has zero properties - and Graph does return empty bodies. foreach over an empty collection is safe.
function Test-HasProperty {
    param($Object, [Parameter(Mandatory)][string] $Name)
    if ($null -eq $Object) { return $false }
    $properties = $Object.PSObject.Properties
    if ($null -eq $properties) { return $false }
    foreach ($property in $properties) {
        if ($property.Name -eq $Name) { return $true }
    }
    return $false
}

function Get-GraphErrorInfo {
    param($ErrorRecord)

    $codeToStatus = @{
        'Request_ResourceNotFound'                = 404
        'ResourceNotFound'                        = 404
        'itemNotFound'                            = 404
        'Request_BadRequest'                      = 400
        'badRequest'                              = 400
        'Authorization_RequestDenied'             = 403
        'accessDenied'                            = 403
        'InvalidAuthenticationToken'              = 401
        'Request_MultipleObjectsWithSameKeyValue' = 409
        'activityLimitReached'                    = 429
        'serviceNotAvailable'                     = 503
    }

    $rawMessage = ''
    if ($ErrorRecord.Exception) { $rawMessage = [string]$ErrorRecord.Exception.Message }

    $status = $null
    foreach ($prop in 'StatusCode', 'Response') {
        if ($null -ne $status) { break }
        try {
            if (-not (Test-HasProperty $ErrorRecord.Exception $prop)) { continue }
            $candidate = $ErrorRecord.Exception.$prop
            if ($null -eq $candidate) { continue }
            if ($prop -eq 'Response') {
                if (-not (Test-HasProperty $candidate 'StatusCode')) { continue }
                $candidate = $candidate.StatusCode
            }
            $status = [int]$candidate
        } catch { $status = $null }
    }

    $code    = $null
    $message = $rawMessage
    if ($ErrorRecord.ErrorDetails -and $ErrorRecord.ErrorDetails.Message) {
        $details = [string]$ErrorRecord.ErrorDetails.Message
        try {
            $parsed = $details | ConvertFrom-Json -ErrorAction Stop
            if (Test-HasProperty $parsed 'error') {
                if (Test-HasProperty $parsed.error 'code')    { $code    = [string]$parsed.error.code }
                if (Test-HasProperty $parsed.error 'message') { $message = [string]$parsed.error.message }
            }
        } catch {
            $message = $details
        }
    }

    if ($null -eq $status) {
        foreach ($pattern in 'status code does not indicate success:\s*(\d{3})',
                             '\((\d{3})\)',
                             '\bHTTP[/ ]?[\d.]*\s+(\d{3})\b',
                             '\bstatus(?:Code)?[:= ]+(\d{3})\b',
                             # Invoke-Graph throws "... failed [403 Code]: message". A caller that
                             # catches and re-parses that string must still recover the status.
                             'failed \[(\d{3})[\s\]]') {
            if ($rawMessage -match $pattern) { $status = [int]$Matches[1]; break }
        }
    }

    if ($null -eq $status -and $code -and $codeToStatus.ContainsKey($code)) {
        $status = $codeToStatus[$code]
    }

    [pscustomobject]@{ Status = $status; Code = $code; Message = $message }
}

function Invoke-Graph {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PATCH', 'PUT', 'DELETE')][string] $Method,
        [Parameter(Mandatory)][string] $Uri,
        $Body,
        [int]    $MaxAttempts = 6,
        [switch] $TolerateNotFound,
        [switch] $TolerateConflict,
        [switch] $RetryOnNotFound   # directory replication lag right after a create
    )

    if ($Uri -notmatch '^https?://') { $Uri = "$script:GraphRoot$Uri" }

    $json = $null
    if ($null -ne $Body) {
        $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 25 }
    }

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            $reqParams = @{
                Method      = $Method
                Uri         = $Uri
                Headers     = @{ 'OData-Version' = '4.0' }
                OutputType  = 'PSObject'
                ErrorAction = 'Stop'
            }
            if ($json) {
                $reqParams.Body        = $json
                $reqParams.ContentType = 'application/json'
            }
            return Invoke-MgGraphRequest @reqParams
        }
        catch {
            $info = Get-GraphErrorInfo -ErrorRecord $_

            if ($info.Status -eq 404 -and $TolerateNotFound -and -not $RetryOnNotFound) { return $null }
            if ($info.Status -eq 409 -and $TolerateConflict) { return $null }

            $transient = ($info.Status -in 429, 500, 502, 503, 504) -or
                         ($info.Status -eq 404 -and $RetryOnNotFound)

            if ($transient -and $attempt -lt $MaxAttempts) {
                $delay = [Math]::Min([Math]::Pow(2, $attempt), 30)
                Write-Verbose "Transient $($info.Status) on $Method $Uri - retry $attempt/$MaxAttempts in ${delay}s"
                Start-Sleep -Seconds $delay
                continue
            }

            if ($info.Status -eq 404 -and $TolerateNotFound) { return $null }
            throw "Graph $Method $Uri failed [$($info.Status) $($info.Code)]: $($info.Message)"
        }
    }
}

function Write-Step {
    param([int]$Number, [string]$Text)
    Write-Host ''
    Write-Host "=== Step $Number : $Text" -ForegroundColor Cyan
}

function ConvertTo-ODataLiteral {
    param([string] $Value)
    return ($Value -replace "'", "''")
}

# Secrets may be supplied as a SecureString, a plain string or a PSCredential. PowerShell will not
# coerce string -> SecureString on its own, and forcing every caller through ConvertTo-SecureString
# for a one-off run is friction with no security benefit once the value is already in the session.
# Plain strings do land in shell history and transcripts, so $env:A365_CLIENT_SECRET remains the
# better habit - hence the warning rather than silent acceptance.
function ConvertTo-SecureStringValue {
    [CmdletBinding()]
    [OutputType([securestring])]
    param(
        [object] $Value,
        [Parameter(Mandatory)][string] $Name
    )

    if ($null -eq $Value) { return $null }

    if ($Value -is [securestring]) {
        if ($Value.Length -eq 0) { throw "-$Name is an empty SecureString. Supply the actual secret value." }
        return $Value
    }
    if ($Value -is [pscredential]) { return $Value.Password }

    if ($Value -isnot [string]) {
        throw "-$Name must be a SecureString, a string or a PSCredential, but was [$($Value.GetType().FullName)]."
    }
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "-$Name was supplied but is empty. Omit the parameter, or pass the actual secret value."
    }

    # Built char by char so the plain text is never handed to ConvertTo-SecureString -AsPlainText.
    $secure = [securestring]::new()
    foreach ($char in $Value.ToCharArray()) { $secure.AppendChar($char) }
    $secure.MakeReadOnly()
    return $secure
}


# ---------------------------------------------------------------------------
# Azure Key Vault secret storage
# ---------------------------------------------------------------------------
# Key Vault is NOT reachable through Microsoft Graph, and this is not a gap in this
# script - it is architectural. Verified against a live tenant:
#   * every Graph path tried for key vaults or secrets answers 400 (no such segment);
#   * a valid Graph token presented to the vault data plane is rejected with 401,
#     while a vault-audience token on the same request gets 403 - an AUTHORIZATION
#     answer, which proves the 401 was about the audience and not the caller.
# So this block acquires a SECOND token for https://vault.azure.net and calls the
# vault's own REST API. Everything that CAN go through Graph still does.
$script:KeyVaultResource   = 'https://vault.azure.net'
$script:KeyVaultScope      = 'https://vault.azure.net/.default'
$script:KeyVaultApiVersion = '7.4'
$script:KeyVaultResult     = $null

function ConvertFrom-SecureStringValue {
    <#
      SecureString back to plain text, for the one moment a credential has to be placed on
      the wire. Kept as its own function so every such moment is greppable.
    #>
    param([object] $Value)
    if ($null -eq $Value) { return $null }
    if ($Value -isnot [securestring]) { return [string]$Value }
    return [Net.NetworkCredential]::new('', $Value).Password
}

function Resolve-KeyVaultUri {
    <#
      Accepts a bare vault name ("contoso-kv") or a full URI
      ("https://contoso-kv.vault.azure.net/") and returns the normalised base URI.
    #>
    param([Parameter(Mandatory)][string] $NameOrUri)

    $v = $NameOrUri.Trim()
    if ($v -match '^https://') {
        $u = $null
        if (-not [uri]::TryCreate($v, [UriKind]::Absolute, [ref]$u)) {
            throw "-KeyVaultName '$NameOrUri' looks like a URL but is not a valid absolute URI."
        }
        return ('https://' + $u.Host)
    }
    # A vault name is 3-24 chars, alphanumeric and hyphens, and cannot start with a digit
    # or contain consecutive hyphens. Checked here so a typo fails with a clear message
    # instead of an opaque DNS error later.
    if ($v -notmatch '^[A-Za-z][A-Za-z0-9-]{1,22}[A-Za-z0-9]$' -or $v -match '--') {
        throw "-KeyVaultName '$NameOrUri' is not a valid Key Vault name. Use 3-24 characters, letters, digits and single hyphens, starting with a letter; or pass the full https://<vault>.vault.azure.net URI."
    }
    return "https://$v.vault.azure.net"
}

function ConvertTo-KeyVaultSecretName {
    <#
      Key Vault secret names allow only 0-9, a-z, A-Z and '-'. Anything else in a display
      name has to be folded, and an empty or all-invalid result must fail loudly rather
      than silently writing to a name the caller did not intend.
    #>
    param([Parameter(Mandatory)][string] $Candidate)

    $n = [regex]::Replace($Candidate, '[^0-9a-zA-Z-]', '-')
    $n = [regex]::Replace($n, '-{2,}', '-').Trim('-')
    if ($n.Length -gt 127) { $n = $n.Substring(0, 127).Trim('-') }
    if ([string]::IsNullOrWhiteSpace($n)) {
        throw "Cannot derive a Key Vault secret name from '$Candidate'. Pass -KeyVaultSecretName explicitly."
    }
    return $n
}

function New-A365ClientAssertion {
    <#
      Builds the signed JWT that certificate credentials use in place of a client secret.
      Needed because the vault token is a SECOND token: the Graph SDK holds the certificate
      for its own connection, but will not mint a token for another audience.
    #>
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
        [Parameter(Mandatory)][string] $ClientId,
        [Parameter(Mandatory)][string] $TenantId
    )

    if (-not $Certificate.HasPrivateKey) {
        throw 'The certificate has no private key, so it cannot sign a client assertion for the Key Vault token.'
    }
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if (-not $rsa) { throw 'Only RSA certificates can sign a client assertion.' }

    $b64url = {
        param([byte[]] $Bytes)
        [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }
    $now    = [DateTimeOffset]::UtcNow
    $header = [ordered]@{
        alg = 'RS256'
        typ = 'JWT'
        x5t = (& $b64url $Certificate.GetCertHash())
    } | ConvertTo-Json -Compress
    $payload = [ordered]@{
        aud = "https://login.microsoftonline.com/$TenantId/v2.0"
        iss = $ClientId
        sub = $ClientId
        jti = [guid]::NewGuid().ToString()
        nbf = $now.ToUnixTimeSeconds()
        exp = $now.AddMinutes(10).ToUnixTimeSeconds()
    } | ConvertTo-Json -Compress

    $signingInput = (& $b64url ([Text.Encoding]::UTF8.GetBytes($header))) + '.' +
                    (& $b64url ([Text.Encoding]::UTF8.GetBytes($payload)))
    $sig = $rsa.SignData(
        [Text.Encoding]::ASCII.GetBytes($signingInput),
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    return $signingInput + '.' + (& $b64url $sig)
}

function Get-KeyVaultToken {
    <#
      Obtains a token for the Key Vault data plane, using the SAME credential the caller
      gave for Graph wherever that is possible.

      The one case that cannot work is -AccessToken: a Graph access token is issued for the
      Graph audience and the vault rejects it outright, and there is no way to exchange one
      for the other. That mode therefore requires -KeyVaultAccessToken.
    #>
    param(
        [Parameter(Mandatory)][string] $TenantId,
        [string] $AuthMode,
        [string] $ClientId,
        [object] $ClientSecret,
        [System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
        [string] $ManagedIdentityPrincipalId,
        [object] $ExplicitToken
    )

    $tokenUri = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"

    if ($ExplicitToken) {
        $sec = ConvertTo-SecureStringValue -Value $ExplicitToken -Name 'KeyVaultAccessToken'
        return [pscustomobject]@{ Token = (ConvertFrom-SecureStringValue $sec); Source = 'KeyVaultAccessToken' }
    }

    switch ($AuthMode) {
        'ClientSecret' {
            $sec  = ConvertTo-SecureStringValue -Value $ClientSecret -Name 'ClientSecret'
            $body = @{
                client_id     = $ClientId
                client_secret = (ConvertFrom-SecureStringValue $sec)
                scope         = $script:KeyVaultScope
                grant_type    = 'client_credentials'
            }
            # The body carries the client secret, so it is never handed to the logger.
            Write-A365LogGraphRequest -Method 'POST' -Uri $tokenUri -Body '{"grant_type":"client_credentials","scope":"https://vault.azure.net/.default","client_secret":"<redacted:secret>"}'
            $r = Invoke-RestMethod -Method POST -Uri $tokenUri -ContentType 'application/x-www-form-urlencoded' -Body $body -ErrorAction Stop
            Write-A365LogGraphResponse -Method 'POST' -Uri $tokenUri -Response '{"access_token":"<redacted:token>"}' -Status 200
            return [pscustomobject]@{ Token = [string]$r.access_token; Source = 'client_credentials' }
        }
        'Certificate' {
            if (-not $Certificate) {
                throw 'Certificate authentication was used for Graph, but the certificate object is not available to mint a Key Vault token. Pass -KeyVaultAccessToken, or use -CertificatePath / -Certificate so the private key is loaded in-process.'
            }
            $assertion = New-A365ClientAssertion -Certificate $Certificate -ClientId $ClientId -TenantId $TenantId
            $body = @{
                client_id             = $ClientId
                client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
                client_assertion      = $assertion
                scope                 = $script:KeyVaultScope
                grant_type            = 'client_credentials'
            }
            Write-A365LogGraphRequest -Method 'POST' -Uri $tokenUri -Body '{"grant_type":"client_credentials","scope":"https://vault.azure.net/.default","client_assertion":"<redacted:secret>"}'
            $r = Invoke-RestMethod -Method POST -Uri $tokenUri -ContentType 'application/x-www-form-urlencoded' -Body $body -ErrorAction Stop
            Write-A365LogGraphResponse -Method 'POST' -Uri $tokenUri -Response '{"access_token":"<redacted:token>"}' -Status 200
            return [pscustomobject]@{ Token = [string]$r.access_token; Source = 'client_assertion' }
        }
        'ManagedIdentity' {
            # IMDS, and the App Service / Functions variant, which use different headers.
            $q = "api-version=2018-02-01&resource=$([uri]::EscapeDataString($script:KeyVaultResource))"
            if ($ManagedIdentityPrincipalId) { $q += "&principal_id=$([uri]::EscapeDataString($ManagedIdentityPrincipalId))" }
            if ($env:IDENTITY_ENDPOINT -and $env:IDENTITY_HEADER) {
                $uri = "$($env:IDENTITY_ENDPOINT)?api-version=2019-08-01&resource=$([uri]::EscapeDataString($script:KeyVaultResource))"
                if ($ManagedIdentityPrincipalId) { $uri += "&principal_id=$([uri]::EscapeDataString($ManagedIdentityPrincipalId))" }
                $r = Invoke-RestMethod -Uri $uri -Headers @{ 'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER } -ErrorAction Stop
            }
            else {
                $r = Invoke-RestMethod -Uri "http://169.254.169.254/metadata/identity/oauth2/token?$q" `
                        -Headers @{ Metadata = 'true' } -TimeoutSec 10 -ErrorAction Stop
            }
            return [pscustomobject]@{ Token = [string]$r.access_token; Source = 'managed_identity' }
        }
        'Interactive' {
            # Connect-MgGraph yields a Graph token only. Rather than force a second sign-in
            # prompt mid-run, reuse a signed-in Azure session if one is present - the same
            # opt-in fallback this suite already uses for the Agent 365 Tools audience.
            $az = Get-Command az -ErrorAction SilentlyContinue
            if ($az) {
                try {
                    $raw = & az account get-access-token --scope $script:KeyVaultScope --tenant $TenantId -o json 2>$null
                    if ($LASTEXITCODE -eq 0 -and $raw) {
                        $p = ($raw | ConvertFrom-Json)
                        if ($p.accessToken) { return [pscustomobject]@{ Token = [string]$p.accessToken; Source = 'azure-cli' } }
                    }
                }
                catch { Write-Verbose "az token acquisition failed: $($_.Exception.Message)" }
            }
            if (Get-Module -ListAvailable -Name Az.Accounts) {
                try {
                    Import-Module Az.Accounts -ErrorAction Stop
                    if (Get-AzContext -ErrorAction SilentlyContinue) {
                        $t = Get-AzAccessToken -ResourceUrl $script:KeyVaultResource -ErrorAction Stop
                        $plain = if ($t.Token -is [securestring]) { ConvertFrom-SecureStringValue $t.Token } else { [string]$t.Token }
                        if ($plain) { return [pscustomobject]@{ Token = $plain; Source = 'az-powershell' } }
                    }
                }
                catch { Write-Verbose "Az.Accounts token acquisition failed: $($_.Exception.Message)" }
            }
            throw 'Interactive Graph sign-in cannot mint a Key Vault token: Connect-MgGraph issues Graph-audience tokens only. Sign in to Azure first ("az login" or "Connect-AzAccount"), or pass -KeyVaultAccessToken.'
        }
        'AccessToken' {
            throw '-AccessToken supplies a Microsoft Graph token, which the Key Vault data plane rejects (verified: HTTP 401). A token is audience-bound and cannot be exchanged. Pass -KeyVaultAccessToken with a token for https://vault.azure.net, or authenticate with -ClientSecret / -Certificate / -UseManagedIdentity so one can be obtained for you.'
        }
        default {
            throw "Cannot obtain a Key Vault token for authentication mode '$AuthMode'. Pass -KeyVaultAccessToken."
        }
    }
}

function Save-A365SecretToKeyVault {
    <#
      PUTs the secret to the vault data plane and reads it back. The read-back is not
      ceremony: a write that is accepted but not persisted has bitten this suite before on
      other APIs, and a client secret that only appears to be stored is worse than one that
      obviously failed, because the caller stops holding the only copy.
    #>
    param(
        [Parameter(Mandatory)][string] $VaultUri,
        [Parameter(Mandatory)][string] $SecretName,
        [Parameter(Mandatory)][object] $SecretValue,
        [Parameter(Mandatory)][string] $Token,
        [string]   $ContentType,
        [hashtable] $Tags,
        [object]   $ExpiresOn
    )

    $plain = if ($SecretValue -is [securestring]) { ConvertFrom-SecureStringValue $SecretValue } else { [string]$SecretValue }
    if ([string]::IsNullOrEmpty($plain)) { throw 'Refusing to write an empty value to Key Vault.' }

    $uri  = "$VaultUri/secrets/$([uri]::EscapeDataString($SecretName))?api-version=$script:KeyVaultApiVersion"
    $body = [ordered]@{ value = $plain }
    if ($ContentType) { $body.contentType = $ContentType }
    if ($Tags -and $Tags.Count -gt 0) { $body.tags = $Tags }
    # PowerShell unwraps a [Nullable[T]] parameter to T, so there is no .Value to read here.
    $expOffset = $null
    if ($ExpiresOn -is [DateTimeOffset]) { $expOffset = [DateTimeOffset]$ExpiresOn }
    elseif ($ExpiresOn -is [datetime])   { $expOffset = [DateTimeOffset]([datetime]$ExpiresOn) }
    if ($null -ne $expOffset) { $body.attributes = [ordered]@{ exp = $expOffset.ToUnixTimeSeconds() } }

    # The body's "value" IS the secret, and "value" is far too generic to add to the
    # redaction list - every Graph collection response is {"value":[...]}. So the real body
    # is never passed to the logger; a redacted stand-in is logged instead.
    $logBody = [ordered]@{}
    foreach ($k in $body.Keys) { $logBody[$k] = $body[$k] }
    $logBody['value'] = '<redacted:secret>'
    $headers = @{ Authorization = "Bearer $Token"; 'Content-Type' = 'application/json' }

    Write-A365LogGraphRequest -Method 'PUT' -Uri $uri -Body ($logBody | ConvertTo-Json -Depth 6 -Compress)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = Invoke-RestMethod -Method PUT -Uri $uri -Headers $headers `
                    -Body ($body | ConvertTo-Json -Depth 6) -ErrorAction Stop
        $sw.Stop()
    }
    catch {
        $sw.Stop()
        $status = 0
        if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
            try { $status = [int]$_.Exception.Response.StatusCode } catch { $status = 0 }
        }
        Write-A365LogGraphResponse -Method 'PUT' -Uri $uri -Status $status -DurationMs $sw.ElapsedMilliseconds `
            -AsFailure -ErrorText (Protect-LogText $_.Exception.Message)
        throw (New-Object System.Management.Automation.RuntimeException(
            "Key Vault write failed with HTTP $status. $($_.Exception.Message)"))
    }

    # Read back the METADATA only. Asking for the value would pull the secret into the
    # response, and with it into any transcript, for no extra proof.
    $verified = $false
    $version  = $null
    if ($resp -and $resp.id) {
        $version = ($resp.id -split '/')[-1]
        try {
            $check = Invoke-RestMethod -Method GET -Headers @{ Authorization = "Bearer $Token" } `
                        -Uri "$VaultUri/secrets/$([uri]::EscapeDataString($SecretName))/versions?api-version=$script:KeyVaultApiVersion" -ErrorAction Stop
            $verified = @($check.value | Where-Object { ($_.id -split '/')[-1] -eq $version }).Count -gt 0
        }
        catch { Write-Verbose "Key Vault read-back failed: $($_.Exception.Message)" }
    }
    Write-A365LogGraphResponse -Method 'PUT' -Uri $uri -Status 200 -DurationMs $sw.ElapsedMilliseconds `
        -Response ("{`"id`":`"$($resp.id)`",`"verified`":$($verified.ToString().ToLower())}")

    return [pscustomobject]@{
        VaultUri   = $VaultUri
        SecretName = $SecretName
        Version    = $version
        Id         = [string]$resp.id
        Verified   = $verified
        ExpiresOn  = $(if ($null -ne $expOffset) { $expOffset.ToString('o') } else { $null })
    }
}

function Write-KeyVaultActionRequired {
    <#
      Turns a vault failure into the specific thing an operator has to do. The failure modes
      are distinguishable and the remedies are completely different, so they are named
      separately rather than reported as one generic error.
    #>
    param(
        [Parameter(Mandatory)][string] $VaultUri,
        [string] $SecretName,
        [string] $Message,
        [int]    $Status,
        [string] $CallerDisplay
    )

    Write-Host ''
    Write-Host 'ACTION REQUIRED - the secret was NOT saved to Key Vault.' -ForegroundColor Red
    Write-Host ("  vault  : {0}" -f $VaultUri) -ForegroundColor Gray
    if ($SecretName) { Write-Host ("  secret : {0}" -f $SecretName) -ForegroundColor Gray }
    if ($Message)    { Write-Host ("  error  : {0}" -f $Message) -ForegroundColor DarkGray }

    switch ($Status) {
        401 {
            Write-Host '  The vault rejected the token. A Microsoft Graph token does not work here -' -ForegroundColor Yellow
            Write-Host '  Key Vault is a separate audience (https://vault.azure.net).' -ForegroundColor Yellow
            Write-Host '  Pass -KeyVaultAccessToken with a vault-audience token, or authenticate with' -ForegroundColor Gray
            Write-Host '  -ClientSecret / -Certificate / -UseManagedIdentity so one can be obtained.' -ForegroundColor Gray
        }
        403 {
            $who = if ($CallerDisplay) { $CallerDisplay } else { '<the principal this script authenticated as>' }
            Write-Host '  Authenticated, but not authorized on the vault DATA plane.' -ForegroundColor Yellow
            Write-Host '  Owner and Contributor are NOT enough: they grant management-plane rights only' -ForegroundColor Yellow
            Write-Host '  and carry no dataActions, so they cannot read or write secrets.' -ForegroundColor Yellow
            Write-Host '  Grant the data-plane role (RBAC vaults):' -ForegroundColor Gray
            Write-Host ("    az role assignment create --role 'Key Vault Secrets Officer' ``") -ForegroundColor Cyan
            Write-Host ("        --assignee-object-id {0} --assignee-principal-type ServicePrincipal ``" -f $who) -ForegroundColor Cyan
            Write-Host ("        --scope <vault resource id>") -ForegroundColor Cyan
            Write-Host '  If the vault still uses access policies instead of RBAC, grant secret set/get there.' -ForegroundColor Gray
        }
        404 {
            Write-Host '  The vault or the path was not found. Check -KeyVaultName, and that the vault' -ForegroundColor Yellow
            Write-Host '  exists in this tenant and is not soft-deleted.' -ForegroundColor Gray
        }
        default {
            Write-Host '  The secret is still shown above (or in the report) - capture it now; Entra will' -ForegroundColor Yellow
            Write-Host '  not display it again.' -ForegroundColor Yellow
        }
    }
}

function Connect-GraphSession {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $TenantId,
        [string]       $ClientId,
        [object]       $ClientSecret,
        [string]       $CertificateThumbprint,
        [switch]       $UseManagedIdentity,
        [object]       $AccessToken,
        [switch]       $Interactive,
        [string[]]     $DelegatedScope = @()
    )

    if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Authentication)) {
        throw 'Microsoft.Graph.Authentication is not installed. Run: Install-Module Microsoft.Graph.Authentication -Scope CurrentUser'
    }
    # The module's own Update-TypeData calls honour $WhatIfPreference, so under -WhatIf they emit a
# dozen "What if: Performing the operation Update TypeData" lines before any real output.
# Import-Module has no -WhatIf to suppress that, so drop the preference for the import only.
$previousWhatIfPreference = $WhatIfPreference
try {
    $WhatIfPreference = $false
    Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
}
finally {
    $WhatIfPreference = $previousWhatIfPreference
}

    # Accept plain strings as well as SecureStrings, and warn about the trade-off once.
    $secretWasPlainText = $ClientSecret -is [string]
    $ClientSecret = ConvertTo-SecureStringValue -Value $ClientSecret -Name 'ClientSecret'
    $AccessToken  = ConvertTo-SecureStringValue -Value $AccessToken  -Name 'AccessToken'

    # Keeps the secret out of command lines, shell history and transcripts.
    if ((-not $ClientSecret) -and $env:A365_CLIENT_SECRET) {
        $ClientSecret = ConvertTo-SecureStringValue -Value $env:A365_CLIENT_SECRET -Name 'ClientSecret'
        Write-Verbose 'Client secret read from $env:A365_CLIENT_SECRET.'
    }
    elseif ($secretWasPlainText) {
        Write-Warning 'A plain-text -ClientSecret was passed on the command line, where it is visible to shell history and transcripts. Prefer $env:A365_CLIENT_SECRET or a SecureString.'
    }

    $modes = @()
    if ($Interactive)           { $modes += 'Interactive' }
    if ($AccessToken)           { $modes += 'AccessToken' }
    if ($UseManagedIdentity)    { $modes += 'ManagedIdentity' }
    if ($CertificateThumbprint) { $modes += 'Certificate' }
    if ($ClientSecret)          { $modes += 'ClientSecret' }

    if ($modes.Count -gt 1) {
        throw "Conflicting authentication options ($($modes -join ', ')). Supply exactly one of -ClientSecret, -AuthCertificateThumbprint, -UseManagedIdentity, -AccessToken or -Interactive."
    }
    if ($modes.Count -eq 0) {
        throw 'No authentication method was specified. This script is normally a one-time bootstrap, so -Interactive is the usual choice. For unattended use pass -ClientId with -ClientSecret or -AuthCertificateThumbprint, or use -UseManagedIdentity / -AccessToken.'
    }

    $mode = $modes[0]
    if (($mode -in @('ClientSecret', 'Certificate')) -and (-not $ClientId)) {
        throw "-ClientId is required for $mode authentication."
    }

    $connect = @{ NoWelcome = $true; ErrorAction = 'Stop' }
    switch ($mode) {
        'ClientSecret'    { $connect.TenantId = $TenantId; $connect.ClientSecretCredential = [pscredential]::new($ClientId, $ClientSecret) }
        'Certificate'     { $connect.TenantId = $TenantId; $connect.ClientId = $ClientId; $connect.CertificateThumbprint = $CertificateThumbprint }
        'ManagedIdentity' { $connect.Identity = $true; if ($ClientId) { $connect.ClientId = $ClientId } }
        'AccessToken'     { $connect.AccessToken = $AccessToken }
        'Interactive'     {
            $connect.TenantId = $TenantId
            if ($ClientId)                   { $connect.ClientId = $ClientId }
            if ($DelegatedScope.Count -gt 0) { $connect.Scopes   = $DelegatedScope }
        }
    }

    Connect-MgGraph @connect

    $mg = Get-MgContext
    if (-not $mg) { throw 'Connect-MgGraph did not establish a Graph context.' }

    $authType   = if (Test-HasProperty $mg 'AuthType') { [string]$mg.AuthType } else { '' }
    $ctxClient  = if (Test-HasProperty $mg 'ClientId') { [string]$mg.ClientId } else { $ClientId }
    $ctxAccount = if (Test-HasProperty $mg 'Account')  { [string]$mg.Account }  else { '' }
    $ctxTenant  = $TenantId
    if ((Test-HasProperty $mg 'TenantId') -and $mg.TenantId) { $ctxTenant = [string]$mg.TenantId }

    $isAppOnly = ($authType -eq 'AppOnly')
    if (($mode -in @('ClientSecret', 'Certificate', 'ManagedIdentity')) -and (-not $isAppOnly)) {
        throw "$mode authentication did not yield an app-only token (Get-MgContext reports AuthType '$authType')."
    }

    if ($isAppOnly) { Write-Host "  Connected app-only as $ctxClient in tenant $ctxTenant [$mode]" -ForegroundColor Green }
    else            { Write-Host "  Connected as $ctxAccount in tenant $ctxTenant [delegated, $mode]" -ForegroundColor Green }

    # The Key Vault token is a SECOND token and has to be minted from the same credential.
    # This script binds certificates by thumbprint only, so resolve the certificate from the
    # store for that case; a miss is not fatal here because only a Key Vault write needs it.
    $ctxCertificate = if ($CertificateThumbprint) {
        @(
            "Cert:\CurrentUser\My\$CertificateThumbprint",
            "Cert:\LocalMachine\My\$CertificateThumbprint"
        ) | ForEach-Object { Get-Item -LiteralPath $_ -ErrorAction SilentlyContinue } |
          Select-Object -First 1
    } else { $null }

    [pscustomobject]@{
        Mode = $mode; IsAppOnly = $isAppOnly; AuthType = $authType
        TenantId = $ctxTenant; ClientId = $ctxClient; Account = $ctxAccount
        Certificate = $ctxCertificate
    }
}

# ---------------------------------------------------------------------------
# Permission sets - kept in sync with the $appRoles list inside each script
# ---------------------------------------------------------------------------

$script:RoleSets = @{
    Blueprint = @(
        'AgentIdentityBlueprint.Create'
        'AgentIdentityBlueprint.Read.All'
        'AgentIdentityBlueprint.ReadWrite.All'
        'AgentIdentityBlueprint.AddRemoveCreds.All'
        'AgentIdentityBlueprintPrincipal.Create'
        'AgentIdentityBlueprintPrincipal.Read.All'
        # Owners on the blueprint PRINCIPAL. Verified live: Application.ReadWrite.All does NOT
        # authorize POST /servicePrincipals/{id}/microsoft.graph.agentIdentityBlueprintPrincipal/
        # owners/$ref - only this role does. Without it that call returns a bare 403
        # Authorization_RequestDenied that names no permission.
        'AgentIdentityBlueprintPrincipal.ReadWrite.All'
        'Application.Read.All'
        'User.Read.All'
        'Group.Read.All'
        # Owners on the blueprint APPLICATION, which is governed separately from the principal.
        'Application.ReadWrite.All'
        'Directory.Read.All'
    )
    AgentIdentity = @(
        'AgentIdentity.Create.All'
        'AgentIdentity.Read.All'
        'AgentIdentity.ReadWrite.All'
        'AgentIdentityBlueprint.Read.All'
        'Application.Read.All'
        'User.Read.All'
        'Group.Read.All'
        # Same as above: required only to assign owners.
        'Application.ReadWrite.All'
        'Directory.Read.All'
        # Custom security attributes are gated entirely separately: no Application.* or
        # Directory.* role reaches them, and a Global Administrator does not hold them either.
        # Verified live - without these, -CustomSecurityAttribute fails with a bare 403.
        'CustomSecAttributeAssignment.ReadWrite.All'  # assign them
        'CustomSecAttributeDefinition.Read.All'       # validate them before assigning
    )
    Registration = @(
        # Both registration permissions ARE published as application roles on Microsoft Graph
        # (AgentRegistration.ReadWrite.All 39fb8c64-..., AgentInstance.ReadWrite.All 07abdd95-...),
        # so app-only registration is a supported path and these are granted, not just requested.
        'AgentRegistration.ReadWrite.All'   # POST /beta/copilot/agentRegistrations
        'AgentInstance.ReadWrite.All'       # POST /beta/agentRegistry/agentInstances
        'AgentIdentity.Read.All'
        # Resolving -Owner (UPN / mail / display name) to the user object ids that go into
        # the registration's ownerIds and createdBy, which app-only cannot get from /me.
        'User.Read.All'
    )
    # The orchestrator's agent user phase: the agent user, its licence, per-identity consent.
    AgentUser = @(
        # POST /beta/users with @odata.type #microsoft.graph.agentUser. This is the specific
        # role the call is authorized by - User.ReadWrite.All is NOT enough and the refusal is
        # a bare "Insufficient privileges" that names no permission.
        'AgentIdUser.ReadWrite.All'
        'User.ReadWrite.All'                     # manager, usageLocation, assignLicense
        'User.Read.All'
        'Directory.Read.All'                     # verified domains, resolve the manager
        'Organization.Read.All'                  # /subscribedSkus licence pre-flight
        'DelegatedPermissionGrant.ReadWrite.All' # per-identity delegated consent
        'AppRoleAssignment.ReadWrite.All'        # per-identity app role assignment
        'Application.Read.All'                   # resolve resource service principals
        'AgentIdentity.Read.All'
    )
}

# Also requested as delegated scopes, so the same app can be used as the client of an interactive
# registration run. The application roles above are what an unattended run actually uses.
$script:RegistrationDelegatedScopes = @(
    'AgentRegistration.ReadWrite.All'   # POST /beta/copilot/agentRegistrations
    'AgentInstance.ReadWrite.All'       # POST /beta/agentRegistry/agentInstances
    'User.Read'                         # GET  /v1.0/me
    'User.ReadBasic.All'                # GET  /users/{upn} -> owner object ids from -Owner
)

# Custom security attributes are gated separately from every other directory permission, and the
# gate applies to BOTH call shapes. New-A365AgentIdentity.ps1 asks for these as delegated scopes
# whenever -CustomSecurityAttribute is used, so an interactive run using this app as its client
# needs them declared here - the application roles alone do not cover a delegated caller.
$script:CustomSecAttributeDelegatedScopes = @(
    'CustomSecAttributeAssignment.ReadWrite.All'  # assign attributes to the agent identity
    'CustomSecAttributeDefinition.Read.All'       # validate sets and allowed values first
)

# ---------------------------------------------------------------------------
# Step 1 - connect
# ---------------------------------------------------------------------------
Write-Step 1 'Connecting to Microsoft Graph'

$connectArgs = @{
    TenantId       = $TenantId
    DelegatedScope = @('Application.ReadWrite.All', 'AppRoleAssignment.ReadWrite.All', 'Directory.Read.All')
}
if ($PSBoundParameters.ContainsKey('ClientId'))                  { $connectArgs.ClientId              = $ClientId }
if ($PSBoundParameters.ContainsKey('ClientSecret'))              { $connectArgs.ClientSecret          = $ClientSecret }
if ($PSBoundParameters.ContainsKey('AuthCertificateThumbprint')) { $connectArgs.CertificateThumbprint = $AuthCertificateThumbprint }
if ($PSBoundParameters.ContainsKey('UseManagedIdentity'))        { $connectArgs.UseManagedIdentity    = $UseManagedIdentity }
if ($PSBoundParameters.ContainsKey('AccessToken'))               { $connectArgs.AccessToken           = $AccessToken }
if ($PSBoundParameters.ContainsKey('Interactive'))               { $connectArgs.Interactive           = $Interactive }

$ctx = Connect-GraphSession @connectArgs

# ---------------------------------------------------------------------------
# Step 2 - resolve the requested permissions against what Graph actually publishes
# ---------------------------------------------------------------------------
Write-Step 2 'Resolving Microsoft Graph permissions'

$requestedRoles = if ($Scenario -eq 'All') {
    @($script:RoleSets.Values | ForEach-Object { $_ }) | Sort-Object -Unique
}
else {
    @($script:RoleSets[$Scenario])
}
if ($AdditionalAppRole.Count -gt 0) { $requestedRoles = @($requestedRoles + $AdditionalAppRole | Sort-Object -Unique) }
if ($SkipAppRole.Count -gt 0)       { $requestedRoles = @($requestedRoles | Where-Object { $SkipAppRole -notcontains $_ }) }

$graphSp = Invoke-Graph -Method GET `
    -Uri "/servicePrincipals(appId='$script:MicrosoftGraphAppId')?`$select=id,appRoles,oauth2PermissionScopes"
if (-not (Test-HasProperty $graphSp 'id')) { throw 'Could not resolve the Microsoft Graph service principal.' }
$graphSpId = [string]$graphSp.id

$roleIdByName = @{}
if (Test-HasProperty $graphSp 'appRoles') {
    foreach ($role in @($graphSp.appRoles)) {
        if (-not (Test-HasProperty $role 'value')) { continue }
        # Only Application-type roles can be granted to a service principal.
        $memberTypes = if (Test-HasProperty $role 'allowedMemberTypes') { @($role.allowedMemberTypes) } else { @() }
        if ($memberTypes -notcontains 'Application') { continue }
        $roleIdByName[[string]$role.value] = [string]$role.id
    }
}

$scopeIdByName = @{}
if (Test-HasProperty $graphSp 'oauth2PermissionScopes') {
    foreach ($scope in @($graphSp.oauth2PermissionScopes)) {
        if (Test-HasProperty $scope 'value') { $scopeIdByName[[string]$scope.value] = [string]$scope.id }
    }
}

$resolved = @()
$missing  = @()
foreach ($name in $requestedRoles) {
    if ($roleIdByName.ContainsKey($name)) {
        $resolved += [pscustomobject]@{ Name = $name; Id = $roleIdByName[$name] }
    }
    else {
        $missing += $name
    }
}

Write-Host "  Scenario '$Scenario' requires $($requestedRoles.Count) application permission(s)." -ForegroundColor Gray
Write-Host "  Resolved $($resolved.Count) against this tenant's Microsoft Graph service principal." -ForegroundColor Green

if ($missing.Count -gt 0) {
    Write-Warning "These permissions are not published as APPLICATION roles in this tenant: $($missing -join ', ')"
    foreach ($name in $missing) {
        if ($scopeIdByName.ContainsKey($name)) {
            Write-Warning "  '$name' exists only as a DELEGATED scope here - it cannot be granted app-only."
        }
    }
    Write-Warning 'Agent 365 permissions are preview surface; a tenant that is not enrolled will not publish them. Enrol the tenant, or re-run with -SkipAppRole to exclude them.'
}
if ($resolved.Count -eq 0) { throw 'None of the requested permissions could be resolved. Nothing to grant.' }

$delegatedToRequest = @()
$wantRegistrationScopes = $AddRegistrationDelegatedScopes -and ($Scenario -in 'Registration', 'All')
if ($wantRegistrationScopes) {
    foreach ($name in $script:RegistrationDelegatedScopes) {
        if ($scopeIdByName.ContainsKey($name)) {
            $delegatedToRequest += [pscustomobject]@{ Name = $name; Id = $scopeIdByName[$name] }
        }
        else {
            Write-Warning "Delegated scope '$name' is not published in this tenant; skipping it. Interactive agent registration will not work until the tenant is enrolled in the preview."
        }
    }
    if ($delegatedToRequest.Count -gt 0) {
        Write-Host "  Will request $($delegatedToRequest.Count) delegated scope(s) for interactive agent registration." -ForegroundColor Gray
    }
}

# The custom security attribute scopes ride with the AgentIdentity scenario, because that is the
# phase that assigns them. Declared for the delegated case as well as the application case: an
# interactive run gets its access from the scope, not from the app role.
if ($Scenario -in 'AgentIdentity', 'All') {
    $csaAdded = 0
    foreach ($name in $script:CustomSecAttributeDelegatedScopes) {
        if (-not $scopeIdByName.ContainsKey($name)) {
            Write-Warning "Delegated scope '$name' is not published in this tenant; skipping it. Assigning custom security attributes from an INTERACTIVE run will not work without it."
            continue
        }
        if (@($delegatedToRequest | Where-Object { $_.Name -eq $name }).Count -gt 0) { continue }
        $delegatedToRequest += [pscustomobject]@{ Name = $name; Id = $scopeIdByName[$name] }
        $csaAdded++
    }
    if ($csaAdded -gt 0) {
        Write-Host "  Will request $csaAdded delegated scope(s) for custom security attributes." -ForegroundColor Gray
    }
}

# ---------------------------------------------------------------------------
# Step 3 - create or reuse the application registration
# ---------------------------------------------------------------------------
Write-Step 3 'Creating the application registration'

$application = $null
if ($AppId) {
    $application = Invoke-Graph -Method GET -Uri "/applications(appId='$AppId')?`$select=id,appId,displayName" -TolerateNotFound
    if (-not $application) { throw "No application found with appId '$AppId'." }
}
else {
    $filter = "displayName eq '$(ConvertTo-ODataLiteral $DisplayName)'"
    $found  = Invoke-Graph -Method GET -Uri "/applications?`$filter=$([uri]::EscapeDataString($filter))&`$select=id,appId,displayName"
    $candidates = @()
    if (Test-HasProperty $found 'value') { $candidates = @($found.value) }

    if ($candidates.Count -gt 1) {
        throw "$($candidates.Count) applications are named '$DisplayName'. Disambiguate with -AppId."
    }
    if ($candidates.Count -eq 1) { $application = $candidates[0] }
}

if ($application) {
    Write-Host "  Reusing existing application '$($application.displayName)' (appId $($application.appId))" -ForegroundColor Green
}
else {
    $body = @{
        displayName    = $DisplayName
        signInAudience = 'AzureADMyOrg'
        notes          = 'Runs the Agent 365 Graph provisioning scripts unattended.'
    }
    if ($PSCmdlet.ShouldProcess($DisplayName, 'POST /applications')) {
        $application = Invoke-Graph -Method POST -Uri '/applications' -Body $body
        Write-Host "  Created application '$DisplayName' (appId $($application.appId))" -ForegroundColor Green
    }
    else {
        Write-Host '  [WhatIf] would create the application; later steps are skipped.' -ForegroundColor Yellow
        return
    }
}

$applicationObjectId = [string]$application.id
$applicationAppId    = [string]$application.appId

# Request the delegated scopes on the app object so an admin can consent to them in one action.
if ($delegatedToRequest.Count -gt 0) {
    $current = Invoke-Graph -Method GET -Uri "/applications/$applicationObjectId`?`$select=requiredResourceAccess"
    $existingAccess = @()
    if (Test-HasProperty $current 'requiredResourceAccess') { $existingAccess = @($current.requiredResourceAccess) }

    $graphEntry = $existingAccess | Where-Object { $_.resourceAppId -eq $script:MicrosoftGraphAppId } | Select-Object -First 1
    $existingIds = @()
    if ($graphEntry -and (Test-HasProperty $graphEntry 'resourceAccess')) {
        $existingIds = @($graphEntry.resourceAccess | ForEach-Object { [string]$_.id })
    }

    $toAdd = @($delegatedToRequest | Where-Object { $existingIds -notcontains $_.Id })
    if ($toAdd.Count -gt 0) {
        $resourceAccess = @()
        if ($graphEntry -and (Test-HasProperty $graphEntry 'resourceAccess')) {
            foreach ($ra in @($graphEntry.resourceAccess)) {
                $resourceAccess += @{ id = [string]$ra.id; type = [string]$ra.type }
            }
        }
        foreach ($scope in $toAdd) { $resourceAccess += @{ id = $scope.Id; type = 'Scope' } }

        $others = @($existingAccess | Where-Object { $_.resourceAppId -ne $script:MicrosoftGraphAppId } | ForEach-Object {
            @{ resourceAppId = [string]$_.resourceAppId
               resourceAccess = @($_.resourceAccess | ForEach-Object { @{ id = [string]$_.id; type = [string]$_.type } }) }
        })
        $payload = @{ requiredResourceAccess = @($others + @{ resourceAppId = $script:MicrosoftGraphAppId; resourceAccess = $resourceAccess }) }

        if ($PSCmdlet.ShouldProcess($DisplayName, "PATCH requiredResourceAccess (+$($toAdd.Count) delegated scope(s))")) {
            Invoke-Graph -Method PATCH -Uri "/applications/$applicationObjectId" -Body $payload | Out-Null
            Write-Host "  Requested delegated scope(s): $(($toAdd | ForEach-Object { $_.Name }) -join ', ')" -ForegroundColor Green
        }
    }
    else {
        Write-Host '  Delegated scopes already requested on the application.' -ForegroundColor Gray
    }
}

# ---------------------------------------------------------------------------
# Step 4 - create or reuse the service principal
# ---------------------------------------------------------------------------
Write-Step 4 'Creating the service principal'

# App role grants attach to the service principal, not the application object.
$servicePrincipal = Invoke-Graph -Method GET -Uri "/servicePrincipals(appId='$applicationAppId')?`$select=id,appId,displayName" -TolerateNotFound

if ($servicePrincipal) {
    Write-Host "  Reusing existing service principal ($($servicePrincipal.id))" -ForegroundColor Green
}
elseif ($PSCmdlet.ShouldProcess($applicationAppId, 'POST /servicePrincipals')) {
    # Directory replication can lag behind the application create, so tolerate a transient 404.
    $servicePrincipal = Invoke-Graph -Method POST -Uri '/servicePrincipals' -Body @{ appId = $applicationAppId } -RetryOnNotFound
    Write-Host "  Created service principal ($($servicePrincipal.id))" -ForegroundColor Green
}
else {
    Write-Host '  [WhatIf] would create the service principal; later steps are skipped.' -ForegroundColor Yellow
    return
}
$servicePrincipalId = [string]$servicePrincipal.id

# ---------------------------------------------------------------------------
# Step 5 - credentials
# ---------------------------------------------------------------------------
Write-Step 5 'Adding credentials'

$plainSecret = $null

if ($NewClientSecret) {
    $body = @{ passwordCredential = @{
        displayName = "a365-automation-$([DateTime]::UtcNow.ToString('yyyyMMdd'))"
        endDateTime = [DateTime]::UtcNow.AddMonths($SecretValidityMonths).ToString('o')
    } }
    if ($PSCmdlet.ShouldProcess($DisplayName, 'POST /applications/{id}/addPassword')) {
        $created = Invoke-Graph -Method POST -Uri "/applications/$applicationObjectId/addPassword" -Body $body
        if (Test-HasProperty $created 'secretText') {
            $plainSecret = [string]$created.secretText
            Write-Host "  Client secret created, valid for $SecretValidityMonths month(s)." -ForegroundColor Green
        }
        else {
            Write-Warning '  addPassword succeeded but returned no secretText.'
        }
    }
}

if ($CertificateThumbprint -or $CertificatePath) {
    $cert = $null
    if ($CertificatePath) {
        if (-not (Test-Path -LiteralPath $CertificatePath)) { throw "Certificate file not found: $CertificatePath" }
        $resolvedPath = (Resolve-Path -LiteralPath $CertificatePath).ProviderPath
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($resolvedPath)
    }
    else {
        $cert = Get-ChildItem -Path 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My' -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $CertificateThumbprint } | Select-Object -First 1
        if (-not $cert) { throw "No certificate with thumbprint '$CertificateThumbprint' was found in CurrentUser\My or LocalMachine\My." }
    }

    $existing = Invoke-Graph -Method GET -Uri "/applications/$applicationObjectId`?`$select=keyCredentials"
    $keyCredentials = @()
    if (Test-HasProperty $existing 'keyCredentials') { $keyCredentials = @($existing.keyCredentials) }

    $thumb = $cert.Thumbprint
    $alreadyPresent = @($keyCredentials | Where-Object {
        (Test-HasProperty $_ 'customKeyIdentifier') -and $_.customKeyIdentifier -and
        ([Convert]::ToHexString([Convert]::FromBase64String([string]$_.customKeyIdentifier)) -eq $thumb)
    }).Count -gt 0

    if ($alreadyPresent) {
        Write-Host "  Certificate $thumb is already registered." -ForegroundColor Gray
    }
    else {
        $rebuilt = @()
        foreach ($kc in $keyCredentials) {
            $rebuilt += @{
                type  = [string]$kc.type
                usage = [string]$kc.usage
                key   = [string]$kc.key
            }
        }
        $rebuilt += @{
            type  = 'AsymmetricX509Cert'
            usage = 'Verify'
            key   = [Convert]::ToBase64String($cert.GetRawCertData())
            displayName = "CN=$($cert.GetNameInfo('SimpleName', $false))"
        }
        if ($PSCmdlet.ShouldProcess($DisplayName, "PATCH keyCredentials (+$thumb)")) {
            Invoke-Graph -Method PATCH -Uri "/applications/$applicationObjectId" -Body @{ keyCredentials = $rebuilt } | Out-Null
            Write-Host "  Certificate $thumb registered." -ForegroundColor Green
        }
    }
}

foreach ($fic in $FederatedCredential) {
    foreach ($required in 'Name', 'Issuer', 'Subject') {
        if (-not $fic.ContainsKey($required) -or [string]::IsNullOrWhiteSpace([string]$fic[$required])) {
            throw "-FederatedCredential entries require a non-empty '$required' key."
        }
    }
    $ficName = [string]$fic['Name']

    $existing = Invoke-Graph -Method GET -Uri "/applications/$applicationObjectId/federatedIdentityCredentials" -TolerateNotFound
    $present = $false
    if ($existing -and (Test-HasProperty $existing 'value')) {
        $present = @($existing.value | Where-Object { [string]$_.name -eq $ficName }).Count -gt 0
    }

    if ($present) {
        Write-Host "  Federated credential '$ficName' already exists." -ForegroundColor Gray
        continue
    }

    $body = @{
        name      = $ficName
        issuer    = [string]$fic['Issuer']
        subject   = [string]$fic['Subject']
        audiences = @(if ($fic.ContainsKey('Audience')) { [string]$fic['Audience'] } else { 'api://AzureADTokenExchange' })
    }
    if ($fic.ContainsKey('Description')) { $body['description'] = [string]$fic['Description'] }

    if ($PSCmdlet.ShouldProcess($ficName, 'POST /applications/{id}/federatedIdentityCredentials')) {
        Invoke-Graph -Method POST -Uri "/applications/$applicationObjectId/federatedIdentityCredentials" -Body $body | Out-Null
        Write-Host "  Federated credential '$ficName' created." -ForegroundColor Green
    }
}

if (-not ($NewClientSecret -or $CertificateThumbprint -or $CertificatePath -or $FederatedCredential.Count)) {
    Write-Warning 'No credential was added. The application cannot authenticate until you add one (-NewClientSecret, -CertificateThumbprint/-CertificatePath or -FederatedCredential).'
}

# ---------------------------------------------------------------------------
# Step 6 - grant the application permissions (this IS admin consent)
# ---------------------------------------------------------------------------
Write-Step 6 'Granting application permissions'

$grantedNow  = @()
$alreadyHeld = @()
$failedGrant = @()

if ($SkipGrant) {
    Write-Host '  Skipped by -SkipGrant.' -ForegroundColor Yellow
}
else {
    $assigned = Invoke-Graph -Method GET `
        -Uri "/servicePrincipals/$servicePrincipalId/appRoleAssignments?`$select=appRoleId,resourceId&`$top=999" `
        -TolerateNotFound
    $heldIds = @()
    if ($assigned -and (Test-HasProperty $assigned 'value')) {
        $heldIds = @($assigned.value | ForEach-Object { [string]$_.appRoleId })
    }

    foreach ($role in $resolved) {
        if ($heldIds -contains $role.Id) {
            $alreadyHeld += $role.Name
            Write-Host "  = $($role.Name)" -ForegroundColor Gray
            continue
        }

        $body = @{
            principalId = $servicePrincipalId
            resourceId  = $graphSpId
            appRoleId   = $role.Id
        }
        if (-not $PSCmdlet.ShouldProcess($role.Name, 'POST appRoleAssignedTo')) { continue }

        try {
            # A concurrent grant surfaces as 409; that is success for our purposes.
            $result = Invoke-Graph -Method POST -Uri "/servicePrincipals/$graphSpId/appRoleAssignedTo" -Body $body -TolerateConflict
            if ($null -eq $result) { $alreadyHeld += $role.Name; Write-Host "  = $($role.Name)" -ForegroundColor Gray }
            else                   { $grantedNow  += $role.Name; Write-Host "  + $($role.Name)" -ForegroundColor Green }
        }
        catch {
            $failedGrant += [pscustomobject]@{ Name = $role.Name; Error = $_.Exception.Message }
            Write-Warning "  ! $($role.Name): $($_.Exception.Message)"
        }
    }
}

# ---------------------------------------------------------------------------
# Step 7 - verify
# ---------------------------------------------------------------------------
Write-Step 7 'Verifying'

$verifiedNames = @()
if (-not $SkipGrant -and -not $WhatIfPreference) {
    # Grants can take a few seconds to become readable.
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        $check = Invoke-Graph -Method GET `
            -Uri "/servicePrincipals/$servicePrincipalId/appRoleAssignments?`$select=appRoleId&`$top=999" -TolerateNotFound
        $ids = @()
        if ($check -and (Test-HasProperty $check 'value')) { $ids = @($check.value | ForEach-Object { [string]$_.appRoleId }) }

        $verifiedNames = @($resolved | Where-Object { $ids -contains $_.Id } | ForEach-Object { $_.Name })
        if ($verifiedNames.Count -eq $resolved.Count) { break }
        if ($attempt -lt 4) { Start-Sleep -Seconds ($attempt * 3) }
    }

    $notVisible = @($resolved | Where-Object { $verifiedNames -notcontains $_.Name } | ForEach-Object { $_.Name })
    Write-Host "  $($verifiedNames.Count)/$($resolved.Count) application permission(s) confirmed on the service principal." `
        -ForegroundColor $(if ($notVisible.Count) { 'Yellow' } else { 'Green' })
    if ($notVisible.Count -gt 0) {
        Write-Warning "Not yet visible: $($notVisible -join ', '). Directory replication can take a few minutes; re-run to reconcile."
    }
}
else {
    Write-Host '  Skipped.' -ForegroundColor Gray
}

# ---------------------------------------------------------------------------
# Step 8 - summary
# ---------------------------------------------------------------------------
Write-Step 8 'Summary'

Write-Host ''
Write-Host "  Tenant             : $($ctx.TenantId)"        -ForegroundColor Gray
Write-Host "  Application        : $DisplayName"            -ForegroundColor Gray
Write-Host "  Application (client) ID : $applicationAppId"  -ForegroundColor White
Write-Host "  Object ID          : $applicationObjectId"    -ForegroundColor Gray
Write-Host "  Service principal  : $servicePrincipalId"     -ForegroundColor Gray
Write-Host "  Granted now        : $($grantedNow.Count)"    -ForegroundColor Green
Write-Host "  Already held       : $($alreadyHeld.Count)"   -ForegroundColor Gray
if ($failedGrant.Count -gt 0) {
    Write-Host "  Failed             : $($failedGrant.Count)" -ForegroundColor Red
}

if ($plainSecret) {
    if ($KeyVaultName) {
        # The secret is still printed if the vault write fails: a credential that exists in
        # Entra but was captured nowhere is unrecoverable, so the console copy is the fallback.
        $kvName = if ($KeyVaultSecretName) { $KeyVaultSecretName } else { ConvertTo-KeyVaultSecretName "$DisplayName-clientsecret" }
        $kvUri  = Resolve-KeyVaultUri $KeyVaultName
        Write-Host ''
        Write-Host "  Saving the client secret to Key Vault $kvUri (secret '$kvName')..." -ForegroundColor Cyan
        try {
            $kvToken = Get-KeyVaultToken -TenantId $ctx.TenantId -AuthMode $ctx.Mode -ClientId $ClientId `
                -ClientSecret $ClientSecret -Certificate $ctx.Certificate -ExplicitToken $KeyVaultAccessToken
            # Expire the vault entry with the credential itself, so a stale secret is not left
            # looking current after Entra has already stopped accepting it.
            $kvExp = [DateTimeOffset]::UtcNow.AddMonths($SecretValidityMonths)
            $script:KeyVaultResult = Save-A365SecretToKeyVault -VaultUri $kvUri -SecretName $kvName `
                -SecretValue $plainSecret -Token $kvToken.Token -ContentType 'application/x-a365-client-secret' `
                -ExpiresOn $kvExp -Tags @{ a365Object = 'automationApp'; a365AppId = "$applicationAppId"; a365DisplayName = "$DisplayName" }
            Write-Host ("  Saved. version {0}{1}" -f $script:KeyVaultResult.Version, $(if ($script:KeyVaultResult.Verified) { ', read back OK' } else { ' (NOT confirmed on read-back)' })) -ForegroundColor Green
            Write-Host ("  Retrieve with: az keyvault secret show --vault-name {0} --name {1} --query value -o tsv" -f ($kvUri -replace '^https://', '' -replace '\.vault\.azure\.net$', ''), $kvName) -ForegroundColor Gray
        }
        catch {
            $st = 0
            if ($_.Exception.Message -match 'HTTP (\d{3})') { $st = [int]$Matches[1] }
            $script:KeyVaultResult = [pscustomobject]@{ VaultUri = $kvUri; SecretName = $kvName; Saved = $false; Error = $_.Exception.Message }
            Write-KeyVaultActionRequired -VaultUri $kvUri -SecretName $kvName -Message $_.Exception.Message `
                -Status $st -CallerDisplay $ctx.ClientId
            Write-Host ''
            Write-Host '  CLIENT SECRET (shown once - Entra will never display it again):' -ForegroundColor Yellow
            Write-Host "  $plainSecret" -ForegroundColor Yellow
        }
    }
    else {
        Write-Host ''
        Write-Host '  CLIENT SECRET (shown once - Entra will never display it again):' -ForegroundColor Yellow
        Write-Host "  $plainSecret" -ForegroundColor Yellow
        Write-Host ''
        Write-Host '  Store it in a secret store and pass it via $env:A365_CLIENT_SECRET:' -ForegroundColor Gray
        Write-Host '    $env:A365_CLIENT_SECRET = ''<secret>''' -ForegroundColor Gray
        Write-Host '  Or pass -KeyVaultName <vault> to have it written straight to Azure Key Vault.' -ForegroundColor DarkGray
    }
}

if ($delegatedToRequest.Count -gt 0) {
    Write-Host ''
    Write-Host '  Delegated scopes were REQUESTED but still need admin consent. Grant them here:' -ForegroundColor Yellow
    Write-Host "  https://login.microsoftonline.com/$($ctx.TenantId)/adminconsent?client_id=$applicationAppId" -ForegroundColor Cyan
}

# The custom security attribute permissions are the one pair on this app that a directory role can
# still block. Saying so here turns a guaranteed later 403 into something actionable, because the
# failure names no permission when it happens.
if ($Scenario -in 'AgentIdentity', 'All') {
    $csaGranted = @($resolved | Where-Object { $_.Name -like 'CustomSecAttribute*' })
    if ($csaGranted.Count -gt 0) {
        Write-Host ''
        Write-Host '  Custom security attributes:' -ForegroundColor Cyan
        Write-Host ('    Application roles granted: {0}' -f (($csaGranted | ForEach-Object { $_.Name }) -join ', ')) -ForegroundColor Gray
        Write-Host '    That is all an UNATTENDED (app-only) run needs.' -ForegroundColor Gray
        Write-Host '    An INTERACTIVE run needs more: the signed-in user must also hold the' -ForegroundColor Yellow
        Write-Host '    Attribute Assignment Administrator directory role. No application permission' -ForegroundColor Yellow
        Write-Host '    grants it, and Global Administrator does NOT include it.' -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host '  Ready-to-paste invocations:' -ForegroundColor Cyan
Write-Host ''
Write-Host '    $env:A365_CLIENT_SECRET = ''<secret>''' -ForegroundColor Gray
Write-Host "    .\New-A365AgentBlueprint.ps1 -TenantId $($ctx.TenantId) ``"        -ForegroundColor Gray
Write-Host "        -ClientId $applicationAppId -DisplayName 'Contoso Helpdesk'"   -ForegroundColor Gray
Write-Host ''
Write-Host "    .\New-A365AgentIdentity.ps1 -TenantId $($ctx.TenantId) ``"         -ForegroundColor Gray
Write-Host "        -ClientId $applicationAppId -BlueprintAppId <blueprint-appId> ``" -ForegroundColor Gray
Write-Host "        -DisplayName 'Contoso Helpdesk Identity'"                      -ForegroundColor Gray
Write-Host ''
Write-Host "    # Agent registration is delegated-first - sign in as a user, using this app as the client:" -ForegroundColor Gray
Write-Host "    .\New-A365AgentRegistration.ps1 -TenantId $($ctx.TenantId) ``"     -ForegroundColor Gray
Write-Host "        -Interactive -ClientId $applicationAppId ``"                   -ForegroundColor Gray
Write-Host "        -DisplayName 'Contoso Helpdesk Agent' -AgentIdentityId <sp-object-id>" -ForegroundColor Gray

$summary = [ordered]@{
    tenantId              = $ctx.TenantId
    displayName           = $DisplayName
    applicationId         = $applicationAppId
    applicationObjectId   = $applicationObjectId
    servicePrincipalId    = $servicePrincipalId
    scenario              = $Scenario
    appRolesGranted       = @($grantedNow)
    appRolesAlreadyHeld   = @($alreadyHeld)
    appRolesVerified      = @($verifiedNames)
    appRolesUnresolved    = @($missing)
    delegatedScopesRequested = @($delegatedToRequest | ForEach-Object { $_.Name })
    adminConsentUrl       = "https://login.microsoftonline.com/$($ctx.TenantId)/adminconsent?client_id=$applicationAppId"
    keyVault              = $script:KeyVaultResult
    generatedUtc          = [DateTimeOffset]::UtcNow.ToString('o')
}

if ($OutputPath) {
    # Deliberately excludes the secret.
    $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    Write-Host ''
    Write-Host "  Summary written to $OutputPath (the client secret is NOT included)." -ForegroundColor Green
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green

[pscustomobject]$summary