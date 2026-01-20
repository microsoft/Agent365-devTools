// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services
{
    public class AdminConsentHelperTests
    {
        [Fact]
        public async Task PollAdminConsentAsync_ReturnsTrue_WhenGrantExists()
        {
            var executor = Substitute.For<CommandExecutor>(Substitute.For<Microsoft.Extensions.Logging.ILogger<CommandExecutor>>());
            var logger = Substitute.For<ILogger>();

            // Mock service principal lookup
            var spJson = JsonDocument.Parse("{\"value\":[{\"id\":\"sp-123\"}]}", new JsonDocumentOptions()).RootElement.GetRawText();
            executor.ExecuteAsync("az", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = spJson }));

            // On the grants call, return a grant
            var grantsJson = JsonDocument.Parse("{\"value\":[{\"id\":\"grant-1\"}]}", new JsonDocumentOptions()).RootElement.GetRawText();
            executor.ExecuteAsync("az", Arg.Is<string>(s => s.Contains("oauth2PermissionGrants")), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = grantsJson }));

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await AdminConsentHelper.PollAdminConsentAsync(executor, logger, "appId-1", "Test", 10, 1, cts.Token);

            result.Should().BeTrue();
        }

        [Fact]
        public async Task PollAdminConsentAsync_ReturnsFalse_WhenNoGrant()
        {
            var executor = Substitute.For<CommandExecutor>(Substitute.For<Microsoft.Extensions.Logging.ILogger<CommandExecutor>>());
            var logger = Substitute.For<ILogger>();

            // service principal not found
            executor.ExecuteAsync("az", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = "{\"value\":[]}" }));

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = await AdminConsentHelper.PollAdminConsentAsync(executor, logger, "appId-1", "Test", 3, 1, cts.Token);

            result.Should().BeFalse();
        }

        [Fact]
        public async Task CheckConsentExistsAsync_ReturnsTrue_WhenAllScopesGranted()
        {
            var graphApiService = Substitute.For<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()));
            var logger = Substitute.For<ILogger>();

            // Mock grant with multiple scopes
            var grantJson = """
            {
                "value": [
                    {
                        "id": "grant-123",
                        "scope": "User.Read Mail.Send Calendars.Read"
                    }
                ]
            }
            """;
            var grantDoc = JsonDocument.Parse(grantJson);
            graphApiService.GraphGetAsync("tenant-1", Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<JsonDocument?>(grantDoc));

            var requiredScopes = new[] { "User.Read", "Mail.Send" };

            var result = await AdminConsentHelper.CheckConsentExistsAsync(
                graphApiService, "tenant-1", "client-sp-123", "resource-sp-456", requiredScopes, logger, CancellationToken.None);

            result.Should().BeTrue();
        }

        [Fact]
        public async Task CheckConsentExistsAsync_ReturnsFalse_WhenScopeMissing()
        {
            var graphApiService = Substitute.For<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()));
            var logger = Substitute.For<ILogger>();

            // Mock grant with fewer scopes than required
            var grantJson = """
            {
                "value": [
                    {
                        "id": "grant-123",
                        "scope": "User.Read"
                    }
                ]
            }
            """;
            var grantDoc = JsonDocument.Parse(grantJson);
            graphApiService.GraphGetAsync("tenant-1", Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<JsonDocument?>(grantDoc));

            var requiredScopes = new[] { "User.Read", "Mail.Send" };

            var result = await AdminConsentHelper.CheckConsentExistsAsync(
                graphApiService, "tenant-1", "client-sp-123", "resource-sp-456", requiredScopes, logger, CancellationToken.None);

            result.Should().BeFalse();
        }

        [Fact]
        public async Task CheckConsentExistsAsync_IsCaseInsensitive()
        {
            var graphApiService = Substitute.For<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()));
            var logger = Substitute.For<ILogger>();

            // Mock grant with different casing
            var grantJson = """
            {
                "value": [
                    {
                        "id": "grant-123",
                        "scope": "user.read MAIL.SEND"
                    }
                ]
            }
            """;
            var grantDoc = JsonDocument.Parse(grantJson);
            graphApiService.GraphGetAsync("tenant-1", Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<JsonDocument?>(grantDoc));

            var requiredScopes = new[] { "User.Read", "Mail.Send" };

            var result = await AdminConsentHelper.CheckConsentExistsAsync(
                graphApiService, "tenant-1", "client-sp-123", "resource-sp-456", requiredScopes, logger, CancellationToken.None);

            result.Should().BeTrue();
        }

        [Fact]
        public async Task CheckConsentExistsAsync_ReturnsFalse_WhenNoGrantsExist()
        {
            var graphApiService = Substitute.For<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()));
            var logger = Substitute.For<ILogger>();

            // Mock empty grants response
            var grantJson = """
            {
                "value": []
            }
            """;
            var grantDoc = JsonDocument.Parse(grantJson);
            graphApiService.GraphGetAsync("tenant-1", Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<JsonDocument?>(grantDoc));

            var requiredScopes = new[] { "User.Read" };

            var result = await AdminConsentHelper.CheckConsentExistsAsync(
                graphApiService, "tenant-1", "client-sp-123", "resource-sp-456", requiredScopes, logger, CancellationToken.None);

            result.Should().BeFalse();
        }

        [Fact]
        public async Task CheckConsentExistsAsync_ReturnsFalse_WhenClientSpIdMissing()
        {
            var graphApiService = Substitute.For<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()));
            var logger = Substitute.For<ILogger>();

            var requiredScopes = new[] { "User.Read" };

            var result = await AdminConsentHelper.CheckConsentExistsAsync(
                graphApiService, "tenant-1", "", "resource-sp-456", requiredScopes, logger, CancellationToken.None);

            result.Should().BeFalse();
        }

        [Fact]
        public async Task CheckConsentExistsAsync_ReturnsFalse_WhenResourceSpIdMissing()
        {
            var graphApiService = Substitute.For<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()));
            var logger = Substitute.For<ILogger>();

            var requiredScopes = new[] { "User.Read" };

            var result = await AdminConsentHelper.CheckConsentExistsAsync(
                graphApiService, "tenant-1", "client-sp-123", string.Empty, requiredScopes, logger, CancellationToken.None);

            result.Should().BeFalse();
        }

        [Fact]
        public async Task CheckConsentExistsAsync_ReturnsFalse_WhenGrantMissingScopeProperty()
        {
            var graphApiService = Substitute.For<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()));
            var logger = Substitute.For<ILogger>();

            // Mock grant without scope property
            var grantJson = """
            {
                "value": [
                    {
                        "id": "grant-123"
                    }
                ]
            }
            """;
            var grantDoc = JsonDocument.Parse(grantJson);
            graphApiService.GraphGetAsync("tenant-1", Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<JsonDocument?>(grantDoc));

            var requiredScopes = new[] { "User.Read" };

            var result = await AdminConsentHelper.CheckConsentExistsAsync(
                graphApiService, "tenant-1", "client-sp-123", "resource-sp-456", requiredScopes, logger, CancellationToken.None);

            result.Should().BeFalse();
        }
    }
}

