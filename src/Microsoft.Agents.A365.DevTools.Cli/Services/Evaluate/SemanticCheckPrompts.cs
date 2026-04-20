// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Provides structured prompt templates for invoking a coding agent (Claude Code
/// or GitHub Copilot) to evaluate semantic checks in an MCP tool schema checklist.
///
/// The generated prompt instructs the agent to:
/// 1. Read the checklist JSON file.
/// 2. Evaluate each item where <c>score</c> is <c>null</c>.
/// 3. Set <c>score</c> to <c>true</c> (pass) or <c>false</c> (fail) with a 1-sentence <c>reason</c>.
/// 4. Leave items where <c>score</c> is already set (deterministic checks) unchanged.
/// 5. Write the updated JSON back to the same file, preserving all other fields.
/// </summary>
internal static class SemanticCheckPrompts
{
    /// <summary>
    /// Builds the full evaluation prompt that a coding agent will receive.
    /// The prompt describes the context, evaluation guidelines, JSON structure,
    /// and concrete examples of good and bad evaluations.
    /// </summary>
    /// <param name="checklistPath">Absolute path to the checklist JSON file to evaluate.</param>
    /// <returns>A self-contained prompt string ready to pass to a coding agent CLI.</returns>
    public static string BuildEvaluationPrompt(string checklistPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checklistPath);

        var sb = new StringBuilder();

        sb.AppendLine("You are evaluating an MCP (Model Context Protocol) tool schema for quality.");
        sb.AppendLine("An MCP server exposes tools that AI agents call. Poor tool names, descriptions,");
        sb.AppendLine("or parameter schemas cause agents to select the wrong tool or pass incorrect arguments.");
        sb.AppendLine();

        AppendInstructions(sb, checklistPath);
        AppendJsonStructure(sb);
        AppendEvaluationGuidelines(sb);
        AppendExamples(sb);
        AppendFinalRules(sb);

