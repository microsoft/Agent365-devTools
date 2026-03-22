// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for Azure Resource Manager (ARM) existence checks via direct HTTP.
/// Replaces subprocess-based 'az group exists', 'az appservice plan show', and
/// 'az webapp show' calls — each drops from ~15-20s to ~0.5s.
/// Token acquisition is handled by AzCliHelper (process-level cache shared with
/// other services using the management endpoint).
/// </summary>
public class ArmApiService : IDisposable
{
    private const string ArmBaseUrl = "https://management.azure.com";
    internal const string ArmResource = "https://management.core.windows.net/";
    private const string ResourceGroupApiVersion = "2021-04-01";
    private const string AppServiceApiVersion = "2022-03-01";

    private readonly ILogger<ArmApiService> _logger;
    private readonly HttpClient _httpClient;

    // Allow injecting a custom HttpMessageHandler for unit testing.
    public ArmApiService(ILogger<ArmApiService> logger, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _httpClient = handler != null ? new HttpClient(handler) : HttpClientFactory.CreateAuthenticatedClient();
    }

    // Parameterless constructor to ease test mocking/substitution frameworks.
    public ArmApiService()
        : this(NullLogger<ArmApiService>.Instance, null)
    {
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<bool> EnsureArmHeadersAsync(string tenantId, CancellationToken ct)
    {
        var token = await AzCliHelper.AcquireAzCliTokenAsync(ArmResource, tenantId);
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("Unable to acquire ARM access token for tenant {TenantId}", tenantId);
            return false;
        }
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.ReplaceLineEndings(string.Empty).Trim());
        return true;
    }

    /// <summary>
    /// Checks whether a resource group exists in the given subscription.
    /// Returns null if the ARM token cannot be acquired (caller should fall back to az CLI).
    /// </summary>
    public virtual async Task<bool?> ResourceGroupExistsAsync(
        string subscriptionId,
        string resourceGroup,
        string tenantId,
        CancellationToken ct = default)
    {
        if (!await EnsureArmHeadersAsync(tenantId, ct))
            return null;

        var url = $"{ArmBaseUrl}/subscriptions/{subscriptionId}/resourcegroups/{resourceGroup}?api-version={ResourceGroupApiVersion}";
        _logger.LogDebug("ARM GET resource group: {ResourceGroup}", resourceGroup);

        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            _logger.LogDebug("ARM resource group check: {StatusCode}", response.StatusCode);
            if (response.StatusCode == HttpStatusCode.OK) return true;
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            return null; // 401/403/5xx — caller falls back to az CLI
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ARM resource group check failed — will fall back to az CLI");
            return null;
        }
    }

    /// <summary>
    /// Checks whether an App Service plan exists.
    /// Returns null if the ARM token cannot be acquired.
    /// </summary>
    public virtual async Task<bool?> AppServicePlanExistsAsync(
        string subscriptionId,
        string resourceGroup,
        string planName,
        string tenantId,
        CancellationToken ct = default)
    {
        if (!await EnsureArmHeadersAsync(tenantId, ct))
            return null;

        var url = $"{ArmBaseUrl}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Web/serverfarms/{planName}?api-version={AppServiceApiVersion}";
        _logger.LogDebug("ARM GET app service plan: {PlanName}", planName);

        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            _logger.LogDebug("ARM app service plan check: {StatusCode}", response.StatusCode);
            if (response.StatusCode == HttpStatusCode.OK) return true;
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            return null; // 401/403/5xx — caller falls back to az CLI
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ARM app service plan check failed — will fall back to az CLI");
            return null;
        }
    }

    /// <summary>
    /// Checks whether a web app exists.
    /// Returns null if the ARM token cannot be acquired.
    /// </summary>
    public virtual async Task<bool?> WebAppExistsAsync(
        string subscriptionId,
        string resourceGroup,
        string webAppName,
        string tenantId,
        CancellationToken ct = default)
    {
        if (!await EnsureArmHeadersAsync(tenantId, ct))
            return null;

        var url = $"{ArmBaseUrl}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Web/sites/{webAppName}?api-version={AppServiceApiVersion}";
        _logger.LogDebug("ARM GET web app: {WebAppName}", webAppName);

        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            _logger.LogDebug("ARM web app check: {StatusCode}", response.StatusCode);
            if (response.StatusCode == HttpStatusCode.OK) return true;
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            return null; // 401/403/5xx — caller falls back to az CLI
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ARM web app check failed — will fall back to az CLI");
            return null;
        }
    }

    // Built-in Azure RBAC role definition GUIDs (stable across all tenants/subscriptions).
    private static readonly Dictionary<string, string> RoleGuidToName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["8e3af657-a8ff-443c-a75c-2fe8c4bcb635"] = "Owner",
        ["b24988ac-6180-42a0-ab88-20f7382dd24c"] = "Contributor",
        ["de139f84-1756-47ae-9be6-808fbbe84772"] = "Website Contributor",
    };

    /// <summary>
    /// Checks whether the user already has a sufficient Azure RBAC role (Owner, Contributor, or
    /// Website Contributor) on the web app or any parent scope (resource group / subscription).
    /// Replaces 'az role assignment list --assignee ... --include-inherited' (~35s) with a
    /// direct ARM HTTP call (~300ms).
    ///
    /// Returns: non-empty role name if found, empty string if not found,
    ///          null if the HTTP call fails (caller should fall back to az CLI or attempt assignment).
    /// </summary>
    public virtual async Task<string?> GetSufficientWebAppRoleAsync(
        string subscriptionId,
        string resourceGroup,
        string webAppName,
        string userObjectId,
        string tenantId,
        CancellationToken ct = default)
    {
        if (!await EnsureArmHeadersAsync(tenantId, ct))
            return null;

        var webAppScope = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Web/sites/{webAppName}";
        var url = $"{ArmBaseUrl}/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleAssignments" +
                  $"?api-version=2022-04-01&$filter=assignedTo('{userObjectId}')";
        _logger.LogDebug("ARM GET role assignments for user {UserId} in subscription {Sub}", userObjectId, subscriptionId);

        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("ARM role assignment check returned {StatusCode}", response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("value", out var assignments))
                return string.Empty;

            foreach (var assignment in assignments.EnumerateArray())
            {
                if (!assignment.TryGetProperty("properties", out var props)) continue;

                var scope = props.TryGetProperty("scope", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                var roleDefId = props.TryGetProperty("roleDefinitionId", out var r) ? r.GetString() ?? string.Empty : string.Empty;

                // Scope must be at or above the web app in the hierarchy for inheritance to apply.
                if (!webAppScope.StartsWith(scope, StringComparison.OrdinalIgnoreCase)) continue;

                // Extract the GUID from the full role definition resource ID.
                var roleGuid = roleDefId.Contains('/') ? roleDefId[(roleDefId.LastIndexOf('/') + 1)..] : roleDefId;
                if (RoleGuidToName.TryGetValue(roleGuid, out var roleName))
                    return roleName;
            }

            return string.Empty; // Authenticated successfully, no sufficient role found
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ARM role assignment check failed — will fall back to az CLI");
            return null;
        }
    }
}
