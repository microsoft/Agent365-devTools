// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Tests to validate that tokens with newline characters are properly trimmed before being used in HTTP headers.
/// This is a regression test for the issue: "Failed to configure MCP server permissions: New line char is now allowed in header"
/// </summary>
public class GraphApiServiceTokenTrimTests
{
    [Theory]
    [InlineData("fake-token\n")]
    [InlineData("fake-token\r\n")]
    [InlineData("fake-token\r")]
    [InlineData("\nfake-token")]
    [InlineData("fake-token\n\n")]
    [InlineData("  fake-token  ")]
    [InlineData("\tfake-token\t")]
    public async Task EnsureGraphHeadersAsync_TrimsNewlineCharactersFromToken(string tokenWithWhitespace)
    {
        // Arrange
        var handler = new TestHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);
                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = tokenWithWhitespace, StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var service = new GraphApiService(logger, executor, handler);

        // Queue successful GET response
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"value\":[]}")
        };
        handler.QueueResponse(response);

        // Act - This should not throw FormatException about newline characters
        var result = await service.GraphGetAsync("tid", "/v1.0/some/path");

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task EnsureGraphHeadersAsync_WithTokenProvider_TrimsNewlineCharactersFromToken()
    {
        // Arrange
        var handler = new TestHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        var tokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();

        // Simulate token with newline from token provider
        tokenProvider.GetMgGraphAccessTokenAsync(
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<bool>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns("fake-token\n");

        var service = new GraphApiService(logger, executor, handler, tokenProvider);

        // Queue successful GET response
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"value\":[]}")
        };
        handler.QueueResponse(response);

        // Act - This should not throw FormatException about newline characters
        var result = await service.GraphGetAsync("tid", "/v1.0/some/path", scopes: new[] { "scope1" });

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task CheckServicePrincipalCreationPrivilegesAsync_TrimsNewlineCharactersFromToken()
    {
        // Arrange
        var handler = new TestHttpMessageHandler();
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);
                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token\r\n", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var service = new GraphApiService(logger, executor, handler);

        // Queue successful response for directory roles
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"value\":[]}")
        };
        handler.QueueResponse(response);

        // Act - This should not throw FormatException about newline characters
        var (hasPrivileges, roles) = await service.CheckServicePrincipalCreationPrivilegesAsync("tid");

        // Assert
        hasPrivileges.Should().BeFalse(); // No roles means no privileges
        roles.Should().BeEmpty();
    }
}
