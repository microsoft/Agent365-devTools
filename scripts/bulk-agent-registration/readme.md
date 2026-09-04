# Microsoft Agent 365 Automation Orchestrator — Setup and Operations Guide

> This file is the source document for setting up and operating the scripts in this folder, kept as plain Markdown for GitHub-flavored rendering, diffing, and search.

*`A365-AutomationOrchestrator.ps1` — Graph-only provisioning — no a365 CLI required*

| Item | Value |
| --- | --- |
| Document scope | Everything required to install, permission, and run `A365-AutomationOrchestrator.ps1` |
| Scripts covered | 15 - the orchestrator; the 4 provisioning and 4 removal scripts it invokes; `New-A365AutomationApp.ps1` as a one-time prerequisite; the 4 dedicated `Update-A365*.ps1` entry points; and `A365-BulkOnboarding.ps1` for CSV-driven bulk runs (`A365-BulkOnboardingCsv.psm1` is its supporting module, not a standalone script) |
| API surface | Microsoft Graph only, with one documented exception (Azure Key Vault) |
| Minimum PowerShell | `7.0 (#requires -Version 7)` |
| Generated | 2026-09-01 |

## 1. What the orchestrator does

`A365-AutomationOrchestrator.ps1` provisions a complete Microsoft Agent 365 agent by invoking a set of single-purpose scripts in dependency order. It performs no Graph calls of its own — it validates the request, decides which phases run, and passes the identifiers each phase produces to the next one.

### 1.1 The four objects

| # | Object | What it is | Created by |
| --- | --- | --- | --- |
| 1 | Blueprint | An Entra application of type agentIdentityBlueprint, plus its blueprint principal. Shared by every agent built from it. | `New-A365AgentBlueprint.ps1` |
| 2 | Agent identity | A servicePrincipal of type agentIdentity, built on the blueprint. This is the agent's identity for token acquisition (fmi_path). | `New-A365AgentIdentity.ps1` |
| 3 | Agent user | A real directory user of type agentUser, bound to the agent identity. Can hold a license and a mailbox. | `New-A365AgentUser.ps1` |
| 4 | Registration | The Microsoft 365 admin center inventory entry plus the agent registry instance. | `New-A365AgentRegistration.ps1` |

> **Note: Agent user and registration are siblings, not a chain.** Both depend only on the agent identity. If the agent user phase fails, the registration phase still runs. That is deliberate — a licensing problem should not prevent the agent from being registered.

### 1.2 Three modes of operation

| Mode | Switches | Behavior |
| --- | --- | --- |
| Create | `-NewBlueprint, -NewAgentIdentity, -NewAgentUser, -NewAgentRegistration` | Creates the selected objects. Phases run in dependency order 1→2→3→4. |
| Update | `-UpdateBlueprint, -UpdateAgentIdentity, -UpdateAgentUser, -UpdateAgentRegistration` | Each takes the id of the object to change and writes ONLY the attributes you supply. |
| Remove | `-RemoveBlueprint, -RemoveAgentIdentity, -RemoveAgentUser, -RemoveAgentRegistration` | Each takes the id of the object to delete. Runs AFTER all create/update phases, in reverse dependency order. |

Create, update and remove can be combined in a single run. Removal always runs last, and in reverse dependency order (registration → agent user → agent identity → blueprint) so a parent is never deleted before the children discovered through it.

### 1.3 Full capability list

- Create a blueprint with sponsors, owners, description, and declared API permissions.
- Grant tenant admin consent on the blueprint principal and make permissions inheritable.
- Create a client secret on the blueprint, and optionally store it in Azure Key Vault.
- Federate a managed identity to the blueprint (preferred over a client secret in production).
- Create an agent identity on a new or existing blueprint, with sponsors, owners and tags.
- Assign custom security attributes to the agent identity, with pre-flight validation.
- Create an agent user with manager, usage location, and an assigned license.
- Register the agent in the Microsoft 365 admin center and the agent registry.
- Update any of the four objects, writing only the attributes supplied.
- Remove any of the four objects, with an inspect-only dry run and an optional recycle-bin purge.
- Write a per-run log for the orchestrator and every script it invokes, with credential redaction on by default.
- Emit a machine-readable JSON report of everything created, changed, or left outstanding.

## 2. Software prerequisites

### 2.1 Required

| Component | Minimum | Verified with | Why |
| --- | --- | --- | --- |
| PowerShell | `7.0` | `7.6.5 (Core)` | The scripts declare #requires -Version 7 and use PowerShell 7 syntax throughout. |
| Microsoft.Graph.Authentication | `2.x` | `2.30.0` | The only module the scripts import. Supplies Connect-MgGraph and the authenticated Graph session. |

Install the Graph module (this one module only — the full Microsoft.Graph meta-module is not needed):

```powershell
Install-Module Microsoft.Graph.Authentication -Scope CurrentUser
```

> **Note: Do not install the full Microsoft.Graph module.** The suite calls Graph through `Invoke-MgGraphRequest`, so only `Microsoft.Graph.Authentication` is required. Installing the complete meta-module pulls in dozens of sub-modules and slows every session start.

### 2.2 Optional — only for Azure Key Vault

These are needed only if you use `-BlueprintKeyVaultName` to store a client secret in Azure Key Vault, AND you authenticate interactively. App-only runs (client secret, certificate, managed identity) need neither.

| Component | Verified with | When it is needed |
| --- | --- | --- |
| Azure CLI | `2.80.0` | Interactive runs that write to Key Vault: supplies the vault-audience token from your az login session. |
| Az.Accounts | `5.4.0` | Alternative to the Azure CLI for the same purpose (Connect-AzAccount session). |

### 2.3 Verify your environment

```powershell
$PSVersionTable.PSVersion                                   # expect 7.0 or higher
Get-Module -ListAvailable Microsoft.Graph.Authentication    # expect 2.x

# Optional, only for interactive Key Vault writes:
az version
Get-Module -ListAvailable Az.Accounts
```

### 2.4 Keep the suite together

