# Agent365 CLI - Multi-Agent Team Support - Implementation Tasks (HACKATHON MVP)

> **Feature**: Multi-Agent Team Support for Agent365 CLI  
> **Design Document**: [design.md](design.md)  
> **Timeline**: 2-Day Hackathon (2 Engineers = 4 Person-Days)  
> **Status**: Not Started  
> **Overall Progress**: 0/13 tasks complete (0%)

---

## 🎯 Hackathon MVP Goal

**Demo Objective**: Load team config → deploy 3 agents with one command → show they exist

### Scope Decisions

**✅ IN SCOPE (Must Have)**
- Parse team JSON config
- `a365 setup all --team` deploys multiple agents
- `a365 deploy --team` deploys binaries to all agents
- `a365 list teams` shows deployed teams
- Reuse existing single-agent infrastructure (loop it)

**❌ OUT OF SCOPE (Future Work)**
- Event Grid communication (mock with console logs)
- Team state persistence (in-memory for demo)
- `publish` command team support (too complex)
- `cleanup` command team support (not needed for demo)
- Full validation (basic only - unique names, required fields)
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

- [ ] 1.1 Create `TeamConfig.cs` model class
  - **Relevant Documentation:**
    - `q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\Models\Agent365Config.cs` - Existing config model pattern
    - `q:\CAMP-AIR\docs\features\agent365-cli-teams\design.md` - Team schema (FR-1)
  - **Time Estimate**: 2 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: Create model with properties: name, displayName, description, managerEmail, orchestrationType, sharedResources (resourceGroup, appServicePlanName, location)

- [ ] 1.2 Create `TeamAgentConfig.cs` model class
  - **Relevant Documentation:**
    - `q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\Models\Agent365Config.cs` - Base agent config
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: Wrapper/extension of existing agent config with team-specific properties (role, capabilities array)

- [ ] 1.3 Add JSON deserialization to ConfigService
  - **Relevant Documentation:**
    - `q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\Services\ConfigService.cs` - Config loading patterns
  - **Time Estimate**: 2 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: Add method to detect and load team config files, deserialize into TeamConfig object

- [ ] 1.4 Implement basic validation
  - **Relevant Documentation:**
    - `q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\Models\Agent365Config.cs` - See Validate() method pattern
  - **Time Estimate**: 2 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: Validate unique agent names, required fields (name, managerEmail), deployment paths exist

- [ ] 1.5 Create example `team.config.json` file
  - **Relevant Documentation:**
    - `q:\Agent365-devTools\src\a365.config.example.json` - Example pattern
    - `q:\CAMP-AIR\docs\features\agent365-cli-teams\design.md` - Team schema example
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: Create sample config with 3 agents (triage, technical, billing) for demo

---

### Phase 2: Setup Command Extension (Engineer 2 - Day 1)

**Status**: Not Started  
**Progress**: 0/4 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD  
**Assigned To**: Engineer 2

- [ ] 2.1 Add `--team <file>` option to SetupCommand
  - **Relevant Documentation:**
    - `q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\Commands\SetupCommand.cs` - Command structure
    - `q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\Commands\SetupSubcommands\AllSubcommand.cs` - Main setup logic
  - **Time Estimate**: 2 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: Add Option<FileInfo> parameter, wire up to setup all subcommand

- [ ] 2.2 Modify setup logic to loop over agents array
  - **Relevant Documentation:**
    - `q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\Commands\SetupSubcommands\AllSubcommand.cs` - Setup orchestration
  - **Time Estimate**: 3 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: If --team provided, load team config, iterate over agents, run existing setup logic per agent with team context

- [ ] 2.3 Apply team's managerEmail to all agents
  - **Relevant Documentation:**
    - `q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\Models\Agent365Config.cs` - ManagerEmail property
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: Override/merge team-level managerEmail into each agent's config during iteration

- [ ] 2.4 Test with 2-3 agent team configuration
  - **Relevant Documentation:**
    - `q:\Agent365-devTools\Readme-Usage.md` - Testing workflow
  - **Time Estimate**: 2 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: Manual integration test - run setup all --team, verify 3 agents created in Azure

---

### Phase 3: List Teams Command (Engineer 1 - Day 2)

**Status**: Not Started  
**Progress**: 0/4 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD  
**Assigned To**: Engineer 1

- [ ] 3.1 Create ListCommand.cs with `teams` subcommand
  - **Relevant Documentation:**
    - `q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\Commands\QueryEntraCommand.cs` - Command pattern reference
    - `q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\Program.cs` - Command registration
  - **Time Estimate**: 3 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: Create new command file, register in Program.cs, implement basic structure

- [ ] 3.2 Query Azure to find deployed team agents
  - **Relevant Documentation:**
    - `q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\Services\GraphApiService.cs` - Azure querying patterns
  - **Time Estimate**: 2 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: Query Azure resources/Graph API to find agents matching team naming pattern

