---
tags: [prompt, prd, design, requirements]
description: Generate a design document (PRD) through guided questions
---

# Rule: Generating a Design Document

> **Usage**: Invoke this prompt to create a PRD. Ask clarifying questions first, then generate the document.

## Prerequisites

**FIRST**: Check `.github/copilot/project-config.md` for feature toggles. This affects:

- **Azure DevOps Integration**: If ✅ ON, PRD may reference ADO work items
- **GitHub Projects**: If ✅ ON, PRD may reference GitHub issues

## Goal

To guide an AI assistant in creating a detailed design document in Markdown format, based on an initial user prompt. The design document should combine product requirements with architectural decisions, and be clear, actionable, and suitable for a junior developer to understand and implement the feature.

## Process

1.  **Receive Initial Prompt:** The user provides a brief description or request for a new feature or functionality.
2.  **Ask Clarifying Questions:** Before writing the design document, the AI *must* ask clarifying questions to gather sufficient detail. The goal is to understand the "what" and "why" of the feature, not necessarily the "how" (which the developer will figure out).
3.  **Generate Design Document:** Based on the initial prompt and the user's answers to the clarifying questions, generate a design document using the structure outlined below.
4.  **Save Design Document:** Save the generated document as `design.md` inside the `/docs/features/[feature-name]/` directory. Create the feature directory if it doesn't exist.

## Clarifying Questions (Examples)

The AI should adapt its questions based on the prompt, but here are some common areas to explore:

- **Problem/Goal:** "What problem does this feature solve for the user?" or "What is the main goal we want to achieve with this feature?"
- **Target User:** "Who is the primary user of this feature?"
- **Core Functionality:** "Can you describe the key actions a user should be able to perform with this feature?"
- **User Stories:** "Could you provide a few user stories? (e.g., As a [type of user], I want to [perform an action] so that [benefit].)"
- **Acceptance Criteria:** "How will we know when this feature is successfully implemented? What are the key success criteria?"
- **Scope/Boundaries:** "Are there any specific things this feature *should not* do (non-goals)?"
- **Data Requirements:** "What kind of data does this feature need to display or manipulate?"
- **Design/UI:** "Are there any existing design mockups or UI guidelines to follow?" or "Can you describe the desired look and feel?"
- **Edge Cases:** "Are there any potential edge cases or error conditions we should consider?"

## Design Document Structure

The generated design document should include the following sections:

1. **Introduction/Overview:** Briefly describe the feature and the problem it solves. State the goal.
2. **Goals:** List the specific, measurable objectives for this feature.
3. **User Stories:** Detail the user narratives describing feature usage and benefits.
4. **Functional Requirements:** List the specific functionalities the feature must have. Use clear, concise language (e.g., "The system must allow users to upload a profile picture."). Number these requirements.
5. **Non-Goals (Out of Scope):** Clearly state what this feature will *not* include to manage scope.
6. **Design Considerations (Optional):** Link to mockups, describe UI/UX requirements, or mention relevant components/styles if applicable.
7. **Technical Considerations (Optional):** Mention any known technical constraints, dependencies, or suggestions (e.g., "Should integrate with the existing Auth module").
8. **Success Metrics:** How will the success of this feature be measured? (e.g., "Increase user engagement by 10%", "Reduce support tickets related to X").
9. **Open Questions:** List any remaining questions or areas needing further clarification.

## Target Audience

Assume the primary reader of the design document is a **junior developer**. Therefore, requirements should be explicit, unambiguous, and avoid jargon where possible. Provide enough detail for them to understand the feature's purpose and core logic.

**Interaction Style:** Ask clarifying questions one at a time, then move to the next. Refine the design document based on user feedback.

## Output

- **Format:** Markdown (`.md`)
- **Location:** `/docs/features/[feature-name]/`
- **Filename:** `design.md`

**Note:** Per `.github/copilot/task-workflow.md`, this design.md file must be updated during implementation when architecture decisions are made. Add "Last Updated" timestamps to modified sections.

## Final Instructions

1. Do NOT start implementing the PRD
2. Ask the user clarifying questions first
3. Generate the PRD based on answers
4. Refine based on user feedback

## Next Step

Once the PRD is complete, use `generate-tasks.prompt.md` to create the implementation task list.

---

**Last Updated:** December 3, 2025 by gregrata
