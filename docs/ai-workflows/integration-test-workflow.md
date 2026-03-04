# AI Integration Test Workflow for Agent365 DevTools CLI

## Overview
This workflow guides an AI agent through comprehensive integration testing of the Agent365 DevTools CLI (`a365`). The workflow tests all major commands and scenarios to ensure no regressions are introduced.

**Test Type**: Integration Test with User Input  
**Execution Mode**: Interactive (requires user participation)  
**Duration**: Approximately 30-45 minutes  
**Prerequisites**: Azure subscription, Entra ID admin access, custom client app with required permissions

---

## Pre-Test Setup

### Prerequisites Checklist
Before starting the workflow, verify:

- [ ] Azure subscription is active and accessible
- [ ] Azure CLI (`az`) is installed and authenticated: `az login`
- [ ] .NET 8.0 SDK is installed: `dotnet --version`
- [ ] Custom Entra ID client app is registered with required delegated permissions
- [ ] Admin consent is granted for the custom client app
- [ ] Test environment variables are ready:
  - `AGENT365_TEST_TENANT_ID`
  - `AGENT365_TEST_SUBSCRIPTION_ID`
  - `AGENT365_TEST_CLIENT_ID`
  - `AGENT365_TEST_MANAGER_EMAIL`

### Test Environment Setup
```bash
# Set environment variables (user should provide these)
$env:AGENT365_TEST_TENANT_ID = "<ask-user>"
$env:AGENT365_TEST_SUBSCRIPTION_ID = "<ask-user>"
$env:AGENT365_TEST_CLIENT_ID = "<ask-user>"
$env:AGENT365_TEST_MANAGER_EMAIL = "<ask-user>"

# Create test workspace
$testWorkspace = "$HOME\a365-integration-test-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
New-Item -ItemType Directory -Path $testWorkspace -Force
Set-Location $testWorkspace
```

---

## Test Sections

### Section 1: CLI Installation and Version Check
**Objective**: Verify CLI can be installed and reports correct version

#### Test 1.1: Install CLI from NuGet
```bash
# Install the CLI
dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli --prerelease

# Expected: Successful installation message
# Record: Installation succeeded (Yes/No)
```

#### Test 1.2: Verify Installation
```bash
# Check CLI is accessible
a365 --version

# Expected: Version number displayed (e.g., "0.x.x")
# Record: Version number
```

#### Test 1.3: Check for Updates
```bash
# Run any command to trigger update check
a365 config init --help

# Expected: May show update notification or proceed normally
# Record: Update check performed (Yes/No)
```

**Section 1 Status**: ✅ Pass | ❌ Fail  
**Notes**:

---

### Section 2: Configuration Management
**Objective**: Test configuration initialization and validation

#### Test 2.1: Interactive Configuration Wizard
```bash
# Start interactive configuration
a365 config init

# User will be prompted for:
# 1. Agent name (alphanumeric only) - suggest: "TestAgent$(Get-Random -Maximum 9999)"
# 2. Deployment project path - use: "./test-app"
# 3. Manager email - use: $env:AGENT365_TEST_MANAGER_EMAIL
# 4. Azure subscription - select from list
# 5. Resource group - create new or select existing
# 6. Location - suggest: "westus" or "eastus"
# 7. App Service Plan - create new or select existing

# Expected: a365.config.json created in current directory
# Record: Configuration wizard completed (Yes/No)
```

#### Test 2.2: Validate Generated Configuration
```bash
# Display configuration
a365 config display

# Expected: Shows all configuration values in table format
# Verify required fields are present:
# - tenantId
# - subscriptionId
# - resourceGroup
# - location
# - webAppName
# - agentIdentityDisplayName
# - agentUserPrincipalName
# - deploymentProjectPath

# Record: All required fields present (Yes/No)
```

