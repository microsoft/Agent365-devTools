// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Discovers MCP tool schemas from a running MCP server using Streamable HTTP transport.
/// Implements the MCP protocol handshake (initialize, notifications/initialized, tools/list)
/// over JSON-RPC 2.0 POST requests.
/// </summary>
internal sealed class SchemaDiscoveryService : ISchemaDiscoveryService, IDisposable
{
    private const string McpProtocolVersion = "2025-03-26";
    private const string ClientName = "a365-evaluate";
    private const string ClientVersion = "1.0";
    private const string JsonRpcVersion = "2.0";

    private readonly ILogger<SchemaDiscoveryService> _logger;
    private readonly HttpClient _httpClient;

    public SchemaDiscoveryService(ILogger<SchemaDiscoveryService> logger, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _httpClient = handler != null ? new HttpClient(handler) : HttpClientFactory.CreateAuthenticatedClient();

        // The MCP server is untrusted input and tool schemas are typically < 1 MB. Cap the
        // buffered response size so a hostile or malformed server cannot exhaust memory with a
        // huge tools/list payload; exceeding it throws during ReadAsStringAsync and is caught as
        // EvaluationException by DiscoverToolsAsync. Scoped to this client rather than
        // HttpClientFactory, which is shared by Graph/ARM/tooling where large responses are valid.
        _httpClient.MaxResponseContentBufferSize = 10 * 1024 * 1024; // 10 MB ceiling
    }

    /// <summary>
    /// Disposes the owned <see cref="HttpClient"/>. The service is registered as a
    /// singleton (Program.cs), so in practice this runs at process shutdown;
    /// implementing it keeps the IDisposable contract correct if the registration
    /// lifetime ever changes to transient or scoped.
    /// </summary>
    public void Dispose() => _httpClient.Dispose();

    /// <inheritdoc />
    public async Task<List<ToolSchema>> DiscoverToolsAsync(string serverUrl, string? authToken = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new EvaluationException(
                ErrorCodes.SchemaDiscoveryFailed,
                "Server URL is required for schema discovery.",
                mitigationSteps: new List<string>
                {
                    "Provide a valid MCP server Streamable HTTP endpoint URL."
                });
        }

        _logger.LogDebug("Starting MCP schema discovery against {ServerUrl}", serverUrl);

        try
        {
            // Step 1: Initialize
            await SendInitializeAsync(serverUrl, authToken, cancellationToken);

            // Step 2: Send initialized notification
            await SendInitializedNotificationAsync(serverUrl, authToken, cancellationToken);

            // Step 3: List tools
            var tools = await SendToolsListAsync(serverUrl, authToken, cancellationToken);

            if (tools.Count == 0)
            {
                throw new EvaluationException(
                    ErrorCodes.SchemaDiscoveryFailed,
                    "MCP server returned an empty tool list.",
                    errorDetails: new List<string> { $"Server URL: {serverUrl}" },
                    mitigationSteps: new List<string>
                    {
                        "Verify the MCP server is running and has tools registered.",
                        "Check the server logs for registration errors."
                    });
            }

            _logger.LogDebug("Schema discovery complete. Found {ToolCount} tool(s).", tools.Count);
            return tools;
        }
        catch (EvaluationException)
        {
            // Re-throw our own exceptions as-is
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new EvaluationException(
                ErrorCodes.SchemaDiscoveryFailed,
                "Failed to connect to MCP server.",
                errorDetails: new List<string> { $"Server URL: {serverUrl}", ex.Message },
                mitigationSteps: new List<string>
                {
                    "Verify the MCP server is running and accessible.",
                    "Check the URL is correct and includes the full endpoint path.",
                    "Ensure no firewall or network issues are blocking the connection."
                },
                innerException: ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !cancellationToken.IsCancellationRequested)
        {
            throw new EvaluationException(
                ErrorCodes.SchemaDiscoveryFailed,
                "Connection to MCP server timed out.",
                errorDetails: new List<string> { $"Server URL: {serverUrl}" },
                mitigationSteps: new List<string>
                {
                    "Verify the MCP server is running and responsive.",
                    "Check if the server URL is correct.",
                    "The server may be under heavy load; try again later."
                },
                innerException: ex);
        }
        catch (JsonException ex)
        {
            throw new EvaluationException(
                ErrorCodes.SchemaDiscoveryFailed,
                "MCP server returned an invalid JSON response.",
                errorDetails: new List<string> { $"Server URL: {serverUrl}", ex.Message },
                mitigationSteps: new List<string>
                {
                    "Verify the server implements the MCP protocol correctly.",
                    "Check the server logs for errors."
                },
                innerException: ex);
        }
    }

    private async Task SendInitializeAsync(string serverUrl, string? authToken, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Sending MCP initialize request...");

        var requestBody = JsonSerializer.Serialize(new
        {
            jsonrpc = JsonRpcVersion,
            method = "initialize",
            @params = new
            {
                protocolVersion = McpProtocolVersion,
                capabilities = new { },
                clientInfo = new
                {
                    name = ClientName,
                    version = ClientVersion
                }
            },
            id = 1
        });

        using var response = await PostJsonRpcAsync(serverUrl, requestBody, authToken, cancellationToken);
        var responseBody = await ReadJsonResponseAsync(response, cancellationToken);

        // Validate JSON-RPC response
        using var doc = JsonDocument.Parse(responseBody);
        if (doc.RootElement.TryGetProperty("error", out var errorElement))
        {
            var errorMessage = errorElement.TryGetProperty("message", out var msgProp)
                ? msgProp.GetString() ?? "Unknown error"
                : "Unknown error";

            throw new EvaluationException(
                ErrorCodes.SchemaDiscoveryFailed,
                "MCP server initialize request failed.",
                errorDetails: new List<string> { $"Server error: {errorMessage}" },
                mitigationSteps: new List<string>
                {
                    "Verify the server supports MCP protocol version " + McpProtocolVersion + ".",
                    "Check the server logs for initialization errors."
                });
        }

        _logger.LogDebug("MCP initialize succeeded.");
    }

