// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Validation;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Validates that the agent blueprint is registered in Microsoft Entra ID.
/// Checks that the blueprint application exists, has a service principal,
/// (if configured) has an agent registration, and has inheritable permissions configured.
/// Expected permissions come from a static baseline plus the tooling manifest (if present).
/// Uses the same Graph API methods as <c>query-entra</c>.
/// </summary>
public class BlueprintRegistrationRequirementCheck : RequirementCheck
{
    private readonly GraphApiService _graphApiService;
    private readonly AgentBlueprintService? _blueprintService;

    /// <summary>
    /// Static baseline permissions required for every agent blueprint.
    /// </summary>
    internal static readonly List<(string ResourceAppId, string ResourceName, string[] Scopes)> BaselinePermissions =
    [
        (ConfigConstants.ObservabilityApiAppId, "Agent365 Observability",
            new[] { ConfigConstants.ObservabilityApiOtelWriteScope }),
        (AuthenticationConstants.MicrosoftGraphResourceAppId, "Microsoft Graph",
            ConfigConstants.DefaultAgentApplicationScopes.ToArray()),
        (PowerPlatformConstants.PowerPlatformApiResourceAppId, "Power Platform API",
            new[] { PowerPlatformConstants.PermissionNames.ConnectivityConnectionsRead }),
        (McpConstants.WorkIQToolsProdAppId, "Work IQ Tools",
            new[] { "McpServersMetadata.Read.All" }),
    ];

    public BlueprintRegistrationRequirementCheck(GraphApiService graphApiService, AgentBlueprintService? blueprintService = null)
    {
        _graphApiService = graphApiService ?? throw new ArgumentNullException(nameof(graphApiService));
        _blueprintService = blueprintService;
    }

    /// <inheritdoc />
    public override string Name => "Blueprint Registration";

    /// <inheritdoc />
    public override string Description => "Validates that the agent blueprint is registered in Microsoft Entra ID";

    /// <inheritdoc />
    public override string Category => "Registration";

    /// <inheritdoc />
    public override async Task<RequirementCheckResult> CheckAsync(
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteCheckWithLoggingAsync(config, logger, CheckImplementationAsync, cancellationToken);
    }

