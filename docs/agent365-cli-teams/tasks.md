# Agent365 CLI - Multi-Agent Team Support - Implementation Tasks

> **Feature**: Multi-Agent Team Support for Agent365 CLI  
> **Design Document**: [design.md](design.md)  
> **Status**: Not Started  
> **Timeline**: Estimated 2 weeks  
> **Engineers**: 2 (parallel development)

---

## Task Assignment Overview

| Engineer | Focus Area | Phases |
|----------|------------|--------|
| **Engineer 1** | Infrastructure & Models | Phase 1, Phase 2, Phase 5 |
| **Engineer 2** | Commands & Integration | Phase 3, Phase 4 |

**Parallel Development Strategy**: Engineer 1 builds the foundational models and services while Engineer 2 integrates team support into existing commands. Both streams merge at Phase 4/5 integration points.

---

## Engineer 1: Infrastructure & Models

### Phase 1: Configuration Models and Schema

**Status**: Not Started  
**Progress**: 0/5 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD

- [ ] 1.0 Create team configuration models and JSON schema support
  - **Relevant Documentation:**
    - [src/Microsoft.Agents.A365.DevTools.Cli/Models/Agent365Config.cs](../../src/Microsoft.Agents.A365.DevTools.Cli/Models/Agent365Config.cs) - Existing config model to extend
    - [src/a365.config.example.json](../../src/a365.config.example.json) - Example config structure
    - [design.md](design.md) - FR-1, FR-7 schema requirements
  - [ ] 1.1 Create `TeamConfig.cs` model class with team-level properties (name, displayName, description, managerEmail, orchestrationType, sharedResources)
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 1.2 Create `TeamAgentConfig.cs` model class for individual agents within a team (name, displayName, role, capabilities, deploymentProjectPath)
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 1.3 Create `CommunicationConfig.cs` model for inter-agent communication settings (type, eventGridTopicName)
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 1.4 Create `SharedResourcesConfig.cs` model for team-level Azure resources (resourceGroup, appServicePlanName, location)
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 1.5 Create example `team.config.json` file with sample 3-agent team configuration
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD

---

### Phase 2: Team Configuration Service Layer

**Status**: Not Started  
**Progress**: 0/5 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD

- [ ] 2.0 Implement team configuration service and validation
  - **Relevant Documentation:**
    - [src/Microsoft.Agents.A365.DevTools.Cli/Services/ConfigService.cs](../../src/Microsoft.Agents.A365.DevTools.Cli/Services/ConfigService.cs) - Existing config service
    - [src/Microsoft.Agents.A365.DevTools.Cli/Helpers/](../../src/Microsoft.Agents.A365.DevTools.Cli/Helpers/) - Helper utilities
    - [design.md](design.md) - FR-10 validation requirements
  - [ ] 2.1 Create `TeamConfigService.cs` with `LoadTeamConfig(string filePath)` method to deserialize team JSON
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 2.2 Implement team configuration validation (unique agent names, required fields, valid paths)
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 2.3 Add resource naming conflict detection (check for duplicate webapp names, MSI names)
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 2.4 Extend `ConfigService.cs` to detect and handle team config files vs single-agent configs
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 2.5 Create unit tests for TeamConfigService validation logic
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD

---

### Phase 5: Team Management Commands and State Tracking

**Status**: Not Started  
**Progress**: 0/5 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD

- [ ] 5.0 Create new team listing and query commands with state tracking
  - **Relevant Documentation:**
    - [src/Microsoft.Agents.A365.DevTools.Cli/Commands/QueryEntraCommand.cs](../../src/Microsoft.Agents.A365.DevTools.Cli/Commands/QueryEntraCommand.cs) - Query command pattern
    - [src/Microsoft.Agents.A365.DevTools.Cli/Models/](../../src/Microsoft.Agents.A365.DevTools.Cli/Models/) - State tracking models
    - [design.md](design.md) - FR-5, FR-8, US-3
  - [ ] 5.1 Create `ListCommand.cs` with `teams` subcommand to list all deployed teams
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 5.2 Add `agents` subcommand with `--team <team-name>` filter to list agents in a team
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 5.3 Implement team state tracking in `a365.team.<team-name>.config.json` files
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 5.4 Format list output (table format for console, optional JSON with `--output json`)
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 5.5 Create unit tests for ListCommand and state tracking
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD

---

## Engineer 2: Commands & Integration

### Phase 3: Team-Aware Setup Command

**Status**: Not Started  
**Progress**: 0/6 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD

- [ ] 3.0 Extend SetupCommand to support team deployments
  - **Relevant Documentation:**
    - [src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupCommand.cs](../../src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupCommand.cs) - Existing setup command
    - [src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupSubcommands/AllSubcommand.cs](../../src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupSubcommands/AllSubcommand.cs) - Setup all subcommand
    - [design.md](design.md) - FR-2, US-2
  - [ ] 3.1 Add `--team <file>` option to SetupCommand and AllSubcommand
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 3.2 Modify setup logic to loop over agents array when `--team` is provided
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 3.3 Create shared Azure resources (Resource Group, App Service Plan) once for the team
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 3.4 Apply team's `managerEmail` to all agents during setup
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 3.5 Add console output showing team setup progress (agent X of Y)
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 3.6 Create integration tests for team setup workflow
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD

---

### Phase 4: Team-Aware Deploy, Publish, and Cleanup Commands

**Status**: Not Started  
**Progress**: 0/8 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD

- [ ] 4.0 Extend Deploy, Publish, and Cleanup commands for team operations
  - **Relevant Documentation:**
    - [src/Microsoft.Agents.A365.DevTools.Cli/Commands/DeployCommand.cs](../../src/Microsoft.Agents.A365.DevTools.Cli/Commands/DeployCommand.cs) - Deploy command
    - [src/Microsoft.Agents.A365.DevTools.Cli/Commands/PublishCommand.cs](../../src/Microsoft.Agents.A365.DevTools.Cli/Commands/PublishCommand.cs) - Publish command
    - [src/Microsoft.Agents.A365.DevTools.Cli/Commands/CleanupCommand.cs](../../src/Microsoft.Agents.A365.DevTools.Cli/Commands/CleanupCommand.cs) - Cleanup command
    - [design.md](design.md) - FR-3, FR-4, FR-6, US-4, US-5
  - [ ] 4.1 Add `--team <file>` option to DeployCommand
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 4.2 Modify deploy logic to loop over agents and build/deploy each
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 4.3 Add `--agent <name>` filter to deploy single agent within a team
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 4.4 Add console output showing deploy progress (agent X of Y, build status)
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 4.5 Add `--team <file>` option to PublishCommand to publish all team agents
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 4.6 Add `--team <team-name>` option to CleanupCommand
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 4.7 Implement team cleanup with confirmation prompt (list all resources to delete)
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 4.8 Create integration tests for team deploy/publish/cleanup workflows
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD

---

## Integration Points

| Dependency | From | To | Description |
|------------|------|-----|-------------|
| Models ready | Phase 1 | Phase 3 | Engineer 2 needs TeamConfig models to implement --team parameter |
| Service ready | Phase 2 | Phase 3, 4 | Commands depend on TeamConfigService for loading/validation |
| State tracking | Phase 5 | Phase 4 | Cleanup needs to read team state to identify resources |

**Sync Points:**

- After Phase 1 completion: Engineer 2 can start using models
- After Phase 2 completion: Full integration begins
- Final integration: Phase 4 + Phase 5 testing together

---

## Summary

| Phase | Engineer | Tasks | Focus |
|-------|----------|-------|-------|
| Phase 1 | Engineer 1 | 5 | Configuration models (TeamConfig, TeamAgentConfig, etc.) |
| Phase 2 | Engineer 1 | 5 | TeamConfigService, validation, conflict detection |
| Phase 3 | Engineer 2 | 6 | SetupCommand --team integration |
| Phase 4 | Engineer 2 | 8 | Deploy/Publish/Cleanup --team integration |
| Phase 5 | Engineer 1 | 5 | ListCommand, state tracking |

**Total**: 29 sub-tasks across 5 phases

---

**Last Updated**: 2026-01-14 by GitHub Copilot
