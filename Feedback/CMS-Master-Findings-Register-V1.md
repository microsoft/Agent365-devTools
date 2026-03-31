# CMS Knowledge Accelerator - Master Findings Register V1

**Report ID:** CMS-MASTER-V1
**Date:** 2026-02-20
**Team:** CMS Watchdog Team (6 specialist agents)
**Scope:** Full workspace audit -- security, code quality, architecture, retrieval, SharePoint, alternatives
**Classification:** INTERNAL

---

## Team Composition

| Agent | Role | Report |
|-------|------|--------|
| security-auditor | Security & credential audit | CMS-Security-Audit-Report-V1.md |
| eval-analyst | Evaluation failure analysis | CMS-Evaluation-Failure-Analysis-V1.md |
| architect-reviewer | Architecture devil's advocate | CMS-Architecture-Review-V1.md |
| tech-researcher | Alternative approaches research | CMS-Alternative-Approaches-Research-V1.md |
| code-reviewer | Python code quality review | CMS-Python-Code-Quality-Review-V1.md |
| sharepoint-specialist | SharePoint schema & strategy | CMS-SharePoint-Schema-Review-V1.md |

---

## Finding Totals

| Severity | Count |
|----------|-------|
| CRITICAL | 3 |
| HIGH | 10 |
| MEDIUM | 12 |
| LOW | 8 |
| **TOTAL** | **33** |

---

## ALL FINDINGS (Named & Numbered)

### CRITICAL (3) -- Fix Today

| ID | Name | Found By | File(s) | Detail |
|----|------|----------|---------|--------|
| CMS-CRIT-001 | Exposed Client Secret in Source Code | security-auditor, code-reviewer | upload_to_sharepoint.py:44, convert_and_upload.py:20, populate_metadata.py:45 | Azure AD client secret in plaintext across 3 files. Grants tenant-wide SharePoint access (Sites.ReadWrite.All, Files.ReadWrite.All, Sites.Manage.All). CVSS 9.8. |
| CMS-CRIT-002 | Exposed MCP Server API Key | security-auditor | .claude/settings.local.json:88 | MCP server auth key in settings file. Allows direct endpoint access bypassing Copilot Studio. CVSS 9.5. |
| CMS-CRIT-003 | Shell Injection via os.system() | security-auditor, code-reviewer | upload_to_sharepoint.py:29-34 | os.system() for pip install. Shell injection risk, no version pinning, no hash verification. CVSS 9.2. |

### HIGH (10) -- Fix Before Deployment

| ID | Name | Found By | Detail |
|----|------|----------|--------|
| CMS-HIGH-001 | Agent Exceeds 20 items_by_url Limit | sharepoint-specialist, eval-analyst | 47 URLs listed, Microsoft limits to 20. Libraries beyond ~20 silently ignored. Root cause of retrieval failures. |
| CMS-HIGH-002 | One Tool Call Restriction Kills Retrieval | eval-analyst | "MAXIMUM ONE tool call per message" prevents retry. Responsible for ~40-50% of failures. |
| CMS-HIGH-003 | Zero Retry Logic Across All Graph API Calls | code-reviewer | No 429/503 handling anywhere. Microsoft requires apps handle throttling. |
| CMS-HIGH-004 | asyncio.run() Crash in Azure Functions | architect-reviewer, code-reviewer | Creates new event loop in existing loop. RuntimeError in newer runtimes. |
| CMS-HIGH-005 | Missing deploy-mcp-server.yml Pipeline | architect-reviewer | README claims it exists. It does not. MCP deployment is manual. |
| CMS-HIGH-006 | httpx.AsyncClient Created Per Request | code-reviewer | Every Graph call creates/destroys HTTP client. Wastes connections. |
| CMS-HIGH-007 | Documentation Contradicts Deployed Reality | architect-reviewer | ARCHITECTURE.md, CLIENT-HANDOVER.md, deploy-agents.yml all reference old 2-agent design. |
| CMS-HIGH-008 | Overly Broad Application Permissions | security-auditor, architect-reviewer | Sites.ReadWrite.All is tenant-wide. MCP server can read ALL SharePoint sites. |
| CMS-HIGH-009 | Error Messages Leak Internal Details | code-reviewer, security-auditor | Exception stack traces returned to API callers. |
| CMS-HIGH-010 | convert_and_upload.py Bypasses MSAL | code-reviewer | Raw HTTP POST to token endpoint. Loses caching, refresh, error handling. |

### MEDIUM (12) -- Fix in Next Sprint

