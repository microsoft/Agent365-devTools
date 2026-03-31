# CMS Knowledge Accelerator - Alternative Approaches Research V1

**Report ID:** CMS-ALT-V1
**Date:** 2026-02-20
**Researcher:** CMS Watchdog Team - Technology Researcher Agent
**Scope:** Research into alternative and complementary approaches for knowledge agent design
**Classification:** INTERNAL

---

## Executive Summary

The current architecture (declarative agent + SharePoint grounding + MCP server) is fundamentally sound and aligned with Microsoft's strategic direction. Several complementary technologies could significantly improve retrieval quality, reduce maintenance burden, and enhance user experience. Top three recommendations: **Azure AI Search** for hybrid retrieval, **Copilot Tuning** for legal terminology, and **built-in Agent Evaluation** for automated testing.

---

## Technologies Evaluated

### 1. Azure AI Search (Hybrid Retrieval) -- STRONGLY RECOMMENDED

**What it is:** Enterprise-grade search with keyword + vector + semantic ranking. Can be added as a Copilot Studio knowledge source alongside SharePoint grounding.

**How it would work:**
1. Index SharePoint content with chunking and vector embeddings
2. Use Azure OpenAI embeddings for vectorization
3. Enable semantic ranker for re-ranking
4. Connect as Copilot Studio knowledge source

| Aspect | Detail |
|--------|--------|
| Pros | Hybrid search outperforms native SharePoint for legal docs. Proper chunking for large documents. Vector search finds conceptually similar content without keyword matches. |
| Cons | Cost: S1 ~GBP 200/month + embeddings. SP Online indexer in preview. Added infrastructure. Data staleness risk. |
| Effort | 2-3 weeks |
| Impact | VERY HIGH -- step change in retrieval quality |
| Verdict | **Phase 2 priority #1** |

### 2. Copilot Tuning (Legal Terminology) -- RECOMMENDED

**What it is:** Low-code fine-tuning of LLMs with company data for domain-specific terminology, tone, and relevance.

| Aspect | Detail |
|--------|--------|
| Pros | Teaches model CMS legal terminology (LMA, facility agreement, clause types). Zero architecture change. |
| Cons | Preview feature. Requires curated training data. |
| Effort | 1-2 weeks |
| Impact | HIGH -- accuracy improvement with minimal effort |
| Verdict | **Phase 2 priority #2** |

### 3. Built-in Agent Evaluation -- STRONGLY RECOMMENDED

**What it is:** Copilot Studio automated evaluation (public preview) with AI-powered grading: relevance, completeness, groundedness scores.

| Aspect | Detail |
|--------|--------|
| Pros | Replaces manual 81-question testing. Enables rapid iteration. Built-in scoring. |
| Cons | Preview feature. May not match legal domain evaluation nuance. |
| Effort | 1 week |
| Impact | HIGH -- enables evaluation-driven development |
| Verdict | **Implement immediately** |

### 4. SharePoint Premium (Document Processing) -- RECOMMENDED

**What it is:** AI-powered content processing: automatic metadata extraction, classification, summarisation, enhanced search.

| Aspect | Detail |
|--------|--------|
| Pros | Auto-classifies documents. Auto-generates summaries. Reduces KM manual tagging. Free tier through June 2026. |
| Cons | Per-document cost after free tier. Classification accuracy for legal docs needs validation. |
| Effort | 2-3 weeks |
| Impact | MEDIUM-HIGH |
| Verdict | **Phase 2 for metadata enrichment** |

### 5. Microsoft Graph Connectors -- PARK FOR FUTURE

**What it is:** Index external data into Microsoft Graph for native Copilot reasoning with security trimming.

| Aspect | Detail |
|--------|--------|
| Pros | Brings in knowledge from iManage, practice management, matter databases. Native M365 security. |
| Cons | CMS knowledge already in SharePoint. Custom development required. |
| Effort | 3-4 weeks |
| Impact | LOW (current scope) |
| Verdict | **Future consideration for non-SharePoint sources** |

