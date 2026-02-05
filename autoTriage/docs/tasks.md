# AutoTriage Implementation Tasks

> **Feature**: AI-Powered GitHub Issue Management  
> **Design Document**: [design.md](./design.md)  
> **Created**: 2026-02-05  
> **Last Updated**: 2026-02-05

---

## Tasks

### Phase 1: Enable Auto-Apply (Quick Win)

**Status**: Not Started  
**Progress**: 0/5 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD

- [ ] 1.0 Enable actual triage application in GitHub Actions
  - **Relevant Documentation:**
    - `autoTriage/docs/design.md` - FR2: Assignee Selection requirements
    - `.github/workflows/auto-triage-issues.yml` - Current workflow definition
    - `autoTriage/README.md` - Setup instructions and label requirements
  - [ ] 1.1 Add `--apply` flag to triage_issue.py invocation in auto-triage-issues.yml
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 1.2 Verify GITHUB_TOKEN has `issues: write` and `pull-requests: write` permissions
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 1.3 Create required priority labels (P0, P1, P2, P3, P4) in repository if missing
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 1.4 Create required type labels (bug, feature, enhancement, documentation, question) if missing
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 1.5 Test end-to-end by creating a test issue and verifying labels/assignee are applied
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD

---

### Phase 2: Copilot Auto-Fix Integration

**Status**: Not Started  
**Progress**: 0/8 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD

- [ ] 2.0 Implement automatic PR creation for copilot-fixable issues
  - **Relevant Documentation:**
    - `autoTriage/docs/design.md` - FR3: Copilot Auto-Fix requirements
    - `autoTriage/services/llm_service.py` - `is_copilot_fixable()` method
    - `autoTriage/services/github_service.py` - GitHub API wrapper patterns
    - `autoTriage/services/intake_service.py` - Triage flow where Copilot fix should trigger
  - [ ] 2.1 Research GitHub Copilot Coding Agent API and document integration approach
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 2.2 Create `services/copilot_service.py` with CopilotService class
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 2.3 Implement `create_fix_branch()` method to create branch `copilot-fix/issue-{number}`
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 2.4 Implement `invoke_copilot_fix()` method to trigger Copilot agent with fix_suggestions
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 2.5 Implement `create_draft_pr()` method to create draft PR linked to issue
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 2.6 Integrate CopilotService into intake_service.py triage flow
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 2.7 Add workflow step in auto-triage-issues.yml to handle Copilot fix output
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 2.8 Add error handling for Copilot API failures (fallback to human assignment)
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD

---

### Phase 3: SLA Escalation System

**Status**: Not Started  
**Progress**: 0/10 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD

- [ ] 3.0 Implement priority-based SLA tracking and escalation
  - **Relevant Documentation:**
    - `autoTriage/docs/design.md` - FR4: SLA Escalation requirements
    - `autoTriage/config/team-members.json` - Team roster (add escalation_chain)
    - `autoTriage/services/teams_service.py` - Teams notification patterns
    - `autoTriage/services/github_service.py` - Issue update methods
  - [ ] 3.1 Update team-members.json schema to include `escalation_chain` and `sla_hours` config
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 3.2 Create `services/escalation_service.py` with EscalationService class
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 3.3 Implement `get_sla_for_priority()` method returning hours based on P0-P4
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 3.4 Implement `check_sla_breach()` method comparing last update time to SLA threshold
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 3.5 Implement `escalate_issue()` method to reassign to Lead and add `needs-attention` label
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 3.6 Implement `escalate_to_manager()` for second-level escalation if Lead doesn't respond
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 3.7 Add @mention notification for Lead and Manager in escalation comment
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 3.8 Create `.github/workflows/escalate-stale-issues.yml` with hourly schedule trigger
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 3.9 Implement `escalation_check.py` CLI script for workflow to invoke
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 3.10 Add Teams notification on escalation (optional, if TEAMS_WEBHOOK_URL configured)
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD

---

### Phase 4: Re-triage on Updates

**Status**: Not Started  
**Progress**: 0/7 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD

- [ ] 4.0 Implement re-triage triggers for issue updates
  - **Relevant Documentation:**
    - `autoTriage/docs/design.md` - FR5: Re-triage requirements
    - `.github/workflows/auto-triage-issues.yml` - Workflow trigger configuration
    - `autoTriage/services/intake_service.py` - Triage logic
    - `autoTriage/services/github_service.py` - `TRIAGE_BOT_USERS` constant
  - [ ] 4.1 Add `issues.edited` trigger to auto-triage-issues.yml workflow
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 4.2 Add `issues.labeled` trigger for manual label changes
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 4.3 Add `issue_comment.created` trigger for substantive comments
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 4.4 Implement `should_retriage()` function to detect if issue was triaged in last 5 minutes
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 4.5 Implement `is_substantive_comment()` function using LLM to detect technical content
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 4.6 Modify `_apply_triage_changes()` to update existing triage comment instead of creating new
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 4.7 Add `--retriage` flag to triage_issue.py for re-triage mode
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD

---

### Phase 5: Team Configuration Updates

**Status**: Not Started  
**Progress**: 0/5 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD

- [ ] 5.0 Update team member expertise and escalation config
  - **Relevant Documentation:**
    - `autoTriage/docs/design.md` - Section 6.1 Configuration Files
    - `autoTriage/config/team-members.json` - Current team roster
    - `autoTriage/services/config_parser.py` - Config loading logic
  - [ ] 5.1 Add expertise arrays for Josh Oratz (joratz) - Backend Engineer
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 5.2 Add expertise arrays for Mengyi Xu (mengyimicro) - Backend Engineer
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 5.3 Add expertise arrays for Johan Broberg (pontemonti) - Backend Engineer
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 5.4 Add `escalation_chain` config with lead (sellakumaran) and manager (tmlsousa)
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 5.5 Add `sla_hours` config with P0:6, P1:12, P2:24, P3:72, P4:72
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD

---

### Phase 6: Security Issue Handling

**Status**: Not Started  
**Progress**: 0/4 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD

- [ ] 6.0 Implement security issue detection and routing
  - **Relevant Documentation:**
    - `autoTriage/docs/design.md` - US4: Security Issue Handling
    - `autoTriage/services/llm_service.py` - Classification logic
    - `autoTriage/services/intake_service.py` - Assignee selection
    - `autoTriage/config/prompts.yaml` - AI prompts
  - [ ] 6.1 Add security keywords list to config (vulnerability, CVE, injection, XSS, auth bypass, etc.)
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 6.2 Implement `is_security_issue()` function in llm_service.py
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 6.3 Override assignee to Tech Lead when security issue detected
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 6.4 Auto-apply `security` label and P0/P1 priority for security issues
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD

---

### Phase 7: Observability and Metrics

**Status**: Not Started  
**Progress**: 0/6 tasks complete (0%)  
**Phase Started**: TBD  
**Phase Completed**: TBD

- [ ] 7.0 Implement triage accuracy tracking and metrics
  - **Relevant Documentation:**
    - `autoTriage/docs/design.md` - Section 8: Success Metrics
    - `autoTriage/services/intake_service.py` - Result output
    - `autoTriage/triage_issue.py` - CLI output
  - [ ] 7.1 Create `metrics/triage_log.json` to store triage decisions with timestamps
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 7.2 Implement `log_triage_decision()` function to append to triage log
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 7.3 Implement `calculate_accuracy_metrics()` to compare predictions vs actual resolutions
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 7.4 Create `scripts/generate_metrics_report.py` for monthly accuracy reports
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 7.5 Add GitHub Action workflow summary with triage stats
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD
  - [ ] 7.6 Track time-to-first-response metric in triage log
    - **Started**: TBD
    - **Completed**: TBD
    - **Duration**: TBD

---

## Summary

| Phase | Tasks | Status |
|-------|-------|--------|
| Phase 1: Enable Auto-Apply | 5 | Not Started |
| Phase 2: Copilot Auto-Fix | 8 | Not Started |
| Phase 3: SLA Escalation | 10 | Not Started |
| Phase 4: Re-triage | 7 | Not Started |
| Phase 5: Team Config | 5 | Not Started |
| Phase 6: Security Handling | 4 | Not Started |
| Phase 7: Observability | 6 | Not Started |
| **Total** | **45** | **0% Complete** |

---

## Recommended Execution Order

1. **Phase 5** (Team Config) - Quick win, enables better assignment immediately
2. **Phase 1** (Auto-Apply) - Core functionality to actually apply triage
3. **Phase 6** (Security) - Critical for open source repo safety
4. **Phase 3** (Escalation) - Ensures accountability
5. **Phase 4** (Re-triage) - Improves accuracy over time
6. **Phase 2** (Copilot) - Highest complexity, biggest value
7. **Phase 7** (Observability) - Measure success after features are live

---

> **Next Step**: Start with Phase 5, Task 5.1 to update team member expertise.
