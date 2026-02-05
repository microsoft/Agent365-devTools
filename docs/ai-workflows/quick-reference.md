# Integration Test Quick Reference

Quick reference for executing the Agent365 DevTools CLI integration test workflow.

---

## Prerequisites Checklist

Before starting, ensure you have:

```
[ ] Azure subscription with admin access
[ ] Custom Entra ID client app with delegated permissions
[ ] Admin consent granted for client app
[ ] .NET 8.0 SDK installed
[ ] Azure CLI installed and authenticated
[ ] PowerShell 7+ (or bash for Linux/macOS)
[ ] ~45 minutes of uninterrupted time
```

---

## Quick Start Commands

### Setup Test Environment
```powershell
# Set test variables
$env:AGENT365_TEST_TENANT_ID = "your-tenant-id"
$env:AGENT365_TEST_SUBSCRIPTION_ID = "your-subscription-id"
$env:AGENT365_TEST_CLIENT_ID = "your-client-app-id"
$env:AGENT365_TEST_MANAGER_EMAIL = "manager@domain.com"

# Create test workspace
$testWorkspace = "$HOME\a365-test-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
New-Item -ItemType Directory -Path $testWorkspace -Force
Set-Location $testWorkspace
```

### Verify Prerequisites
```powershell
dotnet --version        # Should be 8.0.x+
az --version           # Should show version
az account show        # Should show subscription
```

---

## Test Execution Checklist

### Section 1: Installation ✅ ❌
- [ ] 1.1: Install CLI from NuGet
- [ ] 1.2: Verify installation
- [ ] 1.3: Check for updates

### Section 2: Configuration ✅ ❌
- [ ] 2.1: Interactive configuration wizard
- [ ] 2.2: Validate generated configuration
- [ ] 2.3: Import configuration from file
- [ ] 2.4: Global configuration

### Section 3: Setup - Infrastructure ✅ ❌
- [ ] 3.1: Check requirements
- [ ] 3.2: Create infrastructure (dry run)
- [ ] 3.3: Create infrastructure (actual)

### Section 4: Setup - Blueprint ✅ ❌
- [ ] 4.1: Create agent blueprint
- [ ] 4.2: Verify blueprint in Entra ID
- [ ] 4.3: Blueprint permissions - MCP
- [ ] 4.4: Blueprint permissions - Bot

### Section 5: Setup - All-in-One ✅ ❌
- [ ] 5.1: Setup all (new environment)
- [ ] 5.2: Setup all --skip-infrastructure

### Section 6: Develop - MCP ✅ ❌
- [ ] 6.1: List available MCP servers
- [ ] 6.2: List configured MCP servers
- [ ] 6.3: Add MCP server
- [ ] 6.4: Get token for MCP authentication
- [ ] 6.5: Add MCP permissions
- [ ] 6.6: Start mock tooling server
- [ ] 6.7: Remove MCP server

### Section 7: Develop - Dataverse ✅ ❌
- [ ] 7.1: List Dataverse MCP servers
- [ ] 7.2: Add Dataverse MCP server

### Section 8: Deployment ✅ ❌
- [ ] 8.1: Create test application
- [ ] 8.2: Deploy application (dry run)
- [ ] 8.3: Deploy application (actual)
- [ ] 8.4: Deploy with inspect option
- [ ] 8.5: Deploy app subcommand
- [ ] 8.6: Deploy mcp subcommand

### Section 9: Publish ✅ ❌
- [ ] 9.1: Create manifest file
- [ ] 9.2: Publish manifest (dry run)
- [ ] 9.3: Publish manifest (actual)

### Section 10: Query ✅ ❌
- [ ] 10.1: Query blueprint scopes
- [ ] 10.2: Query instance scopes

### Section 11: Cleanup ✅ ❌
- [ ] 11.1: Cleanup blueprint (dry run)
- [ ] 11.2: Cleanup blueprint (actual)
- [ ] 11.3: Cleanup Azure resources
- [ ] 11.4: Cleanup instance
- [ ] 11.5: Cleanup all

### Section 12: Error Handling ✅ ❌
- [ ] 12.1: Missing configuration
- [ ] 12.2: Invalid configuration
- [ ] 12.3: Network error handling
- [ ] 12.4: Verbose logging
- [ ] 12.5: Help documentation

### Section 13: Cross-Platform ✅ ❌
- [ ] 13.1: Path handling
- [ ] 13.2: Line endings
- [ ] 13.3: Shell commands

---