| ID | Name | Found By | Detail |
|----|------|----------|--------|
| CMS-MED-001 | POC Metadata Ignores Production Taxonomy | sharepoint-specialist | CMS already has 8 managed metadata term sets. POC created parallel Choice/Text columns. |
| CMS-MED-002 | DocumentSummary Not Mapped as Managed Property | sharepoint-specialist | Field exists but Graph Search can't use it. Highest-impact search config fix. |
| CMS-MED-003 | No Python Unit Tests for MCP Server | architect-reviewer | Zero test coverage. Typo in tools.json breaks 12 tools undetected. |
| CMS-MED-004 | 62 Libraries Should Consolidate to 3-6 | sharepoint-specialist | Redundancies (Crypto/Cryptocurrency, Bank Guarantees/Guarantees). Scale issue. |
| CMS-MED-005 | Non-Deterministic Search Results | eval-analyst | ~8% variance between identical evaluation runs. Semantic search instability. |
| CMS-MED-006 | Duplicated Code Across Modules | code-reviewer | Stop words (3 copies), formatters (3 copies), auth pattern (3 copies). |
| CMS-MED-007 | CORS Pattern May Match Unintended Domains | code-reviewer, security-auditor | Regex not anchored. Could match *.microsoft.com.attacker.com. |
| CMS-MED-008 | Query Logger Race Condition | code-reviewer | Concurrent log rotation could fail with FileNotFoundError. |
| CMS-MED-009 | Tenant ID and Admin Email in Config | security-auditor | Exposed in tenant-config.json and sharepoint.json. |
| CMS-MED-010 | MSAL Token Cache Not Thread-Safe | code-reviewer | Azure Functions concurrent threads risk cache corruption. |
| CMS-MED-011 | No Monitoring or Alerting Deployed | architect-reviewer | No dashboards, no alerts. Nobody knows when MCP server is down. |
| CMS-MED-012 | Cold Start Latency on Container Apps | architect-reviewer | 5-15 second delay after idle. No mitigation deployed. |

### LOW (8) -- Address When Convenient

| ID | Name | Found By | Detail |
|----|------|----------|--------|
| CMS-LOW-001 | CORP-Q04 Content Mismatch | eval-analyst | Test: "Maxima Holdings", Doc: "Re Halt Garage (1964) Ltd". |
| CMS-LOW-002 | practice_areas_found Bug | architect-reviewer | Set never populated. Briefing notes always say "across the knowledge base". |
| CMS-LOW-003 | Conflict Detection Sentiment Bug | code-reviewer | "not permitted" matches BOTH permissive and restrictive signals. |
| CMS-LOW-004 | find_item_in_drive Swallows Auth Failures | code-reviewer | Bare except: pass. Expired tokens = "file not found". |
| CMS-LOW-005 | No PDF Text Extraction | architect-reviewer | get_document_content explicitly unsupported. Law firms use PDFs heavily. |
| CMS-LOW-006 | Hardcoded Filesystem Paths | code-reviewer | Absolute paths to Ross.Hastie's OneDrive. Single-machine only. |
| CMS-LOW-007 | API Key via Query String | architect-reviewer | Keys appear in logs, browser history, proxy logs. |
| CMS-LOW-008 | Deprecated asyncio.get_event_loop() | code-reviewer | Deprecated Python 3.10+. Use get_running_loop(). |

---

## Remediation Timeline

### TODAY (Immediate)

- [ ] CMS-CRIT-001: Rotate client secret in Azure Portal
- [ ] CMS-CRIT-002: Rotate MCP server API key
- [ ] CMS-HIGH-001: Reduce items_by_url to site URL (30 min)
- [ ] CMS-HIGH-002: Remove "ONE tool call" restriction (30 min)

### WITHIN 48 HOURS

- [ ] CMS-CRIT-003: Replace os.system() with subprocess/requirements.txt
- [ ] CMS-MED-002: Map CMS_DocumentSummary as searchable managed property + re-crawl

### WITHIN 1 WEEK

- [ ] CMS-HIGH-003: Implement Graph API retry logic
- [ ] CMS-HIGH-006: Create shared httpx.AsyncClient
- [ ] CMS-HIGH-008: Reduce permission scopes to least privilege
- [ ] CMS-HIGH-009: Sanitize error responses
- [ ] CMS-HIGH-010: Refactor convert_and_upload.py to use MSAL

### WITHIN 2 WEEKS

- [ ] CMS-HIGH-004: Fix asyncio.run() in function_app.py
- [ ] CMS-HIGH-005: Create deploy-mcp-server.yml pipeline
- [ ] CMS-HIGH-007: Update ARCHITECTURE.md, CLIENT-HANDOVER.md, deploy-agents.yml
- [ ] CMS-MED-006: Consolidate duplicated code
- [ ] CMS-MED-011: Deploy basic monitoring/alerting

### PHASE 2 (1-3 Months)

- [ ] CMS-MED-001: Align metadata with production taxonomy
- [ ] CMS-MED-003: Add Python unit tests for MCP server
- [ ] CMS-MED-004: Consolidate libraries from 62 to 3-6
- [ ] Azure AI Search integration (hybrid retrieval)
- [ ] Copilot Tuning for legal terminology
- [ ] SharePoint Premium for auto-metadata

---

## Realistic Accuracy Ceiling

| Approach | Estimated Ceiling |
|----------|------------------|
| Current (no changes) | 49% |
| After immediate fixes (items_by_url + retry instructions) | 65-75% |
| After medium-term fixes (metadata + restructuring) | 75-85% |
| After Azure AI Search (Phase 2) | 85-95% |

---

*Report Version: V1*
*Generated by CMS Watchdog Team*
*Date: 2026-02-20*
