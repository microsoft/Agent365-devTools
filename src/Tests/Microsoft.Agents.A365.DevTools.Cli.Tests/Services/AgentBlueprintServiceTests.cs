// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class AgentBlueprintServiceTests
{
    private readonly ILogger<AgentBlueprintService> _mockLogger;
    private readonly ILogger<GraphApiService> _mockGraphLogger;
    private readonly CommandExecutor _mockExecutor;
    private readonly IMicrosoftGraphTokenProvider _mockTokenProvider;

    public AgentBlueprintServiceTests()
    {
        _mockLogger = Substitute.For<ILogger<AgentBlueprintService>>();
        _mockGraphLogger = Substitute.For<ILogger<GraphApiService>>();
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        // Use Substitute.For<> (full mock) so unmatched ExecuteAsync calls return a safe default
        // instead of falling through to the real implementation and spawning actual az processes.
        _mockExecutor = Substitute.For<CommandExecutor>(mockExecutorLogger);
        _mockTokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
    }

    [Fact]
    public async Task SetInheritablePermissionsAsync_Creates_WhenMissing()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        // Mock az CLI token acquisition flows used by EnsureGraphHeadersAsync
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);

                // Simulate az account show
                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                }

                // Simulate az account get-access-token -> return token
                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                }

                // Default: success
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var graphService = new GraphApiService(_mockGraphLogger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));
        var service = new AgentBlueprintService(_mockLogger, graphService);

        // ResolveBlueprintObjectIdAsync: First GET to check if blueprintAppId is objectId (returns 404 NotFound)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));

        // ResolveBlueprintObjectIdAsync: Second GET to resolve appId -> objectId
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "resolved-object-id" } } }))
        });

        // SetInheritablePermissionsAsync: GET existing permissions (returns empty list = not found)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = Array.Empty<object>() }))
        });

        // Simulate POST success
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { id = "created" }))
        });

        // Act
        var (ok, already, err) = await service.SetInheritablePermissionsAsync("tid", "bpAppId", "resAppId", new[] { "scope1", "scope2" });

        // Assert
        ok.Should().BeTrue();
        already.Should().BeFalse();
        err.Should().BeNull();

        // POST body must use the allAllowed wildcard kind for BOTH scopes and roles. The old
        // enumeratedScopes shape (with an explicit scopes array) must not appear — agent
        // identities created from this blueprint need allAllowed on both to inherit roles
        // granted to the blueprint SP (e.g. Observability otel-write).
        //
        // Assertions intentionally pin only `kind=allAllowed` and resourceAppId. The polymorphic
        // `@odata.type` discriminator is a serialization-shape detail of the AllAllowedScopes /
        // AllAllowedRoles wire-format models; covered separately by RequestBody_IncludesODataTypeDiscriminator below.
        var post = handler.SentRequests.Single(r => r.Method == HttpMethod.Post && r.Url.Contains("/inheritablePermissions"));
        post.Body.Should().NotBeNull();
        using var doc = JsonDocument.Parse(post.Body!);
        var root = doc.RootElement;
        root.GetProperty("resourceAppId").GetString().Should().Be("resAppId");
        root.GetProperty("inheritableScopes").GetProperty("kind").GetString().Should().Be(
            "allAllowed",
            because: "issue from Sandeep — new entries must use allAllowed only on both scopes and roles");
        root.GetProperty("inheritableRoles").GetProperty("kind").GetString().Should().Be(
            "allAllowed",
            because: "app-role inheritance must be set in the same call so roles granted to the blueprint SP (e.g. Observability otel-write) propagate to agent identities");
        // Defensive: the deprecated enumerated form must NOT appear anywhere in the body.
        post.Body!.Should().NotContain("enumeratedScopes");
    }

    [Fact]
    public async Task SetInheritablePermissionsAsync_RequestBody_IncludesODataTypeDiscriminator()
    {
        // Graph requires the polymorphic @odata.type discriminator on each inheritableScopes /
        // inheritableRoles object so it can resolve the concrete type (allAllowedScopes vs
        // enumeratedScopes). This is a wire-format contract distinct from the kind value asserted
        // above; pinning it here protects against accidental removal during serialization refactors.
        var handler = new FakeHttpMessageHandler();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<string>(1);
                if (args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
            });

        var graphService = new GraphApiService(_mockGraphLogger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));
        var service = new AgentBlueprintService(_mockLogger, graphService);

        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "resolved-object-id" } } }))
        });
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = Array.Empty<object>() }))
        });
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Created));

        await service.SetInheritablePermissionsAsync("tid", "bpAppId", "resAppId", new[] { "scope1" });

        var post = handler.SentRequests.Single(r => r.Method == HttpMethod.Post && r.Url.Contains("/inheritablePermissions"));
        using var doc = JsonDocument.Parse(post.Body!);
        doc.RootElement.GetProperty("inheritableScopes").GetProperty("@odata.type").GetString().Should().Be(
            "#microsoft.graph.allAllowedScopes",
            because: "Graph needs the polymorphic discriminator to route the body to the allAllowedScopes type — without it the API returns 400");
        doc.RootElement.GetProperty("inheritableRoles").GetProperty("@odata.type").GetString().Should().Be(
            "#microsoft.graph.allAllowedRoles",
            because: "same discriminator contract on the roles side");
    }

    [Fact]
    public async Task SetInheritablePermissionsAsync_WhenLegacyEnumeratedEntryExists_PatchesToAllAllowedOnBothSides()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        // Mock az CLI token acquisition flows used by EnsureGraphHeadersAsync
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);

                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
                }

                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                }

                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var graphService = new GraphApiService(_mockGraphLogger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));
        var service = new AgentBlueprintService(_mockLogger, graphService);

        // Existing entry with one scope
        var existing = new
        {
            value = new[]
            {
                new
                {
                    resourceAppId = "resAppId",
                    inheritableScopes = new { scopes = new[] { "scope1" } }
                }
            }
        };

        // ResolveBlueprintObjectIdAsync: Check if bpAppId is an objectId (returns 404 NotFound)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));

        // ResolveBlueprintObjectIdAsync: Resolve appId to objectId
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "resolved-object-id" } } }))
        });

        // SetInheritablePermissionsAsync: GET existing permissions
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(existing))
        });

        // PATCH returns 204 NoContent
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));

        // Act
        var (ok, already, err) = await service.SetInheritablePermissionsAsync("tid", "bpAppId", "resAppId", new[] { "scope2" });

        // Assert — a stale enumerated entry must be reconciled to allAllowed via PATCH.
        ok.Should().BeTrue();
        already.Should().BeFalse();
        err.Should().BeNull();

        var patch = handler.SentRequests.Single(r => r.Method == HttpMethod.Patch && r.Url.Contains("/inheritablePermissions/resAppId"));
        patch.Body.Should().NotBeNull();
        using var doc = JsonDocument.Parse(patch.Body!);
        doc.RootElement.GetProperty("inheritableScopes").GetProperty("kind").GetString().Should().Be("allAllowed",
            because: "stale enumeratedScopes entries must be reconciled to allAllowed");
        doc.RootElement.GetProperty("inheritableRoles").GetProperty("kind").GetString().Should().Be("allAllowed",
            because: "the same PATCH must also set inheritableRoles to allAllowed so role inheritance starts working");
        patch.Body!.Should().NotContain("enumeratedScopes");
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_ReturnsScopesAndResolvedRoleNames()
    {
        const string resourceSpId = "33333333-3333-3333-3333-333333333333";
        const string appRoleId = "44444444-4444-4444-4444-444444444444";

        // Arrange — single resource with one delegated scope grant and one app role assignment on
        // the blueprint SP. The role assignment's appRoleId must be resolved to a human-readable
        // name via a lookup of the resource SP's appRoles array (the same shape Graph returns).
        var handler = new FakeHttpMessageHandler();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<string>(1);
                if (args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
            });

        var graphService = new GraphApiService(_mockGraphLogger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));
        var service = new AgentBlueprintService(_mockLogger, graphService);

        // 1) LookupServicePrincipalByAppIdAsync(blueprintAppId) -> blueprint SP id
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "bp-sp-id" } } }))
        });
        // 2) GetOauth2PermissionGrantsAsync — one delegated grant for the resource
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { resourceId = resourceSpId, scope = "otel-write extra-scope", consentType = "AllPrincipals" } } }))
        });
        // 3) appRoleAssignments on the blueprint SP — one assignment for the resource
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { resourceId = resourceSpId, appRoleId } } }))
        });
        // 4) LookupServicePrincipalByAppIdAsync(resourceAppId) -> resource SP id
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = resourceSpId } } }))
        });
        // 5) GET resource SP appRoles to resolve the role id to a human-readable name
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                appRoles = new[] { new { id = appRoleId, value = "Agent365.Observability.OtelWrite" } }
            }))
        });

        // Act
        const string blueprintAppId = "11111111-1111-1111-1111-111111111111";
        const string resourceAppId = "22222222-2222-2222-2222-222222222222";

        var grants = await service.GetBlueprintSpGrantsAsync(
            "tid", blueprintAppId, new[] { resourceAppId });

        // Assert
        grants.Should().ContainKey(resourceAppId);
        var (delegatedScopes, appRoleNames) = grants[resourceAppId];
        delegatedScopes.Should().BeEquivalentTo(new[] { "extra-scope", "otel-write" },
            because: "the space-delimited Graph scope string must be split into individual scopes and returned sorted");
        appRoleNames.Should().BeEquivalentTo(new[] { "Agent365.Observability.OtelWrite" },
            because: "app role IDs must be resolved to their human-readable values via the resource SP's appRoles array");
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WithUnrelatedNullValuedRole_ResolvesAssignedRole()
    {
        const string resourceSpId = "33333333-3333-3333-3333-333333333333";
        const string assignedRoleId = "44444444-4444-4444-4444-444444444444";
        const string unrelatedRoleId = "55555555-5555-5555-5555-555555555555";
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "bp-sp-id",
                StatusCode = 200
            });
        graph.GetOauth2PermissionGrantsAsync(
                "tenant-id",
                "bp-sp-id",
                Arg.Any<CancellationToken>())
            .Returns(new List<(string resourceId, string scope, string consentType)>());
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains("/servicePrincipals/bp-sp-id/appRoleAssignments", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc($$"""
                    {
                      "value": [
                        { "resourceId": "{{resourceSpId}}", "appRoleId": "{{assignedRoleId}}" }
                      ]
                    }
                    """)
            });
        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "resource-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = resourceSpId,
                StatusCode = 200
            });
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains($"/servicePrincipals/{resourceSpId}?$select=appRoles", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc($$"""
                    {
                      "appRoles": [
                        { "id": "{{assignedRoleId}}", "value": "Assigned.Role" },
                        { "id": "{{unrelatedRoleId}}", "value": null }
                      ]
                    }
                    """)
            });

        var result = await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "resource-app-id" });

        result["resource-app-id"].AppRoleNames.Should().Equal(new[] { "Assigned.Role" },
            because: "an unrelated app role with no value must not invalidate otherwise usable role metadata");
    }

    [Fact]
    public async Task SetInheritablePermissionsAsync_IsIdempotent_WhenAlreadyAllAllowed()
    {
        // Arrange — existing entry is already at kind=allAllowed for both scopes and roles.
        // The service must detect this and return alreadyExists without issuing a PATCH.
        var handler = new FakeHttpMessageHandler();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<string>(1);
                if (args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
            });

        var graphService = new GraphApiService(_mockGraphLogger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));
        var service = new AgentBlueprintService(_mockLogger, graphService);

        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "resolved-object-id" } } }))
        });

        var existing = new
        {
            value = new[]
            {
                new
                {
                    resourceAppId = "resAppId",
                    inheritableScopes = new { kind = "allAllowed" },
                    inheritableRoles = new { kind = "allAllowed" }
                }
            }
        };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(existing))
        });

        // Act
        var (ok, already, err) = await service.SetInheritablePermissionsAsync("tid", "bpAppId", "resAppId", new[] { "anything" });

        // Assert
        ok.Should().BeTrue();
        already.Should().BeTrue(because: "the entry is already at allAllowed for both scopes and roles");
        err.Should().BeNull();
        handler.SentRequests.Any(r => r.Method == HttpMethod.Patch).Should().BeFalse(
            because: "an idempotent re-run must not issue a redundant PATCH");
        handler.SentRequests.Any(r => r.Method == HttpMethod.Post && r.Url.Contains("/inheritablePermissions")).Should().BeFalse(
            because: "an idempotent re-run must not issue a redundant POST either");
    }

    [Fact]
    public async Task SetInheritablePermissionsAsync_Patches_WhenScopesAllAllowedButRolesMissing()
    {
        // Arrange — partial-migration state: the entry has inheritableScopes at allAllowed but
        // inheritableRoles is absent. The service must PATCH to fill in roles=allAllowed.
        var handler = new FakeHttpMessageHandler();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<string>(1);
                if (args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
            });

        var graphService = new GraphApiService(_mockGraphLogger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));
        var service = new AgentBlueprintService(_mockLogger, graphService);

        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "resolved-object-id" } } }))
        });

        var existing = new
        {
            value = new[]
            {
                new
                {
                    resourceAppId = "resAppId",
                    inheritableScopes = new { kind = "allAllowed" }
                    // no inheritableRoles
                }
            }
        };
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(existing))
        });
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));

        // Act
        var (ok, already, _) = await service.SetInheritablePermissionsAsync("tid", "bpAppId", "resAppId", new[] { "anything" });

        // Assert
        ok.Should().BeTrue();
        already.Should().BeFalse(because: "the entry was not yet fully allAllowed (roles missing) so a PATCH was issued");

        var patch = handler.SentRequests.Single(r => r.Method == HttpMethod.Patch && r.Url.Contains("/inheritablePermissions/resAppId"));
        patch.Body.Should().NotBeNull();
        using var doc = JsonDocument.Parse(patch.Body!);
        doc.RootElement.GetProperty("inheritableRoles").GetProperty("kind").GetString().Should().Be("allAllowed",
            because: "the PATCH must add roles=allAllowed to complete the migration");
    }

    [Fact]
    public async Task SetInheritablePermissionsAsync_ReturnsFalse_WhenPatchThrows()
    {
        // Arrange — use a subclass that overrides GraphPatchAsync to throw,
        // simulating a transient network error during the PATCH call (#366 regression).
        var handler = new FakeHttpMessageHandler();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<string>(1);
                if (args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
            });

        var graphService = new ThrowingOnPatchGraphApiService(_mockGraphLogger, executor, FakeAuth(), handler);
        var service = new AgentBlueprintService(_mockLogger, graphService);

        // ResolveBlueprintObjectIdAsync: 404 → resolve via appId filter
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "resolved-object-id" } } }))
        });

        // GET existing permissions — returns an entry so the merge+PATCH path is taken
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                value = new[] { new { resourceAppId = "resAppId", inheritableScopes = new { scopes = new[] { "scope1" } } } }
            }))
        });

        // Act
        var (ok, already, err) = await service.SetInheritablePermissionsAsync("tid", "bpAppId", "resAppId", new[] { "scope2" });

        // Assert — must not throw; must surface the failure gracefully. We assert the contract
        // (non-empty error message identifies the failing operation) rather than the literal log
        // string so cosmetic message changes don't break the test.
        ok.Should().BeFalse(because: "GraphPatchAsync threw an exception and the caller must not crash");
        already.Should().BeFalse();
        err.Should().NotBeNullOrWhiteSpace(because: "callers must receive a non-empty error to surface to operators");
    }

    [Fact]
    public async Task SetInheritablePermissionsAsync_ReturnsFalse_WhenPatchReturnsFalse()
    {
        // Arrange — GraphPatchAsync succeeds at the HTTP level but returns a non-2xx status,
        // causing GraphPatchAsync to return false. The method must return (ok: false) without throwing.
        var handler = new FakeHttpMessageHandler();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<string>(1);
                if (args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "fake-token", StandardError = string.Empty });
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{}", StandardError = string.Empty });
            });

        var graphService = new GraphApiService(_mockGraphLogger, executor, FakeAuth(), handler, loginHintResolver: () => Task.FromResult<string?>(null));
        var service = new AgentBlueprintService(_mockLogger, graphService);

        // ResolveBlueprintObjectIdAsync: 404 → resolve via appId filter
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "resolved-object-id" } } }))
        });

        // GET existing permissions — returns an entry so the PATCH path is taken
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                value = new[] { new { resourceAppId = "resAppId", inheritableScopes = new { scopes = new[] { "scope1" } } } }
            }))
        });

        // PATCH returns 400 Bad Request — GraphPatchAsync returns false
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"code\":\"BadRequest\",\"message\":\"Invalid payload\"}}")
        });

        // Act
        var (ok, already, err) = await service.SetInheritablePermissionsAsync("tid", "bpAppId", "resAppId", new[] { "scope2" });

        // Assert — must not throw; must surface the failure gracefully. The contract is "error
        // identifies the failing operation"; pinning the literal log string would couple this test
        // to the message wording so the assertion is intentionally loose.
        ok.Should().BeFalse(because: "GraphPatchAsync returned false (HTTP 400)");
        already.Should().BeFalse();
        err.Should().NotBeNullOrWhiteSpace(because: "callers must receive a non-empty error to surface to operators");
        err!.Should().Contain("PATCH", because: "the error must mention the failing operation so operators can correlate with logs");
    }

    [Fact]
    public async Task DeleteAgentIdentityAsync_WithValidIdentity_ReturnsTrue()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            const string tenantId = "12345678-1234-1234-1234-123456789012";
            const string identityId = "identity-sp-id-123";

            // Override with specific scope assertion
            _mockTokenProvider.GetMgGraphAccessTokenAsync(
                tenantId,
                Arg.Is<IEnumerable<string>>(scopes => scopes.Contains("AgentIdentity.DeleteRestore.All")),
                false,
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>())
                .Returns("fake-delegated-token");

            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));

            // Act
            var result = await service.DeleteAgentIdentityAsync(tenantId, identityId);

            // Assert
            result.Should().BeTrue();

            await _mockTokenProvider.Received(1).GetMgGraphAccessTokenAsync(
                tenantId,
                Arg.Is<IEnumerable<string>>(scopes => scopes.Contains("AgentIdentity.DeleteRestore.All")),
                false,
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>());
        }
    }

    [Fact]
    public async Task DeleteAgentIdentityAsync_WhenResourceNotFound_ReturnsTrueIdempotent()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            const string tenantId = "12345678-1234-1234-1234-123456789012";
            const string identityId = "non-existent-identity";

            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\": {\"code\": \"Request_ResourceNotFound\"}}")
            });

            // Act
            var result = await service.DeleteAgentIdentityAsync(tenantId, identityId);

            // Assert
            result.Should().BeTrue("404 should be treated as success for idempotent deletion");
        }
    }

    [Fact]
    public async Task DeleteAgentIdentityAsync_WhenTokenProviderIsNull_ReturnsFalse()
    {
        // Arrange
        using var handler = new FakeHttpMessageHandler();
        var graphService = new GraphApiService(_mockGraphLogger, _mockExecutor, Substitute.For<IAuthenticationService>(), handler, tokenProvider: null);
        var service = new AgentBlueprintService(_mockLogger, graphService);

        const string tenantId = "12345678-1234-1234-1234-123456789012";
        const string identityId = "identity-123";

        // Act
        var result = await service.DeleteAgentIdentityAsync(tenantId, identityId);

        // Assert
        result.Should().BeFalse();

        _mockGraphLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Token provider is not configured")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task DeleteAgentIdentityAsync_WhenDeletionFails_ReturnsFalse()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            const string tenantId = "12345678-1234-1234-1234-123456789012";
            const string identityId = "identity-123";

            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"error\": {\"code\": \"Authorization_RequestDenied\"}}")
            });

            // Act
            var result = await service.DeleteAgentIdentityAsync(tenantId, identityId);

            // Assert
            result.Should().BeFalse();

            _mockGraphLogger.Received().Log(
                LogLevel.Error,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Graph DELETE") && o.ToString()!.Contains("403")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
    }

    [Fact]
    public async Task DeleteAgentIdentityAsync_WhenExceptionThrown_ReturnsFalse()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            const string tenantId = "12345678-1234-1234-1234-123456789012";
            const string identityId = "identity-123";

            // Override token provider to throw
            _mockTokenProvider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<bool>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>())
                .Returns(Task.FromException<string?>(new HttpRequestException("Connection timeout")));

            // Act
            var result = await service.DeleteAgentIdentityAsync(tenantId, identityId);

            // Assert
            result.Should().BeFalse();

            _mockLogger.Received().Log(
                LogLevel.Error,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Exception deleting agent identity")),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
    }

    [Fact]
    public async Task GetAgentInstancesForBlueprintAsync_ReturnsFilteredInstances()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            const string blueprintId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

            // Response 1: GET /beta/servicePrincipals/microsoft.graph.agentIdentity?$filter=agentIdentityBlueprintId eq '...'
            // Server-side filtered response returns only matching SPs
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    value = new[]
                    {
                        new { id = "sp-obj-1", displayName = "Instance A", agentIdentityBlueprintId = blueprintId }
                    }
                }))
            });

            // Note: No Response 2 for agent users — AgentIdUser.ReadWrite.All is not in RequiredClientAppPermissions
            // (create-instance is not enabled), so the user query is intentionally skipped and AgentUserId is null.

            // Act
            var instances = await service.GetAgentInstancesForBlueprintAsync("tenant-id", blueprintId);

            // Assert
            instances.Should().HaveCount(1);
            instances[0].IdentitySpId.Should().Be("sp-obj-1");
            instances[0].DisplayName.Should().Be("Instance A");
            instances[0].AgentUserId.Should().BeNull("AgentIdUser.ReadWrite.All is not in RequiredClientAppPermissions when create-instance is disabled");
        }
    }

    [Fact]
    public async Task GetAgentInstancesForBlueprintAsync_ReturnsEmpty_WhenNoneFound()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            // Response 1: SPs query returns empty
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { value = Array.Empty<object>() }))
            });

            // Response 2: Users query returns empty (both run in parallel)
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { value = Array.Empty<object>() }))
            });

            // Act
            var instances = await service.GetAgentInstancesForBlueprintAsync("tenant-id", "b2c3d4e5-f6a7-8901-bcde-f12345678901");

            // Assert
            instances.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task GetAgentInstancesForBlueprintAsync_Throws_WhenGraphQueryFails()
    {
        // Arrange
        var (service, _) = CreateServiceWithFakeHandler();

        // Override token provider to throw so the Graph call fails
        _mockTokenProvider.GetMgGraphAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(Task.FromException<string?>(new HttpRequestException("Connection timeout")));

        // Act & Assert - exception must propagate so callers can abort rather than proceeding with 0 instances
        await service.Invoking(s => s.GetAgentInstancesForBlueprintAsync("tenant-id", "blueprint-id"))
            .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task DeleteAgentUserAsync_ReturnsTrue_OnSuccess()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            // Queue HTTP response for DELETE /beta/agentUsers/{userId}
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));

            // Act
            var result = await service.DeleteAgentUserAsync("tenant-id", "user-obj-1");

            // Assert
            result.Should().BeTrue();
        }
    }

    [Fact]
    public async Task DeleteAgentUserAsync_ReturnsFalse_OnGraphError()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            // Override token provider to throw
            _mockTokenProvider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
                .Returns(Task.FromException<string?>(new HttpRequestException("Connection timeout")));

            // Act
            var result = await service.DeleteAgentUserAsync("tenant-id", "user-obj-1");

            // Assert
            result.Should().BeFalse();
        }
    }

    // Test helper — overrides GraphPatchAsync to throw, simulating a transient network failure.
    private sealed class ThrowingOnPatchGraphApiService : GraphApiService
    {
        public ThrowingOnPatchGraphApiService(
            ILogger<GraphApiService> logger,
            CommandExecutor executor,
            IAuthenticationService authService,
            HttpMessageHandler handler)
            // loginHintResolver: no-op — prevents AzCliHelper.ResolveLoginHintAsync() from
            // spawning a real 'az account get-access-token' subprocess in tests.
            : base(logger, executor, authService, handler, loginHintResolver: () => Task.FromResult<string?>(null)) { }

        public override Task<bool> GraphPatchAsync(
            string tenantId,
            string relativePath,
            object payload,
            CancellationToken ct = default,
            IEnumerable<string>? scopes = null)
            => Task.FromException<bool>(new HttpRequestException("Network error during PATCH"));
    }

    private static IAuthenticationService FakeAuth()
    {
        var mock = Substitute.For<IAuthenticationService>();
        mock.GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Task.FromResult("fake-token"));
        return mock;
    }

    private (AgentBlueprintService service, FakeHttpMessageHandler handler) CreateServiceWithFakeHandler()
    {
        var handler = new FakeHttpMessageHandler();
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult
                { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty }));
        _mockTokenProvider.GetMgGraphAccessTokenAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("test-token");
        // Pass a no-op login hint resolver to skip the real 'az account show' process spawned by
        // AzCliHelper.ResolveLoginHintAsync — that static call bypasses the mocked CommandExecutor
        // and causes each test to wait several seconds for the real az CLI.
        var graphService = new GraphApiService(_mockGraphLogger, executor, FakeAuth(), handler, _mockTokenProvider,
            loginHintResolver: () => Task.FromResult<string?>(null));
        return (new AgentBlueprintService(_mockLogger, graphService), handler);
    }

    // ── GrantAppRoleAssignmentAsync tests ──────────────────────────────────────

    [Fact]
    public async Task GrantAppRoleAssignmentAsync_WhenResourceSpNotFound_ReturnsFalse()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            // SP lookup returns empty value array
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[]}")
            });

            // Act
            var result = await service.GrantAppRoleAssignmentAsync(
                "tenant-id", "blueprint-sp-id", "resource-app-id",
                new[] { "Agent365.Observability.OtelWrite" });

            // Assert
            result.AllSucceeded.Should().BeFalse();
            result.AllAlreadyAssigned.Should().BeFalse(
                because: "AllAlreadyAssigned is only meaningful when AllSucceeded is true");
        }
    }

    [Fact]
    public async Task GrantAppRoleAssignmentAsync_WhenRoleNotFoundOnResourceSp_ReturnsFalse()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            // SP lookup succeeds
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[{\"id\":\"resource-sp-id\"}]}")
            });
            // Resource SP has no matching app roles
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"appRoles\":[]}")
            });
            // Existing assignments
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[]}")
            });

            // Act
            var result = await service.GrantAppRoleAssignmentAsync(
                "tenant-id", "blueprint-sp-id", "resource-app-id",
                new[] { "Agent365.Observability.OtelWrite" });

            // Assert
            result.AllSucceeded.Should().BeFalse();
            result.AllAlreadyAssigned.Should().BeFalse();
        }
    }

    [Fact]
    public async Task GrantAppRoleAssignmentAsync_WhenRoleAlreadyAssigned_ReturnsTrueWithoutPost()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            // SP lookup
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[{\"id\":\"resource-sp-id\"}]}")
            });
            // Resource SP app roles
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"appRoles\":[{\"value\":\"Agent365.Observability.OtelWrite\",\"id\":\"role-id-1\"}]}")
            });
            // Existing assignments — role already assigned
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[{\"resourceId\":\"resource-sp-id\",\"appRoleId\":\"role-id-1\"}]}")
            });
            // No POST should be queued — if the handler is called a 4th time it returns 404

            // Act
            var result = await service.GrantAppRoleAssignmentAsync(
                "tenant-id", "blueprint-sp-id", "resource-app-id",
                new[] { "Agent365.Observability.OtelWrite" });

            // Assert
            result.AllSucceeded.Should().BeTrue();
            result.AllAlreadyAssigned.Should().BeTrue(
                because: "the role was already in existingRoleIds so no POST was issued — the orchestrator uses this to surface 'already granted' in the setup summary");
        }
    }

    [Fact]
    public async Task GrantAppRoleAssignmentAsync_WhenPostSucceeds_ReturnsTrue()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            // SP lookup
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[{\"id\":\"resource-sp-id\"}]}")
            });
            // Resource SP app roles
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"appRoles\":[{\"value\":\"Agent365.Observability.OtelWrite\",\"id\":\"role-id-1\"}]}")
            });
            // Existing assignments — none
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[]}")
            });
            // POST succeeds
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"id\":\"assignment-id\"}")
            });

            // Act
            var result = await service.GrantAppRoleAssignmentAsync(
                "tenant-id", "blueprint-sp-id", "resource-app-id",
                new[] { "Agent365.Observability.OtelWrite" });

            // Assert
            result.AllSucceeded.Should().BeTrue();
            result.AllAlreadyAssigned.Should().BeFalse(
                because: "at least one POST was issued (no existing assignment) — this is the newly-granted path, not the idempotent skip");
        }
    }

    [Fact]
    public async Task GrantAppRoleAssignmentAsync_WhenPostFails_ReturnsFalse()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            // SP lookup
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[{\"id\":\"resource-sp-id\"}]}")
            });
            // Resource SP app roles
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"appRoles\":[{\"value\":\"Agent365.Observability.OtelWrite\",\"id\":\"role-id-1\"}]}")
            });
            // Existing assignments — none
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[]}")
            });
            // POST fails
            handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"error\":{\"code\":\"Authorization_RequestDenied\"}}")
            });

            // Act
            var result = await service.GrantAppRoleAssignmentAsync(
                "tenant-id", "blueprint-sp-id", "resource-app-id",
                new[] { "Agent365.Observability.OtelWrite" });

            // Assert
            result.AllSucceeded.Should().BeFalse();
            result.AllAlreadyAssigned.Should().BeFalse();
        }
    }

    [Fact]
    public async Task GrantAppRoleAssignmentAsync_WithEmptyRoleNames_ReturnsTrue()
    {
        // Arrange
        var (service, handler) = CreateServiceWithFakeHandler();
        using (handler)
        {
            // Act — no HTTP calls should be made for an empty role list
            var result = await service.GrantAppRoleAssignmentAsync(
                "tenant-id", "blueprint-sp-id", "resource-app-id",
                Array.Empty<string>());

            // Assert
            result.AllSucceeded.Should().BeTrue();
            result.AllAlreadyAssigned.Should().BeTrue(
                because: "no roles to grant means there is nothing newly created — treated as fully idempotent");
        }
    }

    // ── FindExistingAgentIdentityAsync tests ──────────────────────────────────

    /// <summary>
    /// Returns a partial mock of AgentBlueprintService with GetAgentInstancesForBlueprintAsync
    /// available for stubbing. The GraphApiService dependency is inert (no HTTP calls expected).
    /// </summary>
    private AgentBlueprintService BuildPartialBlueprintService() =>
        Substitute.ForPartsOf<AgentBlueprintService>(
            _mockLogger,
            new GraphApiService(_mockGraphLogger, _mockExecutor,
                Substitute.For<IAuthenticationService>(),
                new FakeHttpMessageHandler(),
                tokenProvider: null));

    [Fact]
    public async Task FindExistingAgentIdentityAsync_ReturnsSpId_WhenDisplayNameMatches()
    {
        var service = BuildPartialBlueprintService();
        service.GetAgentInstancesForBlueprintAsync("tenant-id", "blueprint-id", Arg.Any<CancellationToken>())
            .Returns(new List<AgentInstanceInfo>
            {
                new() { IdentitySpId = "sp-id-123", DisplayName = "sellakapri211 Identity" }
            });

        var result = await service.FindExistingAgentIdentityAsync("tenant-id", "blueprint-id", "sellakapri211 Identity");

        result.Should().Be("sp-id-123",
            because: "the service must return the SP ID of the matching agent identity");
    }

    [Fact]
    public async Task FindExistingAgentIdentityAsync_IsCaseInsensitive()
    {
        var service = BuildPartialBlueprintService();
        service.GetAgentInstancesForBlueprintAsync("tenant-id", "blueprint-id", Arg.Any<CancellationToken>())
            .Returns(new List<AgentInstanceInfo>
            {
                new() { IdentitySpId = "sp-id-456", DisplayName = "MY AGENT IDENTITY" }
            });

        var result = await service.FindExistingAgentIdentityAsync("tenant-id", "blueprint-id", "my agent identity");

        result.Should().Be("sp-id-456",
            because: "display name matching must be case-insensitive");
    }

    [Fact]
    public async Task FindExistingAgentIdentityAsync_ReturnsNull_WhenNoMatch()
    {
        var service = BuildPartialBlueprintService();
        service.GetAgentInstancesForBlueprintAsync("tenant-id", "blueprint-id", Arg.Any<CancellationToken>())
            .Returns(new List<AgentInstanceInfo>
            {
                new() { IdentitySpId = "sp-id-999", DisplayName = "Some Other Agent Identity" }
            });

        var result = await service.FindExistingAgentIdentityAsync("tenant-id", "blueprint-id", "sellakapri211 Identity");

        result.Should().BeNull(because: "no entry matches the requested display name");
    }

    [Fact]
    public async Task FindExistingAgentIdentityAsync_ReturnsNull_WhenListIsEmpty()
    {
        var service = BuildPartialBlueprintService();
        service.GetAgentInstancesForBlueprintAsync("tenant-id", "blueprint-id", Arg.Any<CancellationToken>())
            .Returns(new List<AgentInstanceInfo>());

        var result = await service.FindExistingAgentIdentityAsync("tenant-id", "blueprint-id", "sellakapri211 Identity");

        result.Should().BeNull(because: "an empty list means no agent identities exist for the blueprint");
    }

    [Fact]
    public async Task FindExistingAgentIdentityAsync_ReturnsNull_WhenLookupThrows()
    {
        var service = BuildPartialBlueprintService();
        service.GetAgentInstancesForBlueprintAsync("tenant-id", "blueprint-id", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<AgentInstanceInfo>>(_ => throw new InvalidOperationException("Network error"));

        var result = await service.FindExistingAgentIdentityAsync("tenant-id", "blueprint-id", "sellakapri211 Identity");

        result.Should().BeNull(because: "exceptions from the underlying query must be swallowed non-fatally");
    }

    private static JsonDocument JsonDoc(string json) => JsonDocument.Parse(json);

    private (AgentBlueprintService Service, GraphApiService Graph) BuildServiceWithMockedGraph()
    {
        // Pass a no-op loginHintResolver so any partially-mocked virtual that falls through to
        // ResolveLoginHintAsync doesn't spawn a real `az account show` subprocess in tests. The
        // tests using this helper stub all Graph virtuals they touch, but explicit loginHint
        // wiring is the safer pattern matching the rest of this file.
        var graph = Substitute.ForPartsOf<GraphApiService>(
            _mockGraphLogger,
            _mockExecutor,
            (Func<Task<string?>>?)(() => Task.FromResult<string?>(null)));
        var service = new AgentBlueprintService(_mockLogger, graph);
        return (service, graph);
    }

    // ── GetBlueprintSpGrantsAsync branch tests (TEST-HIGH-1) ───────────────────
    // The happy path is covered by GetBlueprintSpGrantsAsync_ReturnsScopesAndResolvedRoleNames
    // above. These additional tests pin the five non-happy branches so a future refactor cannot
    // silently drop a resource from the dictionary, fail to surface an unresolved role, or
    // change the "blueprint SP missing" semantics that downstream commands (e.g. query-entra
    // inheritance) depend on.

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WithEmptyResourceAppIds_ReturnsEmptyDictionary_WithoutGraphCalls()
    {
        // The early-exit before any Graph call matters: operators run query-entra inheritance
        // against blueprints that may legitimately have no inheritable resources, and we must
        // not waste a token acquisition or surface a misleading "blueprint SP not found" warning
        // in that case.
        var (service, graph) = BuildServiceWithMockedGraph();

        var result = await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", Array.Empty<string>());

        result.Should().BeEmpty(
            because: "no resource app IDs means there's nothing to enumerate — the method must short-circuit to an empty dict");

        await graph.DidNotReceive().LookupServicePrincipalByAppIdWithResponseAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<GraphAuthenticationMode>());
        await graph.DidNotReceive().GetOauth2PermissionGrantsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WhenBlueprintSpNotFound_ReturnsEmptyDictionary_AndLogsWarning()
    {
        // If the blueprint app has no provisioned SP in this tenant the operator needs a visible
        // signal — a Warning, not Debug — because every downstream "what does this agent inherit?"
        // answer would otherwise come back empty without explanation.
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                StatusCode = 200
            });

        var result = await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "resource-app-id" });

        result.Should().BeEmpty(
            because: "without a blueprint SP there is nothing to enumerate grants against — callers must see an empty dict, not a partial result");

        // No grant enumeration must occur after the SP lookup failed.
        await graph.DidNotReceive().GetOauth2PermissionGrantsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        _mockLogger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Blueprint service principal not found")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WhenBlueprintSpLookupFails_Throws()
    {
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = false,
                StatusCode = 403,
                FailureReason = "Microsoft Graph service-principal lookup failed: HTTP 403 Forbidden."
            });

        Func<Task> act = async () => await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "resource-app-id" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*HTTP 403 Forbidden*",
                because: "an unreadable blueprint lookup must not masquerade as a successfully absent service principal");
        await graph.DidNotReceive().GetOauth2PermissionGrantsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WhenAppRoleAssignmentsReadFails_Throws()
    {
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "bp-sp-id",
                StatusCode = 200
            });
        graph.GetOauth2PermissionGrantsAsync(
                "tenant-id",
                "bp-sp-id",
                Arg.Any<CancellationToken>())
            .Returns(new List<(string resourceId, string scope, string consentType)>());
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains("/servicePrincipals/bp-sp-id/appRoleAssignments", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = false,
                StatusCode = 403,
                ReasonPhrase = "Forbidden"
            });

        Func<Task> act = async () => await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "resource-app-id" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*app role assignments*HTTP 403 Forbidden*",
                because: "a failed assignments read must remain distinct from a successful empty value array");
        await graph.DidNotReceive().LookupServicePrincipalByAppIdWithResponseAsync(
            "tenant-id",
            "resource-app-id",
            Arg.Any<CancellationToken>(),
            GraphAuthenticationMode.Ambient);
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WhenAppRoleAssignmentsTransportFails_ThrowsWithFailureReason()
    {
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "bp-sp-id",
                StatusCode = 200
            });
        graph.GetOauth2PermissionGrantsAsync(
                "tenant-id",
                "bp-sp-id",
                Arg.Any<CancellationToken>())
            .Returns(new List<(string resourceId, string scope, string consentType)>());
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains("/servicePrincipals/bp-sp-id/appRoleAssignments", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = false,
                StatusCode = 0,
                ReasonPhrase = "connection reset"
            });

        Func<Task> act = async () => await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "resource-app-id" });

        var exception = await act.Should().ThrowAsync<InvalidOperationException>(
            because: "a transport failure while reading assignments must not be treated as a successful empty assignment list");
        exception.Which.Message.Should().Contain("connection reset",
            because: "the status-zero response reason is the only visible transport diagnostic");
        exception.Which.Message.Should().NotContain("HTTP 0",
            because: "no HTTP response exists when Graph reports status zero");
        await graph.DidNotReceive().LookupServicePrincipalByAppIdWithResponseAsync(
            "tenant-id",
            "resource-app-id",
            Arg.Any<CancellationToken>(),
            GraphAuthenticationMode.Ambient);
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WhenAppRoleAssignmentsShapeIsMalformed_Throws()
    {
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "bp-sp-id",
                StatusCode = 200
            });
        graph.GetOauth2PermissionGrantsAsync(
                "tenant-id",
                "bp-sp-id",
                Arg.Any<CancellationToken>())
            .Returns(new List<(string resourceId, string scope, string consentType)>());
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc(@"{ ""value"": {} }")
            });

        Func<Task> act = async () => await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "resource-app-id" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid app role assignments response*",
                because: "an unexpected successful response shape must not be treated as an empty assignments collection");
    }

    [Theory]
    [InlineData("not-a-guid", "44444444-4444-4444-4444-444444444444")]
    [InlineData("33333333-3333-3333-3333-333333333333", "not-a-guid")]
    public async Task GetBlueprintSpGrantsAsync_WhenAppRoleAssignmentIdentifierIsNotGuid_Throws(
        string resourceId,
        string appRoleId)
    {
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "bp-sp-id",
                StatusCode = 200
            });
        graph.GetOauth2PermissionGrantsAsync(
                "tenant-id",
                "bp-sp-id",
                Arg.Any<CancellationToken>())
            .Returns(new List<(string resourceId, string scope, string consentType)>());
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains("/servicePrincipals/bp-sp-id/appRoleAssignments", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc($$"""
                    {
                      "value": [
                        { "resourceId": "{{resourceId}}", "appRoleId": "{{appRoleId}}" }
                      ]
                    }
                    """)
            });

        Func<Task> act = async () => await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "resource-app-id" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid app role assignment*",
                because: "Graph app role assignment identifiers must be GUIDs rather than arbitrary non-empty strings");
        await graph.DidNotReceive().LookupServicePrincipalByAppIdWithResponseAsync(
            "tenant-id",
            "resource-app-id",
            Arg.Any<CancellationToken>(),
            GraphAuthenticationMode.Ambient);
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WhenResourceSpMissing_OmitsResourceFromDictionary()
    {
        // The contract documented on the method: "Resources whose SP cannot be resolved are
        // omitted (a debug log is emitted)." A missing resource must not appear as a key with
        // empty arrays — that would be indistinguishable from a known resource with zero grants,
        // and the inheritance command surfaces the two cases differently.
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "bp-sp-id",
                StatusCode = 200
            });
        graph.GetOauth2PermissionGrantsAsync(
                "tenant-id",
                "bp-sp-id",
                Arg.Any<CancellationToken>())
            .Returns(new List<(string resourceId, string scope, string consentType)>());
        // appRoleAssignments — empty
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(s => s.Contains("/servicePrincipals/bp-sp-id/appRoleAssignments", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc(@"{ ""value"": [] }")
            });
        // Resource SP lookup returns null — this resource is unprovisioned.
        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "missing-resource-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                StatusCode = 200
            });

        var result = await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "missing-resource-app-id" });

        result.Should().NotContainKey("missing-resource-app-id",
            because: "an unresolvable resource SP must be omitted from the result entirely — not surfaced as a zero-grants entry — so callers can distinguish 'unknown' from 'known but empty'");
        result.Should().BeEmpty(
            because: "the only requested resource was unresolvable, so the dictionary must be empty");
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WhenResourceSpLookupFails_Throws()
    {
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "bp-sp-id",
                StatusCode = 200
            });
        graph.GetOauth2PermissionGrantsAsync(
                "tenant-id",
                "bp-sp-id",
                Arg.Any<CancellationToken>())
            .Returns(new List<(string resourceId, string scope, string consentType)>());
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc(@"{ ""value"": [] }")
            });
        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "resource-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = false,
                StatusCode = 503,
                FailureReason = "Microsoft Graph service-principal lookup failed: HTTP 503 Service Unavailable."
            });

        Func<Task> act = async () => await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "resource-app-id" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*HTTP 503 Service Unavailable*",
                because: "a failed resource lookup must not be omitted as though Graph successfully found no service principal");
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WhenResourceHasZeroGrants_IncludesResourceWithEmptyArrays()
    {
        // The contract documented on the method: "Resources with no grants on the blueprint SP
        // are present in the dictionary with empty arrays." This is the inverse of the
        // missing-SP branch above — operators reading the inheritance output must be able to
        // tell "this resource has no grants" apart from "we couldn't look this resource up".
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "bp-sp-id",
                StatusCode = 200
            });
        graph.GetOauth2PermissionGrantsAsync(
                "tenant-id",
                "bp-sp-id",
                Arg.Any<CancellationToken>())
            .Returns(new List<(string resourceId, string scope, string consentType)>());
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(s => s.Contains("/servicePrincipals/bp-sp-id/appRoleAssignments", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc(@"{ ""value"": [] }")
            });
        // Resource SP exists but has no delegated grants or app role assignments.
        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "zero-grants-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "zero-grants-sp-id",
                StatusCode = 200
            });

        var result = await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "zero-grants-app-id" });

        result.Should().ContainKey("zero-grants-app-id",
            because: "a known-but-ungranted resource must still appear in the dictionary so the operator sees explicit confirmation that no grants exist");
        var (delegatedScopes, appRoleNames) = result["zero-grants-app-id"];
        delegatedScopes.Should().BeEmpty(
            because: "no delegated grants were issued on the blueprint SP for this resource");
        appRoleNames.Should().BeEmpty(
            because: "no app role assignments were issued on the blueprint SP for this resource");
        await graph.Received(1).GetOauth2PermissionGrantsAsync(
            "tenant-id",
            "bp-sp-id",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WhenAppRoleIdIsUnknown_FallsBackToAngleBracketPlaceholder()
    {
        const string resourceSpId = "33333333-3333-3333-3333-333333333333";
        const string unknownRoleId = "44444444-4444-4444-4444-444444444444";
        const string otherRoleId = "55555555-5555-5555-5555-555555555555";

        // When the blueprint SP has an app role assignment but the resource SP's appRoles array
        // does not contain a matching entry (e.g. the role was removed from the resource after
        // assignment, or the resource SP doc was fetched with a $select that elided it), the
        // method must still surface the role ID so the operator can investigate. The wrapper
        // form is "<role-id>" and we pin it because downstream UI keys off the angle brackets
        // to flag the entry as unresolved.
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "bp-sp-id",
                StatusCode = 200
            });
        graph.GetOauth2PermissionGrantsAsync(
                "tenant-id",
                "bp-sp-id",
                Arg.Any<CancellationToken>())
            .Returns(new List<(string resourceId, string scope, string consentType)>());
        // App role assignment exists on the blueprint SP for our resource.
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(s => s.Contains("/servicePrincipals/bp-sp-id/appRoleAssignments", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc($$"""
                    {
                      "value": [
                        { "resourceId": "{{resourceSpId}}", "appRoleId": "{{unknownRoleId}}" }
                      ]
                    }
                    """)
            });
        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "resource-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = resourceSpId,
                StatusCode = 200
            });
        // Resource SP appRoles does not contain the assigned role ID.
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(s => s.Contains($"/servicePrincipals/{resourceSpId}?$select=appRoles", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc($$"""
                    {
                      "appRoles": [
                        { "id": "{{otherRoleId}}", "value": "Some.Other.Role" }
                      ]
                    }
                    """)
            });

        var result = await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "resource-app-id" });

        result.Should().ContainKey("resource-app-id");
        var (_, appRoleNames) = result["resource-app-id"];
        appRoleNames.Should().Equal(new[] { $"<{unknownRoleId}>" },
            because: "an unresolvable role ID must surface as '<role-id>' so operators can still see and investigate the assignment — silently dropping it would hide a real grant");
        await graph.Received(1).GraphGetWithResponseAsync(
            "tenant-id",
            Arg.Is<string>(path => path.Contains($"/servicePrincipals/{resourceSpId}?$select=appRoles", StringComparison.Ordinal)),
            Arg.Any<bool>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            GraphAuthenticationMode.Ambient);
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WhenAssignedRoleMetadataIsEmpty_ReturnsAngleBracketPlaceholder()
    {
        const string resourceSpId = "33333333-3333-3333-3333-333333333333";
        const string assignedRoleId = "44444444-4444-4444-4444-444444444444";
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "bp-sp-id",
                StatusCode = 200
            });
        graph.GetOauth2PermissionGrantsAsync(
                "tenant-id",
                "bp-sp-id",
                Arg.Any<CancellationToken>())
            .Returns(new List<(string resourceId, string scope, string consentType)>());
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains("/servicePrincipals/bp-sp-id/appRoleAssignments", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc($$"""
                    {
                      "value": [
                        { "resourceId": "{{resourceSpId}}", "appRoleId": "{{assignedRoleId}}" }
                      ]
                    }
                    """)
            });
        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "resource-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = resourceSpId,
                StatusCode = 200
            });
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains($"/servicePrincipals/{resourceSpId}?$select=appRoles", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc("""{ "appRoles": [] }""")
            });

        var result = await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "resource-app-id" });

        result["resource-app-id"].AppRoleNames.Should().Equal(new[] { $"<{assignedRoleId}>" },
            because: "a successful empty metadata array cannot erase a real assignment and must use the documented unresolved-role placeholder");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"appRoles\":{}}")]
    public async Task GetBlueprintSpGrantsAsync_WhenRoleMetadataTopLevelPayloadIsMalformed_Throws(string responseBody)
    {
        const string resourceSpId = "33333333-3333-3333-3333-333333333333";
        const string assignedRoleId = "44444444-4444-4444-4444-444444444444";
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "bp-sp-id",
                StatusCode = 200
            });
        graph.GetOauth2PermissionGrantsAsync(
                "tenant-id",
                "bp-sp-id",
                Arg.Any<CancellationToken>())
            .Returns(new List<(string resourceId, string scope, string consentType)>());
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains("/servicePrincipals/bp-sp-id/appRoleAssignments", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc($$"""
                    {
                      "value": [
                        { "resourceId": "{{resourceSpId}}", "appRoleId": "{{assignedRoleId}}" }
                      ]
                    }
                    """)
            });
        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "resource-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = resourceSpId,
                StatusCode = 200
            });
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains($"/servicePrincipals/{resourceSpId}?$select=appRoles", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc(responseBody)
            });

        Func<Task> act = async () => await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "resource-app-id" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid app role metadata*",
                because: "a successful response without an array-valued 'appRoles' member cannot resolve a real assignment safely");
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WhenAppRoleMetadataIdIsNotGuid_Throws()
    {
        const string resourceSpId = "33333333-3333-3333-3333-333333333333";
        const string assignedRoleId = "44444444-4444-4444-4444-444444444444";
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "bp-sp-id",
                StatusCode = 200
            });
        graph.GetOauth2PermissionGrantsAsync(
                "tenant-id",
                "bp-sp-id",
                Arg.Any<CancellationToken>())
            .Returns(new List<(string resourceId, string scope, string consentType)>());
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains("/servicePrincipals/bp-sp-id/appRoleAssignments", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc($$"""
                    {
                      "value": [
                        { "resourceId": "{{resourceSpId}}", "appRoleId": "{{assignedRoleId}}" }
                      ]
                    }
                    """)
            });
        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "resource-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = resourceSpId,
                StatusCode = 200
            });
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains($"/servicePrincipals/{resourceSpId}?$select=appRoles", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc("""
                    {
                      "appRoles": [
                        { "id": "not-a-guid", "value": "Malformed.Role" }
                      ]
                    }
                    """)
            });

        Func<Task> act = async () => await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "resource-app-id" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid app role metadata*",
                because: "Graph app role metadata identifiers must be GUIDs rather than arbitrary non-empty strings");
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WhenRoleMetadataReadFails_Throws()
    {
        const string resourceSpId = "33333333-3333-3333-3333-333333333333";
        const string appRoleId = "44444444-4444-4444-4444-444444444444";
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "bp-sp-id",
                StatusCode = 200
            });
        graph.GetOauth2PermissionGrantsAsync(
                "tenant-id",
                "bp-sp-id",
                Arg.Any<CancellationToken>())
            .Returns(new List<(string resourceId, string scope, string consentType)>());
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains("/servicePrincipals/bp-sp-id/appRoleAssignments", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc($$"""
                    {
                      "value": [
                        { "resourceId": "{{resourceSpId}}", "appRoleId": "{{appRoleId}}" }
                      ]
                    }
                    """)
            });
        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "resource-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = resourceSpId,
                StatusCode = 200
            });
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains($"/servicePrincipals/{resourceSpId}?$select=appRoles", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = false,
                StatusCode = 503,
                ReasonPhrase = "Service Unavailable"
            });

        Func<Task> act = async () => await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "resource-app-id" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*app role metadata*HTTP 503 Service Unavailable*",
                because: "unreadable role metadata must not turn known assignments into unresolved placeholders");
    }

    [Fact]
    public async Task GetBlueprintSpGrantsAsync_WhenRoleMetadataTransportFails_ThrowsWithFailureReason()
    {
        const string resourceSpId = "33333333-3333-3333-3333-333333333333";
        const string appRoleId = "44444444-4444-4444-4444-444444444444";
        var (service, graph) = BuildServiceWithMockedGraph();

        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "blueprint-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = "bp-sp-id",
                StatusCode = 200
            });
        graph.GetOauth2PermissionGrantsAsync(
                "tenant-id",
                "bp-sp-id",
                Arg.Any<CancellationToken>())
            .Returns(new List<(string resourceId, string scope, string consentType)>());
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains("/servicePrincipals/bp-sp-id/appRoleAssignments", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Json = JsonDoc($$"""
                    {
                      "value": [
                        { "resourceId": "{{resourceSpId}}", "appRoleId": "{{appRoleId}}" }
                      ]
                    }
                    """)
            });
        graph.LookupServicePrincipalByAppIdWithResponseAsync(
                "tenant-id",
                "resource-app-id",
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.ServicePrincipalLookupResult
            {
                IsSuccess = true,
                ServicePrincipalId = resourceSpId,
                StatusCode = 200
            });
        graph.GraphGetWithResponseAsync(
                "tenant-id",
                Arg.Is<string>(path => path.Contains($"/servicePrincipals/{resourceSpId}?$select=appRoles", StringComparison.Ordinal)),
                Arg.Any<bool>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                GraphAuthenticationMode.Ambient)
            .Returns(new GraphApiService.GraphResponse
            {
                IsSuccess = false,
                StatusCode = 0,
                ReasonPhrase = "connection reset"
            });

        Func<Task> act = async () => await service.GetBlueprintSpGrantsAsync(
            "tenant-id", "blueprint-app-id", new[] { "resource-app-id" });

        var exception = await act.Should().ThrowAsync<InvalidOperationException>(
            because: "a role metadata transport failure must remain distinct from a successful empty metadata array");
        exception.Which.Message.Should().Contain("connection reset",
            because: "the status-zero Graph response reason must remain visible to the operator");
        exception.Which.Message.Should().NotContain("HTTP 0",
            because: "status zero represents absence of an HTTP response rather than an HTTP protocol status");
    }
}

// Simple fake handler that returns queued responses sequentially. Also captures the request
// method, URL, and body for tests that need to assert on what was sent.
internal class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly List<HttpResponseMessage> _sentResponses = new();

    public record CapturedRequest(HttpMethod Method, string Url, string? Body);

    public List<CapturedRequest> SentRequests { get; } = new();

    public void QueueResponse(HttpResponseMessage resp) => _responses.Enqueue(resp);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        SentRequests.Add(new CapturedRequest(request.Method, request.RequestUri?.ToString() ?? string.Empty, body));

        if (_responses.Count == 0)
        {
            var fallback = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
            _sentResponses.Add(fallback);
            return fallback;
        }

        var resp = _responses.Dequeue();
        _sentResponses.Add(resp);
        return resp;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var resp in _sentResponses)
                resp.Dispose();
            _sentResponses.Clear();

            while (_responses.Count > 0)
                _responses.Dequeue().Dispose();
        }
        base.Dispose(disposing);
    }
}
