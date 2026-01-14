---
tags: [strategy, guide, api, patterns, methodology]
description: Hypothesis-driven approach to breaking complex questions into testable sub-questions
---

# The Hypothesis-Driven Question Decomposition Method

> **TL;DR**: Decompose questions like a scientist - generate hypotheses about structure, test with MECE/synthesis criteria, iterate based on findings.

## Core Principle: Decomposition as Theory Building

Question decomposition is fundamentally an act of theory building. When you decompose a complex question, you're proposing a hypothesis about:

1. **The question's essential structure** - What are its fundamental components?
2. **The knowledge architecture** - How do different aspects relate to each other?
3. **The synthesis pathway** - How will sub-answers combine to form the complete answer?
4. **The investigation sequence** - What order of exploration will be most effective?

This hypothesis can and should be tested, refined, and revised as you progress through the decomposition and investigation process.

## The Decomposition Hypothesis Framework

### Step 1: Generate Initial Decomposition Theory

When facing a complex question, develop an initial theory about its structure:

**Example Central Question:**
"How do we build minimal cognitive agents that maximize LLM capabilities while escaping the orchestration trap?"

**Initial Decomposition Theory:**
"This question likely decomposes along three axes:

1. **Problem axis**: Understanding what the orchestration trap is and why it matters
2. **Solution axis**: Defining what 'minimal' means and how it enables capability
3. **Implementation axis**: Demonstrating how to build such agents in practice"

This theory generates a testable hypothesis about the question's structure.

### Step 2: Apply Decomposition Patterns

Different question types suggest different decomposition patterns. Use these as hypothesis generators:

#### Pattern 1: Component Analysis

**Theory**: "This question can be understood by examining its constituent parts"

- Identify key concepts (minimal, cognitive, agents, LLM capabilities, orchestration trap)
- Hypothesize relationships between concepts
- Test: Do these components cover all aspects of the question?

#### Pattern 2: Process Analysis

**Theory**: "This question involves a sequence or workflow"

- Map potential process steps
- Identify dependencies and sequences
- Test: Does following this process lead to the answer?

#### Pattern 3: Tension Resolution

**Theory**: "This question contains inherent tensions that must be resolved"

- Identify opposing forces (minimalism vs capability, flexibility vs structure)
- Hypothesize resolution strategies
- Test: Do these resolutions address the core challenge?

#### Pattern 4: Stakeholder Perspectives

**Theory**: "Different audiences need different aspects of this answer"

- Map stakeholder groups (researchers, practitioners, framework developers)
- Identify their specific needs
- Test: Does addressing each perspective provide complete coverage?

### Step 3: Test the Decomposition Hypothesis

A decomposition hypothesis must pass several tests:

#### MECE Test (Mutually Exclusive, Collectively Exhaustive)

- **Mutual Exclusivity**: Do sub-questions overlap significantly?
- **Collective Exhaustion**: Do sub-questions cover all aspects?
- **Failure Response**: Merge overlapping questions, add missing dimensions

#### Synthesis Viability Test

- **Question**: Can the sub-answers logically combine to answer the parent?
- **Method**: Mentally simulate how sub-answers would integrate
- **Failure Response**: Restructure to ensure clean synthesis paths

#### Actionability Test

- **Question**: Can each sub-question be answered with available methods?
- **Method**: Identify how you would investigate each sub-question
- **Failure Response**: Further decompose abstract questions

#### Independence Test

- **Question**: Can sub-questions be answered independently?
- **Method**: Check for circular dependencies
- **Failure Response**: Reorder or restructure to break dependencies

### Step 4: Iterate Based on Validation

Decomposition is inherently iterative. As you investigate sub-questions, you discover:

1. **Missing Questions**: Gaps in your original theory
2. **Wrong Abstractions**: Questions that don't carve reality at its joints
3. **Hidden Dependencies**: Connections you didn't initially see
4. **Emergent Patterns**: Higher-level structures that suggest better organization