- [ ] 3.3 Format output as table/JSON
  - **Relevant Documentation:**
    - Existing command output patterns in Commands/ directory
  - **Time Estimate**: 2 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: Display team name, agent count, manager email, status in readable format

- [ ] 3.4 Integration testing and bug fixes
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: End-to-end test list command after setup, fix any issues

---

### Phase 4: Deploy Command Extension (Engineer 2 - Day 2)

**Status**: Not Started  
**Progress**: 0/4 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD  
**Assigned To**: Engineer 2

- [ ] 4.1 Add `--team <file>` option to DeployCommand
  - **Relevant Documentation:**
    - `q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\Commands\DeployCommand.cs` - Deploy command structure
  - **Time Estimate**: 2 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: Add Option<FileInfo> parameter to deploy command and subcommands

- [ ] 4.2 Modify deploy logic to loop over agents
  - **Relevant Documentation:**
    - `q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\Services\DeploymentService.cs` - Deployment orchestration
    - `q:\Agent365-devTools\docs\commands\deploy.md` - Deploy workflow
  - **Time Estimate**: 3 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: If --team provided, iterate agents, run existing deploy per agent with correct deploymentProjectPath

- [ ] 4.3 Add console output showing team deployment progress
  - **Time Estimate**: 1 hour
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: Output "Deploying agent 1/3: triage-agent..." for user feedback

- [ ] 4.4 End-to-end demo testing
  - **Time Estimate**: 2 hours
  - **Started**: TBD
  - **Completed**: TBD
  - **Duration**: TBD
  - **Details**: Full workflow test: setup → deploy → list, prepare demo script

---

## 📅 Day-by-Day Timeline

### Day 1 - Foundation

**9:00 AM - 12:00 PM**: Kickoff & Setup
- Both engineers sync on design doc
- Review existing CLI codebase
- Set up dev environments
- **Engineer 1**: Start Phase 1 (Tasks 1.1-1.3)
- **Engineer 2**: Start Phase 2 (Tasks 2.1-2.2)

**12:00 PM - 1:00 PM**: Lunch

**1:00 PM - 5:00 PM**: Parallel Development
- **Engineer 1**: Complete Phase 1 (Tasks 1.4-1.5)
- **Engineer 2**: Complete Phase 2 (Tasks 2.3-2.4)

**5:00 PM - 6:00 PM**: Sync Meeting
- Engineer 1 demos team config models
- Engineer 2 demos `setup --team` working
- Identify blockers for Day 2

### Day 2 - Integration & Demo Prep

**9:00 AM - 12:00 PM**: Final Features
- **Engineer 1**: Phase 3 (Tasks 3.1-3.2)
- **Engineer 2**: Phase 4 (Tasks 4.1-4.2)

**12:00 PM - 1:00 PM**: Lunch

**1:00 PM - 3:00 PM**: Polish & Integration
- **Engineer 1**: Phase 3 (Tasks 3.3-3.4)
- **Engineer 2**: Phase 4 (Tasks 4.3-4.4)

**3:00 PM - 5:00 PM**: Demo Rehearsal
- Both engineers test full demo workflow
- Fix critical bugs
- Polish console output

**5:00 PM - 6:00 PM**: Final Prep
- Create demo slides/talking points
- Final rehearsal

---

## 🎬 Demo Script

**1. Show team configuration** (1 min)
```bash
cat team.config.json
# Shows 3 agents with different roles
```

**2. Deploy the team** (2 min)
```bash
a365 setup all --team team.config.json
# Shows progress: Creating triage-agent... Creating technical-agent... etc.
```

**3. List deployed teams** (1 min)
```bash
a365 list teams
# Shows table with team name, 3 agents, manager email
```

**4. Optional: Deploy binaries** (1 min)
```bash
a365 deploy --team team.config.json
# Shows deployment progress for all 3 agents
```

**Total Demo Time**: 5 minutes

---

## 🚨 Risk Mitigation

**If behind schedule by Day 2 at 12 PM:**
- ❌ Skip `deploy --team` implementation (Phase 4)
- ✅ Mock `list teams` output with hardcoded demo data
- ✅ Focus all effort on making `setup all --team` work

**If ahead of schedule (unlikely):**
- ✅ Add `--agent <name>` filter to deploy single agent
- ✅ Better error messages and validation
- ✅ Add color/formatting to console output

---

## 📊 Success Criteria

**Minimum Viable Demo:**
- [ ] Team config file with 3 agents exists and is valid
- [ ] `a365 setup all --team` creates all 3 agents in Azure
- [ ] `a365 list teams` shows the deployed team
- [ ] Demo runs smoothly in < 5 minutes

**Stretch Goals:**
- [ ] `a365 deploy --team` successfully deploys binaries
- [ ] Console output has progress indicators
- [ ] Error handling for common mistakes

---

**Last Updated**: 2026-01-14 by GitHub Copilot
