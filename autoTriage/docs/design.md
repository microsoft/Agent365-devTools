# AutoTriage: AI-Powered GitHub Issue Management

> **Feature**: Automated Issue Triage, Assignment, and Auto-Fix System  
> **Status**: In Progress (Phase 0 Complete)  
> **Last Updated**: 2026-02-05  
> **Authors**: Mengyi Xu, Mrunal Hirve

---

## 1. Introduction / Overview

AutoTriage is an AI-powered GitHub automation system that automatically triages new issues, assigns them to the right team members based on expertise, and creates pull requests for simple fixes using GitHub Copilot.

**Problem Statement**: As the Agent365-devTools open source repository grows in adoption, manual issue triage becomes a bottleneck. Issues may sit unassigned for days, reducing community confidence and slowing down contributions.

**Solution**: A fully automated GitHub Agent that:
- Classifies issues instantly (bug, feature, docs, question)
- Assigns priority levels (P0-P4) based on content analysis
- Routes issues to the right engineer based on expertise matching
- Creates draft PRs automatically for simple fixes
- Escalates unresolved issues based on SLA timers

---

## 2. Goals

| Goal | Metric | Target |
|------|--------|--------|
| **G1**: Reduce manual triage effort | % of issues auto-triaged correctly | 80% |
| **G2**: Improve response time | Time from issue creation to first response | < 1 hour |
| **G3**: Automate simple fixes | Number of auto-generated PRs merged | Track monthly |
| **G4**: Ensure accountability | Issues resolved within SLA | 90% |
| **G5**: Balance workload | Even distribution across team members | Variance < 20% |

---

## 3. User Stories

### US1: Automatic Triage on Issue Creation
**As a** community contributor,  
**I want** my issue to be automatically classified and prioritized,  
**So that** I know my issue is being tracked and will be addressed appropriately.

**Acceptance Criteria:**
- [ ] Issue receives type label (bug/feature/docs/question) within 5 minutes
- [ ] Issue receives priority label (P0-P4) within 5 minutes
- [ ] Bot posts a triage comment explaining the classification
- [ ] Issue is assigned to an appropriate team member

### US2: Automatic PR Creation for Simple Fixes
**As a** maintainer,  
**I want** GitHub Copilot to automatically create PRs for simple issues,  
**So that** I can review and merge fixes faster instead of writing them myself.

**Acceptance Criteria:**
- [ ] System identifies "copilot-fixable" issues with 85%+ accuracy
- [ ] Draft PR is created within 10 minutes of issue creation
- [ ] PR is linked to the original issue
- [ ] PR requires human approval before merge

### US3: SLA-Based Escalation
**As an** engineering manager,  
**I want** issues to escalate automatically if not updated within SLA,  
**So that** no issue falls through the cracks.

**Acceptance Criteria:**
- [ ] P0 issues escalate after 24 hours
- [ ] P1 issues escalate after 48 hours
- [ ] P2 issues escalate after 72 hours
- [ ] P3/P4 issues escalate after 120 hours (5 working days)
- [ ] Escalation notifies both Team Lead and Engineering Manager

### US4: Security Issue Handling
**As a** security-conscious maintainer,  
**I want** security-related issues to be routed to the Tech Lead,  
**So that** sensitive issues are handled by senior engineers.

**Acceptance Criteria:**
- [ ] Security issues are detected via keyword/content analysis
- [ ] Security issues are always assigned to Tech Lead
- [ ] Security issues receive P0 or P1 priority by default

### US5: Re-triage on Issue Updates
**As a** contributor,  
**I want** my issue to be re-analyzed when I update it with new information,  
**So that** the priority and assignment reflect the latest context.

