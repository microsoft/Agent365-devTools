// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Internal;

public static class HttpClientFactory
{
    public const string DefaultUserAgentPrefix = "Agent365CLI";

    public static HttpClient CreateAuthenticatedClient(string? authToken = null, string userAgentPrefix = DefaultUserAgentPrefix)
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
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{effectivePrefix}/{Assembly.GetExecutingAssembly().GetName().Version}");

        return client;
    }
}