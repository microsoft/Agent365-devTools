// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Checks NuGet for a newer version of the CLI and returns an update prompt when one is available.
/// Results are cached locally for <see cref="CacheTtlHours"/> hours to avoid a NuGet API call
/// on every invocation.
/// </summary>
public class VersionCheckService : IVersionCheckService
{
    private const string NuGetApiUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.agents.a365.devtools.cli/index.json";
    private const string CacheFileName = "version.cache.json";
    private const int CacheTtlHours = 24;

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
            cancellationToken.ThrowIfCancellationRequested();

            if (VersionCheckHelper.IsRunningInCiCd())
            {
                _logger.LogDebug("Skipping version check in CI/CD environment");
                return new VersionCheckResult(false, _currentVersion, null, null);
            }

            _logger.LogDebug("Checking for updates...");

            var (latestVersion, newerPreviewVersion) = GetCachedLatestVersion();
            if (latestVersion == null)
            {
                (latestVersion, newerPreviewVersion) = await GetLatestVersionFromNuGetAsync(cancellationToken);
                if (latestVersion != null)
                    SaveCache(new VersionCheckCache(DateTimeOffset.UtcNow, latestVersion, newerPreviewVersion));
            }

            if (latestVersion == null)
            {
                _logger.LogDebug("Could not retrieve latest version from NuGet");
                return new VersionCheckResult(false, _currentVersion, null, null);
            }

            var updateAvailable = IsNewerVersion(_currentVersion, latestVersion);

            if (updateAvailable)
                _logger.LogDebug("Update available: {Latest} (current: {Current})", latestVersion, _currentVersion);
            else
                _logger.LogDebug("Running latest version: {Current}", _currentVersion);

            return new VersionCheckResult(updateAvailable, _currentVersion, latestVersion,
                VersionCheckHelper.GetUpdateCommand(latestVersion), newerPreviewVersion);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Version check cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Version check failed: {Message}", ex.Message);
            return new VersionCheckResult(false, _currentVersion, null, null);
        }
    }

    private async Task<(string? Primary, string? NewerPreview)> GetLatestVersionFromNuGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(authToken: null);
            using var response = await httpClient.GetAsync(NuGetApiUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("NuGet API returned {StatusCode}", response.StatusCode);
                return (null, null);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var versionResponse = JsonSerializer.Deserialize<NuGetVersionResponse>(content, options);

            if (versionResponse?.Versions == null || versionResponse.Versions.Length == 0)
            {
                _logger.LogDebug("No versions found in NuGet response");
                return (null, null);
            }

            return VersionCheckHelper.SelectLatestVersions(versionResponse.Versions, _currentVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to query NuGet API");
            return (null, null);
        }
    }

    private bool IsNewerVersion(string current, string latest)
    {
        try
        {
            return VersionCheckHelper.ParseVersion(latest) > VersionCheckHelper.ParseVersion(current);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to compare versions: current={Current}, latest={Latest}", current, latest);
            return false;
        }
    }

    private (string? Primary, string? NewerPreview) GetCachedLatestVersion()
    {
        try
        {
            var path = GetCacheFilePath();
            if (!File.Exists(path))
                return (null, null);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var cache = JsonSerializer.Deserialize<VersionCheckCache>(File.ReadAllText(path), options);

            if (cache == null)
                return (null, null);

            if (DateTimeOffset.UtcNow - cache.CachedAt >= TimeSpan.FromHours(CacheTtlHours))
            {
                _logger.LogDebug("Version cache expired (cached at {CachedAt})", cache.CachedAt);
                return (null, null);
            }

            _logger.LogDebug("Using cached version {Version} (cached at {CachedAt})", cache.LatestVersion, cache.CachedAt);
            return (cache.LatestVersion, cache.NewerPreviewVersion);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load version cache");
            return (null, null);
        }
    }

    private void SaveCache(VersionCheckCache cache)
    {
        try
        {
            var path = GetCacheFilePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not save version cache");
        }
    }

    /// <summary>
    /// Returns the path to the on-disk version cache file.
    /// </summary>
    internal static string GetCacheFilePath()
        => Path.Combine(ConfigService.GetGlobalConfigDirectory(), CacheFileName);
}
