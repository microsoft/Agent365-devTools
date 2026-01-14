# Agent365 CLI - Multi-Agent Team Support (Hackathon MVP)

## Introduction/Overview

This feature extends the Microsoft Agent 365 DevTools CLI (`a365`) to support creating and deploying teams of specialized agents that work together to accomplish complex tasks. Currently, the CLI creates and deploys single agents individually. This hackathon MVP enables developers to define a team configuration and deploy multiple agents with a single command.

**Problem**: Creating multiple coordinated agents requires running the CLI multiple times with different configurations, which is time-consuming and error-prone. Developers need a streamlined way to deploy cohesive teams of agents.

**Goal**: Enable developers to define a team of agents with different specializations in a JSON configuration file and deploy the entire team with a single CLI command.

**Timeline**: 2-Day Hackathon (January 15-16, 2026)

---

## Goals

1. **Single Command Team Deployment**: Deploy multiple specialized agents as a team using `a365 setup all --team team.config.json`
2. **Declarative Team Configuration**: Define team structure and agents in a JSON configuration file
3. **Reuse Existing Infrastructure**: Loop existing single-agent logic for each team member
4. **Team Visibility**: List deployed teams and their member agents
5. **Hackathon Demo Ready**: Working demo in under 5 minutes

---

## User Stories

### US-1: Team Configuration
**As a** developer building a complex agent solution  
**I want to** define a team of specialized agents in a JSON configuration file  
**So that** I can manage all agent definitions in one place

**Acceptance Criteria**:
- I can create a JSON file defining a team with multiple agents
- Each agent has its own identity, display name, and deployment path
- The team configuration includes metadata like team name and manager email
- Basic validation catches common errors (duplicate names, missing fields)

### US-2: Team Deployment
**As a** developer  
**I want to** run `a365 setup all --team team.config.json` to deploy an entire team  
**So that** all team agents are provisioned with a single command

**Acceptance Criteria**:
- Single command deploys all agents in the team configuration
- Each agent gets its own Azure resources (web app, MSI, agent identity)
- Manager email is assigned to all agents in the team
- Console output shows progress for each agent

### US-3: Team Listing
**As a** developer  
**I want to** view all deployed teams and their member agents  
**So that** I can verify the team was deployed successfully

**Acceptance Criteria**:
- `a365 list teams` shows all deployed teams
- Output displays team name, agent count, and manager email
- Can query Azure to find agents matching team patterns

---

## Functional Requirements

### FR-1: Team Configuration File Schema (MVP)
The CLI **must** support a simplified team configuration JSON file:

```json
{
  "team": {
    "name": "customer-support-team",
    "displayName": "Customer Support Agent Team",
    "description": "Multi-agent team for handling customer support inquiries",
    "managerEmail": "manager@company.onmicrosoft.com",
    "sharedResources": {
      "tenantId": "00000000-0000-0000-0000-000000000000",
      "clientAppId": "your-client-app-id",
      "subscriptionId": "00000000-0000-0000-0000-000000000000",
      "resourceGroup": "rg-agent365-team-dev",
      "appServicePlanName": "asp-agent365-team-dev",
      "location": "westus"
    }
  },
  "agents": [
    {
      "name": "triage-agent",
      "displayName": "Triage Specialist",
      "agentIdentityDisplayName": "Customer Support Triage Agent",
      "agentUserPrincipalName": "triage.agent@company.onmicrosoft.com",
      "agentUserDisplayName": "Triage Agent",
      "deploymentProjectPath": "./agents/triage"
    },
    {
      "name": "technical-agent",
      "displayName": "Technical Support Specialist",
      "agentIdentityDisplayName": "Technical Support Agent",
      "agentUserPrincipalName": "technical.agent@company.onmicrosoft.com",
      "agentUserDisplayName": "Technical Agent",
      "deploymentProjectPath": "./agents/technical"
    },
    {
      "name": "billing-agent",
      "displayName": "Billing Support Specialist",
      "agentIdentityDisplayName": "Billing Support Agent",
      "agentUserPrincipalName": "billing.agent@company.onmicrosoft.com",
      "agentUserDisplayName": "Billing Agent",
      "deploymentProjectPath": "./agents/billing"
    }
  ]
}
```

