// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Tests for GraphApiService.AddAppPasswordAsync covering the optional secret-lifetime parameter
/// and the actionable error surfaced when a tenant's appManagementPolicies rejects the requested
/// lifetime.
/// </summary>
#pragma warning disable CA2000 // Test handlers dispose queued responses themselves.
public class GraphApiServiceAddAppPasswordTests
{
    private const string TenantId = "tenant-abc";
    private const string ObjectId = "app-object-xyz";

    private static IAuthenticationService FakeAuth()
    {
        var mock = Substitute.For<IAuthenticationService>();
        mock.GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Task.FromResult("fake-token"));
        return mock;
    }

    private static CommandExecutor FakeExecutor()
    {
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);
                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });
        return executor;
    }

    private sealed class BodyCapturingHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        public HttpResponseMessage? Response { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return Response ?? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Response?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task AddAppPasswordAsync_NoLifetime_PayloadOmitsEndDateTime()
    {
        // Arrange
        using var handler = new BodyCapturingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { secretText = "the-secret" })),
            },
        };
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var service = new GraphApiService(logger, FakeExecutor(), FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));

        // Act
        var secret = await service.AddAppPasswordAsync(TenantId, ObjectId);

        // Assert
        secret.Should().Be("the-secret");
        handler.CapturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var pwd = doc.RootElement.GetProperty("passwordCredential");
        pwd.GetProperty("displayName").GetString().Should().Be("CLI-generated secret");
        pwd.TryGetProperty("endDateTime", out _).Should().BeFalse("no lifetime supplied -> Graph default applies");
    }

    [Fact]
    public async Task AddAppPasswordAsync_WithLifetime_PayloadHasEndDateTimeRoughlyOffsetByMonths()
    {
        // Arrange
        const int lifetimeMonths = 3;
        using var handler = new BodyCapturingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { secretText = "the-secret" })),
            },
        };
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var service = new GraphApiService(logger, FakeExecutor(), FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));

        var before = DateTimeOffset.UtcNow;

        // Act
        var secret = await service.AddAppPasswordAsync(TenantId, ObjectId, lifetimeMonths: lifetimeMonths);

        var after = DateTimeOffset.UtcNow;

        // Assert
        secret.Should().Be("the-secret");
        handler.CapturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var pwd = doc.RootElement.GetProperty("passwordCredential");
        pwd.GetProperty("displayName").GetString().Should().Be("CLI-generated secret");
        var endDateTimeString = pwd.GetProperty("endDateTime").GetString();
        endDateTimeString.Should().NotBeNullOrWhiteSpace();
        var endDateTime = DateTimeOffset.Parse(endDateTimeString!, System.Globalization.CultureInfo.InvariantCulture);
        endDateTime.Should().BeOnOrAfter(before.AddMonths(lifetimeMonths));
        endDateTime.Should().BeOnOrBefore(after.AddMonths(lifetimeMonths));
    }

    [Fact]
    public async Task AddAppPasswordAsync_TenantPolicyRejection_LogsActionableErrorPointingAtFlag()
    {
        // Arrange — Graph returns 400 with a tenant-policy lifetime rejection
        var errorBody = new
        {
            error = new
            {
                code = "Request_BadRequest",
                message = "Lifetime of password is too long. The application credential lifetime exceeds the max value allowed as configured by tenant administrator.",
            },
        };
        using var handler = new BodyCapturingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(errorBody), Encoding.UTF8, "application/json"),
            },
        };
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var service = new GraphApiService(logger, FakeExecutor(), FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));

        // Act
        var secret = await service.AddAppPasswordAsync(TenantId, ObjectId);

        // Assert
        secret.Should().BeNull();
        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null && state.ToString()!.Contains("--secret-lifetime-months") && state.ToString()!.Contains("Graph default")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task AddAppPasswordAsync_TenantPolicyRejectionWithLifetime_LogsRequestedMonths()
    {
        // Arrange — caller passed an explicit lifetime that still exceeds policy
        var errorBody = new
        {
            error = new
            {
                code = "Request_BadRequest",
                message = "The requested password lifetime exceeds the maximum value allowed by tenant policy.",
            },
        };
        using var handler = new BodyCapturingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(errorBody), Encoding.UTF8, "application/json"),
            },
        };
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var service = new GraphApiService(logger, FakeExecutor(), FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));

        // Act
        var secret = await service.AddAppPasswordAsync(TenantId, ObjectId, lifetimeMonths: 12);

        // Assert
        secret.Should().BeNull();
        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null && state.ToString()!.Contains("12-month") && state.ToString()!.Contains("--secret-lifetime-months")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task AddAppPasswordAsync_NonPolicyFailure_LogsGenericErrorWithoutFlagPointer()
    {
        // Arrange — generic 403 unrelated to lifetime
        var errorBody = new
        {
            error = new
            {
                code = "Authorization_RequestDenied",
                message = "Insufficient privileges to complete the operation.",
            },
        };
        using var handler = new BodyCapturingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(JsonSerializer.Serialize(errorBody), Encoding.UTF8, "application/json"),
            },
        };
        var logger = Substitute.For<ILogger<GraphApiService>>();
        var service = new GraphApiService(logger, FakeExecutor(), FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));

        // Act
        var secret = await service.AddAppPasswordAsync(TenantId, ObjectId);

        // Assert
        secret.Should().BeNull();
        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null
                && state.ToString()!.Contains("Failed to add password")
                && !state.ToString()!.Contains("--secret-lifetime-months")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Theory]
    [InlineData(400, "Lifetime of password is too long.", true)]
    [InlineData(400, "The application credential lifetime exceeds the max value allowed by tenant administrator.", true)]
    [InlineData(403, "appManagementPolicy violation.", true)]
    [InlineData(403, "Request blocked by appManagementPolicies on the tenant.", true)]
    [InlineData(400, "endDateTime on passwordCredential exceeds the maximum allowed.", true)]
    [InlineData(400, "Password did not meet the complexity policy.", false)]
    [InlineData(400, "passwordCredential policy violation: not allowed.", false)]
    [InlineData(400, "Insufficient privileges to complete the operation.", false)]
    [InlineData(403, "Insufficient privileges to complete the operation.", false)]
    [InlineData(401, "Lifetime of password is too long.", false)]
    [InlineData(500, "Lifetime of password is too long.", false)]
    [InlineData(400, null, false)]
    public void IsTenantSecretLifetimePolicyRejection_ClassifiesGraphErrors(int statusCode, string? message, bool expected)
    {
        GraphApiService.IsTenantSecretLifetimePolicyRejection(statusCode, message).Should().Be(expected);
    }
}
