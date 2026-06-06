// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Tests for <see cref="EntraAppProvisioner"/>. The provisioner absorbed the per-app creation flows
/// that previously lived inline in PublishCommandExecutor and RegisterCommandExecutor. These tests
/// pin every branch (success + each failure mode) so the executors can compose the provisioner
/// without needing their own per-step coverage.
/// </summary>
public class EntraAppProvisionerTests
{
    private const string ServerName = "mcp_TestServer";
    private const string TenantId = "00000000-0000-0000-0000-0000000000aa";
    private const string AppObjectId = "11111111-1111-1111-1111-111111111111";
    private const string AppClientId = "22222222-2222-2222-2222-222222222222";
    private const string AppSecret = "fake-secret";

    private readonly ILogger _logger;
    private readonly GraphApiService _graph;
    private readonly RetryHelper _retryHelper;
    private readonly EntraAppProvisioner _provisioner;

    public EntraAppProvisionerTests()
    {
        _logger = Substitute.For<ILogger>();
        _graph = Substitute.For<GraphApiService>();
        _retryHelper = new RetryHelper(_logger, maxRetries: 1, baseDelaySeconds: 0);
        _provisioner = new EntraAppProvisioner(_logger, _graph, _retryHelper);
    }

    [Fact]
    public async Task CreateProxyAppAsync_HappyPath_ReturnsAllFieldsPopulated()
    {
        _graph.CreateEntraAppAsync(TenantId, $"{ServerName}-A365Proxy", serviceTreeId: "svc-tree-7", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string ObjectId, string ClientId)?>((AppObjectId, AppClientId)));
        _graph.AddAppPasswordAsync(TenantId, AppObjectId).Returns(Task.FromResult<string?>(AppSecret));

        var result = await _provisioner.CreateProxyAppAsync(ServerName, TenantId, suffix: "A365Proxy", roleDisplay: "A365 Proxy", serviceTreeId: "svc-tree-7");

        result.Should().NotBeNull();
        result!.ClientId.Should().Be(AppClientId);
        result.Secret.Should().Be(AppSecret);
        result.ObjectId.Should().Be(AppObjectId);
        result.AppName.Should().Be($"{ServerName}-A365Proxy");

