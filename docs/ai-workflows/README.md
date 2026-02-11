# AI Workflows for Agent365 DevTools CLI

This directory contains AI-powered workflows designed to help developers test, validate, and ensure quality of the Agent365 DevTools CLI.

## Overview

AI workflows are structured markdown documents that guide AI agents (like GitHub Copilot, ChatGPT, or other LLMs) through complex, multi-step processes. These workflows are designed to be:

- **Comprehensive**: Cover all major features and edge cases
- **Interactive**: Require user participation and input
- **Structured**: Follow consistent patterns for execution
- **Actionable**: Produce concrete test results and reports

## Available Workflows

### 1. Integration Test Workflow
**File**: [integration-test-workflow.md](./integration-test-workflow.md)

**Purpose**: Comprehensive integration testing of all CLI commands to ensure no regressions.

**Covers**:
- CLI installation and version checking
- Configuration management (init, display, import)
- Setup commands (infrastructure, blueprint, permissions)
- Development commands (MCP management, Dataverse integration)
- Deployment to Azure (multi-platform support)
- Publishing to MOS (Microsoft Online Services)
- Query operations (Entra ID scopes and permissions)
- Cleanup operations (blueprint, instance, Azure resources)
- Error handling and edge cases
- Cross-platform compatibility

**Duration**: 30-45 minutes  
**Test Sections**: 13  
**Total Tests**: 50+

**When to Use**:
- Before releasing a new version
- After significant code changes
- To verify bug fixes don't introduce regressions
- When onboarding new team members
- For periodic quality assurance

### 2. AI Agent Execution Guide
**File**: [ai-agent-execution-guide.md](./ai-agent-execution-guide.md)

**Purpose**: Instructions for AI agents on how to effectively execute the integration test workflow.

**Covers**:
- Execution strategies and phases
- User interaction patterns
- Error handling and recovery
- Command execution best practices
- Result recording and reporting
- Platform-specific considerations
- Troubleshooting common issues

**When to Use**:
- When configuring an AI agent to run integration tests
- As a reference for proper workflow execution
- To understand expected AI agent behavior
- For debugging workflow execution issues

## How to Use These Workflows

### For Human Developers

1. **Manual Testing**
   - Open the integration test workflow
   - Follow each test section sequentially
   - Execute commands as documented
   - Record results in the provided templates

2. **AI-Assisted Testing**
   - Share the integration test workflow with your AI assistant
   - Ask the AI to guide you through the tests
   - The AI will execute commands and collect results
   - Review and verify AI-generated test reports

### For AI Agents

1. **Load the Workflow**
   ```
   Read the integration-test-workflow.md file
   ```

2. **Load the Execution Guide**
   ```
   Read the ai-agent-execution-guide.md file
   ```

3. **Execute the Workflow**
   ```
   Follow the execution strategy from the guide
   Interact with the user as specified
   Record results in the workflow format
   Generate the final test report
   ```

## Workflow Structure

All workflows in this directory follow a consistent structure:

```markdown
# Workflow Title

## Overview
- Purpose
- Duration
- Prerequisites

## Pre-Test Setup
- Environment preparation
- Prerequisites verification

## Test Sections
- Section 1: [Area]
  - Test 1.1: [Specific test]
  - Test 1.2: [Specific test]
  - Section status and notes
- Section 2: [Area]
  - ...

## Test Summary Report
- Overall results
- Detailed results table
- Critical issues
- Performance metrics
- Environment information
- Recommendations

## Post-Test Cleanup
- Resource cleanup steps
- Environment restoration
```

## Creating New Workflows

To create a new AI workflow for the Agent365 CLI:

### 1. Identify the Purpose
- What process needs automation/testing?
- Who is the target user?
- What is the expected outcome?

### 2. Define Sections
- Break down into logical, sequential sections
- Each section should have a clear objective
- Group related tests together

### 3. Write Tests
For each test:
- Clear test name and objective
- Exact commands to execute
- Expected results
- Recording instructions

### 4. Add Error Handling
- Common failure scenarios
- Recovery options
- User decision points

### 5. Include Reporting
- Result recording format
- Summary generation
- Actionable recommendations

### 6. Document Prerequisites
- Required tools and access
- Environment setup
- Input values needed

### Template Structure

```markdown
# [Workflow Name]

## Overview
Purpose: [What this workflow does]
Duration: [Estimated time]
Prerequisites: [What's needed]

## Pre-Test Setup
[Setup steps]

## Test Sections

### Section 1: [Area]
Objective: [What this section tests]

#### Test 1.1: [Test Name]
[Test description]
[Command to run]
[Expected result]
[Recording instructions]

## Test Summary Report
[Report template]

## Post-Test Cleanup
[Cleanup steps]

## Usage Instructions
[How to use this workflow]
```

