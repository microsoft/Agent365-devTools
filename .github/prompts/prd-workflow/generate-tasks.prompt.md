---
tags: [prompt, tasks, implementation, planning]
description: Generate implementation task lists from design documents
---

# Rule: Generating a Task List from a Design Document

> **Usage**: Point to a design.md file, get parent tasks with documentation refs, confirm with "Go", then get sub-tasks.

## Prerequisites

**FIRST**: Check `.github/copilot/project-config.md` for feature toggles:

- **Azure DevOps Integration**: If ❌ OFF, skip all ADO Feature links and SOA prefixes
- **Memory Bank**: If ❌ OFF, skip memory bank updates
- **GitHub Projects**: If ❌ OFF, skip GitHub issue creation

## Goal

To guide an AI assistant in creating a detailed, step-by-step task list in Markdown format based on an existing design document. The task list should guide a developer through implementation.

**IMPORTANT**: All generated tasks MUST follow the rules defined in `.github/copilot/task-workflow.md`, including:

- File location conventions (`/docs/features/[feature-name]/tasks.md` and `design.md`)
- Timestamp tracking format (Phase Started, Last Updated, Phase Completed, Phase Duration)
- Task status markers (`[ ]` not started, `[>]` in progress, `[x]` completed)
- Documentation update requirements (both tasks.md and design.md must be updated)
- **If ADO = ✅ ON**: Azure DevOps Feature-level tracking (phases tracked in ADO, individual tasks in tasks.md)

**DOCUMENTATION HIERARCHY**: Documentation exists at multiple levels:

- **Project-Wide** (`/docs/`) - System architecture, features, cross-component concerns
- **Technical Component** (`/backend/docs/`, `/frontend/docs/`) - Component-specific architecture and implementation
- **Module/Package** (`/[any-path]/docs/`) - Module-specific documentation co-located with code

All levels use the same subdirectory pattern: `architecture/`, `implementation/`, `setup/`, `testing/`. See `.github/copilot/documentation-organization.md` for complete rules.

## Output

- **Format:** Markdown (`.md`)
- **Location:** `/docs/features/[feature-name]/`
- **Filename:** `tasks.md`

## Process

**PREREQUISITE**: Check `.github/copilot/project-config.md` first. If ADO = ✅ ON, also read `.github/copilot/azure-devops-integration.md` to understand:

- Phase header format requirements (Status, Progress, timestamps)
- ADO Feature work item integration (SOA prefix, work item links)
- Task status markers and progression rules
- Documentation synchronization requirements

1. **Receive Design Document Reference:** The user points the AI to a specific design document file (e.g., `@docs/features/[feature-name]/design.md`)
2. **Analyze Design Document:** The AI reads and analyzes the functional requirements, user stories, and other sections of the specified design document.
3. **Phase 1: Generate Parent Tasks:** Based on the design document analysis, create the file and generate the main, high-level tasks required to implement the feature. Use your judgement on how many high-level tasks to use. Present these tasks to the user in the specified format (without sub-tasks yet). Inform the user: "I have generated the high-level tasks based on the design document. Ready to generate the sub-tasks? Respond with 'Go' to proceed."
4. **Identify Relevant Documentation:** For each parent task, identify and list documentation files that will be needed to complete that task. Documentation exists at multiple levels (project-wide, technical component, module/package). Include:
    
    **Project-Wide Documentation (`/docs/`)**:

    - High-level architecture (e.g., `/docs/architecture/SYSTEM-ARCHITECTURE.md`)
    - Feature-specific docs (e.g., `/docs/features/[feature-name]/design.md`)
    - Project-wide testing (e.g., `/docs/testing/TESTING-GUIDE.md`)
    - Project-wide setup (e.g., `/docs/setup/QUICKSTART.md`)
    
    **Technical Component Documentation (`/[component]/docs/`)**:

    - Backend architecture (e.g., `/backend/docs/architecture/ARCHITECTURE.md`, `/backend/docs/architecture/DATA-MODEL.md`)
    - Frontend architecture (e.g., `/frontend/docs/architecture/ARCHITECTURE.md`)
    - Component implementation guides (e.g., `/backend/docs/implementation/API-ENDPOINTS.md`)
    - Component testing (e.g., `/backend/docs/testing/TESTING.md`, `/frontend/docs/testing/TESTING.md`)
    - Component setup (e.g., `/backend/docs/setup/ENV-VARIABLES.md`)
    
    **Module/Package Documentation (when relevant)**:

    - Service-level docs (e.g., `/backend/src/services/docs/implementation/authentication.md`)
    - Component-level docs (e.g., `/frontend/src/components/calendar/docs/README.md`)
    - Any module-specific documentation that provides implementation context
    
    **Existing Code References**:

    - Reference implementations (e.g., `/backend/src/models/User.js`, `/frontend/src/components/TaskList.jsx`)
    - Existing patterns to follow
    
    List these under a `**Relevant Documentation:**` section for each parent task, ordered from highest level (project-wide) to most specific (module-level).
