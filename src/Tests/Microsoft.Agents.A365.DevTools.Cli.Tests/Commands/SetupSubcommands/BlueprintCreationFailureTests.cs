// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands.SetupSubcommands;

/// <summary>
/// Tests that blueprint creation failures are classified by their real cause and reported with
/// accurate remediation, rather than always as a missing Graph permission.
/// </summary>
public class BlueprintCreationFailureTests
{
    private const string ReferenceId = "6f0e5d8a-3b1c-4c2d-9e7f-1a2b3c4d5e6f";

    [Theory]
    [InlineData(400, "Request_BadRequest", "ServiceManagementReference field is required for Create, but is missing in the request.", nameof(BlueprintCreationFailureKind.ServiceManagementReference))]
    [InlineData(400, "Request_BadRequest", "Value for ServiceManagementReference must be a valid GUID.", nameof(BlueprintCreationFailureKind.ServiceManagementReference))]
    [InlineData(400, "Request_BadRequest", "Invalid value specified for property 'serviceManagementReference' of resource 'Application'.", nameof(BlueprintCreationFailureKind.ServiceManagementReference))]
    [InlineData(400, "SomeFutureErrorCode", "A null value was provided for ServiceManagementReference.", nameof(BlueprintCreationFailureKind.ServiceManagementReference))]
    [InlineData(403, "Authorization_RequestDenied", "Insufficient privileges to complete the operation.", nameof(BlueprintCreationFailureKind.PermissionDenied))]
    [InlineData(400, "Authorization_RequestDenied", "Insufficient privileges to complete the operation.", nameof(BlueprintCreationFailureKind.PermissionDenied))]
    [InlineData(401, "InvalidAuthenticationToken", "Access token has expired or is not yet valid.", nameof(BlueprintCreationFailureKind.PermissionDenied))]
    [InlineData(400, "Request_BadRequest", "Invalid value specified for property 'sponsors' of resource 'Application'.", nameof(BlueprintCreationFailureKind.Other))]
    [InlineData(500, "UnknownError", "Failed to process serviceManagementReference.", nameof(BlueprintCreationFailureKind.Other))]
    [InlineData(500, "UnknownError", "The service is temporarily unavailable.", nameof(BlueprintCreationFailureKind.Other))]
    public void Classify_MapsGraphErrorsToFailureKinds(int statusCode, string graphErrorCode, string message, string expectedKind)
    {
        BlueprintCreationFailure.Classify(statusCode, graphErrorCode, message).ToString().Should().Be(expectedKind,
            because: "only a 400 that names serviceManagementReference needs the --service-management-reference remedy, only 401/403 and Authorization_RequestDenied are authorization failures, and everything else must not be reported as a permission problem");
    }

    [Fact]
    public void FromGraphResponse_ParsesStatusCodeAndGraphError()
    {
        var failure = BlueprintCreationFailure.FromGraphResponse(
            HttpStatusCode.BadRequest, BlueprintApplicationCreateTests.ReferenceMissingJson);

        failure.Kind.Should().Be(BlueprintCreationFailureKind.ServiceManagementReference);
        failure.HttpStatusCode.Should().Be(400);
        failure.GraphErrorCode.Should().Be("Request_BadRequest");
        failure.Message.Should().StartWith("ServiceManagementReference field is required for Create",
            because: "the Graph error message, not the raw JSON envelope, is what the user should read");
    }

    [Fact]
    public void FromGraphResponse_NonJsonBody_KeepsTruncatedRawBody()
    {
        var htmlBody = "<html>" + new string('x', 1000) + "</html>";

        var failure = BlueprintCreationFailure.FromGraphResponse(HttpStatusCode.BadGateway, htmlBody);

        failure.Kind.Should().Be(BlueprintCreationFailureKind.Other);
        failure.GraphErrorCode.Should().BeNull();
        failure.Message.Should().StartWith("<html>");
        failure.Message.Length.Should().BeLessThan(htmlBody.Length,
            because: "a proxy error page must not be dumped into the error output in full");
    }