#### Test 2.3: Import Configuration from File
```bash
# Create a test config file
$testConfig = @{
    tenantId = $env:AGENT365_TEST_TENANT_ID
    subscriptionId = $env:AGENT365_TEST_SUBSCRIPTION_ID
    resourceGroup = "rg-a365-test-$(Get-Random -Maximum 9999)"
    location = "westus"
    webAppName = "webapp-test-$(Get-Random -Maximum 9999)"
    agentIdentityDisplayName = "Test Agent Identity"
    agentUserPrincipalName = "test.agent@domain.onmicrosoft.com"
    deploymentProjectPath = "./test-app"
    managerEmail = $env:AGENT365_TEST_MANAGER_EMAIL
} | ConvertTo-Json

$testConfig | Out-File -FilePath "test-config.json"

# Import configuration
a365 config init -c test-config.json

# Expected: Configuration imported and validated
# Record: Import succeeded (Yes/No)
```

#### Test 2.4: Global Configuration
```bash
# Create global configuration
a365 config init --global

# Expected: Configuration created in global directory (AppData)
# Verify file exists at: $env:APPDATA\Microsoft\Agent365DevTools\a365.config.json

# Record: Global config created (Yes/No)
```

#### Test 2.5: Configure Custom Blueprint Permissions
```bash
# Add Microsoft Graph extended permissions
a365 config permissions \
  --resource-app-id 00000003-0000-0000-c000-000000000000 \
  --scopes Presence.ReadWrite,Files.Read.All

# Expected: NO PROMPTS - permission added directly to a365.config.json
# Resource name will be auto-resolved during 'a365 setup permissions custom'
# Verify customBlueprintPermissions array exists in config file
# Record: Custom permission added (Yes/No)

# View configured permissions
a365 config permissions

# Expected: Lists all configured custom permissions (may show appId only until setup runs)
# Record: Permissions displayed correctly (Yes/No)

# Add second custom resource
a365 config permissions \
  --resource-app-id 12345678-1234-1234-1234-123456789012 \
  --scopes CustomScope.Read,CustomScope.Write

# Expected: NO PROMPTS - second permission added directly
# Resource names will be auto-resolved during setup
# Record: Second permission added (Yes/No)
```

**Section 2 Status**: ✅ Pass | ❌ Fail
**Notes**:

---

### Section 3: Setup Command - Requirements and Infrastructure
**Objective**: Test prerequisite checking and infrastructure creation

#### Test 3.1: Check Requirements
```bash
# Check prerequisites
a365 setup requirements

# Expected: Lists all requirements and their status
# - Custom client app permissions
# - Azure CLI authentication
# - Configuration file presence

# Record: Requirements check completed (Yes/No)
# Record: Any missing requirements
```

#### Test 3.2: Create Infrastructure (Dry Run)
```bash
# Dry run infrastructure creation
a365 setup infrastructure --dry-run

# Expected: Shows what would be created without executing
# - Resource group verification
# - App Service Plan creation
# - Web App creation

# Record: Dry run successful (Yes/No)
```

#### Test 3.3: Create Infrastructure (Actual)
```bash
# Create actual infrastructure
a365 setup infrastructure

# Expected: 
# - Resource group created or verified
# - App Service Plan created
# - Web App created
# - Web App configured with Python/Node/.NET runtime

# Record: Infrastructure created (Yes/No)
# Record: Resource group name
# Record: Web app name
```

**Section 3 Status**: ✅ Pass | ❌ Fail  
**Notes**:

---

### Section 4: Setup Command - Blueprint Creation
**Objective**: Test agent blueprint creation in Entra ID

#### Test 4.1: Create Agent Blueprint
```bash
# Create agent blueprint
a365 setup blueprint

# Expected:
# - Blueprint application created in Entra ID
# - Service principal created
# - Blueprint ID saved to configuration
# - Federated credentials configured

# Record: Blueprint created (Yes/No)
# Record: Blueprint ID (from a365.config.json)
```

