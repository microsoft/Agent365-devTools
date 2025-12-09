// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.MockToolingServer.MockTools;

public class MockToolDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("inputSchema")] public object InputSchema { get; set; } = new { type = "object", properties = new { }, required = Array.Empty<string>() };

    // Response behavior
    [JsonPropertyName("responseTemplate")] public object ResponseTemplate { get; set; } = new { };
    [JsonPropertyName("delayMs")] public int DelayMs { get; set; } = 0;
    [JsonPropertyName("errorRate")] public double ErrorRate { get; set; } = 0.0; // 0-1
    [JsonPropertyName("statusCode")] public int StatusCode { get; set; } = 200;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public class MockToolStoreOptions
{
    public string? FilePath { get; set; }
}
