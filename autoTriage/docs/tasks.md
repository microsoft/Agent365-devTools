# AutoTriage Implementation Tasks

> **Feature**: AI-Powered GitHub Issue Management  
> **Design Document**: [design.md](./design.md)  
> **Created**: 2026-02-05  
> **Last Updated**: 2026-02-06

---

## Tasks

### Phase 1: Enable Auto-Apply (Quick Win)

**Status**: Complete  
**Progress**: 5/5 tasks complete (100%)  
**Phase Started**: 2026-02-05  
**Phase Completed**: 2026-02-05

- [x] 1.0 Enable actual triage application in GitHub Actions
  - **Relevant Documentation:**
    - `autoTriage/docs/design.md` - FR2: Assignee Selection requirements
    - `.github/workflows/auto-triage-issues.yml` - Current workflow definition
    - `autoTriage/README.md` - Setup instructions and label requirements
  - [x] 1.1 Add `--apply` flag to triage_issue.py invocation in auto-triage-issues.yml
  - [x] 1.2 Verify GITHUB_TOKEN has `issues: write` and `pull-requests: write` permissions
  - [x] 1.3 Create required priority labels (P0, P1, P2, P3, P4) in repository if missing
  - [x] 1.4 Create required type labels (bug, feature, enhancement, documentation, question) if missing
  - [x] 1.5 Test end-to-end by creating a test issue and verifying labels/assignee are applied

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
  - [ ] 2.2 Create `services/copilot_service.py` with CopilotService class
  - [ ] 2.3 Implement `create_fix_branch()` method to create branch `copilot-fix/issue-{number}`
  - [ ] 2.4 Implement `invoke_copilot_fix()` method to trigger Copilot agent with fix_suggestions
  - [ ] 2.5 Implement `create_draft_pr()` method to create draft PR linked to issue
  - [ ] 2.6 Integrate CopilotService into intake_service.py triage flow
  - [ ] 2.7 Add workflow step in auto-triage-issues.yml to handle Copilot fix output
  - [ ] 2.8 Add error handling for Copilot API failures (fallback to human assignment)

---

### Phase 3: SLA Escalation System

**Status**: Complete  
**Progress**: 10/10 tasks complete (100%)  
**Phase Started**: 2026-02-05  
**Phase Completed**: 2026-02-05

- [x] 3.0 Implement priority-based SLA tracking and escalation
  - **Relevant Documentation:**
    - `autoTriage/docs/design.md` - FR4: SLA Escalation requirements
    - `autoTriage/config/team-members.json` - Team roster with escalation_chain
    - `autoTriage/services/teams_service.py` - Teams notification patterns
    - `autoTriage/services/github_service.py` - Issue update methods
  - [x] 3.1 Update team-members.json schema to include `escalation_chain` and `sla_hours` config
  - [x] 3.2 Create `services/escalation_service.py` with EscalationService class
  - [x] 3.3 Implement `get_sla_for_priority()` method returning hours based on P0-P4
  - [x] 3.4 Implement `check_sla_breach()` method comparing last update time to SLA threshold
  - [x] 3.5 Implement `escalate_issue()` method to add Tech Lead as assignee and add `needs-attention` label
  - [x] 3.6 Implement `escalate_to_manager()` for second-level escalation if Lead doesn't respond
  - [x] 3.7 Add @mention notification for Lead and Manager in escalation comment
  - [x] 3.8 Create `.github/workflows/escalate-stale-issues.yml` with daily schedule (8 AM UTC)
  - [x] 3.9 Implement `escalation_check.py` CLI script for workflow to invoke
  - [x] 3.10 Add Teams notification on escalation (optional, if TEAMS_WEBHOOK_URL configured)

> **Implementation Note**: Changed from hourly to daily schedule (8 AM UTC) to reduce noise.

---

### Phase 4: Re-triage on Updates

**Status**: Complete  
**Progress**: 7/7 tasks complete (100%)  
**Phase Started**: 2026-02-05  
**Phase Completed**: 2026-02-05

