// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Internal;

public static class HttpClientFactory
{
    public const string DefaultUserAgentPrefix = "Agent365CLI";
    public const string CorrelationIdHeaderName = "x-ms-correlation-id";

    /// <summary>
    /// Creates an authenticated HTTP client with standard headers.
    /// </summary>
    /// <param name="authToken">Optional Bearer token for authentication.</param>
    /// <param name="userAgentPrefix">User-Agent prefix (defaults to Agent365CLI).</param>
    /// <param name="correlationId">
    /// Optional correlation ID for request tracing. If null, empty, or whitespace,
    /// a new GUID will be generated automatically.
    /// </param>
    /// <returns>A configured HttpClient instance with the correlation ID applied.</returns>
    public static HttpClient CreateAuthenticatedClient(
        string? authToken = null,
        string userAgentPrefix = DefaultUserAgentPrefix,
        string? correlationId = null)
    {
        var client = new HttpClient();

        if (!string.IsNullOrWhiteSpace(authToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);
        }

        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        // Set a custom User-Agent header
        var effectivePrefix = string.IsNullOrWhiteSpace(userAgentPrefix) ? DefaultUserAgentPrefix : userAgentPrefix;
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{effectivePrefix}/{Assembly.GetExecutingAssembly().GetName().Version}");

        // Set correlation ID header - generate if not provided
        var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString()
            : correlationId;
        client.DefaultRequestHeaders.Add(CorrelationIdHeaderName, effectiveCorrelationId);

        return client;
    }

    /// <summary>
    /// Generates a new correlation ID for use across multiple HTTP requests in a workflow.
    /// Call this at the start of a workflow and pass the returned value to all subsequent
    /// <see cref="CreateAuthenticatedClient"/> calls.
    /// </summary>
    /// <returns>A new GUID string suitable for use as a correlation ID.</returns>
    public static string GenerateCorrelationId() => Guid.NewGuid().ToString();
}