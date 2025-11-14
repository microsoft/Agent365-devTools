// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Internal;

public static class McpServerCatalogWriter
{
    public static string WriteCatalog(string responseContent)
    {
        var catalogPath = Path.Combine(Path.GetTempPath(), "mcpServerCatalog.json");
        File.WriteAllText(catalogPath, responseContent);
        return catalogPath;
    }

    public static string GetCatalogPath()
    {
        return Path.Combine(Path.GetTempPath(), "mcpServerCatalog.json");
    }
}