// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CheckCategory
{
    ToolName,
    ToolDescription,
    ParamName,
    ParamDescription,
    SchemaStructure,
    ToolsetDesign
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Priority
{
    P0,
    P1,
    P2,
    P3
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImpactArea
{
    ToolSelection,
    ParamAccuracy,
    Completeness,
    Conciseness
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IssueCategory
{
    Accuracy,
    Functionality,
    Completeness,
    Conciseness
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CheckType
{
    Deterministic,
    Semantic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvalEngine
{
    Auto,
    GitHubCopilot,
    ClaudeCode,
    None
}
