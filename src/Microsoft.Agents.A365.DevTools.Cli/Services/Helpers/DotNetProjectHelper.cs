// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Helper class for inspecting .NET project metadata.
/// </summary>
public static class DotNetProjectHelper
{
    /// <summary>
    /// Detects the target .NET runtime version (e.g. "8.0", "9.0") from a .csproj file.
    /// Supports both TargetFramework and TargetFrameworks.
    /// </summary>
    /// <param name="projectFilePath">The full path to the .csproj file</param>
    /// <param name="logger">Logger for diagnostic messages</param>
    /// <returns>
    /// The detected .NET version (e.g., "8.0", "9.0"), or null if:
    /// - The file doesn't exist
    /// - No TargetFramework element is found
    /// - The TFM format is not recognized (only supports "netX.Y" format)
    /// When multiple TFMs are specified, returns the first one.
    /// It does NOT support legacy or library-only TFMs and Unsupported TFMs return null and fall back to default runtime selection.
    /// </returns>
    public static string? DetectTargetRuntimeVersion(string projectFilePath, ILogger logger)
    {
        if (!File.Exists(projectFilePath))
        {
            logger.LogWarning("Project file not found: {Path}", projectFilePath);
            return null;
        }

        var content = File.ReadAllText(projectFilePath);

        // Match <TargetFramework> or <TargetFrameworks>
        var tfmMatch = Regex.Match(
            content,
            @"<TargetFrameworks?>\s*([^<]+)\s*</TargetFrameworks?>",
            RegexOptions.IgnoreCase);

        if (!tfmMatch.Success)
        {
            logger.LogWarning("No TargetFramework(s) found in project file: {Path}", projectFilePath);
            return null;
        }

        // If multiple TFMs are specified, pick the first one
        // (future improvement: pick highest)
        var tfms = tfmMatch.Groups[1].Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var tfm = tfms.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(tfm))
        {
            return null;
        }

        // Match net8.0, net9.0, net9.0-windows, etc.
        var verMatch = Regex.Match(
            tfm,
            @"net(\d+)\.(\d+)",
            RegexOptions.IgnoreCase);

        if (!verMatch.Success)
        {
            logger.LogWarning("Unrecognized TargetFramework format: {Tfm}", tfm);
            return null;
        }

        var version = $"{verMatch.Groups[1].Value}.{verMatch.Groups[2].Value}";
        logger.LogInformation(
            "Detected TargetFramework: {Tfm} → .NET {Version}",
            tfm,
            version);

        return version;
    }
}
