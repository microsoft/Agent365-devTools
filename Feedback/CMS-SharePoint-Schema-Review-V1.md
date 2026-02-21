# CMS Knowledge Accelerator - SharePoint Schema & Content Architecture Review V1

**Report ID:** CMS-SP-V1
**Date:** 2026-02-20
**Specialist:** CMS Watchdog Team - SharePoint Specialist Agent
**Scope:** SharePoint site structure, library architecture, metadata strategy, search optimisation
**Classification:** INTERNAL

---

## Executive Summary

The CMSKnowledgeHub SharePoint site has **62 document libraries** with **47 referenced in the banking agent**. This exceeds Microsoft's documented 20-item `items_by_url` limit for declarative agents, likely causing the majority of retrieval failures. The CMS production tenant already has a rich managed metadata infrastructure (8 taxonomy term sets) that the POC ignores, creating a parallel schema that won't align with production. Consolidating to 3-6 libraries with metadata-based classification would dramatically improve agent performance and scalability.

---

## 1. Library Proliferation Analysis

### Current State

| Category | Count | Examples |
|----------|-------|---------|
| Client-specific | 15 | Barclays, HSBC, Lloyds, RBS, JP Morgan |
| Topic | 28 | Guarantees, Fee Letters, Crypto, LIBOR Transition |
| Core | 7 | Banking A-Z, Standard Forms, Most Useful Documents |
| Scotland | 4 | Know How, Essential Documents, Training, Updates |
| Training | 2 | Trainee materials, Practice Development 2023 |
| Corporate | 2 | DV4, Corporate Knowledge Library |
| **Total** | **62** | |

### Critical Finding: items_by_url Exceeds Platform Limit

- Banking agent manifest lists **47 `items_by_url` entries**
- Microsoft documents a **20-item limit** for OneDriveAndSharePoint capability
- Libraries beyond ~20 are **likely silently ignored**
- This directly explains retrieval failures on documents in lower-ranked libraries

**Fix:** Replace 47 URLs with the site-level URL:
```json
{ "url": "https://[TENANT].sharepoint.com/sites/CMSKnowledgeHub" }
```

### Redundancies Found

| Library A | Library B | Issue |
|-----------|-----------|-------|
| "Crypto" | "Cryptocurrency" | Two libraries for same topic |
| "Banking Scotland Know How" | "Banking Scotland Know How Updates" | Updates could be metadata filter |
| "Silicon Valley Bank" | "HSBC Innovation Bank" | Same entity, name changed |
| "Bank Guarantees" | "Guarantees" | Overlapping scope |
| "Finance Transactional A Z" | "Banking A - Z" | Different names, similar purpose |

---

## 2. Metadata Strategy Assessment

### Current POC Schema (metadata-schema.json)

| Field | Type | Status |
|-------|------|--------|
| CMS_PracticeArea | Choice | Exists, not enforced |
| CMS_BusinessArea | Choice | Exists, not enforced |
| CMS_LegalSubject | Text (free-text) | Exists, not enforced |
| CMS_Client | Text (free-text) | Exists, not enforced |
| CMS_CategoryDescription | Note | Exists, not enforced |
| CMS_ReviewDate | DateTime | Exists, not enforced |
| CMS_HideFromDelve | Boolean | Exists, default false |
| CMS_DocumentStatus | Choice | Enhancement, not enforced |
| CMS_DocumentSummary | Note | Enhancement, not enforced |

### Critical Finding: Production Site Has Rich Taxonomy the POC Ignores

The `full-site-template.json` reveals CMS's **existing production site** already uses Managed Metadata tied to the term store:

| Production Taxonomy Field | Term Set | POC Equivalent |
|---------------------------|----------|----------------|
| Practice Area | `Intranet:Practice area` | CMS_PracticeArea (Choice -- inferior) |
| Business Area | `Intranet:Business Area` | CMS_BusinessArea (Choice -- inferior) |
| Legal Subject | `Intranet:Legal Subject` | CMS_LegalSubject (Text -- inferior) |
| Document Type | `Intranet:Document Type` | **MISSING from POC** |
| Geography | `Intranet:Geography` | **MISSING from POC** |
| Client | `Intranet:Client` (open term set) | CMS_Client (Text -- inferior) |
| Client Sectors | `Intranet:Client sectors` (multi) | **MISSING from POC** |
| Non-legal Subject | `Intranet:Non-legal Subject` | **MISSING from POC** |

