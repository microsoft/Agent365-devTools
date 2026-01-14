# Agent365 CLI - Multi-Agent Team Support

## Introduction/Overview

This feature extends the Microsoft Agent 365 DevTools CLI (`a365`) to support creating and managing teams of specialized agents that work together to accomplish complex tasks. Currently, the CLI creates and deploys single agents individually. This enhancement enables orchestration of multiple agents with different roles and specializations that can communicate with each other to achieve a common goal.

**Problem**: Complex tasks often require multiple specialized capabilities. Creating individual agents and manually coordinating them is inefficient and error-prone. Developers need a streamlined way to define, deploy, and manage cohesive teams of agents.

**Goal**: Enable developers to define a team of agents with different specializations in a single configuration file and deploy the entire team with a single CLI command, with all necessary infrastructure for inter-agent communication and orchestration.

---

## Goals

1. **Single Command Team Creation**: Deploy multiple specialized agents as a cohesive team using one CLI command
2. **Declarative Team Configuration**: Define team structure, agent roles, and specializations in a JSON configuration file
3. **Orchestration Infrastructure**: Automatically provision necessary infrastructure for inter-agent communication
4. **Backward Compatibility**: Maintain full compatibility with existing single-agent workflows
5. **Manager Assignment**: Support human manager assignment for team oversight and approval workflows
6. **Team-Aware Commands**: Extend all existing CLI commands (`setup`, `deploy`, `publish`, `cleanup`) to handle both single agents and teams

---

## User Stories

### US-1: Team Creation
**As a** developer building a complex agent solution  
**I want to** define a team of specialized agents in a configuration file  
**So that** I can deploy multiple coordinated agents with a single command instead of managing them individually

**Acceptance Criteria**:

- I can create a JSON file defining a team with multiple agents, each having distinct roles
- Each agent in the team has its own identity, display name, and capabilities
- The team configuration includes metadata like team name, description, and manager
- The CLI validates the team configuration before deployment

### US-2: Team Deployment
**As a** developer  
**I want to** run `a365 setup all --team team.config.json` to deploy an entire team  
**So that** all team agents are provisioned with proper infrastructure and permissions

**Acceptance Criteria**:

- Single command deploys all agents in the team configuration
- Each agent gets its own Azure resources (web app, MSI, etc.)
- Inter-agent communication infrastructure is automatically provisioned
- All agents in the team share a common team context/identity
- Manager email is assigned to all agents in the team

### US-3: Team Listing and Status
**As a** developer  
**I want to** view all teams and their member agents  
**So that** I can monitor and manage deployed teams

**Acceptance Criteria**:

- `a365 list teams` shows all deployed teams
- `a365 list agents --team <team-name>` shows all agents in a specific team
- Status indicates health of each agent (running, stopped, failed)
- Team metadata displays (name, description, manager, creation date)

### US-4: Team Updates
**As a** developer  
**I want to** add or remove agents from an existing team  
**So that** I can evolve the team composition without recreating everything

**Acceptance Criteria**:

- Update team configuration file with new/removed agents
- `a365 deploy --team team.config.json` detects changes
- Only modified/new agents are deployed
- Removed agents are optionally cleaned up with confirmation

### US-5: Team Cleanup
**As a** developer  
**I want to** delete an entire team with one command  
**So that** I can remove all team resources without manual cleanup

**Acceptance Criteria**:

- `a365 cleanup --team <team-name>` removes all team agents
- Confirmation prompt lists all resources to be deleted
- Optional `--force` flag skips confirmation
- Team metadata is removed from tracking

---

## Functional Requirements

### FR-1: Team Configuration File Schema
The CLI **must** support a team configuration JSON file with the following structure:

```json
{
  "team": {
    "name": "customer-support-team",
    "displayName": "Customer Support Agent Team",
    "description": "Multi-agent team for handling customer support inquiries",
    "managerEmail": "manager@company.onmicrosoft.com",
    "orchestrationType": "sequential",
    "sharedResources": {
      "resourceGroup": "rg-agent365-team-dev",
      "appServicePlanName": "asp-agent365-team-dev",
      "location": "westus"
    }
  },
  "agents": [
    {
      "name": "triage-agent",
      "displayName": "Triage Specialist",
      "description": "Routes inquiries to appropriate specialists",
      "role": "triage",
      "agentIdentityDisplayName": "Customer Support Triage Agent",
      "agentUserPrincipalName": "triage.agent@company.onmicrosoft.com",
      "agentUserDisplayName": "Triage Agent",
      "deploymentProjectPath": "./agents/triage",
      "capabilities": ["email-routing", "priority-assessment"]
    },
    {
      "name": "technical-agent",
      "displayName": "Technical Support Specialist",
      "description": "Handles technical product questions",
      "role": "specialist",
      "agentIdentityDisplayName": "Technical Support Agent",
      "agentUserPrincipalName": "technical.agent@company.onmicrosoft.com",
      "agentUserDisplayName": "Technical Agent",
      "deploymentProjectPath": "./agents/technical",
      "capabilities": ["product-knowledge", "troubleshooting"]
    },
    {
      "name": "billing-agent",
      "displayName": "Billing Support Specialist",
      "description": "Handles billing and account inquiries",
      "role": "specialist",
      "agentIdentityDisplayName": "Billing Support Agent",
      "agentUserPrincipalName": "billing.agent@company.onmicrosoft.com",
      "agentUserDisplayName": "Billing Agent",
      "deploymentProjectPath": "./agents/billing",
      "capabilities": ["billing-systems", "account-management"]
    }
  ],
  "communication": {
    "type": "event-grid",
    "eventGridTopicName": "customer-support-team-events"
  }
}
```

