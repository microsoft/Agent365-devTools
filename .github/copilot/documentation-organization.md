# Documentation Organization Rules

## Detailed Rules

### CRITICAL: Documentation File Placement

**All documentation files MUST be placed in the appropriate subdirectory under `docs/`.**

#### Directory Structure

When creating or suggesting documentation files, **ALWAYS** place them in the correct subdirectory:

##### 1. `docs/setup/` - Getting Started & Setup Guides

- **Use for:** Installation guides, quickstart tutorials, environment setup, authentication setup
- **Examples:** QUICKSTART.md, SETUP.md, initial configuration guides
- **Pattern:** Files explaining "how to get started" or "how to configure"

##### 2. `docs/architecture/` - Design Decisions & Architecture

- **Use for:** Architectural decisions, design pivots, schema comparisons, system design docs
- **Examples:** ARCHITECTURAL-PIVOT-*.md, SCHEMA-COMPARISON.md, design analyses
- **Pattern:** Files explaining "why we designed it this way" or "system architecture"

##### 3. `docs/implementation/` - Implementation Plans & Guides

- **Use for:** Implementation plans, integration guides, migration guides
- **Examples:** *-IMPLEMENTATION-PLAN.md, *-INTEGRATION.md, migration references
- **Pattern:** Files explaining "how to implement" or "implementation approach"

##### 4. `docs/tasks/` - Task Completion Summaries

- **Use for:** Task completion documentation, task setup guides, task-related updates
- **Examples:** TASK-*.md, TASK-*-COMPLETION.md, TASK-*-SETUP.md
- **Pattern:** Files starting with "TASK-" or documenting specific task outcomes

##### 5. `docs/troubleshooting/` - Debugging & Problem Resolution

- **Use for:** Bug fixes, debugging notes, troubleshooting guides, error resolution
- **Examples:** *-FIX.md, *-DEBUG.md, TROUBLESHOOTING-*.md, BUILD-FIXES.md
- **Pattern:** Files explaining "how to fix" or documenting bug resolutions

##### 6. `docs/features/` - Feature Documentation

- **Use for:** Feature announcements, feature analysis, feature completion docs
- **Examples:** *-FEATURE.md, feature analyses, feature integration docs
- **Pattern:** Files documenting specific features or feature additions

##### 7. `docs/research/` - Research & External Analysis

- **Use for:** Industry research, comparison studies, external documentation analysis
- **Examples:** Market research docs, comparison documents, example studies
- **Pattern:** Files analyzing external systems or industry approaches
- **Note:** Can include both .md and .docx files

##### 8. `docs/deployment/` - Deployment & Hosting

- **Use for:** Deployment guides, hosting documentation, production setup, service cleanup
- **Examples:** PRODUCTION-HOSTING.md, *-SETUP-COMPLETE.md, deployment status docs
- **Pattern:** Files explaining "how to deploy" or documenting deployment state

##### 9. `docs/testing/` - Testing Documentation

- **Use for:** Testing guides, testing approaches, test environment docs
- **Examples:** TESTING-GUIDE-*.md, test environment setup, testing strategies
- **Pattern:** Files explaining "how to test" or documenting test approaches

##### 10. `docs/onboarding/` - Team Onboarding

- **Use for:** Onboarding guides, team documentation, project overview documents
- **Examples:** ONBOARDING.md, onboarding session notes, project overview docs
- **Pattern:** Files helping new team members understand the project

##### 11. `docs/misc/` - Miscellaneous Documentation

- **Use for:** Documentation that doesn't fit other categories
- **Examples:** Image prompts, migration summaries, one-off documentation
- **Pattern:** Files that don't match other category patterns

#### Enforcement Rules

1. **NEVER** create documentation files in the root `docs/` directory
2. **ALWAYS** determine the correct subdirectory based on the file's purpose
3. **If unsure**, choose the category that best matches the primary purpose of the document
4. **When moving/renaming docs**, maintain the directory structure
5. **When referencing docs in code/comments**, use the full path including subdirectory

#### Special Cases

- **Mixed purpose docs**: Choose the PRIMARY purpose
  - Example: A doc that's both implementation plan AND troubleshooting → If it's primarily a plan, use `implementation/`
