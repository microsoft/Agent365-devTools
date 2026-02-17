// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.MockToolingServer.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.MockToolingServer;

/// <summary>
/// Unit tests for JSON-RPC 2.0 constants used in MockToolingServer.
/// Ensures error codes and HTTP status codes match the JSON-RPC 2.0 specification.
/// </summary>
public class JsonRpcConstantsTests
{
    [Fact]
    public void Version_ShouldBeJsonRpc20()
    {
        JsonRpcConstants.Version.Should().Be("2.0");
    }

    [Theory]
    [InlineData(JsonRpcConstants.ErrorCodes.InvalidRequest, -32600)]
    [InlineData(JsonRpcConstants.ErrorCodes.MethodNotFound, -32601)]
    [InlineData(JsonRpcConstants.ErrorCodes.InvalidParams, -32602)]
    [InlineData(JsonRpcConstants.ErrorCodes.InternalError, -32603)]
    public void ErrorCodes_ShouldMatchJsonRpcSpecification(int actual, int expected)
    {
        actual.Should().Be(expected);
    }

    [Fact]
    public void HttpStatusCodes_Accepted_ShouldBe202()
    {
        JsonRpcConstants.HttpStatusCodes.Accepted.Should().Be(202);
    }

    [Fact]
    public void ErrorCodes_MethodNotFound_ShouldBeUsedForUnknownMethods()
    {
        // This test documents the intended use of MethodNotFound (-32601)
        // It should be returned when the requested JSON-RPC method does not exist
        JsonRpcConstants.ErrorCodes.MethodNotFound.Should().Be(-32601);
    }

    [Fact]
    public void ErrorCodes_InvalidParams_ShouldBeUsedForBadParameters()
    {
        // This test documents the intended use of InvalidParams (-32602)
        // It should be returned when method parameters are invalid (e.g., unknown MCP server name)
        JsonRpcConstants.ErrorCodes.InvalidParams.Should().Be(-32602);
    }

    [Fact]
    public void ErrorCodes_InternalError_ShouldBeUsedForUnexpectedFailures()
    {
        // This test documents the intended use of InternalError (-32603)
        // It should be returned when an unexpected exception occurs during request processing
        JsonRpcConstants.ErrorCodes.InternalError.Should().Be(-32603);
    }
}
