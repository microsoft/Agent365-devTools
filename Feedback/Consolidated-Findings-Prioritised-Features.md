# Consolidated Findings - Prioritised Feature List

**Date:** 2026-02-20
**Sources Cross-Referenced:**
1. CMS-Evaluation-Failure-Analysis-V1.md
2. CMS-Security-Audit-Report-V1.md
3. V1-Architecture-and-Documentation-Feedback.md
4. V1-autoTriage-Code-Review-Feedback.md
5. V1-MCP-Server-Code-Review-Feedback.md
6. V1-Security-Findings-Feedback.md

**Total Unique Findings:** 80+ across 6 reports
**Consolidated Into:** 42 prioritised features (deduplicated)

---

## Priority Key

| Priority | Meaning | Timeline |
|----------|---------|----------|
| **P0 - EMERGENCY** | Active credential exposure or exploitable vulnerability | **Today** |
| **P1 - CRITICAL** | Security vulnerabilities or system-breaking issues | **Week 1** |
| **P2 - HIGH** | Performance, reliability, or significant quality issues | **Week 2-3** |
| **P3 - MEDIUM** | Technical debt, maintainability, documentation gaps | **Month 1-2** |
| **P4 - LOW** | Nice-to-have improvements and hardening | **Month 2-3** |

---

## Feature List

---

### Feature 1: Rotate All Exposed Credentials and Move to Secure Storage
**Priority:** P0 - EMERGENCY | **Effort:** 2-4 hours | **Component:** Workspace-wide

**Findings consolidated:**
- CMS-CRIT-001 / SEC-01: Azure AD client secret hardcoded in `upload_to_sharepoint.py`, `convert_and_upload.py`, `populate_metadata.py`
- CMS-CRIT-002: MCP server API key exposed in `.claude/settings.local.json`
- CMS-MED-009: Tenant ID + admin email exposed in config files

**What to do:**
1. **Immediately** rotate the Azure AD client secret in Azure Portal (App ID: `7efd0f37-8163-45d1-9ac2-edca18dbf932`)
2. **Immediately** rotate the MCP server API key on Azure Container Apps
3. Replace all hardcoded credentials with environment variable references (`os.environ.get()`)
4. If ever committed to git, purge history with BFG Repo Cleaner
5. Audit Azure AD sign-in logs for unauthorised usage
6. Add `.claude/settings.local.json` to `.gitignore`
7. Config files should use placeholders, not real values
8. Consider Azure Key Vault for production secrets

---

### Feature 2: Fix Shell Injection and Path Traversal Vulnerabilities
**Priority:** P0 - EMERGENCY | **Effort:** 2-3 hours | **Component:** Upload Scripts

**Findings consolidated:**
- CMS-CRIT-003: `os.system()` with unsanitised input in `upload_to_sharepoint.py` (lines 29-34)
- SEC-03: Path traversal via unsanitised filenames in SharePoint upload paths (line 303)

**What to do:**
1. Replace `os.system(f'"{sys.executable}" -m pip install {pkg}')` with `subprocess.run([sys.executable, "-m", "pip", "install", pkg], shell=False, check=True)`
2. Better yet: remove auto-install entirely, use `requirements.txt` with pinned versions
3. Apply `os.path.basename()` on all user-supplied filenames before constructing upload paths
4. Validate filenames against a whitelist of allowed characters

---

### Feature 3: Fix KQL Injection in MCP Server Search Queries
**Priority:** P1 - CRITICAL | **Effort:** 1-2 days | **Component:** MCP Server

**Findings consolidated:**
- CRIT-MCP-03 / SEC-04: User queries interpolated into KQL with only double-quote stripping
- Files: `sharepoint_search.py:381-407`, `document_metadata.py:596,619,1006-1017`

**What to do:**
1. Implement proper KQL escaping for all special characters: `* ? : ( ) [ ] { } \ / AND OR NOT NEAR`
2. Create a shared `kql_escape()` utility function
3. Apply sanitisation at every KQL query construction point
4. Add unit tests for malicious input patterns