- **Completion docs with fixes**: If it documents task completion, use `tasks/` even if it includes fixes
- **Research with implementation**: If it's primarily research, use `research/`; if primarily implementation, use `implementation/`

**VIOLATION OF THESE RULES**: If you create a doc in the wrong location, immediately inform the user and move it to the correct location.

---

## Project-Specific Documentation in Subdirectories

### IMPORTANT: Hierarchical Documentation at Multiple Levels

**Documentation can exist at ANY level of the directory structure where it provides value.**

The project uses a **hierarchical documentation architecture**:
- **Level 1**: Project-wide (`/docs/`)
- **Level 2**: Technical components (`/backend/docs/`, `/frontend/docs/`, `/database/docs/`)
- **Level 3+**: Modules, packages, services, or any subdirectory (`/backend/src/services/docs/`, `/frontend/src/components/calendar/docs/`)

**Universal Pattern**: All documentation directories at any level should use the same subdirectory structure (architecture/, implementation/, setup/, testing/) when they grow beyond a simple README.

#### Component Documentation Guidelines

##### Backend Documentation (`backend/docs/`)

**Purpose**: Documentation specific to backend implementation, not general architecture

**Directory Structure**: Backend docs should follow the same subdirectory pattern as `/docs/`:

```
backend/docs/
├── architecture/           # Backend-specific architecture
│   ├── ARCHITECTURE.md     # Backend MVC implementation details
│   ├── DATA-MODEL.md       # Database schema and ORM patterns
│   └── API-DESIGN.md       # API endpoint design decisions
├── implementation/         # Backend implementation guides
│   ├── MIGRATIONS.md       # Database migration procedures
│   └── INTEGRATION.md      # Third-party integration guides
├── setup/                  # Backend-specific setup
│   ├── ENV-VARIABLES.md    # Environment configuration
│   └── LOCAL-DEV.md        # Backend development setup
├── testing/                # Backend testing docs
│   └── TESTING.md          # Backend test suite documentation
└── README.md               # Backend overview and quick start
```

**Should Include**:
- Backend-specific architecture decisions
- Database schema and migration patterns
- API endpoint implementation details
- Backend-specific configuration guides
- Backend testing documentation
- Package/dependency documentation
- Environment variable reference
- Local development setup for backend only

**Do NOT Include** in `backend/docs/`:
- System-wide architecture (belongs in `/docs/architecture/`)
- Cross-component features (belongs in `/docs/features/`)
- Project-wide setup guides (belongs in `/docs/setup/`)

##### Frontend Documentation (`frontend/docs/`)

**Purpose**: Documentation specific to frontend implementation, not general architecture

**Directory Structure**: Frontend docs should follow the same subdirectory pattern as `/docs/`:

```
frontend/docs/
├── architecture/           # Frontend-specific architecture
│   ├── ARCHITECTURE.md     # Component architecture and patterns
│   ├── STATE-MANAGEMENT.md # Context/Redux implementation
│   └── ROUTING.md          # Route configuration and navigation
├── implementation/         # Frontend implementation guides
│   ├── COMPONENTS.md       # Component API reference
│   └── STYLING-GUIDE.md    # CSS/theme conventions
├── setup/                  # Frontend-specific setup
│   ├── LOCAL-DEV.md        # Frontend development setup
│   └── BUILD-CONFIG.md     # Vite/Webpack configuration
├── testing/                # Frontend testing docs
│   └── TESTING.md          # Frontend test suite documentation
└── README.md               # Frontend overview and quick start
```

**Should Include**:
- Component API documentation
- Frontend-specific configuration guides
- Styling guides and theme documentation
- Frontend testing documentation
- Package/dependency documentation
- State management implementation details
- Routing configuration
- Local development setup for frontend only

**Do NOT Include** in `frontend/docs/`:
- System-wide architecture (belongs in `/docs/architecture/`)
- Cross-component features (belongs in `/docs/features/`)
- Project-wide setup guides (belongs in `/docs/setup/`)

##### Other Component Documentation Patterns

**Database Documentation** (`database/docs/`):
```
database/docs/
├── architecture/           # Database-specific architecture
│   └── SCHEMA-DESIGN.md    # Schema design decisions
├── implementation/         # Database implementation
│   ├── MIGRATIONS.md       # Migration scripts and history
│   └── SEEDING.md          # Seed data procedures
├── setup/                  # Database setup
│   └── LOCAL-DB.md         # Local database configuration
└── README.md               # Database overview
```