    [Fact]
    public void CreateException_MissingReference_ReturnsRequiredErrorWithActionableGuidance()
    {
        var failure = BlueprintCreationFailure.FromGraphResponse(
            HttpStatusCode.BadRequest, BlueprintApplicationCreateTests.ReferenceMissingJson);

        var exception = BlueprintCreationFailure.CreateException(failure, serviceManagementReference: null);

        exception.ErrorCode.Should().Be(ErrorCodes.ServiceManagementReferenceRequired,
            because: "a missing serviceManagementReference is a configuration gap, not a Graph permission failure");
        exception.ErrorDetails.Should().Contain(d => d.Contains("400") && d.Contains("ServiceManagementReference field is required"),
            because: "the Graph status and message are kept so the failure stays diagnosable");
        exception.MitigationSteps.Should().Contain(s => s.Contains("--service-management-reference"),
            because: "the user must be told which option supplies the missing value");
        exception.MitigationSteps.Should().Contain(s => s.Contains("\"serviceManagementReference\"") && s.Contains("a365.config.json"),
            because: "the config key is the alternative to passing the option on every run");
        exception.MitigationSteps.Should().Contain("Troubleshooting: https://aka.ms/service-management-reference-error",
            because: "the troubleshooting link from the Graph error is surfaced without its surrounding backticks");
        exception.GetFormattedMessage().Should().NotContain("AgentIdentityBlueprint.ReadWrite.All",
            because: "pointing at a Graph permission sends the user to fix the wrong thing");
    }

    [Fact]
    public void CreateException_MissingReferenceWithoutLink_OmitsTroubleshootingStep()
    {
        var failure = new BlueprintCreationFailure(
            BlueprintCreationFailureKind.ServiceManagementReference,
            "ServiceManagementReference field is required for Create, but is missing in the request.",
            400,
            "Request_BadRequest");

        var exception = BlueprintCreationFailure.CreateException(failure, serviceManagementReference: null);

        exception.ErrorCode.Should().Be(ErrorCodes.ServiceManagementReferenceRequired);
        exception.MitigationSteps.Should().NotContain(s => s.StartsWith("Troubleshooting", StringComparison.Ordinal),
            because: "the troubleshooting link comes only from the Graph error, so none is shown when Graph sends none");
    }

    [Fact]
    public void CreateException_ReferenceErrorWithValue_ReturnsRejectedErrorNamingTheValue()
    {
        var failure = new BlueprintCreationFailure(
            BlueprintCreationFailureKind.ServiceManagementReference, "Value for ServiceManagementReference is not valid.", 400, "Request_BadRequest");

        var exception = BlueprintCreationFailure.CreateException(failure, ReferenceId);

        exception.ErrorCode.Should().Be(ErrorCodes.ServiceManagementReferenceRejected,
            because: "when a value was sent, the problem is the value itself, not its absence");
        exception.ErrorDetails.Should().Contain(d => d.Contains(ReferenceId),
            because: "the user needs to see which value Graph rejected");
        exception.MitigationSteps.Should().Contain(s => s.Contains("--service-management-reference"));
    }

    [Fact]
    public void CreateException_Forbidden_GivesActiveRoleGuidanceInsteadOfGraphPermission()
    {
        var failure = BlueprintCreationFailure.FromGraphResponse(
            HttpStatusCode.Forbidden, BlueprintApplicationCreateTests.AuthorizationDeniedJson);

        var exception = BlueprintCreationFailure.CreateException(failure, ReferenceId);

        exception.ErrorCode.Should().Be(ErrorCodes.GraphPermissionDenied,
            because: "403 Authorization_RequestDenied is a genuine authorization failure");
        exception.MitigationSteps.Should().Contain(
            s => s.Contains("Agent ID Developer") && s.Contains("Agent ID Administrator") && s.Contains("Global Administrator") && s.Contains("active"),
            because: "blueprint creation is authorized by an active directory role, so the guidance must name the roles and that they must be active");
        exception.MitigationSteps.Should().NotContain(s => s.Contains("AgentIdentityBlueprint.ReadWrite.All"),
            because: "the user lacks a directory role, not a Graph API permission");
        exception.MitigationSteps.Should().NotContain(s => s.Contains("Sign in again"),
            because: "a 403 means the token was accepted, so signing in again does not help");
    }

    [Fact]
    public void CreateException_Unauthorized_AlsoSuggestsSigningInAgain()
    {
        var failure = new BlueprintCreationFailure(
            BlueprintCreationFailureKind.PermissionDenied, "Access token has expired or is not yet valid.", 401, "InvalidAuthenticationToken");

        var exception = BlueprintCreationFailure.CreateException(failure, serviceManagementReference: null);

        exception.ErrorCode.Should().Be(ErrorCodes.GraphPermissionDenied);
        exception.MitigationSteps[0].Should().StartWith("Sign in again",
            because: "a 401 is most often an expired or wrong-tenant token, which a fresh sign-in fixes");
        exception.MitigationSteps.Should().Contain(s => s.Contains("Agent ID Developer"));
    }

