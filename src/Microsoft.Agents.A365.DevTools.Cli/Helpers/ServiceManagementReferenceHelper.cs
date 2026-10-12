// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Validates and resolves the serviceManagementReference value sent when a blueprint is created.
/// </summary>
internal static class ServiceManagementReferenceHelper
{
    /// <summary>
    /// Returns the canonical GUID form of <paramref name="value"/>; false for blank, non-GUID, or all-zero values.
    /// </summary>
    internal static bool TryNormalize(string? value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value.Trim(), out var guid) || guid == Guid.Empty)
            return false;

        normalized = guid.ToString("D");
        return true;
    }

    /// <summary>
    /// The --service-management-reference value wins over serviceManagementReference in config; null when neither is usable.
    /// </summary>
    internal static string? Resolve(string? flagValue, Agent365Config? config)
    {
        if (TryNormalize(flagValue, out var fromFlag))
            return fromFlag;

        return TryNormalize(config?.ServiceManagementReference, out var fromConfig) ? fromConfig : null;
    }
}
