// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.MockToolingServer.MockTools;

public class FileMockToolStore : IMockToolStore, IDisposable
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1,1);
    private readonly FileSystemWatcher? _watcher;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly ConcurrentDictionary<string, MockToolDefinition> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string McpServerName { get; }

    // Modified: now requires mcpServerName to determine file name (<mcpServerName>.json)
    public FileMockToolStore(string mcpServerName, MockToolStoreOptions options)
    {
        if (string.IsNullOrWhiteSpace(mcpServerName)) throw new ArgumentException("mcpServerName required", nameof(mcpServerName));

        McpServerName = mcpServerName;

        // Sanitize server name for file system
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string(mcpServerName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

        _filePath = options.FilePath ?? Path.Combine(AppContext.BaseDirectory, "mocks", safeName + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        if (!File.Exists(_filePath))
        {
            File.WriteAllText(_filePath, "[]");
        }
        LoadInternal();

        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(_filePath)!)
            {
                Filter = Path.GetFileName(_filePath),
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            _watcher.Changed += async (_, __) => await SafeReload();
            _watcher.Created += async (_, __) => await SafeReload();
            _watcher.Renamed += async (_, __) => await SafeReload();
        }
        catch
        {
        }
    }

    private async Task SafeReload()
    {
        try { await ReloadAsync(); } catch { }
    }

    private void LoadInternal()
    {
        var json = File.ReadAllText(_filePath);
        var list = JsonSerializer.Deserialize<List<MockToolDefinition>>(json, _jsonOptions) ?? new();
        _cache.Clear();
        foreach(var t in list)
        {
            if (!string.IsNullOrWhiteSpace(t.Name))
            {
                _cache[t.Name] = t;
            }
        }
    }

    private async Task PersistAsync()
    {
        var list = _cache.Values.OrderBy(v => v.Name).ToList();
        var json = JsonSerializer.Serialize(list, _jsonOptions);
        await File.WriteAllTextAsync(_filePath, json);
    }

    public async Task<IReadOnlyList<MockToolDefinition>> ListAsync()
    {
        await Task.CompletedTask;
        return _cache.Values.OrderBy(v => v.Name).ToList();
    }

    public async Task<MockToolDefinition?> GetAsync(string name)
    {
        await Task.CompletedTask;
        _cache.TryGetValue(name, out var def);
        return def;
    }

    public async Task UpsertAsync(MockToolDefinition def)
    {
        if (string.IsNullOrWhiteSpace(def.Name)) throw new ArgumentException("Tool name required");
        await _lock.WaitAsync();
        try
        {
            _cache[def.Name] = def;
            await PersistAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string name)
    {
        await _lock.WaitAsync();
        try
        {
            var removed = _cache.TryRemove(name, out _);
            if (removed)
            {
                await PersistAsync();
            }
            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ReloadAsync()
    {
        await _lock.WaitAsync();
        try { LoadInternal(); }
        finally { _lock.Release(); }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _lock.Dispose();
    }
}
