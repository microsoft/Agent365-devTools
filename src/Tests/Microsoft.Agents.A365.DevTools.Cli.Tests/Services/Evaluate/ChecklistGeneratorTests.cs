// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

public class ChecklistGeneratorTests
{
    private readonly ChecklistGenerator _generator = new();

    // -----------------------------------------------------------------------
    // Metadata
    // -----------------------------------------------------------------------

    [Fact]
    public void Generate_SetsMetadataCorrectly()
    {
        var tools = new List<ToolSchema>
        {
            CreateToolSchema("get_user", "Retrieves a user by ID."),
        };

        var result = _generator.Generate(tools, "TestServer", "http://localhost:3000");

        result.Metadata.ServerName.Should().Be("TestServer");
        result.Metadata.ServerUrl.Should().Be("http://localhost:3000");
        result.Metadata.ToolCount.Should().Be(1);
        result.Metadata.GeneratorVersion.Should().NotBeNullOrWhiteSpace();
        result.Metadata.GeneratedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Generate_WithEmptyTools_SetsToolCountToZero()
    {
        var result = _generator.Generate([], "Empty", "");

        result.Metadata.ToolCount.Should().Be(0);
        result.Tools.Should().BeEmpty();
    }

    [Fact]
    public void Generate_WithMultipleTools_SetsCorrectToolCount()
    {
        var tools = new List<ToolSchema>
        {
            CreateToolSchema("tool1", "Description 1."),
            CreateToolSchema("tool2", "Description 2."),
            CreateToolSchema("tool3", "Description 3."),
        };

        var result = _generator.Generate(tools, "Server", "url");

        result.Metadata.ToolCount.Should().Be(3);
        result.Tools.Should().HaveCount(3);
    }

    [Fact]
    public void Generate_ThrowsOnNullTools()
    {
        var act = () => _generator.Generate(null!, "Server", "url");
        act.Should().Throw<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------
    // Tool-level structure
    // -----------------------------------------------------------------------

    [Fact]
    public void Generate_ToolChecklist_ContainsToolNameAndDescription()
    {
        var tools = new List<ToolSchema>
        {
            CreateToolSchema("search_users", "Searches for users by name or email."),
        };

        var result = _generator.Generate(tools, "Server", "url");
        var toolChecklist = result.Tools[0];

        toolChecklist.Name.Should().Be("search_users");
        toolChecklist.Description.Should().Be("Searches for users by name or email.");
    }

    [Fact]
    public void Generate_ToolChecklist_HasToolNameChecks()
    {
        var tools = new List<ToolSchema>
        {
            CreateToolSchema("get_user", "Retrieves a user by their unique identifier."),
        };

        var result = _generator.Generate(tools, "Server", "url");
        var toolNameChecks = result.Tools[0].Checks.ToolName;

        // Should contain deterministic + semantic checks
        toolNameChecks.Should().NotBeEmpty();

        // Deterministic tool name checks
        toolNameChecks.Should().Contain(c => c.Id == "tn_present" && c.Type == CheckType.Deterministic);
        toolNameChecks.Should().Contain(c => c.Id == "tn_consistent_casing" && c.Type == CheckType.Deterministic);
        toolNameChecks.Should().Contain(c => c.Id == "tn_no_special_chars" && c.Type == CheckType.Deterministic);
        toolNameChecks.Should().Contain(c => c.Id == "tn_reasonable_length" && c.Type == CheckType.Deterministic);

        // Semantic tool name checks
        toolNameChecks.Should().Contain(c => c.Id == "tn_verb_prefix" && c.Type == CheckType.Semantic);
        toolNameChecks.Should().Contain(c => c.Id == "tn_not_generic" && c.Type == CheckType.Semantic);
        toolNameChecks.Should().Contain(c => c.Id == "tn_descriptive" && c.Type == CheckType.Semantic);
    }

    [Fact]
    public void Generate_ToolChecklist_HasToolDescriptionChecks()
    {
        var tools = new List<ToolSchema>
        {
            CreateToolSchema("get_user", "Retrieves a user by their unique identifier."),
        };

        var result = _generator.Generate(tools, "Server", "url");
        var toolDescChecks = result.Tools[0].Checks.ToolDescription;

        // Deterministic checks
        toolDescChecks.Should().Contain(c => c.Id == "td_present" && c.Type == CheckType.Deterministic);
        toolDescChecks.Should().Contain(c => c.Id == "td_min_length" && c.Type == CheckType.Deterministic);
        toolDescChecks.Should().Contain(c => c.Id == "td_max_length" && c.Type == CheckType.Deterministic);

        // Semantic checks
        toolDescChecks.Should().Contain(c => c.Id == "td_has_purpose" && c.Type == CheckType.Semantic);
        toolDescChecks.Should().Contain(c => c.Id == "td_not_name_echo" && c.Type == CheckType.Semantic);
        toolDescChecks.Should().Contain(c => c.Id == "td_has_usage_guidelines" && c.Type == CheckType.Semantic);
        toolDescChecks.Should().Contain(c => c.Id == "td_has_limitations" && c.Type == CheckType.Semantic);
        toolDescChecks.Should().Contain(c => c.Id == "td_has_return_docs" && c.Type == CheckType.Semantic);
        toolDescChecks.Should().Contain(c => c.Id == "td_has_examples" && c.Type == CheckType.Semantic);
        toolDescChecks.Should().Contain(c => c.Id == "td_no_boilerplate" && c.Type == CheckType.Semantic);
    }

    [Fact]
    public void Generate_ToolChecklist_HasSchemaStructureChecks()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "query": {"type": "string", "description": "The search query to find users by name or email"}
            },
            "required": ["query"]
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "search_users", Description = "Searches for users.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var structureChecks = result.Tools[0].Checks.SchemaStructure;

        structureChecks.Should().Contain(c => c.Id == "ss_has_input_schema");
        structureChecks.Should().Contain(c => c.Id == "ss_type_object");
        structureChecks.Should().Contain(c => c.Id == "ss_no_deep_nesting");
        structureChecks.Should().Contain(c => c.Id == "ss_all_typed");
        structureChecks.Should().Contain(c => c.Id == "ss_arrays_have_items");
        structureChecks.Should().Contain(c => c.Id == "ss_required_matches");
        structureChecks.Should().Contain(c => c.Id == "ss_reasonable_param_count");
        structureChecks.Should().Contain(c => c.Id == "ss_no_empty_objects");
    }