---

### Feature 4: Fix LLM Prompt Injection in autoTriage
**Priority:** P1 - CRITICAL | **Effort:** 2-3 days | **Component:** autoTriage

**Findings consolidated:**
- CRIT-AT-01 / SEC-02: GitHub issue titles/bodies passed directly into LLM prompts without sanitisation
- Files: `services/llm_service.py:100-130`, `services/intake_service.py:676-681`

**What to do:**
1. Sanitise issue content before LLM submission (strip control characters, known injection patterns)
2. Wrap user content in XML delimiters: `<issue_title>...</issue_title>`, `<issue_body>...</issue_body>`
3. Validate LLM output against expected schemas before acting on it
4. Enforce body length limits consistently across all LLM calls

---

### Feature 5: Fix SharePoint Retrieval — `items_by_url` Exceeds 20-Item Platform Limit
**Priority:** P1 - CRITICAL | **Effort:** 2-4 hours | **Component:** CMS Agent Configuration

**Findings consolidated:**
- Eval Root Cause 2: Agent references 47 `items_by_url` entries vs Microsoft's 20-item limit
- 19 specific retrieval failures (CMS-EVAL-F01 through F19) where content exists but cannot be found
- 41% / 49% pass rate vs 70-80% SOW target

**What to do:**
1. Reduce `items_by_url` to the site-level URL or max 3-5 curated library URLs
2. Reference individual high-value files by URL (up to the 20-item limit)
3. Re-run evaluation to validate improvement
4. Expected impact: Could fix the majority of retrieval failures

---

### Feature 6: Sanitise All Error Responses — Stop Leaking Internal Details
**Priority:** P1 - CRITICAL | **Effort:** 1-2 days | **Component:** MCP Server + autoTriage

**Findings consolidated:**
- CRIT-MCP-07 / CMS-HIGH-009 / SEC-06: Python exceptions returned to external API callers
- CRIT-AT-02 / SEC-05: GitHub/OpenAI tokens potentially leaked in error logs
- HIGH-MCP-08 / SEC-09: Server file paths in API responses (`query_logger.py`)
- MED-AT-03: `_apply_triage_changes` exposes internals in error dict

**What to do:**
1. Return generic error messages to external callers: `"Internal server error. Contact support."`
2. Log full exception details server-side with correlation IDs
3. Create a sanitisation utility that strips tokens, keys, and Authorization headers from log messages
4. Remove `logFile` path from query logger API responses
5. Use structured logging with explicit fields, never `str(e)` for sensitive contexts

---

### Feature 7: Tighten CORS Configuration
**Priority:** P1 - CRITICAL | **Effort:** 3-4 hours | **Component:** MCP Server

**Findings consolidated:**
- CRIT-MCP-06 / SEC-07 / CMS-MED-007: Wildcard patterns with `allow_credentials=True`
- HIGH-MCP-05: CORS origins differ between Container Apps and Azure Functions deployments
- Regex `r"https://.*\.microsoft\.com"` can match unintended domains

**What to do:**
1. Restrict CORS to specific Copilot Studio origins (not wildcards)
2. Anchor the regex properly: `r"https://[^/]+\.microsoft\.com$"`
3. Remove `allow_credentials=True` unless explicitly required
4. Unify CORS configuration between Container Apps and Azure Functions deployments

---

### Feature 8: Reduce Azure AD Application Permission Scopes
**Priority:** P1 - CRITICAL | **Effort:** 1 day | **Component:** Azure AD

**Findings consolidated:**
- CMS-HIGH-008: App has `Sites.ReadWrite.All`, `Files.ReadWrite.All`, `Sites.Manage.All` — tenant-wide

**What to do:**
1. MCP server (read-only): reduce to `Sites.Read.All` + `Files.Read.All`
2. Upload scripts (write): use `Sites.Selected` with site-specific grants
3. Consider separate app registrations: one read-only for MCP, one write-capable for provisioning

---

### Feature 9: Remove API Key from Query String
**Priority:** P1 - CRITICAL | **Effort:** 2-3 hours | **Component:** MCP Server

