#requires -Version 7.0

<#
.SYNOPSIS
    Creates the Agent 365 Tools Service Principal in your tenant (Admin only).

.DESCRIPTION
    This script creates the Service Principal for Agent 365 Tools in your Microsoft Entra ID tenant.
    This is a ONE-TIME operation per tenant that requires admin permissions.
    
    After the Service Principal is created, regular users can create their own app
    registrations without needing admin rights.

.EXAMPLE
    .\New-Agent365ToolsServicePrincipalProdPublic.ps1

.NOTES
    Requires: Admin permissions to create Service Principals
    This script only needs to be run ONCE per tenant.
#>

$resourceId = "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Service Principal Creation for the 'Agent 365 Tools' application (Admin Only)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "⚠ IMPORTANT: This requires admin permissions!" -ForegroundColor Yellow
Write-Host "⚠ This only needs to be run ONCE per tenant!" -ForegroundColor Yellow
Write-Host ""

# Check if Microsoft.Graph module is installed
Write-Host "Checking for Microsoft.Graph module..." -ForegroundColor Cyan
if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Applications)) {
    Write-Host "Microsoft.Graph.Applications module not found. Installing..." -ForegroundColor Yellow
    Install-Module Microsoft.Graph.Applications -Scope CurrentUser -Force
}

if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Authentication)) {
    Write-Host "Microsoft.Graph.Authentication module not found. Installing..." -ForegroundColor Yellow
    Install-Module Microsoft.Graph.Authentication -Scope CurrentUser -Force
}

# Import required modules
Import-Module Microsoft.Graph.Applications
Import-Module Microsoft.Graph.Authentication

# Connect to Microsoft Graph
Write-Host ""
Write-Host "Connecting to Microsoft Graph..." -ForegroundColor Cyan
Write-Host "⚠ You need admin permissions for this operation." -ForegroundColor Yellow
Write-Host ""

try {
    # Request admin scope for creating service principals
    Connect-MgGraph -Scopes "AppRoleAssignment.ReadWrite.All" -NoWelcome
    $context = Get-MgContext
    Write-Host "✓ Connected to tenant: $($context.TenantId)" -ForegroundColor Green
    $tenantId = $context.TenantId
    Write-Host ""
}
catch {
    Write-Host "✗ Failed to connect to Microsoft Graph" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}

Write-Host "Checking if Service Principal already exists..." -ForegroundColor Cyan

try {
    $existingSp = Get-MgServicePrincipal -Filter "appId eq '$resourceId'" -ErrorAction SilentlyContinue
    
    if ($existingSp) {
        Write-Host ""
        Write-Host "========================================" -ForegroundColor Green
        Write-Host "✓ SERVICE PRINCIPAL ALREADY EXISTS" -ForegroundColor Green
        Write-Host "========================================" -ForegroundColor Green
        Write-Host ""
        Write-Host "Details:" -ForegroundColor Cyan
        Write-Host "  Display Name: $($existingSp.DisplayName)" -ForegroundColor White
        Write-Host "  App ID: $($existingSp.AppId)" -ForegroundColor White
        Write-Host "  Service Principal ID: $($existingSp.Id)" -ForegroundColor White
        Write-Host ""
        Write-Host "✓ No action needed - Service Principal is already configured!" -ForegroundColor Green
        Write-Host ""
        
        Disconnect-MgGraph | Out-Null
        exit 0
    }
    
    Write-Host "✓ Service Principal does not exist - proceeding with creation..." -ForegroundColor Yellow
    Write-Host ""
}
catch {
    Write-Host "⚠ Warning: Could not check for existing Service Principal" -ForegroundColor Yellow
    Write-Host "  Proceeding with creation attempt..." -ForegroundColor Yellow
    Write-Host ""
}

Write-Host "Creating Service Principal..." -ForegroundColor Cyan

try {
    # Create service principal for the resource app
    $spParams = @{
        AppId = $resourceId
    }
    
    $resourceSp = New-MgServicePrincipal -BodyParameter $spParams
    
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "✓ SERVICE PRINCIPAL CREATED SUCCESSFULLY!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "Details:" -ForegroundColor Cyan
    Write-Host "  Display Name: $($resourceSp.DisplayName)" -ForegroundColor White
    Write-Host "  App ID: $($resourceSp.AppId)" -ForegroundColor White
    Write-Host "  Service Principal ID: $($resourceSp.Id)" -ForegroundColor White
    Write-Host "  Tenant ID: $tenantId" -ForegroundColor White
    Write-Host ""
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

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Setup Complete!" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
