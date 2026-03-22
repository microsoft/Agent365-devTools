// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Shared helper for invoking the Azure CLI and parsing its output.
/// Consolidates az CLI interactions to ensure consistent behavior across services.
/// </summary>
internal static class AzCliHelper
{
    // Process-level cache: 'az account show' returns the same user for the lifetime of a CLI
    // invocation. Caching eliminates repeated 20-40s subprocess calls that occur when multiple
    // services and commands each call ResolveLoginHintAsync independently.
    private static volatile Task<string?>? _cachedLoginHintTask;

    // Test seam: replace the underlying resolver without touching the cache layer.
    // Null in production. Tests set this to avoid spawning a real 'az' subprocess.
    internal static Func<Task<string?>>? LoginHintResolverOverride { get; set; }

    /// <summary>
    /// Resolves the currently signed-in Azure CLI user from 'az account show'.
    /// The result is cached for the process lifetime — the active account cannot change
    /// mid-execution of a single CLI command. Returns null if unavailable (non-fatal).
    /// </summary>
    internal static Task<string?> ResolveLoginHintAsync()
        => _cachedLoginHintTask ??= (LoginHintResolverOverride ?? ResolveLoginHintCoreAsync)();

    /// <summary>
    /// Clears the login-hint process-level cache after a fresh 'az login'.
    /// Forces the next call to ResolveLoginHintAsync to re-run 'az account show'.
    /// </summary>
    internal static void InvalidateLoginHintCache() => _cachedLoginHintTask = null;

    /// <summary>Clears the login-hint process-level cache. For use in tests only.</summary>
    internal static void ResetLoginHintCacheForTesting() => _cachedLoginHintTask = null;

    // -------------------------------------------------------------------------
    // az account get-access-token — process-level token cache
    // -------------------------------------------------------------------------
    // Tokens acquired via 'az account get-access-token' are valid for 60+ minutes.
    // Caching at the process level means a single CLI invocation only spawns one
    // subprocess per (resource, tenantId) pair, regardless of how many services or
    // commands request the same token. Expected savings: 40–60s per command run.

    private static readonly ConcurrentDictionary<string, Task<string?>> _azCliTokenCache = new();

    // Test seam: replace the underlying acquirer without touching the cache layer.
    // The override is invoked inside GetOrAdd, so the result is still cached after
    // the first call — only one invocation per cache key, even in tests.
    internal static Func<string, string, Task<string?>>? AzCliTokenAcquirerOverride { get; set; }

    /// <summary>
    /// Acquires an Azure CLI access token for the given resource and tenant.
    /// The result is cached for the process lifetime — a single CLI command cannot
    /// invalidate a token except through explicit re-authentication (az login).
    /// Call <see cref="InvalidateAzCliTokenCache"/> after 'az login' to bust the cache.
    /// </summary>
    internal static Task<string?> AcquireAzCliTokenAsync(string resource, string tenantId = "")
    {
        var key = $"{resource}::{tenantId}";
        return _azCliTokenCache.GetOrAdd(key, _ =>
            AzCliTokenAcquirerOverride != null
                ? AzCliTokenAcquirerOverride(resource, tenantId)
                : AcquireAzCliTokenCoreAsync(resource, tenantId));
    }

    /// <summary>
    /// Injects a token acquired via an alternative auth flow (e.g., after 'az login' recovery)
    /// so that subsequent callers across all services receive the fresh token from cache.
    /// </summary>
    internal static void WarmAzCliTokenCache(string resource, string tenantId, string token)
    {
        var key = $"{resource}::{tenantId}";
        _azCliTokenCache[key] = Task.FromResult<string?>(token);
    }

    /// <summary>
    /// Clears the token cache. Call after 'az login' or 'az logout' to ensure
    /// subsequent callers acquire a fresh token rather than a now-invalid cached one.
    /// </summary>
    internal static void InvalidateAzCliTokenCache() => _azCliTokenCache.Clear();

    /// <summary>Clears the token cache. For use in tests only.</summary>
    internal static void ResetAzCliTokenCacheForTesting() => _azCliTokenCache.Clear();

    private static async Task<string?> AcquireAzCliTokenCoreAsync(string resource, string tenantId)
    {
        try
        {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var tenantArg = string.IsNullOrEmpty(tenantId) ? "" : $" --tenant {tenantId}";
            var azArgs = $"account get-access-token --resource {resource}{tenantArg} --query accessToken -o tsv";
            var startInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "az",
                Arguments = isWindows ? $"/c az {azArgs}" : azArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process == null) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync();
            var output = outputTask.Result.Trim();
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch { }
        return null;
    }

    private static async Task<string?> ResolveLoginHintCoreAsync()
    {
        try
        {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var startInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "az",
                Arguments = isWindows ? "/c az account show" : "account show",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process == null) return null;
            // Read stdout and stderr concurrently to prevent the process from blocking
            // when either pipe's buffer fills up before WaitForExitAsync is called.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync();
            var output = outputTask.Result;
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                var cleaned = JsonDeserializationHelper.CleanAzureCliJsonOutput(output);
                var json = JsonSerializer.Deserialize<JsonElement>(cleaned);
                if (json.TryGetProperty("user", out var user) &&
                    user.TryGetProperty("name", out var name))
                    return name.GetString();
            }
        }
        catch { }
        return null;
    }
}
