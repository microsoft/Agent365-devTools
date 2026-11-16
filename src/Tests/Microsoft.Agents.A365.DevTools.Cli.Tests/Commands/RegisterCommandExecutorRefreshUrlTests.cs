// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

[Collection("ConsoleOutput")]
public sealed class RegisterCommandExecutorRefreshUrlTests : IDisposable
{
    private const string AuthorizationUrl = "https://idp.example.test/authorize";
    private const string TokenUrl = "https://idp.example.test/token";
    private const string RefreshUrl = "https://idp.example.test/refresh";
    private const string InvalidUrlError = "--idp-refresh-url or externalOAuth.refreshUrl must be an absolute HTTPS URL without user information or a fragment.";
    private const string InvalidModeError = "--idp-refresh-url or externalOAuth.refreshUrl is supported only with --auth-type ExternalOAuth.";

    private static readonly RegisterCommandExecutor.EntraAppSet Apps = new(
        "a365-client", "test-a365-secret", "a365-object", "a365-app",
        "remote-client", "test-remote-secret", "remote-object", "remote-app",
        "public-client", "public-object", "public-app");

    private readonly ILogger _logger = Substitute.For<ILogger>();
    private readonly IAgent365ToolingService _toolingService = Substitute.For<IAgent365ToolingService>();
    private readonly string _inputFile = Path.Combine(Path.GetTempPath(), $"a365-refresh-{Guid.NewGuid():N}.json");