**Findings consolidated:**
- SEC-08: API key accepted via `?api_key=` query parameter
- Keys appear in access logs, proxy logs, browser history

**What to do:**
1. Accept API key only via `X-API-Key` header
2. Remove the query string fallback entirely
3. Update all client configurations to use header-based authentication

---

### Feature 10: Fix httpx Client Connection Pooling
**Priority:** P2 - HIGH | **Effort:** 1 day | **Component:** MCP Server

**Findings consolidated:**
- CRIT-MCP-01: New `httpx.AsyncClient` created per API call (~8 instances across codebase)
- ~200-400ms overhead per call from TCP+TLS setup/teardown

**What to do:**
1. Create a single shared `httpx.AsyncClient` at server startup
2. Configure connection pool: `httpx.Limits(max_connections=20, max_keepalive_connections=10)`
3. Ensure proper cleanup on shutdown

---

### Feature 11: Fix `asyncio.run()` in Azure Functions Endpoints
**Priority:** P2 - HIGH | **Effort:** 1 day | **Component:** MCP Server (Azure Functions)

**Findings consolidated:**
- CRIT-MCP-02: `asyncio.run()` called per request (lines 209, 227, 305, 344, 377, 424, 454, 485)
- Creates/destroys event loop per request, can crash with `RuntimeError`

**What to do:**
1. Restructure to use async Azure Functions (`@app.route` with async handlers)
2. Or use `asyncio.get_event_loop().run_until_complete()` as interim fix

---

### Feature 12: Add Retry Logic for Graph API and LLM Calls
**Priority:** P2 - HIGH | **Effort:** 2-3 days | **Component:** MCP Server + autoTriage

**Findings consolidated:**
- HIGH-MCP-01: No retry on Graph API 429/503 responses
- HIGH-AT-05: LLM calls fail silently, no retry for transient errors

**What to do:**
1. Add exponential backoff with retry for 429/5xx responses
2. Use `tenacity` library for clean retry patterns
3. Respect `Retry-After` headers from Graph API
4. Implement for both MCP server Graph calls and autoTriage LLM/GitHub calls

---

### Feature 13: Refactor Agent Instructions to Improve Retrieval
**Priority:** P2 - HIGH | **Effort:** 2-4 hours | **Component:** CMS Agent Configuration

**Findings consolidated:**
- Eval Root Cause 3: "MAXIMUM ONE tool call per user message" prevents retries
- "If unsure, try Banking first" misdirects Corporate questions
- "Search once, answer immediately" prevents keyword reformulation

**What to do:**
1. Remove the "ONE tool call" restriction from agent instructions
2. Add retry instruction: "If 0 results, reformulate with different keywords and search again"
3. Remove Banking-first bias for ambiguous queries
4. Expected impact: Could fix 40-50% of remaining failures after Feature 5

---

### Feature 14: Fix Content and Vocabulary Mismatches
**Priority:** P2 - HIGH | **Effort:** 2-3 days | **Component:** SharePoint Content

**Findings consolidated:**
- Eval Root Cause 4: User query terms don't match document headings (OTP, Mercury, PG82, etc.)
- CMS-EVAL-D01: CORP-Q04 content mismatch ("Maxima Holdings" vs "Halt Garage")
- CMS-EVAL-D02: BNK-Q15 content gap (stock transfer forms missing)

**What to do:**
1. Add keyword-rich metadata and synonym aliases to SharePoint documents
2. Fix CORP-OP-004: Add "Maxima Holdings" as keyword alias
3. Add stock transfer forms content to BNK-EXEC-004
4. Create natural language synonym mappings for legal jargon

---

### Feature 15: Deduplicate MCP Server Code
**Priority:** P2 - HIGH | **Effort:** 2-3 days | **Component:** MCP Server

**Findings consolidated:**
- CRIT-MCP-04: Triplicated code across `sharepoint_search.py`, `document_metadata.py`, `query_logger.py`
  - 3 different `_STOP_WORDS` frozensets
  - 3 variants of keyword extraction
  - 3 separate `_get_auth_client()` singletons (3 separate token caches!)
  - 2 near-identical result formatters