### FR-2: Team-Aware Setup Command
The `a365 setup` command **must** accept a `--team` parameter:

```bash
a365 setup all --team team.config.json
```

This command **must**:

- Parse and validate the team configuration file
- Create shared Azure resources (Resource Group, App Service Plan) once for the team
- Create individual resources for each agent (Web App, MSI, Agent Identity)
- Provision inter-agent communication infrastructure (Event Grid/Service Bus)
- Register all agent blueprints
- Configure permissions for each agent
- Apply manager email to all agents
- Store team metadata for future operations

### FR-3: Team-Aware Deploy Command
The `a365 deploy` command **must** support team deployments:

```bash
a365 deploy --team team.config.json
a365 deploy --team team.config.json --agent triage-agent  # Deploy single agent in team
```

This command **must**:

- Build and deploy all agents in the team (or specific agent if `--agent` specified)
- Update MCP permissions for all team agents
- Configure inter-agent communication endpoints
- Validate all agents are running successfully
- Support `--restart`, `--inspect`, and other existing flags

### FR-4: Team-Aware Publish Command
The `a365 publish` command **must** support team publishing:

```bash
a365 publish --team team.config.json
```

This command **must**:

- Publish manifest for each agent to MOS
- Configure team-level app roles and permissions
- Set up federated identity credentials for all team agents
- Enable team agents to be hired together or individually in Teams

### FR-5: Team Listing and Query
The CLI **must** provide new commands for team management:

```bash
a365 list teams                           # List all deployed teams
a365 list agents --team <team-name>       # List agents in a specific team
a365 query-entra team-scopes --team <team-name>  # Query team-level permissions
```

### FR-6: Team Cleanup
The `a365 cleanup` command **must** support team deletion:

```bash
a365 cleanup --team <team-name>           # Delete entire team
a365 cleanup --team <team-name> --agent <agent-name>  # Remove specific agent from team
```

This command **must**:

- Display all resources to be deleted
- Require confirmation unless `--force` is specified
- Delete all agent identities, blueprints, and Azure resources
- Remove team communication infrastructure
- Clean up team metadata

### FR-7: Configuration Model Extension
The CLI **must** extend `Agent365Config.cs` to support team configurations:

- New `TeamConfig` model class
- New `TeamAgentConfig` model class (extends/wraps existing agent config)
- Team-level shared resource properties
- Team metadata (name, description, orchestration type)
- Communication infrastructure settings

### FR-8: Team State Tracking
The CLI **must** maintain team state in configuration files:

- Store team metadata in `a365.team.<team-name>.config.json`
- Track deployed agents per team
- Store communication infrastructure details
- Enable incremental updates to team composition

### FR-9: Single Agent Backward Compatibility
All existing single-agent workflows **must** continue to work unchanged:

```bash
a365 config init
a365 setup all
a365 publish
a365 deploy
```

No breaking changes to existing configuration schema or command signatures.

### FR-10: Validation and Error Handling
The CLI **must** validate team configurations:

- Ensure unique agent names within a team
- Validate that all required agent properties are present
- Check for conflicting resource names across agents
- Verify deployment paths exist for all agents
- Prevent circular dependencies in agent communication patterns
- Provide clear error messages for configuration issues

---

## Non-Goals (Out of Scope)

1. **Dynamic Agent Scaling**: Auto-scaling agents based on load is not included in this version
2. **Cross-Team Communication**: Communication between agents in different teams
3. **Visual Team Designer**: GUI/web interface for designing teams (CLI-only in this version)
4. **Agent Versioning**: Managing multiple versions of the same agent within a team
5. **Advanced Orchestration Patterns**: Complex workflows like parallel execution, branching, or conditional routing (only sequential orchestration in initial version)
6. **Team Templates**: Pre-built team templates for common scenarios
7. **Sub-Teams**: Hierarchical team structures or nested teams
8. **Real-time Collaboration**: Live agent-to-agent chat or real-time collaboration features
9. **Monitoring Dashboard**: Dedicated UI for monitoring team health and performance

---

## Design Considerations