        return sb.ToString();
    }

    /// <summary>
    /// Describes the tools an agent is allowed to use. Embedded into the prompt so the
    /// agent doesn't have to guess what's available and doesn't pick a strategy that
    /// will silently fail (e.g. many small string-replace edits that can't disambiguate
    /// repeated patterns).
    /// </summary>
    public sealed record AgentToolset(string ReadToolName, string WriteToolName, string? EditToolName = null);

    /// <summary>
    /// Builds a prompt for evaluating a single tool's semantic checks.
    /// The file contains just one tool object (not the full checklist).
    /// </summary>
    public static string BuildToolEvaluationPrompt(string toolFilePath, string toolName, AgentToolset toolset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(toolset);

        var sb = new StringBuilder();

        sb.AppendLine("You are evaluating an MCP tool schema for quality.");
        sb.AppendLine();
        AppendToolsetHeader(sb, toolset);
        sb.AppendLine("TASK:");
        sb.AppendLine($"1. Use `{toolset.ReadToolName}` to read the JSON file at: {toolFilePath}");
        sb.AppendLine($"   It contains a single tool named \"{toolName}\" with its schema and checks.");
        sb.AppendLine("2. For every checklist item in the tool's \"checks\" where \"score\" is null,");
        sb.AppendLine("   evaluate the \"prompt\" against the tool's name, description, and input_schema.");
        sb.AppendLine("3. Set \"score\" to true (pass) or false (fail).");
        sb.AppendLine("4. Set \"reason\" to a single sentence explaining your judgment.");
        sb.AppendLine("5. Do NOT modify items where \"score\" is already set (true or false).");
        AppendWriteStrategy(sb, toolset);
        sb.AppendLine("7. Preserve exact JSON formatting: 2-space indentation, UTF-8 encoding.");
        sb.AppendLine();

        AppendEvaluationGuidelines(sb);
        AppendExamples(sb);
        AppendFinalRules(sb);

        return sb.ToString();
    }

    /// <summary>
    /// Builds a prompt for evaluating server-level checks.
    /// The file contains tool summaries and server_checks array.
    /// </summary>
    public static string BuildServerChecksEvaluationPrompt(string serverChecksFilePath, AgentToolset toolset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverChecksFilePath);
        ArgumentNullException.ThrowIfNull(toolset);

        var sb = new StringBuilder();

        sb.AppendLine("You are evaluating an MCP server's toolset design for quality.");
        sb.AppendLine();
        AppendToolsetHeader(sb, toolset);
        sb.AppendLine("TASK:");
        sb.AppendLine($"1. Use `{toolset.ReadToolName}` to read the JSON file at: {serverChecksFilePath}");
        sb.AppendLine("   It contains \"tool_summaries\" (list of tool names and descriptions)");
        sb.AppendLine("   and \"server_checks\" (checklist items to evaluate).");
        sb.AppendLine("2. For every item in \"server_checks\" where \"score\" is null,");
        sb.AppendLine("   evaluate the \"prompt\" against the full set of tools.");
        sb.AppendLine("3. Set \"score\" to true (pass) or false (fail).");
        sb.AppendLine("4. Set \"reason\" to a single sentence explaining your judgment.");
        sb.AppendLine("5. Do NOT modify items where \"score\" is already set (true or false).");
        AppendWriteStrategy(sb, toolset);
        sb.AppendLine("7. Preserve exact JSON formatting: 2-space indentation, UTF-8 encoding.");
        sb.AppendLine();

        sb.AppendLine("EVALUATION GUIDELINES:");
        sb.AppendLine();
        sb.AppendLine("For TOOLSET checks (category: \"ToolsetDesign\"):");
        sb.AppendLine("  - Evaluate cross-tool consistency and completeness.");
        sb.AppendLine("  - Check for tools with semantically overlapping descriptions (>70% similar).");
        sb.AppendLine("  - Check for incomplete CRUD coverage that seems unintentional.");
        sb.AppendLine("  - Only flag genuinely problematic patterns, not minor style differences.");
        sb.AppendLine();

        AppendFinalRules(sb);

        return sb.ToString();
    }

    private static void AppendToolsetHeader(StringBuilder sb, AgentToolset toolset)
    {
        sb.AppendLine("AVAILABLE TOOLS (use only these):");
        sb.AppendLine($"  - `{toolset.ReadToolName}` — read a file.");
        sb.AppendLine($"  - `{toolset.WriteToolName}` — write a file (overwrites existing). USE THIS to save your updates.");
        if (!string.IsNullOrEmpty(toolset.EditToolName))
        {
            sb.AppendLine($"  - `{toolset.EditToolName}` — targeted string replacement. AVOID for this task");
            sb.AppendLine("    (the repeating \"score\": null pattern is not unique, so replacements fail).");
        }
        sb.AppendLine("  No other tools (shell, web, etc.) are available.");
        sb.AppendLine();
    }

    private static void AppendWriteStrategy(StringBuilder sb, AgentToolset toolset)
    {
        sb.AppendLine("6. WRITE STRATEGY (important — choose correctly):");
        sb.AppendLine($"   Compute all updates in one pass, then call `{toolset.WriteToolName}` ONCE with the full");
        sb.AppendLine("   updated JSON to overwrite the file. Do not make multiple small edits — the");
        sb.AppendLine("   repeating `\"score\": null, \"reason\": null` pattern is not unique across items,");
        sb.AppendLine("   so string replacements will fail and leave checks unscored.");
    }

    private static void AppendInstructions(StringBuilder sb, string checklistPath)
    {
        sb.AppendLine("TASK:");
        sb.AppendLine($"1. Read the JSON file at: {checklistPath}");
        sb.AppendLine("2. For every checklist item where \"score\" is null, evaluate the \"prompt\" field");
        sb.AppendLine("   against the tool schema included in the same JSON file.");
        sb.AppendLine("3. Set \"score\" to true (pass) or false (fail).");
        sb.AppendLine("4. Set \"reason\" to a single sentence explaining your judgment.");
        sb.AppendLine("5. Do NOT modify any item where \"score\" is already set (true or false).");
        sb.AppendLine("   Those are deterministic checks that have already been evaluated.");
        sb.AppendLine("6. Do NOT modify any other fields (id, type, severity, category, issue_ids,");
        sb.AppendLine("   impact_areas, remediation, prompt).");
        sb.AppendLine("7. Write the updated JSON back to the SAME file path.");
        sb.AppendLine("8. Preserve the exact JSON formatting: 2-space indentation, UTF-8 encoding.");
        sb.AppendLine();
    }

    private static void AppendJsonStructure(StringBuilder sb)
    {
        sb.AppendLine("JSON STRUCTURE:");
        sb.AppendLine("The file is an EvaluationChecklist with this shape:");
        sb.AppendLine("  {");
        sb.AppendLine("    \"metadata\": { \"server_name\": \"...\", \"tool_count\": N, ... },");
        sb.AppendLine("    \"tools\": [");
        sb.AppendLine("      {");
        sb.AppendLine("        \"name\": \"tool_name\",");
        sb.AppendLine("        \"description\": \"tool description text\",");
        sb.AppendLine("        \"input_schema\": { ... JSON Schema ... },");
        sb.AppendLine("        \"checks\": {");
        sb.AppendLine("          \"tool_name\": [ { \"id\": \"...\", \"score\": null, \"prompt\": \"...\", ... } ],");
        sb.AppendLine("          \"tool_description\": [ ... ],");
        sb.AppendLine("          \"schema_structure\": [ ... ],");
        sb.AppendLine("          \"parameters\": {");
        sb.AppendLine("            \"<parameterName>\": {");
        sb.AppendLine("              \"param_name\": [ ... ],");
        sb.AppendLine("              \"param_description\": [ ... ]");
        sb.AppendLine("            }");
        sb.AppendLine("          }");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    ],");
        sb.AppendLine("    \"server_checks\": [ { \"id\": \"...\", \"score\": null, \"prompt\": \"...\", ... } ]");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine("Each checklist item has:");
        sb.AppendLine("  - \"type\": \"Deterministic\" or \"Semantic\"");
        sb.AppendLine("  - \"score\": true, false, or null (null = needs your evaluation)");
        sb.AppendLine("  - \"reason\": null or a string (set this when you set score)");
        sb.AppendLine("  - \"prompt\": the question to evaluate against the tool schema");
        sb.AppendLine();
    }

    private static void AppendEvaluationGuidelines(StringBuilder sb)
    {
        sb.AppendLine("EVALUATION GUIDELINES:");
        sb.AppendLine();
        sb.AppendLine("For tool NAME checks (category: \"ToolName\"):");
        sb.AppendLine("  - Evaluate naming quality: does it start with a verb, is it specific enough,");
        sb.AppendLine("    does it follow action+subject pattern (e.g., get_user, search_contacts)?");
        sb.AppendLine("  - Be lenient with domain-specific names; only fail truly vague names.");
        sb.AppendLine("  - Both snake_case and PascalCase naming conventions are acceptable.");
        sb.AppendLine();
        sb.AppendLine("For tool DESCRIPTION checks (category: \"ToolDescription\"):");
        sb.AppendLine("  - Evaluate completeness across these dimensions:");
        sb.AppendLine("    * Purpose: Does it explain what the tool does?");
        sb.AppendLine("    * Usage guidelines: Does it say when/how to use the tool?");
        sb.AppendLine("    * Limitations: Does it mention constraints or things it cannot do?");
        sb.AppendLine("    * Return info: Does it describe what the tool returns?");
        sb.AppendLine("    * Examples: Does it include sample inputs/outputs or usage patterns?");
        sb.AppendLine("  - A description does not need ALL dimensions to pass individual checks;");
        sb.AppendLine("    each check targets one dimension specifically.");
        sb.AppendLine();
        sb.AppendLine("For PARAMETER checks (categories: \"ParamName\", \"ParamDescription\"):");
        sb.AppendLine("  - Evaluate parameter naming: is it descriptive enough in context?");
        sb.AppendLine("    Names like 'query', 'userId', 'messageId' are fine.");
        sb.AppendLine("    Names like 'x', 'val', 'data', 'input' are too vague.");
        sb.AppendLine("  - Evaluate parameter descriptions: do they add info beyond the name?");
        sb.AppendLine("    Do they mention constraints, formats, or valid values?");
        sb.AppendLine("  - For categorical parameters: is an enum defined with valid values?");
        sb.AppendLine();
        sb.AppendLine("For TOOLSET checks (category: \"ToolsetDesign\", in server_checks):");
        sb.AppendLine("  - Evaluate cross-tool consistency and completeness.");
        sb.AppendLine("  - Check for tools with semantically overlapping descriptions (>70% similar).");
        sb.AppendLine("  - Check for incomplete CRUD coverage that seems unintentional.");
        sb.AppendLine("  - Only flag genuinely problematic patterns, not minor style differences.");
        sb.AppendLine();
    }

    private static void AppendExamples(StringBuilder sb)
    {
        sb.AppendLine("EXAMPLES:");
        sb.AppendLine();
        sb.AppendLine("Good evaluation (tool name check - pass):");
        sb.AppendLine("  Tool name: \"search_contacts\"");
        sb.AppendLine("  Prompt: \"Does the tool name start with an action verb?\"");
        sb.AppendLine("  score: true");
        sb.AppendLine("  reason: \"Name starts with the verb 'search', clearly indicating the action.\"");
        sb.AppendLine();
        sb.AppendLine("Good evaluation (tool name check - fail):");
        sb.AppendLine("  Tool name: \"data\"");
        sb.AppendLine("  Prompt: \"Is the tool name specific enough to distinguish it from other tools?\"");
        sb.AppendLine("  score: false");
        sb.AppendLine("  reason: \"Name 'data' is too generic; it does not indicate what action is performed or on what resource.\"");
        sb.AppendLine();
        sb.AppendLine("Good evaluation (description check - pass):");
        sb.AppendLine("  Description: \"Retrieves contact details by email or name. Returns a list of matching contacts with their phone numbers and email addresses.\"");
        sb.AppendLine("  Prompt: \"Does the description clearly state what the tool does?\"");
        sb.AppendLine("  score: true");
        sb.AppendLine("  reason: \"Description opens with 'Retrieves contact details', clearly stating the tool's purpose.\"");
        sb.AppendLine();
        sb.AppendLine("Good evaluation (description check - fail):");
        sb.AppendLine("  Description: \"This is a tool for contacts.\"");
        sb.AppendLine("  Prompt: \"Does the description provide information beyond just restating the tool name?\"");
        sb.AppendLine("  score: false");
        sb.AppendLine("  reason: \"Description only restates the subject 'contacts' without explaining how the tool works or what it returns.\"");
        sb.AppendLine();
        sb.AppendLine("Good evaluation (parameter check - pass):");
        sb.AppendLine("  Parameter: \"query\", Description: \"Search query string to match against contact names and emails. Max 256 characters.\"");
        sb.AppendLine("  Prompt: \"Does the description mention constraints, valid values, or format requirements?\"");
        sb.AppendLine("  score: true");
        sb.AppendLine("  reason: \"Description states the max length constraint (256 characters) and what fields are searched.\"");
        sb.AppendLine();
    }

    private static void AppendFinalRules(StringBuilder sb)
    {
        sb.AppendLine("IMPORTANT RULES:");
        sb.AppendLine("- Only modify items where \"score\" is null. Leave all other items untouched.");
        sb.AppendLine("- Each \"reason\" must be exactly one sentence.");
        sb.AppendLine("- Be calibrated: pass items that meet the check criteria, fail those that do not.");
        sb.AppendLine("- Use the tool's actual name, description, and input_schema from the JSON to evaluate.");
        sb.AppendLine("- Preserve all JSON field names, ordering, and structure exactly as-is.");
        sb.AppendLine("- Write valid JSON with 2-space indentation.");
    }
}
