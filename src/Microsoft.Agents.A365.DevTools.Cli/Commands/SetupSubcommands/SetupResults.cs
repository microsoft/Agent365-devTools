// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Tracks the results of each setup step for summary reporting
/// </summary>
public class SetupResults
{
    public bool InfrastructureCreated { get; set; }
    public bool BlueprintCreated { get; set; }
    public string? BlueprintId { get; set; }
    public string? BlueprintDisplayName { get; set; }
    public bool McpPermissionsConfigured { get; set; }
    public bool BotApiPermissionsConfigured { get; set; }
    public string? ObservabilityResourceAppId { get; set; }
    public bool MessagingEndpointRegistered { get; set; }

    /// <summary>
    /// The raw <see cref="Models.EndpointRegistrationResult"/> returned by the messaging endpoint
    /// registration step. Null means the step was not attempted (e.g. non-M365 flow without
    /// --m365). Drives the messaging endpoint row in the setup summary.
    /// </summary>
    public Models.EndpointRegistrationResult? MessagingEndpointResult { get; set; }

    /// <summary>
    /// The URL that was registered (or attempted) as the messaging endpoint. Populated alongside
    /// <see cref="MessagingEndpointResult"/> so the setup summary is self-contained.
    /// </summary>
    public string? MessagingEndpoint { get; set; }

    /// <summary>
    /// When <see cref="MessagingEndpointResult"/> is Failed, classifies the server's reason:
    /// "NotOwner" for ownership failures (403 from Teams Graph wrapped as 400 by MCP Platform),
    /// "Other" for other failures. Null when the step did not fail.
    /// </summary>
    public string? MessagingEndpointFailureReason { get; set; }
    public bool InheritablePermissionsConfigured { get; set; }
    public bool BotInheritablePermissionsConfigured { get; set; }
    public bool GraphPermissionsConfigured { get; set; }
    public bool GraphInheritablePermissionsConfigured { get; set; }
    public bool CustomPermissionsConfigured { get; set; }

    // Batch phase results — set by AllSubcommand after BatchPermissionsOrchestrator completes.
    // These replace the per-resource flags for the setup all summary display.

    /// <summary>Phase 1: Service principal resolution completed for all specs.</summary>
    public bool BatchPermissionsPhase1Completed { get; set; }

    /// <summary>Phase 2: OAuth2 grants and inheritable permissions configured for all resources.</summary>
    public bool BatchPermissionsPhase2Completed { get; set; }

    /// <summary>
    /// Outcome of the tenant-wide admin consent grant (the <c>/v2.0/adminconsent</c> path that
    /// covers blueprint delegated scopes for All Principals). <see cref="GrantOutcome.Failed"/> with
    /// <see cref="AdminConsentUrl"/> or <see cref="CombinedConsentUrl"/> set means the user is
    /// non-admin and consent is pending; <see cref="GrantOutcome.NotApplicable"/> means the step
    /// was not reached.
    /// </summary>
    public GrantOutcome TenantWideConsentOutcome { get; set; }

    /// <summary>
    /// Outcome of S2S app role assignments targeting the blueprint service principal. Written by
    /// <see cref="BatchPermissionsOrchestrator"/> in the DW path and in the non-DW path when the
    /// blueprint carries app-role scopes (e.g. Observability API).
    /// </summary>
    public GrantOutcome BlueprintS2SOutcome { get; set; }

    /// <summary>
    /// Outcome of S2S app role assignments targeting the agent identity service principal. Written
    /// by the non-DW path when <see cref="SetupContext.IsS2sMode"/> or <see cref="SetupContext.IsBothMode"/>
    /// is true.
    /// </summary>
    public GrantOutcome AgentIdentityS2SOutcome { get; set; }