    [Theory]
    [InlineData("ExternalOAuth", RefreshUrl, null, RefreshUrl)]
    [InlineData("ExternalOAuth", null, RefreshUrl, RefreshUrl)]
    [InlineData("ExternalOAuth", "https://idp.example.test/override", RefreshUrl, "https://idp.example.test/override")]
    [InlineData("ExternalOAuth", TokenUrl, null, TokenUrl)]
    [InlineData("ExternalOAuth", "https://idp.example.test/refresh?audience=inventory%2Fread", null, "https://idp.example.test/refresh?audience=inventory%2Fread")]
    [InlineData("ExternalOAuth", null, "  https://idp.example.test/refresh  ", RefreshUrl)]
    [InlineData("ExternalOAuth", null, null, null)]
    [InlineData("ExternalOAuth", "", RefreshUrl, null)]
    [InlineData("ExternalOAuth", "  ", RefreshUrl, null)]
    [InlineData("ExternalOAuth", null, "", null)]
    [InlineData("ExternalOAuth", null, "  ", null)]
    [InlineData("ExternalIDP", RefreshUrl, null, RefreshUrl)]
    [InlineData("EntraOAuth", null, null, null)]
    [InlineData("EntraOAuth", "  ", null, null)]
    [InlineData("APIKey", null, null, null)]
    [InlineData("APIKey", null, "", null)]
    [InlineData("NoAuth", null, null, null)]
    [InlineData("NoAuth", "", null, null)]
    public async Task ResolveInputsAndBuildRequest_PreserveAuthenticationAndRefreshUrl(
        string authType, string? flagUrl, string? fileUrl, string? expectedUrl)
    {
        await WriteInputFileAsync(authType, fileUrl);
        var executor = new RegisterCommandExecutor(_logger, _toolingService, graphApiService: null);
        var expectedAuthType = authType == "ExternalIDP" ? "ExternalOAuth" : authType;
        var isExternal = expectedAuthType == "ExternalOAuth";
        var isEntra = expectedAuthType == "EntraOAuth";
        var isApiKey = expectedAuthType == "APIKey";
        var isNoAuth = expectedAuthType == "NoAuth";

        var resolved = await executor.ResolveInputsAsync(new RawRegisterArgs(
            ServerName: null,
            ServerUrl: null,
            AuthType: null,
            IdpAuthUrl: null,
            IdpTokenUrl: null,
            IdpRefreshUrl: flagUrl,
            IdpScopes: null,
            IdpClientId: null,
            IdpClientSecret: null,
            ApiKeyLocation: null,
            ApiKeyName: null,
            ToolsInput: null,
            InputFile: _inputFile,
            RemoteScopes: null,
            TenantId: null,
            ServiceTreeId: null,
            SecretLifetimeMonths: null,
            PublisherName: null,
            Description: null,
            DryRun: true));

        resolved.Should().BeEquivalentTo(new RegisterCommandExecutor.ResolvedInput
        {
            ServerName = "ext_Refresh",
            ServerUrl = "https://api.example.test/mcp",
            AuthType = expectedAuthType,
            IsEntra = isEntra,
            IsExternalIdp = isExternal,
            IsNoAuth = isNoAuth,
            IsApiKey = isApiKey,
            ToolList = ["get-item"],
            ToolDescriptions = new Dictionary<string, string> { ["get-item"] = "Gets an item." },
            PublisherName = "Contoso",
            Description = "Test MCP server",
            DryRun = true,
            RemoteScopes = "read",
            TenantId = "11111111-1111-1111-1111-111111111111",
            ServiceTreeId = null,
            SecretLifetimeMonths = 6,
            IdpAuthUrl = AuthorizationUrl,
            IdpTokenUrl = TokenUrl,
            IdpRefreshUrl = expectedUrl,
            IdpScopes = "read offline_access",
            IdpClientId = "external-client",
            IdpClientSecret = "test-external-secret",
            ApiKeyLocation = "Header",
            ApiKeyName = "X-Test-Key",
        }, because: "CLI inputs override file inputs without changing unrelated registration fields");

        var request = RegisterCommandExecutor.BuildRequest(resolved!, Apps);

        request.Should().BeEquivalentTo(new AddMcpServerRequest
        {
            ServerName = "ext_Refresh",
            ServerUrl = "https://api.example.test/mcp",
            ToolList = ["get-item"],
            ToolDescriptions = new Dictionary<string, string> { ["get-item"] = "Gets an item." },
            AuthType = expectedAuthType,
            AuthMetadata = new AddMcpServerAuthMetadata
            {
                ClientApp1Id = "a365-client",
                ClientApp1Secret = "test-a365-secret",
                ClientApp2Id = isEntra ? "remote-client" : isExternal ? "external-client" : null,
                ClientApp2Secret = isEntra ? "test-remote-secret" : isExternal ? "test-external-secret" : null,
            },
            ExternalIdp = isExternal ? new ExternalIdpDetails
            {
                AuthorizationUrl = AuthorizationUrl,
                TokenUrl = TokenUrl,
                RefreshUrl = expectedUrl,
                Scopes = "read offline_access",
            } : null,
            ApiKeyDetails = isApiKey ? new ApiKeyDetails { Location = "Header", Name = "X-Test-Key" } : null,
            RemoteServerScopes = "read",
            PublisherName = "Contoso",
            Description = "Test MCP server",
            CopilotClientAppId = "public-client",
            Force = false,
        }, because: "the optional refresh endpoint must not change existing authentication payloads");

        var externalIdp = JsonSerializer.SerializeToElement(request).GetProperty("externalIdp");
        if (isExternal)
        {
            externalIdp.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
                expectedUrl is null
                    ? ["authorizationUrl", "tokenUrl", "scopes"]
                    : ["authorizationUrl", "tokenUrl", "refreshUrl", "scopes"],
                because: "omitted refresh URLs must preserve the previous wire shape");
            if (expectedUrl is not null)
            {
                externalIdp.GetProperty("refreshUrl").GetString().Should().Be(expectedUrl,
                    because: "the platform expects the supplied URL at externalIdp.refreshUrl");
            }
        }
        else
        {
            externalIdp.ValueKind.Should().Be(JsonValueKind.Null,
                because: "non-external authentication must not acquire an external IdP payload");
        }

