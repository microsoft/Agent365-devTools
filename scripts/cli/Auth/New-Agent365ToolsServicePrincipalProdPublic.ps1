#requires -Version 7.0

<#
.SYNOPSIS
    Creates Service Principals for Agent 365 MCP servers in your tenant (Admin only).

.DESCRIPTION
    V1 model: Creates the shared 'Agent 365 Tools' Service Principal
              (AppId ea9ffc3e-8a23-4a7d-836d-234d7c7565c1).
              All V1 servers share this single resource and use McpServers.*.All scopes.

    V2 model: Creates one Service Principal per MCP server using per-server AppIds.
              V2 servers use the Tools.ListInvoke.All scope against their own audience GUID.
              AppIds are extracted from ToolingManifest.json (-ManifestPath) or passed
              directly via -V2AppIds.

    Use -Mode All (default) during migration when the tenant may have both V1 and V2 servers.

.PARAMETER Mode
    V1   - Provision only the shared V1 ATG Service Principal.
    V2   - Provision per-server V2 Service Principals only.
    All  - Provision both V1 and all V2 servers (default, recommended during migration).

.PARAMETER ManifestPath
    Path to ToolingManifest.json. The script reads audience GUIDs where scope equals
    'Tools.ListInvoke.All' and creates a Service Principal for each unique V2 AppId found.

.PARAMETER V2AppIds
    Explicit list of V2 per-server AppIds. Used when -ManifestPath is not provided.

.EXAMPLE
    .\New-Agent365ToolsServicePrincipalProdPublic.ps1
    (Creates the V1 SP; V2 is skipped unless -ManifestPath or -V2AppIds are supplied.)

.EXAMPLE
    .\New-Agent365ToolsServicePrincipalProdPublic.ps1 -Mode V2 -ManifestPath ".\ToolingManifest.json"

.EXAMPLE
    .\New-Agent365ToolsServicePrincipalProdPublic.ps1 -Mode All -ManifestPath ".\ToolingManifest.json"

.EXAMPLE
    .\New-Agent365ToolsServicePrincipalProdPublic.ps1 -Mode V2 -V2AppIds @("05879165-0320-489e-b644-f72b33f3edf0")

.NOTES
    Requires: Admin permissions to create Service Principals.
    This script is safe to re-run — existing Service Principals are skipped, not re-created.
#>

param(
    [ValidateSet("V1", "V2", "All")]
    [string]$Mode = "All",

    # Path to ToolingManifest.json — used to auto-extract V2 per-server AppIds
    [string]$ManifestPath = "",

    # Explicit V2 per-server AppIds (alternative to -ManifestPath)
    [string[]]$V2AppIds = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# V1: shared ATG AppId (WorkIQToolsProdAppId) — all V1 servers share this resource
$v1AppId = "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1"

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
    if ($ManifestPath -and (Test-Path $ManifestPath)) {
        Write-Host "Reading V2 AppIds from manifest: $ManifestPath" -ForegroundColor Cyan
        $manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
        $resolvedV2AppIds = @(
            $manifest.mcpServers |
                Where-Object { $_.scope -eq "Tools.ListInvoke.All" -and $_.audience -match '(?i)^[0-9a-f]{8}-' } |
                Select-Object -ExpandProperty audience -Unique
        )
        Write-Host "  Found $($resolvedV2AppIds.Count) V2 AppId(s) in manifest." -ForegroundColor Cyan
        Write-Host ""
    }
    elseif ($V2AppIds.Count -gt 0) {
        $resolvedV2AppIds = $V2AppIds
    }
    elseif ($Mode -eq "V2") {
        Write-Host "ERROR: -Mode V2 requires -ManifestPath or -V2AppIds." -ForegroundColor Red
        exit 1
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
Write-Host "⚠ You need admin permissions for this operation." -ForegroundColor Yellow
Write-Host ""

try {
    Connect-MgGraph -Scopes "AppRoleAssignment.ReadWrite.All" -NoWelcome
    $context = Get-MgContext
    Write-Host "✓ Connected to tenant: $($context.TenantId)" -ForegroundColor Green
    Write-Host ""
}
catch {
    Write-Host "✗ Failed to connect to Microsoft Graph" -ForegroundColor Red
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

    # V2: per-server Service Principals
    if (($Mode -eq "V2" -or $Mode -eq "All") -and $resolvedV2AppIds.Count -gt 0) {
        foreach ($appId in $resolvedV2AppIds) {
            Register-ServicePrincipalIfMissing -AppId $appId -Label "V2 Per-Server"
        }
    }
    elseif ($Mode -eq "All" -and $resolvedV2AppIds.Count -eq 0) {
        Write-Host ""
        Write-Host "  V2 provisioning skipped — no V2 AppIds found." -ForegroundColor Yellow
        Write-Host "  Provide -ManifestPath or -V2AppIds to provision V2 servers." -ForegroundColor Yellow
    }
}
catch {
    Write-Host ""
    Write-Host "✗ Failed to create Service Principal" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ""

    if ($_.Exception.Message -like "*Insufficient privileges*" -or $_.Exception.Message -like "*Authorization*") {
        Write-Host "⚠ This error usually means you don't have admin permissions." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Required Permissions:" -ForegroundColor Cyan
        Write-Host "  - AppRoleAssignment.ReadWrite.All" -ForegroundColor White
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
