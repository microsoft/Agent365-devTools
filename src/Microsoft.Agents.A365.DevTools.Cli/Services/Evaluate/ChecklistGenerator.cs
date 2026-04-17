// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Generates an evaluation checklist from discovered MCP tool schemas.
/// Runs deterministic checks inline (structural/objective checks that do not require
/// semantic judgment) and attaches semantic check placeholders for later evaluation
/// by a coding agent.
/// </summary>
internal sealed class ChecklistGenerator : IChecklistGenerator
{
    /// <inheritdoc />
    public EvaluationChecklist Generate(List<ToolSchema> tools, string serverName, string serverUrl)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var toolChecklists = new List<ToolChecklist>();

        foreach (var tool in tools)
        {
            var toolChecklist = BuildToolChecklist(tool, tools);
            toolChecklists.Add(toolChecklist);
        }

        var serverChecks = BuildServerChecks(tools);

        return new EvaluationChecklist
        {
            Metadata = new ChecklistMetadata
            {
                ServerName = serverName,
                ServerUrl = serverUrl,
                ToolCount = tools.Count,
                GeneratedAt = DateTime.UtcNow,
                GeneratorVersion = GetGeneratorVersion(),
            },
            Tools = toolChecklists,
            ServerChecks = serverChecks,
        };
    }

    /// <summary>
    /// Builds a complete checklist for a single tool, including deterministic checks
    /// (pre-scored) and semantic check placeholders (score = null).
    /// </summary>
    private static ToolChecklist BuildToolChecklist(ToolSchema tool, List<ToolSchema> allTools)
    {
        var name = tool.Name ?? string.Empty;
        var description = tool.Description ?? string.Empty;
        var inputSchema = tool.InputSchema;

        // Extract properties and required arrays from inputSchema
        var properties = ExtractProperties(inputSchema);
        var requiredParams = ExtractRequiredParams(inputSchema);
        var allParamNames = properties.Keys.ToList();

        // --- Tool Name checks ---
        var toolNameChecks = new List<ChecklistItem>();
        toolNameChecks.AddRange(RunToolNameDeterministicChecks(name));
        toolNameChecks.AddRange(
            SemanticCheckDefinitions.GetToolLevelChecks()
                .Where(c => c.Category == CheckCategory.ToolName));

        // --- Tool Description checks ---
        var toolDescriptionChecks = new List<ChecklistItem>();
        toolDescriptionChecks.AddRange(RunToolDescriptionDeterministicChecks(description));
        toolDescriptionChecks.AddRange(
            SemanticCheckDefinitions.GetToolLevelChecks()
                .Where(c => c.Category == CheckCategory.ToolDescription));

        // --- Schema Structure checks ---
        var schemaStructureChecks = RunSchemaStructureDeterministicChecks(inputSchema);

        // --- Parameter checks ---
        var parameterGroups = new Dictionary<string, ParamCheckGroups>();
        foreach (var (paramName, paramSchema) in properties)
        {
            var paramNameChecks = new List<ChecklistItem>();
            paramNameChecks.AddRange(RunParamNameDeterministicChecks(paramName, allParamNames));

            var paramDescChecks = new List<ChecklistItem>();
            paramDescChecks.AddRange(RunParamDescriptionDeterministicChecks(paramName, paramSchema));

            // Add semantic param checks, split by category
            var semanticParamChecks = SemanticCheckDefinitions.GetParamLevelChecks(paramName);
            paramNameChecks.AddRange(semanticParamChecks.Where(c => c.Category == CheckCategory.ParamName));
            paramDescChecks.AddRange(semanticParamChecks.Where(c => c.Category == CheckCategory.ParamDescription));

            parameterGroups[paramName] = new ParamCheckGroups
            {
                ParamName = paramNameChecks,
                ParamDescription = paramDescChecks,
            };
        }

        return new ToolChecklist
        {
            Name = name,
            Description = description,
            InputSchema = inputSchema,
            Checks = new ToolCheckGroups
            {
                ToolName = toolNameChecks,
                ToolDescription = toolDescriptionChecks,
                SchemaStructure = schemaStructureChecks,
                Parameters = parameterGroups,
            },
        };
    }

    /// <summary>
    /// Builds server-level (toolset) checks: deterministic + semantic.
    /// </summary>
    private static List<ChecklistItem> BuildServerChecks(List<ToolSchema> tools)
    {
        var checks = new List<ChecklistItem>();
        checks.AddRange(RunToolsetDeterministicChecks(tools));
        checks.AddRange(SemanticCheckDefinitions.GetToolsetLevelChecks());
        return checks;
    }

    // -----------------------------------------------------------------------
    // Tool Name deterministic checks
    // -----------------------------------------------------------------------

    private static List<ChecklistItem> RunToolNameDeterministicChecks(string name)
    {
        return
        [
            CheckToolNamePresent(name),
            CheckToolNameConsistentCasing(name),
            CheckToolNameNoSpecialChars(name),
            CheckToolNameReasonableLength(name),
        ];
    }

    private static ChecklistItem CheckToolNamePresent(string name)
    {
        bool passed = !string.IsNullOrWhiteSpace(name);
        return new ChecklistItem
        {
            Id = "tn_present",
            Type = CheckType.Deterministic,
            Prompt = "Tool has a non-empty name.",
            Score = passed,
            Reason = passed ? "Tool has a name." : "Tool name is empty or missing.",
            Severity = Priority.P0,
            Category = CheckCategory.ToolName,
            IssueIds = [4],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = passed ? string.Empty : "Every tool must have a non-empty name.",
        };
    }

    private static ChecklistItem CheckToolNameConsistentCasing(string name)
    {
        bool isSnake = Regex.IsMatch(name, @"^[a-z][a-z0-9]*(_[a-z0-9]+)*$");
        bool isCamel = Regex.IsMatch(name, @"^[a-z][a-zA-Z0-9]*$");
        bool isPascal = Regex.IsMatch(name, @"^[A-Z][a-zA-Z0-9]*$");
        bool isKebab = Regex.IsMatch(name, @"^[a-z][a-z0-9]*(-[a-z0-9]+)*$");
        bool passed = isSnake || isCamel || isPascal || isKebab;

        string detected = isSnake ? "snake_case"
            : isCamel ? "camelCase"
            : isPascal ? "PascalCase"
            : isKebab ? "kebab-case"
            : "mixed/inconsistent";

        return new ChecklistItem
        {
            Id = "tn_consistent_casing",
            Type = CheckType.Deterministic,
            Prompt = "Tool name uses a consistent naming convention (snake_case, camelCase, PascalCase, or kebab-case).",
            Score = passed,
            Reason = passed ? $"Name uses {detected} convention." : $"Name '{name}' uses mixed casing.",
            Severity = Priority.P2,
            Category = CheckCategory.ToolName,
            IssueIds = [17],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = passed ? string.Empty : "Use consistent snake_case (preferred) or camelCase for all tool names.",
        };
    }

    private static ChecklistItem CheckToolNameNoSpecialChars(string name)
    {
        bool passed = !string.IsNullOrEmpty(name) && Regex.IsMatch(name, @"^[a-zA-Z0-9_.\-]+$");
        var badChars = string.IsNullOrEmpty(name)
            ? []
            : Regex.Matches(name, @"[^a-zA-Z0-9_.\-]").Select(m => m.Value).Distinct().ToList();

        return new ChecklistItem
        {
            Id = "tn_no_special_chars",
            Type = CheckType.Deterministic,
            Prompt = "Tool name contains only valid characters (letters, numbers, underscores, hyphens, dots).",
            Score = passed,
            Reason = passed
                ? "Name contains only valid characters."
                : $"Name contains invalid characters: {string.Join(", ", badChars)}",
            Severity = Priority.P1,
            Category = CheckCategory.ToolName,
            IssueIds = [],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = passed ? string.Empty : "Remove special characters. Use only letters, numbers, underscores, hyphens, and dots.",
        };
    }

    private static ChecklistItem CheckToolNameReasonableLength(string name)
    {
        int length = name?.Length ?? 0;
        bool passed = length >= 3 && length <= 64;
        return new ChecklistItem
        {
            Id = "tn_reasonable_length",
            Type = CheckType.Deterministic,
            Prompt = "Tool name length is between 3 and 64 characters.",
            Score = passed,
            Reason = passed
                ? $"Name length ({length}) is within range."
                : $"Name length ({length}) outside 3-64 range.",
            Severity = Priority.P2,
            Category = CheckCategory.ToolName,
            IssueIds = [],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = passed ? string.Empty : "Keep tool names between 3 and 64 characters.",
        };
    }

    // -----------------------------------------------------------------------
    // Tool Description deterministic checks
    // -----------------------------------------------------------------------

    private static List<ChecklistItem> RunToolDescriptionDeterministicChecks(string description)
    {
        return
        [
            CheckToolDescriptionPresent(description),
            CheckToolDescriptionMinLength(description),
            CheckToolDescriptionMaxLength(description),
        ];
    }

    private static ChecklistItem CheckToolDescriptionPresent(string description)
    {
        bool passed = !string.IsNullOrWhiteSpace(description);
        return new ChecklistItem
        {
            Id = "td_present",
            Type = CheckType.Deterministic,
            Prompt = "Tool has a non-empty description.",
            Score = passed,
            Reason = passed ? "Tool has a description." : "Tool description is empty or missing.",
            Severity = Priority.P0,
            Category = CheckCategory.ToolDescription,
            IssueIds = [4, 5, 6, 7, 8],
            ImpactAreas = [ImpactArea.ToolSelection, ImpactArea.Completeness],
            Remediation = passed ? string.Empty : "Add a description explaining what this tool does, when to use it, and what it returns.",
        };
    }

    private static ChecklistItem CheckToolDescriptionMinLength(string description)
    {
        int length = description?.Trim().Length ?? 0;
        bool passed = length >= 20;
        return new ChecklistItem
        {
            Id = "td_min_length",
            Type = CheckType.Deterministic,
            Prompt = "Tool description is at least 20 characters.",
            Score = passed,
            Reason = passed
                ? $"Description is {length} chars."
                : $"Description is too short ({length} chars, minimum 20).",
            Severity = Priority.P1,
            Category = CheckCategory.ToolDescription,
            IssueIds = [4, 9],
            ImpactAreas = [ImpactArea.ToolSelection, ImpactArea.Completeness],
            Remediation = passed ? string.Empty : "Expand the description to at least 20 characters with meaningful content.",
        };
    }

    private static ChecklistItem CheckToolDescriptionMaxLength(string description)
    {
        int length = description?.Trim().Length ?? 0;
        bool passed = length <= 2000;
        return new ChecklistItem
        {
            Id = "td_max_length",
            Type = CheckType.Deterministic,
            Prompt = "Tool description is under 2000 characters.",
            Score = passed,
            Reason = passed
                ? "Description length is within limits."
                : $"Description is too long ({length} chars, max 2000). Risk of 16.67% regression.",
            Severity = Priority.P2,
            Category = CheckCategory.ToolDescription,
            IssueIds = [14],
            ImpactAreas = [ImpactArea.Conciseness],
            Remediation = passed ? string.Empty : "Trim to under 2000 characters. Focus on purpose, guidelines, and limitations.",
        };
    }

    // -----------------------------------------------------------------------
    // Schema Structure deterministic checks
    // -----------------------------------------------------------------------

    private static List<ChecklistItem> RunSchemaStructureDeterministicChecks(JsonElement? inputSchema)
    {
        return
        [
            CheckHasInputSchema(inputSchema),
            CheckTypeObject(inputSchema),
            CheckNoDeepNesting(inputSchema),
            CheckAllTyped(inputSchema),
            CheckArraysHaveItems(inputSchema),
            CheckRequiredMatchesProperties(inputSchema),
            CheckReasonableParamCount(inputSchema),
            CheckNoEmptyObjects(inputSchema),
        ];
    }

    private static ChecklistItem CheckHasInputSchema(JsonElement? inputSchema)
    {
        bool passed = inputSchema.HasValue && inputSchema.Value.ValueKind == JsonValueKind.Object;
        return new ChecklistItem
        {
            Id = "ss_has_input_schema",
            Type = CheckType.Deterministic,
            Prompt = "Tool has an input schema defined.",
            Score = passed,
            Reason = passed ? "Tool has an input schema." : "Tool has no input schema defined.",
            Severity = Priority.P0,
            Category = CheckCategory.SchemaStructure,
            IssueIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = passed ? string.Empty : "Define an inputSchema with type 'object' and properties for each parameter.",
        };
    }

    private static ChecklistItem CheckTypeObject(JsonElement? inputSchema)
    {
        if (!inputSchema.HasValue || inputSchema.Value.ValueKind != JsonValueKind.Object)
        {
            return MakeDeterministicPass("ss_type_object", "Root type is object",
                CheckCategory.SchemaStructure, "No schema to check.");
        }

        string schemaType = GetStringProperty(inputSchema.Value, "type") ?? string.Empty;
        bool passed = schemaType == "object";
        return new ChecklistItem
        {
            Id = "ss_type_object",
            Type = CheckType.Deterministic,
            Prompt = "Input schema root type is 'object'.",
            Score = passed,
            Reason = passed
                ? "Schema root is type 'object'."
                : $"Schema root type is '{schemaType}', expected 'object'.",
            Severity = Priority.P0,
            Category = CheckCategory.SchemaStructure,
            IssueIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = passed ? string.Empty : "Set the inputSchema type to 'object' with 'properties' for parameters.",
        };
    }

    private static ChecklistItem CheckNoDeepNesting(JsonElement? inputSchema)
    {
        if (!inputSchema.HasValue || inputSchema.Value.ValueKind != JsonValueKind.Object)
        {
            return MakeDeterministicPass("ss_no_deep_nesting", "No deep nesting",
                CheckCategory.SchemaStructure, "No schema to check.");
        }

        int depth = CalculateMaxDepth(inputSchema.Value, 0);
        bool passed = depth < 4;
        var severity = depth >= 4 ? Priority.P0 : depth == 3 ? Priority.P1 : Priority.P3;
        return new ChecklistItem
        {
            Id = "ss_no_deep_nesting",
            Type = CheckType.Deterministic,
            Prompt = "Input schema nesting depth is less than 4 levels.",
            Score = passed,
            Reason = passed
                ? $"Schema nesting depth is {depth} (limit: 3)."
                : $"Schema nesting depth is {depth}. LLMs systematically flatten nested args at depth 4+.",
            Severity = severity,
            Category = CheckCategory.SchemaStructure,
            IssueIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = passed ? string.Empty : "Flatten nested structures. Split deeply nested parameters into separate tools.",
        };
    }

    private static ChecklistItem CheckAllTyped(JsonElement? inputSchema)
    {
        var properties = ExtractProperties(inputSchema);
        if (properties.Count == 0)
        {
            return MakeDeterministicPass("ss_all_typed", "All properties typed",
                CheckCategory.SchemaStructure, "No properties.");
        }

        var untyped = properties
            .Where(p => p.Value.ValueKind == JsonValueKind.Object
                     && !p.Value.TryGetProperty("type", out _)
                     && !p.Value.TryGetProperty("$ref", out _))
            .Select(p => p.Key)
            .ToList();

        bool passed = untyped.Count == 0;
        return new ChecklistItem
        {
            Id = "ss_all_typed",
            Type = CheckType.Deterministic,
            Prompt = "All input schema properties have type definitions.",
            Score = passed,
            Reason = passed
                ? "All properties have type definitions."
                : $"Properties without type: {string.Join(", ", untyped)}. LLM cannot generate valid args.",
            Severity = Priority.P0,
            Category = CheckCategory.SchemaStructure,
            IssueIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = passed ? string.Empty : $"Add 'type' to these properties: {string.Join(", ", untyped)}.",
        };
    }

    private static ChecklistItem CheckArraysHaveItems(JsonElement? inputSchema)
    {
        var properties = ExtractProperties(inputSchema);
        var badArrays = properties
            .Where(p => p.Value.ValueKind == JsonValueKind.Object
                     && GetStringProperty(p.Value, "type") == "array"
                     && !p.Value.TryGetProperty("items", out _))
            .Select(p => p.Key)
            .ToList();

        bool passed = badArrays.Count == 0;
        return new ChecklistItem
        {
            Id = "ss_arrays_have_items",
            Type = CheckType.Deterministic,
            Prompt = "All array properties define their items type.",
            Score = passed,
            Reason = passed
                ? "All arrays define their items type."
                : $"Arrays without items: {string.Join(", ", badArrays)}. Breaks OpenAI/Azure.",
            Severity = Priority.P0,
            Category = CheckCategory.SchemaStructure,
            IssueIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = passed ? string.Empty : $"Add 'items' with a type definition to: {string.Join(", ", badArrays)}.",
        };
    }

    private static ChecklistItem CheckRequiredMatchesProperties(JsonElement? inputSchema)
    {
        var requiredParams = ExtractRequiredParams(inputSchema);
        var propertyNames = ExtractProperties(inputSchema).Keys.ToHashSet();

        if (requiredParams.Count == 0)
        {
            return MakeDeterministicPass("ss_required_matches", "Required matches properties",
                CheckCategory.SchemaStructure, "No required fields.");
        }

        var orphans = requiredParams.Where(r => !propertyNames.Contains(r)).ToList();
        bool passed = orphans.Count == 0;
        return new ChecklistItem
        {
            Id = "ss_required_matches",
            Type = CheckType.Deterministic,
            Prompt = "All required fields exist in the properties definition.",
            Score = passed,
            Reason = passed
                ? "All required fields exist in properties."
                : $"Required fields not in properties: {string.Join(", ", orphans)}. Server will always reject.",
            Severity = Priority.P0,
            Category = CheckCategory.SchemaStructure,
            IssueIds = [1],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = passed ? string.Empty : $"Add these to 'properties' or remove from 'required': {string.Join(", ", orphans)}.",
        };
    }

    private static ChecklistItem CheckReasonableParamCount(JsonElement? inputSchema)
    {
        int count = ExtractProperties(inputSchema).Count;
        bool passed;
        Priority severity;
        string message;

        if (count == 0)
        {
            passed = true;
            severity = Priority.P3;
            message = "Tool has no parameters (verify intentional).";
        }
        else if (count <= 10)
        {
            passed = true;
            severity = Priority.P3;
            message = $"Parameter count ({count}) is in the ideal range.";
        }
        else if (count <= 20)
        {
            passed = false;
            severity = Priority.P1;
            message = $"Parameter count ({count}) is high. gpt-4o-mini gets ~50% wrong with 10+ params.";
        }
        else
        {
            passed = false;
            severity = Priority.P0;
            message = $"Parameter count ({count}) almost certainly needs splitting into multiple tools.";
        }

        return new ChecklistItem
        {
            Id = "ss_reasonable_param_count",
            Type = CheckType.Deterministic,
            Prompt = "Tool has a reasonable number of parameters (10 or fewer is ideal).",
            Score = passed,
            Reason = message,
            Severity = severity,
            Category = CheckCategory.SchemaStructure,
            IssueIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = passed ? string.Empty : "Split tool into multiple focused tools with fewer parameters each.",
        };
    }

    private static ChecklistItem CheckNoEmptyObjects(JsonElement? inputSchema)
    {
        var properties = ExtractProperties(inputSchema);
        var emptyObjects = properties
            .Where(p => p.Value.ValueKind == JsonValueKind.Object
                     && GetStringProperty(p.Value, "type") == "object"
                     && !HasNonEmptyObjectProperty(p.Value, "properties"))
            .Select(p => p.Key)
            .ToList();

        bool passed = emptyObjects.Count == 0;
        return new ChecklistItem
        {
            Id = "ss_no_empty_objects",
            Type = CheckType.Deterministic,
            Prompt = "No object-type parameters are defined without inner properties.",
            Score = passed,
            Reason = passed
                ? "No empty object types."
                : $"Object params without properties: {string.Join(", ", emptyObjects)}. LLM will hallucinate field names.",
            Severity = Priority.P1,
            Category = CheckCategory.SchemaStructure,
            IssueIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = passed ? string.Empty : $"Define 'properties' for: {string.Join(", ", emptyObjects)}.",
        };
    }

    // -----------------------------------------------------------------------
    // Parameter Name deterministic checks
    // -----------------------------------------------------------------------

    private static List<ChecklistItem> RunParamNameDeterministicChecks(string paramName, List<string> allParamNames)
    {
        return
        [
            CheckParamNameNotSingleChar(paramName),
            CheckParamNameReasonableLength(paramName),
            CheckParamNameConsistentCasing(paramName, allParamNames),
        ];
    }

    private static ChecklistItem CheckParamNameNotSingleChar(string paramName)
    {
        bool passed = paramName.Length >= 2;
        return new ChecklistItem
        {
            Id = "pn_not_single_char",
            Type = CheckType.Deterministic,
            Prompt = $"Parameter '{paramName}' name is more than a single character.",
            Score = passed,
            Reason = passed
                ? "Parameter name is descriptive."
                : $"Parameter '{paramName}' is a single character.",
            Severity = Priority.P1,
            Category = CheckCategory.ParamName,
            IssueIds = [9],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = passed ? string.Empty : $"Rename '{paramName}' to a descriptive name.",
        };
    }

    private static ChecklistItem CheckParamNameReasonableLength(string paramName)
    {
        int length = paramName.Length;
        bool passed = length >= 2 && length <= 40;
        return new ChecklistItem
        {
            Id = "pn_reasonable_length",
            Type = CheckType.Deterministic,
            Prompt = $"Parameter '{paramName}' name length is between 2 and 40 characters.",
            Score = passed,
            Reason = passed
                ? "Parameter name length is reasonable."
                : $"Parameter '{paramName}' length ({length}) outside 2-40 range.",
            Severity = Priority.P3,
            Category = CheckCategory.ParamName,
            IssueIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = passed ? string.Empty : "Keep parameter names between 2 and 40 characters.",
        };
    }

    private static ChecklistItem CheckParamNameConsistentCasing(string paramName, List<string> allParamNames)
    {
        if (allParamNames.Count < 2)
        {
            return MakeDeterministicPass("pn_consistent_casing", "Consistent casing",
                CheckCategory.ParamName, "Only one parameter, casing consistent by default.");
        }

        var conventions = allParamNames.Select(DetectCasing).ToList();
        string dominant = conventions
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;
        string thisConvention = DetectCasing(paramName);
        bool passed = thisConvention == dominant;

        return new ChecklistItem
        {
            Id = "pn_consistent_casing",
            Type = CheckType.Deterministic,
            Prompt = $"Parameter '{paramName}' follows the dominant naming convention used by other parameters.",
            Score = passed,
            Reason = passed
                ? $"Parameter uses {thisConvention} (dominant: {dominant})."
                : $"Parameter '{paramName}' uses {thisConvention} but other params use {dominant}.",
            Severity = Priority.P3,
            Category = CheckCategory.ParamName,
            IssueIds = [17],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = passed ? string.Empty : $"Rename to match the dominant {dominant} convention used by other parameters.",
        };
    }

    // -----------------------------------------------------------------------
    // Parameter Description deterministic checks
    // -----------------------------------------------------------------------

    private static List<ChecklistItem> RunParamDescriptionDeterministicChecks(string paramName, JsonElement paramSchema)
    {
        return
        [
            CheckParamDescriptionPresent(paramName, paramSchema),
            CheckParamDescriptionMinLength(paramName, paramSchema),
            CheckParamDescriptionHasTypeGuidance(paramName, paramSchema),
        ];
    }

    private static ChecklistItem CheckParamDescriptionPresent(string paramName, JsonElement paramSchema)
    {
        string description = GetStringProperty(paramSchema, "description") ?? string.Empty;
        bool passed = !string.IsNullOrWhiteSpace(description);
        return new ChecklistItem
        {
            Id = "pd_present",
            Type = CheckType.Deterministic,
            Prompt = $"Parameter '{paramName}' has a non-empty description.",
            Score = passed,
            Reason = passed
                ? $"Parameter '{paramName}' has a description."
                : $"Parameter '{paramName}' has no description (38% more omission errors).",
            Severity = Priority.P0,
            Category = CheckCategory.ParamDescription,
            IssueIds = [9],
            ImpactAreas = [ImpactArea.ParamAccuracy, ImpactArea.Completeness],
            Remediation = passed ? string.Empty : $"Add a description to '{paramName}' explaining what it represents and expected values.",
        };
    }

    private static ChecklistItem CheckParamDescriptionMinLength(string paramName, JsonElement paramSchema)
    {
        string description = GetStringProperty(paramSchema, "description") ?? string.Empty;
        int wordCount = string.IsNullOrWhiteSpace(description)
            ? 0
            : description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        bool passed = wordCount >= 5;
        return new ChecklistItem
        {
            Id = "pd_min_length",
            Type = CheckType.Deterministic,
            Prompt = $"Parameter '{paramName}' description has at least 5 words.",
            Score = passed,
            Reason = passed
                ? $"'{paramName}' has {wordCount}-word description."
                : $"'{paramName}' description is too short ({wordCount} words, minimum 5).",
            Severity = Priority.P1,
            Category = CheckCategory.ParamDescription,
            IssueIds = [9],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = passed ? string.Empty : $"Expand '{paramName}' description to at least 5 words covering format and constraints.",
        };
    }

    private static ChecklistItem CheckParamDescriptionHasTypeGuidance(string paramName, JsonElement paramSchema)
    {
        bool hasType = paramSchema.TryGetProperty("type", out _);
        string description = (GetStringProperty(paramSchema, "description") ?? string.Empty).ToLowerInvariant();
        string[] typeKeywords = ["string", "number", "integer", "boolean", "array", "object", "id", "url", "email", "date", "iso"];
        bool hasTypeInDesc = typeKeywords.Any(keyword => description.Contains(keyword, StringComparison.Ordinal));
        bool passed = hasType || hasTypeInDesc;

        return new ChecklistItem
        {
            Id = "pd_has_type_guidance",
            Type = CheckType.Deterministic,
            Prompt = $"Parameter '{paramName}' has type information in schema or description.",
            Score = passed,
            Reason = passed
                ? $"'{paramName}' has type information."
                : $"'{paramName}' lacks type/format guidance in both schema and description.",
            Severity = Priority.P2,
            Category = CheckCategory.ParamDescription,
            IssueIds = [11],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = passed ? string.Empty : $"Add 'type' to schema for '{paramName}' or mention expected format in description.",
        };
    }

    // -----------------------------------------------------------------------
    // Toolset deterministic checks
    // -----------------------------------------------------------------------

    private static List<ChecklistItem> RunToolsetDeterministicChecks(List<ToolSchema> tools)
    {
        return
        [
            CheckToolsetReasonableCount(tools),
            CheckToolsetNoNearDuplicateNames(tools),
            CheckToolsetConsistentNaming(tools),
            CheckToolsetReasonableTokenBudget(tools),
        ];
    }

    private static ChecklistItem CheckToolsetReasonableCount(List<ToolSchema> tools)
    {
        int count = tools.Count;
        bool passed;
        Priority severity;
        string message;

        if (count == 0)
        {
            passed = false;
            severity = Priority.P0;
            message = "No tools discovered.";
        }
        else if (count <= 15)
        {
            passed = true;
            severity = Priority.P3;
            message = $"Tool count ({count}) is in the optimal range.";
        }
        else if (count <= 40)
        {
            passed = false;
            severity = Priority.P1;
            message = $"Tool count ({count}) may degrade selection accuracy. Consider grouping.";
        }
        else
        {
            passed = false;
            severity = Priority.P0;
            message = $"Tool count ({count}) exceeds most client limits (Cursor caps at 40).";
        }

        return new ChecklistItem
        {
            Id = "ts_reasonable_count",
            Type = CheckType.Deterministic,
            Prompt = "Server has a reasonable number of tools (15 or fewer is optimal).",
            Score = passed,
            Reason = message,
            Severity = severity,
            Category = CheckCategory.ToolsetDesign,
            IssueIds = [],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = passed ? string.Empty : count == 0
                ? "Add at least one tool to the server."
                : "Reduce tool count by merging related tools or using dynamic selection.",
        };
    }

    private static ChecklistItem CheckToolsetNoNearDuplicateNames(List<ToolSchema> tools)
    {
        var names = tools.Select(t => t.Name ?? string.Empty).ToList();
        var dupes = new List<(string Name1, string Name2)>();

        for (int i = 0; i < names.Count; i++)
        {
            for (int j = i + 1; j < names.Count; j++)
            {
                int dist = LevenshteinDistance(names[i].ToLowerInvariant(), names[j].ToLowerInvariant());
                if (dist is > 0 and < 3)
                {
                    dupes.Add((names[i], names[j]));
                }
            }
        }

        bool passed = dupes.Count == 0;
        string dupeList = string.Join("; ", dupes.Take(5).Select(d => $"{d.Name1} / {d.Name2}"));
        return new ChecklistItem
        {
            Id = "ts_no_near_duplicate_names",
            Type = CheckType.Deterministic,
            Prompt = "No tool names are near-duplicates (edit distance < 3).",
            Score = passed,
            Reason = passed
                ? "No near-duplicate tool names."
                : $"Near-duplicate names (edit dist < 3): {dupeList}",
            Severity = Priority.P1,
            Category = CheckCategory.ToolsetDesign,
            IssueIds = [17],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = passed ? string.Empty : "Rename tools to be clearly distinct.",
        };
    }

    private static ChecklistItem CheckToolsetConsistentNaming(List<ToolSchema> tools)
    {
        if (tools.Count < 2)
        {
            return MakeDeterministicPass("ts_consistent_naming", "Consistent naming",
                CheckCategory.ToolsetDesign, "Fewer than 2 tools.");
        }

        var conventions = tools.Select(t => DetectCasing(t.Name ?? string.Empty)).ToList();
        string dominant = conventions
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;
        var outliers = tools
            .Where((t, i) => conventions[i] != dominant)
            .Select(t => t.Name ?? string.Empty)
            .Take(5)
            .ToList();

        bool passed = outliers.Count == 0;
        return new ChecklistItem
        {
            Id = "ts_consistent_naming",
            Type = CheckType.Deterministic,
            Prompt = "All tool names follow the same naming convention.",
            Score = passed,
            Reason = passed
                ? $"All tools use {dominant}."
                : $"Inconsistent naming: most use {dominant}, but outliers: {string.Join(", ", outliers)}",
            Severity = Priority.P2,
            Category = CheckCategory.ToolsetDesign,
            IssueIds = [17],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = passed ? string.Empty : $"Rename outlier tools to match the dominant {dominant} convention.",
        };
    }

    private static ChecklistItem CheckToolsetReasonableTokenBudget(List<ToolSchema> tools)
    {
        int totalChars = tools.Sum(t =>
        {
            int chars = (t.Name?.Length ?? 0) + (t.Description?.Length ?? 0);
            if (t.InputSchema.HasValue)
            {
                chars += t.InputSchema.Value.GetRawText().Length;
            }
            return chars;
        });
        int estimatedTokens = totalChars / 4;
        const int budget = 12_800;
        bool passed = estimatedTokens <= budget;

        return new ChecklistItem
        {
            Id = "ts_reasonable_token_budget",
            Type = CheckType.Deterministic,
            Prompt = $"Total schema token estimate is within budget ({budget:N0} tokens).",
            Score = passed,
            Reason = passed
                ? $"Estimated schema tokens: {estimatedTokens:N0} (budget: {budget:N0})."
                : $"Schema consumes ~{estimatedTokens:N0} tokens (>{budget:N0}). Reduces available context.",
            Severity = passed ? Priority.P3 : Priority.P1,
            Category = CheckCategory.ToolsetDesign,
            IssueIds = [],
            ImpactAreas = [ImpactArea.Conciseness, ImpactArea.ToolSelection],
            Remediation = passed ? string.Empty : "Reduce schema size by trimming verbose descriptions, reducing tool count, or simplifying schemas.",
        };
    }

    // -----------------------------------------------------------------------
    // JSON helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts the 'properties' dictionary from an inputSchema JsonElement.
    /// Returns property name to property schema element mapping.
    /// </summary>
    private static Dictionary<string, JsonElement> ExtractProperties(JsonElement? inputSchema)
    {
        if (!inputSchema.HasValue || inputSchema.Value.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (!inputSchema.Value.TryGetProperty("properties", out var propertiesElement)
            || propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new Dictionary<string, JsonElement>();
        foreach (var property in propertiesElement.EnumerateObject())
        {
            result[property.Name] = property.Value;
        }
        return result;
    }

    /// <summary>
    /// Extracts the 'required' array from an inputSchema JsonElement.
    /// </summary>
    private static List<string> ExtractRequiredParams(JsonElement? inputSchema)
    {
        if (!inputSchema.HasValue || inputSchema.Value.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (!inputSchema.Value.TryGetProperty("required", out var requiredElement)
            || requiredElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in requiredElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (value is not null)
                {
                    result.Add(value);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Gets a string property from a JsonElement, returning null if not found.
    /// </summary>
    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value))
        {
            return value.GetString();
        }
        return null;
    }

    /// <summary>
    /// Checks if a JsonElement has a specified property that is a non-empty object.
    /// </summary>
    private static bool HasNonEmptyObjectProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Check that the object has at least one property
        using var enumerator = value.EnumerateObject();
        return enumerator.MoveNext();
    }

    /// <summary>
    /// Calculates the maximum nesting depth of a JSON schema element.
    /// </summary>
    private static int CalculateMaxDepth(JsonElement schema, int current)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return current;
        }

        int maxDepth = current;

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in properties.EnumerateObject())
            {
                maxDepth = Math.Max(maxDepth, CalculateMaxDepth(prop.Value, current + 1));
            }
        }

        if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            maxDepth = Math.Max(maxDepth, CalculateMaxDepth(items, current + 1));
        }

        if (schema.TryGetProperty("additionalProperties", out var addProps) && addProps.ValueKind == JsonValueKind.Object)
        {
            maxDepth = Math.Max(maxDepth, CalculateMaxDepth(addProps, current + 1));
        }

        return maxDepth;
    }

    // -----------------------------------------------------------------------
    // String helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Detects the naming convention used by a string.
    /// </summary>
    private static string DetectCasing(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "empty";
        }

        if (Regex.IsMatch(name, @"^[a-z][a-z0-9]*(_[a-z0-9]+)+$"))
        {
            return "snake_case";
        }

        if (Regex.IsMatch(name, @"^[a-z][a-z0-9]*(-[a-z0-9]+)+$"))
        {
            return "kebab-case";
        }

        if (Regex.IsMatch(name, @"^[a-z][a-zA-Z0-9]*$") && name.Any(char.IsUpper))
        {
            return "camelCase";
        }

        if (Regex.IsMatch(name, @"^[A-Z][a-zA-Z0-9]*$"))
        {
            return "PascalCase";
        }

        if (Regex.IsMatch(name, @"^[a-z][a-z0-9]*$"))
        {
            return "lowercase";
        }

        return "mixed";
    }

    /// <summary>
    /// Computes the Levenshtein edit distance between two strings.
    /// </summary>
    private static int LevenshteinDistance(string s1, string s2)
    {
        if (s1.Length < s2.Length)
        {
            return LevenshteinDistance(s2, s1);
        }

        if (s2.Length == 0)
        {
            return s1.Length;
        }

        int[] previousRow = Enumerable.Range(0, s2.Length + 1).ToArray();

        for (int i = 0; i < s1.Length; i++)
        {
            int[] currentRow = new int[s2.Length + 1];
            currentRow[0] = i + 1;

            for (int j = 0; j < s2.Length; j++)
            {
                int cost = s1[i] == s2[j] ? 0 : 1;
                currentRow[j + 1] = Math.Min(
                    Math.Min(currentRow[j] + 1, previousRow[j + 1] + 1),
                    previousRow[j] + cost);
            }

            previousRow = currentRow;
        }

        return previousRow[s2.Length];
    }

    // -----------------------------------------------------------------------
    // Convenience helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a passing deterministic check item for cases where the check
    /// is not applicable (e.g., no schema to validate).
    /// </summary>
    private static ChecklistItem MakeDeterministicPass(string id, string prompt, CheckCategory category, string reason)
    {
        return new ChecklistItem
        {
            Id = id,
            Type = CheckType.Deterministic,
            Prompt = prompt,
            Score = true,
            Reason = reason,
            Severity = Priority.P3,
            Category = category,
            IssueIds = [],
            ImpactAreas = [],
            Remediation = string.Empty,
        };
    }

    /// <summary>
    /// Gets the assembly version to use as the generator version in checklist metadata.
    /// Falls back to "0.0.0" if the assembly version cannot be determined.
    /// </summary>
    private static string GetGeneratorVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version is not null ? version.ToString() : "0.0.0";
    }
}
