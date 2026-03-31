# CMS Knowledge Accelerator MCP Server - Code Review Feedback Report V1

**Version:** V1
**Date:** 2026-02-20
**Reviewed by:** Claude Opus 4.6 (multi-agent review team)
**Component:** cms-knowledge-accelerator/mcp-server
**Scope:** server.py, function_app.py, auth/graph_auth.py, tools/*.py, configuration files

---

## 1. Overall Assessment

The MCP server is a well-architected solution connecting Microsoft Copilot Studio agents to SharePoint-hosted legal knowledge via the Model Context Protocol. The Streamable HTTP implementation is correct for Copilot Studio's stateless request model, and the tool handler architecture using declarative `tools.json` is clean and extensible.

**Rating: Functional with significant technical debt - critical items must be addressed before production scaling**

---

## 2. Critical Findings

### CRIT-MCP-01: New httpx.AsyncClient Per API Call
**Severity:** CRITICAL | **Files:** `sharepoint_search.py:464`, `document_metadata.py:196`, `document_content.py:269`

Every Graph API call creates, uses, and destroys an `httpx.AsyncClient`:
```python
async with httpx.AsyncClient(timeout=timeout) as client:
    response = await client.post(...)
```

This pattern appears ~8 times across the codebase. Each invocation requires a fresh TCP connection, TLS handshake, and connection pool setup/teardown.

**Impact:** ~200-400ms overhead per call. At 20 concurrent users, this creates massive connection churn.

**Recommendation:** Create a single `httpx.AsyncClient` at server startup with a configured connection pool:
```python
_http_client = httpx.AsyncClient(
    timeout=30.0,
    limits=httpx.Limits(max_connections=20, max_keepalive_connections=10)
)
```

---

### CRIT-MCP-02: `asyncio.run()` in Azure Functions Endpoints
**Severity:** CRITICAL | **File:** `function_app.py` (lines 209, 227, 305, 344, 377, 424, 454, 485)

Every Azure Functions endpoint calls `asyncio.run()` which creates and destroys a new event loop per request. This can crash with `RuntimeError: This event loop is already running` in certain Azure Functions runtime versions and prevents connection reuse across requests.

**Recommendation:** Use `asyncio.get_event_loop().run_until_complete()` or restructure to use async Azure Functions.

---

### CRIT-MCP-03: KQL Injection Risk
**Severity:** CRITICAL | **Files:** `sharepoint_search.py:381-407`, `document_metadata.py:596,619,1006-1017`

User-supplied queries are interpolated into KQL with minimal sanitisation (only double-quote stripping). KQL operators (`AND`, `OR`, `NOT`, `NEAR`, `*`, `?`) pass through, potentially altering search scope.

**Recommendation:** Implement proper KQL escaping for all special characters.

---

### CRIT-MCP-04: Triplicated Code Across Tool Modules
**Severity:** CRITICAL | **Files:** `sharepoint_search.py`, `document_metadata.py`, `query_logger.py`

The following are duplicated with subtle differences:
- `_STOP_WORDS` frozenset (3 different versions with different contents)
- `_extract_search_keywords()` / `_extract_keywords()` (3 variants with different signatures)
- `_get_auth_client()` singleton pattern (3 instances, 3 separate token caches)
- `_format_hit()` / `_format_search_result()` (2 near-identical result formatters)

**Recommendation:** Extract shared utilities into `utils/text.py`, `utils/auth.py`, and `utils/formatting.py`.

---

### CRIT-MCP-05: Zero Automated Test Coverage
**Severity:** CRITICAL | **File:** `tests/`

The tests directory contains only `test-questions.json` (manual evaluation) and `EVALUATION-GUIDE.md`. No unit tests, integration tests, or automated test scripts exist.

**Recommendation:** Implement pytest-based test suite targeting 80% coverage. Priority: KQL construction, result formatting, auth token handling, error paths.

---

### CRIT-MCP-06: CORS Wildcards with Credentials
**Severity:** CRITICAL | **File:** `server.py:733-765`

`allow_credentials=True` combined with wildcard patterns (`*.microsoft.com`) creates an overly permissive CORS posture.

**Recommendation:** Restrict to specific Copilot Studio origins. Remove `allow_credentials=True` unless required.

---

### CRIT-MCP-07: Error Messages Leak Internal Details
**Severity:** CRITICAL | **Files:** `server.py:468`, `function_app.py:261`

Full Python exception strings (including file paths, module names, stack fragments) are returned to external callers.

**Recommendation:** Return generic errors to callers, log full details internally.

---

## 3. High Priority Improvements

### HIGH-MCP-01: No Retry Logic for Graph API Calls
Graph API calls (429 throttling, 503 service unavailable) fail immediately with no retry. This is the #1 scaling concern.

**Recommendation:** Exponential backoff with retry for 429/5xx responses using `tenacity` or httpx transport retry.

### HIGH-MCP-02: `list_library_contents` Full Table Scan
**File:** `document_metadata.py:843-913`

Fetches ALL library items via pagination then filters in Python. For 10,000+ documents, this means 50+ paginated API calls per invocation.

**Recommendation:** Apply `$filter` OData queries server-side.

### HIGH-MCP-03: `guarded_send` Uses Private Starlette API
**File:** `server.py:721`

`request._send` is a private attribute that will break on Starlette updates.

### HIGH-MCP-04: Health Check Does Not Verify Graph Connectivity
**File:** `server.py:747-753`

Returns static `{"status":"ok"}` without testing Graph API access. Healthy containers with broken credentials will accept but fail all requests.

### HIGH-MCP-05: Dual CORS Configuration Divergence
**Files:** `server.py:730-766`, `function_app.py:91-98`

CORS origins differ between Container Apps and Azure Functions deployments, causing inconsistent behaviour.

### HIGH-MCP-06: Hardcoded Protocol Version
**File:** `function_app.py:246`

`"protocolVersion": "2024-11-05"` is hardcoded rather than read from the MCP SDK.

### HIGH-MCP-07: Manual JSON-RPC Dispatch
**File:** `function_app.py:206-257`

String-matching dispatch (`if method == "tools/list"`) bypasses MCP SDK routing and will break when new methods are added.

### HIGH-MCP-08: Log File Path Exposure in API Responses
**File:** `query_logger.py:408,427,488,518`

Full server-side file paths included in `logFile` response field.

---

## 4. Medium Priority Suggestions

| ID | Description | File(s) |
|----|-------------|---------|
| MED-01 | Configure httpx connection pool limits | All tool modules |
| MED-02 | Add request correlation IDs | server.py |
| MED-03 | Use structured JSON logging for Azure Monitor | All modules |
| MED-04 | Pin dependency versions exactly (`==`) | requirements.txt |
| MED-05 | Consider Pydantic models for tool results | tools/*.py |
| MED-06 | Add OpenTelemetry tracing | server.py, tools/*.py |
| MED-07 | Add input length validation | All tool entry points |
| MED-08 | Implement API key audit logging | server.py |

---

## 5. Positive Observations

- **Clean architecture** - Tool definitions (JSON) are separate from handlers (Python), allowing non-developers to add tools
- **Good error handling patterns** - Most functions have try/except with logging
- **Streamable HTTP implementation** - Correctly handles Copilot Studio's stateless model
- **API key middleware** - Uses `secrets.compare_digest` for constant-time comparison
- **Flexible transport** - Supports both stdio and HTTP modes
- **Dual deployment** - Pragmatic support for both Container Apps and Azure Functions
- **Graph auth** - Clean MSAL implementation with token caching

---

## 6. Scaling Bottleneck Summary

| Priority | Bottleneck | Current Impact | At 50+ Users |
|----------|-----------|---------------|-------------|
| P1 | No Graph API retry logic | Silent failures | Widespread failures under throttling |
| P1 | New httpx client per call | ~400ms overhead/call | Connection exhaustion |
| P1 | Full table scan in list_library_contents | Slow for large libs | 30+ sec per call |
| P2 | No caching layer | Redundant API calls | 60-80% wasted Graph quota |
| P2 | Triple auth client instances | 3x token acquisition | MSAL cache fragmentation |
| P2 | AND-to-OR fallback doubles calls | 2x API calls on miss | Amplified throttling |
| P3 | Synchronous JSONL file logging | Blocking I/O | Log corruption under concurrency |

---

## 7. Recommended Remediation Order

1. **Week 1:** CRIT-MCP-01 (httpx pooling), CRIT-MCP-02 (asyncio.run), CRIT-MCP-03 (KQL sanitisation), CRIT-MCP-07 (error leakage)
2. **Week 2-3:** CRIT-MCP-04 (code dedup), CRIT-MCP-05 (tests), HIGH-MCP-01 (retry logic), HIGH-MCP-04 (health check)
3. **Month 1-2:** CRIT-MCP-06 (CORS), HIGH-MCP-02 (list optimisation), HIGH-MCP-07 (JSON-RPC), caching layer

---

*This is a V1 feedback report. Subsequent versions will track remediation progress and re-assess findings.*
