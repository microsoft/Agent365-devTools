// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Shared helper for invoking the Azure CLI and parsing its output.
/// Used for login-hint resolution (az account show) and az CLI command execution.
/// Token acquisition has been moved to AuthenticationService (MSAL/WAM) — this
/// helper no longer acquires tokens directly.
/// </summary>
internal static class AzCliHelper
{
    // Process-level cache: 'az account show' returns the same user for the lifetime of a CLI
    // invocation. Caching eliminates repeated subprocess calls that occur when multiple
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

    private static async Task<string?> ResolveLoginHintCoreAsync()
    {
        try
        {
            // On Windows az is az.cmd and requires cmd.exe. Arguments are passed via
            // ArgumentList rather than string interpolation to avoid shell-injection risk.
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var startInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "az",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (isWindows)
            {
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add("az");
            }
            startInfo.ArgumentList.Add("account");
            startInfo.ArgumentList.Add("show");

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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch { }
        return null;
    }
}