    private async Task<RequirementCheckResult> CheckImplementationAsync(
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.AgentBlueprintId))
        {
            return RequirementCheckResult.Failure(
                "Agent blueprint ID not found in configuration",
                "Run 'a365 setup blueprint' to create and register a blueprint.",
                details: "The agentBlueprintId must be set in a365.generated.config.json before registration can be verified.");
        }

        if (string.IsNullOrWhiteSpace(config.TenantId))
        {
            return RequirementCheckResult.Failure(
                "Tenant ID not found in configuration",
                "Run 'a365 setup all' to configure your tenant ID.",
                details: "The tenantId must be set in a365.config.json before registration can be verified.");
        }

        var blueprintId = config.AgentBlueprintId;
        var tenantId = config.TenantId;

        // Check 1: Blueprint application exists in Entra
        bool appExists;
        try
        {
            appExists = await _graphApiService.ApplicationExistsByAppIdAsync(tenantId, blueprintId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to query Entra for blueprint application");
            return RequirementCheckResult.Warning(
                "Could not verify blueprint application in Entra ID",
                details: $"Graph API query failed: {ex.Message}. Ensure you are authenticated with 'az login'.");
        }

        if (!appExists)
        {
            var result = RequirementCheckResult.Failure(
                $"Blueprint application '{blueprintId}' not found in Entra ID",
                "Run 'a365 setup blueprint' to create the blueprint application, or verify the agentBlueprintId in your configuration.",
                details: $"No Entra application with appId '{blueprintId}' exists in tenant '{tenantId}'.");
            result.Metadata = new RequirementCheckMetadata { AppExists = false };
            return result;
        }

        // Check 2: Service principal exists for the blueprint
        string? servicePrincipalId;
        try
        {
            servicePrincipalId = await _graphApiService.LookupServicePrincipalByAppIdAsync(tenantId, blueprintId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to query Entra for blueprint service principal");
            var result = RequirementCheckResult.Warning(
                "Blueprint application exists but could not verify service principal",
                details: $"Graph API query failed: {ex.Message}");
            result.Metadata = new RequirementCheckMetadata { AppExists = true };
            return result;
        }

        if (string.IsNullOrEmpty(servicePrincipalId))
        {
            var result = RequirementCheckResult.Failure(
                $"Service principal not found for blueprint '{blueprintId}'",
                "Run 'a365 setup blueprint' to ensure the service principal is provisioned.",
                details: $"Application '{blueprintId}' exists but has no service principal in tenant '{tenantId}'.");
            result.Metadata = new RequirementCheckMetadata { AppExists = true, ServicePrincipalExists = false };
            return result;
        }

        // Check 3: Agent registration exists (if registrationId is configured)
        if (!string.IsNullOrWhiteSpace(config.AgentRegistrationId))
        {
            bool? registrationExists;
            try
            {
                registrationExists = await _graphApiService.AgentRegistrationExistsAsync(
                    tenantId, config.AgentRegistrationId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Failed to query agent registration");
                var result = RequirementCheckResult.Warning(
                    "Blueprint and service principal exist but could not verify agent registration",
                    details: $"Agent registry query failed: {ex.Message}");
                result.Metadata = new RequirementCheckMetadata { AppExists = true, ServicePrincipalExists = true };
                return result;
            }

            if (registrationExists == false)
            {
                var result = RequirementCheckResult.Failure(
                    $"Agent registration '{config.AgentRegistrationId}' not found",
                    "Run 'a365 setup all' to register the agent, or verify the agentRegistrationId in your configuration.",
                    details: $"Blueprint '{blueprintId}' and service principal exist, but agent registration " +
                        $"'{config.AgentRegistrationId}' was not found in the agent registry.");
                result.Metadata = new RequirementCheckMetadata
                {
                    AppExists = true,
                    ServicePrincipalExists = true,
                    RegistrationExists = false
                };
                return result;
            }

            if (registrationExists == null)
            {
                var result = RequirementCheckResult.Warning(
                    "Blueprint registered but agent registration status is unknown",
                    details: $"Application and service principal verified. Agent registration '{config.AgentRegistrationId}' " +
                        "could not be confirmed (insufficient permissions or transient error).");
                result.Metadata = new RequirementCheckMetadata { AppExists = true, ServicePrincipalExists = true };
                return result;
            }

            return await BuildSuccessResult(config, blueprintId, tenantId, logger,
                $"Blueprint '{blueprintId}' registered with service principal and agent registration '{config.AgentRegistrationId}'.",
                registrationExists: true,
                cancellationToken);
        }

            return await BuildSuccessResult(config, blueprintId, tenantId, logger,
            $"Blueprint '{blueprintId}' registered with service principal '{servicePrincipalId}'.",
            registrationExists: null,
            cancellationToken);
    }

    /// <summary>
    /// After core registration checks pass, verify inheritable permissions
    /// by comparing the static baseline + tooling manifest scopes against what is actually in Entra.
    /// Missing or mismatched permissions produce a failure.
    /// </summary>
    private async Task<RequirementCheckResult> BuildSuccessResult(
            Agent365Config config,
            string blueprintId,
            string tenantId,
            ILogger logger,
            string baseDetails,
            bool? registrationExists,
            CancellationToken cancellationToken)
    {
            var baseMetadata = new RequirementCheckMetadata
            {
                AppExists = true,
                ServicePrincipalExists = true,
                RegistrationExists = registrationExists
            };

            if (_blueprintService is null)
            {
                var result = RequirementCheckResult.Success(details: baseDetails);
                result.Metadata = baseMetadata;
                return result;
            }

            // Build expected permissions: static baseline + tooling manifest scopes
            var expectedPermissions = await BuildExpectedPermissionsAsync(config, logger, cancellationToken);

            List<(string ResourceAppId, bool ScopesAllAllowed, bool RolesAllAllowed)> inheritableEntries;
            try
            {
                inheritableEntries = await _blueprintService.ListInheritablePermissionsAsync(
                    tenantId, blueprintId, ct: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Failed to query inheritable permissions");
                var result = RequirementCheckResult.Warning(
                    "Blueprint registered but could not verify inheritable permissions",
                    details: $"{baseDetails} Permissions query failed: {ex.Message}");
                result.Metadata = baseMetadata;
                return result;
            }

            var inheritableByResource = inheritableEntries.ToDictionary(
                e => e.ResourceAppId,
                e => (e.ScopesAllAllowed, e.RolesAllAllowed),
                StringComparer.OrdinalIgnoreCase);

            // Fetch actual granted scopes on the blueprint SP for each expected resource
            Dictionary<string, (string[] DelegatedScopes, string[] AppRoleNames)> grantsByResource;
            try
            {
                grantsByResource = await _blueprintService.GetBlueprintSpGrantsAsync(
                    tenantId, blueprintId,
                    expectedPermissions.Select(e => e.ResourceAppId),
                    ct: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Failed to query blueprint SP grants");
                grantsByResource = new Dictionary<string, (string[] DelegatedScopes, string[] AppRoleNames)>(
                    StringComparer.OrdinalIgnoreCase);
            }

            var warnings = new List<string>();
            var resourcePermissionResults = new List<BlueprintResourcePermission>();

            foreach (var expected in expectedPermissions)
            {
                var hasInheritableConfig = inheritableByResource.TryGetValue(
                    expected.ResourceAppId, out var inheritableFlags);
                var scopesAllAllowed = hasInheritableConfig && inheritableFlags.ScopesAllAllowed;
                var rolesAllAllowed = hasInheritableConfig && inheritableFlags.RolesAllAllowed;

                // Get actual delegated scopes and app roles from the blueprint SP grants
                var actualScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var actualAppRoles = new List<string>();
                if (grantsByResource.TryGetValue(expected.ResourceAppId, out var grants))
                {
                    foreach (var scope in grants.DelegatedScopes)
                    {
                        actualScopes.Add(scope);
                    }
                    actualAppRoles.AddRange(grants.AppRoleNames);
                }

                var hasDelegatedGrants = actualScopes.Count > 0;
                var hasAppRoleGrants = actualAppRoles.Count > 0;
                var hasAnyGrants = hasDelegatedGrants || hasAppRoleGrants;

                // Effective inheritance: kind=allAllowed on both sides AND at least one grant
                var effectiveInheritance = scopesAllAllowed && rolesAllAllowed && hasAnyGrants;

                if (!hasInheritableConfig)
                {
                    warnings.Add($"{expected.ResourceName}: no inheritable permissions configured in Entra");
                    resourcePermissionResults.Add(new BlueprintResourcePermission
                    {
                        ResourceName = expected.ResourceName,
                        ResourceAppId = expected.ResourceAppId,
                        ExpectedScopes = expected.Scopes.ToList(),
                        ActualScopes = actualScopes.ToList(),
                        MissingScopes = expected.Scopes.ToList(),
                        InheritablePermissionsConfigured = false,
                        ScopesAllAllowed = false,
                        RolesAllAllowed = false,
                        ActualAppRoles = actualAppRoles,
                        EffectiveInheritance = false
                    });
                    continue;
                }

                if (!scopesAllAllowed || !rolesAllAllowed)
                {
                    warnings.Add($"{expected.ResourceName}: kind is not allAllowed for " +
                        (!scopesAllAllowed && !rolesAllAllowed ? "scopes and roles" :
                         !scopesAllAllowed ? "scopes" : "roles") +
                        " — re-run 'a365 setup permissions' to reconcile");
                }
                else if (!hasAnyGrants)
                {
                    warnings.Add($"{expected.ResourceName}: kind=allAllowed configured but no permissions granted on blueprint SP — inheritance has nothing to inherit");
                }

                var missingScopes = expected.Scopes
                    .Where(s => !actualScopes.Contains(s))
                    .ToList();

                if (missingScopes.Count > 0)
                {
                    warnings.Add($"{expected.ResourceName}: missing scopes: {string.Join(", ", missingScopes)}");
                }

                resourcePermissionResults.Add(new BlueprintResourcePermission
                {
                    ResourceName = expected.ResourceName,
                    ResourceAppId = expected.ResourceAppId,
                    ExpectedScopes = expected.Scopes.ToList(),
                    ActualScopes = actualScopes.ToList(),
                    MissingScopes = missingScopes,
                    InheritablePermissionsConfigured = true,
                    ScopesAllAllowed = scopesAllAllowed,
                    RolesAllAllowed = rolesAllAllowed,
                    ActualAppRoles = actualAppRoles,
                    EffectiveInheritance = effectiveInheritance
                });
            }

            baseMetadata.ResourcePermissions = resourcePermissionResults;

            if (warnings.Count > 0)
            {
                var result = RequirementCheckResult.Failure(
                    "Blueprint registered but permissions/consent gaps detected",
                    "Run 'a365 setup all' or grant consent in the Azure portal.",
                    details: $"{baseDetails} {string.Join(". ", warnings)}.");
                result.Metadata = baseMetadata;
                return result;
            }

            var scopeSummary = string.Join("; ", expectedPermissions.Select(r =>
                $"{r.ResourceName}: {string.Join(", ", r.Scopes)}"));

            var successResult = RequirementCheckResult.Success(
                details: $"{baseDetails} Permissions verified: {scopeSummary}");
            successResult.Metadata = baseMetadata;
            return successResult;
    }

    /// <summary>
    /// Builds the expected permission list from the static baseline plus tooling manifest scopes.
    /// Scopes for the same resource app ID are merged.
    /// </summary>
    internal static async Task<List<(string ResourceAppId, string ResourceName, List<string> Scopes)>> BuildExpectedPermissionsAsync(
        Agent365Config config, ILogger logger, CancellationToken cancellationToken = default)
    {
        var merged = new Dictionary<string, (string ResourceName, HashSet<string> Scopes)>(StringComparer.OrdinalIgnoreCase);

        // Add static baseline
        foreach (var (appId, name, scopes) in BaselinePermissions)
        {
            if (!merged.TryGetValue(appId, out var entry))
            {
                entry = (name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                merged[appId] = entry;
            }

            foreach (var scope in scopes)
            {
                entry.Scopes.Add(scope);
            }
        }

        // Add tooling manifest scopes (if manifest exists)
        var projectPath = string.IsNullOrWhiteSpace(config.DeploymentProjectPath)
            ? Directory.GetCurrentDirectory()
            : config.DeploymentProjectPath;
        var manifestPath = Path.Combine(
            projectPath,
            McpConstants.ToolingManifestFileName);

        if (File.Exists(manifestPath))
        {
            try
            {
                var scopesByAudience = await ManifestHelper.GetScopesByAudienceAsync(manifestPath);

                foreach (var (audienceAppId, scopes) in scopesByAudience)
                {
                    if (!merged.TryGetValue(audienceAppId, out var entry))
                    {
                        entry = ("Agent 365 Tools", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        merged[audienceAppId] = entry;
                    }

                    foreach (var scope in scopes)
                    {
                        entry.Scopes.Add(scope);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Failed to read tooling manifest at {Path}, skipping manifest scopes", manifestPath);
            }
        }

        return merged.Select(kvp => (kvp.Key, kvp.Value.ResourceName, kvp.Value.Scopes.OrderBy(s => s).ToList())).ToList();
    }
}
