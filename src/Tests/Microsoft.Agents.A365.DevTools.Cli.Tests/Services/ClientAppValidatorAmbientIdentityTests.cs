// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Net;
using System.Text;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// End-to-end guard for issue #489: once the bootstrap resolves a tenant-owned client app,
/// every directory read and repair in client app validation must run as the ambient operator
/// identity. A token issued for the app under validation carries only User.Read, so Microsoft
/// Graph answers 403 Authorization_RequestDenied to application and servicePrincipal queries.
/// </summary>
public class ClientAppValidatorAmbientIdentityTests
{
    private const string CustomAppId = "11111111-2222-3333-4444-555555555555";
    private const string TenantId = "12345678-1234-1234-1234-123456789012";
    private const string AmbientToken = "ambient-identity-token";
    private const string CustomAppToken = "custom-app-user-read-token";

    /// <summary>
    /// Ambient identity can read the directory, while a token minted for the custom client app
    /// is refused by Graph.
    /// </summary>
    private sealed class DualIdentityGraphHandler : HttpMessageHandler
    {
        public List<string> RequestsAsCustomApp { get; } = new();
        public List<string> RequestsAsAmbient { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var descriptor = $"{request.Method} {request.RequestUri!.AbsolutePath}";

            if (request.Headers.Authorization?.Parameter == CustomAppToken)
            {
                RequestsAsCustomApp.Add(descriptor);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""",
                        Encoding.UTF8, "application/json")
                });
            }

            RequestsAsAmbient.Add(descriptor);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildBody(url), Encoding.UTF8, "application/json")
            });
        }

        private static string BuildBody(string url)
        {
            if (url.Contains("oauth2PermissionScopes", StringComparison.OrdinalIgnoreCase))
            {
                var scopes = string.Join(",", AuthenticationConstants.RequiredClientAppPermissions
                    .Select((p, i) => $$"""{"id":"{{PermissionId(i)}}","value":"{{p}}"}"""));
                return $$"""{"value":[{"id":"graph-sp","oauth2PermissionScopes":[{{scopes}}]}]}""";
            }

            if (url.Contains("/applications", StringComparison.OrdinalIgnoreCase))
            {
                var resourceAccess = string.Join(",", AuthenticationConstants.RequiredClientAppPermissions
                    .Select((_, i) => $$"""{"id":"{{PermissionId(i)}}","type":"Scope"}"""));
                var redirectUris = string.Join(",", AuthenticationConstants
                    .GetRequiredRedirectUris(CustomAppId).Select(u => $"\"{u}\""));
                return $$"""
                {"value":[{
                  "id":"app-object-id",
                  "appId":"{{CustomAppId}}",
                  "displayName":"Agent 365 CLI",
                  "isFallbackPublicClient":true,
                  "publicClient":{"redirectUris":[{{redirectUris}}]},
                  "optionalClaims":{"accessToken":[{"name":"wids"}]},
                  "requiredResourceAccess":[{"resourceAppId":"{{AuthenticationConstants.MicrosoftGraphResourceAppId}}","resourceAccess":[{{resourceAccess}}]}]
                }]}
                """;
            }

            return """{"value":[]}""";
        }

        private static string PermissionId(int index) => $"aaaa{index:0000}-0000-0000-0000-000000000000";
    }

    private static GraphApiService CreateGraphService(DualIdentityGraphHandler handler)
    {
        var authService = Substitute.For<IAuthenticationService>();
        authService.GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(),
                Arg.Any<string?>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Task.FromResult(AmbientToken));

        var tokenProvider = Substitute.For<IMicrosoftGraphTokenProvider>();
        tokenProvider.GetMgGraphAccessTokenAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>(CustomAppToken));

        return new GraphApiService(
            NullLogger<GraphApiService>.Instance,
            Substitute.For<CommandExecutor>(Substitute.For<ILogger<CommandExecutor>>()),
            authService, handler, tokenProvider,
            loginHintResolver: () => Task.FromResult<string?>(null),
            retryHelper: new RetryHelper(NullLogger.Instance, maxRetries: 1, baseDelaySeconds: 0))
        {
            // Exactly what RequirementsSubcommand does once the bootstrap resolves the app.
            CustomClientAppId = CustomAppId
        };
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenCustomAppTokenIsRefusedByGraph_StillCompletesValidation()
    {
        using var handler = new DualIdentityGraphHandler();
        var validator = new ClientAppValidator(NullLogger<ClientAppValidator>.Instance, CreateGraphService(handler));

        var act = async () => await validator.EnsureValidClientAppAsync(
            CustomAppId, TenantId, skipConfirmation: true);

        await act.Should().NotThrowAsync(
            because: "the app exists and is fully configured, so a 403 on the custom-app token must not fail setup");
        handler.RequestsAsCustomApp.Should().BeEmpty(
            because: "no directory read or repair may authenticate as the app under validation — that token only carries User.Read");
        handler.RequestsAsAmbient.Should().NotBeEmpty(
            because: "the validation flow must reach Graph using the operator's ambient identity");
    }

    [Fact]
    public async Task EnsureValidClientAppAsync_WhenAppIsFullyConfigured_MakesNoRepairWrites()
    {
        using var handler = new DualIdentityGraphHandler();
        var validator = new ClientAppValidator(NullLogger<ClientAppValidator>.Instance, CreateGraphService(handler));

        await validator.EnsureValidClientAppAsync(CustomAppId, TenantId, skipConfirmation: true);

        handler.RequestsAsAmbient.Should().NotContain(r => r.StartsWith("PATCH", StringComparison.Ordinal),
            because: "an app that already carries every required permission, redirect URI, the public-client flag and the wids claim needs no repair write");
    }
}