**What to do:**
1. Extract shared utilities into `utils/text.py`, `utils/auth.py`, `utils/formatting.py`
2. Single auth client singleton with one token cache
3. One canonical stop words list and keyword extraction function
4. Verify behaviour equivalence with tests

---

### Feature 16: Add Automated Tests for MCP Server
**Priority:** P2 - HIGH | **Effort:** 1-2 weeks | **Component:** MCP Server

**Findings consolidated:**
- CRIT-MCP-05: Zero automated tests — only manual evaluation exists

**What to do:**
1. Set up pytest test framework
2. Priority test targets: KQL construction, result formatting, auth token handling, error paths
3. Target 80% code coverage
4. Add to CI pipeline

---

### Feature 17: Consolidate Agent Configurations (Single Source of Truth)
**Priority:** P2 - HIGH | **Effort:** 1 day | **Component:** Architecture

**Findings consolidated:**
- CRIT-ARCH-02: `agents/README.md` references non-existent/deleted agents
- CRIT-ARCH-03: Three separate locations for agent configs with no canonical source

**What to do:**
1. Designate `cms-knowledge-accelerator/agents/` as the single source of truth
2. Archive or delete top-level `CMS Knowledge Agents/` directory
3. Remove or clearly archive `deprecated/` agents
4. Update `agents/README.md` to reflect only active agents

---

### Feature 18: Fix Documentation Inconsistencies
**Priority:** P2 - HIGH | **Effort:** 1-2 days | **Component:** Documentation

**Findings consolidated:**
- CRIT-ARCH-01: Library counts differ across docs (48 vs 59 vs 62)
- HIGH-ARCH-05: Wiki and `docs/` may diverge with no sync mechanism

**What to do:**
1. Auto-generate library counts from actual contents (script on commit)
2. Audit all count references and align to actual numbers
3. Establish wiki as primary or docs/ as primary — one canonical source
4. Add a sync check or note which source is authoritative

---

### Feature 19: Refactor `convert_and_upload.py` to Use MSAL
**Priority:** P2 - HIGH | **Effort:** 3-4 hours | **Component:** Upload Scripts

**Findings consolidated:**
- CMS-HIGH-010: Raw HTTP POST to token endpoint, bypasses MSAL entirely
- Loses token caching, refresh, error handling, and Microsoft auth best practices

**What to do:**
1. Refactor to use `msal.ConfidentialClientApplication` (consistent with other scripts)
2. Implement token caching
3. Remove raw `requests.post()` to login.microsoftonline.com

---

### Feature 20: Optimize `list_library_contents` — Remove Full Table Scan
**Priority:** P2 - HIGH | **Effort:** 1 day | **Component:** MCP Server

**Findings consolidated:**
- HIGH-MCP-02: Fetches ALL library items via pagination then filters in Python
- For 10,000+ documents: 50+ paginated API calls per invocation

**What to do:**
1. Apply `$filter` OData queries server-side to reduce returned results
2. Push sorting/filtering to Graph API rather than Python
3. Add pagination controls to tool interface

---

### Feature 21: Add LLM Rate Limiting for autoTriage
**Priority:** P2 - HIGH | **Effort:** 1 day | **Component:** autoTriage

**Findings consolidated:**
- HIGH-AT-01: 5 LLM calls per issue, no throttling — 50 issues = 250 unthrottled calls

**What to do:**
1. Add configurable rate limiting (max N calls per minute)
2. Consider batching classification calls where possible
3. Add async processing for parallelism with controlled concurrency

---

### Feature 22: Fix Caching Patterns in autoTriage
**Priority:** P2 - HIGH | **Effort:** 1 day | **Component:** autoTriage

**Findings consolidated:**
- HIGH-AT-02: Module-level cache grows unbounded with only lazy eviction
- HIGH-AT-03: `@lru_cache` on instance methods — `self` as cache key causes misses or stale data
- Dual-caching pattern where both `@lru_cache` and manual `_get_cached` are used on the same method

