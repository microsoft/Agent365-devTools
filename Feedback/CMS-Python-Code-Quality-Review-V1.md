# CMS Knowledge Accelerator - Python Code Quality Review V1

**Report ID:** CMS-CODE-V1
**Date:** 2026-02-20
**Reviewer:** CMS Watchdog Team - Code Reviewer Agent
**Scope:** All Python files across the CMS workspace (10 files reviewed)
**Classification:** INTERNAL

---

## Executive Summary

A thorough review of all 10 Python files identified **3 CRITICAL**, **8 HIGH**, **10 MEDIUM**, and **7 LOW** severity findings. The most pervasive issues are: hardcoded credentials (3 files), zero retry logic (all files), and httpx.AsyncClient created per request (all async tools). The MCP server's `graph_auth.py` is the best-structured file; `convert_and_upload.py` is the worst.

---

## Files Reviewed

| # | File | Lines | Verdict |
|---|------|-------|---------|
| 1 | upload_to_sharepoint.py | 447 | 3 CRITICAL, 2 HIGH, 2 MEDIUM, 1 LOW |
| 2 | mcp-server/server.py | ~900 | 1 HIGH, 2 MEDIUM, 2 LOW |
| 3 | mcp-server/function_app.py | ~500 | 2 HIGH, 2 MEDIUM, 1 LOW |
| 4 | mcp-server/auth/graph_auth.py | ~200 | GOOD overall, 2 MEDIUM, 1 LOW |
| 5 | mcp-server/tools/sharepoint_search.py | ~600 | 2 HIGH, 2 MEDIUM |
| 6 | mcp-server/tools/document_content.py | ~450 | 2 HIGH, 2 MEDIUM, 1 LOW |
| 7 | mcp-server/tools/document_metadata.py | ~1200 | 2 HIGH, 2 MEDIUM, 1 LOW |
| 8 | mcp-server/tools/query_logger.py | ~500 | 3 MEDIUM, 2 LOW |
| 9 | config/dummy-data/convert_and_upload.py | ~250 | 1 CRITICAL, 2 HIGH, 1 MEDIUM |
| 10 | config/dummy-data/populate_metadata.py | ~450 | 1 CRITICAL, 2 HIGH, 1 MEDIUM, 1 LOW |

---

## CRITICAL FINDINGS

### CMS-CODE-C01: Hardcoded Credentials (3 files)

Same client secret in plaintext across 3 files:
- `upload_to_sharepoint.py:44`
- `convert_and_upload.py:20`
- `populate_metadata.py:45`

Triple duplication makes secret rotation extremely error-prone. **Must be rotated immediately and consolidated to environment variables.**

### CMS-CODE-C02: Shell Injection via os.system()

**File:** `upload_to_sharepoint.py:29-34`

```python
os.system(f'"{sys.executable}" -m pip install {pkg} --quiet')
```

Uses os.system() through the shell. Silent failure (return code ignored). No version pinning. No hash verification. Runtime side effects modify Python environment.

### CMS-CODE-C03: convert_and_upload.py Bypasses MSAL

**File:** `convert_and_upload.py:29-39`

Raw HTTP POST to token endpoint. Loses token caching, automatic refresh, error handling, correlation IDs, and Microsoft auth best practices.

---

## HIGH FINDINGS

### CMS-CODE-H01: Zero Retry Logic (ALL files)

Not a single file implements retry with exponential backoff or Retry-After header handling. Microsoft explicitly states: "Your application must ALWAYS be prepared to handle 429 responses."

**Affected functions:**
- `upload_to_sharepoint.py`: graph_get, graph_post, graph_put_bytes, graph_patch
- `sharepoint_search.py`: primary search (line 463), fallback search (line 520)
- `document_content.py`: search call (line 269), download (line 401)
- `document_metadata.py`: _search_graph (line 195), _resolve_site_id (line 366), list_library_contents pagination (line 844)
- `convert_and_upload.py`: all graph helpers
- `populate_metadata.py`: all graph helpers

**Recommended fix:** Create shared retry wrapper:
```python
async def _graph_request_with_retry(client, method, url, max_retries=3, **kwargs):
    for attempt in range(max_retries + 1):
        response = await client.request(method, url, **kwargs)
        if response.status_code == 429:
            retry_after = int(response.headers.get("Retry-After", 2 ** attempt))
            await asyncio.sleep(retry_after)
            continue
        if response.status_code in (503, 504):
            await asyncio.sleep(2 ** attempt)
            continue
        response.raise_for_status()
        return response
    response.raise_for_status()
```

### CMS-CODE-H02: httpx.AsyncClient Created Per Request (all async tools)

**Files:** sharepoint_search.py:464,544 | document_content.py:269,401 | document_metadata.py:196,366,420,845

Every Graph API call creates and destroys an HTTP client. Wastes TCP+TLS handshakes. Prevents HTTP/2 multiplexing. Paginated operations create one client per page.

**Fix:** Module-level shared `httpx.AsyncClient` with connection pooling.

### CMS-CODE-H03: asyncio.run() in Azure Functions

**File:** `function_app.py:209, 227, 305`

Creates new event loop inside handlers that already have one. Raises RuntimeError in newer runtimes. Convert to `async def` handlers with `await`.

### CMS-CODE-H04: Error Messages Leak Internal Details

**File:** `function_app.py:263`

```python
"error": {"code": -32603, "message": f"Internal error: {exc}"}
```

