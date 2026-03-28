// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Notice fetched from the server-side notices endpoint.
/// All fields are nullable — an empty or partial response means no active notice.
/// </summary>
public record Notice(
    string? Message,
    string? MinimumVersion,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Result of a notice check — what the caller acts on.
/// </summary>
public record NoticeResult(bool HasNotice, string? Message, string? UpdateCommand);

/// <summary>
/// On-disk cache envelope for the notice, keyed by fetch timestamp.
/// </summary>
public record NoticeCache(DateTimeOffset CachedAt, Notice? ActiveNotice);
