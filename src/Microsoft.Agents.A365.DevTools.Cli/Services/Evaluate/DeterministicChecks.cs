// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Deterministic (structural/objective) checks for MCP tool schemas.
/// Only checks that can be verified without semantic judgment live here.
///
/// Research basis:
/// - 18-smell taxonomy: Li et al. (arXiv:2602.18914)
/// - 6-component framework: Hasan et al. (arXiv:2602.14878)
/// - TAFC parameter study: arXiv:2601.18282
/// </summary>
internal static class DeterministicChecks
{
    // -----------------------------------------------------------------------
    // Tool Name Checks (4)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs all deterministic tool-name checks against the given name.
    /// </summary>
    public static List<ChecklistItem> RunToolNameChecks(string name)
    {
        return
        [
            TnPresent(name),
            TnConsistentCasing(name),
            TnNoSpecialChars(name),
            TnReasonableLength(name),
        ];
    }

    // -----------------------------------------------------------------------
    // Tool Description Checks (3)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs all deterministic tool-description checks.
    /// </summary>
    public static List<ChecklistItem> RunToolDescriptionChecks(string description)
    {
        return
        [
            TdPresent(description),
            TdMinLength(description),
            TdMaxLength(description),
        ];
    }

    // -----------------------------------------------------------------------
    // Schema Structure Checks (8)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs all deterministic schema-structure checks against the inputSchema.
    /// </summary>
    public static List<ChecklistItem> RunSchemaStructureChecks(JsonElement? inputSchema)
    {
        return
        [
            SsHasInputSchema(inputSchema),
            SsTypeObject(inputSchema),
            SsNoDeepNesting(inputSchema),
            SsAllTyped(inputSchema),
            SsArraysHaveItems(inputSchema),
            SsRequiredMatches(inputSchema),
            SsReasonableParamCount(inputSchema),
            SsNoEmptyObjects(inputSchema),
        ];
    }

    // -----------------------------------------------------------------------
    // Parameter Name Checks (3)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs all deterministic param-name checks for a single parameter.
    /// </summary>
    /// <param name="paramName">Name of the parameter being checked.</param>
    /// <param name="allParamNames">All parameter names in the same tool (for casing consistency).</param>
    public static List<ChecklistItem> RunParamNameChecks(string paramName, List<string>? allParamNames)
    {
        return
        [
            PnNotSingleChar(paramName),
            PnReasonableLength(paramName),
            PnConsistentCasing(paramName, allParamNames),
        ];
    }

    // -----------------------------------------------------------------------
    // Parameter Description Checks (3)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs all deterministic param-description checks for a single parameter.
    /// </summary>
    public static List<ChecklistItem> RunParamDescriptionChecks(string paramName, JsonElement paramSchema)
    {
        return
        [
            PdPresent(paramName, paramSchema),
            PdMinLength(paramName, paramSchema),
            PdHasTypeGuidance(paramName, paramSchema),
        ];
    }

    // -----------------------------------------------------------------------
    // Toolset Design Checks (4)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs all deterministic toolset-level (cross-tool) checks.
    /// </summary>
    /// <param name="tools">All tools in the server, each as a raw JSON element.</param>
    public static List<ChecklistItem> RunToolsetChecks(List<JsonElement> tools)
    {
        return
        [
            TsReasonableCount(tools),
            TsNoNearDuplicateNames(tools),
            TsConsistentNaming(tools),
            TsReasonableTokenBudget(tools),
        ];
    }

    // =======================================================================
    // Individual check implementations
    // =======================================================================

    // -- Tool Name ----------------------------------------------------------

    private static ChecklistItem TnPresent(string name)
    {
        bool ok = !string.IsNullOrWhiteSpace(name);
        return new ChecklistItem
        {
            Id = "tn_present",
            Type = CheckType.Deterministic,
            Prompt = "Tool name present",
            Score = ok,
            Reason = ok ? "Tool has a name." : "Tool name is empty or missing.",
            Severity = Priority.P0,
            Category = CheckCategory.ToolName,
            SmellIds = [4],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = "Every tool must have a non-empty name.",
        };
    }

