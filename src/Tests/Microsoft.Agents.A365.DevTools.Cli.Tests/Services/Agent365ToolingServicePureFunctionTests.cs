// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;

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