**Scripts Documentation** (`scripts/docs/`):
```
scripts/docs/
├── implementation/         # Script implementation guides
│   ├── BUILD-SCRIPTS.md    # Build automation
│   └── DEPLOY-SCRIPTS.md   # Deployment automation
└── README.md               # Scripts overview
```

**Infrastructure Documentation** (`infrastructure/docs/` or `deploy/docs/`):
```
infrastructure/docs/
├── architecture/           # Infrastructure architecture
│   └── INFRA-DESIGN.md     # IaC design decisions
├── deployment/             # Deployment guides
│   ├── CICD-PIPELINE.md    # CI/CD documentation
│   └── PRODUCTION.md       # Production deployment
└── README.md               # Infrastructure overview
```

#### Documentation Hierarchy Rules

```
Project Root Documentation Strategy:
├── /docs/                          # PROJECT-WIDE documentation
│   ├── architecture/               # System architecture (all components)
│   ├── features/                   # Feature documentation (cross-component)
│   ├── setup/                      # Project setup (entire app)
│   ├── implementation/             # Cross-component implementation
│   ├── testing/                    # System-wide testing
│   ├── deployment/                 # Deployment guides
│   ├── troubleshooting/            # Bug fixes and debugging
│   ├── tasks/                      # Task completion docs
│   ├── research/                   # External research
│   ├── onboarding/                 # Team onboarding
│   └── misc/                       # Miscellaneous
│
├── /backend/docs/                  # BACKEND-SPECIFIC documentation
│   ├── architecture/               # Backend architecture (MVC, API design, data model)
│   ├── implementation/             # Backend implementation guides
│   ├── setup/                      # Backend-specific setup
│   ├── testing/                    # Backend test documentation
│   └── README.md                   # Backend overview
│
├── /frontend/docs/                 # FRONTEND-SPECIFIC documentation
│   ├── architecture/               # Frontend architecture (components, state, routing)
│   ├── implementation/             # Frontend implementation guides
│   ├── setup/                      # Frontend-specific setup
│   ├── testing/                    # Frontend test documentation
│   └── README.md                   # Frontend overview
│
└── /[component]/docs/              # OTHER COMPONENT-SPECIFIC documentation
    ├── architecture/               # Component architecture
    ├── implementation/             # Component implementation
    ├── setup/                      # Component setup
    ├── testing/                    # Component testing
    └── README.md                   # Component overview
```

**Key Principle**: Documentation directories at any level (`/docs/`, `/backend/docs/`, `/backend/src/services/docs/`) should mirror the same subdirectory structure to maintain consistency and discoverability.

#### Module/Package Documentation (Level 3+)

**Purpose**: Co-located documentation for specific modules, services, packages, or code sections

**When to Create Module-Level Docs**:
- Complex modules that need explanation beyond code comments
- Services with specific usage patterns or business logic
- Shared utilities that need usage documentation
- Components with non-obvious design decisions
- Packages with specific configuration or setup

**Directory Structure** (same pattern as other levels):
```
/backend/src/services/docs/
├── architecture/           # Service layer design patterns
│   └── SERVICE-PATTERN.md  # How services are structured
├── implementation/         # Service-specific guides
│   ├── authentication.md   # Auth service details
│   └── notification.md     # Notification service details
└── README.md               # Services overview

/frontend/src/components/calendar/docs/
├── architecture/           # Calendar component architecture
│   └── STATE-MANAGEMENT.md # How calendar manages state
├── implementation/         # Implementation details
│   └── EVENT-HANDLING.md   # Event handling patterns
└── README.md               # Calendar component overview

/scripts/deployment/docs/
└── README.md               # Deployment scripts usage
```

**Scope**: Specific to that module, service, or package

**Example Paths**:
- `/backend/src/services/docs/implementation/authentication.md`
- `/backend/src/models/docs/architecture/VALIDATION-PATTERNS.md`
- `/frontend/src/components/calendar/docs/README.md`
- `/scripts/deployment/docs/README.md`

#### Decision Tree: Where Should This Doc Go?

