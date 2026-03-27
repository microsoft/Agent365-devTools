// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Helpers;

public class NetworkHelperTests
{
    [Fact]
    public void IsConnectionResetByProxy_WhenDirectSocketException_ReturnsTrue()
    {
        var ex = new SocketException((int)SocketError.ConnectionReset);

        NetworkHelper.IsConnectionResetByProxy(ex).Should().BeTrue(
            because: "a direct SocketException with ConnectionReset is a proxy-reset signal");
    }

    [Fact]
    public void IsConnectionResetByProxy_WhenHttpRequestExceptionWrapsConnectionReset_ReturnsTrue()
    {
        var inner = new SocketException((int)SocketError.ConnectionReset);
        var ex = new HttpRequestException("Connection reset", inner);

        NetworkHelper.IsConnectionResetByProxy(ex).Should().BeTrue(
            because: "HttpRequestException wrapping a ConnectionReset SocketException is the typical TLS proxy reset pattern");
    }

    [Fact]
    public void IsConnectionResetByProxy_WhenSocketExceptionIsNotConnectionReset_ReturnsFalse()
    {
        var ex = new SocketException((int)SocketError.TimedOut);

        NetworkHelper.IsConnectionResetByProxy(ex).Should().BeFalse(
            because: "only SocketError.ConnectionReset (10054) indicates a proxy reset — other socket errors should not be misclassified");
    }

    [Fact]
    public void IsConnectionResetByProxy_WhenNoSocketExceptionInChain_ReturnsFalse()
    {
        var ex = new InvalidOperationException("some other error");

        NetworkHelper.IsConnectionResetByProxy(ex).Should().BeFalse(
            because: "exceptions unrelated to socket errors are not proxy resets");
    }

    [Fact]
    public void IsConnectionResetByProxy_WhenDeeplyNestedConnectionReset_ReturnsTrue()
    {
        var socketEx = new SocketException((int)SocketError.ConnectionReset);
        var mid = new IOException("IO error", socketEx);
        var outer = new HttpRequestException("outer", mid);

        NetworkHelper.IsConnectionResetByProxy(outer).Should().BeTrue(
            because: "the chain walk must find SocketException at any nesting depth");
    }
}
