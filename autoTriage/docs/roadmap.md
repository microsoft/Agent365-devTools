# AutoTriage Roadmap

> **Note**: This is a temporary planning document. Delete once all features are implemented.  
> **Full details**: See [design.md](./design.md) and [tasks.md](./tasks.md)

---

## Implementation Status

### Phase 0: Foundation
- [x] Core triage engine (`services/intake_service.py`)
- [x] GitHub API integration (`services/github_service.py`)
- [x] Azure OpenAI / GitHub Models (`services/llm_service.py`)
- [x] Issue classification (bug/feature/docs/question)
- [x] Priority assessment (P0-P4)
- [x] Copilot-fixable detection
- [x] Smart assignee selection (LLM-based)
- [x] Fix suggestions generation
- [x] Label mapping to repo labels
- [x] CLI interface (`triage_issue.py`)
- [x] GitHub Action for new issues
- [x] Workload balancing workflow
- [x] Team configuration (`config/team-members.json`)
- [x] Customizable AI prompts (`config/prompts.yaml`)
- [x] MS Teams notifications

### Phase 1: Enable Auto-Apply
- [ ] Add `--apply` flag to workflow
- [ ] Create required labels (P0-P4, bug, feature, etc.)
- [ ] Test end-to-end with real issue

### Phase 2: Copilot Auto-Fix
- [ ] Create `services/copilot_service.py`
- [ ] Implement branch creation for auto-fixes
- [ ] Invoke Copilot agent API
- [ ] Create draft PR linked to issue
- [ ] Add error handling (fallback to human)

### Phase 3: SLA Escalation
- [ ] Add `escalation_chain` to team-members.json
- [ ] Add `sla_hours` config (P0:6h, P1:12h, P2:24h, P3/P4:72h)
- [ ] Create `services/escalation_service.py`
- [ ] Create escalation workflow (hourly check)
- [ ] Notify Lead + Manager on breach
- [ ] Add Tech Lead as assignee on escalation

### Phase 4: Re-triage on Updates
- [ ] Add `issues.edited` trigger
- [ ] Add `issues.labeled` trigger
- [ ] Add `issue_comment.created` trigger
- [ ] Skip if triaged in last 5 minutes
- [ ] Update existing comment (no duplicates)

### Phase 5: Team Config
- [ ] Add expertise for joratz
- [ ] Add expertise for mengyimicro
- [ ] Add expertise for pontemonti
- [ ] Add escalation chain config
- [ ] Add SLA hours config

### Phase 6: Security Handling
- [ ] Detect security issues via keywords
- [ ] Route security issues to Tech Lead
- [ ] Auto-apply `security` label
- [ ] Default to P0/P1 priority

### Phase 7: Observability
- [ ] Log triage decisions
- [ ] Track accuracy metrics
- [ ] Track time-to-first-response
- [ ] Generate monthly reports

---

## Quick Reference

| Priority | SLA | Escalation |
|----------|-----|------------|
| P0 | 24 hrs | Lead + Manager |
| P1 | 48 hrs | Lead + Manager |
| P2 | 72 hrs | Lead + Manager |
| P3/P4 | 120 hrs (5 days) | Lead + Manager |

| Metric | Target |
|--------|--------|
| Triage accuracy | 80% |
| Auto-fix PR merge rate | 50% |
| SLA compliance | 90% |