### FR-2: Team-Aware Setup Command
The `a365 setup all` command **must** accept a `--team` parameter:

```bash
a365 setup all --team team.config.json
```

This command **must**:
- Parse and validate the team configuration file
- Iterate over each agent in the `agents` array
- For each agent, merge team's `sharedResources` with agent-specific properties
- Apply team's `managerEmail` to all agents
- Run existing setup logic for each agent
- Display progress: "Setting up agent 1/3: triage-agent..."

### FR-3: Team-Aware Deploy Command (Stretch Goal)
The `a365 deploy` command **should** support team deployments:

```bash
a365 deploy --team team.config.json
```

This command **should** (if time permits):
- Build and deploy all agents in the team
- Display progress for each agent deployment
- Support existing flags (`--restart`, `--inspect`)

### FR-4: Team Listing Command
The CLI **must** provide a new command to list teams:

```bash
a365 list teams
```

This command **must**:
- Query Azure resources to find deployed agents
- Group agents by team (detected from resource naming pattern or tags)
- Display: team name, agent count, manager email
- Show status of each agent (running/stopped)

### FR-5: Configuration Model Extension
The CLI **must** create new model classes:

**New Models**:
- `TeamConfig.cs` - Represents team configuration
  - Properties: name, displayName, description, managerEmail, sharedResources
- `TeamAgentConfig.cs` - Represents individual agent in team
  - Inherits/wraps existing agent config properties
- `TeamSharedResources.cs` - Shared Azure resource settings
  - Properties: tenantId, subscriptionId, resourceGroup, location, appServicePlanName

**Extend Existing**:
- `ConfigService.cs` - Add methods to load/parse team configs

### FR-6: Basic Validation
The CLI **must** validate team configurations:
- Ensure unique agent names within a team
- Validate all required fields are present
- Check that deployment paths exist
- Provide clear error messages

---

## Non-Goals (Out of Scope for Hackathon)

1. **Event Grid Communication**: No inter-agent communication infrastructure
2. **Team State Persistence**: No persistent state tracking (query Azure directly)
3. **Publish Command Team Support**: Too complex for 2-day timeline
4. **Cleanup Command Team Support**: Not needed for demo
5. **Advanced Validation**: Only basic validation (unique names, required fields)
6. **Error Recovery**: Fail fast on errors, happy path only
7. **Agent Dependency Management**: No `dependsOn` logic
8. **Team Updates**: No incremental updates, full redeploy only

---

## Design Considerations

### Resource Naming Convention
Each agent in a team gets resources named with team + agent name:
- Web App: `{team-name}-{agent-name}-webapp`
- Agent Identity: `{team-name}-{agent-name}-identity`

### Configuration Merge Strategy
When deploying a team agent:
1. Start with team's `sharedResources` (tenant, subscription, RG, etc.)
2. Merge with agent-specific properties (name, UPN, deployment path)
3. Apply team's `managerEmail` to agent config
4. Pass merged config to existing setup logic

### Team Detection for Listing
Teams are identified by:
- Resource naming pattern: `{team-name}-{agent-name}-*`
- Resource tags (if time permits): `team: {team-name}`

---

## Technical Considerations

### Existing Architecture Integration

**Models to Create** (Engineer 1 - Day 1):
- `src/Microsoft.Agents.A365.DevTools.Cli/Models/TeamConfig.cs`
- `src/Microsoft.Agents.A365.DevTools.Cli/Models/TeamAgentConfig.cs`
- `src/Microsoft.Agents.A365.DevTools.Cli/Models/TeamSharedResources.cs`

**Services to Extend** (Engineer 1 - Day 1):
- `src/Microsoft.Agents.A365.DevTools.Cli/Services/ConfigService.cs`
  - Add `LoadTeamConfigAsync(string filePath)` method
  - Add `ValidateTeamConfig(TeamConfig config)` method

