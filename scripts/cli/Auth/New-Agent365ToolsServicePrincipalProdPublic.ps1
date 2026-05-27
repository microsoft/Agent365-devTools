#requires -Version 7.0

<#
.SYNOPSIS
    Creates Service Principals for Agent 365 MCP servers in your tenant (Admin only).

.DESCRIPTION
    V1 model: Creates the shared 'Agent 365 Tools' Service Principal
              (AppId ea9ffc3e-8a23-4a7d-836d-234d7c7565c1).
              All V1 servers share this single resource and use McpServers.*.All scopes.

    V2 model: Creates one Service Principal per MCP server using per-server AppIds.
              V2 AppIds are discovered from the live Agent 365 V2 endpoint:
              https://agent365.svc.cloud.microsoft/agents/v2/discoverMCPServers
              V2 servers use the Tools.ListInvoke.All scope against their own audience GUID.
              Pass -V2AppIds to bypass the live call and supply AppIds directly.

    Use -Mode All (default) during migration when the tenant may have both V1 and V2 servers.

.PARAMETER Mode
    V1   - Provision only the shared V1 ATG Service Principal.
    V2   - Provision per-server V2 Service Principals only (discovered from live endpoint).
    All  - Provision both V1 and all V2 servers (default, recommended during migration).

.PARAMETER V2AppIds
    Explicit list of V2 per-server AppIds. Bypasses the live discover endpoint call.

.EXAMPLE
    .\New-Agent365ToolsServicePrincipalProdPublic.ps1
    (Creates V1 SP and discovers V2 SPs from the live endpoint.)

.EXAMPLE
    .\New-Agent365ToolsServicePrincipalProdPublic.ps1 -Mode V2

.EXAMPLE
    .\New-Agent365ToolsServicePrincipalProdPublic.ps1 -Mode All

.EXAMPLE
    .\New-Agent365ToolsServicePrincipalProdPublic.ps1 -Mode V2 -V2AppIds @("05879165-0320-489e-b644-f72b33f3edf0")

.NOTES
    Requires: Admin permissions to create Service Principals.
    Requires: Az CLI (az login) to acquire a token for the discover endpoint.
    This script is safe to re-run — existing Service Principals are skipped, not re-created.
#>

param(
    [ValidateSet("V1", "V2", "All")]
    [string]$Mode = "All",

    # Explicit V2 per-server AppIds — bypasses the live discover endpoint call
    [string[]]$V2AppIds = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# V1: shared ATG AppId (WorkIQToolsProdAppId) — all V1 servers share this resource
$v1AppId = "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1"

# V2 discover endpoint — returns a bare JSON array of available MCP servers
$v2DiscoverUrl = "https://agent365.svc.cloud.microsoft/agents/v2/discoverMCPServers"

# V2 scope value used by all per-server entries
$v2ScopeValue = "Tools.ListInvoke.All"

# V2 fallback AppIds — used when the discover endpoint is unreachable.
# Source: MCPPlatform_McpScopedApps__ServerAppMappings__* configuration values.
$v2FallbackAppIds = @(
    "16b1878d-62c7-4009-aa25-68989d63bbad",  # mcp_MailTools
    "147dc821-b413-44c0-8009-1a3098378012",  # mcp_MeServer
    "910333d2-47e9-43ca-981f-6df2f4531ef4",  # mcp_CalendarTools
    "ce5029ee-c1d3-45c0-bdcc-efb5a4245687",  # mcp_TeamsServer
    "b0b2a2bb-6361-4549-a00c-a018417eb8e2",  # mcp_OneDriveRemoteServer
    "292cff14-c0e8-4116-9e3b-99934ae05766",  # mcp_SharePointRemoteServer
    "2dbeefeb-6462-48a4-abe6-1c4989699319",  # mcp_AdminTools
    "c2d0c2b6-8013-4346-9f8b-b81d3b754a29",  # mcp_WordServer
    "ab7c82de-7946-4454-ac28-70249d17c95e"   # mcp_M365Copilot
)

# --- Helper: create Service Principal if it does not already exist ---
function Register-ServicePrincipalIfMissing {
    param([string]$AppId, [string]$Label)

    Write-Host ""
    Write-Host "  [$Label] AppId: $AppId" -ForegroundColor Cyan

    $existing = Get-MgServicePrincipal -Filter "appId eq '$AppId'" -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "  Already exists: $($existing.DisplayName) (SP ID: $($existing.Id))" -ForegroundColor Green
        return
    }

    $sp = New-MgServicePrincipal -BodyParameter @{ AppId = $AppId }
    Write-Host "  Created: $($sp.DisplayName) (SP ID: $($sp.Id))" -ForegroundColor Green
}

