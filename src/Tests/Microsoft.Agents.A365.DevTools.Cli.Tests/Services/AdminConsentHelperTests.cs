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

            // Use intervalSeconds=0 and a short CTS to avoid real waits — this is a mock-only test.
            var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var result = await AdminConsentHelper.PollAdminConsentAsync(executor, logger, "appId-1", "Test", 1, 0, cts.Token);

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

        [Fact]
        public async Task PollAdminConsentAsync_Graph_ReturnsAssumedComplete_WhenCanaryReturns403AndTimeoutIsZero()
        {
            // Locks in the tri-state contract: when the calling token cannot read
            // oauth2PermissionGrants (canary 403), the helper MUST report AssumedComplete
            // rather than Verified, even when the wait window elapses without a keypress.
            // Callers rely on this distinction to avoid mutating persisted consent state
            // on the basis of an observation that never happened.
            var graphApiService = Substitute.For<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()));
            var logger = Substitute.For<ILogger>();

            graphApiService.GraphGetWithResponseAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new GraphApiService.GraphResponse
                {
                    IsSuccess = false,
                    StatusCode = 403,
                    ReasonPhrase = "Forbidden",
                    Body = "{}",
                    Json = null
                }));

            var result = await AdminConsentHelper.PollAdminConsentAsync(
                graphApiService, logger, "tenant-1", "client-sp-123",
                "Test", timeoutSeconds: 0, intervalSeconds: 1, CancellationToken.None);

            result.Should().Be(ConsentPollResult.AssumedComplete,
                because: "canary 403 means we cannot observe the grant; the helper must not falsely report Verified, so the caller leaves the consent URL visible.");
        }

        [Fact]
        public async Task PollAdminConsentAsync_Graph_ReturnsVerified_WhenCanaryShowsExistingGrant()
        {
            // Locks in the contract: a successful canary that already shows a grant short-circuits
            // to Verified — the only outcome that is safe to persist as ConsentGranted=true.
            var graphApiService = Substitute.For<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()));
            var logger = Substitute.For<ILogger>();

            var grantsJson = JsonDocument.Parse("{\"value\":[{\"id\":\"grant-1\"}]}");
            graphApiService.GraphGetWithResponseAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new GraphApiService.GraphResponse
                {
                    IsSuccess = true,
                    StatusCode = 200,
                    ReasonPhrase = "OK",
                    Body = grantsJson.RootElement.GetRawText(),
                    Json = grantsJson
                }));

            var result = await AdminConsentHelper.PollAdminConsentAsync(
                graphApiService, logger, "tenant-1", "client-sp-123",
                "Test", timeoutSeconds: 30, intervalSeconds: 1, CancellationToken.None);

            result.Should().Be(ConsentPollResult.Verified,
                because: "the canary directly observed an oauth2PermissionGrant; this is the only outcome that should let the caller persist ConsentGranted=true.");
        }

        [Fact]
        public async Task PollAdminConsentAsync_Graph_ReturnsNotDetected_WhenClientSpIdEmpty()
        {
            // Locks in the contract: if the blueprint SP cannot be resolved upstream,
            // polling is impossible and NotDetected (a hard 'no') must be returned so
            // the caller surfaces the consent URL and skips state mutation.
            var graphApiService = Substitute.For<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()));
            var logger = Substitute.For<ILogger>();

            var result = await AdminConsentHelper.PollAdminConsentAsync(
                graphApiService, logger, "tenant-1", clientSpId: "",
                "Test", timeoutSeconds: 5, intervalSeconds: 1, CancellationToken.None);

            result.Should().Be(ConsentPollResult.NotDetected,
                because: "without a client SP id we cannot poll and must not falsely claim AssumedComplete, which would suppress the consent URL in the Action Required block.");
        }
    }
}

