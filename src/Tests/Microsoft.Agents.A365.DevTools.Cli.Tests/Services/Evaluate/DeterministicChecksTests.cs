// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

public class DeterministicChecksTests
{
    // =======================================================================
    // Tool Name Checks
    // =======================================================================

    // -- tn_present ---------------------------------------------------------

    [Fact]
    public void RunToolNameChecks_EmptyName_TnPresentFails()
    {
        var results = DeterministicChecks.RunToolNameChecks(string.Empty);
        var check = results.First(c => c.Id == "tn_present");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P0);
    }

    [Fact]
    public void RunToolNameChecks_WhitespaceName_TnPresentFails()
    {
        var results = DeterministicChecks.RunToolNameChecks("   ");
        var check = results.First(c => c.Id == "tn_present");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void RunToolNameChecks_ValidName_TnPresentPasses()
    {
        var results = DeterministicChecks.RunToolNameChecks("get_user");
        var check = results.First(c => c.Id == "tn_present");

        check.Score.Should().BeTrue();
    }

    // -- tn_consistent_casing -----------------------------------------------

    [Theory]
    [InlineData("get_user", true)]        // snake_case
    [InlineData("getUser", true)]          // camelCase
    [InlineData("GetUser", true)]          // PascalCase
    [InlineData("get-user", true)]         // kebab-case
    [InlineData("Get_User", false)]        // mixed
    [InlineData("get_User_name", false)]   // mixed
    public void RunToolNameChecks_CasingConventions_TnConsistentCasing(string name, bool expectedPass)
    {
        var results = DeterministicChecks.RunToolNameChecks(name);
        var check = results.First(c => c.Id == "tn_consistent_casing");

        check.Score.Should().Be(expectedPass);
    }

    // -- tn_no_special_chars ------------------------------------------------

    [Theory]
    [InlineData("get_user", true)]
    [InlineData("get-user", true)]
    [InlineData("get.user", true)]
    [InlineData("get user", false)]       // space
    [InlineData("get@user", false)]       // @
    [InlineData("get#user!", false)]      // # and !
    public void RunToolNameChecks_SpecialChars_TnNoSpecialChars(string name, bool expectedPass)
    {
        var results = DeterministicChecks.RunToolNameChecks(name);
        var check = results.First(c => c.Id == "tn_no_special_chars");

        check.Score.Should().Be(expectedPass);
    }

    [Fact]
    public void RunToolNameChecks_EmptyName_TnNoSpecialCharsFails()
    {
        var results = DeterministicChecks.RunToolNameChecks(string.Empty);
        var check = results.First(c => c.Id == "tn_no_special_chars");

        check.Score.Should().BeFalse();
    }

    // -- tn_reasonable_length -----------------------------------------------

    [Theory]
    [InlineData("ab", false)]                     // length 2, below minimum
    [InlineData("abc", true)]                     // length 3, at minimum
    [InlineData("get_user_by_id_from_database", true)] // reasonable length
    public void RunToolNameChecks_Length_TnReasonableLength(string name, bool expectedPass)
    {
        var results = DeterministicChecks.RunToolNameChecks(name);
        var check = results.First(c => c.Id == "tn_reasonable_length");

        check.Score.Should().Be(expectedPass);
    }

    [Fact]
    public void RunToolNameChecks_Length64_TnReasonableLengthPasses()
    {
        string name = new string('a', 64);
        var results = DeterministicChecks.RunToolNameChecks(name);
        var check = results.First(c => c.Id == "tn_reasonable_length");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunToolNameChecks_Length65_TnReasonableLengthFails()
    {
        string name = new string('a', 65);
        var results = DeterministicChecks.RunToolNameChecks(name);
        var check = results.First(c => c.Id == "tn_reasonable_length");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void RunToolNameChecks_Returns4Checks()
    {
        var results = DeterministicChecks.RunToolNameChecks("get_user");
        results.Should().HaveCount(4);
    }

    // =======================================================================
    // Tool Description Checks
    // =======================================================================

    // -- td_present ---------------------------------------------------------

    [Fact]
    public void RunToolDescriptionChecks_EmptyDescription_TdPresentFails()
    {
        var results = DeterministicChecks.RunToolDescriptionChecks(string.Empty);
        var check = results.First(c => c.Id == "td_present");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P0);
    }

    [Fact]
    public void RunToolDescriptionChecks_ValidDescription_TdPresentPasses()
    {
        var results = DeterministicChecks.RunToolDescriptionChecks("Fetches user data from the server");
        var check = results.First(c => c.Id == "td_present");

        check.Score.Should().BeTrue();
    }

    // -- td_min_length ------------------------------------------------------

    [Fact]
    public void RunToolDescriptionChecks_19Chars_TdMinLengthFails()
    {
        // Exactly 19 chars (below 20 minimum)
        string desc = "Short description.x";
        desc.Trim().Length.Should().Be(19, "test setup: verifying exactly 19 chars");

        var results = DeterministicChecks.RunToolDescriptionChecks(desc);
        var check = results.First(c => c.Id == "td_min_length");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void RunToolDescriptionChecks_20Chars_TdMinLengthPasses()
    {
        // Exactly 20 chars
        string desc = "Short description.xy";
        desc.Trim().Length.Should().Be(20, "test setup: verifying exactly 20 chars");

        var results = DeterministicChecks.RunToolDescriptionChecks(desc);
        var check = results.First(c => c.Id == "td_min_length");

        check.Score.Should().BeTrue();
    }

    // -- td_max_length ------------------------------------------------------

    [Fact]
    public void RunToolDescriptionChecks_2000Chars_TdMaxLengthPasses()
    {
        string desc = new string('a', 2000);
        var results = DeterministicChecks.RunToolDescriptionChecks(desc);
        var check = results.First(c => c.Id == "td_max_length");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunToolDescriptionChecks_2001Chars_TdMaxLengthFails()
    {
        string desc = new string('a', 2001);
        var results = DeterministicChecks.RunToolDescriptionChecks(desc);
        var check = results.First(c => c.Id == "td_max_length");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void RunToolDescriptionChecks_Returns3Checks()
    {
        var results = DeterministicChecks.RunToolDescriptionChecks("A valid tool description that is long enough.");
        results.Should().HaveCount(3);
    }

    // =======================================================================
    // Schema Structure Checks
    // =======================================================================

    // -- ss_has_input_schema ------------------------------------------------

    [Fact]
    public void RunSchemaStructureChecks_NullSchema_SsHasInputSchemaFails()
    {
        var results = DeterministicChecks.RunSchemaStructureChecks(null);
        var check = results.First(c => c.Id == "ss_has_input_schema");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P0);
    }

    [Fact]
    public void RunSchemaStructureChecks_ValidObjectSchema_SsHasInputSchemaPasses()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"id":{"type":"string"}}}""").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_has_input_schema");

        check.Score.Should().BeTrue();
    }

    // -- ss_type_object -----------------------------------------------------

    [Fact]
    public void RunSchemaStructureChecks_TypeObject_SsTypeObjectPasses()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_type_object");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunSchemaStructureChecks_TypeArray_SsTypeObjectFails()
    {
        var schema = JsonDocument.Parse("""{"type":"array"}""").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_type_object");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void RunSchemaStructureChecks_NullSchema_SsTypeObjectAutoPassesWithReason()
    {
        var results = DeterministicChecks.RunSchemaStructureChecks(null);
        var check = results.First(c => c.Id == "ss_type_object");

        check.Score.Should().BeTrue();
        check.Reason.Should().Contain("No schema");
    }

    // -- ss_no_deep_nesting -------------------------------------------------

    [Fact]
    public void RunSchemaStructureChecks_Depth3_SsNoDeepNestingPasses()
    {
        // Depth 3: root -> level1 -> level2 -> level3 (properties nested 3 levels)
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "level1": {
                    "type": "object",
                    "properties": {
                        "level2": {
                            "type": "object",
                            "properties": {
                                "level3": {"type": "string"}
                            }
                        }
                    }
                }
            }
        }
        """).RootElement;

        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_no_deep_nesting");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunSchemaStructureChecks_Depth4_SsNoDeepNestingFails()
    {
        // Depth 4: root -> l1 -> l2 -> l3 -> l4
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "l1": {
                    "type": "object",
                    "properties": {
                        "l2": {
                            "type": "object",
                            "properties": {
                                "l3": {
                                    "type": "object",
                                    "properties": {
                                        "l4": {"type": "string"}
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        """).RootElement;

        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_no_deep_nesting");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P0);
    }

    [Fact]
    public void RunSchemaStructureChecks_Depth3Exactly_SsNoDeepNestingSeverityP1()
    {
        // Depth 3: passes but with P1 severity
        var schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "a": {
                    "type": "object",
                    "properties": {
                        "b": {
                            "type": "object",
                            "properties": {
                                "c": {"type":"string"}
                            }
                        }
                    }
                }
            }
        }
        """).RootElement;

        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_no_deep_nesting");

        check.Score.Should().BeTrue();
        check.Severity.Should().Be(Priority.P1);
    }

    // -- ss_all_typed -------------------------------------------------------

    [Fact]
    public void RunSchemaStructureChecks_AllPropsTyped_SsAllTypedPasses()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"id":{"type":"string"},"count":{"type":"integer"}}}""").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_all_typed");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunSchemaStructureChecks_UntypedProp_SsAllTypedFails()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"id":{}}}""").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_all_typed");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P0);
    }

    [Fact]
    public void RunSchemaStructureChecks_PropWithRef_SsAllTypedPasses()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"ref_prop":{"$ref":"#/definitions/Foo"}}}""").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_all_typed");

        check.Score.Should().BeTrue();
    }

    // -- ss_arrays_have_items -----------------------------------------------

    [Fact]
    public void RunSchemaStructureChecks_ArrayWithItems_SsArraysHaveItemsPasses()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"tags":{"type":"array","items":{"type":"string"}}}}""").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_arrays_have_items");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunSchemaStructureChecks_ArrayWithoutItems_SsArraysHaveItemsFails()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"tags":{"type":"array"}}}""").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_arrays_have_items");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P0);
    }

    // -- ss_required_matches ------------------------------------------------

    [Fact]
    public void RunSchemaStructureChecks_RequiredMatchesProperties_SsRequiredMatchesPasses()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_required_matches");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunSchemaStructureChecks_RequiredOrphan_SsRequiredMatchesFails()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"id":{"type":"string"}},"required":["id","missing_field"]}""").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_required_matches");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void RunSchemaStructureChecks_NoRequiredField_SsRequiredMatchesAutoPass()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"id":{"type":"string"}}}""").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_required_matches");

        check.Score.Should().BeTrue();
    }

    // -- ss_reasonable_param_count ------------------------------------------

    [Fact]
    public void RunSchemaStructureChecks_10Params_SsReasonableParamCountPasses()
    {
        var props = string.Join(",", Enumerable.Range(1, 10).Select(i => $"\"p{i}\":{{\"type\":\"string\"}}"));
        var schema = JsonDocument.Parse($"{{\"type\":\"object\",\"properties\":{{{props}}}}}").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_reasonable_param_count");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunSchemaStructureChecks_11Params_SsReasonableParamCountFailsP1()
    {
        var props = string.Join(",", Enumerable.Range(1, 11).Select(i => $"\"p{i}\":{{\"type\":\"string\"}}"));
        var schema = JsonDocument.Parse($"{{\"type\":\"object\",\"properties\":{{{props}}}}}").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_reasonable_param_count");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P1);
    }

    [Fact]
    public void RunSchemaStructureChecks_21Params_SsReasonableParamCountFailsP0()
    {
        var props = string.Join(",", Enumerable.Range(1, 21).Select(i => $"\"p{i}\":{{\"type\":\"string\"}}"));
        var schema = JsonDocument.Parse($"{{\"type\":\"object\",\"properties\":{{{props}}}}}").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_reasonable_param_count");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P0);
    }

    // -- ss_no_empty_objects ------------------------------------------------

    [Fact]
    public void RunSchemaStructureChecks_ObjectWithProperties_SsNoEmptyObjectsPasses()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"data":{"type":"object","properties":{"id":{"type":"string"}}}}}""").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_no_empty_objects");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunSchemaStructureChecks_EmptyObject_SsNoEmptyObjectsFails()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"data":{"type":"object"}}}""").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);
        var check = results.First(c => c.Id == "ss_no_empty_objects");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P1);
    }

    [Fact]
    public void RunSchemaStructureChecks_Returns8Checks()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"id":{"type":"string"}}}""").RootElement;
        var results = DeterministicChecks.RunSchemaStructureChecks(schema);

        results.Should().HaveCount(8);
    }

    // =======================================================================
    // Parameter Name Checks
    // =======================================================================

    // -- pn_not_single_char -------------------------------------------------

    [Fact]
    public void RunParamNameChecks_SingleChar_PnNotSingleCharFails()
    {
        var results = DeterministicChecks.RunParamNameChecks("x", null);
        var check = results.First(c => c.Id == "pn_not_single_char");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P1);
    }

    [Fact]
    public void RunParamNameChecks_TwoChars_PnNotSingleCharPasses()
    {
        var results = DeterministicChecks.RunParamNameChecks("id", null);
        var check = results.First(c => c.Id == "pn_not_single_char");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunParamNameChecks_Empty_PnNotSingleCharFails()
    {
        var results = DeterministicChecks.RunParamNameChecks(string.Empty, null);
        var check = results.First(c => c.Id == "pn_not_single_char");

        check.Score.Should().BeFalse();
    }

    // -- pn_reasonable_length -----------------------------------------------

    [Theory]
    [InlineData("a", false)]                   // length 1
    [InlineData("id", true)]                   // length 2 (minimum)
    public void RunParamNameChecks_Length_PnReasonableLength(string name, bool expectedPass)
    {
        var results = DeterministicChecks.RunParamNameChecks(name, null);
        var check = results.First(c => c.Id == "pn_reasonable_length");

        check.Score.Should().Be(expectedPass);
    }

    [Fact]
    public void RunParamNameChecks_Length40_PnReasonableLengthPasses()
    {
        string name = new string('a', 40);
        var results = DeterministicChecks.RunParamNameChecks(name, null);
        var check = results.First(c => c.Id == "pn_reasonable_length");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunParamNameChecks_Length41_PnReasonableLengthFails()
    {
        string name = new string('a', 41);
        var results = DeterministicChecks.RunParamNameChecks(name, null);
        var check = results.First(c => c.Id == "pn_reasonable_length");

        check.Score.Should().BeFalse();
    }

    // -- pn_consistent_casing -----------------------------------------------

    [Fact]
    public void RunParamNameChecks_SingleParam_PnConsistentCasingAutoPass()
    {
        var results = DeterministicChecks.RunParamNameChecks("userId", null);
        var check = results.First(c => c.Id == "pn_consistent_casing");

        check.Score.Should().BeTrue();
        check.Reason.Should().Contain("Only one parameter");
    }

    [Fact]
    public void RunParamNameChecks_SingleParamInList_PnConsistentCasingAutoPass()
    {
        var results = DeterministicChecks.RunParamNameChecks("userId", ["userId"]);
        var check = results.First(c => c.Id == "pn_consistent_casing");

        check.Score.Should().BeTrue();
        check.Reason.Should().Contain("Only one parameter");
    }

    [Fact]
    public void RunParamNameChecks_ConsistentCamelCase_PnConsistentCasingPasses()
    {
        var allParams = new List<string> { "userId", "userName", "userEmail" };
        var results = DeterministicChecks.RunParamNameChecks("userId", allParams);
        var check = results.First(c => c.Id == "pn_consistent_casing");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunParamNameChecks_InconsistentCasing_PnConsistentCasingFails()
    {
        // Dominant is camelCase, but user_name is snake_case
        var allParams = new List<string> { "userId", "userName", "user_name" };
        var results = DeterministicChecks.RunParamNameChecks("user_name", allParams);
        var check = results.First(c => c.Id == "pn_consistent_casing");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void RunParamNameChecks_Returns3Checks()
    {
        var results = DeterministicChecks.RunParamNameChecks("userId", null);
        results.Should().HaveCount(3);
    }

    // =======================================================================
    // Parameter Description Checks
    // =======================================================================

    // -- pd_present ---------------------------------------------------------

    [Fact]
    public void RunParamDescriptionChecks_NoDescription_PdPresentFails()
    {
        var paramSchema = JsonDocument.Parse("""{"type":"string"}""").RootElement;
        var results = DeterministicChecks.RunParamDescriptionChecks("userId", paramSchema);
        var check = results.First(c => c.Id == "pd_present");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P0);
    }

    [Fact]
    public void RunParamDescriptionChecks_HasDescription_PdPresentPasses()
    {
        var paramSchema = JsonDocument.Parse("""{"type":"string","description":"The unique user identifier"}""").RootElement;
        var results = DeterministicChecks.RunParamDescriptionChecks("userId", paramSchema);
        var check = results.First(c => c.Id == "pd_present");

        check.Score.Should().BeTrue();
    }

    // -- pd_min_length (counts WORDS, not characters) -----------------------

    [Fact]
    public void RunParamDescriptionChecks_4Words_PdMinLengthFails()
    {
        // Exactly 4 words
        var paramSchema = JsonDocument.Parse("""{"type":"string","description":"The user unique identifier"}""").RootElement;
        var results = DeterministicChecks.RunParamDescriptionChecks("userId", paramSchema);
        var check = results.First(c => c.Id == "pd_min_length");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void RunParamDescriptionChecks_5Words_PdMinLengthPasses()
    {
        // Exactly 5 words
        var paramSchema = JsonDocument.Parse("""{"type":"string","description":"The unique user identifier value"}""").RootElement;
        var results = DeterministicChecks.RunParamDescriptionChecks("userId", paramSchema);
        var check = results.First(c => c.Id == "pd_min_length");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunParamDescriptionChecks_NoDescription_PdMinLengthFails()
    {
        var paramSchema = JsonDocument.Parse("""{"type":"string"}""").RootElement;
        var results = DeterministicChecks.RunParamDescriptionChecks("userId", paramSchema);
        var check = results.First(c => c.Id == "pd_min_length");

        check.Score.Should().BeFalse();
    }

    // -- pd_has_type_guidance -----------------------------------------------

    [Fact]
    public void RunParamDescriptionChecks_HasTypeProperty_PdHasTypeGuidancePasses()
    {
        var paramSchema = JsonDocument.Parse("""{"type":"string","description":"some text"}""").RootElement;
        var results = DeterministicChecks.RunParamDescriptionChecks("userId", paramSchema);
        var check = results.First(c => c.Id == "pd_has_type_guidance");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunParamDescriptionChecks_NoTypeButKeywordInDesc_PdHasTypeGuidancePasses()
    {
        // "id" is a keyword, even as substring of "valid"
        var paramSchema = JsonDocument.Parse("""{"description":"A valid token for auth"}""").RootElement;
        var results = DeterministicChecks.RunParamDescriptionChecks("token", paramSchema);
        var check = results.First(c => c.Id == "pd_has_type_guidance");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunParamDescriptionChecks_NoTypeNoKeyword_PdHasTypeGuidanceFails()
    {
        var paramSchema = JsonDocument.Parse("""{"description":"the value for the parameter"}""").RootElement;
        var results = DeterministicChecks.RunParamDescriptionChecks("foo", paramSchema);
        var check = results.First(c => c.Id == "pd_has_type_guidance");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void RunParamDescriptionChecks_UrlKeyword_PdHasTypeGuidancePasses()
    {
        var paramSchema = JsonDocument.Parse("""{"description":"the url of the resource"}""").RootElement;
        var results = DeterministicChecks.RunParamDescriptionChecks("endpoint", paramSchema);
        var check = results.First(c => c.Id == "pd_has_type_guidance");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunParamDescriptionChecks_Returns3Checks()
    {
        var paramSchema = JsonDocument.Parse("""{"type":"string","description":"A long enough description here"}""").RootElement;
        var results = DeterministicChecks.RunParamDescriptionChecks("userId", paramSchema);

        results.Should().HaveCount(3);
    }

    // =======================================================================
    // Toolset Design Checks
    // =======================================================================

    // -- ts_reasonable_count ------------------------------------------------

    [Fact]
    public void RunToolsetChecks_EmptyTools_TsReasonableCountFails()
    {
        var results = DeterministicChecks.RunToolsetChecks([]);
        var check = results.First(c => c.Id == "ts_reasonable_count");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P0);
    }

    [Fact]
    public void RunToolsetChecks_15Tools_TsReasonableCountPasses()
    {
        var tools = CreateToolElements(15);
        var results = DeterministicChecks.RunToolsetChecks(tools);
        var check = results.First(c => c.Id == "ts_reasonable_count");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunToolsetChecks_16Tools_TsReasonableCountFailsP1()
    {
        var tools = CreateToolElements(16);
        var results = DeterministicChecks.RunToolsetChecks(tools);
        var check = results.First(c => c.Id == "ts_reasonable_count");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P1);
    }

    [Fact]
    public void RunToolsetChecks_41Tools_TsReasonableCountFailsP0()
    {
        var tools = CreateToolElements(41);
        var results = DeterministicChecks.RunToolsetChecks(tools);
        var check = results.First(c => c.Id == "ts_reasonable_count");

        check.Score.Should().BeFalse();
        check.Severity.Should().Be(Priority.P0);
    }

    // -- ts_no_near_duplicate_names -----------------------------------------

    [Fact]
    public void RunToolsetChecks_DistinctNames_TsNoNearDuplicateNamesPasses()
    {
        var tools = new List<JsonElement>
        {
            JsonDocument.Parse("""{"name":"get_user"}""").RootElement,
            JsonDocument.Parse("""{"name":"create_item"}""").RootElement,
            JsonDocument.Parse("""{"name":"delete_order"}""").RootElement,
        };

        var results = DeterministicChecks.RunToolsetChecks(tools);
        var check = results.First(c => c.Id == "ts_no_near_duplicate_names");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunToolsetChecks_NearDuplicateDistance1_TsNoNearDuplicateNamesFails()
    {
        // "get_user" and "get_uses" differ by Levenshtein distance 1
        var tools = new List<JsonElement>
        {
            JsonDocument.Parse("""{"name":"get_user"}""").RootElement,
            JsonDocument.Parse("""{"name":"get_uses"}""").RootElement,
        };

        var results = DeterministicChecks.RunToolsetChecks(tools);
        var check = results.First(c => c.Id == "ts_no_near_duplicate_names");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void RunToolsetChecks_NearDuplicateDistance2_TsNoNearDuplicateNamesFails()
    {
        // "get_user" and "get_uzer" differ by Levenshtein distance 2
        var tools = new List<JsonElement>
        {
            JsonDocument.Parse("""{"name":"get_user"}""").RootElement,
            JsonDocument.Parse("""{"name":"get_uzez"}""").RootElement,
        };

        var results = DeterministicChecks.RunToolsetChecks(tools);
        var check = results.First(c => c.Id == "ts_no_near_duplicate_names");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void RunToolsetChecks_Distance3_TsNoNearDuplicateNamesPasses()
    {
        // "get_user" and "get_abcd" differ by distance >= 3
        var tools = new List<JsonElement>
        {
            JsonDocument.Parse("""{"name":"get_user"}""").RootElement,
            JsonDocument.Parse("""{"name":"get_abcd"}""").RootElement,
        };

        var results = DeterministicChecks.RunToolsetChecks(tools);
        var check = results.First(c => c.Id == "ts_no_near_duplicate_names");

        check.Score.Should().BeTrue();
    }

    // -- ts_consistent_naming -----------------------------------------------

    [Fact]
    public void RunToolsetChecks_ConsistentSnakeCase_TsConsistentNamingPasses()
    {
        var tools = new List<JsonElement>
        {
            JsonDocument.Parse("""{"name":"get_user"}""").RootElement,
            JsonDocument.Parse("""{"name":"create_item"}""").RootElement,
        };

        var results = DeterministicChecks.RunToolsetChecks(tools);
        var check = results.First(c => c.Id == "ts_consistent_naming");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunToolsetChecks_MixedNaming_TsConsistentNamingFails()
    {
        var tools = new List<JsonElement>
        {
            JsonDocument.Parse("""{"name":"get_user"}""").RootElement,
            JsonDocument.Parse("""{"name":"createItem"}""").RootElement,
            JsonDocument.Parse("""{"name":"delete_order"}""").RootElement,
        };

        var results = DeterministicChecks.RunToolsetChecks(tools);
        var check = results.First(c => c.Id == "ts_consistent_naming");

        check.Score.Should().BeFalse();
    }

    [Fact]
    public void RunToolsetChecks_SingleTool_TsConsistentNamingAutoPass()
    {
        var tools = new List<JsonElement>
        {
            JsonDocument.Parse("""{"name":"get_user"}""").RootElement,
        };

        var results = DeterministicChecks.RunToolsetChecks(tools);
        var check = results.First(c => c.Id == "ts_consistent_naming");

        check.Score.Should().BeTrue();
        check.Reason.Should().Contain("Fewer than 2");
    }

    // -- ts_reasonable_token_budget ------------------------------------------

    [Fact]
    public void RunToolsetChecks_SmallSchemas_TsReasonableTokenBudgetPasses()
    {
        var tools = new List<JsonElement>
        {
            JsonDocument.Parse("""{"name":"get_user","description":"Gets user"}""").RootElement,
        };

        var results = DeterministicChecks.RunToolsetChecks(tools);
        var check = results.First(c => c.Id == "ts_reasonable_token_budget");

        check.Score.Should().BeTrue();
    }

    [Fact]
    public void RunToolsetChecks_Returns4Checks()
    {
        var tools = new List<JsonElement>
        {
            JsonDocument.Parse("""{"name":"tool_one"}""").RootElement,
            JsonDocument.Parse("""{"name":"tool_two"}""").RootElement,
        };

        var results = DeterministicChecks.RunToolsetChecks(tools);
        results.Should().HaveCount(4);
    }

    // =======================================================================
    // Cross-cutting properties
    // =======================================================================

    [Fact]
    public void AllChecks_HaveDeterministicType()
    {
        var nameChecks = DeterministicChecks.RunToolNameChecks("get_user");
        var descChecks = DeterministicChecks.RunToolDescriptionChecks("A useful tool description here");
        var schemaChecks = DeterministicChecks.RunSchemaStructureChecks(
            JsonDocument.Parse("""{"type":"object","properties":{"id":{"type":"string"}}}""").RootElement);
        var paramNameChecks = DeterministicChecks.RunParamNameChecks("userId", null);
        var paramDescChecks = DeterministicChecks.RunParamDescriptionChecks("userId",
            JsonDocument.Parse("""{"type":"string","description":"The unique user identifier value"}""").RootElement);
        var toolsetChecks = DeterministicChecks.RunToolsetChecks(
            [JsonDocument.Parse("""{"name":"get_user"}""").RootElement]);

        var allChecks = nameChecks
            .Concat(descChecks)
            .Concat(schemaChecks)
            .Concat(paramNameChecks)
            .Concat(paramDescChecks)
            .Concat(toolsetChecks)
            .ToList();

        allChecks.Should().AllSatisfy(c => c.Type.Should().Be(CheckType.Deterministic));
    }

    [Fact]
    public void AllChecks_HaveNonEmptyId()
    {
        var nameChecks = DeterministicChecks.RunToolNameChecks("get_user");
        nameChecks.Should().AllSatisfy(c => c.Id.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void AllChecks_HaveNonEmptyPrompt()
    {
        var nameChecks = DeterministicChecks.RunToolNameChecks("get_user");
        nameChecks.Should().AllSatisfy(c => c.Prompt.Should().NotBeNullOrWhiteSpace());
    }

    // =======================================================================
    // Helper methods
    // =======================================================================

    /// <summary>
    /// Creates a list of simple tool JsonElements with distinct names.
    /// </summary>
    private static List<JsonElement> CreateToolElements(int count)
    {
        var tools = new List<JsonElement>(count);
        for (int i = 0; i < count; i++)
        {
            // Use distinct names with enough distance to avoid near-duplicate detection
            tools.Add(JsonDocument.Parse($"{{\"name\":\"tool_alpha_{i:D4}\"}}").RootElement);
        }

        return tools;
    }
}