**Acceptance Criteria:**
- [ ] Re-triage triggers when issue body is edited
- [ ] Re-triage triggers when new labels are added manually
- [ ] Re-triage triggers when substantive comments are added
- [ ] Bot updates the triage comment (doesn't create duplicates)

---

## 4. Functional Requirements

### FR1: Issue Classification (COMPLETE)
- **FR1.1**: System MUST classify issues into types: `bug`, `feature`, `documentation`, `question`
- **FR1.2**: System MUST assign priority: `P0` (critical), `P1` (high), `P2` (medium), `P3` (low), `P4` (nice-to-have)
- **FR1.3**: System MUST provide confidence score (0.0-1.0) for each classification
- **FR1.4**: System MUST provide human-readable rationale for each decision

### FR2: Assignee Selection (COMPLETE)
- **FR2.1**: System MUST select assignee based on team member expertise from `config/team-members.json`
- **FR2.2**: System MUST consider recent commit history to related files
- **FR2.3**: System MUST factor in workload balance (contribution scores)
- **FR2.4**: System MUST assign security issues to Tech Lead
- **FR2.5**: System MUST actually apply the assignee (not just recommend)

### FR3: Copilot Auto-Fix (TODO)
- **FR3.1**: System MUST detect if issue is suitable for Copilot auto-fix
- **FR3.2**: System MUST create a new branch from `main` for auto-fixes
- **FR3.3**: System MUST invoke GitHub Copilot to generate the fix
- **FR3.4**: System MUST create a draft PR linked to the issue
- **FR3.5**: System MUST NOT auto-merge; human review is required

### FR4: SLA Escalation (COMPLETE)
- **FR4.1**: System MUST track time since last update for each issue
- **FR4.2**: System MUST escalate based on priority-specific SLAs:
  | Priority | SLA | Escalation To |
  |----------|-----|---------------|
  | P0 | 24 hours | Lead + Manager |
  | P1 | 48 hours | Lead + Manager |
  | P2 | 72 hours | Lead + Manager |
  | P3/P4 | 120 hours (5 days) | Lead + Manager |
- **FR4.3**: Escalation MUST add Lead as assignee to the issue
- **FR4.4**: If Lead doesn't respond within another SLA cycle, escalate to Manager
- **FR4.5**: System MUST notify via GitHub @mention and/or Teams message

### FR5: Re-triage (COMPLETE)
- **FR5.1**: System MUST re-triage when issue body is edited
- **FR5.2**: System MUST re-triage when labels are manually changed
- **FR5.3**: System MUST re-triage when comments contain new technical details
- **FR5.4**: System MUST update existing triage comment (not create new one)
- **FR5.5**: System MUST skip re-triage if already triaged by bot in last 5 minutes

### FR6: Label Management (COMPLETE)
- **FR6.1**: System MUST map AI classifications to existing repository labels
- **FR6.2**: System MUST add `copilot-fixable` label when appropriate
- **FR6.3**: System MUST add `needs-attention` label on escalation
- **FR6.4**: System MUST add `security` label for security-related issues

### FR7: Notification (COMPLETE)
- **FR7.1**: System MUST @mention assignee in triage comment
- **FR7.2**: System MUST @mention Lead and Manager on escalation
- **FR7.3**: System MAY post to MS Teams channel (optional, configurable)

---

## 5. Non-Goals (Out of Scope)

| Non-Goal | Reason |
|----------|--------|
| **NG1**: Auto-merge PRs | Security risk; human review required for all changes |
| **NG2**: Cross-repository triage | Focus on Agent365-devTools first; expand later |
| **NG3**: Slack integration | Teams is primary; Slack can be added in future |
| **NG4**: Custom ML model training | Use Azure OpenAI/GitHub Models; no custom training |
| **NG5**: Issue creation | System only triages existing issues, doesn't create new ones |
| **NG6**: Handle confidential/private issues | Open source focus; private repos need different handling |

---

## 6. Design Considerations

### 6.1 Configuration Files

**Team Configuration** (`config/team-members.json`):
```json
{
  "team_members": [
    {
      "name": "Sellakumaran Kanagarathnam",
      "role": "Tech Lead",
      "login": "sellakumaran",
      "contributions": 20,
      "expertise": ["architecture", "backend", "API design", "security"]
    }
  ],
  "escalation_chain": {
    "lead": ["sellakumaran", "pontemonti"],
    "manager": "tmlsousa"
  },
  "sla_hours": {
    "P0": 24,
    "P1": 48,
    "P2": 72,
    "P3": 120,
    "P4": 120
  }
}
```

**AI Prompts** (`config/prompts.yaml`):
- Customizable prompts for classification, assignment, and fix generation
- No code changes required to tune AI behavior

### 6.2 GitHub Action Triggers

| Event | Action |
|-------|--------|
| `issues.opened` | Full triage + assignment + optional Copilot fix |
| `issues.edited` | Re-triage (update existing analysis) |
| `issues.labeled` | Re-triage if manual label conflicts |
| `issue_comment.created` | Re-triage if comment has technical content |
| `schedule (hourly)` | SLA check + escalation workflow |

### 6.3 Escalation Flow

```
Issue Created
     │
     ▼
┌─────────────────┐
│ Triage + Assign │
│  to Engineer    │
└────────┬────────┘
         │
         ▼
    SLA Timer Starts
         │
         ▼
┌─────────────────────────────────────┐
│ Is issue updated within SLA?        │
└──────────┬─────────────┬────────────┘
           │ YES         │ NO
           ▼             ▼
    Timer Resets    Escalate to Lead
                         │
                         ▼
                  ┌──────────────────┐
                  │ Lead responds?   │
                  └──────┬───────────┘
                         │ NO (another SLA cycle)
                         ▼
                  Escalate to Manager
                         │
                         ▼
                  @mention Lead + Manager
                  Add 'needs-attention' label
```

---

## 7. Technical Considerations

### 7.1 Dependencies

| Dependency | Purpose | Status |
|------------|---------|--------|
| Azure OpenAI / GitHub Models | AI classification | ✅ Integrated |
| PyGithub | GitHub API | ✅ Integrated |
| GitHub Actions | Workflow automation | ✅ Integrated |
| GitHub Copilot Coding Agent API | Auto-fix PRs | ❌ TODO |

### 7.2 Environment Variables / Secrets

| Secret | Required | Description |
|--------|----------|-------------|
| `GITHUB_TOKEN` | Yes | Auto-provided by GitHub Actions |
| `AZURE_OPENAI_API_KEY` | Yes* | Azure OpenAI authentication |
| `AZURE_OPENAI_ENDPOINT` | Yes* | Azure OpenAI endpoint |
| `AZURE_OPENAI_DEPLOYMENT` | Yes* | Model deployment name |
| `MODELS_API_KEY` | Alt | GitHub Models (if not using Azure) |
| `TEAMS_WEBHOOK_URL` | No | MS Teams notifications |

*Either Azure OpenAI OR GitHub Models API key required

### 7.3 Rate Limits

- GitHub API: 5000 requests/hour (authenticated)
- Azure OpenAI: Varies by deployment (typically 120 RPM)
- Implement caching to reduce API calls (already done: 15-minute cache TTL)

### 7.4 Security Considerations

- **GITHUB_TOKEN permissions**: `issues: write`, `contents: read`, `pull-requests: write`
- **No secrets in logs**: Ensure API keys are never logged
- **Security issues**: Always route to Tech Lead, never auto-fix

---

## 8. Success Metrics

| Metric | Measurement Method | Target | Current |
|--------|-------------------|--------|---------|
| **Triage Accuracy** | Manual audit of 50 random issues/month | 80% | TBD |
| **Time to First Response** | Avg time from issue creation to bot comment | < 5 min | TBD |
| **Auto-Fix PR Merge Rate** | PRs created by Copilot that get merged | 50% | N/A |
| **SLA Compliance** | Issues resolved within priority SLA | 90% | TBD |
| **Escalation Rate** | % of issues requiring escalation | < 15% | TBD |
| **False Positive Rate** | Issues incorrectly flagged as copilot-fixable | < 10% | TBD |

---

## 9. Open Questions

| # | Question | Status | Resolution |
|---|----------|--------|------------|
| Q1 | How to handle issues with multiple types (bug + feature)? | Open | Pick primary type based on confidence |
| Q2 | Should Copilot fixes be in a dedicated branch prefix? | Open | Propose: `copilot-fix/issue-{number}` |
| Q3 | What if assignee is on PTO? | Open | Check contribution score > 40 as "busy" indicator |
| Q4 | Rate limit handling for high-volume days? | Open | Queue issues, process in batches |
| Q5 | How to measure "substantive" comments for re-triage? | Open | LLM-based or keyword detection |

---

## 10. Implementation Phases

### Phase 0: Foundation ✅ COMPLETE
- Core triage engine
- GitHub/LLM integration
- Basic GitHub Action

### Phase 1: Enable Auto-Apply 🔧 IN PROGRESS
- Add `--apply` flag to workflow
- Verify permissions
- Create required labels

### Phase 2: Copilot Auto-Fix ⬜ TODO
- Copilot API integration
- Branch creation
- Draft PR creation

### Phase 3: Escalation System ⬜ TODO
- SLA tracking
- Escalation workflow
- Notification system

### Phase 4: Re-triage ⬜ TODO
- Edit/label/comment triggers
- Deduplication logic

### Phase 5: Observability ⬜ TODO
- Metrics tracking
- Dashboard

---

## Appendix A: File Structure

```
autoTriage/
├── triage_issue.py           # CLI entry point
├── requirements.txt          # Python dependencies
├── README.md                 # User documentation
├── docs/
│   ├── design.md             # This PRD
│   ├── ROADMAP.md            # Implementation roadmap (temporary)
│   └── tasks.md              # Task list (to be generated)
├── config/
│   ├── team-members.json     # Team roster + escalation config
│   └── prompts.yaml          # AI prompts
├── models/
│   ├── issue_classification.py
│   ├── team_config.py
│   └── ado_models.py
├── services/
│   ├── intake_service.py     # Core triage logic
│   ├── github_service.py     # GitHub API wrapper
│   ├── llm_service.py        # AI integration
│   ├── config_parser.py      # Config loading
│   ├── prompt_loader.py      # Prompt management
│   ├── teams_service.py      # Teams notifications
│   ├── copilot_service.py    # (TODO) Copilot integration
│   └── escalation_service.py # (TODO) SLA tracking
└── scripts/
    └── update_contributions.py

.github/workflows/
├── auto-triage-issues.yml     # Main triage workflow
├── update-team-workload.yml   # Weekly workload updates
└── escalate-stale-issues.yml  # (TODO) Hourly escalation check
```

---

> **Next Step**: Generate tasks from this design using `generate-tasks.prompt.md`

---

**Document History**:
| Date | Author | Changes |
|------|--------|---------|
| 2026-02-05 | Mrunal Hirve | Initial PRD based on existing implementation |
