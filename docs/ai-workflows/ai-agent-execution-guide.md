# AI Agent Guide: Executing Integration Test Workflow

## Overview
This guide provides instructions for AI agents on how to effectively execute the Agent365 DevTools CLI integration test workflow. It covers execution strategies, user interaction patterns, error handling, and reporting.

---

## Before You Start

### 1. Understand Your Role
As an AI agent executing this workflow, you are:
- **A test facilitator**: Guiding the user through comprehensive testing
- **An automation assistant**: Running commands and collecting results
- **A diagnostician**: Identifying issues and suggesting fixes
- **A reporter**: Documenting results for regression analysis

### 2. Read the Workflow
Before starting execution:
1. Load [integration-test-workflow.md](./integration-test-workflow.md)
2. Review all test sections
3. Understand dependencies between sections
4. Identify which tests require user input
5. Note tests that can be run in parallel (none in this workflow)

### 3. Set Expectations with User
Begin the session by:
```
"I'll guide you through comprehensive integration testing of the Agent365 DevTools CLI. 
This workflow has 13 sections covering all major commands and edge cases.

Estimated time: 30-45 minutes
Prerequisites needed:
- Azure subscription with admin access
- Custom Entra ID client app with required permissions
- .NET 8.0 SDK installed
- Azure CLI authenticated

Before we begin, I'll collect necessary information and verify prerequisites.
Would you like to proceed?"
```

---

## Execution Strategy

### Phase 1: Information Gathering (5-10 minutes)

#### Step 1: Collect Prerequisites
Ask user for required information:

```
"Let's gather the information needed for testing:

1. Azure Tenant ID: [Explain: Your Azure AD tenant identifier]
2. Azure Subscription ID: [Explain: Target subscription for test resources]
3. Custom Client App ID: [Explain: Your Entra ID app with delegated permissions]
4. Manager Email: [Explain: Email for agent manager assignment]
5. Test Resource Group Prefix: [Suggest: 'rg-a365-test']

Would you like me to help you find any of these values?"
```

#### Step 2: Verify Prerequisites
Run verification commands:

```powershell
# Check .NET SDK
dotnet --version
# Expected: 8.0.x or higher

# Check Azure CLI
az --version
# Expected: Version info displayed

# Check Azure CLI authentication
az account show
# Expected: Shows current subscription

# Check if CLI is already installed
a365 --version
# Expected: May fail if not installed (this is OK)
```

Document results:
```
"Verification Results:
✅ .NET SDK: 8.0.xxx
✅ Azure CLI: 2.xx.x (authenticated as user@domain.com)
❌ a365 CLI: Not installed (we'll install it in Section 1)

Ready to proceed with testing."
```

### Phase 2: Test Execution (25-35 minutes)

#### Execution Pattern for Each Section

For each test section, follow this pattern:

1. **Announce Section**
   ```
   "Starting Section X: [Section Name]
   Objective: [Objective]
   Number of tests: X
   Estimated time: X minutes"
   ```

2. **Execute Each Test**
   - Display test number and description
   - Show command before running
   - Execute command
   - Capture output
   - Record result
   - Handle errors if any

3. **Report Section Results**
   ```
   "Section X Complete:
   ✅ Passed: X/Y tests
   ❌ Failed: X/Y tests
   ⚠️ Warnings: X
   
   Critical issues: [List if any]
   Ready to proceed to Section X+1?"
   ```

#### Example Test Execution

```
"Test 1.1: Install CLI from NuGet
Command: dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli --prerelease

[Execute command]

Output:
[Show command output]

Result: ✅ Pass
- Installation succeeded
- Version X.X.X installed
- Tool accessible globally

Recorded: Installation succeeded = Yes
---"
```

### Phase 3: Reporting (5 minutes)

Generate comprehensive test summary at the end.

---

## User Interaction Patterns

### Pattern 1: Collecting Input

When you need user input:

**Good:**
```
"For Test 2.1, I need to run the interactive configuration wizard.
This will prompt you for several values:

1. Agent name (alphanumeric only)
   - Suggestion: TestAgent$(Get-Random -Maximum 9999)
   - Purpose: Unique identifier for your test agent

2. Deployment project path
   - Suggestion: ./test-app
   - Purpose: Where your agent code will be located

[Continue for all prompts]

I'll run the command now. Please answer the prompts when they appear.
Ready? Type 'yes' to proceed."
```

