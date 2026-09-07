// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

public static class ThirdPartyCommand
{
    public static Command CreateCommand(
        ILogger<ThirdPartyCommand> logger,
        HttpClient httpClient)
    {
        var command = new Command("3p", "Manage third-party agent connections");
        command.AddCommand(CreateConnectCommand(logger, httpClient));
        return command;
    }

    private static Command CreateConnectCommand(
        ILogger<ThirdPartyCommand> logger,
        HttpClient httpClient)
    {
        var endpointOption = new Option<string>(
            "--endpoint",
            "Base URL of the Agent 365 third-party Connect API")
        {
            IsRequired = true,
        };
        var providerOption = new Option<string>(
            "--provider",
            getDefaultValue: () => "aws",
            "Third-party agent provider");
        var nameOption = new Option<string>(
            ["--name", "-n"],
            "Name of the provider connection")
        {
            IsRequired = true,
        };

        var command = new Command(
            "connect",
            "Create a connection, import provider agents, and start telemetry synchronization");
        command.AddOption(endpointOption);
        command.AddOption(providerOption);
        command.AddOption(nameOption);

        command.SetHandler(async (InvocationContext context) =>
        {
            var endpoint = context.ParseResult.GetValueForOption(endpointOption)!;
            var provider = context.ParseResult.GetValueForOption(providerOption)!;
            var name = context.ParseResult.GetValueForOption(nameOption)!;
            var cancellationToken = context.GetCancellationToken();

            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri)
                || baseUri.Scheme is not ("http" or "https"))
            {
                logger.LogError("Endpoint must be an absolute HTTP or HTTPS URL.");
                context.ExitCode = 1;
                return;
            }

            try
            {
                var requestUri = new Uri(baseUri, "/api/3p/connections");
                using var response = await httpClient.PostAsJsonAsync(
                    requestUri,
                    new ConnectRequest(provider, name),
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                    logger.LogError(
                        "Third-party connection failed ({StatusCode}): {Detail}",
                        (int)response.StatusCode,
                        detail);
                    context.ExitCode = 1;
                    return;
                }

                var result = await response.Content.ReadFromJsonAsync<ConnectResponse>(
                    cancellationToken: cancellationToken);
                if (result is null)
                {
                    logger.LogError("The Connect API returned an empty response.");
                    context.ExitCode = 1;
                    return;
                }

                logger.LogInformation("Connection: {ConnectionId}", result.Connection.ConnectionId);
                logger.LogInformation("Discovered agents: {Count}", result.DiscoveredAgents);
                logger.LogInformation("Imported agents: {Count}", result.ImportedAgents.Count);
                logger.LogInformation("Telemetry records exported: {Count}", result.Telemetry.RecordsExported);
                logger.LogInformation("Checkpoint: {Checkpoint}", result.Telemetry.Checkpoint);
            }
            catch (HttpRequestException ex)
            {
                logger.LogError("Unable to reach the Connect API: {Message}", ex.Message);
                context.ExitCode = 1;
            }
            catch (JsonException ex)
            {
                logger.LogError("The Connect API returned an invalid response: {Message}", ex.Message);
                context.ExitCode = 1;
            }
        });

        return command;
    }

    private sealed record ConnectRequest(string Provider, string Name);

    private sealed record ConnectResponse(
        ConnectionResponse Connection,
        int DiscoveredAgents,
        IReadOnlyList<ImportedAgentResponse> ImportedAgents,
        TelemetryResponse Telemetry);

    private sealed record ConnectionResponse(
        [property: JsonPropertyName("connection_id")] string ConnectionId,
        string Provider);

    private sealed record ImportedAgentResponse(
        [property: JsonPropertyName("provider_agent_id")] string ProviderAgentId,
        [property: JsonPropertyName("observability_id")] string ObservabilityId);

    private sealed record TelemetryResponse(
        [property: JsonPropertyName("records_read")] int RecordsRead,
        [property: JsonPropertyName("records_exported")] int RecordsExported,
        [property: JsonPropertyName("checkpoint")] string Checkpoint);
}