The orchestrator resolves the step scripts from its own directory. Keep all files in one folder, or pass `-ScriptRoot` to point at it explicitly. If a required script is missing the run fails immediately, naming the file and the phase that needs it.

## 3. The automation application

Unattended runs authenticate as an Entra application. `New-A365AutomationApp.ps1` creates that application, adds the Microsoft Graph application roles for the scenarios you select, and can grant admin consent in the same run.

### 3.1 Create it

```powershell
.\New-A365AutomationApp.ps1 `
    -TenantId <tenant-id> `
    -DisplayName 'A365 Provisioning Automation' `
    -Scenario All `
    -NewClientSecret
```

> **Important: App roles are granted by default.** `New-A365AutomationApp.ps1` grants the app roles as part of the run — there is no `-GrantAdminConsent` switch. Use `-SkipGrant` to create the application and its credentials WITHOUT granting them. If the caller lacks the directory role needed to consent, the script reports what is outstanding and prints a consent link.

Scenarios are additive — run the script once per scenario, or use `All`:

| Scenario | Covers |
| --- | --- |
| `Blueprint` | Creating and configuring blueprints and blueprint principals |
| `AgentIdentity` | Creating agent identities, including tags and custom security attributes |
| `Registration` | Registering agents in the admin center and the agent registry |
| `AgentUser` | Agent users, licensing, and per-identity permission consent |
| `All` | Every role in the four scenarios above |

### 3.2 Microsoft Graph application permissions

The following are Microsoft Graph APPLICATION roles (not delegated scopes). They must be granted to the automation application's service principal and admin-consented.

#### Scenario: Blueprint

| Permission | Why it is needed |
| --- | --- |
| `AgentIdentityBlueprint.Create` | Create the blueprint application |
| `AgentIdentityBlueprint.Read.All` | Read blueprints, resolve one by appId or objectId |
| `AgentIdentityBlueprint.ReadWrite.All` | Update the blueprint, declare required permissions, set inheritance |
| `AgentIdentityBlueprint.AddRemoveCreds.All` | addPassword (client secret) and federated identity credentials |
| `AgentIdentityBlueprintPrincipal.Create` | Create the blueprint principal |
| `AgentIdentityBlueprintPrincipal.Read.All` | Read the blueprint principal |
| `AgentIdentityBlueprintPrincipal.ReadWrite.All` | Assign owners on the blueprint PRINCIPAL |
| `Application.Read.All` | Resolve resource service principals when declaring permissions |
| `Application.ReadWrite.All` | Assign owners on the blueprint APPLICATION |
| `Directory.Read.All` | Resolve directory objects for owner assignment |
| `User.Read.All` | Resolve sponsors and owners given as UPN, mail or display name |
| `Group.Read.All` | Resolve group sponsors |

#### Scenario: AgentIdentity

| Permission | Why it is needed |
| --- | --- |
| `AgentIdentity.Create.All` | Create the agent identity |
| `AgentIdentity.Read.All` | Read agent identities and verify the created object |
| `AgentIdentity.ReadWrite.All` | Update tags, display name, sponsors and owners |
| `AgentIdentityBlueprint.Read.All` | Validate the blueprint the identity is built on |
| `Application.Read.All` | Resolve resource service principals |
| `Application.ReadWrite.All` | Assign owners on the agent identity |
| `Directory.Read.All` | Resolve directory objects |
| `User.Read.All` | Resolve sponsors and owners |
| `Group.Read.All` | Resolve group sponsors |
| `CustomSecAttributeAssignment.ReadWrite.All` | Assign custom security attributes |
| `CustomSecAttributeDefinition.Read.All` | Validate attribute sets and allowed values before assigning |

*The last two rows are requested twice over: as application roles (shown here) and, for `-Scenario AgentIdentity` and `-Scenario All`, as delegated scopes as well. An unattended app-only run needs only the application roles. An interactive run needs the delegated scopes AND the Attribute Assignment Administrator directory role, which no application permission grants.*

#### Scenario: Registration

| Permission | Why it is needed |
| --- | --- |
| `AgentRegistration.ReadWrite.All` | POST /beta/copilot/agentRegistrations |
| `AgentInstance.ReadWrite.All` | POST /beta/agentRegistry/agentInstances |
| `AgentIdentity.Read.All` | Validate the agent identity being registered |
| `User.Read.All` | Resolve `-AgentRegistrationOwner` to object ids (app-only cannot use /me) |

#### Scenario: AgentUser

*This set covers the agent user phase of the orchestrator. It holds the only copy of `AgentIdUser.ReadWrite.All`, which is the role that authorizes creating an agent user, so it is also required by `-Scenario All`.*

| Permission | Why it is needed |
| --- | --- |
| `AgentIdUser.ReadWrite.All` | Create the agent user. This is the specific role the call is authorized by. |
| `User.ReadWrite.All` | Set manager and usage location, assign the license |
| `User.Read.All` | Resolve the manager and check for an existing account |
| `Directory.Read.All` | Read verified domains, resolve the manager |
| `Organization.Read.All` | Read /subscribedSkus for the license pre-flight check |
| `DelegatedPermissionGrant.ReadWrite.All` | Per-identity delegated consent |
| `AppRoleAssignment.ReadWrite.All` | Per-identity application role assignment |
| `Application.Read.All` | Resolve resource service principals |
| `AgentIdentity.Read.All` | Validate the identity the user is bound to |

### 3.3 Three permissions that are commonly missed

Each of these fails with a bare 403 that names no permission, so the cause is not obvious from the error:

| Permission | The trap |
| --- | --- |
| `AgentIdentityBlueprintPrincipal.ReadWrite.All` | Required to set owners on the blueprint PRINCIPAL. `Application.ReadWrite.All` does NOT authorize it — granting more `Application.*` permissions will not fix a principal owner denial. |
| `AgentIdUser.ReadWrite.All` | Required to create an agent user. `User.ReadWrite.All` is NOT enough. |
| `CustomSecAttributeAssignment.ReadWrite.All` | Custom security attributes are gated separately from every other directory permission. No `Application.*` or `Directory.*` role reaches them, and Global Administrator does not hold them by default. `New-A365AutomationApp.ps1 -Scenario AgentIdentity` (or `All`) requests them in BOTH shapes: as application roles for an unattended run, and as delegated scopes for an interactive one. An interactive run needs one more thing that no application permission can grant - the signed-in user must also hold the Attribute Assignment Administrator directory role. |