**Poor:**
```
"Running a365 config init. Answer the questions."
```

### Pattern 2: Confirming Destructive Actions

Before cleanup or deletion:

**Good:**
```
"Test 11.3 will delete Azure resources:
- Web app: webapp-test-1234
- Resource group: rg-a365-test-1234
- All associated resources

This is a destructive action. The command will prompt for confirmation.
Shall I proceed with the test?"
```

**Poor:**
```
"Deleting resources now."
```

### Pattern 3: Handling Test Failures

When a test fails:

**Good:**
```
"Test 4.1 Failed: Create Agent Blueprint

Error: 
[Show error message]

Analysis:
- Likely cause: Insufficient permissions on custom client app
- Impact: Blocks dependent tests (4.2, 4.3, 4.4)

Options:
1. Troubleshoot and retry Test 4.1
2. Skip Section 4 and continue with Section 5
3. Abort workflow and fix prerequisites

What would you like to do? (1/2/3)"
```

**Poor:**
```
"Error occurred. Moving to next test."
```

### Pattern 4: Providing Progress Updates

After each section:

**Good:**
```
"Progress Update:
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Completed: 4/13 sections (31%)
Time elapsed: ~12 minutes
Estimated remaining: ~23 minutes

Results so far:
✅ Sections passed: 3
❌ Sections failed: 1
⚠️ Total warnings: 2

Current status: On track
Next: Section 5 - Setup All-in-One
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
```

**Poor:**
```
"Section 4 done. Next section."
```

---

## Error Handling Strategies

### Strategy 1: Categorize Errors

Classify each error:
- **Blocker**: Prevents all subsequent tests (e.g., authentication failure)
- **Section Blocker**: Prevents tests in current section only
- **Warning**: Test failed but doesn't block others
- **Expected**: Error is part of test (e.g., testing error handling)

### Strategy 2: Error Recovery Actions

Based on category:

**Blocker:**
```
"CRITICAL ERROR in Test X.Y
This error blocks all remaining tests.

Error: [Details]

Recommended Actions:
1. Fix: [Specific fix steps]
2. If fixed, restart workflow from current section
3. Or abort and resolve prerequisites

Shall I attempt automatic retry, or would you like to fix this manually?"
```

**Section Blocker:**
```
"ERROR in Test X.Y
This error blocks remaining tests in Section X.

Error: [Details]

I can:
1. Skip to Section X+1 (recommended)
2. Retry Test X.Y after you fix the issue
3. Mark Section X as failed and continue

Choose option (1/2/3):"
```

**Warning:**
```
"⚠️ WARNING in Test X.Y
Test failed but doesn't block other tests.

Error: [Details]

Recorded as: Test X.Y = FAIL
Continuing with Test X.Y+1..."
```

### Strategy 3: Detailed Error Logging

For each error, record:
```
Error Record:
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Test: 4.1 - Create Agent Blueprint
Command: a365 setup blueprint
Exit Code: 1

Error Output:
[Full error message]

Error Type: Section Blocker
Timestamp: 2026-02-05 14:32:15

User Action: Skipped to Section 5
Resolution: Will investigate after workflow
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

---

## Command Execution Best Practices

### Practice 1: Show Before Execute

Always show commands before running:
```
"Executing Test 3.2: Create Infrastructure (Dry Run)

Command:
┌────────────────────────────────────────┐
│ a365 setup infrastructure --dry-run   │
└────────────────────────────────────────┘

[Execute]
"
```

### Practice 2: Capture Complete Output

Save both stdout and stderr:
```powershell
# Use proper command execution
$output = & a365 setup infrastructure 2>&1 | Out-String
```

### Practice 3: Validate Success

Don't just check exit codes:
```
"Validating Test 3.3 result:
✓ Exit code: 0
✓ Resource group exists: [az group show]
✓ Web app exists: [az webapp show]
✓ Configuration updated: [Check a365.config.json]

Result: ✅ PASS"
```

### Practice 4: Handle Timeouts

For long-running commands:
```
"Test 8.3: Deploy Application
This may take 3-5 minutes...

