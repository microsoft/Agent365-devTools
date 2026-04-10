// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Defines all semantic check metadata for MCP tool schema evaluation.
/// Semantic checks require judgment (by a coding agent or human) and cannot be
/// evaluated deterministically. Each check produces a <see cref="ChecklistItem"/>
/// with <see cref="CheckType.Semantic"/> and a null Score that will be filled
/// during the evaluation phase.
///
/// Based on:
/// - 18-smell taxonomy: Li et al. (arXiv:2602.18914)
/// - 6-component framework: Hasan et al. (arXiv:2602.14878)
/// - TAFC parameter study: arXiv:2601.18282
/// </summary>
internal static class SemanticCheckDefinitions
{
    /// <summary>
    /// Returns the 10 tool-level semantic checks that evaluate naming quality
    /// and description completeness. These require semantic understanding to judge.
    /// </summary>
    /// <returns>A list of 10 semantic <see cref="ChecklistItem"/> instances with null scores.</returns>
    internal static List<ChecklistItem> GetToolLevelChecks()
    {
        return
        [
            new ChecklistItem
            {
                Id = "tn_verb_prefix",
                Type = CheckType.Semantic,
                Prompt = "Does the tool name start with (or clearly contain) an action verb? "
                       + "Action verbs include any word describing what the tool does "
                       + "(get, create, send, search, forward, reply, flag, deploy, lock, etc.). "
                       + "Pass if the first word or segment of the name is an action verb in any domain.",
                Score = null,
                Reason = null,
                Severity = Priority.P1,
                Category = CheckCategory.ToolName,
                SmellIds = [4, 18],
                ImpactAreas = [ImpactArea.ToolSelection],
                Remediation = "Rename to start with an action verb like get_, create_, search_, send_, etc.",
            },

            new ChecklistItem
            {
                Id = "tn_not_generic",
                Type = CheckType.Semantic,
                Prompt = "Is the tool name specific enough to distinguish it from other tools? "
                       + "Fail only for extremely vague names like 'run', 'execute', 'tool', 'process', 'action'. "
                       + "Domain-specific names like 'ForwardMessage' or 'SearchContacts' always pass.",
                Score = null,
                Reason = null,
                Severity = Priority.P1,
                Category = CheckCategory.ToolName,
                SmellIds = [4, 18],
                ImpactAreas = [ImpactArea.ToolSelection],
                Remediation = "Rename to describe the specific action and resource, e.g., 'search_contacts'.",
            },

            new ChecklistItem
            {
                Id = "tn_descriptive",
                Type = CheckType.Semantic,
                Prompt = "Does the tool name follow an action+subject pattern (e.g., 'GetUser', 'search_contacts')? "
                       + "Pass if the name contains both an action and what it acts on.",
                Score = null,
                Reason = null,
                Severity = Priority.P2,
                Category = CheckCategory.ToolName,
                SmellIds = [4, 18],
                ImpactAreas = [ImpactArea.ToolSelection],
                Remediation = "Use verb_noun pattern, e.g., 'get_user', 'search_documents', 'create_task'.",
            },

            new ChecklistItem
            {
                Id = "td_has_purpose",
                Type = CheckType.Semantic,
                Prompt = "Does the description clearly state what the tool does? "
                       + "Pass if reading the description tells you the tool's primary function.",
                Score = null,
                Reason = null,
                Severity = Priority.P0,
                Category = CheckCategory.ToolDescription,
                SmellIds = [4],
                ImpactAreas = [ImpactArea.ToolSelection],
                Remediation = "Start the description with a verb phrase: 'Retrieves...', 'Creates...', 'Searches for...'.",
            },

            new ChecklistItem
            {
                Id = "td_not_name_echo",
                Type = CheckType.Semantic,
                Prompt = "Does the description provide information beyond just restating the tool name? "
                       + "Fail if the description is essentially the tool name with minor filler words.",
                Score = null,
                Reason = null,
                Severity = Priority.P2,
                Category = CheckCategory.ToolDescription,
                SmellIds = [13],
                ImpactAreas = [ImpactArea.Conciseness],
                Remediation = "Rewrite the description to explain purpose, guidelines, and return values -- not just restate the name.",
            },

            new ChecklistItem
            {
                Id = "td_has_usage_guidelines",
                Type = CheckType.Semantic,
                Prompt = "Does the description explain when or how to use this tool? "
                       + "Pass if it mentions scenarios, conditions, or workflows where this tool is appropriate.",
                Score = null,
                Reason = null,
                Severity = Priority.P1,
                Category = CheckCategory.ToolDescription,
                SmellIds = [5],
                ImpactAreas = [ImpactArea.ToolSelection],
                Remediation = "Add a sentence like 'Use this when you need to...' or 'Useful for...'.",
            },

            new ChecklistItem
            {
                Id = "td_has_limitations",
                Type = CheckType.Semantic,
                Prompt = "Does the description mention any limitations, constraints, or things the tool cannot do? "
                       + "Pass if it states any boundary, restriction, or caveat.",
                Score = null,
                Reason = null,
                Severity = Priority.P2,
                Category = CheckCategory.ToolDescription,
                SmellIds = [6],
                ImpactAreas = [ImpactArea.ToolSelection, ImpactArea.Completeness],
                Remediation = "Add a sentence stating what the tool does NOT do or its constraints.",
            },

            new ChecklistItem
            {
                Id = "td_has_return_docs",
                Type = CheckType.Semantic,
                Prompt = "Does the description explain what the tool returns or produces? "
                       + "Pass if it mentions the output, response format, or what to expect back.",
                Score = null,
                Reason = null,
                Severity = Priority.P1,
                Category = CheckCategory.ToolDescription,
                SmellIds = [8],
                ImpactAreas = [ImpactArea.Completeness],
                Remediation = "Add 'Returns ...' describing the output format and content.",
            },

            new ChecklistItem
            {
                Id = "td_has_examples",
                Type = CheckType.Semantic,
                Prompt = "Does the description include usage examples, sample values, or illustrative patterns? "
                       + "Pass if there are concrete examples, 'e.g.' patterns, or sample inputs/outputs.",
                Score = null,
                Reason = null,
                Severity = Priority.P2,
                Category = CheckCategory.ToolDescription,
                SmellIds = [10],
                ImpactAreas = [ImpactArea.Completeness],
                Remediation = "Add examples: 'e.g., search_contacts(query=\"John\")' or 'For example, ...'.",
            },

            new ChecklistItem
            {
                Id = "td_no_boilerplate",
                Type = CheckType.Semantic,
                Prompt = "Is the description specific to this tool, not generic boilerplate? "
                       + "Fail if it starts with 'This is a tool that...' or uses generic filler without specific detail.",
                Score = null,
                Reason = null,
                Severity = Priority.P1,
                Category = CheckCategory.ToolDescription,
                SmellIds = [14],
                ImpactAreas = [ImpactArea.Conciseness],
                Remediation = "Remove generic phrases and replace with specific information about what this tool does.",
            },
        ];
    }