#### Test 4.1a: Verify CustomClientAppId Configuration
```bash
# This test verifies the fix for issue #271 where CustomClientAppId was not being set,
# causing inheritable permissions operations to fail on macOS/Linux

# Prerequisite: Ensure config has clientAppId set
$config = Get-Content a365.config.json | ConvertFrom-Json
$config.clientAppId | Should -Not -BeNullOrEmpty

# Enable trace logging to capture Graph API authentication
$env:AGENT365_LOG_LEVEL = "Trace"

# Re-run blueprint setup with inheritable permissions
a365 setup blueprint --verbose 2>&1 | Tee-Object -Variable output

# Verify output contains correct client ID usage
# The trace logs should show Connect-MgGraph with the correct -ClientId parameter
$output | Select-String -Pattern "Connect-MgGraph.*-ClientId $($config.clientAppId)"

# Expected:
# - Trace logs show Connect-MgGraph command with correct -ClientId
# - No "Tenant not found" errors (would indicate clientAppId/tenantId were swapped)
# - Inheritable permissions operations succeed without falling back to SDK default app

# Record: CustomClientAppId configured correctly (Yes/No)
# Record: Connect-MgGraph used correct ClientId (Yes/No)
```

#### Test 4.2: Verify Blueprint in Entra ID
```bash
# Query blueprint scopes
a365 query-entra blueprint-scopes

# Expected: Lists configured scopes and consent status
# Record: Scopes displayed (Yes/No)
# Record: Number of scopes shown
```

#### Test 4.3: Blueprint Permissions - MCP
```bash
# Configure MCP permissions
a365 setup permissions mcp

# Expected: MCP permissions configured for blueprint
# Record: MCP permissions set (Yes/No)
```

#### Test 4.4: Blueprint Permissions - Bot
```bash
# Configure bot permissions
a365 setup permissions bot

# Expected: Bot messaging permissions configured
# Record: Bot permissions set (Yes/No)
```

#### Test 4.5: Blueprint Permissions - Custom Resources (with Auto-Lookup)
```bash
# Configure custom permissions (requires Test 2.5 completed)
a365 setup permissions custom

# Expected:
# - AUTO-LOOKUP: CLI queries Azure to resolve resource display names
# - Output shows: "Resource name not provided, attempting auto-lookup for {appId}..."
# - Output shows: "Auto-resolved resource name: Microsoft Graph" (or similar)
# - OAuth2 grants created for each custom resource
# - Inheritable permissions configured
# - Permissions visible in Azure Portal under API permissions
# - Success messages for each configured resource
# - Note: ResourceName is resolved in-memory for logging only; it is NOT persisted to any config file

# IMPORTANT: Verify auto-lookup messages appear in output
# If resource not found in Azure, should show fallback: "Custom-{first 8 chars}"

# Record: Custom permissions configured (Yes/No)
# Record: Number of custom resources configured
# Record: Auto-lookup succeeded (Yes/No)
```

#### Test 4.6: Verify Custom Permissions in Azure Portal
```bash
# Query blueprint application to verify custom permissions
az ad app show --id <blueprint-app-id> --query "requiredResourceAccess[].{ResourceAppId:resourceAppId, Scopes:resourceAccess[].id}"

# Expected: Shows custom resource permissions configured
# - Microsoft Graph (00000003-0000-0000-c000-000000000000) with extended scopes
# - Custom API resource (if configured)

# Alternatively, verify in Azure Portal:
# Navigate to: Entra ID → Applications → [Blueprint App] → API permissions
# Verify custom permissions are listed with "Granted" status

# Record: Custom permissions visible in portal (Yes/No)
```

#### Test 4.7: Verify Inheritable Permissions via Graph API
```powershell
# Get blueprint object ID from config
$blueprintObjectId = (Get-Content a365.generated.config.json | ConvertFrom-Json).agentBlueprintObjectId

# Get access token
$token = az account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv

# Query inheritable permissions (this is what the CLI verifies internally)
$headers = @{ Authorization = "Bearer $token" }
$uri = "https://graph.microsoft.com/beta/applications/microsoft.graph.agentIdentityBlueprint/$blueprintObjectId/inheritablePermissions"
$response = Invoke-RestMethod -Uri $uri -Headers $headers
$response | ConvertTo-Json -Depth 10

# Expected response format:
# {
#   "value": [
#     {
#       "resourceAppId": "00000003-0000-0000-c000-000000000000",
#       "resourceName": "Microsoft Graph",
#       "scopes": ["Presence.ReadWrite", "Files.Read.All"]
#     }
#   ]
# }

# Verify:
# - Each custom resource appears in the "value" array
# - resourceAppId matches configured permissions
# - resourceName is populated (auto-resolved during setup)
# - All requested scopes are present

# Note: This is the SAME endpoint the CLI uses to verify permissions were set correctly
# If this query succeeds, inheritable permissions are working properly

# Record: Inheritable permissions verified via Graph API (Yes/No)
# Record: Number of resources found in response
```