    /// <summary>
    /// Outcome of principal-scoped delegated grants on the agent identity service principal.
    /// Written by the non-DW path when <see cref="SetupContext.IsOboMode"/> or <see cref="SetupContext.IsBothMode"/>
    /// is true. <see cref="GrantOutcome.Failed"/> drives the "Agent identity delegated permissions"
    /// PowerShell action item in the setup summary.
    /// </summary>
    public GrantOutcome AgentIdentityDelegatedOutcome { get; set; }

    /// <summary>
    /// Error message when Microsoft Graph inheritable permissions fail to configure.
    /// Non-null indicates failure. This is critical for agent token exchange functionality.
    /// </summary>
    public string? GraphInheritablePermissionsError { get; set; }

    /// <summary>
    /// Whether the Federated Identity Credential was configured for the managed identity.
    /// False (with FederatedCredentialError set) means agent token exchange may not work.
    /// </summary>
    public bool FederatedCredentialConfigured { get; set; }

    /// <summary>
    /// Error message when Federated Identity Credential configuration failed.
    /// </summary>
    public string? FederatedCredentialError { get; set; }
    
    /// <summary>
    /// True when the client secret could not be created automatically and the user must
    /// create it manually in Entra ID and re-run setup. Surfaces in the summary as Action Required.
    /// </summary>
    public bool ClientSecretManualActionRequired { get; set; }

    // Idempotency tracking flags - track whether resources already existed (vs newly created)
    public bool InfrastructureAlreadyExisted { get; set; }
    public bool BlueprintAlreadyExisted { get; set; }
    public bool EndpointAlreadyExisted { get; set; }
    public bool McpPermissionsAlreadyExisted { get; set; }
    public bool InheritablePermissionsAlreadyExisted { get; set; }
    public bool BotApiPermissionsAlreadyExisted { get; set; }
    public bool BotInheritablePermissionsAlreadyExisted { get; set; }
    public bool GraphPermissionsAlreadyExisted { get; set; }
    public bool GraphInheritablePermissionsAlreadyExisted { get; set; }
    public bool CustomPermissionsAlreadyExisted { get; set; }

    /// <summary>
    /// True when the tenant-wide admin consent for the blueprint already existed before this run.
    /// Set alongside <see cref="TenantWideConsentOutcome"/> = <see cref="GrantOutcome.Granted"/>
    /// when the consent pre-check observed an existing grant covering all required scopes, so
    /// the browser was not opened and nothing was newly granted. Drives "already granted" vs
    /// "granted" wording in the Blueprint Permission Grants row of the setup summary.
    /// </summary>
    public bool TenantWideConsentAlreadyExisted { get; set; }

    /// <summary>
    /// True when every S2S app role assignment on the blueprint SP already existed before this
    /// run. Set alongside <see cref="BlueprintS2SOutcome"/> = <see cref="GrantOutcome.Granted"/>
    /// when the S2S pre-check observed all required (resource, role) pairs already assigned, so
    /// the assignment loop was skipped and nothing was newly created. Drives "already granted"
    /// vs "granted" wording in the Blueprint Permission Grants row of the setup summary.
    /// </summary>
    public bool BlueprintS2SAlreadyAssigned { get; set; }

    /// <summary>
    /// True when the principal-scoped delegated grant on the agent identity SP already existed
    /// before this run. Set alongside <see cref="AgentIdentityDelegatedOutcome"/> =
    /// <see cref="GrantOutcome.Granted"/> by the non-DW OBO/Both code path when the per-principal
    /// oauth2PermissionGrant was found already in place. Drives "already granted" vs "granted"
    /// wording in the Blueprint Permission Grants row for the bothMode label
    /// "S2S app roles + developer-scoped delegated on agent identity" — without it the row could
    /// not tell whether the OBO half of the bothMode result was idempotent.
    /// </summary>
    /// <remarks>
    /// No writer exists in production code as of this commit: the non-DW OBO grant path relies on
    /// blueprint-inheritance + tenant-wide admin consent rather than a per-principal grant call
    /// (see <c>NonDwBlueprintSetupOrchestrator.cs</c> step 5a comment). When that design is
    /// revisited and an explicit per-principal grant call is introduced, callers must populate
    /// this flag so the bothMode "already granted" wording renders correctly. The renderer in
    /// <c>SetupHelpers.DisplaySetupSummary</c> already consumes the flag.
    /// </remarks>
    public bool AgentIdentityDelegatedAlreadyExisted { get; set; }