## 4. Granting admin consent

Application permissions do nothing until an administrator consents to them. Which administrator can do that depends on what is being consented, and getting this wrong sends the request to someone who will find the consent button grayed out.

| What you are consenting | Directory role required |
| --- | --- |
| Microsoft Graph APPLICATION roles (application permissions) — this is most of the table in section 3 | Privileged Role Administrator, or Global Administrator |
| Anything else: delegated scopes, or application roles on a non-Graph API | Application Administrator, Cloud Application Administrator, Privileged Role Administrator, or Global Administrator |

> **Important: Application Administrator cannot consent Microsoft Graph app roles.** Application Administrator and Cloud Application Administrator may consent any permission for any API EXCEPT Microsoft Graph application permissions. Because the A365 roles are Graph application permissions, consenting them needs Privileged Role Administrator or Global Administrator.

### 4.1 If a grant fails at run time

When a consent or app-role assignment is refused, the scripts do not abort — the object has usually already been created. Instead the run finishes and prints exactly what an administrator must do, including a one-click consent link:

```text
ACTION REQUIRED - an administrator must finish granting these permissions.
    app role  User.Read.All on Microsoft Graph
              Insufficient privileges to complete the operation
  They are declared on the app but grant no claims until consent succeeds.

  Send this link to an administrator...
    https://login.microsoftonline.com/<tenant>/adminconsent?client_id=<appId>

  Or grant it in the Microsoft Entra admin center:
    https://entra.microsoft.com/#view/Microsoft_AAD_IAM/ManagedAppMenuBlade/~/Permissions/...

  Required role: Privileged Role Administrator, or Global Administrator.
```

The same information appears in the JSON report as `adminConsentUrl`, `portalPermissionsUrl` and `consentFailures`, and the orchestrator repeats it once at the end of a multi-phase run under `summary.consentActionRequired`.

## 5. Authentication

Exactly one authentication method must be supplied. Supplying two is refused up front.

| Method | Parameters | App-only? | Notes |
| --- | --- | --- | --- |
| Client secret | `-ClientId -ClientSecret` | Yes | Prefer `$env:A365_CLIENT_SECRET` over passing the value on the command line. |
| Certificate | `-ClientId -CertificateThumbprint`<br>`-Certificate \| -CertificatePath` | Yes | Recommended for production. `-CertificatePath` also accepts `-CertificatePassword`. |
| Managed identity | `-UseManagedIdentity` | Yes | For Azure-hosted automation. `-ClientId` selects a user-assigned identity. |
| Access token | `-AccessToken` | Depends | A pre-obtained Microsoft Graph token. |
| Interactive | `-Interactive` | No | Signs in as a user. Cannot be used with `-NewAgentUser`. |

> **Important: `-NewAgentUser` requires app-only authentication.** `New-A365AgentUser.ps1` supports client secret, certificate and managed identity only — it has no interactive mode. The orchestrator refuses the combination before any phase runs, rather than failing after the blueprint and identity have already been created.

### 5.1 Mixed authentication for the registration phase

The registration API is delegated-first. If your application cannot be granted the registration roles, run the earlier phases app-only and sign in interactively for phase 4 only:

```powershell
-AgentRegistrationAuth Interactive
```