**Commands to Extend** (Engineer 2 - Day 1-2):
- `src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupCommand.cs`
  - Add `--team` Option<FileInfo> parameter
- `src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupSubcommands/AllSubcommand.cs`
  - Detect if `--team` parameter provided
  - If yes, load team config and iterate agents
  - For each agent, merge with shared resources and call existing setup
- `src/Microsoft.Agents.A365.DevTools.Cli/Commands/DeployCommand.cs` (stretch)
  - Add `--team` Option<FileInfo> parameter
  - Similar iteration logic

**Commands to Create** (Engineer 1 - Day 2):
- `src/Microsoft.Agents.A365.DevTools.Cli/Commands/ListCommand.cs`
  - New command with `teams` subcommand
  - Query Azure resources using existing services
  - Format output as table

### Dependencies
- **No new NuGet packages required**
- Reuse existing:
  - System.Text.Json for config parsing
  - Azure SDK packages (already referenced)
  - Microsoft Graph SDK (already referenced)

### Example Team Configuration File
Create `team.config.example.json` in `src/` directory for reference.

---

## Success Metrics

**Hackathon Demo Success**:
1. **Team Config Validated**: `team.config.json` with 3 agents loads without errors
2. **Team Deployed**: `a365 setup all --team` creates 3 agents in Azure in < 10 minutes
3. **Team Listed**: `a365 list teams` shows the deployed team with 3 agents
4. **Demo Time**: Complete demo runs in < 5 minutes

**Stretch Goals**:
- `a365 deploy --team` successfully deploys binaries to all 3 agents
- Console output has color/formatting
- Error messages are clear and actionable

---

## Open Questions

1. **Resource Tagging**: Should we tag Azure resources with team name for easier querying?
   - **Recommendation**: Yes if time permits, otherwise use naming pattern

2. **WebApp Naming**: Current naming `{team-name}-{agent-name}-webapp` - is this acceptable?
   - **Recommendation**: Yes, ensures uniqueness across teams

3. **Manager Permissions**: What actual permissions does manager email need?
   - **Recommendation**: Same as single agent - it's just metadata for now

4. **Team Config Location**: Where should users store `team.config.json`?
   - **Recommendation**: Same directory as `a365.config.json`, flexible path via parameter

---

## Implementation Timeline

### Day 1 (January 15, 2026)

**Morning (9am-12pm):**
- Both engineers: Repo setup, review existing code
- Engineer 1: Tasks 1.1-1.3 (Models, ConfigService)
- Engineer 2: Tasks 2.1-2.2 (Setup command extension)

**Afternoon (1pm-6pm):**
- Engineer 1: Tasks 1.4-1.5 (Validation, example config)
- Engineer 2: Tasks 2.3-2.4 (Manager email, testing)

### Day 2 (January 16, 2026)

**Morning (9am-12pm):**
- Engineer 1: Tasks 3.1-3.2 (List command, Azure querying)
- Engineer 2: Tasks 4.1-4.2 (Deploy command extension)

**Afternoon (1pm-6pm):**
- Engineer 1: Tasks 3.3-3.4 (Format output, testing)
- Engineer 2: Tasks 4.3-4.4 (Progress output, demo prep)
- Both: Integration testing, demo rehearsal

---

## Demo Script

**Duration**: 5 minutes

**1. Show Team Configuration** (1 min)
```bash
cat team.config.json
# Shows 3 agents: triage, technical, billing
```

**2. Deploy the Team** (2 min)
```bash
a365 setup all --team team.config.json
# Console shows: "Setting up agent 1/3: triage-agent..."
# "Setting up agent 2/3: technical-agent..."
# "Setting up agent 3/3: billing-agent..."
# "✓ Team deployed successfully!"
```

**3. Verify Deployment** (1 min)
```bash
a365 list teams
# Shows table:
# Team Name                | Agents | Manager Email           | Status
# customer-support-team    | 3      | manager@company.com    | Running
```

**4. Optional: Deploy Binaries** (1 min)
```bash
a365 deploy --team team.config.json
# Shows deployment progress for all agents
```

---

**Last Updated**: 2026-01-14 by GitHub Copilot
