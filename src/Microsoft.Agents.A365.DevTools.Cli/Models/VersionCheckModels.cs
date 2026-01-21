// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Response from NuGet V3 API containing available versions.
/// </summary>
public record NuGetVersionResponse(string[] Versions);

/// <summary>
/// Result of a version check operation.
/// </summary>
public record VersionCheckResult(
    bool UpdateAvailable,
    string? CurrentVersion,
    string? LatestVersion,
    string? UpdateCommand);