## Common Commands Reference

### Installation
```bash
dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli --prerelease
a365 --version
```

### Configuration
```bash
a365 config init                    # Interactive wizard
a365 config init -c config.json     # Import from file
a365 config init --global           # Global config
a365 config display                 # Show current config
```

### Setup
```bash
a365 setup requirements             # Check prerequisites
a365 setup infrastructure           # Create Azure resources
a365 setup blueprint                # Create agent blueprint
a365 setup permissions mcp          # Configure MCP permissions
a365 setup permissions bot          # Configure bot permissions
a365 setup all                      # All steps at once
a365 setup all --skip-infrastructure # Skip infrastructure
```

### Develop
```bash
a365 develop list-available         # List MCP servers in catalog
a365 develop list-configured        # List local MCP servers
a365 develop add                    # Add MCP server
a365 develop remove                 # Remove MCP server
a365 develop gettoken               # Get auth token
a365 develop addpermissions         # Add MCP permissions
a365 develop start-mock-tooling-server # Start mock server
```

### Deploy
```bash
a365 deploy                         # Deploy application
a365 deploy --dry-run               # Show deployment plan
a365 deploy --inspect               # Pause before deploy
a365 deploy --restart               # Use existing build
a365 deploy app                     # Deploy app only
a365 deploy mcp                     # Update MCP only
```

### Publish
```bash
a365 publish                        # Publish to MOS
a365 publish --dry-run              # Show publish plan
```

### Query
```bash
a365 query-entra blueprint-scopes   # List blueprint scopes
a365 query-entra instance-scopes    # List instance scopes
```

### Cleanup
```bash
a365 cleanup blueprint              # Remove blueprint
a365 cleanup azure                  # Remove Azure resources
a365 cleanup instance               # Remove instance
a365 cleanup                        # Remove everything
```

### Global Options
```bash
--config, -c <file>                 # Specify config file
--verbose, -v                       # Enable verbose logging
--dry-run                           # Show plan without executing
--help, -h                          # Show help
```

---

## Test Data Templates

### Minimal Configuration
```json
{
  "tenantId": "00000000-0000-0000-0000-000000000000",
  "subscriptionId": "00000000-0000-0000-0000-000000000000",
  "resourceGroup": "rg-a365-test",
  "location": "westus",
  "webAppName": "webapp-test-1234",
  "agentIdentityDisplayName": "Test Agent Identity",
  "agentUserPrincipalName": "test.agent@domain.onmicrosoft.com",
  "deploymentProjectPath": "./test-app",
  "managerEmail": "manager@domain.com"
}
```

### Test Agent Names
Use these patterns for test agents:
- `TestAgent1234` (random number)
- `IntegrationTestAgent`
- `QATestAgent$(Get-Date -Format 'MMdd')`

### Test Resource Groups
Naming convention:
- `rg-a365-test-[random]`
- `rg-a365-integration-test`
- `rg-a365-qa-[date]`

---

## Expected Durations

| Section | Tests | Time |
|---------|-------|------|
| 1. Installation | 3 | 2 min |
| 2. Configuration | 4 | 5-7 min |
| 3. Setup - Infrastructure | 3 | 3-5 min |
| 4. Setup - Blueprint | 4 | 4-6 min |
| 5. Setup - All | 2 | 5-8 min |
| 6. Develop - MCP | 7 | 6-8 min |
| 7. Develop - Dataverse | 2 | 2-3 min |
| 8. Deployment | 6 | 8-12 min |
| 9. Publish | 3 | 3-5 min |
| 10. Query | 2 | 2-3 min |
| 11. Cleanup | 5 | 3-5 min |
| 12. Error Handling | 5 | 3-4 min |
| 13. Cross-Platform | 3 | 2-3 min |
| **Total** | **49** | **30-45 min** |

---

## Troubleshooting Quick Reference

### Issue: CLI Not Found
```bash
# Verify installation
dotnet tool list -g | Select-String "Agent365"

# Reinstall if needed
dotnet tool uninstall -g Microsoft.Agents.A365.DevTools.Cli
dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli --prerelease

# Restart terminal
```

### Issue: Azure Authentication
```bash
# Check current login
az account show

# Re-login if needed
az logout
az login

# Set subscription
az account set --subscription "your-subscription-id"
```

### Issue: Insufficient Permissions
```
Error: Insufficient privileges to complete the operation.

Solution:
1. Verify custom client app has required delegated permissions
2. Ensure admin consent is granted
3. Check you're logged in with admin account
4. Review: a365 setup requirements
```