    private static ChecklistItem TnConsistentCasing(string name)
    {
        bool isSnake = Regex.IsMatch(name, @"^[a-z][a-z0-9]*(_[a-z0-9]+)*$");
        bool isCamel = Regex.IsMatch(name, @"^[a-z][a-zA-Z0-9]*$");
        bool isPascal = Regex.IsMatch(name, @"^[A-Z][a-zA-Z0-9]*$");
        bool isKebab = Regex.IsMatch(name, @"^[a-z][a-z0-9]*(-[a-z0-9]+)*$");
        bool ok = isSnake || isCamel || isPascal || isKebab;

        string detected = isSnake ? "snake_case"
            : isCamel ? "camelCase"
            : isPascal ? "PascalCase"
            : isKebab ? "kebab-case"
            : "mixed/inconsistent";

        return new ChecklistItem
        {
            Id = "tn_consistent_casing",
            Type = CheckType.Deterministic,
            Prompt = "Consistent naming convention",
            Score = ok,
            Reason = ok
                ? $"Name uses {detected} convention."
                : $"Name '{name}' uses mixed casing.",
            Severity = Priority.P2,
            Category = CheckCategory.ToolName,
            SmellIds = [17],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = "Use consistent snake_case (preferred) or camelCase for all tool names.",
        };
    }

    private static ChecklistItem TnNoSpecialChars(string name)
    {
        bool ok = !string.IsNullOrEmpty(name) && Regex.IsMatch(name, @"^[a-zA-Z0-9_.\-]+$");
        var badChars = string.IsNullOrEmpty(name)
            ? new HashSet<char>()
            : new HashSet<char>(Regex.Matches(name, @"[^a-zA-Z0-9_.\-]").Select(m => m.Value[0]));

        return new ChecklistItem
        {
            Id = "tn_no_special_chars",
            Type = CheckType.Deterministic,
            Prompt = "No special characters",
            Score = ok,
            Reason = ok
                ? "Name contains only valid characters."
                : $"Name contains invalid characters: {{{string.Join(", ", badChars.Select(c => $"'{c}'"))}}}",
            Severity = Priority.P1,
            Category = CheckCategory.ToolName,
            SmellIds = [],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = "Remove special characters. Use only letters, numbers, underscores, hyphens, and dots.",
        };
    }

    private static ChecklistItem TnReasonableLength(string name)
    {
        int length = name?.Length ?? 0;
        bool ok = length >= 3 && length <= 64;
        return new ChecklistItem
        {
            Id = "tn_reasonable_length",
            Type = CheckType.Deterministic,
            Prompt = "Reasonable name length",
            Score = ok,
            Reason = ok
                ? $"Name length ({length}) is within range."
                : $"Name length ({length}) outside 3-64 range.",
            Severity = Priority.P2,
            Category = CheckCategory.ToolName,
            SmellIds = [],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = "Keep tool names between 3 and 64 characters.",
        };
    }

    // -- Tool Description ---------------------------------------------------

    private static ChecklistItem TdPresent(string description)
    {
        bool ok = !string.IsNullOrWhiteSpace(description);
        return new ChecklistItem
        {
            Id = "td_present",
            Type = CheckType.Deterministic,
            Prompt = "Description present",
            Score = ok,
            Reason = ok ? "Tool has a description." : "Tool description is empty or missing.",
            Severity = Priority.P0,
            Category = CheckCategory.ToolDescription,
            SmellIds = [4, 5, 6, 7, 8],
            ImpactAreas = [ImpactArea.ToolSelection, ImpactArea.Completeness],
            Remediation = "Add a description explaining what this tool does, when to use it, and what it returns.",
        };
    }

    /// <summary>
    /// Minimum description length check. Uses CHARACTER count (not words).
    /// </summary>
    private static ChecklistItem TdMinLength(string description)
    {
        int length = description?.Trim().Length ?? 0;
        bool ok = length >= 20;
        return new ChecklistItem
        {
            Id = "td_min_length",
            Type = CheckType.Deterministic,
            Prompt = "Minimum description length",
            Score = ok,
            Reason = ok
                ? $"Description is {length} chars."
                : $"Description is too short ({length} chars, minimum 20).",
            Severity = Priority.P1,
            Category = CheckCategory.ToolDescription,
            SmellIds = [4, 9],
            ImpactAreas = [ImpactArea.ToolSelection, ImpactArea.Completeness],
            Remediation = "Expand the description to at least 20 characters with meaningful content.",
        };
    }