**What to do:**
1. Add periodic cache cleanup or max size with LRU eviction
2. Use the existing `_get_cached`/`_set_cached` pattern consistently
3. Remove dual-caching — pick one approach per method

---

### Feature 23: Enforce Consistent Input Truncation
**Priority:** P2 - HIGH | **Effort:** 3-4 hours | **Component:** autoTriage

**Findings consolidated:**
- HIGH-AT-04: Issue body truncated at `[:2000]` in some LLM calls but not `classify_issue` or `is_security_issue`
- Risk: Extremely long bodies consume excessive tokens or hit limits

**What to do:**
1. Define `MAX_ISSUE_BODY_LENGTH` constant
2. Apply uniformly across all LLM entry points
3. Log when truncation occurs

---

### Feature 24: Remove Tables from SharePoint Documents
**Priority:** P2 - HIGH | **Effort:** 1-2 days | **Component:** SharePoint Content

**Findings consolidated:**
- Eval Root Cause 1 (partial): "Copilot is currently unable to parse tables" — tables break retrieval

**What to do:**
1. Convert table-formatted content in SharePoint docs to prose or bullet lists
2. Re-index affected documents
3. Re-run evaluation to measure improvement

---

### Feature 25: Restructure Large Multi-Topic Documents
**Priority:** P2 - HIGH | **Effort:** 1-2 days | **Component:** SharePoint Content

**Findings consolidated:**
- Eval medium-term fix: Large docs like BNK-EXEC-011 (10+ topics) misalign with chunking

**What to do:**
1. Split large documents into one topic per document
2. Add cross-references between related docs
3. Update any agent references to new document structure

---

### Feature 26: Fix Health Check to Verify Graph Connectivity
**Priority:** P3 - MEDIUM | **Effort:** 3-4 hours | **Component:** MCP Server

**Findings consolidated:**
- HIGH-MCP-04: Health endpoint returns static `{"status":"ok"}` without testing Graph API

**What to do:**
1. Add a lightweight Graph API call (e.g., token acquisition test) to the health check
2. Return degraded status if Graph is unreachable

---

### Feature 27: Remove Private Starlette API Usage
**Priority:** P3 - MEDIUM | **Effort:** 3-4 hours | **Component:** MCP Server

**Findings consolidated:**
- HIGH-MCP-03: `guarded_send` uses `request._send` (private attribute, will break on Starlette updates)

**What to do:**
1. Replace with public Starlette API equivalent
2. Add Starlette version pinning as an interim safeguard

---

### Feature 28: Replace Manual JSON-RPC Dispatch with MCP SDK Routing
**Priority:** P3 - MEDIUM | **Effort:** 1 day | **Component:** MCP Server (Azure Functions)

**Findings consolidated:**
- HIGH-MCP-07: String-matching dispatch (`if method == "tools/list"`) bypasses MCP SDK
- HIGH-MCP-06: Hardcoded protocol version `"2024-11-05"`

**What to do:**
1. Use MCP SDK's built-in routing instead of manual string matching
2. Read protocol version from the SDK rather than hardcoding

---

### Feature 29: Add MCP Server Deployment Documentation
**Priority:** P3 - MEDIUM | **Effort:** 1-2 days | **Component:** Documentation

**Findings consolidated:**
- HIGH-ARCH-01: Deployment docs missing MCP-specific sections

**What to do:**
1. Document environment variable configuration for production
2. Document container image build and push process
3. Document Azure Container Apps scaling configuration
4. Document secrets management (Key Vault integration)

---

### Feature 30: Standardise PowerShell Scripts
**Priority:** P3 - MEDIUM | **Effort:** 2-3 days | **Component:** Provisioning Scripts

**Findings consolidated:**
- HIGH-ARCH-03: Inconsistent error handling — some use `$ErrorActionPreference = 'Stop'`, others don't
- Not all scripts validate prerequisites or support idempotent re-runs