    private async Task SendInitializedNotificationAsync(string serverUrl, string? authToken, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Sending MCP initialized notification...");

        var requestBody = JsonSerializer.Serialize(new
        {
            jsonrpc = JsonRpcVersion,
            method = "notifications/initialized",
            @params = new { }
        });

        // Notifications may not return a response body, but we still POST
        using var response = await PostJsonRpcAsync(serverUrl, requestBody, authToken, cancellationToken);

        _logger.LogDebug("MCP initialized notification sent.");
    }

    private async Task<List<ToolSchema>> SendToolsListAsync(string serverUrl, string? authToken, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Sending MCP tools/list request...");

        var requestBody = JsonSerializer.Serialize(new
        {
            jsonrpc = JsonRpcVersion,
            method = "tools/list",
            @params = new { },
            id = 2
        });

        using var response = await PostJsonRpcAsync(serverUrl, requestBody, authToken, cancellationToken);
        var responseBody = await ReadJsonResponseAsync(response, cancellationToken);

        using var doc = JsonDocument.Parse(responseBody);

        // Check for JSON-RPC error
        if (doc.RootElement.TryGetProperty("error", out var errorElement))
        {
            var errorMessage = errorElement.TryGetProperty("message", out var msgProp)
                ? msgProp.GetString() ?? "Unknown error"
                : "Unknown error";

            throw new EvaluationException(
                ErrorCodes.SchemaDiscoveryFailed,
                "MCP server tools/list request failed.",
                errorDetails: new List<string> { $"Server error: {errorMessage}" },
                mitigationSteps: new List<string>
                {
                    "Verify the server has tools registered.",
                    "Check the server logs for errors."
                });
        }

        // Parse result.tools array
        if (!doc.RootElement.TryGetProperty("result", out var resultElement) ||
            !resultElement.TryGetProperty("tools", out var toolsElement) ||
            toolsElement.ValueKind != JsonValueKind.Array)
        {
            throw new EvaluationException(
                ErrorCodes.SchemaDiscoveryFailed,
                "MCP server returned an unexpected response format for tools/list.",
                errorDetails: new List<string> { "Expected result.tools to be a JSON array." },
                mitigationSteps: new List<string>
                {
                    "Verify the server implements the MCP tools/list method correctly."
                });
        }

        var tools = new List<ToolSchema>();

        foreach (var toolElement in toolsElement.EnumerateArray())
        {
            var name = toolElement.TryGetProperty("name", out var nameProp)
                ? nameProp.GetString() ?? string.Empty
                : string.Empty;

            var description = toolElement.TryGetProperty("description", out var descProp)
                ? descProp.GetString() ?? string.Empty
                : string.Empty;

            JsonElement? inputSchema = toolElement.TryGetProperty("inputSchema", out var schemaProp)
                ? schemaProp.Clone()
                : null;

            tools.Add(new ToolSchema
            {
                Name = name,
                Description = description,
                InputSchema = inputSchema
            });
        }

        _logger.LogDebug("tools/list returned {ToolCount} tool(s).", tools.Count);
        return tools;
    }

    private async Task<HttpResponseMessage> PostJsonRpcAsync(
        string serverUrl,
        string requestBody,
        string? authToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, serverUrl)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        // MCP Streamable HTTP transport requires Accept header
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrWhiteSpace(authToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            var reasonPhrase = response.ReasonPhrase;
            response.Dispose();

            throw new EvaluationException(
                ErrorCodes.SchemaDiscoveryFailed,
                $"MCP server returned HTTP {statusCode}.",
                errorDetails: new List<string> { $"Server URL: {serverUrl}", $"HTTP Status: {statusCode} {reasonPhrase}" },
                mitigationSteps: new List<string>
                {
                    "Verify the MCP server is running and accessible.",
                    "Check that the URL points to the correct Streamable HTTP endpoint."
                });
        }

        return response;
    }

    /// <summary>
    /// Reads the response body, handling both plain JSON and SSE (Server-Sent Events) formats.
    /// MCP Streamable HTTP may return SSE with lines like:
    ///   event: message
    ///   data: {"jsonrpc":"2.0",...}
    /// </summary>
    private async Task<string> ReadJsonResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;

        // If plain JSON, return as-is
        if (contentType == "application/json" || body.TrimStart().StartsWith('{'))
        {
            return body;
        }

        // Parse SSE: extract the last "data:" line that contains JSON
        _logger.LogDebug("Response is SSE format, extracting JSON from event stream");
        string? lastJsonData = null;
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                var data = trimmed["data:".Length..].Trim();
                if (data.StartsWith('{'))
                {
                    lastJsonData = data;
                }
            }
        }

        if (lastJsonData is not null)
        {
            return lastJsonData;
        }

        // Fallback: return raw body and let the JSON parser report the error
        _logger.LogWarning("Could not extract JSON from SSE response");
        return body;
    }
}
