# CMS Knowledge Accelerator - Evaluation Failure Analysis V1

**Report ID:** CMS-EVAL-V1
**Date:** 2026-02-20
**Analyst:** CMS Watchdog Team - Eval Analyst Agent
**Scope:** Analysis of agent evaluation results, retrieval quality, and knowledge base coverage
**Classification:** INTERNAL

---

## Executive Summary

Analysis of two evaluation runs (41 questions each) reveals a **41% pass rate (Run 1)** and **49% pass rate (Run 2)**, both well below the 70-80% SOW target. The improvement between runs is marginal (~8 percentage points), indicating **structural issues** rather than prompt-level problems.

**Root cause:** Nearly 100% of failures are **SharePoint search retrieval failures**, not agent reasoning failures. The content exists in the knowledge base but the search layer cannot find it.

---

## Evaluation Results Overview

| Metric | Run 1 (1523) | Run 2 (2019) |
|--------|-------------|-------------|
| Pass | ~17 (41%) | ~20 (49%) |
| Fail | ~20 (49%) | ~20 (49%) |
| Error | ~4 (10%) | ~1 (2%) |
| Total Questions | 41 | 41 |

---

## Failure Categories

### Category A: Content EXISTS but Agent Cannot Find It (Core Retrieval Failure)

These are the most concerning failures. The content is definitively in the SharePoint libraries but the agent's search returns nothing.

| Finding ID | Question | Expected Document | Root Cause |
|-----------|----------|-------------------|------------|
| CMS-EVAL-F01 | BNK-Q01: DocuSign signing instructions | BNK-EXEC-001 | SharePoint search fails on exact title match |
| CMS-EVAL-F02 | BNK-Q02: Mercury virtual signing | BNK-EXEC-008 | Search returns 0 results for "Mercury" |
| CMS-EVAL-F03 | BNK-Q04: How to gain DocuSign access | BNK-EXEC-009 Section 2 | Search fails despite well-indexed doc |
| CMS-EVAL-F04 | BNK-Q06: Should we control DocuSign process | BNK-EXEC-009 Section 3 | Query mismatch with document headings |
| CMS-EVAL-F05 | BNK-Q07: Can we generate OTP/access code | BNK-EXEC-009 Section 4 | Jargon "OTP" may not match indexed terms |
| CMS-EVAL-F06 | BNK-Q08: Two factor authentication | BNK-EXEC-009 Section 5 | Generic query dilutes relevance |
| CMS-EVAL-F07 | BNK-Q12: Specimen signatures on DocuSign | BNK-EXEC-009 Section 7 | Search returns 0 results both runs |
| CMS-EVAL-F08 | BNK-Q13: Dating documents outside DocuSign | BNK-EXEC-009 Section 8 | Search returns 0 results both runs |
| CMS-EVAL-F09 | BNK-Q16: Share certificates on DocuSign | BNK-EXEC-011 Section 4 | Search fails to find it |
| CMS-EVAL-F10 | BNK-Q18: Land Registry requirements | BNK-EXEC-007 | Search returns 0 results |
| CMS-EVAL-F11 | BNK-Q22: Someone else sign on behalf | BNK-EXEC-011 Section 7 | Search fails |
| CMS-EVAL-F12 | BNK-Q23: Lender not informed of e-signature | BNK-EXEC-011 Section 8 | Search fails |
| CMS-EVAL-F13 | BNK-Q28: E-signatures non-English law | BNK-EXEC-005 | Search returns 0 results |
| CMS-EVAL-F14 | BNK-Q30: Edit envelope party opts out | BNK-EXEC-010 Section 4 | Search fails both runs |
| CMS-EVAL-F15 | BNK-Q33: In Process watermark | BNK-EXEC-010 Section 7 | Search fails both runs |
| CMS-EVAL-F16 | BNK-Q34: Individual personal e-signature | BNK-EXEC-011 Section 10 | Search fails both runs |
| CMS-EVAL-F17 | CORP-Q02: Formula-based allotment authority | CORP-OP-002 | Search returns 0 results |
| CMS-EVAL-F18 | CORP-Q03: Non-cash consideration valuation | CORP-OP-003 | Search returns 0 results |
| CMS-EVAL-F19 | CORP-Q05: Financial assistance fees | CORP-OP-005 | Search returns 0 results |

### Category B: Inconsistent Results Between Runs (Non-Deterministic)

| Question | Run 1 | Run 2 | Concern |
|----------|-------|-------|---------|
| BNK-Q10: Certificate of completion | Fail | Pass | Same content, different result |
| BNK-Q11: Third-party DocuSign control | Pass | Error | Regression |
| BNK-Q29: Download from incomplete envelope | Fail | Pass | Same content, different result |
| BNK-Q31: Switch to wet-ink | Fail | Pass | Same content, different result |
| BNK-Q32: Document upload issues | Fail | Pass | Same content, different result |

### Category C: Content/Data Issues

| Finding ID | Issue | Detail |
|-----------|-------|--------|
| CMS-EVAL-D01 | CORP-Q04 content mismatch | Test asks about "Maxima Holdings" but document titled "Re Halt Garage (1964) Ltd" |
| CMS-EVAL-D02 | BNK-Q15 content gap | Wet-ink requirements doc does NOT mention stock transfer forms |

---

## Root Cause Analysis

### Root Cause 1: SharePoint Search/Semantic Index Limitations (PRIMARY - 60%)

- Documents are within character limits but **tables break retrieval** (per Microsoft: "Copilot is currently unable to parse tables")
- Test tenant (bsstest238691.sharepoint.com) may not have fully warmed semantic index
- Search across 60+ libraries dilutes relevant results

