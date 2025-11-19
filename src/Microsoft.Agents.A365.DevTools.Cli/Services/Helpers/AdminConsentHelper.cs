// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Helper methods for admin consent flows that use az cli to poll Graph resources.
/// Kept intentionally small and focused so it can be reused across commands/runners.
/// </summary>
public static class AdminConsentHelper
{
    /// <summary>
    /// Polls Azure AD/Graph (via az rest) to detect an oauth2 permission grant for the provided appId.
    /// Mirrors the behavior previously implemented in A365SetupRunner.PollAdminConsentAsync.
    /// </summary>
    public static async Task<bool> PollAdminConsentAsync(
        CommandExecutor executor,
        ILogger logger,
        string appId,
        string scopeDescriptor,
        int timeoutSeconds,
        int intervalSeconds,
        CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        string? spId = null;

        while ((DateTime.UtcNow - start).TotalSeconds < timeoutSeconds && !ct.IsCancellationRequested)
        {
            if (spId == null)
            {
                var spResult = await executor.ExecuteAsync("az",
                    $"rest --method GET --url \"https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId eq '{appId}'\"",
                    captureOutput: true, suppressErrorLogging: true, cancellationToken: ct);

                if (spResult.Success)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(spResult.StandardOutput);
                        var value = doc.RootElement.GetProperty("value");
                        if (value.GetArrayLength() > 0)
                        {
                            spId = value[0].GetProperty("id").GetString();
                        }
                    }
                    catch { }
                }
            }

            if (spId != null)
            {
                var grants = await executor.ExecuteAsync("az",
                    $"rest --method GET --url \"https://graph.microsoft.com/v1.0/oauth2PermissionGrants?$filter=clientId eq '{spId}'\"",
                    captureOutput: true, suppressErrorLogging: true, cancellationToken: ct);

                if (grants.Success)
                {
                    try
                    {
                        using var gdoc = JsonDocument.Parse(grants.StandardOutput);
                        var arr = gdoc.RootElement.GetProperty("value");
                        if (arr.GetArrayLength() > 0)
                        {
                            logger.LogInformation("Consent granted ({ScopeDescriptor}).", scopeDescriptor);
                            return true;
                        }
                    }
                    catch { }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
        }

        return false;
    }
}