**What to do:**
1. Add `$ErrorActionPreference = 'Stop'` to all scripts
2. Add prerequisite validation at the start of each script
3. Ensure idempotency (safe to re-run)

---

### Feature 31: Document Required GitHub Secrets
**Priority:** P3 - MEDIUM | **Effort:** 3-4 hours | **Component:** CI/CD

**Findings consolidated:**
- HIGH-ARCH-04: Workflow files reference `${{ secrets.* }}` but no doc says which secrets to configure
- No GitHub secret scanning enabled

**What to do:**
1. Create a `SECRETS.md` or section in deployment guide listing all required secrets
2. Enable GitHub Advanced Security secret scanning
3. Document secret rotation policy

---

### Feature 32: Populate Source Library or Fix Cross-References
**Priority:** P3 - MEDIUM | **Effort:** 3-5 days | **Component:** Knowledge Libraries

**Findings consolidated:**
- CRIT-ARCH-04: Source library has correct template structure but zero entries
- HIGH-ARCH-02: Problem library entries reference non-existent source library files

**What to do:**
1. Either populate source library entries for all referenced sources
2. Or remove cross-references from problem library entries until population is complete
3. Add CI check to validate cross-references

---

### Feature 33: Create FAQ/Index Document for Improved Retrieval
**Priority:** P3 - MEDIUM | **Effort:** 1 day | **Component:** SharePoint Content

**Findings consolidated:**
- Eval medium-term fix #10: FAQ mapping questions to document references

**What to do:**
1. Create a master FAQ document mapping common questions to specific doc references
2. Include synonym/alias listings for common legal terms
3. Upload to SharePoint as a retrieval amplifier

---

### Feature 34: Add Structured Logging and Observability
**Priority:** P3 - MEDIUM | **Effort:** 2-3 days | **Component:** MCP Server

**Findings consolidated:**
- MED-MCP-02: No request correlation IDs
- MED-MCP-03: No structured JSON logging for Azure Monitor
- MED-MCP-06: No OpenTelemetry tracing
- MED-MCP-08: No API key audit logging

**What to do:**
1. Add correlation ID to every request
2. Switch to structured JSON logging
3. Add OpenTelemetry tracing for key operations
4. Log API key usage (without logging the key itself)

---

### Feature 35: Pin All Dependency Versions
**Priority:** P3 - MEDIUM | **Effort:** 2-3 hours | **Component:** MCP Server + autoTriage

**Findings consolidated:**
- MED-MCP-04: Dependencies not pinned with `==`
- CMS-CRIT-003 (related): Unpinned auto-installed packages = supply chain risk

**What to do:**
1. Pin all versions in `requirements.txt` with `==`
2. Add hash verification where possible
3. Set up Dependabot or Renovate for automated updates

---

### Feature 36: Add Input Length Validation
**Priority:** P3 - MEDIUM | **Effort:** 1 day | **Component:** MCP Server

**Findings consolidated:**
- SEC-10 / MED-MCP-07: No max length on search queries, document titles, topic strings

**What to do:**
1. Define maximum lengths for all user-supplied inputs
2. Validate and truncate at tool entry points
3. Return clear error messages for oversized inputs

---

### Feature 37: autoTriage Code Quality Improvements
**Priority:** P3 - MEDIUM | **Effort:** 2-3 days | **Component:** autoTriage

**Findings consolidated:**
- MED-AT-01: Duplicate `MAX_CONTRIBUTORS_TO_SHOW` constant in two files
- MED-AT-02: `triage_issues` function is 360 lines long
- MED-AT-04: `_parse_issue_url` doesn't handle GitHub Enterprise URLs
- MED-AT-05: Missing type hints on some parameters
- HIGH-AT-06: Emojis in output violating CLAUDE.md rules

**What to do:**
1. Extract shared constants to a single module
2. Break `triage_issues` into smaller functions (`_fetch_issues_to_triage()`, `_classify_single_issue()`)
3. Add GitHub Enterprise URL support
4. Add missing type hints
5. Replace emojis with text indicators: `[OK]`, `[WARNING]`, `[FAILED]`

---

