// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands.SetupSubcommands;

/// <summary>
/// Regression tests for the generated-config invalidation block in
/// <see cref="BlueprintSubcommand.CreateAgentBlueprintAsync"/> (around lines 993-1021).
///
/// <para>
/// When no existing blueprint is found, the code path:
/// <list type="number">
///   <item><description>Calls <c>configService.InvalidateGeneratedConfigAsync(..., reason: "newblueprint", ...)</c>
///     which timestamp-backs-up and resets the on-disk <c>a365.generated.config.json</c>.</description></item>
///   <item><description>Clears the in-memory <see cref="JsonObject"/> by iterating its keys and removing each one.</description></item>
/// </list>
/// Both steps are required: the on-disk reset alone leaves stale identifiers in the
/// caller's in-memory mirror, and subsequent writes
/// (<c>generatedConfig["agentBlueprintId"] = ...</c>) re-introduce the stale agent
/// identity, registration, SP IDs, client secret, and resource consents from the
/// previous blueprint into the new blueprint's generated config — silently corrupting it.
/// </para>
/// </summary>
public class BlueprintSubcommandInvalidationTests
{
    private const string TenantId = "00000000-0000-0000-0000-000000000001";
    private const string DisplayName = "Test Blueprint For Invalidation";

    private readonly ILogger _logger;
    private readonly IConfigService _configService;
    private readonly CommandExecutor _executor;
    private readonly GraphApiService _graphApiService;
    private readonly AgentBlueprintService _blueprintService;
    private readonly BlueprintLookupService _blueprintLookupService;
    private readonly FederatedCredentialService _federatedCredentialService;

    public BlueprintSubcommandInvalidationTests()
    {
        _logger = Substitute.For<ILogger>();
        _configService = Substitute.For<IConfigService>();
        var executorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _executor = Substitute.For<CommandExecutor>(executorLogger);

        // Full mock — both virtual methods we drive (GraphGetAsync, IsApplicationOwnerAsync, etc.)
        // are stubbed by callers, so ForPartsOf is not needed and would risk falling through to a
        // real Graph call.
        _graphApiService = Substitute.ForPartsOf<GraphApiService>(
            Substitute.For<ILogger<GraphApiService>>(),
            _executor,
            (Func<Task<string?>>)(() => Task.FromResult<string?>(null)));

        _blueprintService = Substitute.ForPartsOf<AgentBlueprintService>(
            Substitute.For<ILogger<AgentBlueprintService>>(),
            _graphApiService);

        // BlueprintLookupService is concrete and its lookup method is non-virtual, so we drive its
        // behavior through GraphApiService.GraphGetAsync (which IS virtual). Returning null from
        // GraphGetAsync causes GetApplicationByDisplayNameAsync to report Found=false, which is the
        // condition that triggers the invalidation block under test.
        _blueprintLookupService = new BlueprintLookupService(
            NullLogger<BlueprintLookupService>.Instance,
            _graphApiService);

        _federatedCredentialService = Substitute.ForPartsOf<FederatedCredentialService>(
            Substitute.For<ILogger<FederatedCredentialService>>(),
            _graphApiService);
    }

