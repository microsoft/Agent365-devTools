# CMS Knowledge Accelerator - Architecture Review (Devil's Advocate) V1

**Report ID:** CMS-ARCH-V1
**Date:** 2026-02-20
**Reviewer:** CMS Watchdog Team - Architecture Reviewer Agent
**Scope:** Full architecture review across all project components
**Classification:** INTERNAL

---

## Executive Summary

The CMS Knowledge Accelerator is genuinely impressive work for a fixed-price POC. The MCP server architecture, config-driven tool system, KQL optimization, and 81-question evaluation framework exceed typical POC quality. However, documentation is inconsistent, CI/CD has material gaps, there are no Python-level tests, and the security model uses broader permissions than documented.

---

## 1. Overall Design Decisions

### What's Good

- **Dual-channel architecture** (SharePoint grounding + MCP server) is belt-and-suspenders, not redundant
- **Config-driven tool registration** (tools.json). Adding a new tool requires zero Python changes
- **KQL keyword extraction** (stop-word stripping, AND-to-OR fallback). A non-obvious fix most POCs miss
- **Document currency/staleness warnings** integrated at the tool level. Critical for legal knowledge

### What's Questionable

- **62 libraries in one agent's `items_by_url`** -- exceeds documented 20-item limit
- **Application permissions instead of OBO** -- ARCHITECTURE.md describes OBO but implementation uses client credentials with tenant-wide access
- **ARCHITECTURE.md is stale** -- still describes two agents with OBO auth

---

## 2. MCP Server Architecture

### Chosen Approach
Python MCP server on Azure Container Apps, Streamable HTTP, 12 tools, client credentials outbound, API key inbound.

### Why It's Defensible

| Aspect | Assessment |
|--------|-----------|
| MCP standard | Microsoft's strategic direction (GA in Copilot Studio) |
| Streamable HTTP | Correct -- SSE deprecated August 2025 |
| Stateless sessions | Matches Copilot Studio invocation pattern |
| Container Apps scale-to-zero | Cost-appropriate (~GBP 4-9/month) |

### Devil's Advocate Challenges

| Challenge | Detail |
|-----------|--------|
| Why both server.py AND function_app.py? | Two complete parallel implementations. Which is deployed? Maintenance burden. |
| Why not Power Automate? | Stays within M365 boundary. No external container, no API key, no CORS. |
| Single point of failure | MCP server down = all 12 tools gone. No circuit breaker or degraded-mode fallback. |
| Cold start | Scale-to-zero: 5-15 second first request after idle. No mitigation deployed. |

---

## 3. Deployment Pipeline Gaps

| Component | Status | Issue |
|-----------|--------|-------|
| validate.yml | EXISTS | Checks JSON/PowerShell only. No Python testing. |
| deploy-sharepoint.yml | EXISTS | Has environment input but doesn't use it. No approval gates. |
| deploy-agents.yml | BROKEN | Matrix deploys banking-agent/corporate-agent but actual agent is cms-knowledge-agent. |
| deploy-mcp-server.yml | **MISSING** | README claims it exists. It does not. Manual deployment only. |
| Integration tests | MISSING | No MCP server startup verification in CI. |
| Rollback automation | MISSING | Manual procedures only. |

---

## 4. Agent Design: Unified vs Split

**Verdict:** Unified was the right call for 2 practice areas.

**Concerns for scaling:**
- System prompt is 4,000+ words, pushing Copilot Studio token limits
- Adding practice areas requires rewriting the entire prompt
- Internal Banking/Corporate routing relies on LLM intent parsing
- Will not scale to 5+ practice areas without multi-agent orchestration

---

## 5. Documentation Contradictions

| Document | Claims | Reality |
|----------|--------|---------|
| ARCHITECTURE.md | Two agents (Banking + Corporate) | One unified agent |
| ARCHITECTURE.md | OBO authentication | Client credentials |
| CLIENT-HANDOVER.md | Two separate agents | One unified agent |
| deploy-agents.yml | Matrix: banking-agent, corporate-agent | Actual: cms-knowledge-agent |
| README.md | deploy-mcp-server.yml exists | File does not exist |

---

## 6. Testing Strategy

### What Exists
- 81-question test suite (41 client + 40 additional)
- 700-line EVALUATION-GUIDE.md with three-point scoring
- Copilot Studio evaluation CSV format

### What's Missing

| Gap | Impact |
|-----|--------|
| No Python unit tests | Zero coverage for MCP server. Typo in tools.json breaks 12 tools undetected. |
| No integration tests | No verification MCP server starts and responds to JSON-RPC. |
| No load testing | Unknown concurrent behaviour. httpx client per request = no pooling. |
| No regression testing | Prompt changes could degrade one category while improving another. |
| Tests evaluate agent, not server | Broken tool = FAIL score but never identifies root cause. |

---

## 7. Scalability at 10x Documents

| Component | Issue at Scale |
|-----------|---------------|
| Graph Search API | `size: 20` max. Documents ranked 21+ invisible. No pagination. |
| list_library_contents | Fetches ALL items. 5,000 items = 25 API calls, each creating new httpx client. |
| generate_briefing_note | Limited to top 20 search results. |
| Query logger | Local JSON file. Multiple replicas = split logs. Lost on restart. |
| items_by_url | Already 47/20 limit. 150 libraries at 10x is unworkable. |

---

## 8. Maintenance Burden After Handover

| Requirement | Expertise Needed | Risk if Missing |
|-------------|-----------------|-----------------|
| MCP server patches | Python developer | Security vulnerabilities accumulate |
| System prompt tuning | AI engineering | Changing one rule cascades into degraded behaviour |
| Secret rotation | Azure expertise | Silent auth failure, degraded agent |
| Monitoring | DevOps | Nobody knows when MCP server is down |

**Biggest risk:** Secret expiry with no alerting. MCP tools fail silently, OneDriveAndSharePoint still works, agent appears "mostly fine" but all value-add features vanish.

---

## 9. Production Failure Scenarios

| Scenario | Impact | Mitigation Status |
|----------|--------|-------------------|
| Client secret expires | All MCP tools fail silently | Not deployed |
| SharePoint index lag (up to 24h) | Agent cites old/archived documents | Currency tool partially mitigates |
| Large .docx OOM | Container memory exhaustion | MAX_DOWNLOAD_BYTES exists but may be insufficient |
| CORS misconfiguration | *.microsoft.com matches unintended domains | API key second layer |
| asyncio.run() conflict | Azure Functions crash | Latent bug |
| practice_areas_found bug | Briefing notes always generic | Code bug, unnoticed |

---

## 10. What's Missing

| Gap | Effort to Fix |
|-----|---------------|
| Rate limiting on MCP endpoint | 1 day |
| Request/response logging with redaction | 2-3 days |
| Deep health check (verify Graph API + SharePoint reachability) | 1 day |
| Retry logic with exponential backoff | 1-2 days |
| PDF text extraction | 2-3 days |
| HTTP connection pooling (shared httpx.AsyncClient) | 1 day |
| Distributed tracing (OpenTelemetry) | 3-5 days |
| deploy-mcp-server.yml workflow | 1-2 days |

---

## Verdict

Strong POC work demonstrating genuine engineering quality. The MCP architecture, tool design, and evaluation framework are above average. The gaps between documentation and reality, the missing CI/CD pipeline, the absence of Python tests, and security model concerns need addressing before production. The biggest strategic risk is maintenance handover.

---

*Report Version: V1*
*Generated by CMS Watchdog Team - Architecture Reviewer Agent*
*Date: 2026-02-20*
