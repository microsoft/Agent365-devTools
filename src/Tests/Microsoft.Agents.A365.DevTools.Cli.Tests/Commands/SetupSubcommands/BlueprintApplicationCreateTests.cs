// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands.SetupSubcommands;

/// <summary>
/// Tests for the blueprint application create request sent to Microsoft Graph: serviceManagementReference
/// in the payload, and the sponsors/owners fallback chain.
/// </summary>
public class BlueprintApplicationCreateTests
{
    private const string GraphBaseUrl = "https://graph.microsoft.com";
    private const string DisplayName = "Contoso Agent Blueprint";
    private const string SponsorUserId = "11111111-1111-1111-1111-111111111111";
    private const string ReferenceId = "6f0e5d8a-3b1c-4c2d-9e7f-1a2b3c4d5e6f";

    private const string CreatedApplicationJson = """{"id":"object-id","appId":"app-id"}""";

    // Graph's message for a tenant that requires the property; classification keys on the message, not the code.
    internal const string ReferenceMissingJson =
        """{"error":{"code":"Request_BadRequest","message":"ServiceManagementReference field is required for Create, but is missing in the request. Refer to the TSG `https://aka.ms/service-management-reference-error` for resolving the error","innerError":{"date":"2026-10-08T12:00:00","request-id":"00000000-0000-0000-0000-00000000000a","client-request-id":"00000000-0000-0000-0000-00000000000b"}}}""";

    private const string SponsorBadRequestJson =
        """{"error":{"code":"Request_BadRequest","message":"Invalid value specified for property 'sponsors' of resource 'Application'."}}""";

    internal const string AuthorizationDeniedJson =
        """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""";

    [Fact]
    public async Task CreateBlueprintApplicationAsync_WithReference_SendsServiceManagementReference()
    {
        using var handler = new RecordingGraphHandler().Respond(HttpStatusCode.Created, CreatedApplicationJson);

        var (application, failure) = await CreateAsync(handler, SponsorUserId, new BlueprintCreationOptions(ServiceManagementReference: ReferenceId));

        failure.Should().BeNull();
        application.Should().NotBeNull();
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post,
            because: "Microsoft Graph creates application objects only through POST");
        request.Uri.Should().Be($"{GraphBaseUrl}/beta/applications",
            because: "blueprints are created by POSTing an AgentIdentityBlueprint-typed application to the Graph beta endpoint; moving it changes the Graph contract and must be a deliberate change, not one this test follows");
        request.Body["serviceManagementReference"]!.GetValue<string>().Should().Be(ReferenceId,
            because: "tenants that require serviceManagementReference reject blueprint creation unless it is in the create request");
        request.Body["@odata.type"]!.GetValue<string>().Should().Be("Microsoft.Graph.AgentIdentityBlueprint",
            because: "adding serviceManagementReference must not change the type of the application being created");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateBlueprintApplicationAsync_WithoutReference_OmitsServiceManagementReference(string? reference)
    {
        using var handler = new RecordingGraphHandler().Respond(HttpStatusCode.Created, CreatedApplicationJson);

        await CreateAsync(handler, SponsorUserId, new BlueprintCreationOptions(ServiceManagementReference: reference));

        handler.Requests.Should().ContainSingle().Which.Body.ContainsKey("serviceManagementReference").Should().BeFalse(
            because: "tenants without the requirement must receive the same create payload as before the option existed");
    }

    [Fact]
    public async Task CreateBlueprintApplicationAsync_WithoutOptions_OmitsServiceManagementReference()
    {
        using var handler = new RecordingGraphHandler().Respond(HttpStatusCode.Created, CreatedApplicationJson);

        await CreateAsync(handler, SponsorUserId, options: null);

        handler.Requests.Should().ContainSingle().Which.Body.ContainsKey("serviceManagementReference").Should().BeFalse(
            because: "callers that pass no creation options must keep the original create payload");
    }