[Progress indicators]
- Building application... ✅ Done (1m 23s)
- Packaging artifacts... ✅ Done (34s)
- Uploading to Azure... ⏳ In progress (1m 12s elapsed)
- Deployment... ⏳ Starting
"
```

---

## Result Recording

### Record Format

For each test, record in structured format:

```json
{
  "testId": "1.1",
  "section": "Installation",
  "testName": "Install CLI from NuGet",
  "status": "PASS|FAIL|SKIP",
  "timestamp": "2026-02-05T14:30:00Z",
  "duration": "12s",
  "command": "dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli --prerelease",
  "exitCode": 0,
  "output": "[command output]",
  "error": null,
  "notes": "Installation succeeded",
  "recordedValues": {
    "installationSucceeded": true,
    "version": "0.5.0"
  }
}
```

### Maintain Running Summary

Keep cumulative statistics:
```
Running Summary:
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Tests Run: 45
Passed: 42
Failed: 2
Skipped: 1
Pass Rate: 93.3%

Sections Complete: 8/13
Estimated Completion: 78%
Time Elapsed: 28m 15s
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

---

## Final Report Generation

### Report Structure

Generate comprehensive report:

```markdown
# Agent365 DevTools CLI Integration Test Report
Generated: 2026-02-05 15:45:23
Duration: 37m 42s
Tester: [User name/AI Agent]
Environment: Windows 11, .NET 8.0.2, Azure CLI 2.57.0

## Executive Summary
✅ Overall Status: PASS
Pass Rate: 94.7% (54/57 tests)

Critical Issues: 0
Warnings: 3
Sections Failed: 0 (minor test failures only)

## Section Results
[Detailed table from workflow]

## Failed Tests Detail
### Test 4.3: Blueprint Permissions - MCP
- Command: a365 setup permissions mcp
- Error: Permission already exists
- Impact: Non-critical, likely duplicate run
- Resolution: None needed

[Continue for each failed test]

## Performance Metrics
- Fastest command: a365 --version (0.2s)
- Slowest command: a365 deploy (4m 32s)
- Average command time: 12.3s

## Environment Info
[Complete environment details]

## Regression Analysis
Compared to previous run (2026-01-28):
- New failures: 0
- Resolved failures: 2
- Performance: 5% faster

## Recommendations
1. Document expected behavior for Test 4.3 duplicate scenario
2. Improve error message clarity in Test 8.2
3. Add retry logic for transient Azure errors

## Detailed Test Log
[Complete log of all commands and outputs]

## Appendix
- Test environment configuration
- Azure resources created
- Configuration files used
```

### Report Delivery

Present report in stages:

1. **Immediate**: Show summary as tests complete
2. **Final**: Generate full report at end
3. **Formatted**: Offer to save as markdown file
4. **Actionable**: Highlight items needing attention

Example delivery:
```
"Testing Complete! 🎉

Summary:
✅ 54/57 tests passed (94.7%)
⚠️ 3 minor issues found
❌ 0 critical failures

The CLI appears stable with no regressions detected.

I've generated a detailed report. Would you like me to:
1. Display the full report here
2. Save it to a file (integration-test-report-[date].md)
3. Show only critical issues and recommendations
4. All of the above

What would you prefer?"
```

---

## Special Considerations

### 1. Handling Long-Running Operations

For deployments and builds:
- Provide progress updates every 30 seconds
- Show estimated time remaining
- Allow user to cancel if needed
- Capture partial output if interrupted

### 2. Sensitive Information

Protect sensitive data:
- Redact access tokens in logs
- Mask subscription IDs in reports (show last 4 chars only)
- Don't save client secrets to files
- Warn before displaying sensitive info

Example:
```
"Test 6.4 retrieved an access token.

⚠️ This token provides authentication to your Azure resources.
Should I:
1. Display the token (for manual testing)
2. Save to environment variable (recommended)
3. Skip display (most secure)

Choose option (1/2/3):"
```

### 3. Resource Cleanup

Track created resources:
```
Resources Created During Testing:
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Resource Group: rg-a365-test-1234
Web App: webapp-test-5678
Blueprint App: app-test-9012
Service Principal: sp-test-3456
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Cleanup performed in Section 11: ✅
Manual verification recommended: az group list
```

### 4. Platform-Specific Behavior

Adapt to platform:
```powershell
# Detect platform
if ($IsWindows -or $env:OS -eq "Windows_NT") {
    $platform = "Windows"
    $pathSep = "\"
} else {
    $platform = "Unix"
    $pathSep = "/"
}

# Adjust commands accordingly
```

---

