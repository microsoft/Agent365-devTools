# CMS Workspace - Architecture & Documentation Feedback Report V1

**Version:** V1
**Date:** 2026-02-20
**Reviewed by:** Claude Opus 4.6 (multi-agent review team)
**Scope:** Agent configurations, deployment docs, wiki, knowledge libraries, scripts, CI/CD

---

## 1. Overall Assessment

The CMS Knowledge Accelerator has thorough documentation with excellent architecture docs, a comprehensive wiki, and clear client handover materials. However, several documentation inconsistencies and stale references undermine reliability. The knowledge libraries have strong template design but the source-library is unpopulated.

**Rating: Good documentation foundation with critical consistency gaps**

---

## 2. Critical Findings

### CRIT-ARCH-01: Library Count Inconsistency Across Documentation
**Severity:** CRITICAL | **Locations:** Multiple docs and wiki pages

Documentation reports conflicting library entry counts:
- **48** entries referenced in one location
- **59** entries referenced in another
- **62** entries referenced in a third

This creates confusion about the actual state of the knowledge base and undermines trust in the documentation's accuracy.

**Recommendation:** Auto-generate counts from actual library contents. Add a script that updates documentation counts on each commit.

---

### CRIT-ARCH-02: agents/README.md References Non-Existent Agents
**Severity:** CRITICAL | **File:** `cms-knowledge-accelerator/agents/README.md`

The README references agents that have been:
- Deleted entirely
- Moved to the `deprecated/` folder
- Renamed or consolidated

Anyone onboarding from this README will encounter broken references and confusion about which agents are active.

**Recommendation:** Update README.md to reflect only currently active agents. Add a "Deprecated" section with clear notes about what replaced each deprecated agent.

---

### CRIT-ARCH-03: Three Agent Configuration Sources with No Single Source of Truth
**Severity:** CRITICAL | **Locations:**
- `CMS Knowledge Agents/` (top-level directory)
- `cms-knowledge-accelerator/agents/`
- `cms-knowledge-accelerator/agents/deprecated/`

Three separate locations contain agent JSON configurations with no clear indication of which is canonical. The top-level `CMS Knowledge Agents/` directory appears to be an older version of the configurations now in `cms-knowledge-accelerator/agents/`.

**Recommendation:**
1. Designate `cms-knowledge-accelerator/agents/` as the single source of truth
2. Archive or delete the top-level `CMS Knowledge Agents/` directory
3. Remove `deprecated/` agents entirely or move to a clearly separate archive

---

### CRIT-ARCH-04: Source Library Is Entirely Empty
**Severity:** CRITICAL | **Directory:** `source-library/`

The source-library has the correct template structure, SEARCH.md, and category directories, but contains **zero actual entries**. All cross-references from problem-library entries to sources point to non-existent files.

**Impact:** The knowledge graph is fundamentally incomplete. Any agent or user following source references from problem-library hits dead links.

**Recommendation:** Either populate source-library entries corresponding to referenced sources, or remove cross-references until population is complete.

---

## 3. High Priority Findings

### HIGH-ARCH-01: Missing MCP Server Deployment Documentation
**File:** `docs/DEPLOYMENT.md` or wiki equivalent

Deployment documentation covers agent configuration and SharePoint setup but lacks a dedicated section for MCP server deployment:
- Environment variable configuration for production
- Container image build and push process
- Azure Container Apps scaling configuration
- Azure Functions deployment slots and warm-up
- Secrets management (Key Vault integration)

### HIGH-ARCH-02: Broken Cross-Library References
**Files:** `problem-library/` entries referencing `source-library/`

Problem entries reference sources via identifiers or relative paths, but corresponding source entries don't exist. This creates a one-way reference system that appears broken.

### HIGH-ARCH-03: PowerShell Scripts Lack Consistent Error Handling
**Files:** `scripts/*.ps1`, `provisioning/**/*.ps1`

Scripts vary significantly:
- Some use `$ErrorActionPreference = 'Stop'`, others don't
- Not all scripts validate prerequisites
- Idempotency varies (some scripts fail on re-run)

### HIGH-ARCH-04: GitHub Workflows Reference Undocumented Secrets
**Files:** `.github/workflows/*.yml`

Workflow files reference secrets (Azure credentials, etc.) but there's no documentation of which secrets must be configured in repository settings.

### HIGH-ARCH-05: Wiki and docs/ May Diverge
**Locations:** `docs/` vs `cms-knowledge-accelerator.wiki/`

Documentation exists in both locations with no automated sync. Wiki versions appear longer and more detailed.

---

## 4. Medium Priority Findings

### MED-ARCH-01: Problem Library Has Empty Categories
Several categories have zero entries:
- `copilot-studio/`
- `devops-pipelines/`
- `general/`
- `terraform/`

If these are planned but not yet populated, they should be marked as such. If they're not needed, remove them.

### MED-ARCH-02: `map-search-properties.ps1` Not Prominently Documented
This script is critical for metadata search to function (configures SharePoint Managed Properties), but it's not called out in the main deployment guide.

### MED-ARCH-03: No Schema Validation for Problem Library Frontmatter
Problem entries use markdown with YAML frontmatter, but there's no schema validation to ensure required fields (category, status, severity, etc.) are present and correctly formatted.

### MED-ARCH-04: `upload_to_sharepoint.py` Is Orphaned
Sits at project root with unclear relationship to `config/dummy-data/convert_and_upload.py`. Contains hardcoded credentials (see Security Report).

---

## 5. Positive Observations

- **Architecture documentation is thorough** - Clear diagrams, deployment patterns, and design decisions
- **SOW vs Delivered comparison** - Excellent client transparency
- **Client handover guide** - Well-structured for knowledge transfer
- **Security documentation** - Comprehensive security posture description
- **Problem library template design** - Consistent frontmatter with clear categorisation
- **SEARCH.md files** - Useful discovery guidance for each library
- **Wiki depth** - Detailed supplementary content beyond the repo docs

---

## 6. Documentation Health Matrix

| Document Area | Accuracy | Completeness | Consistency | Priority |
|--------------|----------|-------------|-------------|----------|
| Architecture docs | Good | Good | Good | Low |
| Agent configs | Poor | Medium | Poor | Critical |
| Deployment docs | Good | Medium (missing MCP) | Medium | High |
| Knowledge libraries | Good | Poor (empty source-lib) | Medium | Critical |
| Wiki | Good | Good | Medium (sync risk) | Medium |
| Security docs | Good | Good | Good | Low |
| Script documentation | Medium | Medium | Poor | High |

---

## 7. Remediation Priority

| Priority | Finding | Effort |
|----------|---------|--------|
| Week 1 | CRIT-ARCH-02: Update agents/README.md | Low |
| Week 1 | CRIT-ARCH-03: Consolidate agent configs | Medium |
| Week 1 | CRIT-ARCH-01: Fix library count references | Low |
| Week 2-3 | HIGH-ARCH-01: Add MCP deployment docs | Medium |
| Week 2-3 | CRIT-ARCH-04: Populate source-library or fix references | High |
| Month 1 | HIGH-ARCH-03: Standardise PowerShell scripts | Medium |
| Month 1 | HIGH-ARCH-04: Document required secrets | Low |

---

*This is a V1 feedback report. Subsequent versions will track remediation progress and re-assess findings.*
