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
            var result = await AdminConsentHelper.PollAdminConsentAsync(
                executor, logger, "appId-1", "Test", 10, 1, cts.Token,
                graphBaseUrl: "https://graph.example");

            result.Should().BeTrue();
            await executor.Received(2).ExecuteAsync(
                "az",
                Arg.Is<string>(args => args.Contains("https://graph.example/v1.0", StringComparison.Ordinal)),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task PollAdminConsentAsync_PropagatesCancellation_WhenTokenCanceled()
        {
            // Requirement: Ctrl+C during admin-consent polling must propagate the
            // OperationCanceledException up to AllSubcommand's OCE handler so setup aborts
            // cleanly. The previous implementation swallowed OCE and returned false, which
            // then fell through to the az rest fallback prompt — confusing operators who
            // had just pressed Ctrl+C. Mirrors the Graph overload contract.
            var executor = Substitute.For<CommandExecutor>(Substitute.For<Microsoft.Extensions.Logging.ILogger<CommandExecutor>>());
            var logger = Substitute.For<ILogger>();

            // No grant — so the loop will iterate and hit Task.Delay where the CT fires.
            executor.ExecuteAsync("az", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = "{\"value\":[]}" }));

            var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            Func<Task> act = () => AdminConsentHelper.PollAdminConsentAsync(executor, logger, "appId-1", "Test", 10, 1, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>(
                because: "OCE must propagate so Ctrl+C aborts setup via AllSubcommand's OCE handler instead of falling into the az rest delegated-consent fallback prompt with a stale 'permission(s)?' question");
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
            graphApiService.GraphGetAsync("tenant-1", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
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
            graphApiService.GraphGetAsync("tenant-1", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
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
            graphApiService.GraphGetAsync("tenant-1", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
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
            graphApiService.GraphGetAsync("tenant-1", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
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
            graphApiService.GraphGetAsync("tenant-1", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
                .Returns(Task.FromResult<JsonDocument?>(grantDoc));

            var requiredScopes = new[] { "User.Read" };

            var result = await AdminConsentHelper.CheckConsentExistsAsync(
                graphApiService, "tenant-1", "client-sp-123", "resource-sp-456", requiredScopes, logger, CancellationToken.None);

            result.Should().BeFalse();
        }

        [Fact]
        public async Task PollAdminConsentAsync_Graph_ReturnsAssumedComplete_WhenNoGrantsDetectedAndTimeoutIsZero()
        {
            // Locks in the tri-state contract: when the timeout elapses without observing a grant,
            // the helper MUST report AssumedComplete rather than Verified. Callers rely on this
            // distinction to avoid mutating persisted consent state on the basis of an observation
            // that never happened.
            var graphApiService = Substitute.For<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()));
            var logger = Substitute.For<ILogger>();

            var result = await AdminConsentHelper.PollAdminConsentAsync(
                graphApiService, logger, "tenant-1", "client-sp-123",
                "Test", timeoutSeconds: 0, intervalSeconds: 1, CancellationToken.None);

            result.Should().Be(ConsentPollResult.AssumedComplete,
                because: "no grants were detected before the timeout; the helper must not falsely report Verified, so the caller leaves the consent URL visible.");
        }

        [Fact]
        public async Task PollAdminConsentAsync_Graph_ReturnsVerified_WhenGrantFoundDuringPolling()
        {
            // Locks in the contract: when GraphGetAsync returns a non-empty grants array during
            // polling, the helper returns Verified — the only outcome safe to persist as ConsentGranted=true.
            var graphApiService = Substitute.For<GraphApiService>(Substitute.For<ILogger<GraphApiService>>(), Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()));
            var logger = Substitute.For<ILogger>();

            var grantsJson = """
            {
                "value": [
                    {
                        "id": "grant-123",
                        "scope": "User.Read Mail.Send",
                        "consentType": "AllPrincipals"
                    }
                ]
            }
            """;
            graphApiService.GraphGetAsync(
                Arg.Any<string>(), Arg.Is<string>(s => s.Contains("oauth2PermissionGrants")),
                Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
                .Returns(_ => Task.FromResult<JsonDocument?>(JsonDocument.Parse(grantsJson)));

            var result = await AdminConsentHelper.PollAdminConsentAsync(
                graphApiService, logger, "tenant-1", "client-sp-123",
                "Test", timeoutSeconds: 30, intervalSeconds: 1, CancellationToken.None);

            result.Should().Be(ConsentPollResult.Verified,
                because: "an oauth2PermissionGrant was observed during polling; this is the only outcome that should let the caller persist ConsentGranted=true.");
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

        // ──────────────────────────────────────────────────────────────────────────────────────
        // CheckConsentExistsAsync — CommandExecutor (az-cli) overload tests
        //
        // The az-cli overload is the path BatchPermissionsOrchestrator uses for the pre-check
        // that decides whether to open the admin-consent browser on re-runs. The CLI's MSAL
        // Graph token cannot read oauth2PermissionGrants (PR #409 removed the scope), so the
        // GraphApiService overload always returns false in production — making this overload
        // the only path that can short-circuit a no-op browser open. Coverage here is therefore
        // load-bearing for the "no unnecessary re-consent" UX.
        // ──────────────────────────────────────────────────────────────────────────────────────

        private const string ValidBlueprintAppId = "11111111-1111-1111-1111-111111111111";
        private const string ValidResourceAppId = "22222222-2222-2222-2222-222222222222";

        [Fact]
        public async Task CheckConsentExistsAsync_AzCli_ReturnsTrue_WhenAllScopesGrantedWithAllPrincipalsConsent()
        {
            // Locks in the happy-path contract: when az rest returns a grant covering every
            // required scope AND the orchestrator passes consentType='AllPrincipals' (per CR-003),
            // the pre-check returns true and the browser is NOT opened.
            var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
            var logger = Substitute.For<ILogger>();

            string? capturedGrantsFilter = null;
            executor.ExecuteAsync("az", Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var args = ci.ArgAt<string>(1);
                    if (args.Contains("servicePrincipals?$filter=appId eq"))
                    {
                        // SP lookup — return a stable object id per appId so the helper threads through.
                        var spId = args.Contains(ValidBlueprintAppId) ? "bp-sp-id" : "res-sp-id";
                        return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = $"{{\"value\":[{{\"id\":\"{spId}\"}}]}}" });
                    }
                    if (args.Contains("oauth2PermissionGrants"))
                    {
                        capturedGrantsFilter = args;
                        return Task.FromResult(new CommandResult
                        {
                            ExitCode = 0,
                            StandardOutput = "{\"value\":[{\"scope\":\"User.Read Mail.Send\",\"consentType\":\"AllPrincipals\"}]}"
                        });
                    }
                    return Task.FromResult(new CommandResult { ExitCode = 1 });
                });

            var result = await AdminConsentHelper.CheckConsentExistsAsync(
                executor, logger, ValidBlueprintAppId, ValidResourceAppId,
                requiredScopes: new[] { "User.Read", "Mail.Send" },
                ct: default,
                consentType: "AllPrincipals");

            result.Should().BeTrue(
                because: "every required scope is in the existing AllPrincipals grant — opening the browser would be a no-op");
            capturedGrantsFilter.Should().NotBeNull();
            capturedGrantsFilter!.Should().Contain("AllPrincipals",
                because: "the orchestrator's pre-check must filter by consentType so a leftover Principal-scoped grant doesn't falsely satisfy the tenant-wide consent check (CR-003)");
        }

        [Fact]
        public async Task CheckConsentExistsAsync_AzCli_ReturnsFalse_WhenAzRestFails()
        {
            // When the az rest invocation itself fails (network, az login expired, throttling),
            // the helper must return false so the caller opens the browser rather than silently
            // skipping consent.
            var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
            var logger = Substitute.For<ILogger>();

            executor.ExecuteAsync("az", Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "az login expired" }));

            var result = await AdminConsentHelper.CheckConsentExistsAsync(
                executor, logger, ValidBlueprintAppId, ValidResourceAppId,
                requiredScopes: new[] { "User.Read" },
                ct: default);

            result.Should().BeFalse(
                because: "az rest failure must not be interpreted as 'consent exists' — the safe default is to open the browser");
        }

        [Fact]
        public async Task CheckConsentExistsAsync_AzCli_ReturnsFalse_AndDoesNotCallExecutor_ForInvalidAppIdGuid()
        {
            // CR-004 contract: appIds come from config and must be validated as GUIDs before
            // being interpolated into the az rest URL filter. An invalid input must short-circuit
            // without spawning any az subprocess.
            var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
            var logger = Substitute.For<ILogger>();

            var result = await AdminConsentHelper.CheckConsentExistsAsync(
                executor, logger,
                blueprintAppId: "not-a-guid",
                resourceAppId: ValidResourceAppId,
                requiredScopes: new[] { "User.Read" },
                ct: default);

            result.Should().BeFalse(because: "a non-GUID blueprintAppId must be rejected before any az rest call is made");
            await executor.DidNotReceive().ExecuteAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task CheckConsentExistsAsync_AzCli_ReturnsFalse_WhenExistingGrantMissingARequiredScope()
        {
            // The existing grant covers only some of the requested scopes. The pre-check must
            // return false so the caller opens the browser to complete the missing scopes —
            // otherwise the user would be left without permissions they expected.
            var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
            var logger = Substitute.For<ILogger>();

            executor.ExecuteAsync("az", Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var args = ci.ArgAt<string>(1);
                    if (args.Contains("servicePrincipals?$filter=appId eq"))
                        return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{\"value\":[{\"id\":\"sp-id\"}]}" });
                    return Task.FromResult(new CommandResult
                    {
                        ExitCode = 0,
                        StandardOutput = "{\"value\":[{\"scope\":\"User.Read\",\"consentType\":\"AllPrincipals\"}]}"
                    });
                });

            var result = await AdminConsentHelper.CheckConsentExistsAsync(
                executor, logger, ValidBlueprintAppId, ValidResourceAppId,
                requiredScopes: new[] { "User.Read", "Mail.Send" },
                ct: default,
                consentType: "AllPrincipals");

            result.Should().BeFalse(
                because: "the existing grant covers User.Read but not Mail.Send; the browser must open to capture the missing scope");
        }

        [Fact]
        public async Task CheckConsentExistsAsync_AzCli_AggregatesScopesAcrossMultipleGrantRows()
        {
            // Entra can split a single (client, resource) consent across multiple
            // oauth2PermissionGrant rows when consent was given incrementally. The helper must
            // union the scope strings across rows; otherwise a re-run would unnecessarily open
            // the browser even though the union of grants already covers everything required.
            var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
            var logger = Substitute.For<ILogger>();

            executor.ExecuteAsync("az", Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var args = ci.ArgAt<string>(1);
                    if (args.Contains("servicePrincipals?$filter=appId eq"))
                        return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{\"value\":[{\"id\":\"sp-id\"}]}" });
                    return Task.FromResult(new CommandResult
                    {
                        ExitCode = 0,
                        StandardOutput = "{\"value\":[{\"scope\":\"User.Read\"},{\"scope\":\"Mail.Send\"}]}"
                    });
                });

            var result = await AdminConsentHelper.CheckConsentExistsAsync(
                executor, logger, ValidBlueprintAppId, ValidResourceAppId,
                requiredScopes: new[] { "User.Read", "Mail.Send" },
                ct: default,
                consentType: "AllPrincipals");

            result.Should().BeTrue(
                because: "the union of all matching grant rows covers every required scope — splitting consent across rows is a normal Entra behavior, not a missing-consent signal");
        }
    }
}