    private static ChecklistItem TdMaxLength(string description)
    {
        int length = description?.Trim().Length ?? 0;
        bool ok = length <= 2000;
        return new ChecklistItem
        {
            Id = "td_max_length",
            Type = CheckType.Deterministic,
            Prompt = "Not over-verbose",
            Score = ok,
            Reason = ok
                ? "Description length is within limits."
                : $"Description is too long ({length} chars, max 2000). Risk of 16.67% regression.",
            Severity = Priority.P2,
            Category = CheckCategory.ToolDescription,
            SmellIds = [14],
            ImpactAreas = [ImpactArea.Conciseness],
            Remediation = "Trim to under 2000 characters. Focus on purpose, guidelines, and limitations.",
        };
    }

    // -- Parameter Name -----------------------------------------------------

    private static ChecklistItem PnNotSingleChar(string paramName)
    {
        bool ok = !string.IsNullOrEmpty(paramName) && paramName.Length >= 2;
        return new ChecklistItem
        {
            Id = "pn_not_single_char",
            Type = CheckType.Deterministic,
            Prompt = "Not single character",
            Score = ok,
            Reason = ok
                ? "Parameter name is descriptive."
                : $"Parameter '{paramName}' is a single character.",
            Severity = Priority.P1,
            Category = CheckCategory.ParamName,
            SmellIds = [9],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = $"Rename '{paramName}' to a descriptive name.",
        };
    }

    private static ChecklistItem PnReasonableLength(string paramName)
    {
        int length = paramName?.Length ?? 0;
        bool ok = length >= 2 && length <= 40;
        return new ChecklistItem
        {
            Id = "pn_reasonable_length",
            Type = CheckType.Deterministic,
            Prompt = "Reasonable length",
            Score = ok,
            Reason = ok
                ? "Parameter name length is reasonable."
                : $"Parameter '{paramName}' length ({length}) outside 2-40 range.",
            Severity = Priority.P3,
            Category = CheckCategory.ParamName,
            SmellIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = "Keep parameter names between 2 and 40 characters.",
        };
    }

    /// <summary>
    /// Checks if this parameter follows the dominant casing convention in its tool.
    /// Auto-passes for single-parameter tools.
    /// </summary>
    private static ChecklistItem PnConsistentCasing(string paramName, List<string>? allParamNames)
    {
        if (allParamNames is null || allParamNames.Count < 2)
        {
            return Pass(
                "pn_consistent_casing",
                "Consistent casing",
                CheckCategory.ParamName,
                "Only one parameter, casing consistent by default.");
        }

        var conventions = allParamNames.Select(DetectCasing).ToList();
        string dominant = conventions
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;
        string thisConvention = DetectCasing(paramName);
        bool ok = thisConvention == dominant;

        return new ChecklistItem
        {
            Id = "pn_consistent_casing",
            Type = CheckType.Deterministic,
            Prompt = "Consistent casing",
            Score = ok,
            Reason = ok
                ? $"Parameter uses {thisConvention} (dominant: {dominant})."
                : $"Parameter '{paramName}' uses {thisConvention} but other params use {dominant}.",
            Severity = Priority.P3,
            Category = CheckCategory.ParamName,
            SmellIds = [17],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = $"Rename to match the dominant {dominant} convention used by other parameters.",
        };
    }

    // -- Parameter Description ----------------------------------------------

    private static ChecklistItem PdPresent(string paramName, JsonElement paramSchema)
    {
        string desc = GetStringProperty(paramSchema, "description");
        bool ok = !string.IsNullOrWhiteSpace(desc);
        return new ChecklistItem
        {
            Id = "pd_present",
            Type = CheckType.Deterministic,
            Prompt = "Description present",
            Score = ok,
            Reason = ok
                ? $"Parameter '{paramName}' has a description."
                : $"Parameter '{paramName}' has no description (38% more omission errors).",
            Severity = Priority.P0,
            Category = CheckCategory.ParamDescription,
            SmellIds = [9],
            ImpactAreas = [ImpactArea.ParamAccuracy, ImpactArea.Completeness],
            Remediation = $"Add a description to '{paramName}' explaining what it represents and expected values.",
        };
    }

