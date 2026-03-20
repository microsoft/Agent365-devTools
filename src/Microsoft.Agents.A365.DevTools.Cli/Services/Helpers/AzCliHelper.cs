// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
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
    /// <summary>
    /// Resolves the currently signed-in Azure CLI user from 'az account show'.
    /// Returns null if az CLI is unavailable or the user field is absent (non-fatal).
    /// </summary>
    internal static async Task<string?> ResolveLoginHintAsync()
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
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
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