        _toolingService.ReceivedCalls().Should().BeEmpty(
            because: "resolving inputs and constructing a payload must not provision resources");
    }

    [Theory]
    [InlineData("ExternalOAuth", "/refresh", null, InvalidUrlError)]
    [InlineData("ExternalOAuth", "http://idp.example.test/refresh", null, InvalidUrlError)]
    [InlineData("ExternalOAuth", "ftp://idp.example.test/refresh", null, InvalidUrlError)]
    [InlineData("ExternalOAuth", "https://user@idp.example.test/refresh", null, InvalidUrlError)]
    [InlineData("ExternalOAuth", "https://idp.example.test/refresh#fragment", null, InvalidUrlError)]
    [InlineData("ExternalOAuth", "not a URL", null, InvalidUrlError)]
    [InlineData("ExternalOAuth", null, "/refresh", InvalidUrlError)]
    [InlineData("ExternalOAuth", null, "http://idp.example.test/refresh", InvalidUrlError)]
    [InlineData("ExternalOAuth", null, "https://user@idp.example.test/refresh", InvalidUrlError)]
    [InlineData("ExternalOAuth", null, "https://idp.example.test/refresh#fragment", InvalidUrlError)]
    [InlineData("EntraOAuth", RefreshUrl, null, InvalidModeError)]
    [InlineData("Entra", null, RefreshUrl, InvalidModeError)]
    [InlineData("APIKey", RefreshUrl, null, InvalidModeError)]
    [InlineData("APIKey", null, RefreshUrl, InvalidModeError)]
    [InlineData("NoAuth", RefreshUrl, null, InvalidModeError)]
    [InlineData("NoAuth", null, RefreshUrl, InvalidModeError)]
    public async Task InvalidRefreshUrl_ReturnsFailureBeforeProvisioning(
        string authType, string? flagUrl, string? fileUrl, string expectedError)
    {
        await WriteInputFileAsync(authType, fileUrl);
        var command = DevelopMcpCommand.CreateCommand(_logger, _toolingService, graphApiService: null);
        var args = new List<string> { "register-external-mcp-server", "--input-file", _inputFile };
        if (flagUrl is not null)
        {
            args.AddRange(["--idp-refresh-url", flagUrl]);
        }

        var exitCode = await command.InvokeAsync(args.ToArray());

        exitCode.Should().Be(1);
        _logger.Received(1).Log(
            LogLevel.Error,
            default,
            Arg.Is<object>(state => state.ToString() == expectedError),
            null,
            Arg.Any<Func<object, Exception?, string>>());
        _toolingService.ReceivedCalls().Should().BeEmpty(
            because: "invalid OAuth configuration must fail before provisioning or registration calls");
    }

    [Fact]
    public async Task RefreshUrlFlag_OverridesFileInDryRunSummaryWithoutProvisioning()
    {
        await WriteInputFileAsync("ExternalOAuth", "https://idp.example.test/file-refresh");
        var command = DevelopMcpCommand.CreateCommand(_logger, _toolingService, graphApiService: null);
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = await command.InvokeAsync(
            [
                "register-external-mcp-server",
                "--input-file", _inputFile,
                "--idp-refresh-url", RefreshUrl,
                "--dry-run",
            ]);

            exitCode.Should().Be(0);
            output.ToString().Should().Contain("IDP Refresh URL: " + RefreshUrl,
                because: "the summary must show the effective CLI override");
            output.ToString().Should().NotContain("https://idp.example.test/file-refresh",
                because: "a file URL overridden by the CLI must not remain effective");
            _toolingService.ReceivedCalls().Should().BeEmpty(
                because: "dry-run must remain free of provisioning side effects");
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    public void Dispose() => File.Delete(_inputFile);

    private async Task WriteInputFileAsync(string authType, string? refreshUrl)
    {
        var input = new RegisterExternalMcpServerInput
        {
            ServerName = "ext_Refresh",
            ServerUrl = "https://api.example.test/mcp",
            AuthType = authType,
            Description = "Test MCP server",
            PublisherName = "Contoso",
            Tools = [new RegisterExternalMcpServerInput.ToolEntry { Name = "get-item", Description = "Gets an item." }],
            RemoteScopes = "read",
            TenantId = "11111111-1111-1111-1111-111111111111",
            SecretLifetimeMonths = 6,
            ExternalOAuth = new RegisterExternalMcpServerInput.ExternalOAuthInput
            {
                AuthorizationUrl = AuthorizationUrl,
                TokenUrl = TokenUrl,
                RefreshUrl = refreshUrl,
                Scopes = "read offline_access",
                ClientId = "external-client",
                ClientSecret = "test-external-secret",
            },
            ApiKey = new RegisterExternalMcpServerInput.ApiKeyInput { Location = "Header", Name = "X-Test-Key" },
        };
        await File.WriteAllTextAsync(_inputFile, JsonSerializer.Serialize(input));
    }
}