    /// <summary>
    /// Minimum parameter description length check. Uses WORD count (not characters).
    /// </summary>
    private static ChecklistItem PdMinLength(string paramName, JsonElement paramSchema)
    {
        string desc = GetStringProperty(paramSchema, "description");
        int words = string.IsNullOrEmpty(desc) ? 0 : desc.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        bool ok = words >= 5;
        return new ChecklistItem
        {
            Id = "pd_min_length",
            Type = CheckType.Deterministic,
            Prompt = "Minimum description length",
            Score = ok,
            Reason = ok
                ? $"'{paramName}' has {words}-word description."
                : $"'{paramName}' description is too short ({words} words, minimum 5).",
            Severity = Priority.P1,
            Category = CheckCategory.ParamDescription,
            SmellIds = [9],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = $"Expand '{paramName}' description to at least 5 words covering format and constraints.",
        };
    }

    /// <summary>
    /// Checks if the schema has explicit type or the description mentions type keywords.
    /// Uses substring matching that catches partial words (e.g. "id" in "valid").
    /// </summary>
    private static ChecklistItem PdHasTypeGuidance(string paramName, JsonElement paramSchema)
    {
        bool hasType = paramSchema.ValueKind == JsonValueKind.Object
            && paramSchema.TryGetProperty("type", out _);

        string desc = GetStringProperty(paramSchema, "description").ToLowerInvariant();
        // Substring matching preserves Python behavior: "id" matches inside "valid", etc.
        string[] typeKeywords = ["string", "number", "integer", "boolean", "array", "object", "id", "url", "email", "date", "iso"];
        bool hasTypeInDesc = typeKeywords.Any(w => desc.Contains(w, StringComparison.Ordinal));
        bool ok = hasType || hasTypeInDesc;

        return new ChecklistItem
        {
            Id = "pd_has_type_guidance",
            Type = CheckType.Deterministic,
            Prompt = "Type/format guidance",
            Score = ok,
            Reason = ok
                ? $"'{paramName}' has type information."
                : $"'{paramName}' lacks type/format guidance in both schema and description.",
            Severity = Priority.P2,
            Category = CheckCategory.ParamDescription,
            SmellIds = [11],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = $"Add 'type' to schema for '{paramName}' or mention expected format in description.",
        };
    }

    // -- Schema Structure ---------------------------------------------------

    private static ChecklistItem SsHasInputSchema(JsonElement? inputSchema)
    {
        bool ok = inputSchema.HasValue && inputSchema.Value.ValueKind == JsonValueKind.Object;
        return new ChecklistItem
        {
            Id = "ss_has_input_schema",
            Type = CheckType.Deterministic,
            Prompt = "Input schema present",
            Score = ok,
            Reason = ok ? "Tool has an input schema." : "Tool has no input schema defined.",
            Severity = Priority.P0,
            Category = CheckCategory.SchemaStructure,
            SmellIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = "Define an inputSchema with type 'object' and properties for each parameter.",
        };
    }

    private static ChecklistItem SsTypeObject(JsonElement? inputSchema)
    {
        if (!inputSchema.HasValue || inputSchema.Value.ValueKind != JsonValueKind.Object)
        {
            return Pass("ss_type_object", "Root type is object", CheckCategory.SchemaStructure, "No schema.");
        }

        string schemaType = GetStringProperty(inputSchema.Value, "type");
        bool ok = schemaType == "object";
        return new ChecklistItem
        {
            Id = "ss_type_object",
            Type = CheckType.Deterministic,
            Prompt = "Root type is object",
            Score = ok,
            Reason = ok
                ? "Schema root is type 'object'."
                : $"Schema root type is '{schemaType}', expected 'object'.",
            Severity = Priority.P0,
            Category = CheckCategory.SchemaStructure,
            SmellIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = "Set the inputSchema type to 'object' with 'properties' for parameters.",
        };
    }