**Impact:** The POC metadata won't align with CMS's real infrastructure. Taxonomy fields are automatically mapped as crawled properties, making them inherently more searchable than plain Choice/Text columns.

### Metadata Gaps

| Gap | Impact |
|-----|--------|
| No Document Type classification | Can't distinguish guidance notes from templates from opinions |
| CMS_LegalSubject is free-text, not taxonomy | "e-signing" vs "E-Signatures" vs "Electronic Signatures" divergence |
| No Geography field | Scotland content needs separate libraries instead of metadata |
| No cross-referencing between Banking and Corporate | No metadata connection for related content |
| CMS_ReviewDate not enforced | No lifecycle management |
| CMS_DocumentSummary not mapped as managed property | Graph Search can't use it (root cause of poor retrieval) |

---

## 3. Content Architecture Alternatives

### Option A: Fewer Libraries with Richer Metadata (RECOMMENDED)

Consolidate 62 libraries to **3-6 primary libraries**:

| Proposed Library | Replaces | Content |
|-----------------|----------|---------|
| Banking Knowledge Library | 45+ banking libraries | All banking know-how, precedents, client materials |
| Corporate Knowledge Library | DV4, Corporate content | All corporate know-how and opinions |
| Cross-Practice Library | Execution of Documents, Brexit, COVID-19, etc. | Shared content |
| Archive | Archive | Superseded/historical content |
| Training Materials | Trainee materials, Practice Development | Onboarding and development |

Classification via existing taxonomy fields:
- **Practice Area** -- Banking, Corporate, Employment, Real Estate
- **Document Type** -- Guidance Note, Counsel's Opinion, Template, Checklist, Precedent
- **Legal Subject** -- Electronic Signatures, Companies House, Guarantees, LMA, LIBOR
- **Client** -- HSBC, Barclays, Lloyds
- **Geography** -- England & Wales, Scotland, Northern Ireland, International
- **Document Status** -- Current, Under Review, Archived
- **Document Summary** -- Critical search signal

**Benefits:**
- Agent needs only 3-5 `items_by_url` entries (within 20-item limit)
- New practice areas = new taxonomy term, not 15+ new libraries
- Client documents identified by Client field, not separate library

### Option B: Leverage Existing Production Taxonomy

Align POC columns with production taxonomy:

| POC Field | Should Map To |
|-----------|--------------|
| CMS_PracticeArea (Choice) | Practice Area (Taxonomy) |
| CMS_BusinessArea (Choice) | Business Area (Taxonomy) |
| CMS_LegalSubject (Text) | Legal Subject (Taxonomy) |
| CMS_Client (Text) | Client (Taxonomy) |
| *missing* | Document Type (Taxonomy) |
| *missing* | Geography (Taxonomy) |
| CMS_ReviewDate (DateTime) | Review Date (existing field) |

### Option C: Hub Site Pattern (Future Scaling)

Site is already hub-associated (`RelatedHubSiteIds` present). For 4+ practice areas:
- Hub site = CMS Knowledge Hub (shared navigation, search, content types)
- Associated sites per practice area inherit content types and taxonomy

**Verdict:** Over-engineering for current scope. Plan for it, don't build it yet.

---

## 4. Search Optimisation

### Critical Gap: CMS_DocumentSummary Not Mapped as Managed Property

This is the **single highest-impact search configuration fix**. The field exists but Graph Search cannot index it.

### Managed Properties Needed

| Column | Managed Property | Configuration |
|--------|-----------------|---------------|
| CMS_DocumentSummary | CMSDocumentSummary | Searchable, Queryable, Retrievable, Full-text |
| CMS_DocumentStatus | CMSDocumentStatus | Queryable, Retrievable, Refinable |
| CMS_PracticeArea | CMSPracticeArea | Queryable, Retrievable, Refinable |
| CMS_LegalSubject | CMSLegalSubject | Searchable, Queryable, Retrievable |
| CMS_ReviewDate | CMSReviewDate | Queryable, Retrievable, Sortable |