    /// <summary>
    /// Returns the 4 per-parameter semantic checks that evaluate naming quality
    /// and description completeness for a single parameter.
    /// </summary>
    /// <param name="paramName">The parameter name, used to customize prompt text and remediation advice.</param>
    /// <returns>A list of 4 semantic <see cref="ChecklistItem"/> instances with null scores.</returns>
    internal static List<ChecklistItem> GetParamLevelChecks(string paramName)
    {
        return
        [
            new ChecklistItem
            {
                Id = "pn_not_generic",
                Type = CheckType.Semantic,
                Prompt = $"Is the parameter name '{paramName}' specific enough in this tool's context? "
                       + "Fail only for truly uninformative names like 'x', 'val', 'data', 'input', 'arg'. "
                       + "Names like 'query', 'messageId', 'userId' are fine.",
                Score = null,
                Reason = null,
                Severity = Priority.P2,
                Category = CheckCategory.ParamName,
                SmellIds = [9, 1],
                ImpactAreas = [ImpactArea.ParamAccuracy],
                Remediation = $"Rename '{paramName}' to describe what it represents (e.g., 'user_id', 'search_query').",
            },

            new ChecklistItem
            {
                Id = "pd_not_name_echo",
                Type = CheckType.Semantic,
                Prompt = $"Does the description for parameter '{paramName}' provide more information than "
                       + "just restating the parameter name? Fail if the description is essentially the "
                       + "parameter name with minor filler words.",
                Score = null,
                Reason = null,
                Severity = Priority.P1,
                Category = CheckCategory.ParamDescription,
                SmellIds = [15],
                ImpactAreas = [ImpactArea.Conciseness, ImpactArea.ParamAccuracy],
                Remediation = $"Rewrite description for '{paramName}' to explain format, constraints, and purpose.",
            },

            new ChecklistItem
            {
                Id = "pd_has_constraints",
                Type = CheckType.Semantic,
                Prompt = $"Does the description or schema for parameter '{paramName}' mention constraints, "
                       + "valid values, format requirements, or limits? Pass if any form of constraint "
                       + "guidance is provided.",
                Score = null,
                Reason = null,
                Severity = Priority.P1,
                Category = CheckCategory.ParamDescription,
                SmellIds = [11],
                ImpactAreas = [ImpactArea.ParamAccuracy],
                Remediation = $"Add constraints to '{paramName}' schema (enum, min/max, pattern) or describe limits.",
            },

            new ChecklistItem
            {
                Id = "pd_enum_for_categorical",
                Type = CheckType.Semantic,
                Prompt = $"Does parameter '{paramName}' represent a finite set of choices "
                       + "(like status, type, priority, format)? If it looks categorical, "
                       + "does the schema define an enum with valid values? "
                       + "Pass if the parameter is not categorical, or if it is categorical and has an enum defined.",
                Score = null,
                Reason = null,
                Severity = Priority.P2,
                Category = CheckCategory.ParamDescription,
                SmellIds = [1],
                ImpactAreas = [ImpactArea.ParamAccuracy],
                Remediation = $"Add an 'enum' array to '{paramName}' listing all valid values.",
            },
        ];
    }

