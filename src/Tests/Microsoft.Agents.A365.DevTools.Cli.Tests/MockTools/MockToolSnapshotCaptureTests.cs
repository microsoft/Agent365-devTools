// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.MockTools;

/// <summary>
/// Integration tests that query live M365 MCP servers and verify tool catalogs
/// match the checked-in snapshot files.
///
/// These tests are skipped unless <c>MCP_BEARER_TOKEN</c> is set. They are never
/// run in CI (which has no M365 credentials) — they are a developer tool for
/// detecting and refreshing snapshots when real servers change.
///
/// Usage:
///   # Drift detection only (fails if live server differs from snapshot)
///   $env:MCP_BEARER_TOKEN = a365 develop get-token --output raw
///   dotnet test --filter "FullyQualifiedName~MockToolSnapshotCaptureTests"
///
///   # Refresh snapshots AND auto-update the corresponding mock files
///   $env:MCP_UPDATE_SNAPSHOTS = "true"
///   dotnet test --filter "FullyQualifiedName~MockToolSnapshotCaptureTests"
///
/// When MCP_UPDATE_SNAPSHOTS=true, both the snapshot file and the mock file are
/// written. The mock merge preserves existing responseTemplate / delayMs / errorRate
/// values for tools that still exist, adds new tools with generated defaults, and
/// sets enabled=false for tools that have been removed from the real server.
///
/// Future: consider promoting snapshot capture and mock sync to explicit
/// <c>a365 develop</c> subcommands for better discoverability.
/// </summary>
[CollectionDefinition("MockToolSnapshotCapture", DisableParallelization = true)]
public class MockToolSnapshotCaptureCollection { }

[Collection("MockToolSnapshotCapture")]
public class MockToolSnapshotCaptureTests
{
    private const string BearerTokenEnvVar = "MCP_BEARER_TOKEN";
    private const string UpdateSnapshotsEnvVar = "MCP_UPDATE_SNAPSHOTS";
    private const string McpBaseUrl = "https://substrate.office.com";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IEnumerable<object[]> GetServerNames()
    {
        yield return new object[] { "mcp_CalendarTools" };
        yield return new object[] { "mcp_MailTools" };
        yield return new object[] { "mcp_MeServer" };
        yield return new object[] { "mcp_KnowledgeTools" };
    }

    /// <summary>
    /// Queries each live M365 MCP server and compares its tool catalog against the
    /// checked-in snapshot. Fails with a clear diff if the live server has added or
    /// removed tools since the snapshot was captured.
    ///
    /// When <c>MCP_UPDATE_SNAPSHOTS=true</c>, writes refreshed snapshot files to disk
    /// instead of asserting, so the caller can review and commit the changes.
    /// </summary>
    [Theory]
    [MemberData(nameof(GetServerNames))]
    public async Task LiveServer_ToolCatalog_ShouldMatchSnapshot(string serverName)
    {
        var token = Environment.GetEnvironmentVariable(BearerTokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
        {
            // No token — skip. Set MCP_BEARER_TOKEN to run these tests.
            return;
        }

        var liveTools = await FetchLiveToolsAsync(serverName, token);

        var shouldUpdate = string.Equals(
            Environment.GetEnvironmentVariable(UpdateSnapshotsEnvVar),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (shouldUpdate)
        {
            WriteSnapshot(serverName, liveTools);
            return;
        }

        // Drift detection: compare live tool names against snapshot
        var snapshot = LoadSnapshot(serverName);

        if (string.Equals(snapshot.CapturedAt, "UNPOPULATED", StringComparison.OrdinalIgnoreCase))
        {
            // Snapshot never populated — write it now that we have a live token
            WriteSnapshot(serverName, liveTools);
            return;
        }

        var snapshotNames = snapshot.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var liveNames    = liveTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var addedOnLive      = liveNames.Except(snapshotNames).OrderBy(n => n).ToList();
        var removedFromLive  = snapshotNames.Except(liveNames).OrderBy(n => n).ToList();

        addedOnLive.Should().BeEmpty(
            $"server '{serverName}' exposes new tools not yet captured in the snapshot: " +
            $"{string.Join(", ", addedOnLive)}. " +
            $"Re-run with {UpdateSnapshotsEnvVar}=true to refresh snapshots and auto-update the mock file.");

        removedFromLive.Should().BeEmpty(
            $"server '{serverName}' no longer exposes tools that are still in the snapshot: " +
            $"{string.Join(", ", removedFromLive)}. " +
            $"Re-run with {UpdateSnapshotsEnvVar}=true to refresh snapshots and auto-update the mock file.");
    }

    // -------------------------------------------------------------------------
    // HTTP / SSE helpers
    // -------------------------------------------------------------------------

    private static async Task<List<LiveTool>> FetchLiveToolsAsync(string serverName, string token)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        client.DefaultRequestHeaders.Add("Accept", "application/json, text/event-stream");

        var requestBody = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id      = 1,
            method  = "tools/list",
            @params = new { }
        });

