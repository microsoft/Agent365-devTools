// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class Agent365ToolingServicePureFunctionTests
{
    // --- ExtractErrorMessage tests ---

    [Fact]
    public void ExtractErrorMessage_ReturnsNull_WhenContentIsNull()
    {
        Agent365ToolingService.ExtractErrorMessage(null).Should().BeNull();
    }

    [Fact]
    public void ExtractErrorMessage_ReturnsNull_WhenContentIsEmpty()
    {
        Agent365ToolingService.ExtractErrorMessage("").Should().BeNull();
    }

    [Fact]
    public void ExtractErrorMessage_ExtractsDetails_WhenPresent()
    {
        var json = """{"details":"Something went wrong"}""";
        Agent365ToolingService.ExtractErrorMessage(json).Should().Be("Something went wrong");
    }

    [Fact]
    public void ExtractErrorMessage_ExtractsErrorString_WhenPresent()
    {
        var json = """{"error":"Bad request"}""";
        Agent365ToolingService.ExtractErrorMessage(json).Should().Be("Bad request");
    }

    [Fact]
    public void ExtractErrorMessage_ExtractsErrorObjectMessage_WhenPresent()
    {
        var json = """{"error":{"message":"Insufficient privileges","code":"Authorization_RequestDenied"}}""";
        Agent365ToolingService.ExtractErrorMessage(json).Should().Be("Insufficient privileges");
    }

    [Fact]
    public void ExtractErrorMessage_HandlesErrorObjectWithoutMessage()
    {
        var json = """{"error":{"code":"InvalidRequest"}}""";
        var result = Agent365ToolingService.ExtractErrorMessage(json);
        result.Should().NotBeNull();
        result.Should().Contain("InvalidRequest");
    }

    [Fact]
    public void ExtractErrorMessage_ExtractsMessage_WhenPresent()
    {
        var json = """{"message":"Server error occurred"}""";
        Agent365ToolingService.ExtractErrorMessage(json).Should().Be("Server error occurred");
    }

    [Fact]
    public void ExtractErrorMessage_PrefersDetails_OverErrorAndMessage()
    {
        var json = """{"details":"Detail text","error":"Error text","message":"Message text"}""";
        Agent365ToolingService.ExtractErrorMessage(json).Should().Be("Detail text");
    }

    [Fact]
    public void ExtractErrorMessage_PrefersError_OverMessage()
    {
        var json = """{"error":"Error text","message":"Message text"}""";
        Agent365ToolingService.ExtractErrorMessage(json).Should().Be("Error text");
    }

    [Fact]
    public void ExtractErrorMessage_ReturnsRawContent_WhenNotValidJson()
    {
        var content = "This is not JSON";
        Agent365ToolingService.ExtractErrorMessage(content).Should().Be(content);
    }

    [Fact]
    public void ExtractErrorMessage_ReturnsNull_WhenJsonHasNoKnownFields()
    {
        var json = """{"status":"Failed","code":500}""";
        Agent365ToolingService.ExtractErrorMessage(json).Should().BeNull();
    }

    // --- BuildPublishFailureResponse tests ---

    [Fact]
    public void BuildPublishFailureResponse_SurfacesEnvelopeMessage_WhenDoubleSerialized()
    {
        // The platform returns its already-JSON string via Ok(string), which serializes it a second
        // time. This is the exact shape a duplicate-instance rejection reaches the CLI as.
        const string expected = "MCP server 'msdyn_DataverseMCPServer' is already published in environment 'env' under alias 'DG_DV_S22'. Only one published instance is allowed per server. Unpublish the existing instance before republishing.";
        var inner = JsonSerializer.Serialize(new { Status = "Failed", Message = expected });
        var doubleSerialized = JsonSerializer.Serialize(inner);

        var result = Agent365ToolingService.BuildPublishFailureResponse(doubleSerialized, HttpStatusCode.OK, NullLogger.Instance);

        result.Should().NotBeNull();
        result.Status.Should().Be("Failed");
        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Be(expected);
    }

    [Fact]
    public void BuildPublishFailureResponse_SurfacesEnvelopeMessage_WhenSingleSerialized()
    {
        const string expected = "Custom MCP server 'x' in the environment 'env' is not setup correctly. Please recreate the server and try publishing again.";
        var body = JsonSerializer.Serialize(new { Status = "Failed", Message = expected });

        var result = Agent365ToolingService.BuildPublishFailureResponse(body, HttpStatusCode.OK, NullLogger.Instance);

        result.Status.Should().Be("Failed");
        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Be(expected);
    }

    [Fact]
    public void BuildPublishFailureResponse_SurfacesError_FromAspNetProblemBody()
    {
        var body = """{"error":"DisplayName is required in the request body"}""";

        var result = Agent365ToolingService.BuildPublishFailureResponse(body, HttpStatusCode.BadRequest, NullLogger.Instance);

        result.Status.Should().Be("Failed");
        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Be("DisplayName is required in the request body");
    }

    [Fact]
    public void BuildPublishFailureResponse_PrefersDetails_FromAspNetErrorAndDetailsBody()
    {
        var body = """{"error":"Failed to publish (v2) MCP server to Dataverse environment","details":"TEDS API call failed with status 403"}""";

        var result = Agent365ToolingService.BuildPublishFailureResponse(body, HttpStatusCode.InternalServerError, NullLogger.Instance);

        result.Status.Should().Be("Failed");
        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Be("TEDS API call failed with status 403");
    }

    [Fact]
    public void BuildPublishFailureResponse_FallsBackToStatusCode_WhenBodyIsEmpty()
    {
        var result = Agent365ToolingService.BuildPublishFailureResponse(string.Empty, HttpStatusCode.BadGateway, NullLogger.Instance);

        result.Status.Should().Be("Failed");
        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Be("Server returned BadGateway");
    }

    [Fact]
    public void BuildPublishFailureResponse_FallsBackToStatusCode_WhenBodyIsNull()
    {
        var result = Agent365ToolingService.BuildPublishFailureResponse(null, HttpStatusCode.InternalServerError, NullLogger.Instance);

        result.Status.Should().Be("Failed");
        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Be("Server returned InternalServerError");
    }

    [Fact]
    public void BuildPublishFailureResponse_SurfacesRawContent_WhenBodyIsNotJson()
    {
        var result = Agent365ToolingService.BuildPublishFailureResponse("Bad Gateway", HttpStatusCode.BadGateway, NullLogger.Instance);

        result.Status.Should().Be("Failed");
        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Be("Bad Gateway");
    }

    // --- RedactSecretsFromPayload tests ---

    [Fact]
    public void RedactSecretsFromPayload_RedactsClientApp1Secret()
    {
        var payload = """{"authMetadata":{"clientApp1Id":"id1","clientApp1Secret":"supersecret"}}""";
        var result = Agent365ToolingService.RedactSecretsFromPayload(payload);
        result.Should().NotContain("supersecret");
        result.Should().Contain("***REDACTED***");
        result.Should().Contain("id1");
    }

    [Fact]
    public void RedactSecretsFromPayload_RedactsClientApp2Secret()
    {
        var payload = """{"authMetadata":{"clientApp2Id":"id2","clientApp2Secret":"secret2"}}""";
        var result = Agent365ToolingService.RedactSecretsFromPayload(payload);
        result.Should().NotContain("secret2");
        result.Should().Contain("***REDACTED***");
        result.Should().Contain("id2");
    }

    [Fact]
    public void RedactSecretsFromPayload_RedactsClientSecret()
    {
        var payload = """{"clientSecret":"topsecret","clientId":"myid"}""";
        var result = Agent365ToolingService.RedactSecretsFromPayload(payload);
        result.Should().NotContain("topsecret");
        result.Should().Contain("***REDACTED***");
        result.Should().Contain("myid");
    }

    [Fact]
    public void RedactSecretsFromPayload_PreservesNonSecretFields()
    {
        var payload = """{"serverName":"ext_Test","serverUrl":"https://example.com","authMetadata":{"clientApp1Id":"id1","clientApp1Secret":"secret"}}""";
        var result = Agent365ToolingService.RedactSecretsFromPayload(payload);
        result.Should().Contain("ext_Test");
        result.Should().Contain("https://example.com");
        result.Should().Contain("id1");
        result.Should().NotContain("\"secret\"");
    }

    [Fact]
    public void RedactSecretsFromPayload_HandlesNonJsonPayload()
    {
        var result = Agent365ToolingService.RedactSecretsFromPayload("not json");
        result.Should().Be("[payload redacted]");
    }

    [Fact]
    public void RedactSecretsFromPayload_HandlesPayloadWithNoSecrets()
    {
        var payload = """{"serverName":"ext_Test","toolList":["tool1"]}""";
        var result = Agent365ToolingService.RedactSecretsFromPayload(payload);
        result.Should().Contain("ext_Test");
        result.Should().Contain("tool1");
        result.Should().NotContain("REDACTED");
    }

    [Fact]
    public void RedactSecretsFromPayload_IsCaseInsensitive()
    {
        var payload = """{"ClientApp1Secret":"secret1","CLIENTAPP2SECRET":"secret2"}""";
        var result = Agent365ToolingService.RedactSecretsFromPayload(payload);
        result.Should().NotContain("secret1");
        result.Should().NotContain("secret2");
    }
}
