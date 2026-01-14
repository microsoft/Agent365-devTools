# Team Configuration Setup - Agent Projects

**Date**: 2025-01-14  
**Feature**: Multi-Agent Team Deployment  
**Task**: Create agent project copies with customized instructions

---

## Summary

Successfully created three agent projects from the Agent365-Samples semantic-kernel sample, customized the agent instructions to match team roles, and updated the team configuration file with correct deployment paths.

---

## Actions Performed

### 1. Cloned Agent365-Samples Repository
```powershell
cd Q:\
git clone https://github.com/microsoft/Agent365-Samples
```

**Location**: `Q:\Agent365-Samples\dotnet\semantic-kernel`

### 2. Created Agent Project Copies

Created three copies of the semantic-kernel sample in the CLI directory:

```
q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\agents\
├── Researcher\
│   └── sample-agent\
├── CurrencyConverter\
│   └── sample-agent\
└── Billing\
    └── sample-agent\
```

**Commands Used**:
```powershell
cd q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli
mkdir agents
cd agents
Copy-Item -Recurse Q:\Agent365-Samples\dotnet\semantic-kernel\* -Destination Researcher -Exclude .vs,bin,obj
Copy-Item -Recurse Q:\Agent365-Samples\dotnet\semantic-kernel\* -Destination CurrencyConverter -Exclude .vs,bin,obj
Copy-Item -Recurse Q:\Agent365-Samples\dotnet\semantic-kernel\* -Destination Billing -Exclude .vs,bin,obj
```

### 3. Customized Agent Instructions

Modified `Agents/Agent365Agent.cs` in each project to match the agent's role:

#### Researcher Agent
**File**: `agents/Researcher/sample-agent/Agents/Agent365Agent.cs`

**Instructions Changed To**:
```csharp
private string AgentInstructions() => $@"
    You are a Research Agent that researches customer questions and prepares a detailed plan to address them.
```

#### CurrencyConverter Agent
**File**: `agents/CurrencyConverter/sample-agent/Agents/Agent365Agent.cs`

**Instructions Changed To**:
```csharp
private string AgentInstructions() => $@"
    You are a Currency Conversion Agent specialized in handling currency conversions and exchange rate queries.
```

#### Billing Agent
**File**: `agents/Billing/sample-agent/Agents/Agent365Agent.cs`

**Instructions Changed To**:
```csharp
private string AgentInstructions() => $@"
    You are a Billing Agent that manages billing inquiries, subscription changes, and payment issues.
```

### 4. Updated Team Configuration

Modified `team.config.example.json` to point to the new agent project paths:

**File**: `src/Microsoft.Agents.A365.DevTools.Cli/team.config.example.json`

**Changes**:
```json
{
  "agents": [
    {
      "name": "Researcher",
      "deploymentProjectPath": "./agents/Researcher/sample-agent",
      // ... other properties
    },
    {
      "name": "CurrencyConverter",
      "deploymentProjectPath": "./agents/CurrencyConverter/sample-agent",
      // ... other properties
    },
    {
      "name": "Billing",
      "deploymentProjectPath": "./agents/Billing/sample-agent",
      // ... other properties
    }
  ]
}
```

---

## Team Configuration Structure

### Team: Trip Planner
**Shared Resources**:
- Tenant ID: adfa4542-3e1e-46f5-9c70-3df0b15b3f6c
- Subscription: e09e22f2-9193-4f54-a335-01f59575eefd
- Resource Group: a365demorg
- Location: westus
- App Service Plan: a365agent-app-plan2 (B1 SKU)

### Agents

#### 1. Researcher Agent
- **Name**: Researcher
- **Display Name**: Research Agent
- **Description**: Researches the customer questions and prepares a plan
- **UPN**: UPN.ResearchAgent@a365preview001.onmicrosoft.com
- **Deployment Path**: `./agents/Researcher/sample-agent`
- **Instructions**: Research and plan preparation specialist