    /// <summary>
    /// DYNAMIC severity: P0 at depth >= 4, P1 at depth == 3, P3 otherwise.
    /// </summary>
    private static ChecklistItem SsNoDeepNesting(JsonElement? inputSchema)
    {
        int depth = inputSchema.HasValue ? MaxDepth(inputSchema.Value, 0) : 0;
        bool ok = depth < 4;
        Priority severity = depth >= 4 ? Priority.P0
            : depth == 3 ? Priority.P1
            : Priority.P3;

        return new ChecklistItem
        {
            Id = "ss_no_deep_nesting",
            Type = CheckType.Deterministic,
            Prompt = "No deep nesting",
            Score = ok,
            Reason = ok
                ? $"Schema nesting depth is {depth} (limit: 3)."
                : $"Schema nesting depth is {depth}. LLMs systematically flatten nested args at depth 4+.",
            Severity = severity,
            Category = CheckCategory.SchemaStructure,
            SmellIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = "Flatten nested structures. Split deeply nested parameters into separate tools.",
        };
    }

    private static ChecklistItem SsAllTyped(JsonElement? inputSchema)
    {
        var props = GetProperties(inputSchema);
        if (props.Count == 0)
        {
            return Pass("ss_all_typed", "All properties typed", CheckCategory.SchemaStructure, "No properties.");
        }

        var untyped = props
            .Where(kvp =>
                kvp.Value.ValueKind == JsonValueKind.Object
                && !kvp.Value.TryGetProperty("type", out _)
                && !kvp.Value.TryGetProperty("$ref", out _))
            .Select(kvp => kvp.Key)
            .ToList();

        bool ok = untyped.Count == 0;
        return new ChecklistItem
        {
            Id = "ss_all_typed",
            Type = CheckType.Deterministic,
            Prompt = "All properties typed",
            Score = ok,
            Reason = ok
                ? "All properties have type definitions."
                : $"Properties without type: [{string.Join(", ", untyped)}]. LLM cannot generate valid args.",
            Severity = Priority.P0,
            Category = CheckCategory.SchemaStructure,
            SmellIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = ok ? string.Empty : $"Add 'type' to these properties: {string.Join(", ", untyped)}.",
        };
    }

    private static ChecklistItem SsArraysHaveItems(JsonElement? inputSchema)
    {
        var props = GetProperties(inputSchema);
        var badArrays = props
            .Where(kvp =>
                kvp.Value.ValueKind == JsonValueKind.Object
                && GetStringProperty(kvp.Value, "type") == "array"
                && !kvp.Value.TryGetProperty("items", out _))
            .Select(kvp => kvp.Key)
            .ToList();

        bool ok = badArrays.Count == 0;
        return new ChecklistItem
        {
            Id = "ss_arrays_have_items",
            Type = CheckType.Deterministic,
            Prompt = "Arrays have items defined",
            Score = ok,
            Reason = ok
                ? "All arrays define their items type."
                : $"Arrays without items: [{string.Join(", ", badArrays)}]. Breaks OpenAI/Azure.",
            Severity = Priority.P0,
            Category = CheckCategory.SchemaStructure,
            SmellIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = ok ? string.Empty : $"Add 'items' with a type definition to: {string.Join(", ", badArrays)}.",
        };
    }