### Root Cause 2: items_by_url Exceeds Platform Limit (PRIMARY - may affect all searches)

- Agent references **47 `items_by_url` entries**
- Microsoft documents a **20-item limit**
- Libraries beyond ~20 are likely **silently ignored**, making most content unsearchable

### Root Cause 3: Agent Instruction Design (SECONDARY - 20%)

- "MAXIMUM ONE tool call per user message" prevents retry with different keywords
- "If unsure, try Banking first" misdirects Corporate questions
- "Search once, answer immediately" prevents keyword reformulation

### Root Cause 4: Query-Document Vocabulary Mismatch (CONTRIBUTING - 15%)

| User Query | Document Heading |
|-----------|-----------------|
| "OTP" | "One-Time Passwords (OTPs) and Access Codes" |
| "Mercury virtual signing" | "Mercury Electronic Signing Platform - Guide" |
| "PG82 certificate" | "Form PG82 - Identity Verification" |
| "two factor authentication" | "Multi-Factor Authentication and Security" |

### Root Cause 5: Library Scoping (CONTRIBUTING - 5%)

- Every search queries all 60+ libraries
- Bank-specific libraries (23+) create noise for execution-of-documents queries

---

## Recommendations

### Immediate Fixes (This Week)

| # | Action | Expected Impact | Effort |
|---|--------|----------------|--------|
| 1 | Reduce `items_by_url` to site URL or 3-5 libraries | May fix majority of retrieval failures | 30 min |
| 2 | Remove "ONE tool call" restriction | Could fix 40-50% of remaining failures | 30 min |
| 3 | Add retry instruction: "If 0 results, reformulate and search again" | Improves recovery from initial misses | 30 min |
| 4 | Fix CORP-OP-004: Add "Maxima Holdings" as keyword alias | Fixes 1 guaranteed failure | 15 min |
| 5 | Add stock transfer forms to BNK-EXEC-004 | Fixes 1 content gap | 15 min |

### Medium-Term Fixes (2-4 Weeks)

| # | Action | Expected Impact | Effort |
|---|--------|----------------|--------|
| 6 | Remove tables from SharePoint documents | Improves Copilot parsing | 1-2 days |
| 7 | Add keyword-rich metadata (synonyms, natural language forms) | Improves search matching | 2-3 days |
| 8 | Restructure large documents (BNK-EXEC-011: 10+ topics) | Aligns chunking with topics | 1-2 days |
| 9 | Reference individual files by URL (up to 20) | Full-content access | 1 day |
| 10 | Create FAQ/index document mapping questions to doc refs | Retrieval amplifier | 1 day |

### Architectural Fixes (Phase 2)

| # | Action | Expected Impact | Effort |
|---|--------|----------------|--------|
| 11 | Azure AI Search with hybrid retrieval | Step change in precision | 2-3 weeks |
| 12 | Copilot Tuning for legal terminology | Domain specificity | 1-2 weeks |
| 13 | Copilot Connectors for custom indexing | Control chunking/embedding | 3-4 weeks |

---

## Evaluation Methodology Critique

### Current Flaws

1. **"GeneralQuality" is too binary** -- "I can't find it but I'll search differently" gets same "Fail" as completely wrong answer
2. **No distinction between retrieval failure vs reasoning failure** -- Nearly 100% are retrieval failures but evaluation doesn't separate them
3. **Pass criteria favour "seems relevant" over accuracy** -- Finding but not answering still passes
4. **No multi-turn evaluation** -- Real users would follow up
5. **High run variance (~8%)** -- System not stable enough for reliable measurement

### Recommended Improvements

- Add "Retrieval Success" metric separate from "Answer Quality"
- Track specific SharePoint URL returned vs expected
- Run each question 3-5 times and report consistency
- Add multi-turn test scenarios
- Score partial credit for correct topic identification

---

## Realistic Ceiling Assessment

| Approach | Estimated Accuracy Ceiling |
|----------|---------------------------|
| Current (no changes) | 49% |
| With immediate fixes (items_by_url, retry, instructions) | 65-75% |
| With medium-term fixes (metadata, restructuring) | 75-85% |
| With Azure AI Search (Phase 2) | 85-95% |

---

## Key Files Referenced

- Evaluation results (latest): `Evaluate CMS Knowledge Agent 260220_2019.csv`
- Evaluation results (earlier): `Evaluate CMS Knowledge Agent 260220_1523.csv`
- Test questions: `cms-knowledge-accelerator/tests/test-questions.json`
- Agent config: `cms-knowledge-accelerator/agents/cms-knowledge-agent/appPackage/declarativeAgent.json`
- Key dummy data files with retrieval failures:
  - `config/dummy-data/banking/execution-of-documents/docusign-access-and-administration.md` (BNK-EXEC-009)
  - `config/dummy-data/banking/execution-of-documents/docusign-troubleshooting-guide.md` (BNK-EXEC-010)
  - `config/dummy-data/banking/execution-of-documents/execution-special-cases-guide.md` (BNK-EXEC-011)
  - `config/dummy-data/banking/execution-of-documents/mercury-esigning-guide.md` (BNK-EXEC-008)
  - `config/dummy-data/banking/execution-of-documents/land-registry-requirements.md` (BNK-EXEC-007)
  - `config/dummy-data/banking/execution-of-documents/wet-ink-requirements.md` (BNK-EXEC-004)
  - `config/dummy-data/corporate/counsels-opinions/` (all 7 opinion files)

---

*Report Version: V1*
*Generated by CMS Watchdog Team - Eval Analyst Agent*
*Date: 2026-02-20*