## Decomposition Theory Templates

### For "How" Questions (Process/Method Focus)

**Initial Theory Structure:**

1. What is the current state/problem?
2. What is the desired state/solution?
3. What steps connect current to desired?
4. What principles guide the transition?
5. How do we validate success?

**Example Applied:**
"How do we build minimal cognitive agents?"

1. Current state: Orchestration-heavy frameworks
2. Desired state: Minimal agents maximizing LLM capability
3. Steps: Remove orchestration, implement intent-based design, add protocols
4. Principles: Model maximalism, code minimalism
5. Validation: Working implementation, measurable benefits

### For "What" Questions (Definition/Classification Focus)

**Initial Theory Structure:**

1. What are the essential characteristics?
2. What are the boundaries/non-examples?
3. What categories or types exist?
4. What relationships connect to other concepts?
5. What implications follow from this definition?

### For "Why" Questions (Causal/Justification Focus)

**Initial Theory Structure:**

1. What is the phenomenon requiring explanation?
2. What are the proposed causal factors?
3. What evidence supports each factor?
4. How do factors interact?
5. What alternative explanations exist?

## Advanced Decomposition Techniques

### The Recursive Decomposition Test

For each proposed sub-question, ask:

1. Could this sub-question itself be decomposed?
2. If yes, does that decomposition reveal flaws in the parent decomposition?
3. Should we elevate some sub-sub-questions to the parent level?

### The Synthesis Simulation

Before committing to a decomposition:

1. Imagine you have perfect answers to all sub-questions
2. Mentally combine them to answer the parent question
3. Identify gaps or awkward combinations
4. Revise decomposition to enable cleaner synthesis

### The Expert Review Test

Present your decomposition theory to domain experts:

1. What questions are they surprised to see?
2. What questions do they expect that are missing?
3. How would they structure the decomposition differently?
4. What implicit assumptions does your decomposition make?

## Common Decomposition Failures and Fixes

### Failure: Level Confusion

**Symptom**: Mixing different levels of abstraction in the same tier
**Fix**: Apply consistent abstraction level tests to each tier

### Failure: Hidden Assumptions

**Symptom**: Decomposition only works given unstated assumptions
**Fix**: Make assumptions explicit and test robustness without them

### Failure: Solution Bias

**Symptom**: Decomposition assumes a particular answer
**Fix**: Ensure questions allow for multiple valid answers

### Failure: Incomplete Coverage

**Symptom**: Important aspects only emerge during investigation
**Fix**: Use multiple decomposition patterns and cross-validate

## The Meta-Theory of Question Decomposition

Good decomposition theories share characteristics:

1. **Testability**: You can determine if the decomposition works
2. **Revisability**: The structure can evolve as understanding deepens
3. **Clarity**: The logic is transparent and communicable
4. **Completeness**: All aspects of the original question are addressed
5. **Efficiency**: The structure minimizes redundancy and maximizes insight

## Practical Decomposition Workflow

### Phase 1: Theory Generation (Divergent)

1. Generate multiple decomposition hypotheses
2. Use different patterns and perspectives
3. Don't judge quality yet - quantity matters
4. Document the reasoning behind each theory

### Phase 2: Theory Testing (Convergent)

1. Apply validation tests to each hypothesis
2. Identify strengths and weaknesses
3. Combine best elements from different theories
4. Document why certain approaches were rejected

### Phase 3: Theory Refinement (Iterative)

1. Begin investigating with your best theory
2. Track surprises and gaps as they emerge
3. Revise the decomposition based on findings
4. Document the evolution of your theory

### Phase 4: Theory Validation (Final)

1. Confirm all sub-questions have been answered
2. Verify synthesis produces complete answer
3. Test with stakeholders/experts
4. Document lessons for future decompositions

## Case Study: Applying Hypothesis-Driven Decomposition