    private static ChecklistItem SsRequiredMatches(JsonElement? inputSchema)
    {
        if (!inputSchema.HasValue || inputSchema.Value.ValueKind != JsonValueKind.Object)
        {
            return Pass("ss_required_matches", "Required matches properties", CheckCategory.SchemaStructure, "No required fields.");
        }

        var required = new HashSet<string>();
        if (inputSchema.Value.TryGetProperty("required", out JsonElement reqElement)
            && reqElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in reqElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    required.Add(item.GetString()!);
                }
            }
        }

        if (required.Count == 0)
        {
            return Pass("ss_required_matches", "Required matches properties", CheckCategory.SchemaStructure, "No required fields.");
        }

        var propNames = new HashSet<string>(GetProperties(inputSchema).Select(kvp => kvp.Key));
        var orphans = required.Except(propNames).ToList();
        bool ok = orphans.Count == 0;

        return new ChecklistItem
        {
            Id = "ss_required_matches",
            Type = CheckType.Deterministic,
            Prompt = "Required matches properties",
            Score = ok,
            Reason = ok
                ? "All required fields exist in properties."
                : $"Required fields not in properties: {{{string.Join(", ", orphans)}}}. Server will always reject.",
            Severity = Priority.P0,
            Category = CheckCategory.SchemaStructure,
            SmellIds = [1],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = ok ? string.Empty : $"Add these to 'properties' or remove from 'required': {string.Join(", ", orphans)}.",
        };
    }

    /// <summary>
    /// Tiered severity: 0-10 pass, 11-20 fail/P1, 21+ fail/P0.
    /// </summary>
    private static ChecklistItem SsReasonableParamCount(JsonElement? inputSchema)
    {
        int count = GetProperties(inputSchema).Count;
        bool ok;
        Priority severity;
        string msg;
        string remediation;

        if (count == 0)
        {
            ok = true;
            severity = Priority.P3;
            msg = "Tool has no parameters (verify intentional).";
            remediation = string.Empty;
        }
        else if (count <= 10)
        {
            ok = true;
            severity = Priority.P3;
            msg = $"Parameter count ({count}) is in the ideal range.";
            remediation = string.Empty;
        }
        else if (count <= 20)
        {
            ok = false;
            severity = Priority.P1;
            msg = $"Parameter count ({count}) is high. gpt-4o-mini gets ~50% wrong with 10+ params.";
            remediation = "Split tool into multiple focused tools with fewer parameters each.";
        }
        else
        {
            ok = false;
            severity = Priority.P0;
            msg = $"Parameter count ({count}) almost certainly needs splitting into multiple tools.";
            remediation = "Split tool into multiple focused tools with fewer parameters each.";
        }

        return new ChecklistItem
        {
            Id = "ss_reasonable_param_count",
            Type = CheckType.Deterministic,
            Prompt = "Reasonable parameter count",
            Score = ok,
            Reason = msg,
            Severity = severity,
            Category = CheckCategory.SchemaStructure,
            SmellIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = remediation,
        };
    }

    private static ChecklistItem SsNoEmptyObjects(JsonElement? inputSchema)
    {
        var props = GetProperties(inputSchema);
        var emptyObjs = props
            .Where(kvp =>
                kvp.Value.ValueKind == JsonValueKind.Object
                && GetStringProperty(kvp.Value, "type") == "object"
                && !HasNonEmptyProperties(kvp.Value))
            .Select(kvp => kvp.Key)
            .ToList();

        bool ok = emptyObjs.Count == 0;
        return new ChecklistItem
        {
            Id = "ss_no_empty_objects",
            Type = CheckType.Deterministic,
            Prompt = "No empty object types",
            Score = ok,
            Reason = ok
                ? "No empty object types."
                : $"Object params without properties: [{string.Join(", ", emptyObjs)}]. LLM will hallucinate field names.",
            Severity = Priority.P1,
            Category = CheckCategory.SchemaStructure,
            SmellIds = [],
            ImpactAreas = [ImpactArea.ParamAccuracy],
            Remediation = ok ? string.Empty : $"Define 'properties' for: {string.Join(", ", emptyObjs)}.",
        };
    }

    // -- Toolset Design -----------------------------------------------------

    private static ChecklistItem TsReasonableCount(List<JsonElement> tools)
    {
        int count = tools.Count;
        if (count == 0)
        {
            return Fail(
                "ts_reasonable_count",
                "Reasonable tool count",
                CheckCategory.ToolsetDesign,
                "No tools discovered.",
                Priority.P0,
                [],
                [ImpactArea.ToolSelection],
                "Add at least one tool to the server.");
        }

        bool ok;
        Priority severity;
        string msg;
        string remediation;
        if (count <= 15)
        {
            ok = true;
            severity = Priority.P3;
            msg = $"Tool count ({count}) is in the optimal range.";
            remediation = string.Empty;
        }
        else if (count <= 40)
        {
            ok = false;
            severity = Priority.P1;
            msg = $"Tool count ({count}) may degrade selection accuracy. Consider grouping.";
            remediation = "Reduce tool count by merging related tools or using dynamic selection.";
        }
        else
        {
            ok = false;
            severity = Priority.P0;
            msg = $"Tool count ({count}) exceeds most client limits (Cursor caps at 40).";
            remediation = "Reduce tool count by merging related tools or using dynamic selection.";
        }

        return new ChecklistItem
        {
            Id = "ts_reasonable_count",
            Type = CheckType.Deterministic,
            Prompt = "Reasonable tool count",
            Score = ok,
            Reason = msg,
            Severity = severity,
            Category = CheckCategory.ToolsetDesign,
            SmellIds = [],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = remediation,
        };
    }

    /// <summary>
    /// Near-duplicate detection: Levenshtein distance less than 3 AND greater than 0, case-insensitive.
    /// </summary>
    private static ChecklistItem TsNoNearDuplicateNames(List<JsonElement> tools)
    {
        var names = tools
            .Select(t => t.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty)
            .ToList();

        var dupes = new List<(string A, string B)>();
        for (int i = 0; i < names.Count; i++)
        {
            for (int j = i + 1; j < names.Count; j++)
            {
                int dist = Levenshtein(names[i].ToLowerInvariant(), names[j].ToLowerInvariant());
                if (dist > 0 && dist < 3)
                {
                    dupes.Add((names[i], names[j]));
                }
            }
        }

        bool ok = dupes.Count == 0;
        string dupeDisplay = string.Join("; ", dupes.Take(5).Select(d => $"{d.A} / {d.B}"));
        return new ChecklistItem
        {
            Id = "ts_no_near_duplicate_names",
            Type = CheckType.Deterministic,
            Prompt = "No near-duplicate names",
            Score = ok,
            Reason = ok
                ? "No near-duplicate tool names."
                : $"Near-duplicate names (edit dist < 3): {dupeDisplay}",
            Severity = Priority.P1,
            Category = CheckCategory.ToolsetDesign,
            SmellIds = [17],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = "Rename tools to be clearly distinct.",
        };
    }

    /// <summary>
    /// Uses the <see cref="DetectCasing"/> helper (same as <c>pn_consistent_casing</c>).
    /// </summary>
    private static ChecklistItem TsConsistentNaming(List<JsonElement> tools)
    {
        if (tools.Count < 2)
        {
            return Pass("ts_consistent_naming", "Consistent naming", CheckCategory.ToolsetDesign, "Fewer than 2 tools.");
        }

        var names = tools
            .Select(t => t.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty)
            .ToList();

        var conventions = names.Select(DetectCasing).ToList();
        string dominant = conventions
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;

        var outliers = names
            .Where((name, idx) => conventions[idx] != dominant)
            .Take(5)
            .ToList();

        bool ok = outliers.Count == 0;
        return new ChecklistItem
        {
            Id = "ts_consistent_naming",
            Type = CheckType.Deterministic,
            Prompt = "Consistent naming convention",
            Score = ok,
            Reason = ok
                ? $"All tools use {dominant}."
                : $"Inconsistent naming: most use {dominant}, but outliers: [{string.Join(", ", outliers)}]",
            Severity = Priority.P2,
            Category = CheckCategory.ToolsetDesign,
            SmellIds = [17],
            ImpactAreas = [ImpactArea.ToolSelection],
            Remediation = ok ? string.Empty : $"Rename outlier tools to match the dominant {dominant} convention.",
        };
    }

    /// <summary>
    /// Estimate total schema tokens: sum(json_serialized_chars) / 4, budget = 12,800.
    /// </summary>
    private static ChecklistItem TsReasonableTokenBudget(List<JsonElement> tools)
    {
        int totalChars = tools.Sum(t => t.GetRawText().Length);
        int estimatedTokens = totalChars / 4;
        const int Budget = 12_800;
        bool ok = estimatedTokens <= Budget;

        return new ChecklistItem
        {
            Id = "ts_reasonable_token_budget",
            Type = CheckType.Deterministic,
            Prompt = "Reasonable token budget",
            Score = ok,
            Reason = ok
                ? $"Estimated schema tokens: {estimatedTokens:N0} (budget: {Budget:N0})."
                : $"Schema consumes ~{estimatedTokens:N0} tokens (>{Budget:N0}). Reduces available context.",
            Severity = ok ? Priority.P3 : Priority.P1,
            Category = CheckCategory.ToolsetDesign,
            SmellIds = [],
            ImpactAreas = [ImpactArea.Conciseness, ImpactArea.ToolSelection],
            Remediation = ok
                ? string.Empty
                : "Reduce schema size by trimming verbose descriptions, reducing tool count, or simplifying schemas.",
        };
    }

    // =======================================================================
    // Helper methods
    // =======================================================================

    /// <summary>
    /// Detect the naming convention of a string. Shared by <c>pn_consistent_casing</c>
    /// and <c>ts_consistent_naming</c>. Mirrors the Python <c>_detect_casing</c> helper.
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
    /// Calculate maximum nesting depth of a JSON schema.
    /// Traverses <c>properties</c>, <c>items</c>, and <c>additionalProperties</c>.
    /// </summary>
    private static int MaxDepth(JsonElement schema, int current)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return current;
        }

        int maxD = current;

        // Traverse "properties" -- each child property is one level deeper
        if (schema.TryGetProperty("properties", out JsonElement propsElement)
            && propsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in propsElement.EnumerateObject())
            {
                maxD = Math.Max(maxD, MaxDepth(prop.Value, current + 1));
            }
        }

        // Traverse "items" -- single level deeper
        if (schema.TryGetProperty("items", out JsonElement itemsElement)
            && itemsElement.ValueKind == JsonValueKind.Object)
        {
            maxD = Math.Max(maxD, MaxDepth(itemsElement, current + 1));
        }

        // Traverse "additionalProperties" -- single level deeper
        if (schema.TryGetProperty("additionalProperties", out JsonElement addlElement)
            && addlElement.ValueKind == JsonValueKind.Object)
        {
            maxD = Math.Max(maxD, MaxDepth(addlElement, current + 1));
        }

        return maxD;
    }

    /// <summary>
    /// Compute the Levenshtein edit distance between two strings.
    /// </summary>
    private static int Levenshtein(string s1, string s2)
    {
        if (s1.Length < s2.Length)
        {
            return Levenshtein(s2, s1);
        }

        if (s2.Length == 0)
        {
            return s1.Length;
        }

        var prevRow = new int[s2.Length + 1];
        for (int i = 0; i <= s2.Length; i++)
        {
            prevRow[i] = i;
        }

        for (int i = 0; i < s1.Length; i++)
        {
            var currRow = new int[s2.Length + 1];
            currRow[0] = i + 1;
            for (int j = 0; j < s2.Length; j++)
            {
                int cost = s1[i] == s2[j] ? 0 : 1;
                currRow[j + 1] = Math.Min(
                    Math.Min(currRow[j] + 1, prevRow[j + 1] + 1),
                    prevRow[j] + cost);
            }

            prevRow = currRow;
        }

        return prevRow[s2.Length];
    }

    /// <summary>
    /// Convenience factory for a passing check result.
    /// </summary>
    private static ChecklistItem Pass(string id, string prompt, CheckCategory category, string reason)
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
            SmellIds = [],
            ImpactAreas = [],
            Remediation = string.Empty,
        };
    }

    /// <summary>
    /// Convenience factory for a failing check result.
    /// </summary>
    private static ChecklistItem Fail(
        string id,
        string prompt,
        CheckCategory category,
        string reason,
        Priority severity,
        List<int> smellIds,
        List<ImpactArea> impactAreas,
        string remediation)
    {
        return new ChecklistItem
        {
            Id = id,
            Type = CheckType.Deterministic,
            Prompt = prompt,
            Score = false,
            Reason = reason,
            Severity = severity,
            Category = category,
            SmellIds = smellIds,
            ImpactAreas = impactAreas,
            Remediation = remediation,
        };
    }

    /// <summary>
    /// Safely extracts a string property from a <see cref="JsonElement"/>.
    /// Returns <see cref="string.Empty"/> if the property does not exist or is not a string.
    /// </summary>
    private static string GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// Extracts the "properties" object members from an input schema.
    /// Returns an empty list if the schema or properties are missing.
    /// </summary>
    private static List<KeyValuePair<string, JsonElement>> GetProperties(JsonElement? inputSchema)
    {
        if (!inputSchema.HasValue
            || inputSchema.Value.ValueKind != JsonValueKind.Object
            || !inputSchema.Value.TryGetProperty("properties", out JsonElement propsElement)
            || propsElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return propsElement.EnumerateObject()
            .Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value))
            .ToList();
    }

    /// <summary>
    /// Checks whether a schema element has a non-empty "properties" object.
    /// </summary>
    private static bool HasNonEmptyProperties(JsonElement element)
    {
        if (element.TryGetProperty("properties", out JsonElement propsElement)
            && propsElement.ValueKind == JsonValueKind.Object)
        {
            // EnumerateObject on an empty object yields no elements
            using var enumerator = propsElement.EnumerateObject().GetEnumerator();
            return enumerator.MoveNext();
        }

        return false;
    }
}
