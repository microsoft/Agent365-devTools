---
tags: [prompt, review, analysis, fact-check]
description: Verify factual claims in a document with source citations
---

# Fact-Check Analysis

> **Usage**: Identify and verify all factual claims in a document.

## Task

Analyze the document and verify the accuracy of all factual claims.

## Analysis Steps

1. **Identify Factual Claims**
   - Extract all statements presented as facts
   - Distinguish facts from opinions/interpretations
   - Note the source (if any) the author provides

2. **Verify Each Claim**
   - Research using reliable external sources
   - Determine: Confirmed, Disputed, Unverifiable, or False
   - Document your sources

3. **Assess Impact**
   - How critical is each claim to the document's argument?
   - What are the implications if a claim is false?

## Output Format

```markdown
## Fact-Check Report

**Document**: [Title]
**Claims Analyzed**: [X]
**Verified**: [X] | **Disputed**: [X] | **Unverifiable**: [X] | **False**: [X]

### Claim Analysis

#### Claim 1: "[Quote the claim]"

- **Verdict**: [Confirmed/Disputed/Unverifiable/False]
- **Source**: [Citation]
- **Notes**: [Context or nuance]
- **Impact**: [High/Medium/Low] - [Why this matters]

#### Claim 2: ...

### Critical Findings

[Highlight any claims that significantly affect the document's credibility]

### Summary

[Overall assessment of factual accuracy]
```

---

**Last Updated:** December 3, 2025 by Greg Ratajik
