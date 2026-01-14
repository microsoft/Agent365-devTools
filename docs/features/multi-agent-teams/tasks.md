# Agent365 CLI - Multi-Agent Team Support - Implementation Tasks (HACKATHON MVP)

> **Feature**: Multi-Agent Team Support for Agent365 CLI  
> **Design Document**: [design.md](design.md)  
> **Timeline**: 2-Day Hackathon (January 15-16, 2026)  
> **Status**: Not Started  
> **Overall Progress**: 0/13 tasks complete (0%)

---

## 🎯 Hackathon MVP Goal

**Demo Objective**: Load team config → deploy 3 agents with one command → show they exist

### Scope Decisions

**✅ IN SCOPE (Must Have)**
- Parse team JSON config
- `a365 setup all --team` deploys multiple agents
- `a365 deploy --team` deploys binaries to all agents (stretch)
- `a365 list teams` shows deployed teams
- Reuse existing single-agent infrastructure (loop it)

**❌ OUT OF SCOPE (Future Work)**
- Event Grid communication
- Team state persistence (query Azure directly for demo)
- `publish` command team support
- `cleanup` command team support
- Advanced validation
- Error recovery (fail fast, happy path only)

---

## 👥 Engineer Assignment

### Engineer 1: Config & Models Track
**Days 1-2**: Configuration models → List command

### Engineer 2: Commands & Deployment Track
**Days 1-2**: Setup command extension → Deploy command extension

---

## Tasks

### Phase 1: Configuration Models (Engineer 1 - Day 1)

**Status**: Not Started  
**Progress**: 0/5 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD  
**Assigned To**: Engineer 1

- [ ] 1.1 Create TeamConfig.cs model class
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Models/Agent365Config.cs` - Existing config model pattern
    - `docs/features/multi-agent-teams/design.md` - Team schema (FR-1, FR-5)
  - **Time Estimate**: 2 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: 
    - Create `src/Microsoft.Agents.A365.DevTools.Cli/Models/TeamConfig.cs`
    - Properties: Name, DisplayName, Description, ManagerEmail
    - Include TeamSharedResources property
    - Include List<TeamAgentConfig> Agents property
    - Add JsonPropertyName attributes for serialization
    - Add basic Validate() method stub

- [ ] 1.2 Create TeamSharedResources.cs model class
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Models/Agent365Config.cs` - Azure config properties pattern
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Create `src/Microsoft.Agents.A365.DevTools.Cli/Models/TeamSharedResources.cs`
    - Properties: TenantId, ClientAppId, SubscriptionId, ResourceGroup, AppServicePlanName, Location
    - Add JsonPropertyName attributes
    - Properties use `init` setter for immutability

- [ ] 1.3 Create TeamAgentConfig.cs model class
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Models/Agent365Config.cs` - Agent config properties
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Create `src/Microsoft.Agents.A365.DevTools.Cli/Models/TeamAgentConfig.cs`
    - Properties: Name, DisplayName, AgentIdentityDisplayName, AgentUserPrincipalName, AgentUserDisplayName, DeploymentProjectPath
    - Add JsonPropertyName attributes
    - Add method to merge with TeamSharedResources into Agent365Config

- [ ] 1.4 Implement validation logic
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Models/Agent365Config.cs` - See Validate() method pattern
    - `docs/features/multi-agent-teams/design.md` - Validation requirements (FR-6)
  - **Time Estimate**: 2 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Implement TeamConfig.Validate() method
    - Check: Team name required and alphanumeric
    - Check: Manager email required and valid format
    - Check: Agent names are unique within team
    - Check: All required agent fields present
    - Check: Deployment paths exist
    - Return List<string> of error messages

- [ ] 1.5 Create example team.config.json file
  - **Relevant Documentation:**
    - `src/a365.config.example.json` - Example pattern
    - `docs/features/multi-agent-teams/design.md` - Team schema example
  - **Time Estimate**: 30 minutes
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Create `src/team.config.example.json`
    - Include 3 sample agents (triage, technical, billing)
    - Include all required team and shared resource fields
    - Add comments explaining each section
    - Use realistic example values

---

### Phase 2: Team Configuration Service Layer (Engineer 1 - Day 1)

**Status**: Not Started  
**Progress**: 0/4 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD  
**Assigned To**: Engineer 1