        using var content  = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"{McpBaseUrl}/agents/servers/{serverName}", content);

        response.EnsureSuccessStatusCode();

        var rawContent  = await response.Content.ReadAsStringAsync();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

        // Real M365 MCP servers respond with SSE (text/event-stream).
        // Take the last data: payload whose content starts with "{" — this is
        // the JSON-RPC result frame. Earlier data: events (ping, endpoint) are
        // discarded.
        string jsonText;
        if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            jsonText = rawContent.Split('\n')
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line["data:".Length..].Trim())
                .LastOrDefault(s => s.StartsWith("{", StringComparison.Ordinal))
                ?? string.Empty;
        }
        else
        {
            jsonText = rawContent;
        }

        if (string.IsNullOrWhiteSpace(jsonText))
            throw new InvalidOperationException(
                $"Server '{serverName}' returned an empty response body.");

        var rpcResponse = JsonSerializer.Deserialize<JsonRpcResponse>(jsonText, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize JSON-RPC response from '{serverName}'.");

        if (rpcResponse.Error is not null)
            throw new InvalidOperationException(
                $"JSON-RPC error from '{serverName}': {rpcResponse.Error.Message}");

        return rpcResponse.Result?.Tools ?? [];
    }

    // -------------------------------------------------------------------------
    // Snapshot read / write helpers
    // -------------------------------------------------------------------------

    private static SnapshotFile LoadSnapshot(string serverName)
    {
        var path = Path.Combine(GetSnapshotsDirectory(), $"{serverName}.snapshot.json");

        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Snapshot file not found for server '{serverName}'. Expected at: {path}");

        var json     = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<SnapshotFile>(json, JsonOptions);

        return snapshot ?? throw new InvalidOperationException(
            $"Failed to deserialize snapshot file: {path}");
    }

    private static void WriteSnapshot(string serverName, List<LiveTool> tools)
    {
        var dict = new Dictionary<string, object>
        {
            ["$schema"]    = "mock-snapshot-schema",
            ["capturedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["serverName"] = serverName,
            ["tools"]      = tools.Select(t => new { t.Name, t.Description, t.InputSchema }).ToList()
        };

        var json    = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        var outPath = Path.Combine(GetSnapshotsDirectory(), $"{serverName}.snapshot.json");

        var utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(outPath, json, utf8NoBom);

        // Auto-sync the mock file so its inputSchema stays aligned with the new snapshot.
        // Existing responseTemplate / delayMs / errorRate / statusCode values are preserved.
        MergeMockFile(serverName, tools);
    }

    // -------------------------------------------------------------------------
    // Mock file auto-sync
    // -------------------------------------------------------------------------

    /// <summary>
    /// Merges live tool definitions into the corresponding mock file:
    /// <list type="bullet">
    ///   <item>Existing tools: schema updated from snapshot; behavior fields (responseTemplate,
    ///   delayMs, errorRate, statusCode, enabled) preserved from the current mock entry.</item>
    ///   <item>New tools (in snapshot but not in mock): added with generated defaults.</item>
    ///   <item>Removed tools (in mock but not in snapshot): kept with <c>enabled=false</c>
    ///   so the developer can review and delete them explicitly.</item>
    /// </list>
    /// </summary>
    private static void MergeMockFile(string serverName, List<LiveTool> liveTools)
    {
        var mockPath = Path.Combine(GetMocksDirectory(), $"{serverName}.json");

        // Load existing mock entries to preserve their behavior fields.
        var existingByName = File.Exists(mockPath)
            ? (JsonSerializer.Deserialize<List<ExistingMockEntry>>(File.ReadAllText(mockPath), JsonOptions) ?? [])
              .ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ExistingMockEntry>(StringComparer.OrdinalIgnoreCase);

        // Rebuild from snapshot order, preserving behavior fields for existing tools.
        var updated = liveTools.Select(live =>
        {
            existingByName.TryGetValue(live.Name, out var ex);
            return new Dictionary<string, object?>
            {
                ["name"]             = live.Name,
                ["description"]      = live.Description,
                ["inputSchema"]      = live.InputSchema,
                ["responseTemplate"] = ex?.ResponseTemplate ?? $"Mock response from {live.Name} (mock).",
                ["delayMs"]          = (object)(ex?.DelayMs   ?? 250),
                ["errorRate"]        = (object)(ex?.ErrorRate  ?? 0.0),
                ["statusCode"]       = (object)(ex?.StatusCode ?? 200),
                ["enabled"]          = (object)(ex?.Enabled    ?? true),
            };
        }).ToList();

        // Tools removed from the real server: keep as disabled for developer review.
        var liveNames = liveTools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var ex in existingByName.Values.Where(e => !liveNames.Contains(e.Name)))
        {
            updated.Add(new Dictionary<string, object?>
            {
                ["name"]             = ex.Name,
                ["description"]      = ex.Description,
                ["inputSchema"]      = ex.InputSchema,
                ["responseTemplate"] = ex.ResponseTemplate,
                ["delayMs"]          = (object)ex.DelayMs,
                ["errorRate"]        = (object)ex.ErrorRate,
                ["statusCode"]       = (object)ex.StatusCode,
                ["enabled"]          = (object)false,
            });
        }

        var utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(
            mockPath,
            JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }),
            utf8NoBom);
    }

    private static string GetSnapshotsDirectory()
        => Path.Combine(GetMockToolingServerDirectory(), "snapshots");

    private static string GetMocksDirectory()
        => Path.Combine(GetMockToolingServerDirectory(), "mocks");

    private static string GetMockToolingServerDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return Path.Combine(
                    dir.FullName,
                    "src",
                    "Microsoft.Agents.A365.DevTools.MockToolingServer");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root. " +
            "Ensure the test is running from within the repository directory tree.");
    }

    // -------------------------------------------------------------------------
    // Private models
    // -------------------------------------------------------------------------

    private sealed class SnapshotFile
    {
        [JsonPropertyName("capturedAt")]
        public string CapturedAt { get; set; } = string.Empty;

        [JsonPropertyName("serverName")]
        public string ServerName { get; set; } = string.Empty;

        [JsonPropertyName("tools")]
        public List<SnapshotTool> Tools { get; set; } = [];
    }

    private sealed class SnapshotTool
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    private sealed class LiveTool
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("inputSchema")]
        public JsonElement InputSchema { get; set; }
    }

    private sealed class JsonRpcResponse
    {
        [JsonPropertyName("result")]
        public JsonRpcResult? Result { get; set; }

        [JsonPropertyName("error")]
        public JsonRpcError? Error { get; set; }
    }

    private sealed class JsonRpcResult
    {
        [JsonPropertyName("tools")]
        public List<LiveTool> Tools { get; set; } = [];
    }

    private sealed class JsonRpcError
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Minimal model for reading existing mock entries to preserve their behavior fields
    /// during auto-merge. Only the fields that must survive a snapshot refresh are included.
    /// </summary>
    private sealed class ExistingMockEntry
    {
        [JsonPropertyName("name")]             public string Name             { get; set; } = string.Empty;
        [JsonPropertyName("description")]      public string Description      { get; set; } = string.Empty;
        [JsonPropertyName("inputSchema")]      public JsonElement InputSchema  { get; set; }
        [JsonPropertyName("responseTemplate")] public string ResponseTemplate { get; set; } = string.Empty;
        [JsonPropertyName("delayMs")]          public int    DelayMs          { get; set; } = 250;
        [JsonPropertyName("errorRate")]        public double ErrorRate         { get; set; } = 0.0;
        [JsonPropertyName("statusCode")]       public int    StatusCode        { get; set; } = 200;
        [JsonPropertyName("enabled")]          public bool   Enabled           { get; set; } = true;
    }
}
