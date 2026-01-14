---
tags: [prompt, review, analysis, summary]
description: Create a concise summary capturing main points, arguments, evidence, and conclusions
---

# Document Summarization

> **Usage**: Create a comprehensive yet concise summary of a document.

## Task

Provide a thorough summary that captures the document's essence.

## Analysis Steps

1. **Identify Core Elements**
   - Main purpose/thesis
   - Key arguments or points
   - Supporting evidence
   - Conclusions drawn

2. **Determine Hierarchy**
   - What's most important?
   - What's supporting detail?
   - What can be omitted?

3. **Preserve Intent**
   - Maintain author's original meaning
   - Don't inject interpretation
   - Keep context intact

## Output Format

```markdown
## Summary

### One-Sentence Summary

[Single sentence capturing the document's core message]

### Key Points

1. **[Point 1]**: [Brief explanation]
2. **[Point 2]**: [Brief explanation]
3. **[Point 3]**: [Brief explanation]

### Main Arguments

- [Argument]: [Supporting evidence in brief]

### Conclusions

[What the author concludes or recommends]

### Context

- **Audience**: [Who this is for]
- **Purpose**: [Why it was written]
- **Scope**: [What it covers/doesn't cover]

---

**Document Stats**
- Original length: [X words/pages]
- Summary length: [X words]
- Compression ratio: [X%]
```

## Summary Length Options

- **Brief** (1-2 paragraphs): Core thesis + 3 key points
- **Standard** (1 page): All major points with brief evidence
- **Detailed** (2-3 pages): Comprehensive with key quotes

Specify desired length when invoking.

---

**Last Updated:** December 3, 2025 by Greg Ratajik
