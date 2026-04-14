# autoTriage - Code Review Feedback Report V1

**Version:** V1
**Date:** 2026-02-20
**Reviewed by:** Claude Opus 4.6 (multi-agent review team)
**Component:** Agent365-devTools/autoTriage
**Scope:** All Python source files, configuration, tests, and GitHub workflows

---

## 1. Overall Assessment

The autoTriage application is well-structured with clean separation of concerns (cli/services/models), good dependency injection patterns that enable testability, and thoughtful fallback logic when LLM services are unavailable. The codebase demonstrates strong engineering fundamentals.

**Rating: Good - with critical security items requiring immediate attention**

---

## 2. Critical Findings

### CRIT-AT-01: LLM Prompt Injection Vulnerability
**Severity:** CRITICAL | **Files:** `services/llm_service.py:100-130`, `services/intake_service.py:676-681`

GitHub issue titles and bodies are passed directly into LLM prompts without sanitisation:

```python
# llm_service.py:121-125
user_prompt = self.prompts.format(
    "classify_issue_user",
    default=f"Classify this issue:\nTitle: {title}\nBody: {body}",
    title=title,
    body=body,
    ...
)
```

A malicious GitHub issue could contain prompt injection payloads in its title or body that override the system prompt, causing the LLM to:
- Misclassify issues intentionally (e.g., marking a P1 critical bug as P4 low)
- Leak system prompt content in triage comments
- Generate harmful or misleading triage output posted back to GitHub

**Recommendation:**
1. Sanitise issue content before LLM submission - strip control characters and known prompt injection patterns
2. Add delimiters around user content in prompts (e.g., XML tags `<issue_title>...</issue_title>`)
3. Validate LLM output against expected schemas before acting on it
4. Consider body length limits more aggressively (currently 2000 chars in some places but not all)

---

### CRIT-AT-02: GitHub Token Exposure Risk in Error Handling
**Severity:** CRITICAL | **Files:** `services/llm_service.py:79-80`, `services/github_service.py`

Exception handlers use f-string formatting that may surface sensitive context:

```python
# llm_service.py:79-80
except Exception as e:
    logging.error(f"LLM call failed: {e}")
    return None
```

When the OpenAI client fails (e.g., authentication error), the exception may contain the API key or endpoint URL in its string representation. Similarly, `GithubException` objects may contain request headers including the `GITHUB_TOKEN`.

**Recommendation:**
1. Create a sanitisation utility that strips known sensitive patterns (tokens, keys, Authorization headers) from exception messages before logging
2. Use structured logging with explicit fields rather than `str(e)`
3. Never log raw HTTP request/response objects

---

### CRIT-AT-03: LLM API Key Stored as Instance Attribute
**Severity:** HIGH | **File:** `services/llm_service.py:46`

```python
self.api_key = os.environ.get("GITHUB_TOKEN", os.environ.get("GITHUB_MODELS_KEY", ""))
```

The API key is stored as a plain instance attribute on `LlmService`. If the object is ever serialised, logged, or inspected in a debugger, the key is exposed. While this is standard practice for many SDK wrappers, it's worth noting for security-conscious deployments.

**Recommendation:** Consider using a property or method that reads from env at call time, or mark the attribute as private (`_api_key`).

---

## 3. High Priority Improvements

### HIGH-AT-01: No Rate Limiting for LLM API Calls
**Files:** `services/llm_service.py`

Each issue triage triggers multiple LLM calls:
1. `classify_issue` - classification
2. `is_security_issue` - security assessment
3. `is_copilot_fixable` - Copilot assessment
4. `generate_fix_suggestions` - fix suggestions
5. `select_assignee` - assignee selection

That's 5 LLM calls per issue. During a burst of new issues (e.g., 50 issues from a migration), this generates 250 LLM API calls with no throttling.

**Recommendation:** Add configurable rate limiting (e.g., max N calls per minute) and consider batching where possible.

---

### HIGH-AT-02: Module-Level Cache Without Eviction Bounds
**File:** `services/github_service.py:22-24`

```python
_cache: Dict[str, Tuple[Any, datetime]] = {}
CACHE_TTL_SECONDS = 900  # 15 minutes
```

The in-memory cache grows unbounded. While TTL causes stale entries to be skipped, they are only cleaned up on access (lazy eviction). In a long-running process triaging many repositories, the cache dictionary will accumulate expired entries.

**Recommendation:** Add a periodic cleanup or maximum cache size with LRU eviction. The `@lru_cache` decorators on `get_repository_labels`, `get_repository_context`, etc. (lines 435, 456, 527, 610, 832) also have no clear eviction strategy for different repository contexts.

---

### HIGH-AT-03: `@lru_cache` on Instance Methods
**Files:** `services/github_service.py:435,456,527,610,832`

Multiple instance methods use `@lru_cache`:

```python
@lru_cache(maxsize=100)
def get_repository_labels(self, owner: str, repo: str) -> Dict[str, dict]:
```

`@lru_cache` on instance methods means `self` is part of the cache key. If `GitHubService` instances are created frequently (e.g., per request), the cache is never hit. If there's only one instance, this works but the cache is never invalidated even if repository labels change.