### Additional Search Improvements

| Improvement | Detail |
|-------------|--------|
| Custom result source | Scope to CMSKnowledgeHub, filter DocumentStatus = Current |
| Full re-crawl | Required after property mappings configured |
| Result source for agent | Pre-filter archived documents from agent queries |

---

## 5. Governance Concerns

### Document Lifecycle

| Gap | Impact |
|-----|--------|
| No automated review date alerts | Stale documents cited by agent without warning |
| No process to move documents from Current to Under Review | Manual only |
| No process to archive superseded documents | Old versions remain active |
| currencyThresholdDays (730) only used by MCP tool | No SharePoint-side workflow |

**Recommendation:** Power Automate flow: daily check for CMS_ReviewDate < Today, auto-set status to "Under Review", email KM team.

### Content Ownership

- 7 Owners, 5 Members, 16 Visitors for 62 libraries
- No per-library ownership assignment
- Need "Knowledge Owners Register" mapping Legal Subject terms to responsible KM team members

### Version Control

- Default versioning (major only, no limit)
- No content approval workflow
- No check-in/check-out enforcement
- Set version limit of 50-100 to control storage

### Permissions

- All 62 libraries inherit from site level (appropriate for knowledge library)
- Risk: client-specific libraries may need restricted access
- If consolidating, keep a small "Restricted Client Materials" library for permissioned content

---

## 6. Scalability Analysis

### Adding a Third Practice Area

| Current approach | Consolidated approach |
|-----------------|----------------------|
| 15-30 new libraries | 0-1 new libraries |
| New agent manifest with 15-30 URLs | New taxonomy term + 1 URL |
| New routing config in MCP server | Practice Area filter |
| Total: 80-100+ libraries | Total: 4-7 libraries |

### 10x More Documents Per Library

SharePoint supports 30M items/library. Practical limit is 5,000 items per view (list view threshold). Managed via:
- Indexed columns for filtered views
- Custom views showing < 5,000 items
- Agent/Graph Search not affected (queries search index)

### Multi-Jurisdiction Content

Geography taxonomy field handles this. No separate jurisdiction libraries needed. International expansion = new Geography terms.

---

## Priority Recommendations

### Immediate (This Week)

| # | Action | Effort | Impact |
|---|--------|--------|--------|
| 1 | Map CMS_DocumentSummary as searchable managed property + re-crawl | 2-4 hours | Highest-impact search fix |
| 2 | Reduce items_by_url to site URL or 3-5 consolidated library URLs | 30 min | May fix majority of retrieval failures |

### Short-Term (2-4 Weeks)

| # | Action | Effort | Impact |
|---|--------|--------|--------|
| 3 | Align POC metadata with production taxonomy | 1-2 weeks | Production alignment |
| 4 | Mark CMS_DocumentSummary and CMS_DocumentStatus as required | 1 day | Data quality |
| 5 | Backfill CMS_DocumentSummary on all documents | 2-3 days | Search improvement |

### Medium-Term (1-3 Months)

| # | Action | Effort | Impact |
|---|--------|--------|--------|
| 6 | Consolidate libraries from 62 to 3-6 | 2-4 weeks | Scalability |
| 7 | Deploy Power Automate lifecycle workflows | 1-2 weeks | Governance |
| 8 | Create custom search result source | 1 day | Relevance |

### Long-Term (Pre-Scaling)

| # | Action | Effort | Impact |
|---|--------|--------|--------|
| 9 | Evaluate hub site pattern (before 3rd practice area) | 1 week | Future-proofing |
| 10 | Design restricted content permission model | 1 week | Security |
| 11 | Establish Knowledge Owners Register | 1 day | Governance |

---

## Key Sources

- SharePoint Online limits: 2,000 lists/libraries per site, 30M items per library
- Declarative agent knowledge sources: items_by_url (up to 20 items)
- Declarative agent best practices: "Relevance over quantity" for knowledge sources
- Managed metadata: automatic crawled property creation for taxonomy fields

---

*Report Version: V1*
*Generated by CMS Watchdog Team - SharePoint Specialist Agent*
*Date: 2026-02-20*