**Section 4 Status**: ✅ Pass | ❌ Fail
**Notes**:

---

### Section 5: Setup Command - All-in-One
**Objective**: Test complete setup in a single command

#### Test 5.1: Setup All (New Environment)
```bash
# Clean up previous test artifacts
a365 cleanup

# Create new test config for all-in-one setup
# Use a365 config init with new agent name

# Run complete setup
a365 setup all

# Expected:
# - Infrastructure created
# - Blueprint created
# - MCP permissions configured
# - Bot API permissions configured
# - Custom blueprint permissions configured (if present in config)
# - Messaging endpoint registered
# - All steps completed successfully

# Verify custom permissions were configured (if Test 2.5 was completed):
# - Check output for "Configuring custom blueprint permissions..."
# - Verify each custom resource shows "configured successfully"

# Record: Setup all completed (Yes/No)
# Record: Custom permissions included (Yes/No/N/A)
# Record: Time taken (approximate)
```

#### Test 5.2: Setup All - Skip Infrastructure
```bash
# Run setup skipping infrastructure (if it already exists)
a365 setup all --skip-infrastructure

# Expected: Blueprint and permissions configured, infrastructure skipped
# Record: Skipped infrastructure correctly (Yes/No)
```

**Section 5 Status**: ✅ Pass | ❌ Fail  
**Notes**:

---

### Section 6: Development Commands - MCP Management
**Objective**: Test MCP server management features

#### Test 6.1: List Available MCP Servers
```bash
# List MCP servers from catalog
a365 develop list-available

# Expected: Shows available MCP servers from Agent365 Tools service
# Record: List displayed (Yes/No)
# Record: Number of servers shown
```

#### Test 6.2: List Configured MCP Servers
```bash
# List locally configured MCP servers
a365 develop list-configured

# Expected: Shows MCP servers configured in local project
# Record: List displayed (Yes/No)
```

#### Test 6.3: Add MCP Server
```bash
# Add an MCP server (user should choose one from available list)
a365 develop add

# Follow prompts to select and configure MCP server
# Expected: MCP server added to configuration
# Record: MCP server added (Yes/No)
# Record: Server name
```

#### Test 6.4: Get Token for MCP Authentication
```bash
# Get authentication token for MCP
a365 develop gettoken

# Expected: Displays access token for MCP authentication
# Record: Token retrieved (Yes/No)
```

#### Test 6.5: Add MCP Permissions
```bash
# Add permissions for MCP tools
a365 develop addpermissions

# Expected: Permissions added to blueprint for MCP tools
# Record: Permissions added (Yes/No)
```

#### Test 6.6: Start Mock Tooling Server
```bash
# Start mock tooling server for local testing
a365 develop start-mock-tooling-server

# Expected: Mock server starts (background process)
# Record: Server started (Yes/No)
# Note: Stop server after test
```

#### Test 6.7: Remove MCP Server
```bash
# Remove previously added MCP server
a365 develop remove

# Follow prompts to select server to remove
# Expected: MCP server removed from configuration
# Record: MCP server removed (Yes/No)
```

**Section 6 Status**: ✅ Pass | ❌ Fail  
**Notes**:

---

### Section 7: Development Commands - Dataverse MCP
**Objective**: Test Dataverse-hosted MCP server management

#### Test 7.1: List Dataverse MCP Servers
```bash
# List MCP servers in Dataverse environment
a365 develop-mcp list

# Expected: Shows MCP servers deployed to Dataverse
# Record: List displayed (Yes/No)
```

#### Test 7.2: Add Dataverse MCP Server
```bash
# Add MCP server to Dataverse (if applicable)
a365 develop-mcp add

# Expected: MCP server registered in Dataverse
# Record: Server added (Yes/No)
# Note: May require Dataverse environment setup
```