    /// <summary>
    /// Consent URL to present when admin consent was not granted because the user lacks an admin role.
    /// Non-null indicates a tenant administrator needs to complete consent at this URL.
    /// </summary>
    public string? AdminConsentUrl { get; set; }

    /// <summary>
    /// Path to the generated config file where admin consent URLs were saved.
    /// Non-null when the current user lacks the GA role and consent URLs have been written to
    /// the <c>resourceConsents[*].consentUrl</c> fields in <c>a365.generated.config.json</c>.
    /// </summary>
    public string? ConsentUrlsSavedToPath { get; set; }

    /// <summary>
    /// Display names of the resources for which consent URLs were saved.
    /// Populated alongside <see cref="ConsentUrlsSavedToPath"/>.
    /// </summary>
    public List<string> ConsentResourceNames { get; } = new();

    /// <summary>
    /// A single combined /v2.0/adminconsent URL covering all five required resources.
    /// Populated alongside <see cref="ConsentUrlsSavedToPath"/> as a simpler handover option.
    /// </summary>
    public string? CombinedConsentUrl { get; set; }

    /// <summary>
    /// Whether this is a blueprint agent setup flow (--aiteammate false).
    /// Used in the summary display to show the correct recovery actions.
    /// </summary>
    public bool IsNonDwBlueprintFlow { get; set; }

    /// <summary>
    /// True for standalone `setup blueprint`: scopes the summary to the rows that command runs,
    /// suppressing the agent identity / registration / endpoint / project-settings rows.
    /// </summary>
    public bool IsBlueprintOnlyFlow { get; set; }

    /// <summary>
    /// Whether the agent is an M365 AI Teammate. Used only to tailor the "Next steps" wording
    /// (naming the Bot API explicitly). Does not change which steps run.
    /// </summary>
    public bool IsM365 { get; set; }

    /// <summary>
    /// The effective --authmode value used during the non-DW grant step.
    /// Null when the non-DW grant step was not reached (e.g. agent identity creation failed) or
    /// when the run is a DW (AI Teammate) flow — DW does not use --authmode.
    /// Used by DisplaySetupSummary to compute per-grant-type completion for the "both" mode and to
    /// derive which Action Required items apply.
    /// </summary>
    public AuthMode? EffectiveAuthMode { get; set; }

    /// <summary>
    /// Whether the Agent Identity was successfully created via the Agent Identity Graph API.
    /// Populated by the non-DW blueprint setup flow only.
    /// </summary>
    public bool AgentIdentityCreated { get; set; }

    /// <summary>
    /// True when <see cref="AgentIdentityCreated"/> is set because an existing identity was
    /// found (via config or API lookup) rather than freshly created. Drives "reused" vs "created"
    /// in the setup summary.
    /// </summary>
    public bool AgentIdentityAlreadyExisted { get; set; }

    /// <summary>
    /// The Agent Identity ID returned after agent identity creation.
    /// Non-null when <see cref="AgentIdentityCreated"/> is true.
    /// </summary>
    public string? AgentIdentityId { get; set; }

    /// <summary>
    /// The display name of the agent identity Entra app (e.g. "MyAgent Agent Identity").
    /// </summary>
    public string? AgentIdentityDisplayName { get; set; }

    /// <summary>
    /// Whether the Agent Instance was successfully registered via the Agent Instance Graph API.
    /// Populated by the non-DW blueprint setup flow only.
    /// </summary>
    public bool AgentInstanceRegistered { get; set; }