- [x] 4.0 Implement re-triage triggers for issue updates
  - **Relevant Documentation:**
    - `autoTriage/docs/design.md` - FR5: Re-triage requirements
    - `.github/workflows/auto-triage-issues.yml` - Workflow trigger configuration
    - `autoTriage/services/intake_service.py` - Triage logic
    - `autoTriage/services/github_service.py` - `TRIAGE_BOT_USERS` constant
  - [x] 4.1 Add `issues.edited` trigger to auto-triage-issues.yml workflow
  - [x] 4.2 Add `issues.labeled` trigger for manual label changes
  - [x] 4.3 Add `issue_comment.created` trigger for substantive comments
  - [x] 4.4 Implement `was_recently_triaged()` function to detect if issue was triaged in last 5 minutes
  - [x] 4.5 Skip bot comments to prevent infinite loops
  - [x] 4.6 Implement `update_or_add_triage_comment()` to update existing triage comment instead of creating new
  - [x] 4.7 Add `--retriage` flag to triage_issue.py for re-triage mode

---

### Phase 5: Team Configuration Updates

**Status**: Complete  
**Progress**: 5/5 tasks complete (100%)  
**Phase Started**: 2026-02-05  
**Phase Completed**: 2026-02-05

- [x] 5.0 Update team member expertise and escalation config
  - **Relevant Documentation:**
    - `autoTriage/docs/design.md` - Section 6.1 Configuration Files
    - `autoTriage/config/team-members.json` - Current team roster
    - `autoTriage/services/config_parser.py` - Config loading logic
  - [x] 5.1 Add expertise arrays for Josh Oratz (joratz) - Backend Engineer
  - [x] 5.2 Add expertise arrays for Mengyi Xu (mengyimicro) - Backend Engineer
  - [x] 5.3 Add expertise arrays for Johan Broberg (pontemonti) - Tech Lead
  - [x] 5.4 Add `escalation_chain` config with lead (sellakumaran, pontemonti) and manager (tmlsousa)
  - [x] 5.5 Add `sla_hours` config with P0:24, P1:48, P2:72, P3:120, P4:120

---

### Phase 6: Security Issue Handling

**Status**: Complete  
**Progress**: 4/4 tasks complete (100%)  
**Phase Started**: 2026-02-05  
**Phase Completed**: 2026-02-05

- [x] 6.0 Implement security issue detection and routing
  - **Relevant Documentation:**
    - `autoTriage/docs/design.md` - US4: Security Issue Handling
    - `autoTriage/services/llm_service.py` - Classification logic
    - `autoTriage/services/intake_service.py` - Assignee selection
    - `autoTriage/config/team-members.json` - Security configuration
  - [x] 6.1 Add security keywords list to config (vulnerability, CVE, injection, XSS, auth bypass, etc.)
  - [x] 6.2 Implement `is_security_issue()` function in intake_service.py
  - [x] 6.3 Override assignee to Tech Lead when security issue detected
  - [x] 6.4 Auto-apply `security` label and P1 priority for security issues

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
  - [ ] 7.2 Implement `log_triage_decision()` function to append to triage log
  - [ ] 7.3 Implement `calculate_accuracy_metrics()` to compare predictions vs actual resolutions
  - [ ] 7.4 Create `scripts/generate_metrics_report.py` for monthly accuracy reports
  - [ ] 7.5 Add GitHub Action workflow summary with triage stats
  - [ ] 7.6 Track time-to-first-response metric in triage log

---

## Summary

| Phase | Tasks | Status |
|-------|-------|--------|
| Phase 1: Enable Auto-Apply | 5 | Complete |
| Phase 2: Copilot Auto-Fix | 8 | Not Started |
| Phase 3: SLA Escalation | 10 | Complete |
| Phase 4: Re-triage | 7 | Complete |
| Phase 5: Team Config | 5 | Complete |
| Phase 6: Security Handling | 4 | Complete |
| Phase 7: Observability | 6 | Not Started |
| **Total** | **45** | **69% Complete (31/45)** |

---

## Bonus: Daily Report System (Added)

**Status**: Complete  
**Progress**: 4/4 tasks complete (100%)

- [x] B.1 Create `services/daily_report_service.py` with DailyReportService class
- [x] B.2 Create `daily_report.py` CLI script
- [x] B.3 Create `.github/workflows/daily-issue-report.yml` (9 AM UTC daily)
- [x] B.4 Generate GitHub Actions Summary with SLA status

> **Note**: Teams webhook blocked by DLP policy. Using GitHub Actions Summary instead.

---

## Recommended Next Steps

1. **Phase 2** (Copilot Auto-Fix) - Research GitHub Copilot Coding Agent API
2. **Phase 7** (Observability) - Add metrics tracking after features stabilize
3. **Validation** - Monitor workflows after PR merge to verify functionality

---

> **Current State**: Core triage functionality is complete and deployed. Workflows run on issue events and daily schedules.