### Team Configuration File Location
Team configuration files should be stored alongside agent project files, typically in the repository root or a `teams/` directory. The CLI will reference these files by relative or absolute path.

### Resource Naming Convention
Each agent in a team gets resources with names derived from both team and agent names:

- Web App: `webapp-{team-name}-{agent-name}`
- Agent Identity: `{team-name}-{agent-name}-identity`
- Resource Group: Shared across team (optional, can use per-agent RGs)

### Inter-Agent Communication
Initial version supports Event Grid for asynchronous communication:

- Each team gets a dedicated Event Grid topic
- Agents publish events to the topic
- Agents subscribe to relevant event types
- CLI configures necessary permissions and connection strings

Future versions may support Service Bus queues or direct HTTP calls.

### Team Metadata Storage
Team metadata will be stored in generated config files:

- `a365.team.<team-name>.config.json` - Team-level metadata and state
- `a365.generated.config.json` - Enhanced to track team membership

---

## Technical Considerations

### Existing Architecture Integration

**Models to Create/Extend**:

- `TeamConfig.cs` - Main team configuration model
- `TeamAgentConfig.cs` - Individual agent within a team
- `CommunicationConfig.cs` - Communication infrastructure settings
- Extend `Agent365Config.cs` with optional `TeamContext` property

**Services to Create/Extend**:

- `TeamConfigService.cs` - Load, validate, and save team configurations
- `TeamDeploymentService.cs` - Orchestrate team-wide deployments
- Extend `ConfigService.cs` to handle team config files
- Extend `DeploymentService.cs` to support team deployments
- `TeamCommunicationService.cs` - Provision Event Grid/Service Bus resources

**Commands to Create/Extend**:

- New `ListCommand.cs` with `teams` and `agents` subcommands
- Extend `SetupCommand.cs` with `--team` parameter
- Extend `DeployCommand.cs` with `--team` and `--agent` parameters
- Extend `PublishCommand.cs` with `--team` parameter
- Extend `CleanupCommand.cs` with `--team` and `--agent` parameters
- Extend `ConfigCommand.cs` to support team config initialization

**Dependencies**:

- Azure Event Grid SDK for communication infrastructure
- Existing Microsoft Graph SDK for agent identity management
- Existing Azure Resource Manager SDK for resource provisioning
- No new external dependencies required

### Azure Resources Per Team

**Shared Resources**:

- Resource Group (optional, configurable)
- App Service Plan (recommended for cost optimization)
- Event Grid Topic (for communication)
- Storage Account (for shared state, optional)

**Per-Agent Resources**:

- Azure Web App
- Managed Service Identity
- Agent Identity (Entra ID)
- Agent Blueprint

### Configuration File Priority
When team configuration conflicts with global or local config:

1. Team configuration file (highest priority)
2. Local `a365.config.json`
3. Global configuration
4. CLI defaults (lowest priority)

---

## Success Metrics

1. **Deployment Time Reduction**: Teams of 3-5 agents deploy in < 10 minutes (vs. 30-50 minutes deploying individually)
2. **Configuration Simplification**: Team configuration reduces lines of config by 60% vs. individual agent configs
3. **Error Rate**: < 5% deployment failures for valid team configurations
4. **Developer Satisfaction**: 90%+ of developers prefer team workflow over individual agent deployment
5. **Adoption**: 30%+ of new projects use team configurations within 3 months of release

---

## Open Questions

1. **Communication Pattern Flexibility**: Should initial version support both Event Grid and Service Bus, or just Event Grid?
   - **Recommendation**: Start with Event Grid only, add Service Bus in v2 based on feedback

2. **Resource Group Strategy**: Should teams require a shared resource group, or allow per-agent resource groups?
   - **Recommendation**: Default to shared RG for teams, allow override per agent if needed

3. **Team Blueprint**: Should there be a separate "Team Blueprint" in Entra ID that represents the collective team?
   - **Recommendation**: Not in v1; treat team as metadata linking individual blueprints

4. **Agent Dependency Management**: How to specify dependencies between agents (e.g., "triage must deploy before specialists")?
   - **Recommendation**: Add optional `dependsOn` array in agent config for v1

5. **Cost Optimization**: Should CLI support serverless deployment options (Functions, Container Instances) for cost-sensitive teams?
   - **Recommendation**: Future enhancement; v1 uses App Service only

6. **Team Updates**: When updating a team, should CLI automatically redeploy dependent agents?
   - **Recommendation**: Yes, with clear output showing which agents are affected

7. **Manager Permissions**: What permissions should the manager email have over the team?
   - **Recommendation**: Read-only monitoring in v1; management actions require explicit config changes

8. **Multi-Region Teams**: Should teams support agents deployed across multiple Azure regions?
   - **Recommendation**: Out of scope for v1; all team agents in same region

---

**Last Updated**: 2026-01-14 by GitHub Copilot
