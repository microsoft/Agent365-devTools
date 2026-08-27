// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Requirement check that verifies the client app declares the <c>wids</c> optional claim on its
/// access tokens.
/// <para>
/// Without this claim the CLI cannot detect whether the signed-in user holds Global Administrator
/// (the <see cref="GraphApiService.IsCurrentUserAdminAsync"/> path reads role membership from the
/// access token's <c>wids</c> claim with no Graph call). When the claim is absent the role check
/// returns <see cref="Models.RoleCheckResult.Unknown"/>, which the batch permissions orchestrator
/// collapses to "not GA" and silently skips Phase 2b — the AllPrincipals OAuth2 grants on the
/// blueprint SP. The visible symptom is a blueprint with <c>inheritablePermissions.kind=allAllowed</c>
/// but zero granted scopes/roles on the blueprint SP; inheritance has nothing to inherit and MAC
/// has nothing to display.
/// </para>
/// </summary>
public class WidsOptionalClaimRequirementCheck : RequirementCheck
{
    private readonly IClientAppValidator _clientAppValidator;

    public WidsOptionalClaimRequirementCheck(IClientAppValidator clientAppValidator)
    {
        _clientAppValidator = clientAppValidator ?? throw new ArgumentNullException(nameof(clientAppValidator));
    }

    /// <inheritdoc />
    public override string Name => "Client App 'wids' Optional Claim";

    /// <inheritdoc />
    public override string Description => "Verifies the client app emits the 'wids' optional claim on access tokens, required for Global Administrator role detection";

    /// <inheritdoc />
    public override string Category => "Authentication";

    /// <inheritdoc />
    public override async Task<RequirementCheckResult> CheckAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken = default)
    {
        return await ExecuteCheckWithLoggingAsync(config, logger, CheckImplementationAsync, cancellationToken);
    }

    private async Task<RequirementCheckResult> CheckImplementationAsync(Agent365Config config, ILogger logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.ClientAppId))
        {
            return RequirementCheckResult.Failure(
                errorMessage: "clientAppId is not configured",
                resolutionGuidance: "Configure clientAppId in a365.config.json or run 'a365 setup blueprint' to register the CLI client app.",
                details: "The wids optional claim check requires a clientAppId to inspect.");
        }

        if (string.IsNullOrWhiteSpace(config.TenantId))
        {
            return RequirementCheckResult.Failure(
                errorMessage: "tenantId is not configured",
                resolutionGuidance: "Configure tenantId in a365.config.json or pass --tenant-id.",
                details: "The wids optional claim check requires a tenantId to query Graph.");
        }

        if (AuthenticationConstants.IsWellKnownFirstPartyClientApp(config.ClientAppId))
        {
            return await CheckFirstPartyWidsClaimAsync(config, cancellationToken);
        }

        bool hasWids;
        try
        {
            hasWids = await _clientAppValidator.HasWidsAccessTokenOptionalClaimAsync(
                config.ClientAppId, config.TenantId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unexpected error reading optionalClaims for client app {ClientAppId}", config.ClientAppId);
            return RequirementCheckResult.Failure(
                errorMessage: $"Could not read optionalClaims for client app {config.ClientAppId}: {ex.Message}",
                resolutionGuidance: "Ensure 'az login' has succeeded and your account has Application.Read.All consented for the CLI client app.",
                details: ex.ToString());
        }

        if (hasWids)
        {
            return RequirementCheckResult.Success(details: $"'wids' is present on accessToken optionalClaims for {config.ClientAppId}");
        }

        var manualPatch = BuildManualPatchInstructions(config.ClientAppId, config.TenantId);

        return RequirementCheckResult.Failure(
            errorMessage: $"Client app {config.ClientAppId} is missing the 'wids' optional claim on accessToken. " +
                "Global Administrator role detection cannot work, so blueprint-level AllPrincipals OAuth2 grants " +
                "(Phase 2b) will be silently skipped during 'setup all'. The blueprint will end up with " +
                "inheritablePermissions.kind=allAllowed but zero granted scopes/roles on the blueprint SP, and MAC " +
                "will have nothing to display.",
            resolutionGuidance: manualPatch,
            details: "Without 'wids' in the access token, IsCurrentUserAdminAsync returns Unknown for every user — " +
                "the orchestrator collapses Unknown to 'not GA' and skips Phase 2b.");
    }

    /// <summary>
    /// Verifies <c>wids</c> for the Microsoft first-party app from a token actually issued to it —
    /// its registration is not readable from the tenant, so the token is the only evidence.
    /// </summary>
    private async Task<RequirementCheckResult> CheckFirstPartyWidsClaimAsync(
        Agent365Config config,
        CancellationToken cancellationToken)
    {
        const string FirstPartyGuidance =
            "The 'wids' claim is configured by Microsoft on the Agent 365 CLI application and cannot be changed in your tenant. " +
            "It is also absent when the signed-in account holds no directory role assignment. " +
            "Sign in with an account that holds Global Administrator (az logout && az login) and re-run this check. " +
            "If it remains absent, run 'a365 setup blueprint' as a Global Administrator so the blueprint permission grants are not skipped.";

        var hasWids = await _clientAppValidator.HasWidsClaimOnIssuedAccessTokenAsync(
            config.ClientAppId, config.TenantId, cancellationToken);

        if (hasWids == true)
        {
            return RequirementCheckResult.Success(
                details: $"'wids' is present on an access token issued to {config.ClientAppId}");
        }

        if (hasWids == false)
        {
            return RequirementCheckResult.Warning(
                message: $"'wids' is not present on the access token issued to the first-party Agent 365 CLI application {config.ClientAppId}. " +
                    "Global Administrator detection will return Unknown, so blueprint-level AllPrincipals grants may be skipped.",
                details: FirstPartyGuidance);
        }

        return RequirementCheckResult.Warning(
            message: $"Could not verify the 'wids' claim for the first-party Agent 365 CLI application {config.ClientAppId} — " +
                "no access token was available to inspect.",
            details: FirstPartyGuidance);
    }

    private static string BuildManualPatchInstructions(string clientAppId, string tenantId)
    {
        // Two-line remediation: portal path for humans, raw `az rest` for scriptable runs.
        // Both add { name: "wids", essential: false } to optionalClaims.accessToken.
        return
            "Add the 'wids' optional claim on the client app's access tokens. Options:\n" +
            $"  1. Portal: https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/TokenConfiguration/appId/{clientAppId} → 'Add optional claim' → Token type 'Access' → check 'wids' → Add.\n" +
            "  2. Or run as an Application Administrator / Global Administrator:\n" +
            $"     az rest --method PATCH --url \"https://graph.microsoft.com/v1.0/applications(appId='{clientAppId}')\" --headers \"Content-Type=application/json\" --body \"{{\\\"optionalClaims\\\":{{\\\"accessToken\\\":[{{\\\"name\\\":\\\"wids\\\",\\\"essential\\\":false,\\\"additionalProperties\\\":[]}}]}}}}\"\n" +
            "After updating, sign out and back in (az logout && az login) so the next token carries the new claim, then re-run 'a365 setup requirements'.";
    }
}