    /// <summary>
    /// True when <see cref="AgentInstanceRegistered"/> is set because an existing registration was
    /// found (via config or API lookup) rather than freshly registered. Drives "reused" vs "registered"
    /// in the setup summary.
    /// </summary>
    public bool AgentRegistrationAlreadyExisted { get; set; }

    /// <summary>
    /// The Agent Instance ID returned by the Agent Instance Graph API after registration.
    /// Non-null when <see cref="AgentInstanceRegistered"/> is true.
    /// </summary>
    public string? AgentInstanceId { get; set; }

    /// <summary>
    /// The display name used when registering the agent in the Agent Registry (e.g. "MyAgent Agent").
    /// </summary>
    public string? AgentRegistrationDisplayName { get; set; }


    /// <summary>Tenant ID used during setup. Populated so DisplaySetupSummary can include it in handoff output.</summary>
    public string? TenantId { get; set; }

    /// <summary>Whether step 1 (Requirements validation) was skipped via --skip-requirements.</summary>
    public bool PrerequisitesSkipped { get; set; }

    /// <summary>Whether step 2 (Azure hosting) was skipped because no Azure deployment is configured.</summary>
    public bool InfrastructureSkipped { get; set; }

    /// <summary>Whether step 3 (Blueprint creation) failed. Drives "failed"/"skipped" rows in the summary.</summary>
    public bool BlueprintFailed { get; set; }

    /// <summary>Whether the blueprint service principal was created successfully. False means blueprint is partial.</summary>
    public bool BlueprintServicePrincipalCreated { get; set; }

    /// <summary>Whether step 6 (Agent identity creation) was attempted but failed.</summary>
    public bool AgentIdentityFailed { get; set; }

    /// <summary>Whether step 7 (Agent registration) was attempted but failed.</summary>
    public bool AgentRegistrationFailed { get; set; }

    /// <summary>Whether step 8 (Project settings) was written to appsettings.json.</summary>
    public bool ProjectSettingsWritten { get; set; }

    /// <summary>
    /// True when the permission grants step was explicitly skipped because --agent-registration-only
    /// was passed. Drives the "skipped" row in the setup summary instead of showing a grant status.
    /// </summary>
    public bool PermissionGrantsSkipped { get; set; }

    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Resources whose service principal could not be provisioned in-line during setup
    /// (operator declined the per-SP prompt, az ad sp create failed, or
    /// <c>--skip-sp-provisioning</c> was set). Each entry is a fully-actionable pair: the
    /// <c>az ad sp create</c> command to provision the SP plus the per-SP unified-consent
    /// URL that grants the blueprint consent for this resource's scopes. The setup
    /// summary's "Action Required" block renders these as numbered items so the operator
    /// can complete provisioning without re-running setup.
    /// </summary>
    public List<MissingSpAction> MissingSpActions { get; } = new();

    public bool HasErrors => Errors.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;
}

/// <summary>
/// One entry in <see cref="SetupResults.MissingSpActions"/>. Resource identity plus the
/// two concrete commands/URLs the operator needs to complete provisioning manually:
/// (1) the <c>az ad sp create</c> command that creates the SP in the tenant, and
/// (2) the per-SP <c>/v2.0/adminconsent</c> URL that grants the blueprint consent for
/// this resource's delegated scopes once the SP exists.
/// </summary>
/// <param name="ResourceName">Human-readable display name (e.g. "Work IQ Teams MCP").</param>
/// <param name="ResourceAppId">Application ID of the resource (the GUID).</param>
/// <param name="Scopes">Delegated scopes the blueprint needs on this resource.</param>
/// <param name="AzCreateCommand">Copy-paste-able <c>az ad sp create --id ...</c>.</param>
/// <param name="PerSpConsentUrl">Per-SP unified-consent URL keyed to the blueprint as client and the resource scopes as the request.</param>
public sealed record MissingSpAction(
    string ResourceName,
    string ResourceAppId,
    string[] Scopes,
    string AzCreateCommand,
    string PerSpConsentUrl);