    [Fact]
    public async Task CreateAgentBlueprintAsync_WhenNoExistingBlueprintFound_InvalidatesGeneratedConfigAndClearsInMemoryJsonObject()
    {
        // Arrange — create an isolated temp directory so the invalidation block's path computation
        // (configFile.DirectoryName + "a365.generated.config.json") is reproducible and does not
        // touch the test runner's working directory.
        var tempDir = Path.Combine(Path.GetTempPath(), $"a365-blueprint-inv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configFile = new FileInfo(Path.Combine(tempDir, "a365.config.json"));
        var expectedGeneratedPath = Path.Combine(tempDir, "a365.generated.config.json");

        try
        {
            // Force the displayName-first lookup to report "not found" so we reach the new-blueprint
            // creation path. The service's `if (doc == null)` branch maps to Found=false.
            _graphApiService.GraphGetAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IEnumerable<string>?>())
                .Returns(Task.FromResult<JsonDocument?>(null));

            // Pre-populate the in-memory JsonObject with the kinds of stale identifiers the
            // invalidation block exists to wipe. If the clear loop is removed, these survive and
            // get re-persisted into the new blueprint's generated config.
            var generatedConfig = new JsonObject
            {
                ["agentBlueprintId"] = "stale-blueprint-app-id",
                ["agentBlueprintObjectId"] = "stale-blueprint-object-id",
                ["agentBlueprintServicePrincipalObjectId"] = "stale-sp-id",
                ["agentBlueprintClientSecret"] = "stale-encrypted-secret",
                ["botId"] = "stale-bot-id",
                ["resourceConsents"] = new JsonArray
                {
                    new JsonObject { ["resourceName"] = "Microsoft Graph", ["consentGranted"] = true }
                }
            };
            var initialKeyCount = generatedConfig.Count;
            initialKeyCount.Should().BeGreaterThan(0,
                because: "the test pre-condition requires stale keys to be present so the clear loop has work to do");

            // Capture the JsonObject state AT THE TIME InvalidateGeneratedConfigAsync is called.
            // This is the regression guard against someone reversing the order (clearing the
            // in-memory JsonObject BEFORE invoking InvalidateGeneratedConfigAsync). The contract is:
            // 1) InvalidateGeneratedConfigAsync writes the empty file first, then
            // 2) the caller clears the in-memory JsonObject to match.
            // If reversed, by the time the mock is called the JsonObject would already be empty.
            int keyCountAtInvalidationCall = -1;
            _configService.InvalidateGeneratedConfigAsync(
                Arg.Any<Agent365Config>(),
                Arg.Any<string>(),
                Arg.Any<string>())
                .Returns(callInfo =>
                {
                    keyCountAtInvalidationCall = generatedConfig.Count;
                    return Task.FromResult<string?>(Path.Combine(tempDir, "a365.generated.config.before-newblueprint-stub.json"));
                });

            var setupConfig = new Agent365Config
            {
                TenantId = TenantId,
                AgentBlueprintDisplayName = DisplayName,
            };

            // A pre-canceled token forces InteractiveGraphAuthService (invoked after the
            // invalidation block) to throw OperationCanceledException, which is caught by the outer
            // try/catch in CreateAgentBlueprintAsync and translated into a `success=false` return.
            // The invariants we assert (InvalidateGeneratedConfigAsync called + JsonObject cleared)
            // are set BEFORE the cancellation observation point, so this short-circuit is safe.
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act
            var result = await BlueprintSubcommand.CreateAgentBlueprintAsync(
                _logger,
                _executor,
                _graphApiService,
                _blueprintService,
                _blueprintLookupService,
                _federatedCredentialService,
                tenantId: TenantId,
                displayName: DisplayName,
                agentIdentityDisplayName: null,
                managedIdentityPrincipalId: null,
                useManagedIdentity: true,
                generatedConfig,
                setupConfig,
                _configService,
                configFile,
                cts.Token,
                options: null,
                loginHintResolver: () => Task.FromResult<string?>(null));

            // Assert — the outer try/catch swallows the auth/cancellation failure and returns
            // success=false. That is expected and not what this test is guarding.
            result.success.Should().BeFalse(
                because: "the test deliberately short-circuits the downstream Graph auth call with a canceled token; the invariants we care about are the ones established BEFORE that short-circuit");

            // Invariant 1 — invalidation was triggered with the correct reason.
            // This is the on-disk backup + reset; ConfigService.InvalidateGeneratedConfigAsync
            // is contract-tested separately in Agent365ConfigServiceTests, so we assert only the
            // call shape here.
            await _configService.Received(1).InvalidateGeneratedConfigAsync(
                Arg.Is<Agent365Config>(c => ReferenceEquals(c, setupConfig)),
                Arg.Is<string>(reason => reason == "newblueprint"),
                Arg.Is<string>(path => path == expectedGeneratedPath));

            // Invariant 2 — the in-memory JsonObject is empty after the block runs. If someone
            // removes the clear loop (lines 1017-1021) while leaving InvalidateGeneratedConfigAsync
            // intact, this assertion fails — exactly the regression we want to catch.
            generatedConfig.Should().BeEmpty(
                because: "if the in-memory JsonObject is not cleared, subsequent writes such as " +
                         "generatedConfig[\"agentBlueprintId\"] = ... merge into stale entries from " +
                         "the previous blueprint (agent identity, registration, SP IDs, client " +
                         "secret, resource consents), silently corrupting the new blueprint's " +
                         "generated config and breaking downstream Developer Portal + runtime " +
                         "token-exchange paths");

            // Invariant 3 — ordering: InvalidateGeneratedConfigAsync was invoked BEFORE the
            // in-memory clear. The mock's AndDoes/Returns callback captured generatedConfig.Count
            // at the moment of the call. If someone reverses the order (in-memory clear first),
            // the captured count would be 0. The contract requires the on-disk reset to land
            // first so the disk view and the post-clear in-memory view match atomically when
            // SaveStateAsync is called inside InvalidateGeneratedConfigAsync.
            keyCountAtInvalidationCall.Should().Be(initialKeyCount,
                because: "InvalidateGeneratedConfigAsync must be called BEFORE the in-memory " +
                         "JsonObject clear loop; reversing the order would mean SaveStateAsync " +
                         "(inside the mock's real implementation) writes an empty file based on " +
                         "an already-emptied config object, while the caller's JsonObject view " +
                         "would briefly differ from the on-disk view in a way that masks bugs " +
                         "in the clear loop itself");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