## Troubleshooting Guide for AI Agents

### Issue: Command Not Found

**Symptom**: `a365: command not found`

**AI Agent Action**:
```
"The CLI command isn't accessible. Possible causes:
1. Not installed yet (expected in Section 1)
2. PATH not updated (Windows terminal restart needed)
3. Installation failed

Current context: [Check which section we're in]

If this is Section 1, this is expected.
If this is Section 2+, let's verify installation:
[Run verification commands]

Shall I attempt reinstallation?"
```

### Issue: Authentication Failures

**Symptom**: Graph API calls return 401/403

**AI Agent Action**:
```
"Authentication error detected.

Diagnostic steps:
1. Verify Azure CLI auth: az account show
2. Check token expiry: [attempt token refresh]
3. Verify client app permissions: [list required permissions]

Results:
[Show diagnostic output]

Based on diagnostics, the issue appears to be:
[Root cause analysis]

Recommended fix:
[Specific steps]

Shall I guide you through the fix?"
```

### Issue: Resource Already Exists

**Symptom**: Azure returns 409 Conflict

**AI Agent Action**:
```
"Resource already exists: [resource name]

This could indicate:
1. Previous test run wasn't fully cleaned up
2. Resource name collision
3. Intentional rerun of same test

Options:
1. Use existing resource and continue (if safe)
2. Clean up and recreate (recommended for fresh test)
3. Generate new unique name and retry

Which option would you prefer? (1/2/3)"
```

---

## Best Practices Summary

### Do:
✅ Explain every step before executing  
✅ Provide context for user inputs  
✅ Show commands before running them  
✅ Capture complete output  
✅ Record detailed results  
✅ Offer options when errors occur  
✅ Give progress updates  
✅ Generate comprehensive reports  
✅ Protect sensitive information  
✅ Clean up test resources  

### Don't:
❌ Run destructive commands without confirmation  
❌ Hide error messages  
❌ Assume user knowledge  
❌ Skip validation steps  
❌ Ignore warnings  
❌ Continue blindly after critical failures  
❌ Display tokens unnecessarily  
❌ Leave orphaned resources  

---

## Example Complete Test Execution

Here's how a complete section should look:

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
SECTION 2: CONFIGURATION MANAGEMENT
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Objective: Test configuration initialization and validation
Tests: 4
Estimated time: 5-7 minutes

───────────────────────────────────────────────────────────────
Test 2.1: Interactive Configuration Wizard
───────────────────────────────────────────────────────────────
This test runs the interactive configuration wizard.
You'll be prompted for several values:

Required inputs:
1. Agent name: Suggest "TestAgent4523" (unique, alphanumeric)
2. Deployment path: Suggest "./test-app"
3. Manager email: Your email or test account
4. Azure subscription: Select from displayed list
5. Resource group: Create new with suggested name
6. Location: Suggest "westus"
7. App Service Plan: Create new with suggested name

Command:
┌────────────────────────────┐
│ a365 config init          │
└────────────────────────────┘

Ready to proceed? (yes/no): yes

[Execute command - interactive prompts appear]

Output:
Welcome to Agent365 CLI Configuration Wizard!
...
Configuration saved to: C:\Users\test\a365.config.json

Validation:
✅ Exit code: 0
✅ File created: a365.config.json
✅ File contains valid JSON
✅ Required fields present: 12/12

Result: ✅ PASS
Recorded: Configuration wizard completed = Yes
Duration: 2m 15s
───────────────────────────────────────────────────────────────

[Continue with Tests 2.2, 2.3, 2.4...]

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
SECTION 2 COMPLETE
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Results:
✅ Passed: 4/4 tests (100%)
❌ Failed: 0/4 tests
Duration: 6m 32s

Critical issues: None
Warnings: None

Ready to proceed to Section 3? (yes/no):
```

---

## Final Checklist for AI Agents

Before ending the workflow session:

- [ ] All sections executed or explicitly skipped
- [ ] Results recorded for each test
- [ ] Final report generated
- [ ] Critical issues highlighted
- [ ] Recommendations provided
- [ ] Test resources cleaned up (if applicable)
- [ ] Report saved to file (if requested)
- [ ] User questions answered
- [ ] Next steps communicated

---

**End of AI Agent Guide**

Good luck with testing! Remember: Your goal is to provide valuable regression testing insights while maintaining a smooth, professional user experience.
