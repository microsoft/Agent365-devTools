// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Internal;

public static class McpServerCatalogWriter
{
    /// <summary>
    /// Normalizes a discover-endpoint response to the wrapped <c>{"mcpServers":[...]}</c> format.
    /// V2 returns a bare JSON array; V1 returns the wrapped object. Both are accepted.
    /// </summary>
    public static string Normalize(string responseContent)
    {
        if (responseContent.TrimStart().StartsWith('['))
        {
            return $"{{\"mcpServers\":{responseContent}}}";
        }
        return responseContent;
    }

    public static string WriteCatalog(string responseContent)
    {
        var catalogPath = Path.Combine(Path.GetTempPath(), "mcpServerCatalog.json");
        File.WriteAllText(catalogPath, Normalize(responseContent));
        return catalogPath;
    }

    public static string GetCatalogPath()
    {
        return Path.Combine(Path.GetTempPath(), "mcpServerCatalog.json");
    }
}