    /// <summary>
    /// Returns the 2 toolset-level semantic checks that evaluate cross-tool design quality.
    /// These examine the tool collection as a whole rather than individual tools.
    /// </summary>
    /// <returns>A list of 2 semantic <see cref="ChecklistItem"/> instances with null scores.</returns>
    internal static List<ChecklistItem> GetToolsetLevelChecks()
    {
        return
        [
            new ChecklistItem
            {
                Id = "ts_no_description_overlap",
                Type = CheckType.Semantic,
                Prompt = "Are there any pairs of tools whose descriptions are semantically so similar "
                       + "(>70% overlap) that an AI agent would be confused about which to use? "
                       + "Only flag genuinely overlapping pairs, not tools that operate on the same entity "
                       + "with different verbs. Pass if no significant description overlap exists.",
                Score = null,
                Reason = null,
                Severity = Priority.P1,
                Category = CheckCategory.ToolsetDesign,
                SmellIds = [17],
                ImpactAreas = [ImpactArea.ToolSelection],
                Remediation = "Differentiate overlapping tool descriptions. Clarify when to use each.",
            },

            new ChecklistItem
            {
                Id = "ts_crud_completeness",
                Type = CheckType.Semantic,
                Prompt = "For entities that have 2+ CRUD-like operations (create/read/update/delete), "
                       + "are there any missing operations that seem unintentional? "
                       + "Only flag entities where gaps appear unintentional. "
                       + "Pass if CRUD operations are complete or gaps are clearly intentional.",
                Score = null,
                Reason = null,
                Severity = Priority.P2,
                Category = CheckCategory.ToolsetDesign,
                SmellIds = [18],
                ImpactAreas = [ImpactArea.Completeness],
                Remediation = "Add missing CRUD operations or document why they're intentionally omitted.",
            },
        ];
    }
}