- [ ] 2.1 Add LoadTeamConfigAsync method to ConfigService
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Services/ConfigService.cs` - Existing load methods
    - `src/Microsoft.Agents.A365.DevTools.Cli/Services/IConfigService.cs` - Interface to extend
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Add method signature to IConfigService interface
    - Implement LoadTeamConfigAsync(string filePath) in ConfigService
    - Use JsonSerializer.Deserialize<TeamConfig>
    - Handle file not found, invalid JSON errors
    - Return TeamConfig object or null on error

- [ ] 2.2 Add ValidateTeamConfig method
  - **Relevant Documentation:**
    - `s1 Add --team parameter to SetupCommand
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupCommand.cs` - Command structure
    - `src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupSubcommands/AllSubcommand.cs` - Where to add parameter
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Add Option<FileInfo> for --team parameter in AllSubcommand.CreateCommand
    - Parameter description: "Path to team configuration JSON file"
    - Wire parameter to handler method
    - Make it optional (null if not provided)

- [ ] 3.2 Implement team detection and loading logic
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Services/ConfigService.cs` - Use LoadTeamConfigAsync
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - In AllSubcommand handler, check if --team parameter provided
    - If yes, call configService.LoadTeamConfigAsync(teamFilePath)
    - Validate team config using configService.ValidateTeamConfig
    - If validation fails, display errors and exit with error code
    - Log: "Deploying team: {team.DisplayName} ({agents.Count} agents)"

- [ ] 3.3 Implement agent iteration and deployment loop
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupSubcommands/AllSubcommand.cs` - Existing setup logic
    - `docs/features/multi-agent-teams/design.md` - Config merge strategy
  - **Time Estimate**: 2 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Loop through teamConfig.Agents (for i = 0; i < agents.Count; i++)
    - For each agent, call configService.MergeTeamAgentConfig(team, agent)
    - Get merged Agent365Config
    - Call existing setup logic with merged config
    - Catch exceptions per agent, log error, continue to next agent
    - Track success/failure count

- [ ] 3.4 Add progress output and summary
  - **Relevant Documentation:**
    - Existing command output patterns in Commands/ directory
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Before each agent: Console.WriteLine($"Setting up agent {i+1}/{total}: {agent.Name}...")
    - After each agent: Log success or failure with checkmark/X
    - After all agents: Display summary
    - Summary: "✓ Successfully deployed {successCount}/{totalCount} agents"
    - If failures: List failed agents with error messages
    - Exit code: 0 if all success, 1 if any failures
    - Add additional cross-field validation
    - Check deployment paths exist on filesystem
    - Log validation errors with clear messages
    - Return bool (true if valid)

- [ ] 2.3 Implement MergeTeamAgentConfig method
  - **Relevant Documentation:**
    - `docs/features/multi-agent-teams/design.md` - Config merge strategy
  - **Time Estimate**: 1.5 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Du1 Add --team parameter to DeployCommand
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Commands/DeployCommand.cs` - Command structure
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Add Option<FileInfo> for --team parameter
    - Similar pattern to SetupCommand
    - Make it work with app and mcp subcommands
    - Wire to handler methods

- [ ] 4.2 Implement team detection in deploy logic
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Services/DeploymentService.cs` - Deployment service
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - In deploy handler, check if --team provided
    - Load and validate team config (reuse Phase 2 methods)
    - Log: "Deploying team: {team.DisplayName}"

- [ ] 4.3 Implement agent deployment iteration
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Services/DeploymentService.cs` - Existing deploy logic
    - `docs/commands/deploy.md` - Deploy workflow
  - **Time Estimate**: 2 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Loop through team.Agents
    - For each agent, merge config (reuse MergeTeamAgentConfig)
    - Call deploymentService.DeployAsync with merged config
    - Ensure correct deploymentProjectPath is used per agent
    - Support --restart and --inspect flags per agent
    - Handle errors per agent, continue iteration