        await _graph.Received(1).CreateEntraAppAsync(TenantId, $"{ServerName}-A365Proxy", serviceTreeId: "svc-tree-7", Arg.Any<CancellationToken>());
        await _graph.Received(1).AddAppPasswordAsync(TenantId, AppObjectId);
    }

    [Fact]
    public async Task CreateProxyAppAsync_UsesSuffixInAppName()
    {
        _graph.CreateEntraAppAsync(TenantId, $"{ServerName}-RemoteProxy", serviceTreeId: null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string ObjectId, string ClientId)?>((AppObjectId, AppClientId)));
        _graph.AddAppPasswordAsync(TenantId, AppObjectId).Returns(Task.FromResult<string?>(AppSecret));

        var result = await _provisioner.CreateProxyAppAsync(ServerName, TenantId, suffix: "RemoteProxy", roleDisplay: "Remote Proxy", serviceTreeId: null);

        result.Should().NotBeNull();
        result!.AppName.Should().Be($"{ServerName}-RemoteProxy");
    }

    [Fact]
    public async Task CreateProxyAppAsync_WhenCreateAppReturnsNull_ReturnsNullAndLogsError()
    {
        _graph.CreateEntraAppAsync(TenantId, Arg.Any<string>(), serviceTreeId: Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string ObjectId, string ClientId)?>(null));

        var result = await _provisioner.CreateProxyAppAsync(ServerName, TenantId, suffix: "A365Proxy", roleDisplay: "A365 Proxy", serviceTreeId: null);

        result.Should().BeNull();
        await _graph.DidNotReceive().AddAppPasswordAsync(Arg.Any<string>(), Arg.Any<string>());
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Failed to create Entra application") && o.ToString()!.Contains($"{ServerName}-A365Proxy")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CreateProxyAppAsync_WhenAddPasswordReturnsNull_ReturnsNullAndLogsError()
    {
        _graph.CreateEntraAppAsync(TenantId, Arg.Any<string>(), serviceTreeId: Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string ObjectId, string ClientId)?>((AppObjectId, AppClientId)));
        _graph.AddAppPasswordAsync(TenantId, AppObjectId).Returns(Task.FromResult<string?>(null));

        var result = await _provisioner.CreateProxyAppAsync(ServerName, TenantId, suffix: "A365Proxy", roleDisplay: "A365 Proxy", serviceTreeId: null);

        result.Should().BeNull();
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Failed to create secret") && o.ToString()!.Contains($"{ServerName}-A365Proxy")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CreateProxyAppAsync_WhenAddPasswordReturnsWhitespace_ReturnsNullAndLogsError()
    {
        _graph.CreateEntraAppAsync(TenantId, Arg.Any<string>(), serviceTreeId: Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string ObjectId, string ClientId)?>((AppObjectId, AppClientId)));
        _graph.AddAppPasswordAsync(TenantId, AppObjectId).Returns(Task.FromResult<string?>("   "));

        var result = await _provisioner.CreateProxyAppAsync(ServerName, TenantId, suffix: "A365Proxy", roleDisplay: "A365 Proxy", serviceTreeId: null);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateProxyAppAsync_WhenClientIdIsEmpty_ReturnsNullAndLogsError()
    {
        _graph.CreateEntraAppAsync(TenantId, Arg.Any<string>(), serviceTreeId: Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string ObjectId, string ClientId)?>((AppObjectId, string.Empty)));
        _graph.AddAppPasswordAsync(TenantId, AppObjectId).Returns(Task.FromResult<string?>(AppSecret));

        var result = await _provisioner.CreateProxyAppAsync(ServerName, TenantId, suffix: "RemoteProxy", roleDisplay: "Remote Proxy", serviceTreeId: null);

        result.Should().BeNull();
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Remote Proxy") && o.ToString()!.Contains("empty client ID")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CreatePublicClientsAppAsync_ProdEnvironment_SetsPublicClientAndWebConsentRedirectUris()
    {
        var capturedPublicClientUris = new List<string[]>();
        var capturedWebUris = new List<string[]>();
        _graph.CreateEntraAppAsync(TenantId, $"{ServerName}-PublicClients", serviceTreeId: "svc-tree-9", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string ObjectId, string ClientId)?>((AppObjectId, AppClientId)));
        _graph.UpdateAppPublicClientRedirectUrisAsync(
                TenantId,
                AppObjectId,
                Arg.Do<string[]>(uris => capturedPublicClientUris.Add(uris)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _graph.UpdateAppRedirectUrisAsync(
                TenantId,
                AppObjectId,
                Arg.Do<string[]>(uris => capturedWebUris.Add(uris)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var warnings = new List<string>();
        var result = await _provisioner.CreatePublicClientsAppAsync(ServerName, TenantId, serviceTreeId: "svc-tree-9", warnings, environment: "prod");

        result.Should().NotBeNull();
        result.ClientId.Should().Be(AppClientId);
        result.ObjectId.Should().Be(AppObjectId);
        result.AppName.Should().Be($"{ServerName}-PublicClients");
        warnings.Should().BeEmpty();

        capturedPublicClientUris.Should().ContainSingle();
        capturedPublicClientUris[0].Should().BeEquivalentTo(
            new[]
            {
                $"ms-appx-web://Microsoft.AAD.BrokerPlugin/{AppClientId}",
                "http://localhost:8080/callback",
                "https://vscode.dev/redirect",
                "http://localhost",
            },
            opt => opt.WithStrictOrdering(),
            because: "Public Clients redirect URIs are part of the OAuth contract with VS Code / " +
                     "Copilot CLI and the Windows broker. The broker URI is required for WAM/SSO " +
                     "on Windows, the localhost callbacks support MSAL.NET and Copilot CLI flows, " +
                     "and vscode.dev/redirect supports the VS Code web client. Silently " +
                     "adding/removing/reordering entries breaks one of those flows.");

        capturedWebUris.Should().ContainSingle();
        capturedWebUris[0].Should().BeEquivalentTo(
            new[]
            {
                "https://admin.cloud.microsoft/?ref=tools/consent",
            },
            opt => opt.WithStrictOrdering());
    }

    [Theory]
    [InlineData("test")]
    [InlineData("preprod")]
    [InlineData("ppe")]
    public async Task CreatePublicClientsAppAsync_TestPreProdEnvironment_SetsTestPreProdWebConsentRedirectUris(string environment)
    {
        var capturedPublicClientUris = new List<string[]>();
        var capturedWebUris = new List<string[]>();
        _graph.CreateEntraAppAsync(TenantId, $"{ServerName}-PublicClients", serviceTreeId: null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string ObjectId, string ClientId)?>((AppObjectId, AppClientId)));
        _graph.UpdateAppPublicClientRedirectUrisAsync(
                TenantId,
                AppObjectId,
                Arg.Do<string[]>(uris => capturedPublicClientUris.Add(uris)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _graph.UpdateAppRedirectUrisAsync(
                TenantId,
                AppObjectId,
                Arg.Do<string[]>(uris => capturedWebUris.Add(uris)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var warnings = new List<string>();
        var result = await _provisioner.CreatePublicClientsAppAsync(ServerName, TenantId, serviceTreeId: null, warnings, environment: environment);

        result.Should().NotBeNull();
        result.ClientId.Should().Be(AppClientId);
        warnings.Should().BeEmpty();

        capturedPublicClientUris.Should().ContainSingle();
        capturedPublicClientUris[0].Should().BeEquivalentTo(
            new[]
            {
                $"ms-appx-web://Microsoft.AAD.BrokerPlugin/{AppClientId}",
                "http://localhost:8080/callback",
                "https://vscode.dev/redirect",
                "http://localhost",
            },
            opt => opt.WithStrictOrdering());

        capturedWebUris.Should().ContainSingle();
        capturedWebUris[0].Should().BeEquivalentTo(
            new[]
            {
                "https://sdf.admin.cloud.microsoft/?ref=tools/consent",
                "https://ignite.admin.cloud.microsoft/?ref=tools/consent",
            },
            opt => opt.WithStrictOrdering());
    }

    [Fact]
    public async Task CreatePublicClientsAppAsync_WhenCreateAppReturnsNull_ReturnsAppNameOnlyAndAppendsWarning()
    {
        _graph.CreateEntraAppAsync(TenantId, Arg.Any<string>(), serviceTreeId: Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string ObjectId, string ClientId)?>(null));

        var warnings = new List<string>();
        var result = await _provisioner.CreatePublicClientsAppAsync(ServerName, TenantId, serviceTreeId: null, warnings);

        result.ClientId.Should().BeNull();
        result.ObjectId.Should().BeNull();
        result.AppName.Should().Be($"{ServerName}-PublicClients");
        warnings.Should().ContainSingle().Which.Should().Be("Failed to create Public Clients Entra app. Continuing without it.");

        await _graph.DidNotReceive().UpdateAppPublicClientRedirectUrisAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePublicClientsAppAsync_WhenPublicClientRedirectUriUpdateReturnsFalse_AppendsWarning()
    {
        _graph.CreateEntraAppAsync(TenantId, Arg.Any<string>(), serviceTreeId: Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string ObjectId, string ClientId)?>((AppObjectId, AppClientId)));
        _graph.UpdateAppPublicClientRedirectUrisAsync(
                TenantId, AppObjectId, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        _graph.UpdateAppRedirectUrisAsync(
                TenantId, AppObjectId, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var warnings = new List<string>();
        var result = await _provisioner.CreatePublicClientsAppAsync(ServerName, TenantId, serviceTreeId: null, warnings);

        result.ClientId.Should().Be(AppClientId);
        result.ObjectId.Should().Be(AppObjectId);
        result.AppName.Should().Be($"{ServerName}-PublicClients");
        warnings.Should().ContainSingle().Which.Should().Be($"Failed to set publicClient redirect URIs on Public Clients app '{ServerName}-PublicClients' after retries.");
    }

    [Fact]
    public async Task CreatePublicClientsAppAsync_WhenWebRedirectUriUpdateReturnsFalse_AppendsWarning()
    {
        _graph.CreateEntraAppAsync(TenantId, Arg.Any<string>(), serviceTreeId: Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string ObjectId, string ClientId)?>((AppObjectId, AppClientId)));
        _graph.UpdateAppPublicClientRedirectUrisAsync(
                TenantId, AppObjectId, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _graph.UpdateAppRedirectUrisAsync(
                TenantId, AppObjectId, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var warnings = new List<string>();
        var result = await _provisioner.CreatePublicClientsAppAsync(ServerName, TenantId, serviceTreeId: null, warnings);

        result.ClientId.Should().Be(AppClientId);
        warnings.Should().ContainSingle().Which.Should().Be($"Failed to set web redirect URIs on Public Clients app '{ServerName}-PublicClients'.");
    }

    [Fact]
    public async Task CreatePublicClientsAppAsync_WhenRedirectUriUpdateThrows_ReturnsIdsAndAppendsExceptionWarning()
    {
        _graph.CreateEntraAppAsync(TenantId, Arg.Any<string>(), serviceTreeId: Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string ObjectId, string ClientId)?>((AppObjectId, AppClientId)));
        _graph.UpdateAppPublicClientRedirectUrisAsync(
                TenantId, AppObjectId, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("Graph blew up"));

        var warnings = new List<string>();
        var result = await _provisioner.CreatePublicClientsAppAsync(ServerName, TenantId, serviceTreeId: null, warnings);

        result.ClientId.Should().Be(AppClientId);
        result.ObjectId.Should().Be(AppObjectId);
        result.AppName.Should().Be($"{ServerName}-PublicClients");
        warnings.Should().ContainSingle().Which.Should().Be("Failed to set redirect URIs on Public Clients app: Graph blew up");
    }
}
