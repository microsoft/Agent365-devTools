// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.MockToolingServer.MockTools;

public interface IMockToolStore
{
    string McpServerName { get; }
    Task<IReadOnlyList<MockToolDefinition>> ListAsync();
    Task<MockToolDefinition?> GetAsync(string name);
    Task UpsertAsync(MockToolDefinition def);
    Task<bool> DeleteAsync(string name);
    Task ReloadAsync();
}
