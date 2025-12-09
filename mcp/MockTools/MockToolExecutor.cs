// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.MockToolingServer.MockTools;

public interface IMockToolExecutor
{
    Task<object> ListToolsAsync(string mcpServerName);
    Task<object> CallToolAsync(string mcpServerName, string name, IDictionary<string, object>? arguments);
}

public class MockToolExecutor : IMockToolExecutor
{
    private readonly IReadOnlyList<IMockToolStore> _stores;
    private readonly Random _rng = new();
    private static readonly Regex PlaceholderRegex = new("{{(.*?)}}", RegexOptions.Compiled);

    // Default template constant so we can detect when user has not supplied one
    private const string DefaultTemplate = "Mock response from {{name}}";

    public MockToolExecutor(IEnumerable<IMockToolStore> stores)
    {
        _stores = stores?.ToList() ?? throw new ArgumentNullException(nameof(stores));
    }

    private IMockToolStore GetStore(string mcpServerName)
    {
        var store = _stores.FirstOrDefault(s => string.Equals(s.McpServerName, mcpServerName, StringComparison.OrdinalIgnoreCase));
        if (store == null)
        {
            throw new ArgumentException($"No mock tool store found for MCP server '{mcpServerName}'", nameof(mcpServerName));
        }
        return store;
    }

    public async Task<object> ListToolsAsync(string mcpServerName)
    {
        var store = GetStore(mcpServerName);
        var tools = await store.ListAsync();
        return new
        {
            tools = tools.Where(t => t.Enabled).Select(t => new
            {
                name = t.Name,
                description = t.Description,
                responseTemplate = t.ResponseTemplate,
                placeholders = ExtractPlaceholders(t.ResponseTemplate),
                inputSchema = t.InputSchema,
            })
        };
    }

    public async Task<object> CallToolAsync(string mcpServerName, string name, IDictionary<string, object>? arguments)
    {
        var store = GetStore(mcpServerName);
        var tool = await store.GetAsync(name);
        if (tool == null || !tool.Enabled)
        {
            return new
            {
                error = new { code = 404, message = $"Mock tool '{name}' not found" }
            };
        }

        if (tool.ErrorRate > 0 && _rng.NextDouble() < tool.ErrorRate)
        {
            return new
            {
                error = new { code = 500, message = $"Simulated error for mock tool '{name}'" }
            };
        }

        if (tool.DelayMs > 0)
        {
            await Task.Delay(tool.DelayMs);
        }

        // Allow callers to override the template at call time when the saved definition still uses the default
        // This enables quick ad-hoc mocked responses without redefining the tool.
        var effectiveTemplate = tool.ResponseTemplate;
        if (string.Equals(effectiveTemplate, DefaultTemplate, StringComparison.Ordinal) && arguments != null)
        {
            string? overrideValue = FindDynamicTemplate(arguments);
            if (!string.IsNullOrWhiteSpace(overrideValue))
            {
                effectiveTemplate = overrideValue!;
            }
        }

        var rendered = RenderTemplate(effectiveTemplate, arguments ?? new Dictionary<string, object>());

        return new
        {
            content = new[] { new { type = "text", text = rendered } },
            isMock = true,
            tool = name,
            usedArguments = arguments,
            template = tool.ResponseTemplate,
            missingPlaceholders = ExtractPlaceholders(effectiveTemplate).Where(p => arguments == null || !arguments.Keys.Any(k => string.Equals(k, p, StringComparison.OrdinalIgnoreCase))).ToArray()
        };
    }

    private static string? FindDynamicTemplate(IDictionary<string, object> args)
    {
        var candidateKeys = new[] { "responseTemplate", "response", "mockResponse", "text", "value", "output" };
        foreach (var key in candidateKeys)
        {
            var match = args.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key))
            {
                var val = match.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        return null;
    }

    private static string[] ExtractPlaceholders(string template)
        => string.IsNullOrWhiteSpace(template)
            ? Array.Empty<string>()
            : PlaceholderRegex.Matches(template)
                .Select(m => m.Groups[1].Value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string RenderTemplate(string template, IDictionary<string, object> args)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        // Build case-insensitive lookup (last write wins)
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in args)
        {
            var value = kvp.Value?.ToString() ?? string.Empty;
            lookup[kvp.Key] = value;
            var lower = kvp.Key.ToLowerInvariant();
            if (!lookup.ContainsKey(lower)) lookup[lower] = value;
        }

        return PlaceholderRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value.Trim();
            if (lookup.TryGetValue(key, out var val)) return val;
            // Keep placeholder visible if missing so user/LLM knows what to supply
            return match.Value;
        });
    }
}
