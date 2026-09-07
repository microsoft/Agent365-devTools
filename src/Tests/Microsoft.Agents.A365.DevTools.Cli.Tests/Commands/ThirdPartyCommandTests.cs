// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

public class ThirdPartyCommandTests
{
    [Fact]
    public void Command_Has_Connect_Subcommand()
    {
        using var httpClient = new HttpClient(new RecordingHandler());
        var command = ThirdPartyCommand.CreateCommand(
            Substitute.For<ILogger<ThirdPartyCommand>>(),
            httpClient);

        Assert.Equal("3p", command.Name);
        var connect = Assert.Single(command.Subcommands);
        Assert.Equal("connect", connect.Name);
        Assert.Contains(connect.Options, option => option.Name == "endpoint" && option.IsRequired);
        Assert.Contains(connect.Options, option => option.Name == "name" && option.IsRequired);
        Assert.Contains(connect.Options, option => option.Name == "provider");
    }

    [Fact]
    public async Task Connect_Posts_To_Shared_Api()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var command = ThirdPartyCommand.CreateCommand(
            Substitute.For<ILogger<ThirdPartyCommand>>(),
            httpClient);

        var exitCode = await command.InvokeAsync(
            "connect --endpoint http://localhost:8000 --name \"E2E AWS\"");

        Assert.Equal(0, exitCode);
        Assert.Equal(new Uri("http://localhost:8000/api/3p/connections"), handler.RequestUri);
        using var payload = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("aws", payload.RootElement.GetProperty("provider").GetString());
        Assert.Equal("E2E AWS", payload.RootElement.GetProperty("name").GetString());
    }

        [Fact]
        public async Task Connect_Returns_Failure_For_Invalid_Api_Response()
        {
                using var httpClient = new HttpClient(new RecordingHandler("not-json"));
                var command = ThirdPartyCommand.CreateCommand(
                        Substitute.For<ILogger<ThirdPartyCommand>>(),
                        httpClient);

                var exitCode = await command.InvokeAsync(
                        "connect --endpoint http://localhost:8000 --name \"E2E AWS\"");

                Assert.Equal(1, exitCode);
        }

    private sealed class RecordingHandler : HttpMessageHandler
    {
                private readonly string _response;

                public RecordingHandler(string? response = null)
                {
                        _response = response ?? """
                                {
                                    "connection": {"connection_id": "connection-1", "provider": "aws"},
                                    "discoveredAgents": 4,
                                    "importedAgents": [
                                        {"provider_agent_id": "agent-1", "observability_id": "obs-1"}
                                    ],
                                    "telemetry": {
                                        "records_read": 3,
                                        "records_exported": 3,
                                        "checkpoint": "checkpoint-1"
                                    }
                                }
                                """;
                }

        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                                Content = new StringContent(_response, Encoding.UTF8, "application/json"),
            };
        }
    }
}