// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class AgentPublishServiceTests
{
    private readonly ILogger<AgentPublishService> _mockLogger;
    private readonly ILogger<GraphApiService> _mockGraphLogger;
    private readonly CommandExecutor _mockExecutor;

    public AgentPublishServiceTests()
    {
        _mockLogger = Substitute.For<ILogger<AgentPublishService>>();
        _mockGraphLogger = Substitute.For<ILogger<GraphApiService>>();
        var mockExecutorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(mockExecutorLogger);
    }

    [Fact]
    public async Task ExecutePublishGraphStepsAsync_MakesExpectedHttpCallsInCorrectOrder()
    {
        // CRITICAL INTEGRATION TEST: This test captures the EXACT behavior of ExecutePublishGraphStepsAsync
        // before refactoring. It validates:
        // 1. The sequence of HTTP calls (GET/POST order)
        // 2. The URLs and query parameters
        // 3. Header values (Authorization present, ConsistencyLevel ABSENT)
        // 4. Request payloads
        // 5. Idempotency checks (checking before creating)
        //
        // This test serves as a REGRESSION GUARD during refactoring. After refactoring private methods
        // to use GraphGetAsync/GraphPostAsync helpers, this test MUST still pass, proving behavior is unchanged.
        //
        // Test approach: Use CapturingHttpMessageHandler to record ALL HTTP requests, then validate
        // each request matches expected behavior.

        // Arrange
        var capturedRequests = new List<HttpRequestMessage>();
        var handler = new MultiCapturingHttpMessageHandler((req) => capturedRequests.Add(req));
        var executor = Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>());

        const string tenantId = "11111111-1111-1111-1111-111111111111";
        const string blueprintId = "22222222-2222-2222-2222-222222222222";
        const string manifestId = "test-manifest-id";
        const string spObjectId = "sp-33333333-3333-3333-3333-333333333333";
        const string msGraphSpId = "ms-44444444-4444-4444-4444-444444444444";

        // Mock az CLI token acquisition
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cmd = callInfo.ArgAt<string>(0);
                var args = callInfo.ArgAt<string>(1);
                
                if (cmd == "az" && args != null && args.StartsWith("account show", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new CommandResult 
                    { 
                        ExitCode = 0, 
                        StandardOutput = JsonSerializer.Serialize(new { tenantId }), 
                        StandardError = string.Empty 
                    });
                }
                
                if (cmd == "az" && args != null && args.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new CommandResult 
                    { 
                        ExitCode = 0, 
                        StandardOutput = "test-bearer-token-xyz", 
                        StandardError = string.Empty 
                    });
                }
                
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = string.Empty, StandardError = string.Empty });
            });

        var graphService = new GraphApiService(_mockGraphLogger, executor, handler);
        var service = new AgentPublishService(_mockLogger, graphService);

        // Expected HTTP call sequence (based on current implementation):
        // 1. GET /beta/applications/{blueprintId}/federatedIdentityCredentials - check if FIC exists
        // 2. POST /beta/applications/{blueprintId}/federatedIdentityCredentials - create FIC (if not exists)
        // 3. GET /v1.0/servicePrincipals?$filter=appId eq '{blueprintId}' - lookup SP
        // 4. GET /v1.0/servicePrincipals?$filter=appId eq '{msGraphAppId}' - lookup MS Graph SP
        // 5. GET /v1.0/servicePrincipals/{spObjectId}/appRoleAssignments - check if role exists
        // 6. POST /v1.0/servicePrincipals/{spObjectId}/appRoleAssignments - assign role (if not exists)

        // Queue responses for each expected HTTP call
        // Response 1: GET FIC - return empty (FIC doesn't exist)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = Array.Empty<object>() }))
        });

        // Response 2: POST FIC - return created
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { id = "fic-created-id", name = $"fic-{manifestId}" }))
        });

        // Response 3: GET SP by appId - return service principal
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = spObjectId, appId = blueprintId } } }))
        });

        // Response 4: GET MS Graph SP - return Microsoft Graph service principal
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = msGraphSpId, appId = AuthenticationConstants.MicrosoftGraphResourceAppId } } }))
        });

        // Response 5: GET app role assignments - return empty (role doesn't exist)
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = Array.Empty<object>() }))
        });

        // Response 6: POST app role assignment - return created
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { id = "role-assignment-id" }))
        });

        // Act
        var result = await service.ExecutePublishGraphStepsAsync(tenantId, blueprintId, manifestId, CancellationToken.None);

        // Assert
        result.Should().BeTrue("ExecutePublishGraphStepsAsync should succeed");
        capturedRequests.Should().HaveCount(6, "should make exactly 6 HTTP calls");

        // Validate Request 1: GET federated identity credentials
        var req1 = capturedRequests[0];
        req1.Method.Should().Be(HttpMethod.Get);
        req1.RequestUri.Should().NotBeNull();
        req1.RequestUri!.AbsolutePath.Should().Be($"/beta/applications/{blueprintId}/federatedIdentityCredentials");
        req1.Headers.Authorization.Should().NotBeNull();
        req1.Headers.Authorization!.Scheme.Should().Be("Bearer");
        req1.Headers.Authorization.Parameter.Should().Be("test-bearer-token-xyz");
        req1.Headers.Contains("ConsistencyLevel").Should().BeFalse("ConsistencyLevel header must NOT be present");

        // Validate Request 2: POST federated identity credential
        var req2 = capturedRequests[1];
        req2.Method.Should().Be(HttpMethod.Post);
        req2.RequestUri.Should().NotBeNull();
        req2.RequestUri!.AbsolutePath.Should().Be($"/beta/applications/{blueprintId}/federatedIdentityCredentials");
        req2.Headers.Authorization.Should().NotBeNull();
        req2.Headers.Contains("ConsistencyLevel").Should().BeFalse("ConsistencyLevel header must NOT be present");
        req2.Content.Should().NotBeNull();
        var req2Body = await req2.Content!.ReadAsStringAsync();
        req2Body.Should().Contain($"fic-{manifestId}");
        req2Body.Should().Contain($"https://login.microsoftonline.com/{tenantId}/v2.0");
        req2Body.Should().Contain("api://AzureADTokenExchange");

        // Validate Request 3: GET service principal by appId
        var req3 = capturedRequests[2];
        req3.Method.Should().Be(HttpMethod.Get);
        req3.RequestUri.Should().NotBeNull();
        req3.RequestUri!.AbsolutePath.Should().Be("/v1.0/servicePrincipals");
        Uri.UnescapeDataString(req3.RequestUri.Query).Should().Contain($"$filter=appId eq '{blueprintId}'");
        req3.Headers.Authorization.Should().NotBeNull();
        req3.Headers.Contains("ConsistencyLevel").Should().BeFalse("ConsistencyLevel header must NOT be present");

        // Validate Request 4: GET Microsoft Graph service principal
        var req4 = capturedRequests[3];
        req4.Method.Should().Be(HttpMethod.Get);
        req4.RequestUri.Should().NotBeNull();
        req4.RequestUri!.AbsolutePath.Should().Be("/v1.0/servicePrincipals");
        Uri.UnescapeDataString(req4.RequestUri.Query).Should().Contain($"$filter=appId eq '{AuthenticationConstants.MicrosoftGraphResourceAppId}'");
        req4.Headers.Authorization.Should().NotBeNull();
        req4.Headers.Contains("ConsistencyLevel").Should().BeFalse("ConsistencyLevel header must NOT be present");

        // Validate Request 5: GET app role assignments
        var req5 = capturedRequests[4];
        req5.Method.Should().Be(HttpMethod.Get);
        req5.RequestUri.Should().NotBeNull();
        req5.RequestUri!.AbsolutePath.Should().Be($"/v1.0/servicePrincipals/{spObjectId}/appRoleAssignments");
        req5.Headers.Authorization.Should().NotBeNull();
        req5.Headers.Contains("ConsistencyLevel").Should().BeFalse("ConsistencyLevel header must NOT be present");

        // Validate Request 6: POST app role assignment
        var req6 = capturedRequests[5];
        req6.Method.Should().Be(HttpMethod.Post);
        req6.RequestUri.Should().NotBeNull();
        req6.RequestUri!.AbsolutePath.Should().Be($"/v1.0/servicePrincipals/{spObjectId}/appRoleAssignments");
        req6.Headers.Authorization.Should().NotBeNull();
        req6.Headers.Contains("ConsistencyLevel").Should().BeFalse("ConsistencyLevel header must NOT be present");
        req6.Content.Should().NotBeNull();
        var req6Body = await req6.Content!.ReadAsStringAsync();
        req6Body.Should().Contain(spObjectId);
        req6Body.Should().Contain(msGraphSpId);
        req6Body.Should().Contain("4aa6e624-eee0-40ab-bdd8-f9639038a614"); // AgentIdUser.ReadWrite.IdentityParentedBy role ID
    }
}

// Multi-capturing handler that captures ALL requests in a list (for integration tests)
internal class MultiCapturingHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly Action<HttpRequestMessage> _captureAction;

    public MultiCapturingHttpMessageHandler(Action<HttpRequestMessage> captureAction)
    {
        _captureAction = captureAction;
    }

    public void QueueResponse(HttpResponseMessage resp) => _responses.Enqueue(resp);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Capture each request in sequence for integration test validation
        _captureAction(request);

        if (_responses.Count == 0)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });

        var resp = _responses.Dequeue();
        return Task.FromResult(resp);
    }
}
