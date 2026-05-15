// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Internal;

public static class McpServerCatalogWriter
{
    /// <summary>
    /// Environment variable that overrides the catalog path. Set in tests to avoid the
    /// machine-global default and prevent cross-test-class collisions. Public so external
    /// harnesses can reliably reference the name without depending on friend-assembly wiring.
    /// </summary>
    public const string CatalogPathEnvVar = "A365_MCP_CATALOG_PATH";

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
        var catalogPath = GetCatalogPath();
        File.WriteAllText(catalogPath, Normalize(responseContent));
        return catalogPath;
    }

    public static string GetCatalogPath()
    {
        var defaultPath = Path.Combine(Path.GetTempPath(), "mcpServerCatalog.json");
        var overridePath = Environment.GetEnvironmentVariable(CatalogPathEnvVar);
        if (string.IsNullOrWhiteSpace(overridePath))
        {
            return defaultPath;
        }

        // Normalize: env vars are sometimes set with surrounding whitespace or wrapping
        // quotes (e.g. `set VAR="C:\path\to\file.json"`), which would cause File.WriteAllText
        // and File.ReadAllText to fail downstream with a confusing path-not-found error.
        var trimmed = overridePath.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            trimmed = trimmed[1..^1];
        }

        // Re-validate after normalization: an override of `""` or `''` (or `"   "`)
        // strips to an empty string, which would make File.* throw with an unhelpful
        // "path is empty" exception. Treat any post-normalization empty/whitespace
        // value the same as a missing override and fall back to the default.
        return string.IsNullOrWhiteSpace(trimmed) ? defaultPath : trimmed;
    }
}