Allowed values are `Same` (default — reuse the run's authentication) and `Interactive`.

### 5.2 Keeping the secret out of your shell history

```powershell
$env:A365_CLIENT_SECRET = '<secret>'
.\A365-AutomationOrchestrator.ps1 -TenantId <tid> -ClientId <appId> `
    -ClientSecret $env:A365_CLIENT_SECRET ...
```

If `-ClientSecret` is omitted entirely, the scripts read `$env:A365_CLIENT_SECRET` automatically. Passing a plain-text secret on the command line produces a warning, because it is visible to shell history and transcripts.

## 6. Azure Key Vault (optional)

`-BlueprintKeyVaultName` stores a newly created blueprint client secret in Azure Key Vault instead of printing it once to the console. This is the only capability in the suite that does not run on Microsoft Graph.

### 6.1 Why this one is not a Graph operation

Key Vault is a separate service with its own data plane and its own token audience. This was verified against a live tenant rather than assumed:

| Probe | Result | Conclusion |
| --- | --- | --- |
| `GET /beta/keyVaults, /v1.0/keyVaults, /beta/secrets` | `400` | Microsoft Graph exposes no Key Vault surface at all |
| Graph token → vault data plane | `401` | The vault rejects a Graph token outright |
| Vault-audience token → the same request | `403` | Authenticated; only the RBAC role was missing |
| The same Graph token → /v1.0/organization | `200` | Control: the Graph token was valid |

The 401/403 split is what makes this conclusive. Because the vault answered an authorization question when given a vault-audience token, the 401 was purely about audience. A token is bound to its audience and cannot be exchanged.

### 6.2 Azure RBAC role required

> **Important: Owner and Contributor cannot read or write secrets.** Key Vault separates its management plane (create and configure vaults) from its data plane (read and write secrets). Owner and Contributor grant the former and have NO dataActions at all. This was confirmed live: a caller holding Contributor on the vault still received 403 on a secret write.

| Role | Role definition id | Grants |
| --- | --- | --- |
| Key Vault Secrets Officer | `b86a8fe4-44ce-4948-aee5-eccb2c155cd7` | `Microsoft.KeyVault/vaults/secrets/*` — required to WRITE |
| Key Vault Secrets User | `4633458b-17de-408a-b874-0445c86b69e6` | getSecret + readMetadata — read secret values |
| Key Vault Reader | `21090545-7ca7-4776-b22c-e363652d74d2` | List keys and secrets, but NOT read secret values |
| Key Vault Crypto Officer | `14b46e9e-c2b7-41b4-b07b-48a6ebf60603` | `Microsoft.KeyVault/vaults/keys/*` — manage keys |
| Key Vault Administrator | `00482a5a-887f-4fb3-b363-3b7fe8e74483` | All data actions: keys, secrets and certificates |

Grant the automation application's SERVICE PRINCIPAL object id (not the application object id):

```powershell
az role assignment create `
    --role 'Key Vault Secrets Officer' `
    --assignee-object-id <automation-app-SP-object-id> `
    --assignee-principal-type ServicePrincipal `
    --scope /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.KeyVault/vaults/<vault>
```

> **Note: This is an Azure RBAC assignment, not a Graph permission.** `New-A365AutomationApp.ps1` cannot grant it — that script grants Microsoft Graph roles. Creating a role assignment requires Owner, User Access Administrator, or Role Based Access Control Administrator. Contributor explicitly cannot: it carries notActions `Microsoft.Authorization/*/Write`.

If the vault still uses access policies rather than RBAC, grant secret set and get there instead. The script reports which model it encountered.

### 6.3 How the vault token is obtained

| Graph authentication | Key Vault token |
| --- | --- |
| `-ClientSecret` | Client credentials against the token endpoint, with the vault scope |
| `-Certificate / -CertificateThumbprint` | A signed RS256 client assertion — the Graph SDK will not mint a token for another audience |
| `-UseManagedIdentity` | IMDS, or IDENTITY_ENDPOINT on App Service and Functions |
| `-Interactive` | A signed-in Azure session (az login or Connect-AzAccount) |
| `-AccessToken` | Not possible — supply `-KeyVaultAccessToken` |

### 6.4 What gets stored

The secret is written as a new VERSION (an existing name is never overwritten, so credential history is preserved), with the vault entry expiring at the same moment as the Entra credential itself:

| Field | Value |
| --- | --- |
| Secret name | The display name folded to the characters Key Vault allows, or `-BlueprintKeyVaultSecretName` |
| contentType | `application/x-a365-client-secret` |
| Tags | `a365Object, a365AppId, a365DisplayName` |
| Expiry | Aligned with the credential lifetime, so a stale secret is not left looking current |

The write is confirmed by reading the version back. If the vault write fails for any reason, the secret is still printed to the console — a credential that exists in Entra but was captured nowhere is unrecoverable.

## 7. Parameter reference

75 parameters, grouped by the phase they configure. Every setting for a phase carries that phase's prefix, so a parameter that belongs to a phase you did not select is reported as ignored rather than silently doing nothing.

### 7.1 Authentication and connection

| Parameter | Type | Purpose |
| --- | --- | --- |
| `-TenantId` | String | Tenant id or verified domain. Required. |
| `-ClientId` | String | Application (client) id of the automation app. |
| `-ClientSecret` | Object | Client secret. Accepts a string or SecureString. |
| `-CertificateThumbprint` | String | Certificate in the current user or machine store. |
| `-Certificate` | X509Certificate2 | An already-loaded certificate object. |
| `-CertificatePath` | String | Path to a .pfx file. |
| `-CertificatePassword` | Object | Password for the .pfx. |
| `-UseManagedIdentity` | Switch | Authenticate as an Azure managed identity. |
| `-AccessToken` | Object | A pre-obtained Microsoft Graph bearer token. |
| `-Interactive` | Switch | Sign in as a user. |
| `-SkipPermissionCheck` | Switch | Skip the app-role pre-flight check. |
| `-ScriptRoot` | String | Directory holding the step scripts, if not alongside the orchestrator. |

### 7.2 Phase 1 — Blueprint

| Parameter | Purpose |
| --- | --- |
| `-NewBlueprint` | Create a blueprint. Requires `-BlueprintDisplayName`. |
| `-UpdateBlueprint <id>` | Update an existing blueprint (appId or objectId). |
| `-UseExistingBlueprint <appId>` | Build on a blueprint that already exists, without changing it. |
| `-BlueprintDisplayName` | Display name. Mandatory with `-NewBlueprint`. |
| `-BlueprintDescription` | Description. |
| `-BlueprintSponsor` | One or more sponsors (UPN, mail, display name or object id). |
| `-BlueprintOwner` | One or more owners. |
| `-BlueprintRequireOwnerAssignment` | Fail the run if an owner cannot be assigned. |
| `-BlueprintRequiredPermission` | Hashtable array declaring API permissions. Replaces the defaults. |
| `-BlueprintGrantAdminConsent` | Consent the declared permissions and enable inheritance. Usually wanted. |
| `-BlueprintSkipInheritablePermissions` | Declare and consent, but do not make permissions inheritable. |
| `-BlueprintNewClientSecret` | Add a client secret to the blueprint. |
| `-BlueprintKeyVaultName` | Store that secret in Azure Key Vault (see section 6). |
| `-BlueprintKeyVaultSecretName` | Override the derived Key Vault secret name. |
| `-BlueprintManagedIdentityPrincipalId` | Federate a managed identity to the blueprint. |
| `-BlueprintParameter` | Hashtable of extra arguments passed straight to the blueprint script. |

### 7.3 Phase 2 — Agent identity

| Parameter | Purpose |
| --- | --- |
| `-NewAgentIdentity` | Create an agent identity. Needs a blueprint source. |
| `-UpdateAgentIdentity <id>` | Update an existing agent identity. |
| `-UseExistingAgentIdentity <objectId>` | Use an identity that already exists, for the agent user and/or registration phases. |
| `-AgentIdentityDisplayName` | Display name. Mandatory with `-NewAgentIdentity`. |
| `-AgentIdentitySponsor` | Sponsors. Mandatory with `-NewAgentIdentity`. |
| `-AgentIdentityOwner` | Owners. |
| `-AgentIdentityRequireOwnerAssignment` | Fail the run if an owner cannot be assigned. |
| `-AgentIdentityTag` | Tags, as key:value strings. |
| `-AgentIdentityCustomSecurityAttribute` | Custom security attributes, as strings or hashtables. |
| `-AgentIdentitySkipCustomSecurityAttributeValidation` | Skip pre-flight validation of attribute sets and values. |
| `-AgentIdentityRequiredPermission` | Per-identity API permissions. |
| `-AgentIdentityGrantAdminConsent` | Consent those per-identity permissions. |
| `-AgentIdentityParameter` | Extra arguments passed to the identity script. |

### 7.4 Phase 3 — Agent user

| Parameter | Purpose |
| --- | --- |
| `-NewAgentUser` | Create the agent user. App-only authentication required. |
| `-UpdateAgentUser <id-or-upn>` | Update an existing agent user. |
| `-AgentUserPrincipalName` | UPN. Mandatory with `-NewAgentUser`; there is no fallback. |
| `-AgentUserDisplayName` | Display name. |
| `-AgentUserMailNickname` | Mail nickname. |
| `-AgentUserManagerUserId` | Manager by object id. |
| `-AgentUserManagerUpn` | Manager by UPN. Mutually exclusive with the above. |
| `-AgentUserUsageLocation` | Two-letter usage location. Required before a license can be assigned. |
| `-AgentUserAssignLicense` | Assign a license. Needs a usage location and a SKU. |
| `-AgentUserLicenseSkuId` | License SKU by id. |
| `-AgentUserLicenseSkuPartNumber` | License SKU by part number, e.g. `MICROSOFT_AGENT_365_TIER_3`. |
| `-AgentUserParameter` | Extra arguments passed to the agent user script. |

There is no `-AgentUserSponsor`, `-AgentUserOwner` or `-AgentUserDescription`: an agent user takes its sponsorship from the identity it is created under.

### 7.5 Phase 4 — Registration

| Parameter | Purpose |
| --- | --- |
| `-NewAgentRegistration` | Register the agent. Needs an agent identity source. |
| `-UpdateAgentRegistration <T_id>` | Update an existing registration. |
| `-AgentRegistrationDisplayName` | Display name. Optional only when phase 2 runs in the same call, where it defaults to the identity name. |
| `-AgentRegistrationDescription` | Description. |
| `-AgentRegistrationOwner` | Owners, resolved through Graph. |
| `-AgentRegistrationOwnerId` | Owner by object id. |
| `-AgentRegistrationIdentityId` | Register an existing identity; on update, re-point the registration at a DIFFERENT identity. |
| `-AgentRegistrationAuth` | `Same` (default) or `Interactive` — sign in as a user for this phase only. |
| `-AgentRegistrationParameter` | Extra arguments passed to the registration script. |

### 7.6 Removal

| Parameter | Purpose |
| --- | --- |
| `-RemoveBlueprint <id>` | Delete a blueprint. Refuses if agent identities exist unless `-RemoveForce`. |
| `-RemoveAgentIdentity <objectId>` | Delete an agent identity, and nothing else. |
| `-RemoveAgentUser <objectId>` | Delete an agent user. Destroys the mailbox and releases the UPN. |
| `-RemoveAgentRegistration <T_id>` | Delete the admin center registration, and nothing else. |
| `-RemoveInspectOnly` | Resolve and report everything that would be deleted, then exit. Run this first. |
| `-RemovePermanent` | Purge from the recycle bin after the soft delete. IRREVERSIBLE. |
| `-RemoveForce` | Suppress confirmation, and allow a blueprint removal to cascade. |

> **Important: Each removal script deletes exactly one kind of object.** Correct retirement order is registration → agent user → agent identity → blueprint. The agent user must precede the identity because it is discovered THROUGH the identity that owns it; deleting the identity first orphans the user, which still holds its license and UPN.

### 7.7 Logging and output

| Parameter | Purpose |
| --- | --- |
| `-LogPath` | Directory for run logs. One file per script per run. |
| `-LogIncludeSecrets` | Include plain-string client secrets and passwords in the log. SecureString values remain redacted. Off by default. |
| `-LogCorrelationId` | Correlation id shared by every log in the run. Generated if omitted. |
| `-OutputJsonPath` | Write a machine-readable JSON report. |
| `-IncludeBlueprintSecretsInOutput` | Include the blueprint client secret in that report. |
| `-KeyVaultAccessToken` | A vault-audience token, for the cases where one cannot be derived. |

> **Security: Bearer tokens are always redacted.** Credential redaction is on by default. `-LogIncludeSecrets` opts in to recording plain-string client secrets and passwords, but SecureString values, tokens, and anything JWT-shaped are removed unconditionally — a captured token is directly replayable until it expires.

## 8. Worked examples

### 8.1 Full pipeline — all four objects

```powershell
$env:A365_CLIENT_SECRET = '<secret>'

.\A365-AutomationOrchestrator.ps1 `
    -TenantId     <tenant-id> `
    -ClientId     <automation-app-id> `
    -ClientSecret $env:A365_CLIENT_SECRET `
    -NewBlueprint `
        -BlueprintDisplayName 'Contoso Helpdesk Blueprint' `
        -BlueprintDescription 'Helpdesk agent blueprint' `
        -BlueprintSponsor     sponsor@contoso.com `
        -BlueprintOwner       owner@contoso.com `
        -BlueprintNewClientSecret `
        -BlueprintKeyVaultName MyKeyVault `
        -BlueprintGrantAdminConsent `
    -NewAgentIdentity `
        -AgentIdentityDisplayName 'Contoso Helpdesk Identity' `
        -AgentIdentitySponsor     sponsor@contoso.com `
        -AgentIdentityTag         'AgentBusinessUnit:HR','AgentEnvironment:Production' `
    -NewAgentUser `
        -AgentUserPrincipalName helpdesk.agent@contoso.com `
        -AgentUserDisplayName   'Contoso Helpdesk' `
        -AgentUserManagerUpn    manager@contoso.com `
        -AgentUserUsageLocation US `
        -AgentUserAssignLicense -AgentUserLicenseSkuPartNumber MICROSOFT_AGENT_365_TIER_3 `
    -NewAgentRegistration `
        -AgentRegistrationDisplayName 'Contoso Helpdesk Agent' `
        -AgentRegistrationOwner       owner@contoso.com `
    -OutputJsonPath 'C:\A365\helpdesk.json' `
    -LogPath        'C:\A365\Logs'
```

### 8.2 New agent identity on an existing blueprint

```powershell
.\A365-AutomationOrchestrator.ps1 `
    -TenantId <tenant-id> -Interactive `
    -UseExistingBlueprint <blueprint-appId> `
    -NewAgentIdentity `
        -AgentIdentityDisplayName 'Contoso Helpdesk Identity 02' `
        -AgentIdentitySponsor     sponsor@contoso.com `
    -LogPath 'C:\A365\Logs'
```

### 8.3 Agent user and registration on an existing identity

One `-UseExistingAgentIdentity` serves both phases:

```powershell
.\A365-AutomationOrchestrator.ps1 `
    -TenantId <tenant-id> -ClientId <app-id> -ClientSecret $env:A365_CLIENT_SECRET `
    -UseExistingAgentIdentity <agent-identity-objectId> `
    -NewAgentUser `
        -AgentUserPrincipalName agent02@contoso.com `
        -AgentUserDisplayName   'Contoso Agent 02' `
        -AgentUserUsageLocation US `
    -NewAgentRegistration `
        -AgentRegistrationDisplayName 'Contoso Agent 02'
```

### 8.4 Dry run before deleting anything

```powershell
.\A365-AutomationOrchestrator.ps1 `
    -TenantId <tenant-id> -Interactive `
    -RemoveAgentRegistration <T_registration-id> `
    -RemoveAgentUser         <agent-user-objectId> `
    -RemoveAgentIdentity     <agent-identity-objectId> `
    -RemoveInspectOnly
```

Then repeat without `-RemoveInspectOnly`, adding `-RemoveForce` for an unattended run.

### 8.5 Preview any run without writing

`-WhatIf` is supported throughout. A dry run still produces a log file, so you can inspect exactly which Graph calls would have been made:

```powershell
.\A365-AutomationOrchestrator.ps1 ... -WhatIf -LogPath 'C:\A365\Logs'
```

## 9. Logging and output

### 9.1 Log files

`-LogPath` produces one log per script per run. Every file in a run shares a correlation id, and the orchestrator log ends with an index naming each child log and the phase that produced it — including grandchildren, such as the cascade a blueprint removal delegates to.

```text
<script-name>-yyyy-MM-dd_HH-mm-ss-<correlationId>.log
```

Windows file names cannot contain a colon, so the time is written HH-mm-ss. Logs are flushed line by line rather than buffered, and a trap writes the run footer even when the script fails, so the log is complete up to the moment of a crash.

Each log records:

- Every Graph call: method, URI, request body, response, HTTP status and duration.
- Retry decisions and tolerated status codes.
- The full argument list handed to each child script.
- Phase banners and per-step results, so the log explains what the run decided as well as what it called.

### 9.2 JSON report

`-OutputJsonPath` writes a machine-readable summary containing:

- identifiers: blueprint appId and objectId, agent identity id, fmi_path, agent user id and UPN, registration id.
- phases: for each phase, the script, status, timing, parameters and identifiers.
- steps: the per-step result table shown on screen.
- consentActionRequired: any permissions still needing an administrator, with the consent link and required role. Empty when nothing is outstanding.
- keyVault: the vault, secret name, version and read-back verification, when `-BlueprintKeyVaultName` was used.
- tokenRequest: a ready-to-use token request for the provisioned agent identity.

## 10. Troubleshooting

These are failure modes confirmed against a live tenant. Each produces an error that does not obviously point at its cause.

| Symptom | Cause and fix |
| --- | --- |
| 403 with no permission named, when assigning owners on a blueprint principal | Needs `AgentIdentityBlueprintPrincipal.ReadWrite.All`. `Application.ReadWrite.All` does not authorize that call, so granting more `Application.*` permissions will not help. |
| 403 'Insufficient privileges' when creating an agent user | Needs `AgentIdUser.ReadWrite.All`. `User.ReadWrite.All` is not enough. |
| 403 when assigning custom security attributes | Needs `CustomSecAttributeAssignment.ReadWrite.All` and `CustomSecAttributeDefinition.Read.All`. These are not implied by any `Directory.*` or `Application.*` role, nor held by Global Administrator by default. |
| Consent appears to succeed but the agent gets no claims | The permission was declared but not consented. Check `consentFailures` in the JSON report, and use the printed admin consent link. |
| Registration 'Deleted' but the agent is still listed | The registration id has two spellings: a bare GUID and a tenant-prefixed `T_<guid>`. DELETE answers 404 for an unknown id, and 404 is the desired end state, so the wrong spelling reports success. The scripts probe both and report a 404 as 'Already absent' rather than 'Deleted'. |
| Key Vault write returns 401 | A Microsoft Graph token was presented. Key Vault is a different audience. Use `-KeyVaultAccessToken`, or authenticate app-only so a vault token can be minted. |
| Key Vault write returns 403 while you hold Owner or Contributor | Those are management-plane roles with no dataActions. Grant Key Vault Secrets Officer on the vault. |
| `az role assignment create` fails with `AuthorizationFailed` | Creating role assignments needs Owner, User Access Administrator, or RBAC Administrator. Contributor carries notActions `Microsoft.Authorization/*/Write`. |
| A role assignment was just made but still returns 403 | Azure RBAC changes are not enforced instantly. Allow up to about 15 minutes, and obtain a fresh token — a cached token issued before the change carries stale context. |
| 'Ignored: -X applies to ... which was not selected' | A parameter was supplied for a phase that did not run. Harmless, but usually means a switch was forgotten. |

## 11. Script inventory

Every script this guide covers. The orchestrator invokes the four `New-A365Agent*` provisioning scripts and the four `Remove-A365*` scripts directly, passing each phase the identifiers the previous one produced. `New-A365AutomationApp.ps1` is a one-time prerequisite, run by hand before the first pipeline run; the orchestrator never calls it. Keep these files together in one folder, or point at them with `-ScriptRoot`.

### 11.1 Provisioning

| Script | Purpose |
| --- | --- |
| `A365-AutomationOrchestrator.ps1` | Runs the whole pipeline; makes no Graph calls of its own |
| `New-A365AutomationApp.ps1` | PREREQUISITE, not invoked by the orchestrator. Creates the automation app and grants its Graph permissions. |
| `New-A365AgentBlueprint.ps1` | Creates and configures a blueprint and its principal |
| `New-A365AgentIdentity.ps1` | Creates an agent identity on an existing blueprint |
| `New-A365AgentUser.ps1` | Creates an agent user, with manager and license |
| `New-A365AgentRegistration.ps1` | Registers the agent in the admin center and registry |
| `A365-BulkOnboarding.ps1` | Provisions many agents from one CSV file; drives the orchestrator once per row, in dependency order |

### 11.2 Removal

| Script | Purpose |
| --- | --- |
| `Remove-A365Blueprint.ps1` | Deletes a blueprint and its principal; can cascade with -Force |
| `Remove-A365AgentIdentity.ps1` | Deletes agent identities, and nothing else |
| `Remove-A365AgentUser.ps1` | Deletes agent users, and nothing else |
| `Remove-A365AgentRegistration.ps1` | Deletes the admin center registration, and nothing else |

### 11.3 Updating a single object

These four scripts are an alternative interface to the orchestrator's `-UpdateX` parameters, not extra steps. Each one forwards to the matching `New-A365*.ps1` in its update mode, so both routes run exactly the same code. Use the orchestrator when one run changes several objects; use the dedicated script when it changes one. They are listed in the orchestrator's own help.

| Script | Purpose |
| --- | --- |
| `Update-A365Blueprint.ps1` | Changes an existing blueprint. Takes `-BlueprintId` (application or object id). |
| `Update-A365AgentIdentity.ps1` | Changes an existing agent identity. Takes `-AgentIdentityId`. |
| `Update-A365AgentUser.ps1` | Changes an existing agent user. Takes `-AgentUserId`. |
| `Update-A365AgentRegistration.ps1` | Changes an existing registration. Takes `-RegistrationId`. |

Each takes `-TenantId` and its own object id as the only mandatory parameters, plus the usual authentication parameters. ONLY THE ATTRIBUTES YOU PASS ARE WRITTEN: forwarding is decided on whether a parameter was bound, never on whether a variable holds a value, so a parameter left off the command line cannot overwrite what is already stored.

Merge behavior differs by attribute, and it is not uniform. Custom security attributes merge per attribute, though a multi-valued attribute that is written is replaced wholesale. Agent identity tags merge — the script reads the current set and writes the union, so adding one tag does not drop the others. Agent identity owners are additive. Registration ownerIds REPLACE the whole collection, so list every owner you want to keep.

Like the step scripts they wrap (section 7.1), `-ClientSecret`, `-CertificatePassword` and `-AccessToken` on all four scripts accept either a plain string or a `SecureString` and are forwarded to the step script unchanged - the exact object bound on the command line is the exact object the step script receives, never re-typed, copied or stringified in between. Plain strings remain supported for compatibility but still produce the step script's own command-line-exposure warning.

## 12. Quick start checklist

1. **Install PowerShell 7 and the Graph module**

   ```powershell
   Install-Module Microsoft.Graph.Authentication -Scope CurrentUser
   ```

2. **Create the automation application**

   ```powershell
   .\New-A365AutomationApp.ps1 -TenantId <tid> -DisplayName 'A365 Provisioning Automation' -Scenario All -NewClientSecret
   ```

3. **Have an administrator consent the Graph app roles**

   Requires `Privileged Role Administrator` or `Global Administrator`. The script prints a consent link if it cannot consent them itself.

4. **Store the client secret safely**

   ```powershell
   $env:A365_CLIENT_SECRET = '<secret>'
   ```

   — or use a certificate, which is preferred in production.

5. **(Optional) Grant Key Vault access**

   ```powershell
   az role assignment create --role 'Key Vault Secrets Officer' --assignee-object-id <SP-object-id> --assignee-principal-type ServicePrincipal --scope <vault-resource-id>
   ```

6. **Dry run first**

   ```powershell
   .\A365-AutomationOrchestrator.ps1 ... -WhatIf -LogPath 'C:\A365\Logs'
   ```

7. **Run for real, with a log and a report**

   ```powershell
   .\A365-AutomationOrchestrator.ps1 ... -LogPath 'C:\A365\Logs' -OutputJsonPath 'C:\A365\run.json'
   ```

8. **Check the report for outstanding work**

   `summary.consentActionRequired` lists anything an administrator still has to finish. It is empty when the run is complete.

> **Security reminders.** Prefer a certificate or a federated managed identity over a client secret in production. Client secrets created by these scripts are intended for development. If a secret is ever exposed — in a transcript, a chat, a ticket or a screenshot — rotate it immediately: possession of the secret is possession of every permission the application holds.

## 13. Bulk CSV onboarding (A365-BulkOnboarding.ps1)

### 13.1 What it does

`A365-AutomationOrchestrator.ps1` provisions one agent per invocation. `A365-BulkOnboarding.ps1` reads a CSV describing many blueprints, agent identities, agent users and registrations, works out which rows depend on which, and calls the orchestrator once per row that needs creating - always in an order where a parent runs before its children - threading the id each parent produces into the children that need it.

The orchestrator is invoked in-process with the PowerShell call operator, in the same runspace as the wrapper script. No new process is started and nothing is serialized: a SecureString client secret or an X509Certificate2 passed to `-ClientSecret` / `-Certificate` stays the exact same live object for every row that uses it.

### 13.2 Parameters

| Parameter | Purpose |
| --- | --- |
| `-CsvPath` | Path to the bulk onboarding CSV. Mandatory. |
| `-TenantId` | The Microsoft Entra tenant every row is provisioned into. Mandatory. |
| `-ScriptRoot` | Directory containing `A365-AutomationOrchestrator.ps1`. Defaults to this script's own directory - keep the two files together, or pass this explicitly. |
| `-OutputJsonPath` | Write one aggregate JSON report (`A365BulkProvisioningRunReport`) covering every row (see 13.6). |
| `-IncludeBlueprintSecretsInOutput` | Include any blueprint client secret in the aggregate report. Off by default, like the orchestrator's own switch of the same name. |
| `-ClientId`, `-ClientSecret`, `-CertificateThumbprint`, `-Certificate`, `-CertificatePath`, `-CertificatePassword`, `-UseManagedIdentity`, `-AccessToken`, `-Interactive`, `-SkipPermissionCheck` | Authentication, forwarded verbatim to every row's orchestrator call. Exactly one method must be supplied - see section 5 and section 7.1. |
| `-LogPath`, `-LogIncludeSecrets`, `-LogCorrelationId` | Logging, forwarded to every row's orchestrator call so every log file produced by the run - the orchestrator's and every step script's - shares one correlation id. A correlation id is generated when omitted, exactly like the orchestrator. |

`A365-BulkOnboarding.ps1` supports `-WhatIf` and `-Confirm` (`ConfirmImpact = 'Medium'`). The CSV is always read and validated first, before the `-WhatIf`/`-Confirm` prompt; under `-WhatIf` (or a declined `-Confirm`), the full dependency plan is printed and no orchestrator call is made. For a real run, authentication is checked once before any row is attempted: exactly one authentication method must be supplied, and a CSV with AgentUser rows is refused unless that method is app-only (client secret, certificate, or managed identity) - the orchestrator would otherwise reject every AgentUser row identically.

### 13.3 CSV schema

One row per object. Every row always has these four columns, blank when not applicable:

| Column | Meaning |
| --- | --- |
| ObjectType | Blueprint \| AgentIdentity \| AgentUser \| AgentRegistration |
| Key | Unique name for this row (case-insensitive); referenced by child rows |
| ParentKey | The Key of this row's parent. Blank for Blueprint (the root) |
| ExistingId | Blueprint appId or AgentIdentity objectId of an object that already exists. Marks the row as a reference only - nothing is created or changed for it |

Every other column maps to one `-Blueprint*` / `-AgentIdentity*` / `-AgentUser*` / `-AgentRegistration*` parameter of the orchestrator. A column that does not apply to a row's ObjectType must be left blank; it is rejected outright, not silently ignored.

| Column | Applies to | Notes |
| --- | --- | --- |
| DisplayName | Blueprint, AgentIdentity, AgentUser, AgentRegistration | Required to create a Blueprint, AgentIdentity, or AgentRegistration |
| Sponsor (semicolon-separated) | Blueprint, AgentIdentity | Required to create |
| Owner (semicolon-separated) | Blueprint, AgentIdentity, AgentRegistration | |
| OwnerId | AgentRegistration | Owner by object id; a separate column from Owner, resolved by Graph |
| RequiredPermissionJson (JSON array) | Blueprint, AgentIdentity | Escape hatch for declared API permissions |
| PrincipalName | AgentUser | Required; must be a valid UPN |
| ManagerUserId (GUID) / ManagerUpn | AgentUser | Specify one, not both |
| AssignLicense / LicenseSkuId / LicenseSkuPartNumber / UsageLocation | AgentUser | UsageLocation and a SKU (LicenseSkuId or LicenseSkuPartNumber) are both required when AssignLicense is true |
| Auth (Same \| Interactive) | AgentRegistration | Matches `-AgentRegistrationAuth` |
| ParameterJson (JSON object) | all types | Allowlisted advanced parameters only: Blueprint ClientSecretLifetimeDays, ExposedScopeValue, FederatedCredentialName; AgentIdentity Disabled; AgentUser DisabledPlans, MaxRetries, RetryDelaySeconds, NoDefaultOwner, NoOwnershipSelfHeal; AgentRegistration ManagedByAppId, RolePropagationDelaySeconds, SkipDisplayNameNormalization. Names must be complete; tenant, credential, authentication, parent, action, output and logging parameters are rejected. |

### 13.4 Validation and dependency ordering

The whole file is checked before a single row is executed: required headers, a recognized ObjectType, unique keys, legal ExistingId / ParentKey shapes, valid GUIDs and UPNs, the fields required to create each type, a parent of the right type, no row parented to an AgentUser or AgentRegistration row (they are leaves), and no dependency cycle. Every error found is reported together, with its row and column.

Rows then run in an order where every parent completes before its children, preserving the CSV's own order among rows that do not depend on each other. A row whose parent did not resolve to a live id is never sent to Graph - it is marked SkippedDependency, and independent trees elsewhere in the file still run. A row that itself fails is marked Failed and its descendants are then skipped the same way.

### 13.5 Usage examples

```powershell
.\A365-BulkOnboarding.ps1 -CsvPath .\sample-bulk-onboarding.csv -TenantId <tid> -ClientId <appId> -ClientSecret $env:A365_CLIENT_SECRET -WhatIf
```

Validates the CSV and prints the full dependency plan without calling Graph.

```powershell
.\A365-BulkOnboarding.ps1 -CsvPath .\sample-bulk-onboarding.csv -TenantId <tid> -ClientId <appId> -ClientSecret $env:A365_CLIENT_SECRET -LogPath 'C:\A365\Logs' -OutputJsonPath 'C:\A365\bulk-run.json'
```

Provisions every row, sharing one log correlation id across every child script, and writes one aggregate report.

### 13.6 Aggregate report and redaction

`-OutputJsonPath` writes exactly one aggregate report for the whole run - the orchestrator is never given its own `-OutputJsonPath`, so no per-row report files are produced. Each row's result is redacted the same way the orchestrator redacts its own report: secrets such as a blueprint client secret are omitted unless `-IncludeBlueprintSecretsInOutput` is passed. Authentication inputs are never written to the report, with or without that switch.