- [ ] 4.4 Add deployment progress output
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Commands/DeployCommand.cs` - Output patterns
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Log: "Deploying agent {i+1}/{total}: {agent.Name}..."
    - Show build/package/upload progress per agent
    - After each: "✓ {agent.Name} deployed successfully" or "✗ Failed"
    - Final summary: "{successCount}/{totalCount} agents deployed"
    - Exit code based on success/failure
    - Generate resource names: {team-name}-{agent-name}-*

- [ ] 2.4 Add unit tests for config service
  - **Relevant Documentation:**
    - `src/Tests/` - Existing test patterns
  - **Time Estimate**: 1.5 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Test LoadTeamConfigAsync with valid JSON
    - Test validation with invalid configs (missing fields, duplicates)
    - Test MergeTeamAgentConfig produces correct Agent365Config
    - Test error handling (file not found, invalid JSON)
    - Mock file system if needed
1 Create ListCommand.cs and register in Program.cs
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Commands/QueryEntraCommand.cs` - Command pattern
    - `src/Microsoft.Agents.A365.DevTools.Cli/Program.cs` - Command registration
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Create `src/Microsoft.Agents.A365.DevTools.Cli/Commands/ListCommand.cs`
    - Implement CreateCommand() method returning Command
    - Add 'teams' subcommand
    - Register in Program.cs: rootCommand.AddCommand(ListCommand.CreateCommand(...))
    - Wire up required services (ConfigService, GraphApiService, etc.)

- [ ] 5.2 Implement Azure resource querying for teams
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Services/GraphApiService.cs` - Graph API usage
    - `src/Microsoft.Agents.A365.DevTools.Cli/Helpers/AzureCliHelper.cs` - Azure CLI integration
  - **Time Estimate**: 2 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Query Azure Web Apps in subscription
    - Filter by naming pattern: contains hyphen (team-name-agent-name pattern)
    - Extract team name from resource name (first part before second hyphen)
    - Group web apps by team name
    - For each web app, query agent status (running/stopped)
    - Query agent identity via Graph API to get manager email
    - Build list of TeamInfo objects (TeamName, AgentCount, Agents[], ManagerEmail)

- [ ] 5.3 Format output as readable table
  - **Relevant Documentation:**
    - Existing command output formatting in Commands/ directory
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Create table with columns: Team Name | Agents | Manager Email | Status
    - Team Name: team.DisplayName or team.Name
    - Agents: count (e.g., "3")
    - Manager Email: from agent identity
    - Status: "Running" if all agents running, "Partial" if mixed, "Stopped" if all stopped
    - Use Console.WriteLine or formatting library for alignment
    - Add header row with separator line

- [ ] 5.4 Add error handling and edge cases
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**:
    - Handle no teams found: "No teams deployed"
    - Handle Azure auth errors: "Please run 'az login'"
    - Handle Graph API errors gracefully
    - If can't determine manager email, show "N/A"
    - Add --verbose flag for detailed agent information
    - Test with 0 teams, 1 team, multiple teams
**Phase Started**: TBD  
**Phase Completed**: TBD  
**Assigned To**: Engineer 2

- [ ] 3.0 Add team support to setup command
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupCommand.cs` - Command structure to extend
    - `src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupSubcommands/AllSubcommand.cs` - Main setup orchestration
    - `docs/commands/config-init.md` - Config initialization patterns
    - `docs/features/multi-agent-teams/design.md` - Setup requirements (FR-2)
  - **Description**: Add --team parameter to setup command, detect when team config is provided, iterate over agents array, merge team settings with each agent's config, apply manager email, and run existing setup logic for each agent with progress output.

---

### Phase 4: Deploy Command Extension (Engineer 2 - Day 2 - Stretch Goal)

**Status**: Not Started  
**Progress**: 0/4 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD  
**Assigned To**: Engineer 2