    // -----------------------------------------------------------------------
    // Deterministic checks - Tool Name
    // -----------------------------------------------------------------------

    [Fact]
    public void Generate_ToolNamePresent_PassesForNonEmptyName()
    {
        var result = GenerateSingleTool("get_user", "A description that is long enough.");
        var check = FindCheck(result, "tn_present");

        check.Score.Should().BeTrue();
        check.Type.Should().Be(CheckType.Deterministic);
    }

    [Fact]
    public void Generate_ToolNamePresent_FailsForEmptyName()
    {
        var result = GenerateSingleTool("", "A description.");
        var check = FindCheck(result, "tn_present");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void Generate_ToolNameConsistentCasing_PassesForSnakeCase()
    {
        var result = GenerateSingleTool("get_user_by_id", "Description.");
        var check = FindCheck(result, "tn_consistent_casing");

        check.Score.Should().BeTrue();
        check.Reason.Should().Contain("snake_case");
    }

    [Fact]
    public void Generate_ToolNameConsistentCasing_PassesForCamelCase()
    {
        var result = GenerateSingleTool("getUserById", "Description.");
        var check = FindCheck(result, "tn_consistent_casing");

        check.Score.Should().BeTrue();
        check.Reason.Should().Contain("camelCase");
    }

    [Fact]
    public void Generate_ToolNameConsistentCasing_PassesForPascalCase()
    {
        var result = GenerateSingleTool("GetUserById", "Description.");
        var check = FindCheck(result, "tn_consistent_casing");

        check.Score.Should().BeTrue();
        check.Reason.Should().Contain("PascalCase");
    }

    [Fact]
    public void Generate_ToolNameNoSpecialChars_PassesForCleanName()
    {
        var result = GenerateSingleTool("get_user", "Description.");
        var check = FindCheck(result, "tn_no_special_chars");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_ToolNameNoSpecialChars_FailsForSpecialChars()
    {
        var result = GenerateSingleTool("get user!", "Description.");
        var check = FindCheck(result, "tn_no_special_chars");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void Generate_ToolNameReasonableLength_PassesForNormalLength()
    {
        var result = GenerateSingleTool("get_user", "Description.");
        var check = FindCheck(result, "tn_reasonable_length");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_ToolNameReasonableLength_FailsForTooShort()
    {
        var result = GenerateSingleTool("ab", "Description.");
        var check = FindCheck(result, "tn_reasonable_length");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void Generate_ToolNameReasonableLength_FailsForTooLong()
    {
        var result = GenerateSingleTool(new string('a', 65), "Description.");
        var check = FindCheck(result, "tn_reasonable_length");

        check.Score.Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Deterministic checks - Tool Description
    // -----------------------------------------------------------------------

    [Fact]
    public void Generate_ToolDescPresent_PassesForNonEmptyDescription()
    {
        var result = GenerateSingleTool("get_user", "Retrieves a user by their unique identifier from the system.");
        var check = FindCheck(result, "td_present");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_ToolDescPresent_FailsForEmptyDescription()
    {
        var result = GenerateSingleTool("get_user", "");
        var check = FindCheck(result, "td_present");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void Generate_ToolDescMinLength_PassesForLongDescription()
    {
        var result = GenerateSingleTool("get_user", "Retrieves a user by their unique identifier from the database.");
        var check = FindCheck(result, "td_min_length");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_ToolDescMinLength_FailsForShortDescription()
    {
        var result = GenerateSingleTool("get_user", "Gets a user.");
        var check = FindCheck(result, "td_min_length");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void Generate_ToolDescMaxLength_PassesForNormalDescription()
    {
        var result = GenerateSingleTool("get_user", "Retrieves a user by ID.");
        var check = FindCheck(result, "td_max_length");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_ToolDescMaxLength_FailsForOverlyLongDescription()
    {
        var result = GenerateSingleTool("get_user", new string('a', 2001));
        var check = FindCheck(result, "td_max_length");

        check.Score.Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Deterministic checks - Schema Structure
    // -----------------------------------------------------------------------

    [Fact]
    public void Generate_HasInputSchema_PassesWhenSchemaPresent()
    {
        var schema = JsonDocument.Parse("""{"type": "object", "properties": {}}""").RootElement;
        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.Tools[0].Checks.SchemaStructure.First(c => c.Id == "ss_has_input_schema");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_HasInputSchema_FailsWhenSchemaNull()
    {
        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = null },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.Tools[0].Checks.SchemaStructure.First(c => c.Id == "ss_has_input_schema");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void Generate_TypeObject_PassesWhenTypeIsObject()
    {
        var schema = JsonDocument.Parse("""{"type": "object", "properties": {}}""").RootElement;
        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.Tools[0].Checks.SchemaStructure.First(c => c.Id == "ss_type_object");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_TypeObject_FailsWhenTypeIsNotObject()
    {
        var schema = JsonDocument.Parse("""{"type": "array"}""").RootElement;
        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.Tools[0].Checks.SchemaStructure.First(c => c.Id == "ss_type_object");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void Generate_AllTyped_PassesWhenAllPropertiesHaveTypes()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "name": {"type": "string"},
                "age": {"type": "integer"}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.Tools[0].Checks.SchemaStructure.First(c => c.Id == "ss_all_typed");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_AllTyped_FailsWhenPropertyMissingType()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "name": {"type": "string"},
                "data": {"description": "No type specified"}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.Tools[0].Checks.SchemaStructure.First(c => c.Id == "ss_all_typed");

        check.Score.Should().BeFalse();
        check.Reason.Should().Contain("data");
    }

    [Fact]
    public void Generate_ArraysHaveItems_FailsWhenArrayMissingItems()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "tags": {"type": "array"}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.Tools[0].Checks.SchemaStructure.First(c => c.Id == "ss_arrays_have_items");

        check.Score.Should().BeFalse();
        check.Reason.Should().Contain("tags");
    }

    [Fact]
    public void Generate_ArraysHaveItems_PassesWhenArrayHasItems()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "tags": {"type": "array", "items": {"type": "string"}}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.Tools[0].Checks.SchemaStructure.First(c => c.Id == "ss_arrays_have_items");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_RequiredMatches_FailsForOrphanedRequired()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "name": {"type": "string"}
            },
            "required": ["name", "ghost"]
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.Tools[0].Checks.SchemaStructure.First(c => c.Id == "ss_required_matches");

        check.Score.Should().BeFalse();
        check.Reason.Should().Contain("ghost");
    }

    [Fact]
    public void Generate_ReasonableParamCount_PassesForFewParams()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "a": {"type": "string"},
                "b": {"type": "string"},
                "c": {"type": "string"}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.Tools[0].Checks.SchemaStructure.First(c => c.Id == "ss_reasonable_param_count");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_NoEmptyObjects_FailsForEmptyObjectParam()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "config": {"type": "object"}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.Tools[0].Checks.SchemaStructure.First(c => c.Id == "ss_no_empty_objects");

        check.Score.Should().BeFalse();
        check.Reason.Should().Contain("config");
    }

    // -----------------------------------------------------------------------
    // Parameter checks
    // -----------------------------------------------------------------------

    [Fact]
    public void Generate_CreatesParameterChecksForEachProperty()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "query": {"type": "string", "description": "The search query to find matching records in the database"},
                "limit": {"type": "integer", "description": "Maximum number of results to return from the search"}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "search", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var parameters = result.Tools[0].Checks.Parameters;

        parameters.Should().ContainKey("query");
        parameters.Should().ContainKey("limit");
    }

    [Fact]
    public void Generate_ParamChecks_ContainsDeterministicAndSemantic()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "userId": {"type": "string", "description": "The unique identifier for the user account in the system"}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "get_user", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var paramChecks = result.Tools[0].Checks.Parameters["userId"];

        // ParamName should have deterministic + semantic checks
        paramChecks.ParamName.Should().Contain(c => c.Id == "pn_not_single_char" && c.Type == CheckType.Deterministic);
        paramChecks.ParamName.Should().Contain(c => c.Id == "pn_reasonable_length" && c.Type == CheckType.Deterministic);
        paramChecks.ParamName.Should().Contain(c => c.Id == "pn_not_generic" && c.Type == CheckType.Semantic);

        // ParamDescription should have deterministic + semantic checks
        paramChecks.ParamDescription.Should().Contain(c => c.Id == "pd_present" && c.Type == CheckType.Deterministic);
        paramChecks.ParamDescription.Should().Contain(c => c.Id == "pd_min_length" && c.Type == CheckType.Deterministic);
        paramChecks.ParamDescription.Should().Contain(c => c.Id == "pd_not_name_echo" && c.Type == CheckType.Semantic);
        paramChecks.ParamDescription.Should().Contain(c => c.Id == "pd_has_constraints" && c.Type == CheckType.Semantic);
        paramChecks.ParamDescription.Should().Contain(c => c.Id == "pd_enum_for_categorical" && c.Type == CheckType.Semantic);
    }

    [Fact]
    public void Generate_ParamDescPresent_FailsWhenNoDescription()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "userId": {"type": "string"}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "get_user", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var descChecks = result.Tools[0].Checks.Parameters["userId"].ParamDescription;
        var check = descChecks.First(c => c.Id == "pd_present");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void Generate_ParamDescPresent_PassesWhenDescriptionPresent()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "userId": {"type": "string", "description": "The unique user identifier used to look up the account"}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "get_user", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var descChecks = result.Tools[0].Checks.Parameters["userId"].ParamDescription;
        var check = descChecks.First(c => c.Id == "pd_present");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_ParamNameSingleChar_FailsForSingleCharName()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "x": {"type": "string", "description": "A coordinate value for the position"}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var nameChecks = result.Tools[0].Checks.Parameters["x"].ParamName;
        var check = nameChecks.First(c => c.Id == "pn_not_single_char");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void Generate_ParamDescHasTypeGuidance_PassesWhenTypePresent()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "userId": {"type": "string"}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var descChecks = result.Tools[0].Checks.Parameters["userId"].ParamDescription;
        var check = descChecks.First(c => c.Id == "pd_has_type_guidance");

        check.Score.Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Server-level (toolset) checks
    // -----------------------------------------------------------------------

    [Fact]
    public void Generate_ServerChecks_ContainsDeterministicToolsetChecks()
    {
        var tools = new List<ToolSchema>
        {
            CreateToolSchema("get_user", "Retrieves a user."),
            CreateToolSchema("create_user", "Creates a user."),
        };

        var result = _generator.Generate(tools, "Server", "url");

        result.ServerChecks.Should().Contain(c => c.Id == "ts_reasonable_count" && c.Type == CheckType.Deterministic);
        result.ServerChecks.Should().Contain(c => c.Id == "ts_no_near_duplicate_names" && c.Type == CheckType.Deterministic);
        result.ServerChecks.Should().Contain(c => c.Id == "ts_consistent_naming" && c.Type == CheckType.Deterministic);
        result.ServerChecks.Should().Contain(c => c.Id == "ts_reasonable_token_budget" && c.Type == CheckType.Deterministic);
    }

    [Fact]
    public void Generate_ServerChecks_ContainsSemanticToolsetChecks()
    {
        var tools = new List<ToolSchema>
        {
            CreateToolSchema("get_user", "Retrieves a user."),
        };

        var result = _generator.Generate(tools, "Server", "url");

        result.ServerChecks.Should().Contain(c => c.Id == "ts_no_description_overlap" && c.Type == CheckType.Semantic);
        result.ServerChecks.Should().Contain(c => c.Id == "ts_crud_completeness" && c.Type == CheckType.Semantic);
    }

    [Fact]
    public void Generate_ToolsetReasonableCount_PassesForFewTools()
    {
        var tools = Enumerable.Range(1, 5)
            .Select(i => CreateToolSchema($"tool_{i}", $"Description for tool {i}."))
            .ToList();

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.ServerChecks.First(c => c.Id == "ts_reasonable_count");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_ToolsetReasonableCount_FailsForNoTools()
    {
        var result = _generator.Generate([], "Server", "url");
        var check = result.ServerChecks.First(c => c.Id == "ts_reasonable_count");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P0);
    }

    [Fact]
    public void Generate_ToolsetNoNearDuplicateNames_PassesForDistinctNames()
    {
        var tools = new List<ToolSchema>
        {
            CreateToolSchema("get_user", "Retrieves a user."),
            CreateToolSchema("search_contacts", "Searches contacts."),
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.ServerChecks.First(c => c.Id == "ts_no_near_duplicate_names");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_ToolsetNoNearDuplicateNames_FailsForSimilarNames()
    {
        var tools = new List<ToolSchema>
        {
            CreateToolSchema("get_user", "Retrieves a user."),
            CreateToolSchema("get_users", "Retrieves users."),
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.ServerChecks.First(c => c.Id == "ts_no_near_duplicate_names");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void Generate_ToolsetConsistentNaming_PassesWhenAllSameConvention()
    {
        var tools = new List<ToolSchema>
        {
            CreateToolSchema("get_user", "Retrieves a user."),
            CreateToolSchema("create_user", "Creates a user."),
            CreateToolSchema("delete_user", "Deletes a user."),
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.ServerChecks.First(c => c.Id == "ts_consistent_naming");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_ToolsetConsistentNaming_FailsForMixedConventions()
    {
        var tools = new List<ToolSchema>
        {
            CreateToolSchema("get_user", "Retrieves a user."),
            CreateToolSchema("create_user", "Creates a user."),
            CreateToolSchema("DeleteUser", "Deletes a user."),
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.ServerChecks.First(c => c.Id == "ts_consistent_naming");

        check.Score.Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Semantic checks have null scores
    // -----------------------------------------------------------------------

    [Fact]
    public void Generate_SemanticChecks_AllHaveNullScore()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "query": {"type": "string", "description": "The search query to find matching records in the database"}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "search", Description = "Searches for records.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");

        // Collect all semantic checks from all locations
        var allSemanticChecks = new List<ChecklistItem>();
        foreach (var tool in result.Tools)
        {
            allSemanticChecks.AddRange(tool.Checks.ToolName.Where(c => c.Type == CheckType.Semantic));
            allSemanticChecks.AddRange(tool.Checks.ToolDescription.Where(c => c.Type == CheckType.Semantic));
            foreach (var paramGroup in tool.Checks.Parameters.Values)
            {
                allSemanticChecks.AddRange(paramGroup.ParamName.Where(c => c.Type == CheckType.Semantic));
                allSemanticChecks.AddRange(paramGroup.ParamDescription.Where(c => c.Type == CheckType.Semantic));
            }
        }
        allSemanticChecks.AddRange(result.ServerChecks.Where(c => c.Type == CheckType.Semantic));

        allSemanticChecks.Should().NotBeEmpty();
        allSemanticChecks.Should().AllSatisfy(c =>
        {
            c.Score.Should().BeNull($"semantic check '{c.Id}' should have null score");
            c.Reason.Should().BeNull($"semantic check '{c.Id}' should have null reason");
        });
    }

    [Fact]
    public void Generate_DeterministicChecks_AllHaveNonNullScore()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "query": {"type": "string", "description": "The search query to find matching records in the database"}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "search", Description = "Searches for records.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");

        // Collect all deterministic checks from all locations
        var allDeterministicChecks = new List<ChecklistItem>();
        foreach (var tool in result.Tools)
        {
            allDeterministicChecks.AddRange(tool.Checks.ToolName.Where(c => c.Type == CheckType.Deterministic));
            allDeterministicChecks.AddRange(tool.Checks.ToolDescription.Where(c => c.Type == CheckType.Deterministic));
            allDeterministicChecks.AddRange(tool.Checks.SchemaStructure.Where(c => c.Type == CheckType.Deterministic));
            foreach (var paramGroup in tool.Checks.Parameters.Values)
            {
                allDeterministicChecks.AddRange(paramGroup.ParamName.Where(c => c.Type == CheckType.Deterministic));
                allDeterministicChecks.AddRange(paramGroup.ParamDescription.Where(c => c.Type == CheckType.Deterministic));
            }
        }
        allDeterministicChecks.AddRange(result.ServerChecks.Where(c => c.Type == CheckType.Deterministic));

        allDeterministicChecks.Should().NotBeEmpty();
        allDeterministicChecks.Should().AllSatisfy(c =>
        {
            c.Score.Should().NotBeNull($"deterministic check '{c.Id}' should have a non-null score");
            c.Reason.Should().NotBeNullOrWhiteSpace($"deterministic check '{c.Id}' should have a non-null reason");
        });
    }

    // -----------------------------------------------------------------------
    // Deep nesting check
    // -----------------------------------------------------------------------

    [Fact]
    public void Generate_NoDeepNesting_PassesForShallowSchema()
    {
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "name": {"type": "string"}
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.Tools[0].Checks.SchemaStructure.First(c => c.Id == "ss_no_deep_nesting");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void Generate_NoDeepNesting_FailsForDeeplyNestedSchema()
    {
        // depth: object -> props -> config -> props -> inner -> props -> deep -> props -> leaf = depth 4
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "config": {
                    "type": "object",
                    "properties": {
                        "inner": {
                            "type": "object",
                            "properties": {
                                "deep": {
                                    "type": "object",
                                    "properties": {
                                        "leaf": {"type": "string"}
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        """).RootElement;

        var tools = new List<ToolSchema>
        {
            new() { Name = "tool", Description = "Description.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");
        var check = result.Tools[0].Checks.SchemaStructure.First(c => c.Id == "ss_no_deep_nesting");

        check.Score.Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // No parameters scenario
    // -----------------------------------------------------------------------

    [Fact]
    public void Generate_WithNoParameters_HasEmptyParameterChecks()
    {
        var schema = JsonDocument.Parse("""{"type": "object", "properties": {}}""").RootElement;
        var tools = new List<ToolSchema>
        {
            new() { Name = "ping", Description = "Pings the server.", InputSchema = schema },
        };

        var result = _generator.Generate(tools, "Server", "url");

        result.Tools[0].Checks.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Generate_WithNullInputSchema_HasEmptyParameterChecks()
    {
        var tools = new List<ToolSchema>
        {
            new() { Name = "ping", Description = "Pings the server.", InputSchema = null },
        };

        var result = _generator.Generate(tools, "Server", "url");

        result.Tools[0].Checks.Parameters.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static ToolSchema CreateToolSchema(string name, string description)
    {
        return new ToolSchema { Name = name, Description = description, InputSchema = null };
    }

    private EvaluationChecklist GenerateSingleTool(string name, string description)
    {
        var tools = new List<ToolSchema> { CreateToolSchema(name, description) };
        return _generator.Generate(tools, "Server", "url");
    }

    private static ChecklistItem FindCheck(EvaluationChecklist checklist, string checkId)
    {
        var allChecks = new List<ChecklistItem>();
        foreach (var tool in checklist.Tools)
        {
            allChecks.AddRange(tool.Checks.ToolName);
            allChecks.AddRange(tool.Checks.ToolDescription);
            allChecks.AddRange(tool.Checks.SchemaStructure);
            foreach (var paramGroup in tool.Checks.Parameters.Values)
            {
                allChecks.AddRange(paramGroup.ParamName);
                allChecks.AddRange(paramGroup.ParamDescription);
            }
        }
        allChecks.AddRange(checklist.ServerChecks);

        return allChecks.First(c => c.Id == checkId);
    }
}
