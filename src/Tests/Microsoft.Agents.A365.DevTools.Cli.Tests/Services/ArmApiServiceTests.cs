// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Unit tests for ArmApiService.
/// Uses TestHttpMessageHandler (defined in GraphApiServiceTests.cs, same assembly)
/// to inject fake HTTP responses.
/// </summary>
public class ArmApiServiceTests
{
    private const string TenantId = "tid";
    private const string SubscriptionId = "sub-123";
    private const string ResourceGroup = "rg-test";
    private const string PlanName = "plan-test";
    private const string WebAppName = "webapp-test";
    private const string UserObjectId = "user-obj-id";

    private static IAuthenticationService FakeAuth()
    {
        var mock = Substitute.For<IAuthenticationService>();
        mock.GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Task.FromResult("fake-arm-token"));
        return mock;
    }

    private static ArmApiService CreateService(HttpMessageHandler handler) =>
        new ArmApiService(NullLogger<ArmApiService>.Instance, FakeAuth(), handler, retryHelper: new RetryHelper(NullLogger.Instance, maxRetries: 1, baseDelaySeconds: 0));

    // ──────────────────────────── ResourceGroupExistsAsync ────────────────────────────

    [Fact]
    public async Task ResourceGroupExistsAsync_When200_ReturnsTrue()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        var svc = CreateService(handler);

        var result = await svc.ResourceGroupExistsAsync(SubscriptionId, ResourceGroup, TenantId);

        result.Should().BeTrue(because: "HTTP 200 means the resource group exists");
    }

    [Fact]
    public async Task ResourceGroupExistsAsync_When404_ReturnsFalse()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        var svc = CreateService(handler);

        var result = await svc.ResourceGroupExistsAsync(SubscriptionId, ResourceGroup, TenantId);

        result.Should().BeFalse(because: "HTTP 404 means the resource group does not exist");
    }

    [Fact]
    public async Task ResourceGroupExistsAsync_WhenHttpThrows_ReturnsNull()
    {
        using var handler = new ThrowingHttpMessageHandler();
        var svc = CreateService(handler);

        var result = await svc.ResourceGroupExistsAsync(SubscriptionId, ResourceGroup, TenantId);

        result.Should().BeNull(because: "a network exception should cause the caller to fall back to az CLI");
    }

    [Fact]
    public async Task ResourceGroupExistsAsync_When401_ReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(string.Empty) });
        var svc = CreateService(handler);

        var result = await svc.ResourceGroupExistsAsync(SubscriptionId, ResourceGroup, TenantId);

        result.Should().BeNull(because: "a 401 means the ARM token lacks permission — caller must fall back to az CLI, not treat the resource as absent");
    }

    // ──────────────────────────── AppServicePlanExistsAsync ───────────────────────────

    [Fact]
    public async Task AppServicePlanExistsAsync_When200_ReturnsTrue()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        var svc = CreateService(handler);

        var result = await svc.AppServicePlanExistsAsync(SubscriptionId, ResourceGroup, PlanName, TenantId);

        result.Should().BeTrue(because: "HTTP 200 means the App Service plan exists");
    }

    [Fact]
    public async Task AppServicePlanExistsAsync_When404_ReturnsFalse()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        var svc = CreateService(handler);

        var result = await svc.AppServicePlanExistsAsync(SubscriptionId, ResourceGroup, PlanName, TenantId);

        result.Should().BeFalse(because: "HTTP 404 means the App Service plan does not exist");
    }

    [Fact]
    public async Task AppServicePlanExistsAsync_WhenHttpThrows_ReturnsNull()
    {
        using var handler = new ThrowingHttpMessageHandler();
        var svc = CreateService(handler);

        var result = await svc.AppServicePlanExistsAsync(SubscriptionId, ResourceGroup, PlanName, TenantId);

        result.Should().BeNull(because: "a network exception should cause the caller to fall back to az CLI");
    }

    [Fact]
    public async Task AppServicePlanExistsAsync_When401_ReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(string.Empty) });
        var svc = CreateService(handler);

        var result = await svc.AppServicePlanExistsAsync(SubscriptionId, ResourceGroup, PlanName, TenantId);

        result.Should().BeNull(because: "a 401 means the ARM token lacks permission — caller must fall back to az CLI, not treat the plan as absent");
    }

    // ──────────────────────────── WebAppExistsAsync ───────────────────────────────────

    [Fact]
    public async Task WebAppExistsAsync_When200_ReturnsTrue()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        var svc = CreateService(handler);

        var result = await svc.WebAppExistsAsync(SubscriptionId, ResourceGroup, WebAppName, TenantId);

        result.Should().BeTrue(because: "HTTP 200 means the web app exists");
    }

    [Fact]
    public async Task WebAppExistsAsync_When404_ReturnsFalse()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        var svc = CreateService(handler);

        var result = await svc.WebAppExistsAsync(SubscriptionId, ResourceGroup, WebAppName, TenantId);

        result.Should().BeFalse(because: "HTTP 404 means the web app does not exist");
    }

    [Fact]
    public async Task WebAppExistsAsync_When401_ReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(string.Empty) });
        var svc = CreateService(handler);

        var result = await svc.WebAppExistsAsync(SubscriptionId, ResourceGroup, WebAppName, TenantId);

        result.Should().BeNull(because: "a 401 means the ARM token lacks permission — caller must fall back to az CLI, not treat the web app as absent");
    }

    [Fact]
    public async Task WebAppExistsAsync_WhenHttpThrows_ReturnsNull()
    {
        using var handler = new ThrowingHttpMessageHandler();
        var svc = CreateService(handler);

        var result = await svc.WebAppExistsAsync(SubscriptionId, ResourceGroup, WebAppName, TenantId);

        result.Should().BeNull(because: "a network exception should cause the caller to fall back to az CLI");
    }

    // ──────────────────────────── GetSufficientWebAppRoleAsync ────────────────────────

    [Fact]
    public async Task GetSufficientWebAppRoleAsync_WhenOwnerAtSubscriptionScope_ReturnsOwner()
    {
        // Owner role at subscription scope — scope chain includes the web app (inherited).
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(BuildRoleAssignmentsResponse(
            scope: $"/subscriptions/{SubscriptionId}",
            roleGuid: "8e3af657-a8ff-443c-a75c-2fe8c4bcb635")); // Owner
        var svc = CreateService(handler);

        var result = await svc.GetSufficientWebAppRoleAsync(SubscriptionId, ResourceGroup, WebAppName, UserObjectId, TenantId);

        result.Should().Be("Owner",
            because: "Owner at subscription scope is inherited by all resources in that subscription");
    }

    [Fact]
    public async Task GetSufficientWebAppRoleAsync_WhenContributorAtResourceGroupScope_ReturnsContributor()
    {
        // Contributor role at the resource group — inherited by the web app within it.
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(BuildRoleAssignmentsResponse(
            scope: $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}",
            roleGuid: "b24988ac-6180-42a0-ab88-20f7382dd24c")); // Contributor
        var svc = CreateService(handler);

        var result = await svc.GetSufficientWebAppRoleAsync(SubscriptionId, ResourceGroup, WebAppName, UserObjectId, TenantId);

        result.Should().Be("Contributor",
            because: "Contributor at resource group scope is inherited by all resources in that group");
    }

    [Fact]
    public async Task GetSufficientWebAppRoleAsync_WhenNoSufficientRole_ReturnsEmpty()
    {
        // Role assignments exist but none are Owner/Contributor/Website Contributor.
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(BuildRoleAssignmentsResponse(
            scope: $"/subscriptions/{SubscriptionId}",
            roleGuid: "acdd72a7-3385-48ef-bd42-f606fba81ae7")); // Reader — not sufficient
        var svc = CreateService(handler);

        var result = await svc.GetSufficientWebAppRoleAsync(SubscriptionId, ResourceGroup, WebAppName, UserObjectId, TenantId);

        result.Should().BeEmpty(
            because: "Reader does not grant the access required to deploy or configure the web app");
    }

    [Fact]
    public async Task GetSufficientWebAppRoleAsync_WhenRoleIsAtUnrelatedScope_ReturnsEmpty()
    {
        // Owner on a different resource group — scope chain does NOT include our web app.
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(BuildRoleAssignmentsResponse(
            scope: $"/subscriptions/{SubscriptionId}/resourceGroups/other-rg",
            roleGuid: "8e3af657-a8ff-443c-a75c-2fe8c4bcb635")); // Owner, wrong scope
        var svc = CreateService(handler);

        var result = await svc.GetSufficientWebAppRoleAsync(SubscriptionId, ResourceGroup, WebAppName, UserObjectId, TenantId);

        result.Should().BeEmpty(
            because: "a role on an unrelated resource group does not grant access to our web app");
    }

    [Fact]
    public async Task GetSufficientWebAppRoleAsync_WhenHttpFails_ReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(string.Empty)
        });
        var svc = CreateService(handler);

        var result = await svc.GetSufficientWebAppRoleAsync(SubscriptionId, ResourceGroup, WebAppName, UserObjectId, TenantId);

        result.Should().BeNull(because: "a non-success HTTP response should cause the caller to fall back to az CLI");
    }

    [Fact]
    public async Task GetSufficientWebAppRoleAsync_WhenHttpThrows_ReturnsNull()
    {
        using var handler = new ThrowingHttpMessageHandler();
        var svc = CreateService(handler);

        var result = await svc.GetSufficientWebAppRoleAsync(SubscriptionId, ResourceGroup, WebAppName, UserObjectId, TenantId);

        result.Should().BeNull(because: "a network exception should cause the caller to fall back to az CLI");
    }

    // ──────────────────────────── Helpers ─────────────────────────────────────────────

    private static HttpResponseMessage BuildRoleAssignmentsResponse(string scope, string roleGuid)
    {
        var body = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    properties = new
                    {
                        scope,
                        roleDefinitionId = $"/subscriptions/{SubscriptionId}/providers/Microsoft.Authorization/roleDefinitions/{roleGuid}"
                    }
                }
            }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };
    }

    // ──────────────────────── GetEnterprisePolicySystemIdAsync ────────────────────────

    private const string PolicyArmId =
        "/subscriptions/8d1e5b21-0000-0000-0000-000000000000/resourceGroups/rg-test/providers/Microsoft.PowerPlatform/enterprisePolicies/policy-1";

    private const string PolicySystemId =
        "/regions/unitedstates/providers/Microsoft.PowerPlatform/enterprisePolicies/1b2c8a4e-0000-0000-0000-000000000000";

    private static HttpResponseMessage PolicyResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Theory]
    // Userinfo trick: `management.azure.com` becomes the username and the real host is the attacker's.
    [InlineData("@evil.example/x")]
    [InlineData("evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("https://evil.example/x")]
    [InlineData("/subscriptions/not-a-guid/resourceGroups/rg/providers/Microsoft.PowerPlatform/enterprisePolicies/p")]
    [InlineData("/subscriptions/8d1e5b21-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct")]
    [InlineData("/subscriptions/8d1e5b21-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.PowerPlatform/enterprisePolicies/p?x=1")]
    [InlineData("/subscriptions/8d1e5b21-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.PowerPlatform/enterprisePolicies/p#frag")]
    [InlineData("/subscriptions/8d1e5b21-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.PowerPlatform/enterprisePolicies/p/../../x")]
    public async Task GetEnterprisePolicySystemIdAsync_WhenArmIdIsNotAnEnterprisePolicyPath_RejectsWithoutCalling(
        string policyArmId)
    {
        using var handler = new TestHttpMessageHandler();
        var svc = CreateService(handler);

        var result = await svc.GetEnterprisePolicySystemIdAsync(policyArmId, TenantId);

        result.Should().BeNull();
        handler.RequestCount.Should().Be(
            0,
            because: "the ARM bearer token is a default header, so a redirected host would receive it");
    }

    [Fact]
    public async Task GetEnterprisePolicySystemIdAsync_When200_ReturnsSystemId()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(PolicyResponse(
            JsonSerializer.Serialize(new { properties = new { systemId = PolicySystemId } })));
        var svc = CreateService(handler);

        var result = await svc.GetEnterprisePolicySystemIdAsync(PolicyArmId, TenantId);

        result.Should().Be(PolicySystemId, because: "the systemId is the only value BAP accepts for a link");
    }

    [Fact]
    public async Task GetEnterprisePolicySystemIdAsync_RequestsTheArmPolicyResource()
    {
        HttpRequestMessage? captured = null;
        using var handler = new CapturingHttpMessageHandler(r => captured = r);
        handler.QueueResponse(PolicyResponse(
            JsonSerializer.Serialize(new { properties = new { systemId = PolicySystemId } })));
        var svc = CreateService(handler);

        await svc.GetEnterprisePolicySystemIdAsync(PolicyArmId, TenantId);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.ToString().Should().Be(
            $"https://management.azure.com{PolicyArmId}?api-version=2020-10-30");
    }

    [Fact]
    public async Task GetEnterprisePolicySystemIdAsync_WhenStableApiVersionRejected_RetriesWithPreview()
    {
        var urls = new List<string>();
        using var handler = new CapturingHttpMessageHandler(r => urls.Add(r.RequestUri!.ToString()));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("") });
        handler.QueueResponse(PolicyResponse(
            JsonSerializer.Serialize(new { properties = new { systemId = PolicySystemId } })));
        var svc = CreateService(handler);

        var result = await svc.GetEnterprisePolicySystemIdAsync(PolicyArmId, TenantId);

        result.Should().Be(PolicySystemId);
        urls.Should().HaveCount(2);
        urls[0].Should().EndWith("api-version=2020-10-30");
        urls[1].Should().EndWith("api-version=2020-10-30-preview");
    }

    [Fact]
    public async Task GetEnterprisePolicySystemIdAsync_WhenEveryApiVersionRejected_ReturnsNull()
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("") });
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("") });
        var svc = CreateService(handler);

        var result = await svc.GetEnterprisePolicySystemIdAsync(PolicyArmId, TenantId);

        result.Should().BeNull(because: "there is no api-version left to try");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetEnterprisePolicySystemIdAsync_WhenNonSuccess_ReturnsNullWithoutRetrying(HttpStatusCode status)
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(status) { Content = new StringContent("") });
        var svc = CreateService(handler);

        var result = await svc.GetEnterprisePolicySystemIdAsync(PolicyArmId, TenantId);

        result.Should().BeNull(because: "a rejected or missing policy is not an api-version problem");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"properties\":{}}")]
    [InlineData("{\"properties\":{\"systemId\":\"\"}}")]
    [InlineData("{\"properties\":{\"systemId\":\"   \"}}")]
    public async Task GetEnterprisePolicySystemIdAsync_WhenSystemIdMissing_ReturnsNull(string body)
    {
        using var handler = new TestHttpMessageHandler();
        handler.QueueResponse(PolicyResponse(body));
        var svc = CreateService(handler);

        var result = await svc.GetEnterprisePolicySystemIdAsync(PolicyArmId, TenantId);

        result.Should().BeNull(because: "a policy without a systemId is not yet usable for linking");
    }

    [Fact]
    public async Task GetEnterprisePolicySystemIdAsync_WhenHttpThrows_ReturnsNull()
    {
        using var handler = new ThrowingHttpMessageHandler();
        var svc = CreateService(handler);

        var result = await svc.GetEnterprisePolicySystemIdAsync(PolicyArmId, TenantId);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetEnterprisePolicySystemIdAsync_WhenPolicyArmIdBlank_Throws(string? policyArmId)
    {
        using var handler = new TestHttpMessageHandler();
        var svc = CreateService(handler);

        var act = async () => await svc.GetEnterprisePolicySystemIdAsync(policyArmId!, TenantId);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetEnterprisePolicySystemIdAsync_WhenTokenUnavailable_ReturnsNullWithoutCallingArm()
    {
        using var handler = new TestHttpMessageHandler();
        var auth = Substitute.For<IAuthenticationService>();
        auth.GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Task.FromResult(string.Empty));
        var svc = new ArmApiService(NullLogger<ArmApiService>.Instance, auth, handler,
            retryHelper: new RetryHelper(NullLogger.Instance, maxRetries: 1, baseDelaySeconds: 0));

        var result = await svc.GetEnterprisePolicySystemIdAsync(PolicyArmId, TenantId);

        result.Should().BeNull();
        handler.RequestCount.Should().Be(0, because: "without a token there is nothing worth sending");
    }
}

/// <summary>
/// HttpMessageHandler that always throws an HttpRequestException to simulate network failure.
/// </summary>
internal class ThrowingHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new HttpRequestException("Simulated network failure");

    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}