    [Fact]
    public void CreateException_OtherFailure_IsNotReportedAsPermissionDenied()
    {
        var failure = new BlueprintCreationFailure(
            BlueprintCreationFailureKind.Other, "Invalid value specified for property 'sponsors' of resource 'Application'.", 400, "Request_BadRequest");

        var exception = BlueprintCreationFailure.CreateException(failure, serviceManagementReference: null);

        exception.ErrorCode.Should().Be(ErrorCodes.GraphApiFailed,
            because: "reporting every failure as GRAPH_PERMISSION_DENIED misdiagnosed non-permission errors");
        exception.ErrorDetails.Should().Contain(d => d.Contains("400") && d.Contains("Request_BadRequest"),
            because: "the HTTP status and Graph error code must reach the user");
        exception.MitigationSteps.Should().NotContain(s => s.Contains("Agent ID Developer"));
        exception.MitigationSteps.Should().Contain(s => s.Contains("Graph response above"),
            because: "Graph rejected the request itself, so the user must act on the reported problem");
        exception.MitigationSteps.Should().NotContain(s => s.Contains("network connection"),
            because: "network advice does not fit a request that Graph received and rejected");
        exception.GetFormattedMessage().Should().NotContain("AgentIdentityBlueprint.ReadWrite.All");
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    public void CreateException_TransientFailure_KeepsRetryGuidance(int statusCode)
    {
        var failure = new BlueprintCreationFailure(
            BlueprintCreationFailureKind.Other, "The service is temporarily unavailable.", statusCode, "ServiceUnavailable");

        var exception = BlueprintCreationFailure.CreateException(failure, serviceManagementReference: null);

        exception.ErrorCode.Should().Be(ErrorCodes.GraphApiFailed);
        exception.MitigationSteps.Should().Contain(s => s.Contains("Try again in a few minutes"),
            because: "server errors and throttling are transient, so retrying later is the right advice");
    }

    [Fact]
    public void CreateException_WithoutFailureDetail_ReportsGenericGraphFailure()
    {
        var exception = BlueprintCreationFailure.CreateException(failure: null, serviceManagementReference: null);

        exception.ErrorCode.Should().Be(ErrorCodes.GraphApiFailed,
            because: "an unknown cause must not be presented as a permission problem");
        exception.ErrorDetails.Should().ContainSingle();
    }

    [Theory]
    [InlineData(nameof(BlueprintCreationFailureKind.ServiceManagementReference), 400, null, ErrorCodes.ServiceManagementReferenceRequired)]
    [InlineData(nameof(BlueprintCreationFailureKind.ServiceManagementReference), 400, ReferenceId, ErrorCodes.ServiceManagementReferenceRejected)]
    [InlineData(nameof(BlueprintCreationFailureKind.PermissionDenied), 403, null, ErrorCodes.GraphPermissionDenied)]
    [InlineData(nameof(BlueprintCreationFailureKind.Other), 400, null, ErrorCodes.GraphApiFailed)]
    [InlineData(null, null, null, ErrorCodes.GraphApiFailed)]
    public void CreateException_FormattedOutputIncludesErrorCode(string? kind, int? statusCode, string? reference, string expectedCode)
    {
        var failure = kind is null
            ? null
            : new BlueprintCreationFailure(Enum.Parse<BlueprintCreationFailureKind>(kind), "Graph rejected the request.", statusCode, "Request_BadRequest");

        var exception = BlueprintCreationFailure.CreateException(failure, reference);

        exception.ErrorCode.Should().Be(expectedCode);
        exception.GetFormattedMessage().Should().Contain($"Error code: {expectedCode}",
            because: "the documented error codes must appear in the printed error, which is all that standalone 'setup blueprint' shows");
    }

    [Theory]
    [InlineData("Refer to the TSG `https://aka.ms/service-management-reference-error` for resolving the error", "https://aka.ms/service-management-reference-error")]
    [InlineData("See https://example.com/troubleshooting.", "https://example.com/troubleshooting")]
    [InlineData("No link in this message.", null)]
    [InlineData(null, null)]
    public void GetTroubleshootingUrl_ReturnsLinkFromGraphMessage(string? message, string? expected)
    {
        BlueprintCreationFailure.GetTroubleshootingUrl(message).Should().Be(expected,
            because: "the troubleshooting link is taken from the Graph error with punctuation stripped, and is absent when Graph sends none");
    }
}
