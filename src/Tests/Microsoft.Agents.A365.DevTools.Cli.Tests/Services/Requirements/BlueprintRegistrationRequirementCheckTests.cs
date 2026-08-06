// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Agents.A365.DevTools.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Requirements;

public class BlueprintRegistrationRequirementCheckTests
{
    private readonly GraphApiService _mockGraphApiService;
    private readonly ILogger _logger = NullLoggerFactory.Instance.CreateLogger("test");

    private const string TestTenantId = "00000000-0000-0000-0000-000000000001";
    private const string TestBlueprintId = "00000000-0000-0000-0000-000000000002";
    private const string TestServicePrincipalId = "00000000-0000-0000-0000-000000000003";
    private const string TestRegistrationId = "reg-12345";

    public BlueprintRegistrationRequirementCheckTests()
    {
        _mockGraphApiService = Substitute.ForPartsOf<GraphApiService>();
    }

    // --- Metadata ---

    [Fact]
    public void Name_ReturnsBlueprintRegistration()
    {
        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        check.Name.Should().Be("Blueprint Registration");
    }

    [Fact]
    public void Category_ReturnsRegistration()
    {
        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        check.Category.Should().Be("Registration");
    }

    // --- Missing config ---

    [Fact]
    public async Task CheckAsync_NoBlueprintId_ReturnsFail()
    {
        var config = new Agent365Config { TenantId = TestTenantId };
        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "blueprintId is required");
        result.ErrorMessage.Should().Contain("blueprint ID not found",
            because: "error should indicate missing blueprint ID");
    }

    [Fact]
    public async Task CheckAsync_NoTenantId_ReturnsFail()
    {
        var config = new Agent365Config { AgentBlueprintId = TestBlueprintId };
        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);

        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "tenantId is required");
        result.ErrorMessage.Should().Contain("Tenant ID not found",
            because: "error should indicate missing tenant ID");
    }

    // --- Application check ---

    [Fact]
    public async Task CheckAsync_AppNotFound_ReturnsFail()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        _mockGraphApiService.ApplicationExistsByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>())
            .Returns(false);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "application does not exist in Entra");
        result.ErrorMessage.Should().Contain("not found in Entra ID",
            because: "error should indicate app not found");
    }

    [Fact]
    public async Task CheckAsync_AppCheckThrows_ReturnsWarning()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        _mockGraphApiService.ApplicationExistsByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(because: "auth/network errors are warnings, not failures");
        result.IsWarning.Should().BeTrue(because: "Graph API errors should produce a warning");
    }

    // --- Service principal check ---

    [Fact]
    public async Task CheckAsync_AppExistsButNoServicePrincipal_ReturnsFail()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        _mockGraphApiService.ApplicationExistsByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>())
            .Returns(true);
        _mockGraphApiService.LookupServicePrincipalByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns((string?)null);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "service principal must exist");
        result.ErrorMessage.Should().Contain("Service principal not found",
            because: "error should indicate missing service principal");
    }

    [Fact]
    public async Task CheckAsync_ServicePrincipalCheckThrows_ReturnsWarning()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        _mockGraphApiService.ApplicationExistsByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>())
            .Returns(true);
        _mockGraphApiService.LookupServicePrincipalByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .ThrowsAsync(new HttpRequestException("Token expired"));

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(because: "network errors on service principal check are warnings");
        result.IsWarning.Should().BeTrue();
    }

    // --- Full success without registration ---

    [Fact]
    public async Task CheckAsync_AppAndServicePrincipalExist_NoRegistrationId_ReturnsSuccess()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        _mockGraphApiService.ApplicationExistsByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>())
            .Returns(true);
        _mockGraphApiService.LookupServicePrincipalByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(TestServicePrincipalId);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(because: "app and service principal exist");
        result.IsWarning.Should().BeFalse();
        result.Details.Should().Contain(TestBlueprintId,
            because: "details should mention the blueprint ID");
    }

    // --- Agent registration checks ---

    [Fact]
    public async Task CheckAsync_RegistrationExists_ReturnsSuccess()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId,
            AgentRegistrationId = TestRegistrationId
        };

        _mockGraphApiService.ApplicationExistsByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>())
            .Returns(true);
        _mockGraphApiService.LookupServicePrincipalByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(TestServicePrincipalId);
        _mockGraphApiService.AgentRegistrationExistsAsync(TestTenantId, TestRegistrationId, Arg.Any<CancellationToken>())
            .Returns(true);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(because: "all registration checks passed");
        result.IsWarning.Should().BeFalse();
        result.Details.Should().Contain(TestRegistrationId,
            because: "details should mention the registration ID");
    }

    [Fact]
    public async Task CheckAsync_RegistrationNotFound_ReturnsFail()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId,
            AgentRegistrationId = TestRegistrationId
        };

        _mockGraphApiService.ApplicationExistsByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>())
            .Returns(true);
        _mockGraphApiService.LookupServicePrincipalByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(TestServicePrincipalId);
        _mockGraphApiService.AgentRegistrationExistsAsync(TestTenantId, TestRegistrationId, Arg.Any<CancellationToken>())
            .Returns(false);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "agent registration does not exist");
        result.ErrorMessage.Should().Contain("not found",
            because: "error should indicate registration not found");
    }

    [Fact]
    public async Task CheckAsync_RegistrationUnknown_ReturnsWarning()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId,
            AgentRegistrationId = TestRegistrationId
        };

        _mockGraphApiService.ApplicationExistsByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>())
            .Returns(true);
        _mockGraphApiService.LookupServicePrincipalByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(TestServicePrincipalId);
        _mockGraphApiService.AgentRegistrationExistsAsync(TestTenantId, TestRegistrationId, Arg.Any<CancellationToken>())
            .Returns((bool?)null);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(because: "unknown registration status is a warning, not a failure");
        result.IsWarning.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_RegistrationCheckThrows_ReturnsWarning()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId,
            AgentRegistrationId = TestRegistrationId
        };

        _mockGraphApiService.ApplicationExistsByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>())
            .Returns(true);
        _mockGraphApiService.LookupServicePrincipalByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(TestServicePrincipalId);
        _mockGraphApiService.AgentRegistrationExistsAsync(TestTenantId, TestRegistrationId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Forbidden"));

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(because: "registration check errors are warnings");
        result.IsWarning.Should().BeTrue();
    }

    // --- Constructor validation ---

    [Fact]
    public void Constructor_NullGraphApiService_Throws()
    {
        var act = () => new BlueprintRegistrationRequirementCheck(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // --- Inheritable permissions checks ---

    private AgentBlueprintService CreateMockBlueprintService()
    {
        var bpLogger = NullLoggerFactory.Instance.CreateLogger<AgentBlueprintService>();
        return Substitute.ForPartsOf<AgentBlueprintService>(bpLogger, _mockGraphApiService);
    }

    private void SetupAppAndSpExist()
    {
        _mockGraphApiService.ApplicationExistsByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>())
            .Returns(true);
        _mockGraphApiService.LookupServicePrincipalByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns(TestServicePrincipalId);
    }

    [Fact]
    public async Task CheckAsync_AllBaselinePermissionsPresent_ReturnsSuccess()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        SetupAppAndSpExist();
        var mockBpService = CreateMockBlueprintService();
        SetupAllBaselinePermissionsPresent(mockBpService);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService, mockBpService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(because: "all baseline scopes are present in Entra");
        result.IsWarning.Should().BeFalse();
        result.Details.Should().Contain("Permissions verified",
            because: "details should confirm permissions were verified");
    }

    [Fact]
    public async Task CheckAsync_MissingGraphScopes_ReturnsFail()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        SetupAppAndSpExist();
        var mockBpService = CreateMockBlueprintService();

        // All resources have inheritable permissions configured
        mockBpService.ListInheritablePermissionsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(BlueprintRegistrationRequirementCheck.BaselinePermissions.Select(b =>
                (b.ResourceAppId, true, true)).ToList());

        // Return all baseline resources but Graph only has User.Read.All (missing other scopes)
        var grants = BuildBaselineGrants();
        grants[AuthenticationConstants.MicrosoftGraphResourceAppId] = (new[] { "User.Read.All" }, Array.Empty<string>());
        mockBpService.GetBlueprintSpGrantsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(grants);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService, mockBpService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "missing scopes should fail the tier");
        result.ErrorMessage.Should().Contain("gaps detected",
            because: "error should describe permission gaps");
        result.Details.Should().Contain("Mail.ReadWrite",
            because: "details should list one of the missing Graph scopes");
    }

    [Fact]
    public async Task CheckAsync_ResourceNotInEntra_ReturnsFail()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        SetupAppAndSpExist();
        var mockBpService = CreateMockBlueprintService();
        // Return empty — no resources configured at all
        mockBpService.ListInheritablePermissionsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<(string ResourceAppId, bool ScopesAllAllowed, bool RolesAllAllowed)>());
        mockBpService.GetBlueprintSpGrantsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, (string[] DelegatedScopes, string[] AppRoleNames)>(StringComparer.OrdinalIgnoreCase));

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService, mockBpService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "missing resource permissions should fail the tier");
        result.Details.Should().Contain("no inheritable permissions configured in Entra",
            because: "details should indicate resources are missing");
    }

    [Fact]
    public async Task CheckAsync_PermissionsCheckThrows_ReturnsWarning()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        SetupAppAndSpExist();
        var mockBpService = CreateMockBlueprintService();
        mockBpService.ListInheritablePermissionsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Forbidden"));

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService, mockBpService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(because: "permissions query errors are warnings");
        result.IsWarning.Should().BeTrue();
        result.Details.Should().Contain("Permissions query failed",
            because: "warning should indicate what went wrong");
    }

    [Fact]
    public async Task CheckAsync_NoBlueprintService_SkipsPermissionsCheck()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        SetupAppAndSpExist();

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService, blueprintService: null);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeTrue(because: "without blueprint service, permissions check is skipped");
        result.IsWarning.Should().BeFalse(because: "skipping permissions check is not a warning");
    }

    // --- Metadata population ---

    [Fact]
    public async Task CheckAsync_AppNotFound_MetadataHasAppExistsFalse()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        _mockGraphApiService.ApplicationExistsByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>())
            .Returns(false);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        var result = await check.CheckAsync(config, _logger);

        result.Metadata.Should().NotBeNull(because: "metadata should be set on failure results");
        var meta = (RequirementCheckMetadata)result.Metadata!;
        meta.AppExists.Should().BeFalse(because: "app does not exist in Entra");
    }

    [Fact]
    public async Task CheckAsync_NoServicePrincipal_MetadataHasAppTrueSpFalse()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        _mockGraphApiService.ApplicationExistsByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>())
            .Returns(true);
        _mockGraphApiService.LookupServicePrincipalByAppIdAsync(TestTenantId, TestBlueprintId, Arg.Any<CancellationToken>(), Arg.Any<IEnumerable<string>?>())
            .Returns((string?)null);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        var result = await check.CheckAsync(config, _logger);

        result.Metadata.Should().NotBeNull();
        var meta = (RequirementCheckMetadata)result.Metadata!;
        meta.AppExists.Should().BeTrue(because: "app exists");
        meta.ServicePrincipalExists.Should().BeFalse(because: "SP does not exist");
    }

    [Fact]
    public async Task CheckAsync_RegistrationNotFound_MetadataHasRegistrationFalse()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId,
            AgentRegistrationId = TestRegistrationId
        };

        SetupAppAndSpExist();
        _mockGraphApiService.AgentRegistrationExistsAsync(TestTenantId, TestRegistrationId, Arg.Any<CancellationToken>())
            .Returns(false);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        var result = await check.CheckAsync(config, _logger);

        result.Metadata.Should().NotBeNull();
        var meta = (RequirementCheckMetadata)result.Metadata!;
        meta.AppExists.Should().BeTrue();
        meta.ServicePrincipalExists.Should().BeTrue();
        meta.RegistrationExists.Should().BeFalse(because: "registration was not found");
    }

    [Fact]
    public async Task CheckAsync_Success_MetadataHasAllTrue()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        SetupAppAndSpExist();

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService);
        var result = await check.CheckAsync(config, _logger);

        result.Metadata.Should().NotBeNull(because: "metadata should be set on success results");
        var meta = (RequirementCheckMetadata)result.Metadata!;
        meta.AppExists.Should().BeTrue();
        meta.ServicePrincipalExists.Should().BeTrue();
        meta.RegistrationExists.Should().BeNull(
            because: "no registration ID was configured, so registration check was skipped");
    }

    [Fact]
    public async Task CheckAsync_WithPermissions_MetadataHasResourceDetails()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        SetupAppAndSpExist();
        var mockBpService = CreateMockBlueprintService();
        SetupAllBaselinePermissionsPresent(mockBpService);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService, mockBpService);
        var result = await check.CheckAsync(config, _logger);

        result.Metadata.Should().NotBeNull();
        var meta = (RequirementCheckMetadata)result.Metadata!;
        meta.ResourcePermissions.Should().NotBeNull();

        var graphResource = meta.ResourcePermissions!
            .FirstOrDefault(r => r.ResourceAppId == AuthenticationConstants.MicrosoftGraphResourceAppId);
        graphResource.Should().NotBeNull(because: "Microsoft Graph is a baseline resource");
        graphResource!.ResourceName.Should().Be("Microsoft Graph");
        graphResource.MissingScopes.Should().BeEmpty(because: "all expected scopes are present");
        graphResource.InheritablePermissionsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_MissingScopes_MetadataHasMissingScopesListed()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        SetupAppAndSpExist();
        var mockBpService = CreateMockBlueprintService();

        // All resources have inheritable permissions configured
        mockBpService.ListInheritablePermissionsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(BlueprintRegistrationRequirementCheck.BaselinePermissions.Select(b =>
                (b.ResourceAppId, true, true)).ToList());

        // Return Graph with only User.Read.All — missing other baseline scopes
        var grants = BuildBaselineGrants();
        grants[AuthenticationConstants.MicrosoftGraphResourceAppId] = (new[] { "User.Read.All" }, Array.Empty<string>());
        mockBpService.GetBlueprintSpGrantsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(grants);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService, mockBpService);
        var result = await check.CheckAsync(config, _logger);

        result.Metadata.Should().NotBeNull();
        var meta = (RequirementCheckMetadata)result.Metadata!;
        var graphResource = meta.ResourcePermissions!
            .First(r => r.ResourceAppId == AuthenticationConstants.MicrosoftGraphResourceAppId);
        graphResource.MissingScopes.Should().Contain("Mail.ReadWrite",
            because: "Mail.ReadWrite is a baseline Graph scope not returned by Entra");
        graphResource.InheritablePermissionsConfigured.Should().BeTrue(
            because: "the resource exists in Entra, just missing some scopes");
    }

    [Fact]
    public async Task CheckAsync_ResourceNotInEntra_MetadataShowsNotConfigured()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        SetupAppAndSpExist();
        var mockBpService = CreateMockBlueprintService();
        // Return no resources at all
        mockBpService.ListInheritablePermissionsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<(string ResourceAppId, bool ScopesAllAllowed, bool RolesAllAllowed)>());
        mockBpService.GetBlueprintSpGrantsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, (string[] DelegatedScopes, string[] AppRoleNames)>(StringComparer.OrdinalIgnoreCase));

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService, mockBpService);
        var result = await check.CheckAsync(config, _logger);

        result.Metadata.Should().NotBeNull();
        var meta = (RequirementCheckMetadata)result.Metadata!;
        meta.ResourcePermissions.Should().NotBeNull()
            .And.HaveCountGreaterOrEqualTo(BlueprintRegistrationRequirementCheck.BaselinePermissions.Count,
                because: "all baseline resources should appear in metadata even when missing from Entra");

        var graphResource = meta.ResourcePermissions!
            .First(r => r.ResourceAppId == AuthenticationConstants.MicrosoftGraphResourceAppId);
        graphResource.InheritablePermissionsConfigured.Should().BeFalse(
            because: "the resource was not found in Entra at all");
        graphResource.MissingScopes.Should().Contain("Mail.ReadWrite",
            because: "all expected scopes are missing when resource is not configured");
    }

    [Fact]
    public async Task CheckAsync_RolesNotAllAllowed_ReturnsFail()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        SetupAppAndSpExist();
        var mockBpService = CreateMockBlueprintService();
        // Scopes allAllowed but roles NOT allAllowed
        mockBpService.ListInheritablePermissionsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(BlueprintRegistrationRequirementCheck.BaselinePermissions.Select(b =>
                (b.ResourceAppId, true, false)).ToList());
        mockBpService.GetBlueprintSpGrantsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(BuildBaselineGrants());

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService, mockBpService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "roles not allAllowed should fail the tier");
        result.Details.Should().Contain("roles",
            because: "details should indicate roles are not allAllowed");
    }

    [Fact]
    public async Task CheckAsync_AllAllowedButNoGrants_ReturnsFail()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        SetupAppAndSpExist();
        var mockBpService = CreateMockBlueprintService();
        // Both allAllowed
        mockBpService.ListInheritablePermissionsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(BlueprintRegistrationRequirementCheck.BaselinePermissions.Select(b =>
                (b.ResourceAppId, true, true)).ToList());
        // But no grants on the blueprint SP
        var emptyGrants = new Dictionary<string, (string[] DelegatedScopes, string[] AppRoleNames)>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseline in BlueprintRegistrationRequirementCheck.BaselinePermissions)
        {
            emptyGrants[baseline.ResourceAppId] = (Array.Empty<string>(), Array.Empty<string>());
        }
        mockBpService.GetBlueprintSpGrantsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(emptyGrants);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService, mockBpService);
        var result = await check.CheckAsync(config, _logger);

        result.Passed.Should().BeFalse(because: "no grants means nothing to inherit");
        result.Details.Should().Contain("nothing to inherit",
            because: "details should explain inheritance has nothing to inherit");
    }

    [Fact]
    public async Task CheckAsync_EffectiveInheritance_MetadataReflectsStatus()
    {
        var config = new Agent365Config
        {
            TenantId = TestTenantId,
            AgentBlueprintId = TestBlueprintId
        };

        SetupAppAndSpExist();
        var mockBpService = CreateMockBlueprintService();
        SetupAllBaselinePermissionsPresent(mockBpService);

        var check = new BlueprintRegistrationRequirementCheck(_mockGraphApiService, mockBpService);
        var result = await check.CheckAsync(config, _logger);

        result.Metadata.Should().NotBeNull();
        var meta = (RequirementCheckMetadata)result.Metadata!;
        var graphResource = meta.ResourcePermissions!
            .First(r => r.ResourceAppId == AuthenticationConstants.MicrosoftGraphResourceAppId);
        graphResource.ScopesAllAllowed.Should().BeTrue(
            because: "scopes kind=allAllowed was configured");
        graphResource.RolesAllAllowed.Should().BeTrue(
            because: "roles kind=allAllowed was configured");
        graphResource.EffectiveInheritance.Should().BeTrue(
            because: "kind=allAllowed on both sides AND grants exist");
    }

    /// <summary>
    /// Builds a grants dictionary with all baseline scopes present as delegated scopes.
    /// </summary>
    private static Dictionary<string, (string[] DelegatedScopes, string[] AppRoleNames)> BuildBaselineGrants()
    {
        var grants = new Dictionary<string, (string[] DelegatedScopes, string[] AppRoleNames)>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseline in BlueprintRegistrationRequirementCheck.BaselinePermissions)
        {
            grants[baseline.ResourceAppId] = (baseline.Scopes.ToArray(), Array.Empty<string>());
        }
        return grants;
    }

    /// <summary>
    /// Sets up mocks so all baseline resources have inheritable permissions configured and all scopes are granted.
    /// </summary>
    private void SetupAllBaselinePermissionsPresent(AgentBlueprintService mockBpService)
    {
        mockBpService.ListInheritablePermissionsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(BlueprintRegistrationRequirementCheck.BaselinePermissions.Select(b =>
                (b.ResourceAppId, true, true)).ToList());
        mockBpService.GetBlueprintSpGrantsAsync(TestTenantId, TestBlueprintId, Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(BuildBaselineGrants());
    }
}