**Central Question**: "How do we integrate multidisciplinary human knowledge into AI systems capable of achieving superintelligence?"

### Theory 1: Discipline-Centric Decomposition

**Hypothesis**: "Each discipline offers unique insights that can be systematically extracted and integrated"

**Proposed Structure**:

- Part I: Knowledge extraction from each discipline
- Part II: Translation to computational form
- Part III: Integration strategies
- Part IV: Scaling to superintelligence

**Testing Results**:

- ✓ MECE: Good coverage, minimal overlap
- ✗ Synthesis: Unclear how discipline-specific insights combine
- ✓ Actionable: Each discipline can be researched
- ✗ Independence: Integration depends on extraction format

### Theory 2: Capability-Centric Decomposition

**Hypothesis**: "Superintelligence requires specific capabilities that draw from multiple disciplines"

**Proposed Structure**:

- Part I: Reasoning capabilities (logic, philosophy, mathematics)
- Part II: Learning capabilities (psychology, neuroscience, education)
- Part III: Social capabilities (sociology, economics, anthropology)
- Part IV: Integration and scaling

**Testing Results**:

- ✗ MECE: Some disciplines contribute to multiple capabilities
- ✓ Synthesis: Clear how capabilities combine
- ✓ Actionable: Can research each capability domain
- ✓ Independence: Capabilities can be developed separately

### Theory 3: Problem-Centric Decomposition (Refined)

**Hypothesis**: "The integration challenge has distinct problem types that each require multidisciplinary solutions"

**Proposed Structure**:

- Part I: The knowledge representation problem (What formats work across disciplines?)
- Part II: The translation problem (How do we formalize human insights?)
- Part III: The integration problem (How do we combine diverse knowledge types?)
- Part IV: The scaling problem (How do we grow toward superintelligence?)

**Testing Results**:

- ✓ MECE: Problems are distinct and comprehensive
- ✓ Synthesis: Solutions to each problem combine naturally
- ✓ Actionable: Each problem has clear research paths
- ✓ Independence: Can tackle problems in parallel

**Conclusion**: Theory 3 provides the most robust decomposition hypothesis.

## Integration with Original Question-Oriented Method

This hypothesis-driven approach enhances the original method by:

1. **Making decomposition rigorous**: Not just breaking down, but theory building
2. **Enabling systematic improvement**: Clear tests and iteration paths
3. **Reducing arbitrariness**: Principled reasons for structure choices
4. **Improving robustness**: Decompositions that survive investigation
5. **Facilitating collaboration**: Explicit theories can be debated and refined

The original question-oriented method provides the overall framework; the hypothesis-driven decomposition provides the scientific rigor for creating and validating that framework's structure.

## Summary

Question decomposition is theory building. Treat it as such:

1. **Generate hypotheses** about question structure using multiple patterns
2. **Test rigorously** using MECE, synthesis, actionability, and independence criteria
3. **Iterate based on findings** as investigation reveals new insights
4. **Validate the final structure** through synthesis and expert review
5. **Document the evolution** to improve future decomposition efforts

This approach transforms decomposition from an art into a science, providing reproducible methods for tackling complex questions while maintaining the flexibility to evolve as understanding deepens.

## Related Concepts

### Prerequisites

- [[thinking_question_knowledge]] - Need to understand question-oriented approach before learning rigorous decomposition methodology

### Related Topics

- [[thinking_first_principles]] - Decomposition applies first principles thinking to breaking questions into fundamental components
- [[thinking_strategy_critical_assessment]] - Critical assessment validates decomposition quality through MECE and synthesis tests
- [[thinking_question_content_synthesis]] - Content synthesis uses decomposition methodology to create question hierarchies that are then filled with educational content

### Extends

- [[thinking_question_knowledge]] - Adds scientific rigor and hypothesis-driven validation to question decomposition process

---

**Last Updated:** December 3, 2025