Stack traces returned to callers. Return generic error, log full exception server-side.

### CMS-CODE-H05: CORS Wildcard Pattern

**File:** `server.py:733-741`

`allow_origin_regex=r"https://.*\.microsoft\.com"` may match unintended domains without proper anchoring.

### CMS-CODE-H06: No Token Refresh During Long Uploads

**File:** `upload_to_sharepoint.py:66-80`

Single token acquired at startup for all 33 uploads. Tokens valid ~1 hour. Long-running uploads will fail with 401.

### CMS-CODE-H07: time.sleep(0.3) as Throttle Protection

**File:** `populate_metadata.py:431`

Fixed 300ms delay doesn't respect Retry-After headers. Too slow for unconstrained ops, too fast during throttling.

### CMS-CODE-H08: find_item_in_drive Swallows Auth Failures

**File:** `populate_metadata.py:240-256`

```python
except Exception:
    pass
```

Expired tokens silently reported as "file not found". Every subsequent lookup fails silently.

---

## MEDIUM FINDINGS

### CMS-CODE-M01: Duplicated Stop Words (3 copies)
Files: sharepoint_search.py:45, document_metadata.py:42, query_logger.py:343

### CMS-CODE-M02: Duplicated Graph Response Formatters (3 implementations)
Files: sharepoint_search.py (_format_search_result), document_metadata.py (_format_hit, _format_list_item)

### CMS-CODE-M03: Module-Level Singletons with global Pattern
Files: sharepoint_search.py:115-151, document_content.py, document_metadata.py
Prevents testing with mock dependencies.

### CMS-CODE-M04: python-docx Import Error Returns as Content
**File:** document_content.py:141-146
```python
return "[python-docx is not installed...]"
```
Error message returned as if it were document content. Agent may present to user.

### CMS-CODE-M05: No max_chars Validation
**File:** document_content.py:198
Malicious caller could pass max_chars=999999999.

### CMS-CODE-M06: Conflict Detection Sentiment Analysis Bug
**File:** document_metadata.py:1168-1176
"not permitted" matches BOTH permissive ("permitted") AND restrictive ("not permitted") signal sets.

### CMS-CODE-M07: practice_areas_found Never Populated
**File:** sharepoint_search.py:684
Set initialized but never written to. Briefing notes always say "across the knowledge base".

### CMS-CODE-M08: Query Logger Race Condition
**File:** query_logger.py:84-111
Concurrent requests could both rename the same log file during rotation.

### CMS-CODE-M09: Deprecated asyncio.get_event_loop()
**File:** query_logger.py:269
Deprecated in Python 3.10+. Use asyncio.get_running_loop().

### CMS-CODE-M10: MSAL Token Cache Not Thread-Safe
**File:** graph_auth.py:33-36
Azure Functions v2 can run concurrent threads. Risk of token cache corruption.

---

## LOW FINDINGS

### CMS-CODE-L01: Hardcoded Filesystem Paths
Files: upload_to_sharepoint.py:49-53, convert_and_upload.py:23

### CMS-CODE-L02: guarded_send Accesses Private request._send
File: server.py:721. Relies on Starlette internal.

### CMS-CODE-L03: Tool Definition Lookup is O(n)
File: server.py:447. Should be dict lookup.

### CMS-CODE-L04: No Graceful SIGTERM Handling
File: server.py. Container Apps sends SIGTERM before kill.

### CMS-CODE-L05: Unused import xml.etree.ElementTree
File: document_content.py:105

### CMS-CODE-L06: Bare Exception Catches
Files: document_content.py:97, populate_metadata.py:240, document_metadata.py:111

### CMS-CODE-L07: sys.exit(1) on Single Library Failure
File: populate_metadata.py:359. Should continue processing and report.

---

## Cross-Cutting Recommendations

### Priority 1: Shared Graph Client Utility
Create `graph_client.py` with:
- Retry with exponential backoff (base 2s, max 32s)
- Retry-After header respect
- HTTP 429 and 503 handling
- Connection pooling via shared httpx.AsyncClient
- Request logging with correlation IDs

### Priority 2: Credential Management
- Move all secrets to environment variables
- Single source of truth (no duplication across files)
- Certificate-based auth for production

### Priority 3: Code Deduplication
Consolidate into shared utilities:
- Stop words
- Graph response formatters
- Auth client singleton
- Config loaders
- Keyword extraction

### Priority 4: Testability
- Refactor module-level singletons to dependency injection
- Abstract file I/O
- Create httpx client factory for mock injection
- Add pytest fixtures for common test patterns

---

## Prioritised Action Items

| Priority | Action | Files Affected |
|----------|--------|---------------|
| IMMEDIATE | Rotate credentials, move to env vars | 3 files |
| Before deploy | Implement retry logic | All Graph API callers |
| Before deploy | Create shared httpx.AsyncClient | All async tool modules |
| Before deploy | Fix asyncio.run() in function_app.py | function_app.py |
| Before deploy | Sanitize error responses | function_app.py |
| Short term | Consolidate duplicated code | 5+ modules |
| Short term | Add thread-safety to token cache | graph_auth.py |
| Short term | Remove os.system() pip install | upload_to_sharepoint.py |
| Medium term | Refactor for dependency injection | All tool modules |
| Medium term | Add structured logging | All modules |

---

*Report Version: V1*
*Generated by CMS Watchdog Team - Code Reviewer Agent*
*Date: 2026-02-20*