**Section 7 Status**: ✅ Pass | ❌ Fail  
**Notes**:

---

### Section 8: Deployment Commands
**Objective**: Test application deployment to Azure

#### Test 8.1: Create Test Application
```bash
# Create a simple test application
New-Item -ItemType Directory -Path "./test-app" -Force
Set-Location "./test-app"

# Create .NET app
dotnet new webapi -n TestAgent
Copy-Item "./TestAgent/*" "./" -Recurse

# Or create Python app
@"
from flask import Flask
app = Flask(__name__)

@app.route('/')
def hello():
    return 'Hello from Agent365 Test!'

if __name__ == '__main__':
    app.run()
"@ | Out-File -FilePath "app.py"

@"
flask>=3.0.0
"@ | Out-File -FilePath "requirements.txt"

Set-Location ..

# Expected: Test application created
# Record: Platform (DotNet/Python/Node)
```

#### Test 8.2: Deploy Application (Dry Run)
```bash
# Dry run deployment
a365 deploy --dry-run

# Expected: Shows deployment plan without executing
# Record: Dry run successful (Yes/No)
```

#### Test 8.3: Deploy Application (Actual)
```bash
# Deploy application to Azure
a365 deploy

# Expected:
# - Application built
# - Artifacts packaged
# - Uploaded to Azure App Service
# - Web app restarted
# - Deployment successful

# Record: Deployment succeeded (Yes/No)
# Record: Deployment time (approximate)
```

#### Test 8.4: Deploy with Inspect Option
```bash
# Deploy with inspection pause
a365 deploy --inspect

# Expected: Pauses before deployment to inspect artifacts
# User can verify publish folder and ZIP contents
# Record: Inspection worked (Yes/No)
```

#### Test 8.5: Deploy Subcommand - App Only
```bash
# Deploy only application binaries
a365 deploy app

# Expected: Application deployed without MCP updates
# Record: App deployed (Yes/No)
```

#### Test 8.6: Deploy Subcommand - MCP Only
```bash
# Update only MCP permissions
a365 deploy mcp

# Expected: MCP permissions updated without app deployment
# Record: MCP updated (Yes/No)
```

**Section 8 Status**: ✅ Pass | ❌ Fail  
**Notes**:

---

### Section 9: Publish Command
**Objective**: Test manifest publication to MOS

#### Test 9.1: Create Manifest File
```bash
# Create test manifest.json
$manifest = @{
    manifestVersion = "1.0"
    id = "test-agent-$(Get-Random)"
    version = "1.0.0"
    name = @{
        short = "Test Agent"
        full = "Test Agent for Integration Testing"
    }
    description = @{
        short = "Integration test agent"
        full = "Agent created for integration testing purposes"
    }
    developer = @{
        name = "Test Developer"
        websiteUrl = "https://example.com"
        privacyUrl = "https://example.com/privacy"
        termsOfUseUrl = "https://example.com/terms"
    }
} | ConvertTo-Json -Depth 10

$manifest | Out-File -FilePath "manifest.json"

# Record: Manifest created (Yes/No)
```

#### Test 9.2: Publish Manifest (Dry Run)
```bash
# Dry run publish
a365 publish --dry-run

# Expected: Shows what would be published
# Record: Dry run successful (Yes/No)
```

#### Test 9.3: Publish Manifest (Actual)
```bash
# Publish to MOS
a365 publish

# Expected:
# - Manifest updated with blueprint IDs
# - Package created
# - Published to MOS (Microsoft Online Services)
# - Federated identity configured
# - App role assignments updated

# Record: Publish succeeded (Yes/No)
# Note: After publish, hire agent through Teams to complete onboarding
```

**Section 9 Status**: ✅ Pass | ❌ Fail  
**Notes**:

---

### Section 10: Query Commands
**Objective**: Test Entra ID query functionality

#### Test 10.1: Query Blueprint Scopes
```bash
# Query blueprint scopes
a365 query-entra blueprint-scopes

# Expected: Lists all configured scopes for blueprint
# - Scope names
# - Consent status
# - Admin consent requirement

# Record: Scopes listed (Yes/No)
# Record: Number of scopes
```