- [ ] 4.0 Add team support to deploy command
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Commands/DeployCommand.cs` - Deploy command structure
    - `src/Microsoft.Agents.A365.DevTools.Cli/Services/DeploymentService.cs` - Deployment orchestration
    - `docs/commands/deploy.md` - Deployment workflow documentation
    - `docs/features/multi-agent-teams/design.md` - Deploy requirements (FR-3)
  - **Description**: Add --team parameter to deploy command, iterate over agents in team config, run existing deploy logic per agent with correct deployment path, show progress output, support existing flags (--restart, --inspect).

---

### Phase 5: Team Listing Command (Engineer 1 - Day 2)

**Status**: Not Started  
**Progress**: 0/4 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD  
**Assigned To**: Engineer 1

- [ ] 5.0 Create list command for team visibility
  - **Relevant Documentation:**
    - `src/Microsoft.Agents.A365.DevTools.Cli/Commands/QueryEntraCommand.cs` - Query command pattern reference
    - `src/Microsoft.Agents.A365.DevTools.Cli/Services/GraphApiService.cs` - Azure querying service
    - `src/Microsoft.Agents.A365.DevTools.Cli/Program.cs` - Command registration
    - `docs/features/multi-agent-teams/design.md` - Listing requirements (FR-4, US-3)
  - **Description**: Create new ListCommand with 'teams' subcommand, query Azure resources to find deployed agents, group by team (detected from naming pattern or tags), format output as table showing team name/agent count/manager/status, register command in Program.cs.

---

## 📅 Day-by-Day Timeline

### Day 1 - Foundation (January 15, 2026)

**9:00 AM - 12:00 PM**: Kickoff & Core Development
- Both engineers sync on design doc and existing CLI codebase
- Set up dev environments and review relevant code
- **Engineer 1**: Start Phase 1 & 2 (Models, ConfigService extension)
- **Engineer 2**: Start Phase 3 (Setup command --team parameter)

**12:00 PM - 1:00 PM**: Lunch

**1:00 PM - 5:00 PM**: Parallel Development
- **Engineer 1**: Complete Phase 1 & 2 (All model and service work)
- **Engineer 2**: Complete Phase 3 (Setup command team support)

**5:00 PM - 6:00 PM**: Sync Meeting
- Engineer 1 demos team config models and validation
- Engineer 2 demos `a365 setup all --team` working with progress output
- Identify any blockers for Day 2

### Day 2 - Integration & Demo (January 16, 2026)

**9:00 AM - 12:00 PM**: Final Features
- **Engineer 1**: Phase 5 (List command creation and Azure querying)
- **Engineer 2**: Phase 4 (Deploy command team support - stretch goal)

**12:00 PM - 1:00 PM**: Lunch

**1:00 PM - 3:00 PM**: Polish & Integration
- **Engineer 1**: Complete Phase 5 (Format output, testing)
- **Engineer 2**: Complete Phase 4 or help with integration testing
- Both: Integration testing of full workflow

**3:00 PM - 5:00 PM**: Demo Rehearsal
- Test complete demo workflow end-to-end
- Fix critical bugs
- Polish console output and error messages

**5:00 PM - 6:00 PM**: Final Prep
- Create demo slides/talking points
- Final rehearsal
- Document any known issues/limitations

---

## 🎬 Demo Script

**Duration**: 5 minutes

**1. Show team configuration** (1 min)
```bash
cat team.config.json
# Shows 3 agents with different roles: triage, technical, billing
```

**2. Deploy the team** (2 min)
```bash
a365 setup all --team team.config.json
# Console output:
# "Setting up agent 1/3: triage-agent..."
# "Setting up agent 2/3: technical-agent..."
# "Setting up agent 3/3: billing-agent..."
# "✓ Team deployed successfully!"
```

**3. List deployed teams** (1 min)
```bash
a365 list teams
# Shows table:
# Team Name              | Agents | Manager Email          | Status
# customer-support-team  | 3      | manager@company.com   | Running
```

**4. Optional: Deploy binaries** (1 min - if Phase 4 completed)
```bash
a365 deploy --team team.config.json
# Shows deployment progress for all 3 agents
```

**Total Demo Time**: 5 minutes

---

## 🚨 Risk Mitigation

**If behind schedule by Day 2 at 12 PM:**
- ❌ Skip Phase 4 (deploy --team) entirely
- ✅ Mock Phase 5 output with hardcoded demo data
- ✅ Focus all effort on making Phase 3 (setup --team) work perfectly

**If ahead of schedule (unlikely):**
- ✅ Add `--agent <name>` filter to deploy single agent in team
- ✅ Better error messages and validation feedback
- ✅ Add color/formatting to console output
- ✅ Resource tagging for easier team detection

---

## 📊 Success Criteria

**Minimum Viable Demo:**
- [ ] Team config file with 3 agents exists and validates successfully
- [ ] `a365 setup all --team` creates all 3 agents in Azure
- [ ] `a365 list teams` shows the deployed team with accurate information
- [ ] Demo runs smoothly in < 5 minutes
- [ ] Console output is clear and informative

**Stretch Goals:**
- [ ] `a365 deploy --team` successfully deploys binaries to all agents
- [ ] Console output has progress indicators and colors
- [ ] Error handling for common mistakes (missing fields, invalid paths)
- [ ] Resource tagging for team identification

---

**I have generated the high-level tasks based on the design document. Ready to generate the detailed sub-tasks? Respond with 'Go' to proceed.**

---

**Last Updated**: 2026-01-14 by GitHub Copilot
