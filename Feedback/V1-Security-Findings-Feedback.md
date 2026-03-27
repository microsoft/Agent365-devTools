# CMS Workspace - Security Findings Feedback Report V1

**Version:** V1
**Date:** 2026-02-20
**Reviewed by:** Claude Opus 4.6 (multi-agent review team)
**Scope:** All code across CMS workspace - security-specific findings
**Classification:** INTERNAL

---

## 1. Executive Summary

This report consolidates all security findings across the CMS Knowledge Accelerator workspace. **One finding requires emergency action** (hardcoded credentials in source). The remaining findings range from high to low severity and should be addressed according to the priority schedule below.

**Total Security Findings:** 11
- Emergency: 1
- Critical: 3
- High: 4
- Medium: 3

---

## 2. EMERGENCY - Immediate Action Required

### SEC-01: HARDCODED AZURE AD CLIENT SECRET IN SOURCE CONTROL
**Severity:** EMERGENCY | **File:** `upload_to_sharepoint.py:61-63`

The `upload_to_sharepoint.py` script contains hardcoded Azure AD application credentials in plaintext, including the client secret. This grants application-level access to the Azure AD tenant and Graph API.

**Immediate Remediation Steps:**
1. **Rotate the client secret NOW** in Azure AD (Portal > App Registrations > Certificates & secrets)
2. Remove the hardcoded values from the file
3. Scan git history for this file using `git log -- upload_to_sharepoint.py`
4. If found in git history, use BFG Repo Cleaner to purge the secret
5. Audit Azure AD sign-in logs for the application ID for unauthorized usage
6. Replace with environment variable references (`os.environ.get("CLIENT_SECRET")`)
7. Add credential files to `.gitignore`

**Risk Assessment:** If this repository has ever been shared, forked, or backed up, the credentials are compromised. Assume they are.

---

## 3. Critical Security Findings

### SEC-02: LLM Prompt Injection in autoTriage
**Severity:** CRITICAL | **Component:** Agent365-devTools/autoTriage

GitHub issue content (title + body) flows directly into LLM prompts without sanitisation. Attack vectors:
- Crafted issue titles that override classification logic
- Issue bodies containing prompt escape sequences
- Payload that causes triage comments to contain leaked system prompts

**Recommendation:** Input sanitisation, XML delimiters around user content, output validation.

### SEC-03: Path Traversal in SharePoint Upload
**Severity:** CRITICAL | **File:** `upload_to_sharepoint.py:303`

User-supplied filenames are used to construct SharePoint upload paths without sanitising directory traversal characters (`..`, `/`, `\`).

**Recommendation:** Use `os.path.basename()` on all filenames. Validate against a whitelist of allowed characters.

### SEC-04: KQL Injection in Search Queries
**Severity:** CRITICAL | **Files:** MCP server tool modules

User-supplied search terms are interpolated into KQL queries with only double-quote stripping. KQL operators can alter search scope and potentially return documents from other sites.

**Recommendation:** Escape all KQL special characters: `* ? : ( ) [ ] { } \ / AND OR NOT NEAR`.

---

## 4. High Security Findings

### SEC-05: GitHub Token Exposure in Error Handling
**Severity:** HIGH | **Component:** autoTriage

`GithubException` objects may contain request headers (including Authorization) when stringified in error handlers. Similarly, OpenAI client exceptions may leak API keys.

**Recommendation:** Create sanitised error logging that strips sensitive headers.

### SEC-06: Error Messages Leak Server Internals
**Severity:** HIGH | **Component:** MCP Server

Python exception strings returned to external callers in API responses reveal file paths, module names, and stack fragments.

**Recommendation:** Return generic error messages externally. Log full details server-side only.

### SEC-07: CORS Configuration Overly Permissive
**Severity:** HIGH | **Component:** MCP Server

Wildcard patterns with `allow_credentials=True` and `allow_headers=["*"]` create a broad attack surface.

**Recommendation:** Restrict to specific Copilot Studio origins. Remove credential mode unless required.

### SEC-08: API Key in Query String
**Severity:** HIGH | **File:** `server.py:593`

API key accepted via `?api_key=` query parameter. Query parameters appear in access logs, proxy logs, CDN logs, and browser history.

**Recommendation:** Accept API key only via `X-API-Key` header. Remove query string option.

---

## 5. Medium Security Findings

### SEC-09: File Path Disclosure in API Responses
**Severity:** MEDIUM | **File:** `query_logger.py`

Analytics API responses include full server-side file paths in the `logFile` field.

### SEC-10: No Input Length Validation
**Severity:** MEDIUM | **Component:** MCP Server

Search queries, document titles, and topic strings have no maximum length. Extremely long strings could cause memory pressure or KQL parsing issues.

### SEC-11: MSAL Token Cache Not Encrypted
**Severity:** LOW | **File:** `auth/graph_auth.py`

In-memory MSAL token cache stores tokens in plaintext. Acceptable for containerised deployment but noted for compliance reviews.

---

## 6. Security Posture Summary

| Area | Status | Key Concern |
|------|--------|-------------|
| Credential Management | CRITICAL | Hardcoded secret in source |
| Input Validation | CRITICAL | KQL injection, prompt injection |
| Error Handling | HIGH | Token/key leakage in exceptions |
| CORS Policy | HIGH | Overly permissive with credentials |
| Authentication | GOOD | Constant-time API key comparison |
| Transport Security | GOOD | HTTPS enforced, TLS for Graph API |
| Logging | MEDIUM | Path disclosure, potential token leakage |

---

## 7. Remediation Priority

| Priority | Finding | Timeline |
|----------|---------|----------|
| EMERGENCY | SEC-01: Rotate hardcoded secret | Today |
| Week 1 | SEC-02: Prompt injection mitigation | 5 days |
| Week 1 | SEC-03: Path traversal fix | 1 day |
| Week 1 | SEC-04: KQL escaping | 2 days |
| Week 2 | SEC-05: Error handler sanitisation | 2 days |
| Week 2 | SEC-06: Generic error responses | 2 days |
| Week 2 | SEC-07: CORS tightening | 1 day |
| Week 2 | SEC-08: Remove query string API key | 1 day |
| Month 1 | SEC-09, SEC-10, SEC-11 | Low effort |

---

*This is a V1 feedback report. Subsequent versions will track remediation progress and re-assess findings.*