#### Test 10.2: Query Instance Scopes
```bash
# Query instance scopes
a365 query-entra instance-scopes

# Expected: Lists scopes for agent instance
# Note: Requires instance to be created first

# Record: Scopes listed (Yes/No)
```

**Section 10 Status**: ✅ Pass | ❌ Fail  
**Notes**:

---

### Section 11: Cleanup Commands
**Objective**: Test resource cleanup functionality

#### Test 11.1: Cleanup Blueprint (Dry Run)
```bash
# Dry run blueprint cleanup
a365 cleanup blueprint --dry-run

# Expected: Shows what would be deleted
# Record: Dry run successful (Yes/No)
```

#### Test 11.2: Cleanup Blueprint (Actual)
```bash
# Clean up blueprint
a365 cleanup blueprint

# Expected:
# - Blueprint application deleted from Entra ID
# - Service principal removed
# - Federated credentials removed

# Record: Blueprint cleaned up (Yes/No)
```

#### Test 11.3: Cleanup Azure Resources
```bash
# Clean up Azure infrastructure
a365 cleanup azure

# Expected:
# - Web app deleted
# - App Service Plan deleted (if not shared)
# - Resource group deleted (with confirmation)

# Record: Azure resources cleaned up (Yes/No)
```

#### Test 11.4: Cleanup Instance
```bash
# Clean up agent instance
a365 cleanup instance

# Expected:
# - Agent instance removed
# - Associated resources cleaned up

# Record: Instance cleaned up (Yes/No)
```

#### Test 11.5: Cleanup All
```bash
# Clean up everything
a365 cleanup

# Expected: Prompts for confirmation, then removes all resources
# - Blueprint
# - Instance
# - Azure infrastructure

# Record: All resources cleaned up (Yes/No)
```

**Section 11 Status**: ✅ Pass | ❌ Fail  
**Notes**:

---

### Section 12: Error Handling and Edge Cases
**Objective**: Test error handling and edge case scenarios

#### Test 12.1: Missing Configuration
```bash
# Remove config file
Remove-Item "a365.config.json" -Force

# Try to run command without config
a365 setup blueprint

# Expected: Clear error message about missing configuration
# Record: Error handled gracefully (Yes/No)
```

#### Test 12.2: Invalid Configuration
```bash
# Create invalid config
@"
{
    "invalid": "config"
}
"@ | Out-File -FilePath "a365.config.json"

# Try to use invalid config
a365 setup blueprint

# Expected: Validation error with specific fields listed
# Record: Validation errors shown (Yes/No)
```

#### Test 12.3: Network Error Handling
```bash
# Test with invalid Azure credentials
az logout

# Try to run command
a365 setup infrastructure

# Expected: Clear error about authentication failure
# Record: Error handled gracefully (Yes/No)

# Re-authenticate
az login
```

#### Test 12.4: Verbose Logging
```bash
# Test verbose mode
a365 deploy --verbose

# Expected: Detailed debug logs shown
# Record: Verbose logging works (Yes/No)
```

#### Test 12.5: Help Documentation
```bash
# Test help for each command
a365 --help
a365 config --help
a365 setup --help
a365 deploy --help
a365 publish --help
a365 query-entra --help
a365 develop --help
a365 cleanup --help

# Expected: Help text displayed for all commands
# Record: All help texts accessible (Yes/No)
```

**Section 12 Status**: ✅ Pass | ❌ Fail  
**Notes**:

---

### Section 13: Cross-Platform Testing (Windows/Linux/macOS)
**Objective**: Verify cross-platform compatibility

#### Test 13.1: Path Handling
```bash
# Test with various path formats
# Windows: C:\path\to\project
# Linux/macOS: /path/to/project

# Create config with platform-specific paths
# Deploy and verify paths are handled correctly

# Record: Cross-platform paths work (Yes/No)
```

#### Test 13.2: Line Endings
```bash
# Test config files with different line endings
# CRLF (Windows) vs LF (Unix)

# Expected: Both formats handled correctly
# Record: Line endings handled (Yes/No)
```