**Question 1: Is this documentation about the entire system or cross-component?**
- **YES** → Place in `/docs/[category]/`
- **NO** → Continue to Question 2

**Question 2: Is this documentation specific to ONE technical component (backend, frontend, database)?**
- **YES, and applies to the entire component** → Place in `/[component]/docs/[category]/`
- **NO** → Place in `/docs/[category]/`
- **YES, but only to a specific module/package within the component** → Continue to Question 3

**Question 3: Is this documentation specific to a module, service, or package?**
- **YES** → Place in `/[path-to-module]/docs/` with appropriate subdirectories
- Example: `/backend/src/services/docs/implementation/authentication.md`
- Example: `/frontend/src/components/calendar/docs/README.md`

**Question 4: What category does this documentation fall under?**
- **Architecture** → `[location]/docs/architecture/`
- **Implementation** → `[location]/docs/implementation/`
- **Setup/Configuration** → `[location]/docs/setup/`
- **Testing** → `[location]/docs/testing/`
- **Simple cases** → `[location]/docs/README.md` (just a README, no subdirectories)
- **Other** → `[location]/docs/[appropriate-category]/`

**Note**: `[location]` can be `/docs/`, `/[component]/docs/`, or `/[path-to-module]/docs/` at any level

#### Examples of Correct Placement

| Documentation | Correct Location | Reason |
|---------------|------------------|--------|
| **Project-Wide** | | |
| System architecture overview | `/docs/architecture/SYSTEM-ARCHITECTURE.md` | Cross-component, high-level |
| Calendar feature design | `/docs/features/calendar/design.md` | Product feature (cross-component) |
| Project-wide setup guide | `/docs/setup/QUICKSTART.md` | Cross-component setup |
| Database schema (system-wide) | `/docs/architecture/DATA-MODEL.md` | Cross-component architecture |
| **Technical Component Level** | | |
| Backend MVC architecture | `/backend/docs/architecture/ARCHITECTURE.md` | Backend-specific architecture |
| Backend data model | `/backend/docs/architecture/DATA-MODEL.md` | Backend-specific architecture |
| Backend API implementation | `/backend/docs/implementation/API-ENDPOINTS.md` | Backend-specific implementation |
| Backend migrations guide | `/backend/docs/implementation/MIGRATIONS.md` | Backend-specific implementation |
| Backend environment config | `/backend/docs/setup/ENV-VARIABLES.md` | Backend-specific setup |
| Backend testing guide | `/backend/docs/testing/TESTING.md` | Backend-specific testing |
| Frontend component architecture | `/frontend/docs/architecture/ARCHITECTURE.md` | Frontend-specific architecture |
| Frontend component API | `/frontend/docs/implementation/COMPONENTS.md` | Frontend-specific implementation |
| Frontend styling guide | `/frontend/docs/implementation/STYLING-GUIDE.md` | Frontend-specific implementation |
| Frontend local dev setup | `/frontend/docs/setup/LOCAL-DEV.md` | Frontend-specific setup |
| Frontend testing guide | `/frontend/docs/testing/TESTING.md` | Frontend-specific testing |
| Database seed scripts | `/database/docs/implementation/SEEDING.md` | Database-specific implementation |
| **Module/Package Level** | | |
| Auth service patterns | `/backend/src/services/docs/implementation/authentication.md` | Service module specifics |
| Model validation rules | `/backend/src/models/docs/architecture/VALIDATION-PATTERNS.md` | Model module architecture |
| Calendar component internals | `/frontend/src/components/calendar/docs/README.md` | Component module docs |
| Deployment script usage | `/scripts/deployment/docs/README.md` | Script module usage |

#### Benefits of Hierarchical Documentation

1. **Co-location**: Documentation lives at the appropriate level near the code it describes
2. **Clarity**: Clear separation between system, component, and module documentation
3. **Maintainability**: Easier to update docs when changing code in that specific area
4. **Discoverability**: Developers find relevant docs at the level they're working at
5. **Scalability**: Reduces clutter at any level by distributing documentation hierarchically
6. **Flexibility**: Documentation can exist wherever it provides value in the directory tree

#### README Files at Any Level

**Every directory with code SHOULD have a README.md when it needs explanation:**

