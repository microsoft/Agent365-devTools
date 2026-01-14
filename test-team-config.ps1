# Test script for team configuration functionality
# This tests the team config loading, validation, and merging without requiring Azure resources

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Testing Team Configuration Implementation" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Build the CLI first
Write-Host "Building CLI..." -ForegroundColor Yellow
Push-Location "Q:\Agent365-devTools\src"
$buildResult = dotnet build Microsoft.Agents.A365.DevTools.Cli.sln --verbosity quiet
Pop-Location

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Build successful!" -ForegroundColor Green
Write-Host ""

# Test 1: Verify team.config.example.json exists
Write-Host "Test 1: Verifying example configuration file..." -ForegroundColor Yellow
$exampleConfigPath = "Q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\team.config.example.json"

if (Test-Path $exampleConfigPath) {
    Write-Host "  ✓ team.config.example.json found" -ForegroundColor Green
} else {
    Write-Host "  ✗ team.config.example.json not found" -ForegroundColor Red
    exit 1
}

# Test 2: Test JSON parsing
Write-Host "Test 2: Testing JSON parsing..." -ForegroundColor Yellow
try {
    $teamConfig = Get-Content $exampleConfigPath -Raw | ConvertFrom-Json
    Write-Host "  ✓ JSON is valid" -ForegroundColor Green
    Write-Host "    Team: $($teamConfig.displayName) ($($teamConfig.name))" -ForegroundColor Gray
    Write-Host "    Agents: $($teamConfig.agents.Count)" -ForegroundColor Gray
} catch {
    Write-Host "  ✗ JSON parsing failed: $_" -ForegroundColor Red
    exit 1
}

# Test 3: Run unit tests for team configuration
Write-Host "Test 3: Running unit tests..." -ForegroundColor Yellow
Push-Location "Q:\Agent365-devTools\src"
$testResult = dotnet test Microsoft.Agents.A365.DevTools.Cli.sln --filter "FullyQualifiedName~TeamConfig" --verbosity quiet --nologo
Pop-Location

if ($LASTEXITCODE -eq 0) {
    Write-Host "  ✓ All unit tests passed" -ForegroundColor Green
} else {
    Write-Host "  ✗ Unit tests failed" -ForegroundColor Red
    exit 1
}

Write-Host ""

# Test 4: Test dry-run of setup command with team config
Write-Host "Test 4: Testing setup all --team --dry-run..." -ForegroundColor Yellow

# Create a test team config with valid paths
$testConfigDir = Join-Path $env:TEMP "a365-team-test-$(Get-Random)"
New-Item -ItemType Directory -Path $testConfigDir -Force | Out-Null

# Create fake agent directories
$agentDirs = @("triage", "technical", "billing")
foreach ($agentDir in $agentDirs) {
    $agentPath = Join-Path $testConfigDir "agents\$agentDir"
    New-Item -ItemType Directory -Path $agentPath -Force | Out-Null
}

# Create test team config with valid deployment paths
$testTeamConfig = @{
    name = "test-support"
    displayName = "Test Support Team"
    description = "Test team configuration"
    managerEmail = "manager@contoso.com"
    sharedResources = @{
        tenantId = "12345678-1234-1234-1234-123456789012"
        clientAppId = "87654321-4321-4321-4321-210987654321"
        subscriptionId = "abcdef12-3456-7890-abcd-ef1234567890"
        resourceGroup = "rg-test-support"
        location = "eastus"
        appServicePlanName = "asp-test-support"
        appServicePlanSku = "B1"
        environment = "prod"
        agentUserUsageLocation = "US"
    }
    agents = @(
        @{
            name = "triage"
            displayName = "Triage Agent"
            agentIdentityDisplayName = "Test Triage Agent"
            agentUserPrincipalName = "triage@contoso.com"
            agentUserDisplayName = "Triage Bot"
            deploymentProjectPath = (Join-Path $testConfigDir "agents\triage")
        },
        @{
            name = "technical"
            displayName = "Technical Agent"
            agentIdentityDisplayName = "Test Technical Agent"
            agentUserPrincipalName = "technical@contoso.com"
            agentUserDisplayName = "Technical Bot"
            deploymentProjectPath = (Join-Path $testConfigDir "agents\technical")
        },
        @{
            name = "billing"
            displayName = "Billing Agent"
            agentIdentityDisplayName = "Test Billing Agent"
            agentUserPrincipalName = "billing@contoso.com"
            agentUserDisplayName = "Billing Bot"
            deploymentProjectPath = (Join-Path $testConfigDir "agents\billing")
        }
    )
}

$testTeamConfigPath = Join-Path $testConfigDir "team.config.json"
$testTeamConfig | ConvertTo-Json -Depth 10 | Set-Content $testTeamConfigPath

Write-Host "  Created test team config at: $testTeamConfigPath" -ForegroundColor Gray

# Run dry-run command
$cliPath = "Q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\bin\Debug\net8.0\Microsoft.Agents.A365.DevTools.Cli.dll"

if (Test-Path $cliPath) {
    Write-Host "  Running: a365 setup all --team $testTeamConfigPath --dry-run" -ForegroundColor Gray
    Write-Host ""
    
    dotnet $cliPath setup all --team $testTeamConfigPath --dry-run
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "  ✓ Dry-run completed successfully" -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "  ✗ Dry-run failed with exit code $LASTEXITCODE" -ForegroundColor Red
        
        # Cleanup
        Remove-Item $testConfigDir -Recurse -Force -ErrorAction SilentlyContinue
        exit 1
    }
} else {
    Write-Host "  ✗ CLI executable not found at: $cliPath" -ForegroundColor Red
    
    # Cleanup
    Remove-Item $testConfigDir -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}

# Cleanup
Write-Host ""
Write-Host "Cleaning up test files..." -ForegroundColor Gray
Remove-Item $testConfigDir -Recurse -Force -ErrorAction SilentlyContinue

# Summary
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "All Tests Passed! ✓" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Team configuration implementation is working correctly!" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps for actual deployment:" -ForegroundColor Yellow
Write-Host "  1. Create your team.config.json with real values" -ForegroundColor Gray
Write-Host "  2. Update tenantId, clientAppId, subscriptionId, etc." -ForegroundColor Gray
Write-Host "  3. Set valid deployment project paths for each agent" -ForegroundColor Gray
Write-Host "  4. Run: a365 setup all --team team.config.json" -ForegroundColor Gray
Write-Host ""