5. **Wait for Confirmation:** Pause and wait for the user to respond with "Go".
6. **Phase 2: Generate Sub-Tasks:** Once the user confirms, break down each parent task into smaller, actionable sub-tasks necessary to complete the parent task. Ensure sub-tasks logically follow from the parent task and cover the implementation details implied by the design document.
7. **Generate Final Output:** Combine the parent tasks, sub-tasks, and relevant documentation into the final Markdown structure.
8. **Save Task List:** Save the generated document in the same `/docs/features/[feature-name]/` directory as the design document with the filename `tasks.md`.

## Output Format

The generated task list format depends on project-config.md settings:

### If Azure DevOps = ✅ ON

```markdown
## Tasks

### Phase 1: [Phase Name]

**ADO Feature**: [SOA Phase 1: Phase Name - #WORKITEM_ID](https://dynamicscrm.visualstudio.com/OneCRM/_workitems/edit/WORKITEM_ID)  
**Status**: Not Started  
**Progress**: 0/X tasks complete (0%)  
**Phase Started**: TBD  
**Last Updated**: TBD  
**Phase Completed**: TBD  
**Phase Duration**: TBD

- [ ] 1.0 Parent Task Title
  - **Relevant Documentation:**
    - `/docs/architecture/SYSTEM-ARCHITECTURE.md` - Overall system design and component interaction
    - `/backend/docs/architecture/ARCHITECTURE.md` - Backend MVC implementation details
    - `/docs/features/[feature-name]/design.md` - Feature-specific requirements and design
  - [ ] 1.1 [Sub-task description 1.1]
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 1.2 [Sub-task description 1.2]
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD

---

### Phase 2: [Phase Name]

**ADO Feature**: [SOA Phase 2: Phase Name - #WORKITEM_ID](https://dynamicscrm.visualstudio.com/OneCRM/_workitems/edit/WORKITEM_ID)  
**Status**: Not Started  
**Progress**: 0/X tasks complete (0%)  
**Phase Started**: TBD  
**Last Updated**: TBD  
**Phase Completed**: TBD  
**Phase Duration**: TBD
  
- [ ] 2.0 Parent Task Title
  - **Relevant Documentation:**
    - `/frontend/docs/architecture/ARCHITECTURE.md` - Frontend component architecture
    - `/docs/architecture/SYSTEM-ARCHITECTURE.md` - API design conventions
  - [ ] 2.1 [Sub-task description 2.1]
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  
---

### Phase 3: [Phase Name]

**ADO Feature**: [SOA Phase 3: Phase Name - #WORKITEM_ID](https://dynamicscrm.visualstudio.com/OneCRM/_workitems/edit/WORKITEM_ID)  
**Status**: Not Started  
**Progress**: 0/X tasks complete (0%)  
**Phase Started**: TBD  
**Last Updated**: TBD  
**Phase Completed**: TBD  
**Phase Duration**: TBD

- [ ] 3.0 Parent Task Title (may not require sub-tasks if purely structural or configuration)
  - **Relevant Documentation:**
    - `/backend/docs/architecture/DATA-MODEL.md` - Database schema and relationships
  - [ ] 3.1 [Sub-task description if needed]
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
```

### If Azure DevOps = ❌ OFF (Simplified)

```markdown
## Tasks

### Phase 1: [Phase Name]

**Status**: Not Started  
**Progress**: 0/X tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD

- [ ] 1.0 Parent Task Title
  - **Relevant Documentation:**
    - `/docs/architecture/SYSTEM-ARCHITECTURE.md` - Overall system design and component interaction
    - `/backend/docs/architecture/ARCHITECTURE.md` - Backend MVC implementation details
    - `/docs/features/[feature-name]/design.md` - Feature-specific requirements and design
  - [ ] 1.1 [Sub-task description 1.1]
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 1.2 [Sub-task description 1.2]
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD

---

### Phase 2: [Phase Name]

**Status**: Not Started  
**Progress**: 0/X tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD
  
- [ ] 2.0 Parent Task Title
  - **Relevant Documentation:**
    - `/frontend/docs/architecture/ARCHITECTURE.md` - Frontend component architecture
    - `/docs/architecture/SYSTEM-ARCHITECTURE.md` - API design conventions
  - [ ] 2.1 [Sub-task description 2.1]
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
```

**Key Requirements** (per task-workflow.md and project-config.md):

- Phase metadata: Status, Progress, Phase Started, Phase Completed
- Task status markers: `[ ]` (not started), `[>]` (in progress), `[x]` (completed)
- Timestamp format: `YYYY-MM-DD HH:MM:SS UTC±X`
- Each sub-task includes Started, Completed, Duration fields (TBD initially)
- Relevant Documentation section under each parent task
- Completion summaries go in `docs/tasks/TASK-X.Y.Z-TASK-NAME-COMPLETION-SUMMARY.md`
- **If ADO = ✅ ON**: Include ADO Feature link with SOA prefix, Last Updated, Phase Duration

## Interaction Model

The process explicitly requires a pause after generating parent tasks (with relevant documentation) to get user confirmation ("Go") before proceeding to generate the detailed sub-tasks. This ensures the high-level plan and documentation references align with user expectations before diving into details.

## Target Audience

Assume the primary reader of the task list is a **junior developer** who will implement the feature. Documentation references help them quickly find the context they need.

## Next Step

Once the task list is complete, use `process-task-list.prompt.md` to execute tasks one sub-task at a time.

---

**Last Updated:** December 3, 2025 by gregrata