- `/backend/README.md` - Backend overview, how to run, basic info
- `/frontend/README.md` - Frontend overview, how to run, basic info
- `/database/README.md` - Database overview, connection info
- `/backend/src/services/README.md` - Services overview (if complex)
- `/frontend/src/components/calendar/README.md` - Calendar component overview (if complex)

**README.md should**:
- Provide quick orientation for developers
- Link to detailed documentation in `[current-location]/docs/` if it exists
- Include quick start commands or usage examples
- NOT duplicate higher-level documentation

#### Cross-Referencing Across Documentation Hierarchy

Documentation should reference other documentation at appropriate levels:

**Project-wide docs → Component docs:**
```markdown
For detailed API implementation, see `/backend/docs/implementation/API-ENDPOINTS.md`
```

**Component docs → Project-wide docs:**
```markdown
For system architecture overview, see `/docs/architecture/SYSTEM-ARCHITECTURE.md`
```

**Component docs → Module docs:**
```markdown
For auth service details, see `/backend/src/services/docs/implementation/authentication.md`
```

**Module docs → Component docs:**
```markdown
For overall backend patterns, see `/backend/docs/architecture/ARCHITECTURE.md`
For system architecture overview, see `/docs/architecture/SYSTEM-ARCHITECTURE.md`
```

#### Enforcement for Hierarchical Documentation

1. **CREATE** documentation at the appropriate level (project, component, or module)
2. **MIRROR** the same subdirectory pattern at all levels (architecture/, implementation/, setup/, testing/)
3. **DO NOT** duplicate documentation across levels - each level has its purpose
4. **DO** link between different levels of documentation
5. **KEEP** documentation focused on the appropriate scope for its level
6. **MAINTAIN** clear distinction between system-wide, component, and module concerns
7. **USE** subdirectories when documentation grows beyond a simple README
8. **ALLOW** module-level docs anywhere in the tree where they provide value

#### Migration Guide for Existing Component Documentation

If you have existing component documentation in a flat structure, reorganize it:

**Before (Flat Structure)**:
```
backend/docs/
├── ARCHITECTURE.md
├── DATA-MODEL.md
├── API-ENDPOINTS.md
├── MIGRATIONS.md
├── ENV-VARIABLES.md
└── TESTING.md
```

**After (Subdirectory Structure)**:
```
backend/docs/
├── architecture/
│   ├── ARCHITECTURE.md       # Backend MVC architecture
│   ├── DATA-MODEL.md          # Database schema and ORM patterns
│   └── API-DESIGN.md          # API design decisions
├── implementation/
│   ├── API-ENDPOINTS.md       # Detailed endpoint implementation
│   └── MIGRATIONS.md          # Migration procedures
├── setup/
│   └── ENV-VARIABLES.md       # Environment configuration
├── testing/
│   └── TESTING.md             # Backend test suite
└── README.md                  # Backend overview
```

**Migration Steps**:
1. Create subdirectories at appropriate level: `architecture/`, `implementation/`, `setup/`, `testing/`
2. Move files to appropriate subdirectories based on their purpose
3. Update all internal references to reflect new paths
4. Update higher-level documentation references
5. Update task files and code comments with new paths
6. Consider creating module-level docs for complex subdirectories

---

## Summary: Hierarchical Documentation Strategy

### Level 1: Project-Wide Documentation (`/docs/`)
- **Purpose**: Architecture, features, setup, cross-component concerns
- **Audience**: All team members, stakeholders, new developers
- **Scope**: Entire system, high-level design, cross-component features

### Level 2: Technical Component Documentation (`/[component]/docs/`)
- **Purpose**: Component-specific architecture and implementation details
- **Audience**: Developers working on that specific technical component
- **Scope**: Single technical component (backend, frontend, database, etc.)

### Level 3+: Module/Package Documentation (`/[any-path]/docs/`)
- **Purpose**: Module, service, or package-specific documentation
- **Audience**: Developers working in that specific part of the codebase
- **Scope**: Specific module, service, component, or package

**Universal Pattern**: All levels use the same subdirectory structure (architecture/, implementation/, setup/, testing/) for consistency.

**Both layers are essential for complete project documentation.**

---

**Last Updated:** December 3, 2025 by gregrata