// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.MockToolingServer.MockTools;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.MockTools;

/// <summary>
/// Verifies that mock tool definitions stay in sync with real M365 MCP server snapshots.
/// Each snapshot file drives a separate test case via <see cref="MemberData"/>.
/// </summary>
public class MockToolFidelityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Discovers all snapshot files under the MockToolingServer/snapshots directory.
    /// Returns one object[] per file so xUnit shows each server as a separate test case.
    /// </summary>
    public static IEnumerable<object[]> GetSnapshotFiles()
    {
        var snapshotsDir = GetSnapshotsDirectory();
        var files = Directory.GetFiles(snapshotsDir, "*.snapshot.json");

        foreach (var file in files)
        {
            yield return new object[] { file };
        }
    }

    [Theory]
    [MemberData(nameof(GetSnapshotFiles))]
    public void SnapshotTools_ShouldAllExistInMockDefinition(string snapshotFilePath)
    {
        // Arrange
        var snapshot = LoadSnapshot(snapshotFilePath);

        if (string.Equals(snapshot.CapturedAt, "UNPOPULATED", StringComparison.OrdinalIgnoreCase))
        {
            // Snapshot has no real data yet. Pass vacuously until populated via
            // MockToolSnapshotCaptureTests (set MCP_BEARER_TOKEN and run:
            // dotnet test --filter "FullyQualifiedName~MockToolSnapshotCaptureTests").
            return;
        }

        snapshot.Tools.Should().NotBeEmpty(
            $"Snapshot '{snapshot.ServerName}' is marked as populated (capturedAt={snapshot.CapturedAt}) " +
            "but contains no tools. Re-capture the snapshot or mark it UNPOPULATED.");

        var mockTools = LoadEnabledMockTools(snapshot.ServerName).ToList();
        var mockToolsByName = mockTools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        // Act & Assert - every snapshot tool must exist in the mock and have a compatible inputSchema
        foreach (var snapshotTool in snapshot.Tools)
        {
            mockToolsByName.Should().ContainKey(
                snapshotTool.Name,
                $"Snapshot tool '{snapshotTool.Name}' for server '{snapshot.ServerName}' " +
                $"is missing from the mock definition. Add it to mocks/{snapshot.ServerName}.json.");

            if (!mockToolsByName.TryGetValue(snapshotTool.Name, out var mockTool))
            {
                continue;
            }

            if (TryGetInputSchema(snapshotTool.InputSchema, out var snapshotSchema))
            {
                TryGetInputSchema(mockTool.InputSchema, out var mockSchema).Should().BeTrue(
                    $"Mock tool '{snapshotTool.Name}' for server '{snapshot.ServerName}' " +
                    "must define an inputSchema when the snapshot tool does.");

                GetSchemaRequiredAndPropertyNames(snapshotSchema, out var snapshotRequired, out var snapshotPropertyNames);
                GetSchemaRequiredAndPropertyNames(mockSchema, out var mockRequired, out var mockPropertyNames);

                mockRequired.Should().BeEquivalentTo(
                    snapshotRequired,
                    $"Required fields for tool '{snapshotTool.Name}' on server '{snapshot.ServerName}' " +
                    "must match between snapshot and mock inputSchema.");

                mockPropertyNames.Should().BeEquivalentTo(
                    snapshotPropertyNames,
                    $"Property names for tool '{snapshotTool.Name}' on server '{snapshot.ServerName}' " +
                    "must match between snapshot and mock inputSchema.");
            }
        }
    }

    private static bool TryGetInputSchema(object? value, out JsonElement inputSchema)
    {
        inputSchema = default;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
            {
                return false;
            }

            inputSchema = element.Clone();
            return true;
        }

        if (value is string json && !string.IsNullOrWhiteSpace(json))
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            inputSchema = document.RootElement.Clone();
            return true;
        }

        return false;
    }

    private static void GetSchemaRequiredAndPropertyNames(
        JsonElement schema,
        out HashSet<string> required,
        out HashSet<string> propertyNames)
    {
        required = new HashSet<string>(StringComparer.Ordinal);
        propertyNames = new HashSet<string>(StringComparer.Ordinal);

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("required", out var requiredElement) &&
            requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var name = item.GetString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        required.Add(name);
                    }
                }
            }
        }

        if (schema.TryGetProperty("properties", out var propertiesElement) &&
            propertiesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in propertiesElement.EnumerateObject())
            {
                if (!string.IsNullOrEmpty(property.Name))
                {
                    propertyNames.Add(property.Name);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(GetSnapshotFiles))]
    public void MockTools_ShouldAllExistInSnapshot(string snapshotFilePath)
    {
        // Arrange
        var snapshot = LoadSnapshot(snapshotFilePath);

        if (string.Equals(snapshot.CapturedAt, "UNPOPULATED", StringComparison.OrdinalIgnoreCase))
        {
            // Snapshot has no real data yet. Pass vacuously until populated via
            // MockToolSnapshotCaptureTests (set MCP_BEARER_TOKEN and run:
            // dotnet test --filter "FullyQualifiedName~MockToolSnapshotCaptureTests").
            return;
        }

        snapshot.Tools.Should().NotBeEmpty(
            $"Snapshot '{snapshot.ServerName}' is marked as populated (capturedAt={snapshot.CapturedAt}) " +
            "but contains no tools. Re-capture the snapshot or mark it UNPOPULATED.");

        var mockTools = LoadEnabledMockTools(snapshot.ServerName);
        var snapshotToolNames = new HashSet<string>(snapshot.Tools.Select(t => t.Name));

        // Act & Assert - every enabled mock tool must exist in the snapshot
        foreach (var mockTool in mockTools)
        {
            snapshotToolNames.Should().Contain(
                mockTool.Name,
                $"Mock tool '{mockTool.Name}' for server '{snapshot.ServerName}' " +
                "does not exist in the real server snapshot. " +
                "Verify the tool name against the real M365 MCP server.");
        }
    }

    /// <summary>
    /// Resolves the repository root by walking up from the test assembly output directory
    /// until a directory containing a <c>src</c> subdirectory is found.
    /// </summary>
    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root. " +
            "Ensure the test is running from within the repository directory tree.");
    }

    private static string GetSnapshotsDirectory()
    {
        var repoRoot = GetRepoRoot();
        var snapshotsDir = Path.Combine(
            repoRoot, "src", "Microsoft.Agents.A365.DevTools.MockToolingServer", "snapshots");

        if (!Directory.Exists(snapshotsDir))
        {
            throw new DirectoryNotFoundException(
                $"Snapshots directory not found at: {snapshotsDir}");
        }

        return snapshotsDir;
    }

    private static string GetMocksDirectory()
    {
        var repoRoot = GetRepoRoot();
        var mocksDir = Path.Combine(
            repoRoot, "src", "Microsoft.Agents.A365.DevTools.MockToolingServer", "mocks");

        if (!Directory.Exists(mocksDir))
        {
            throw new DirectoryNotFoundException(
                $"Mocks directory not found at: {mocksDir}");
        }

        return mocksDir;
    }

    private static SnapshotFile LoadSnapshot(string snapshotFilePath)
    {
        var json = File.ReadAllText(snapshotFilePath);
        var snapshot = JsonSerializer.Deserialize<SnapshotFile>(json, JsonOptions);

        if (snapshot is null)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize snapshot file: {snapshotFilePath}");
        }

        return snapshot;
    }

    private static List<MockToolDefinition> LoadEnabledMockTools(string serverName)
    {
        var mocksDir = GetMocksDirectory();
        var mockFilePath = Path.Combine(mocksDir, $"{serverName}.json");

        if (!File.Exists(mockFilePath))
        {
            throw new FileNotFoundException(
                $"Mock definition file not found for server '{serverName}'. " +
                $"Expected at: {mockFilePath}");
        }

        var json = File.ReadAllText(mockFilePath);
        var allTools = JsonSerializer.Deserialize<List<MockToolDefinition>>(json, JsonOptions);

        if (allTools is null)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize mock file: {mockFilePath}");
        }

        return allTools.Where(t => t.Enabled).ToList();
    }

    /// <summary>
    /// Minimal model for deserializing snapshot JSON files.
    /// </summary>
    private sealed class SnapshotFile
    {
        [JsonPropertyName("capturedAt")]
        public string CapturedAt { get; set; } = string.Empty;

        [JsonPropertyName("serverName")]
        public string ServerName { get; set; } = string.Empty;

        [JsonPropertyName("tools")]
        public List<SnapshotTool> Tools { get; set; } = new();
    }

    /// <summary>
    /// Minimal model for a tool entry within a snapshot file.
    /// </summary>
    private sealed class SnapshotTool
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("inputSchema")]
        public JsonElement InputSchema { get; set; }
    }
}