#### 2. CurrencyConverter Agent
- **Name**: CurrencyConverter
- **Display Name**: Currency Conversion Agent
- **Description**: Handles currency conversion
- **UPN**: UPN.CurrencyConverterAgent@a365preview001.onmicrosoft.com
- **Deployment Path**: `./agents/CurrencyConverter/sample-agent`
- **Instructions**: Currency conversion and exchange rate specialist

#### 3. Billing Agent
- **Name**: Billing
- **Display Name**: Billing Agent
- **Description**: Manages billing inquiries, subscription changes, and payment issues
- **UPN**: UPN.BillingAgent@a365preview001.onmicrosoft.com
- **Deployment Path**: `./agents/Billing/sample-agent`
- **Instructions**: Billing and payment support specialist

---

## File Structure

```
Agent365-devTools/
└── src/
    └── Microsoft.Agents.A365.DevTools.Cli/
        ├── team.config.example.json  ✅ Updated
        └── agents/  ✅ Created
            ├── Researcher/
            │   └── sample-agent/
            │       ├── Agents/
            │       │   ├── Agent365Agent.cs  ✅ Modified
            │       │   └── MyAgent.cs
            │       ├── Plugins/
            │       ├── Program.cs
            │       └── *.csproj
            ├── CurrencyConverter/
            │   └── sample-agent/
            │       ├── Agents/
            │       │   ├── Agent365Agent.cs  ✅ Modified
            │       │   └── MyAgent.cs
            │       ├── Plugins/
            │       ├── Program.cs
            │       └── *.csproj
            └── Billing/
                └── sample-agent/
                    ├── Agents/
                    │   ├── Agent365Agent.cs  ✅ Modified
                    │   └── MyAgent.cs
                    ├── Plugins/
                    ├── Program.cs
                    └── *.csproj
```

---

## Testing the Configuration

### Validate Team Configuration
```powershell
cd q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli
dotnet run -- config validate --team team.config.example.json
```

### Dry-Run Setup
```powershell
dotnet run -- setup all --team team.config.example.json --dry-run
```

### Dry-Run Deploy
```powershell
dotnet run -- deploy --team team.config.example.json --dry-run
```

---

## Important Notes

### Building Agent Projects

Each agent project needs to be built **independently** before deployment:

```powershell
# Build Researcher
cd q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\agents\Researcher\sample-agent
dotnet build

# Build CurrencyConverter
cd q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\agents\CurrencyConverter\sample-agent
dotnet build

# Build Billing
cd q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\agents\Billing\sample-agent
dotnet build
```

### Deployment Requirements

Before deploying, ensure:
1. Each agent project builds successfully
2. Azure CLI is authenticated (`az login`)
3. Azure resources (Web Apps) exist for each agent
4. Team config has valid Azure subscription and tenant IDs
5. Agent User Principal Names match your Azure AD setup

### Paths in Team Config

The `deploymentProjectPath` in team.config.json uses **relative paths** from the CLI directory:
- `./agents/Researcher/sample-agent`
- `./agents/CurrencyConverter/sample-agent`
- `./agents/Billing/sample-agent`

When the CLI runs, it resolves these relative to:
```
q:\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\
```

---

## Next Steps

1. **Build Each Agent**: Ensure all three agent projects compile successfully
2. **Test Dry-Run**: Verify the team configuration loads correctly
3. **Update Azure Details**: Modify team.config.example.json with your Azure details
4. **Deploy to Azure**: Run actual deployment with `setup all --team` and `deploy --team`

---

## Customization Instructions Summary

For each agent, the following file was modified:

**File**: `Agents/Agent365Agent.cs`  
**Method**: `AgentInstructions()`  
**Line**: ~31-33

**Original**:
```csharp
You are a friendly assistant that helps office workers with their daily tasks.
```

**Modified To Match Agent Role**:
- **Researcher**: Research specialist for customer questions
- **CurrencyConverter**: Currency conversion specialist
- **Billing**: Billing and payment support specialist

---

**Status**: ✅ Complete - Agent projects created and customized  
**Ready For**: Testing and deployment to Azure