### Issue: Resource Already Exists
```
Error: Resource 'xxx' already exists.

Solution:
1. Use different resource name
2. Or clean up existing: a365 cleanup
3. Or reuse existing and continue
```

### Issue: Configuration Invalid
```bash
# Validate configuration
a365 config display

# Check for missing required fields
# Reinitialize if needed
a365 config init
```

---

## Result Recording Template

### Quick Record Format
```
Test X.Y: [Test Name]
Status: PASS/FAIL/SKIP
Time: __s
Notes: ___________________
```

### Section Summary Format
```
Section X: [Name]
Passed: __/__
Failed: __/__
Duration: __m __s
Issues: ___________________
```

---

## Pass/Fail Criteria

### Pass Criteria
- Command exits with code 0
- Expected output matches actual
- Resources created successfully
- Configuration updated correctly
- No critical errors

### Fail Criteria
- Non-zero exit code
- Error messages displayed
- Expected resources not created
- Configuration invalid/missing
- Critical functionality broken

### Warning (Not Failure)
- Informational warnings
- Non-critical deprecations
- Performance slower than expected
- Minor UI inconsistencies

---

## Post-Test Actions

### If All Tests Pass ✅
```
1. Generate final report
2. Save report to file
3. Clean up test resources
4. Archive test artifacts
5. Update last-test-date
```

### If Tests Fail ❌
```
1. Document failures
2. Capture error logs
3. Create GitHub issues for bugs
4. Don't clean up (for debugging)
5. Share results with team
```

### Always Do
```
1. Record test date and version
2. Note any manual interventions
3. List environment specifics
4. Archive configuration used
5. Update regression baseline
```

---

## Quick Command Sequences

### Full Happy Path (45 min)
```bash
# Install
dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli --prerelease

# Configure
a365 config init

# Setup everything
a365 setup all

# Create test app
dotnet new webapi -n TestAgent

# Deploy
a365 deploy

# Publish
a365 publish

# Query
a365 query-entra blueprint-scopes

# Cleanup
a365 cleanup
```

### Quick Sanity Check (5 min)
```bash
a365 --version
a365 config init
a365 setup requirements
a365 deploy --dry-run
a365 cleanup --dry-run
```

### Configuration Only (5 min)
```bash
a365 config init
a365 config display
a365 config init -c test-config.json
a365 config init --global
```

---

## Files Created During Testing

Watch for these files:
```
./
├── a365.config.json              # Configuration
├── test-config.json              # Test import config
├── manifest.json                 # Agent manifest
├── test-app/                     # Test application
│   ├── *.cs / *.py / *.js       # Source files
│   └── bin/publish/              # Build artifacts
└── test-report-[date].md         # Test results
```

Global config location:
```
Windows: %APPDATA%\Microsoft\Agent365DevTools\a365.config.json
Linux/Mac: ~/.config/Microsoft/Agent365DevTools/a365.config.json
```

---

## Environment Variables

Useful environment variables:
```powershell
# Test configuration
$env:AGENT365_TEST_TENANT_ID
$env:AGENT365_TEST_SUBSCRIPTION_ID
$env:AGENT365_TEST_CLIENT_ID
$env:AGENT365_TEST_MANAGER_EMAIL

# CLI configuration (optional)
$env:AGENT365_CONFIG_PATH           # Override default config location
$env:MOS_TITLES_URL                 # Override MOS endpoint (testing)
```

---

## Emergency Stop

If you need to stop testing:

1. **Cancel current command**: `Ctrl+C`
2. **Note progress**: Record last completed test
3. **Save state**: Keep test workspace intact
4. **Resume later**: Start from last completed section

Do NOT delete test workspace until you:
- Review any failures
- Capture error logs
- Screenshot issues
- Save configuration

---

## Success Metrics

Target metrics for a successful test run:
- **Pass rate**: ≥ 95% (47+ of 49 tests)
- **Critical failures**: 0
- **Total duration**: ≤ 50 minutes
- **Manual interventions**: ≤ 3
- **Blocking errors**: 0

---

## Contact

For help during testing:
- **Issues**: File at github.com/microsoft/Agent365-devTools/issues
- **Docs**: learn.microsoft.com/microsoft-agent-365
- **Security**: See SECURITY.md

---

**Last Updated**: February 5, 2026  
**Workflow Version**: 1.0  
**For Full Workflow**: See [integration-test-workflow.md](./integration-test-workflow.md)