### Feature 38: Clean Up Orphaned Files and Config
**Priority:** P3 - MEDIUM | **Effort:** 3-4 hours | **Component:** Workspace

**Findings consolidated:**
- MED-ARCH-04: `upload_to_sharepoint.py` at project root is orphaned, unclear relationship to `convert_and_upload.py`
- Overlapping functionality between the two scripts

**What to do:**
1. Determine which script is canonical
2. Consolidate into a single upload utility or clearly document each script's purpose
3. Remove or archive the redundant script

---

### Feature 39: Problem Library and Schema Cleanup
**Priority:** P4 - LOW | **Effort:** 2-3 days | **Component:** Knowledge Libraries

**Findings consolidated:**
- MED-ARCH-01: Empty problem library categories (`copilot-studio/`, `devops-pipelines/`, `general/`, `terraform/`)
- MED-ARCH-02: `map-search-properties.ps1` not prominently documented despite being critical
- MED-ARCH-03: No schema validation for problem library YAML frontmatter

**What to do:**
1. Mark empty categories as "planned" or remove them
2. Add `map-search-properties.ps1` to the main deployment guide
3. Add YAML schema validation for problem library entries (CI check)

---

### Feature 40: Consider Pydantic Models for Tool Results
**Priority:** P4 - LOW | **Effort:** 2-3 days | **Component:** MCP Server

**Findings consolidated:**
- MED-MCP-05: Tool results are raw dicts without schema validation

**What to do:**
1. Define Pydantic models for each tool's input and output
2. Auto-validate at tool boundaries
3. Enables auto-generated documentation

---

### Feature 41: Improve Evaluation Methodology
**Priority:** P4 - LOW | **Effort:** 1-2 weeks | **Component:** Testing

**Findings consolidated:**
- Eval methodology critique: Binary pass/fail too simplistic
- No distinction between retrieval failure vs reasoning failure
- High run variance (~8%), no multi-turn evaluation

**What to do:**
1. Add "Retrieval Success" metric separate from "Answer Quality"
2. Track specific SharePoint URL returned vs expected
3. Run each question 3-5 times and report consistency percentage
4. Add multi-turn test scenarios
5. Score partial credit for correct topic identification

---

### Feature 42: Azure AI Search with Hybrid Retrieval (Phase 2)
**Priority:** P4 - LOW (strategic) | **Effort:** 2-4 weeks | **Component:** Architecture

**Findings consolidated:**
- Eval architectural fix: Step-change in precision with hybrid (keyword + vector) retrieval
- Current SharePoint search ceiling: ~75-85% with all fixes
- Azure AI Search ceiling: 85-95%

**What to do:**
1. Evaluate Azure AI Search as replacement for native SharePoint search
2. Implement hybrid retrieval (BM25 + vector embeddings)
3. Custom chunking aligned to document topics
4. Consider Copilot Connectors for custom indexing

---

## Summary by Priority

| Priority | Features | Count |
|----------|----------|-------|
| **P0 - EMERGENCY** | Features 1-2 | 2 |
| **P1 - CRITICAL** | Features 3-9 | 7 |
| **P2 - HIGH** | Features 10-25 | 16 |
| **P3 - MEDIUM** | Features 26-38 | 13 |
| **P4 - LOW** | Features 39-42 | 4 |
| **TOTAL** | | **42** |

## Estimated Impact After Remediation

| Milestone | Features Completed | Expected Agent Accuracy | Security Posture |
|-----------|--------------------|------------------------|------------------|
| After P0 (today) | 1-2 | No change (infra) | Credentials secured |
| After P1 (week 1) | 1-9 | ~65-75% (from 49%) | All critical vulns fixed |
| After P2 (week 2-3) | 1-25 | ~75-85% | Scalable and reliable |
| After P3 (month 1-2) | 1-38 | ~80-85% | Production-ready |
| After P4 (month 2-3) | 1-42 | ~85-95% (with AI Search) | Hardened |

---

*Generated: 2026-02-20*
*Source: Cross-reference of 6 V1 feedback reports*
