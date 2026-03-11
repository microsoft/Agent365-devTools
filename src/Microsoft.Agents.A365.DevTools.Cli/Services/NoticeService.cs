// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Checks for an active notice posted to the server-side notices endpoint.
/// Results are cached locally for <see cref="CacheTtlHours"/> hours to avoid a network
/// call on every CLI invocation.
/// </summary>
public class NoticeService : INoticeService
{
    private const string NoticesUrl = "https://raw.githubusercontent.com/microsoft/Agent365-devTools/main/notices.json";
    private const string CacheFileName = "notice.cache.json";
    private const int CacheTtlHours = 4;

    private readonly ILogger<NoticeService> _logger;
    private readonly string _currentVersion;

    public NoticeService(ILogger<NoticeService> logger)
    {
        _logger = logger;
        _currentVersion = Program.GetDisplayVersion();
    }

    /// <inheritdoc />
    public async Task<NoticeResult> CheckForNoticeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (VersionCheckHelper.IsRunningInCiCd())
            {
                _logger.LogDebug("Skipping notice check in CI/CD environment");
                return new NoticeResult(false, null, null);
            }

            var notice = await GetNoticeWithCacheAsync(cancellationToken);

            if (notice == null || string.IsNullOrWhiteSpace(notice.Message))
                return new NoticeResult(false, null, null);

            if (notice.ExpiresAt.HasValue && notice.ExpiresAt.Value <= DateTimeOffset.UtcNow)
            {
                _logger.LogDebug("Notice expired at {ExpiresAt}", notice.ExpiresAt);
                return new NoticeResult(false, null, null);
            }

            // If the user is already on a version that meets the minimum, suppress the notice
            if (!string.IsNullOrWhiteSpace(notice.MinimumVersion))
            {
                try
                {
                    var current = VersionCheckHelper.ParseVersion(_currentVersion);
                    var minimum = VersionCheckHelper.ParseVersion(notice.MinimumVersion);
                    if (current >= minimum)
                    {
                        _logger.LogDebug("Current version {Current} meets minimum {Minimum}; notice suppressed", _currentVersion, notice.MinimumVersion);
                        return new NoticeResult(false, null, null);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Version comparison failed; showing notice as a precaution");
                }
            }

            // Use minimumVersion to determine --prerelease flag when specified;
            // fall back to current version (e.g. notice has no minimumVersion).
            var versionForCommand = string.IsNullOrWhiteSpace(notice.MinimumVersion)
                ? _currentVersion
                : notice.MinimumVersion;
            var updateCommand = VersionCheckHelper.GetUpdateCommand(versionForCommand);

            return new NoticeResult(true, notice.Message, updateCommand);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Notice check cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Notice check failed: {Message}", ex.Message);
            return new NoticeResult(false, null, null);
        }
    }

    private async Task<Notice?> GetNoticeWithCacheAsync(CancellationToken cancellationToken)
    {
        var cacheFilePath = GetCacheFilePath();
        var cached = TryLoadCache(cacheFilePath);

        if (cached != null && DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromHours(CacheTtlHours))
        {
            _logger.LogDebug("Using cached notice (cached at {CachedAt})", cached.CachedAt);
            return cached.ActiveNotice;
        }

        var notice = await FetchFromServerAsync(cancellationToken);
        SaveCache(cacheFilePath, new NoticeCache(DateTimeOffset.UtcNow, notice));
        return notice;
    }

    private async Task<Notice?> FetchFromServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = HttpClientFactory.CreateAuthenticatedClient(authToken: null);
            using var response = await httpClient.GetAsync(NoticesUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Notices endpoint returned {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<Notice>(content, options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to fetch notices from server");
            return null;
        }
    }

    private NoticeCache? TryLoadCache(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<NoticeCache>(File.ReadAllText(path), options);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load notice cache from {Path}", path);
            return null;
        }
    }

    private void SaveCache(string path, NoticeCache cache)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not save notice cache to {Path}", path);
        }
    }

    /// <summary>
    /// Returns the path to the on-disk notice cache file.
    /// </summary>
    internal static string GetCacheFilePath()
        => Path.Combine(ConfigService.GetGlobalConfigDirectory(), CacheFileName);
}
