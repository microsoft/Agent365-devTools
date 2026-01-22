// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for checking if newer versions of the CLI are available on NuGet.
/// </summary>
public class VersionCheckService : IVersionCheckService
{
    private const string NuGetApiUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.agents.a365.devtools.cli/index.json";
    private const string PackageId = "Microsoft.Agents.A365.DevTools.Cli";

    private readonly ILogger<VersionCheckService> _logger;
    private readonly string _currentVersion;

    public VersionCheckService(ILogger<VersionCheckService> logger)
    {
        _logger = logger;
        _currentVersion = Program.GetDisplayVersion();
    }

    /// <inheritdoc />
    public async Task<VersionCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Skip check in CI/CD environments
            if (IsRunningInCiCd())
            {
                _logger.LogDebug("Skipping version check in CI/CD environment");
                return new VersionCheckResult(false, _currentVersion, null, null);
            }

            _logger.LogDebug("Checking for updates from NuGet...");

            // Query NuGet API for available versions
            var latestVersion = await GetLatestVersionFromNuGetAsync(cancellationToken);

            if (latestVersion == null)
            {
                _logger.LogDebug("Could not retrieve latest version from NuGet");
                return new VersionCheckResult(false, _currentVersion, null, null);
            }

            // Compare versions
            var updateAvailable = IsNewerVersion(_currentVersion, latestVersion);

            if (updateAvailable)
            {
                _logger.LogDebug("Update available: {LatestVersion} (current: {CurrentVersion})", latestVersion, _currentVersion);
            }
            else
            {
                _logger.LogDebug("Running latest version: {CurrentVersion}", _currentVersion);
            }

            // Generate update command based on whether the latest version is a preview
            var updateCommand = GetUpdateCommand(latestVersion);

            return new VersionCheckResult(updateAvailable, _currentVersion, latestVersion, updateCommand);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Version check cancelled (timeout or user requested)");
            throw; // Re-throw to let caller handle timeout
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Version check failed: {Message}", ex.Message);
            return new VersionCheckResult(false, _currentVersion, null, null);
        }
    }

    /// <summary>
    /// Queries the NuGet V3 API to get the latest version of the package.
    /// </summary>
    /// <remarks>
    /// Uses HttpClientFactory.CreateAuthenticatedClient, which is the established pattern
    /// in this codebase for creating HTTP clients. This is a static factory method that
    /// properly configures and returns a new HttpClient instance.
    /// </remarks>
    private async Task<string?> GetLatestVersionFromNuGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(authToken: null);

            using var response = await httpClient.GetAsync(NuGetApiUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("NuGet API returned status code: {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var versionResponse = JsonSerializer.Deserialize<NuGetVersionResponse>(content, options);
            
            if (versionResponse?.Versions == null || versionResponse.Versions.Length == 0)
            {
                _logger.LogDebug("No versions found in NuGet response");
                return null;
            }

            // Sort versions semantically and return the latest
            // NuGet API typically returns versions in chronological order, but we sort to be safe
            var sortedVersions = versionResponse.Versions
                .Select(v => new { Original = v, Parsed = TryParseVersion(v) })
                .Where(v => v.Parsed != null)
                .OrderByDescending(v => v.Parsed)
                .ToList();

            if (sortedVersions.Count == 0)
            {
                _logger.LogDebug("No valid versions found in NuGet response");
                return null;
            }

            return sortedVersions[0].Original;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to query NuGet API");
            return null;
        }
    }

    /// <summary>
    /// Compares two semantic versions to determine if the latest is newer than current.
    /// </summary>
    /// <param name="currentVersion">Current version string.</param>
    /// <param name="latestVersion">Latest version string from NuGet.</param>
    /// <returns>True if latest is newer than current.</returns>
    private bool IsNewerVersion(string currentVersion, string latestVersion)
    {
        try
        {
            // Parse semantic versions
            var current = ParseVersion(currentVersion);
            var latest = ParseVersion(latestVersion);

            // Compare versions
            return latest > current;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to compare versions: current={Current}, latest={Latest}", currentVersion, latestVersion);
            return false;
        }
    }

    /// <summary>
    /// Parses a semantic version string into a comparable Version object.
    /// Handles formats like "1.1.0-preview.123+git.hash".
    /// </summary>
    internal Version ParseVersion(string versionString)
    {
        var parsed = TryParseVersion(versionString);
        if (parsed == null)
        {
            throw new FormatException($"Invalid version format: {versionString}");
        }
        return parsed;
    }

    /// <summary>
    /// Tries to parse a semantic version string into a comparable Version object.
    /// Returns null if parsing fails.
    ///
    /// Note: This parsing treats preview versions as comparable by their preview number.
    /// Handles two formats:
    /// - "1.1.52-preview" (version number includes preview iteration)
    /// - "1.1.0-preview.50" (preview number is separate)
    /// </summary>
    private Version? TryParseVersion(string versionString)
    {
        try
        {
            // Remove any build metadata (+...)
            var cleanVersion = versionString.Split('+')[0];

            // For preview versions, extract the version number
            if (cleanVersion.Contains('-'))
            {
                var parts = cleanVersion.Split('-');
                var baseVersion = parts[0]; // e.g., "1.1.52" or "1.1.0"

                // Check if there's a preview number after the dash
                if (parts.Length > 1)
                {
                    var previewPart = parts[1]; // e.g., "preview" or "preview.50"

                    // Format 1: "1.1.0-preview.50" - append preview number as revision
                    if (previewPart.StartsWith("preview.") && previewPart.Length > 8)
                    {
                        var previewNumber = previewPart.Substring(8); // Get number after "preview."
                        cleanVersion = int.TryParse(previewNumber, out var preview)
                            ? $"{baseVersion}.{preview}"
                            : baseVersion; // If parsing fails, just use base version
                    }
                    // Format 2: "1.1.52-preview" - version already includes iteration number
                    else
                    {
                        cleanVersion = baseVersion;
                    }
                }
                else
                {
                    cleanVersion = baseVersion;
                }
            }

            // Ensure we have at least 3 components for Version constructor
            var versionParts = cleanVersion.Split('.');
            var componentsNeeded = 3 - versionParts.Length;
            for (var i = 0; i < componentsNeeded; i++)
            {
                cleanVersion += ".0";
            }

            return new Version(cleanVersion);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Generates the appropriate update command based on the version type.
    /// </summary>
    /// <param name="version">The version string to check for preview status.</param>
    /// <returns>The dotnet tool update command with or without --prerelease flag.</returns>
    private static string GetUpdateCommand(string version)
    {
        var baseCommand = $"dotnet tool update -g {PackageId}";

        // If the version contains "preview", add the --prerelease flag
        if (version.Contains("preview", StringComparison.OrdinalIgnoreCase))
        {
            return $"{baseCommand} --prerelease";
        }

        return baseCommand;
    }

    /// <summary>
    /// Detects if the CLI is running in a CI/CD environment.
    /// </summary>
    private static bool IsRunningInCiCd()
    {
        // Common CI/CD environment variables
        var ciEnvVars = new[]
        {
            "CI",                    // Generic CI indicator
            "TF_BUILD",              // Azure DevOps
            "GITHUB_ACTIONS",        // GitHub Actions
            "JENKINS_HOME",          // Jenkins
            "GITLAB_CI",             // GitLab CI
            "CIRCLECI",              // CircleCI
            "TRAVIS",                // Travis CI
            "TEAMCITY_VERSION",      // TeamCity
            "BUILDKITE",             // Buildkite
            "CODEBUILD_BUILD_ID"     // AWS CodeBuild
        };

        return ciEnvVars.Any(envVar => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)));
    }
}
