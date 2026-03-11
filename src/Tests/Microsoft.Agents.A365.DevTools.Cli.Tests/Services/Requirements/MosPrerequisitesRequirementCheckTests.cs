// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Requirements;

/// <summary>
/// Unit tests for MosPrerequisitesRequirementCheck
/// </summary>
public class MosPrerequisitesRequirementCheckTests
{
    private readonly GraphApiService _mockGraphApiService;
    private readonly AgentBlueprintService _mockBlueprintService;
    private readonly ILogger _mockLogger;

    public MosPrerequisitesRequirementCheckTests()
    {
        var mockExecutor = Substitute.ForPartsOf<CommandExecutor>(NullLogger<CommandExecutor>.Instance);
        _mockGraphApiService = Substitute.For<GraphApiService>(NullLogger<GraphApiService>.Instance, mockExecutor);
        _mockBlueprintService = Substitute.ForPartsOf<AgentBlueprintService>(NullLogger<AgentBlueprintService>.Instance, _mockGraphApiService);
        _mockLogger = Substitute.For<ILogger>();
    }

    [Fact]
    public async Task CheckAsync_WhenClientAppIdMissing_ShouldReturnFailure()
    {
        // Arrange — missing ClientAppId causes EnsureMosPrerequisitesAsync to throw SetupValidationException
        var check = new MosPrerequisitesRequirementCheck(_mockGraphApiService, _mockBlueprintService);
        var config = new Agent365Config { TenantId = "test-tenant" }; // no ClientAppId

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert
        result.Should().NotBeNull();
        result.Passed.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CheckAsync_WhenSetupValidationExceptionHasMitigationSteps_ShouldIncludeThemInResolution()
    {
        // Arrange — mock GraphGetAsync to throw SetupValidationException with explicit mitigation steps
        var mitigationStep = "Grant admin consent via https://entra.microsoft.com";
        var check = new MosPrerequisitesRequirementCheck(_mockGraphApiService, _mockBlueprintService);
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            ClientAppId = "00000000-0000-0000-0000-000000000001"
        };

        _mockGraphApiService.GraphGetAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(Task.FromException<System.Text.Json.JsonDocument?>(new SetupValidationException(
                issueDescription: "MOS service principal not found",
                mitigationSteps: [mitigationStep])));

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert — mitigation steps from the exception must appear in ResolutionGuidance
        result.Passed.Should().BeFalse();
        result.ResolutionGuidance.Should().Contain(mitigationStep);
    }

    [Fact]
    public async Task CheckAsync_WhenSetupValidationExceptionHasNoMitigationSteps_ShouldUseFallbackResolution()
    {
        // Arrange — GraphGetAsync returns null for the app lookup, causing SetupValidationException
        // with no mitigation steps (the default exception message is used)
        var check = new MosPrerequisitesRequirementCheck(_mockGraphApiService, _mockBlueprintService);
        var config = new Agent365Config
        {
            TenantId = "test-tenant",
            ClientAppId = "00000000-0000-0000-0000-000000000001"
        };

        _mockGraphApiService.GraphGetAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns((System.Text.Json.JsonDocument?)null);

        // Act
        var result = await check.CheckAsync(config, _mockLogger);

        // Assert — app not found throws SetupValidationException, check maps it to Failure with fallback guidance
        result.Passed.Should().BeFalse();
        result.ResolutionGuidance.Should().Contain("a365 setup all");
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectName()
    {
        var check = new MosPrerequisitesRequirementCheck(_mockGraphApiService, _mockBlueprintService);
        check.Name.Should().Be("MOS Prerequisites");
    }

    [Fact]
    public void Metadata_ShouldHaveCorrectCategory()
    {
        var check = new MosPrerequisitesRequirementCheck(_mockGraphApiService, _mockBlueprintService);
        check.Category.Should().Be("MOS");
    }

    [Fact]
    public void Constructor_WithNullGraphApiService_ShouldThrowArgumentNullException()
    {
        var act = () => new MosPrerequisitesRequirementCheck(null!, _mockBlueprintService);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("graphApiService");
    }

    [Fact]
    public void Constructor_WithNullBlueprintService_ShouldThrowArgumentNullException()
    {
        var act = () => new MosPrerequisitesRequirementCheck(_mockGraphApiService, null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("blueprintService");
    }
}