# --- Helper: call the V2 discover endpoint and extract per-server AppIds ---
function Get-V2AppIdsFromDiscoverEndpoint {
    Write-Host "Discovering V2 AppIds from: $v2DiscoverUrl" -ForegroundColor Cyan

    # Acquire a token for the ATG audience using az CLI
    try {
        $token = az account get-access-token --resource $v1AppId --query accessToken -o tsv 2>$null
        if ([string]::IsNullOrWhiteSpace($token)) {
            Write-Host "  WARNING: Could not acquire token via az CLI. Ensure you are logged in with 'az login'." -ForegroundColor Yellow
            return @()
        }
    }
    catch {
        Write-Host "  WARNING: az CLI token acquisition failed: $($_.Exception.Message)" -ForegroundColor Yellow
        return @()
    }

    try {
        $headers = @{ Authorization = "Bearer $token" }
        $response = Invoke-RestMethod -Uri $v2DiscoverUrl -Headers $headers -Method Get -ErrorAction Stop

        # V2 returns a bare array; V1 (legacy) returns a wrapped { mcpServers: [...] } object
        $servers = if ($response -is [array]) { $response } else { $response.mcpServers }

        if (-not $servers -or $servers.Count -eq 0) {
            Write-Host "  No servers returned from discover endpoint." -ForegroundColor Yellow
            return @()
        }

        $appIds = @(
            $servers |
                Where-Object { $_.scope -eq $v2ScopeValue -and $_.audience -match '(?i)^[0-9a-f]{8}-' } |
                Select-Object -ExpandProperty audience -Unique
        )

        Write-Host "  Found $($appIds.Count) V2 AppId(s) from discover endpoint." -ForegroundColor Cyan
        Write-Host ""
        return $appIds
    }
    catch {
        Write-Host "  WARNING: Failed to call discover endpoint: $($_.Exception.Message)" -ForegroundColor Yellow
        return @()
    }
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Service Principal Creation for Agent 365 MCP Servers (Admin Only)" -ForegroundColor Cyan
Write-Host "  Mode: $Mode" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "WARNING: This requires admin permissions!" -ForegroundColor Yellow
Write-Host "WARNING: Safe to re-run — existing Service Principals are skipped, not re-created." -ForegroundColor Yellow
Write-Host ""

# --- Resolve V2 AppIds ---
$resolvedV2AppIds = @()

if ($Mode -ne "V1") {
    if ($V2AppIds.Count -gt 0) {
        $resolvedV2AppIds = $V2AppIds
        Write-Host "Using explicit V2 AppIds provided via -V2AppIds." -ForegroundColor Cyan
        Write-Host ""
    }
    else {
        $liveAppIds = Get-V2AppIdsFromDiscoverEndpoint
        # Always union live results with the hardcoded fallback so servers absent from
        # the discover response (e.g. mcp_MeServer) are still provisioned.
        $resolvedV2AppIds = @($liveAppIds + $v2FallbackAppIds | Select-Object -Unique)
        Write-Host "  Total V2 AppIds to provision (live + fallback): $($resolvedV2AppIds.Count)" -ForegroundColor Cyan
        Write-Host ""
    }
}

# --- Ensure Microsoft.Graph modules are available ---
Write-Host "Checking for Microsoft.Graph module..." -ForegroundColor Cyan
if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Applications)) {
    Write-Host "Microsoft.Graph.Applications module not found. Installing..." -ForegroundColor Yellow
    Install-Module Microsoft.Graph.Applications -Scope CurrentUser -Force -ErrorAction Stop
}
if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Authentication)) {
    Write-Host "Microsoft.Graph.Authentication module not found. Installing..." -ForegroundColor Yellow
    Install-Module Microsoft.Graph.Authentication -Scope CurrentUser -Force -ErrorAction Stop
}

# Import required modules
Import-Module Microsoft.Graph.Applications -ErrorAction Stop
Import-Module Microsoft.Graph.Authentication -ErrorAction Stop

# --- Connect to Microsoft Graph ---
Write-Host ""
Write-Host "Connecting to Microsoft Graph..." -ForegroundColor Cyan
Write-Host "You need admin permissions for this operation." -ForegroundColor Yellow
Write-Host ""

try {
    Connect-MgGraph -Scopes "Application.ReadWrite.All" -NoWelcome
    $context = Get-MgContext
    Write-Host "Connected to tenant: $($context.TenantId)" -ForegroundColor Green
    Write-Host ""
}
catch {
    Write-Host "Failed to connect to Microsoft Graph" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}

# --- Provision Service Principals ---
try {
    Write-Host "Provisioning Service Principals..." -ForegroundColor Cyan

    # V1: shared ATG Service Principal
    if ($Mode -eq "V1" -or $Mode -eq "All") {
        Register-ServicePrincipalIfMissing -AppId $v1AppId -Label "V1 Shared ATG"
    }

    # V2: per-server Service Principals discovered from the live endpoint
    if (($Mode -eq "V2" -or $Mode -eq "All") -and $resolvedV2AppIds.Count -gt 0) {
        foreach ($appId in $resolvedV2AppIds) {
            Register-ServicePrincipalIfMissing -AppId $appId -Label "V2 Per-Server"
        }
    }
    elseif ($Mode -eq "All" -and $resolvedV2AppIds.Count -eq 0) {
        Write-Host ""
        Write-Host "  V2 provisioning skipped — no V2 AppIds available." -ForegroundColor Yellow
    }
}
catch {
    Write-Host ""
    Write-Host "Failed to create Service Principal" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ""

    if ($_.Exception.Message -like "*Insufficient privileges*" -or $_.Exception.Message -like "*Authorization*") {
        Write-Host "This error usually means you don't have admin permissions." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Required Permissions:" -ForegroundColor Cyan
        Write-Host "  - Application.ReadWrite.All" -ForegroundColor White
        Write-Host "  - Or Global Administrator / Application Administrator role" -ForegroundColor White
        Write-Host ""
        Write-Host "Please contact your Microsoft Entra ID administrator to run this script." -ForegroundColor Yellow
    }

    Disconnect-MgGraph | Out-Null
    exit 1
}

Disconnect-MgGraph | Out-Null

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Setup Complete!" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