#### Test 13.3: Shell Commands
```bash
# Test shell command execution on current platform
# PowerShell (Windows) vs Bash (Linux/macOS)

# Expected: Commands execute correctly on current platform
# Record: Shell commands work (Yes/No)
```

**Section 13 Status**: ✅ Pass | ❌ Fail  
**Notes**:

---

## Test Summary Report

### Overall Results
```
Total Sections: 13
Sections Passed: ___
Sections Failed: ___
Pass Rate: ___%
```

### Detailed Results
| Section | Status | Critical Issues | Notes |
|---------|--------|----------------|-------|
| 1. Installation | ✅/❌ | | |
| 2. Configuration | ✅/❌ | | |
| 3. Setup - Infrastructure | ✅/❌ | | |
| 4. Setup - Blueprint | ✅/❌ | | |
| 5. Setup - All-in-One | ✅/❌ | | |
| 6. Develop - MCP | ✅/❌ | | |
| 7. Develop - Dataverse | ✅/❌ | | |
| 8. Deployment | ✅/❌ | | |
| 9. Publish | ✅/❌ | | |
| 10. Query | ✅/❌ | | |
| 11. Cleanup | ✅/❌ | | |
| 12. Error Handling | ✅/❌ | | |
| 13. Cross-Platform | ✅/❌ | | |

### Critical Issues Found
1. 
2. 
3. 

### Warnings/Non-Critical Issues
1. 
2. 
3. 

### Performance Metrics
- Total test time: ___ minutes
- Average command execution time: ___ seconds
- Slowest command: ___ (___ seconds)

### Environment Information
- OS: Windows/Linux/macOS
- .NET SDK Version: ___
- Azure CLI Version: ___
- CLI Version: ___
- Test Date: ___
- Tester: ___

### Regression Check
Compare with previous test run:
- New failures: ___
- Resolved issues: ___
- New warnings: ___

### Recommendations
1. 
2. 
3. 

---

## Post-Test Cleanup

```bash
# Clean up test environment
Set-Location $HOME
Remove-Item -Path $testWorkspace -Recurse -Force

# Clean up Azure resources if needed
az group delete --name $testResourceGroup --yes --no-wait

# Uninstall CLI (optional)
dotnet tool uninstall -g Microsoft.Agents.A365.DevTools.Cli
```

---

## Usage Instructions for AI Agents

### How to Execute This Workflow

1. **Read the entire workflow** before starting
2. **Ask user for prerequisites** (tenant ID, subscription ID, client ID, etc.)
3. **Execute tests sequentially** by section
4. **Record results** for each test in the workflow
5. **Handle failures gracefully**:
   - Note the failure
   - Continue to next test unless it's a blocking dependency
   - Ask user if they want to skip remaining tests in failed section
6. **Provide progress updates** after each section
7. **Generate final report** at the end

### Handling User Input

When the workflow requires user input:
1. **Clearly explain** what information is needed and why
2. **Provide examples** of expected input format
3. **Validate input** before proceeding
4. **Confirm** critical actions (like deletions) before executing

### Error Recovery

If a test fails:
1. **Capture error details** (error message, stack trace if available)
2. **Record in test results**
3. **Ask user** if they want to:
   - Retry the test
   - Skip to next test
   - Abort the workflow
4. **Suggest troubleshooting steps** based on error type

### Reporting

Throughout execution:
- Provide real-time status updates
- Show progress (e.g., "Section 3 of 13 complete")
- Highlight critical failures immediately
- At the end, generate the complete test summary report

---

## Notes

- This workflow is designed to be **comprehensive** but **flexible**
- Tests can be run **individually** or as a **complete suite**
- Some tests may require **manual verification** (e.g., Teams integration)
- The workflow should be **updated** as new features are added to the CLI
- Test results should be **saved** for regression comparison

---

## Version History

- **v1.0** (2026-02-05): Initial workflow creation
  - Covers all major CLI commands
  - Includes error handling tests
  - Cross-platform considerations

---

**End of Integration Test Workflow**