### 6. Multi-Agent Orchestration -- NOT NOW

**What it is:** Parent agent routes to specialized child agents based on intent (Build 2025).

| Aspect | Detail |
|--------|--------|
| Pros | Better per-area prompt engineering. Add specialists without modifying main agent. |
| Cons | Complexity. Routing mistakes. New in Copilot Studio. Overkill for 2 areas. |
| Effort | 2-3 weeks |
| Impact | LOW (2 areas), HIGH (5+ areas) |
| Verdict | **Design for future evolution, don't implement now** |

### 7. GraphRAG (Knowledge Graphs) -- ASPIRATIONAL

**What it is:** Microsoft open-source combining vector search with knowledge graphs. Legal documents have natural citation relationships.

| Aspect | Detail |
|--------|--------|
| Pros | Precision up to 99%. Captures legal citation relationships. |
| Cons | Complex. Requires knowledge graph construction. Significant engineering. |
| Effort | 4-6 weeks |
| Impact | HIGH but complex |
| Verdict | **Phase 3 aspirational** |

### 8. Microsoft Agents SDK (Custom Engine) -- NOT APPROPRIATE

**What it is:** Full custom engine agent with own AI model, multi-channel deployment, complex orchestration.

| Aspect | Detail |
|--------|--------|
| Pros | Full model control. Multi-channel. Complex reasoning pipelines. |
| Cons | Massive scope increase. Hosting costs. Overkill for knowledge retrieval. |
| Effort | 6-8 weeks |
| Impact | HIGH but massive scope |
| Verdict | **Not for current scope.** Revisit for custom fine-tuned legal model or non-M365 deployment. |

---

## Competitor Landscape

### Harvey AI (Allen & Overy, PwC)
- Custom-trained case law models (fine-tuned on all U.S. case law)
- Multi-agent pipeline for knowledge ingestion
- Hallucination detection: decomposes responses into individual factual claims and cross-references each
- Scaled from 6 to 60+ jurisdictions

### DeepJudge
- Focuses on unlocking knowledge trapped inside law firms' own work product
- True advantage comes from proprietary data, not public data

### Industry Trends (2025/2026)
- 79% of legal professionals now use AI (Clio Legal Trends 2025)
- Big law firms building internal LLM fine-tuning strategies
- Context (playbooks, precedents, templates) is the main value driver
- Human oversight remains central

### CMS Could Adopt
- **Hallucination mitigation** through better citation requirements and confidence scoring
- **Automated knowledge health checking** (currency tool is a start)
- **Copilot Tuning** as lightweight alternative to full fine-tuning

---

## Recommended Architecture (If Starting From Scratch)

```
                    Copilot Studio Agent (Unified)
                            |
            +---------------+---------------+
            |               |               |
   SharePoint Grounding  Azure AI Search   MCP Server
   (site-level URL)      (Hybrid RAG)      (Specialized Tools)
            |               |               |
            +-------+-------+               |
                    |                       |
             SharePoint Online        Microsoft Graph
            (Document Repository)    (Metadata, Analytics)
```

---

## Priority Roadmap

| Priority | Enhancement | Effort | Impact | Phase |
|----------|-------------|--------|--------|-------|
| 1 | Agent Evaluation (automated testing) | 1 week | High | Now |
| 2 | Copilot Tuning for legal terminology | 1-2 weeks | High | Phase 2 |
| 3 | Azure AI Search (hybrid retrieval) | 2-3 weeks | Very High | Phase 2 |
| 4 | SharePoint Premium for auto-metadata | 2-3 weeks | Medium-High | Phase 2 |
| 5 | MCP server tool refinement | 1 week | Medium | Phase 2 |
| 6 | Multi-agent orchestration | 2-3 weeks | Low (now) | Phase 3 |
| 7 | GraphRAG | 4-6 weeks | High | Phase 3 |

---

*Report Version: V1*
*Generated by CMS Watchdog Team - Technology Researcher Agent*
*Date: 2026-02-20*