    [Fact]
    public async Task CreateBlueprintApplicationAsync_MissingReference_FailsFastWithoutSponsorOrOwnerRetries()
    {
        using var handler = new RecordingGraphHandler().Respond(HttpStatusCode.BadRequest, ReferenceMissingJson);
        var logger = Substitute.For<ILogger>();

        var (application, failure) = await CreateAsync(handler, SponsorUserId, options: null, logger);

        application.Should().BeNull();
        handler.Requests.Should().ContainSingle(
            because: "dropping sponsors or owners cannot supply a missing serviceManagementReference, so retrying only repeats the same failure");
        failure.Should().NotBeNull();
        failure!.Kind.Should().Be(BlueprintCreationFailureKind.ServiceManagementReference);
        failure.HttpStatusCode.Should().Be(400);
        failure.GraphErrorCode.Should().Be("Request_BadRequest");
        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Retrying without")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CreateBlueprintApplicationAsync_ReferenceErrorOnSponsorRetry_StopsBeforeOwnerRetry()
    {
        using var handler = new RecordingGraphHandler()
            .Respond(HttpStatusCode.BadRequest, SponsorBadRequestJson)
            .Respond(HttpStatusCode.BadRequest, ReferenceMissingJson);

        var (_, failure) = await CreateAsync(handler, SponsorUserId, options: null);

        handler.Requests.Should().HaveCount(2,
            because: "once Graph reports a serviceManagementReference error, dropping owners cannot fix it and would only cost ownership");
        failure!.Kind.Should().Be(BlueprintCreationFailureKind.ServiceManagementReference);
    }

    [Fact]
    public async Task CreateBlueprintApplicationAsync_SponsorRelatedBadRequest_RetriesWithoutSponsorsThenOwners()
    {
        using var handler = new RecordingGraphHandler()
            .Respond(HttpStatusCode.BadRequest, SponsorBadRequestJson)
            .Respond(HttpStatusCode.BadRequest, SponsorBadRequestJson)
            .Respond(HttpStatusCode.Created, CreatedApplicationJson);

        var (application, failure) = await CreateAsync(handler, SponsorUserId, new BlueprintCreationOptions(ServiceManagementReference: ReferenceId));

        failure.Should().BeNull();
        application.Should().NotBeNull();
        handler.Requests.Should().HaveCount(3,
            because: "a 400 unrelated to serviceManagementReference keeps the existing sponsors-then-owners fallback");
        handler.Requests[0].Body.ContainsKey("sponsors@odata.bind").Should().BeTrue();
        handler.Requests[1].Body.ContainsKey("sponsors@odata.bind").Should().BeFalse(
            because: "the first retry drops only the sponsors field");
        handler.Requests[1].Body.ContainsKey("owners@odata.bind").Should().BeTrue(
            because: "owners are kept until the last retry so ownership survives a sponsor-only problem");
        handler.Requests[2].Body.ContainsKey("owners@odata.bind").Should().BeFalse(
            because: "the last retry drops owners as well");
        handler.Requests.Should().OnlyContain(r => r.Body["serviceManagementReference"]!.GetValue<string>() == ReferenceId,
            because: "every retry must still carry serviceManagementReference, or the retry fails in tenants that require it");
    }

    [Fact]
    public async Task CreateBlueprintApplicationAsync_Forbidden_ReturnsPermissionDeniedWithoutRetry()
    {
        using var handler = new RecordingGraphHandler().Respond(HttpStatusCode.Forbidden, AuthorizationDeniedJson);

        var (_, failure) = await CreateAsync(handler, SponsorUserId, new BlueprintCreationOptions(ServiceManagementReference: ReferenceId));

        handler.Requests.Should().ContainSingle(
            because: "the sponsors/owners fallback only applies to 400 Bad Request");
        failure!.Kind.Should().Be(BlueprintCreationFailureKind.PermissionDenied);
        failure.HttpStatusCode.Should().Be(403);
        failure.GraphErrorCode.Should().Be("Authorization_RequestDenied");
    }

    [Fact]
    public async Task CreateBlueprintApplicationAsync_BadRequestWithoutSponsor_ReturnsNonPermissionFailure()
    {
        using var handler = new RecordingGraphHandler().Respond(HttpStatusCode.BadRequest, SponsorBadRequestJson);

        var (_, failure) = await CreateAsync(handler, sponsorUserId: null, options: null);

        handler.Requests.Should().ContainSingle(
            because: "without sponsors or owners in the payload there is nothing to drop and retry");
        failure!.Kind.Should().Be(BlueprintCreationFailureKind.Other,
            because: "a generic 400 is not an authorization failure and must not be reported as one");
    }

    private static async Task<(JsonObject? Application, BlueprintCreationFailure? Failure)> CreateAsync(
        RecordingGraphHandler handler,
        string? sponsorUserId,
        BlueprintCreationOptions? options,
        ILogger? logger = null)
    {
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        return await BlueprintSubcommand.CreateBlueprintApplicationAsync(
            httpClient, GraphBaseUrl, DisplayName, sponsorUserId, options, logger ?? NullLogger.Instance, CancellationToken.None);
    }

    /// <summary>
    /// Records each request body as JSON and replies with queued responses in order.
    /// </summary>
    internal sealed class RecordingGraphHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        public List<(HttpMethod Method, string? Uri, JsonObject Body)> Requests { get; } = new();

        public RecordingGraphHandler Respond(HttpStatusCode status, string body)
        {
            _responses.Enqueue((status, body));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri?.ToString(), JsonNode.Parse(body)!.AsObject()));

            var (status, responseBody) = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.InternalServerError, """{"error":{"code":"UnexpectedRequest","message":"No response queued."}}""");
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }
}