**Recommendation:** Use the existing `_get_cached`/`_set_cached` pattern consistently, or use `functools.cached_property` for truly static data. Remove the dual-caching pattern where both `@lru_cache` and manual `_get_cached` are used on the same method (e.g., `get_repository_context` at line 456-525).

---

### HIGH-AT-04: Issue Body Truncation Inconsistency
**Files:** `services/llm_service.py`, `services/intake_service.py`

Issue body is truncated at different lengths across different LLM calls:
- `is_copilot_fixable`: `body[:2000]` (line 486)
- `generate_fix_suggestions`: `body[:2000]` (line 588)
- `select_assignee`: `body[:2000]` (line 804)
- `classify_issue`: No truncation (line 108: `combined = f"{title} {body}".lower()`)
- `is_security_issue`: No truncation (line 186)

**Risk:** Extremely long issue bodies (e.g., a crash log dump) will consume excessive tokens in `classify_issue` and `is_security_issue`, potentially hitting token limits or causing errors.

**Recommendation:** Apply consistent truncation across all LLM entry points. Define a constant `MAX_ISSUE_BODY_LENGTH` and apply it uniformly.

---

### HIGH-AT-05: No Retry Logic for LLM or GitHub API Failures
**Files:** `services/llm_service.py:55-81`, `services/github_service.py`

LLM calls fail silently and fall back to keyword matching:

```python
except Exception as e:
    logging.error(f"LLM call failed: {e}")
    return None
```

GitHub API calls similarly return empty results on failure. For transient errors (network timeouts, rate limiting), a single retry with backoff would significantly improve reliability.

**Recommendation:** Add retry logic with exponential backoff for transient failures (429, 503, timeout). Consider using `tenacity` for clean retry patterns.

---

### HIGH-AT-06: `_write_reasoning_log` Uses Emojis in Output
**File:** `services/intake_service.py:489,498,500`

```python
f.write("... ✅ **Applied Successfully**\n")
f.write("⚠️ **Skipped due to validation issues**\n")
f.write("❌ **Failed to apply changes**\n")
```

The CLAUDE.md for this project explicitly states: "No emojis in code, comments, logs, or output". The reasoning log uses emoji status indicators.

**Recommendation:** Replace emojis with text indicators: `[OK]`, `[WARNING]`, `[FAILED]`.

---

## 4. Medium Priority Suggestions

### MED-AT-01: Duplicate `MAX_CONTRIBUTORS_TO_SHOW` Constant
**Files:** `services/llm_service.py:16`, `services/github_service.py:37`

Both files define `MAX_CONTRIBUTORS_TO_SHOW = 3`. This should be defined once and imported.

### MED-AT-02: `triage_issues` Function Is Too Long
**File:** `services/intake_service.py:508-868`

The `triage_issues` function is 360 lines long with deeply nested logic. Consider extracting:
- Issue fetching logic into `_fetch_issues_to_triage()`
- Per-issue classification into `_classify_single_issue()`
- Results writing into its own module

### MED-AT-03: Error Handling in `_apply_triage_changes` Exposes Internals
**File:** `services/intake_service.py:413-415`

```python
return {"labels": False, "assignee": False, "comment": False, "error": str(e)}
```

`str(e)` may contain internal details. Sanitise before including in output.

### MED-AT-04: `_parse_issue_url` Does Not Handle Enterprise GitHub URLs
**File:** `services/intake_service.py:25-39`

The regex pattern only matches `github.com`. GitHub Enterprise URLs (e.g., `github.mycompany.com`) would fail silently.

### MED-AT-05: Missing Type Hints on Some Parameters
**File:** `services/intake_service.py:122-130`

The `config` parameter in `_select_human_assignee` lacks a type hint (uses bare `config`).

---

## 5. Positive Observations

- **Dependency injection throughout** - Services accept injected dependencies, making testing straightforward
- **Comprehensive fallback logic** - Every LLM call has a keyword-based fallback for when the LLM is unavailable
- **Good test coverage** - Unit and integration tests exist with proper mocking
- **Clean prompt management** - `prompt_loader.py` with YAML-based prompt templates is well-designed
- **Security-aware triage** - The security issue detection and priority elevation logic is thoughtful
- **Label validation** - The `validate_labels` method with fuzzy matching prevents applying non-existent labels
- **Idempotent triage** - `update_or_add_triage_comment` prevents duplicate bot comments on re-triage
- **Recent triage detection** - `was_recently_triaged` prevents redundant processing

---

## 6. Scaling Considerations

| Concern | Current Behaviour | At Scale Impact |
|---------|------------------|-----------------|
| LLM calls per issue | 5 calls | 50 issues = 250 LLM calls, potential rate limiting |
| In-memory cache | Unbounded growth | Memory leak in long-running processes |
| GitHub API rate | 5000/hour (authenticated) | ~100 issues consumes ~500-1000 API calls |
| `@lru_cache` on methods | Per-instance | Multiple instances = cache misses |
| Sequential processing | One issue at a time | 50 issues = serial processing, no parallelism |

**Recommendation for scale:** Add async processing (current code is synchronous), implement request queuing, and add configurable concurrency limits.

---

*This is a V1 feedback report. Subsequent versions will track remediation progress and re-assess findings.*