## Best Practices for AI Workflows

### 1. Be Explicit
- Provide exact commands, not pseudo-code
- Include expected output examples
- Define success criteria clearly

### 2. Structure for Readability
- Use consistent heading levels
- Include visual separators
- Group related items

### 3. Handle Errors
- Anticipate common failures
- Provide troubleshooting steps
- Offer recovery options

### 4. Enable Tracking
- Include checkboxes for progress
- Provide recording templates
- Support result documentation

### 5. Make It Interactive
- Prompt for user input when needed
- Ask for confirmation on destructive actions
- Offer choices at decision points

### 6. Generate Reports
- Include reporting templates
- Capture detailed results
- Enable regression analysis

## Example Usage

### Scenario: Pre-Release Testing

```powershell
# 1. Human developer initiates
"I need to run integration tests before releasing version 0.6.0"

# 2. AI agent responds
"I'll guide you through the comprehensive integration test workflow.
This will test all CLI commands and ensure no regressions.

First, let me gather prerequisites..."

# 3. AI executes workflow
[Follows integration-test-workflow.md]
[Uses ai-agent-execution-guide.md for execution patterns]

# 4. AI generates report
"Testing complete! Results:
✅ 54/57 tests passed (94.7%)
⚠️ 3 minor issues found
❌ 0 critical failures

Would you like the detailed report?"

# 5. Developer reviews
[Reviews report]
[Addresses issues]
[Approves release]
```

## Workflow Maintenance

### When to Update Workflows

Update workflows when:
- New commands are added to the CLI
- Command behavior changes
- New edge cases are discovered
- Testing processes improve
- User feedback suggests improvements

### Version Control

Track workflow changes:
- Include version history in each workflow
- Document major changes
- Link to related PRs/issues
- Update dates and contributors

### Validation

Periodically validate workflows:
- Run the workflow end-to-end
- Verify all commands still work
- Check expected outputs match reality
- Update for new CLI versions

## Contributing Workflows

To contribute a new workflow:

1. **Create the workflow file**
   - Use the template structure
   - Follow naming convention: `[purpose]-workflow.md`
   - Include comprehensive documentation

2. **Test the workflow**
   - Execute it manually
   - Test with an AI agent
   - Verify results are accurate

3. **Update this README**
   - Add to the "Available Workflows" section
   - Document purpose and usage

4. **Submit a pull request**
   - Include example execution results
   - Explain the workflow's value

## Support and Feedback

### Questions
For questions about these workflows:
- Open an issue on GitHub
- Tag with `documentation` label
- Include workflow name in title

### Improvements
To suggest improvements:
- Describe the enhancement
- Provide examples if applicable
- Explain the benefit

### Bug Reports
If a workflow has errors:
- Specify which workflow
- Describe the issue
- Include error messages
- Share execution context

## Future Workflows (Planned)

- **Performance Testing Workflow**: Benchmark CLI command execution times
- **Security Testing Workflow**: Validate permission handling and credential management
- **Upgrade Testing Workflow**: Test CLI updates and migrations
- **Multi-Platform Testing Workflow**: Cross-platform compatibility testing (Windows/Linux/macOS)
- **Developer Onboarding Workflow**: Guide new contributors through the codebase

## Resources

### Related Documentation
- [CLI README](../../README.md)
- [Developer Guide](../../src/DEVELOPER.md)
- [CLI Command Reference](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli)

### Testing Tools
- [xUnit](https://xunit.net/) - Unit testing framework used by the CLI
- [FluentAssertions](https://fluentassertions.com/) - Assertion library
- [NSubstitute](https://nsubstitute.github.io/) - Mocking framework

### AI Tools
- [GitHub Copilot](https://github.com/features/copilot)
- [ChatGPT](https://chat.openai.com/)
- [Claude](https://claude.ai/)

---

## Quick Start

Want to run integration tests right now? Here's how:

```
1. Ask your AI assistant: 
   "Please run the Agent365 CLI integration test workflow"

2. The AI will:
   - Load the workflow files
   - Gather prerequisites from you
   - Execute all tests
   - Generate a comprehensive report

3. Review the report and address any issues

That's it! The AI handles the execution details.
```

---

**Last Updated**: February 5, 2026  
**Workflows Version**: 1.0  
**CLI Version Compatibility**: 0.5.x and higher

For the latest workflows, always check this directory in the main branch.
