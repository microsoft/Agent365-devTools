# Non-Admin Setup Failures Analysis

**Date:** 2026-03-16
**Test Account:** `sellakdev@a365preview070.onmicrosoft.com` (Contributor on subscription + resource group, no admin roles)
**Command:** `a365 setup all`
**Trace ID:** `d7191831-e307-4d4c-beb9-01c7d21e0574`

---

## Failure 1: Website Contributor Role Assignment (Warning)

**Severity:** Low — non-blocking, warning only
**Symptom:**
```
Could not assign Website Contributor role to user. Diagnostic logs may not be accessible.
Error: (AuthorizationFailed) The client '...' does not have authorization to perform action
'Microsoft.Authorization/roleAssignments/write' over scope
'/subscriptions/.../providers/Microsoft.Web/sites/sk70devdotnetagent-webapp/providers/Microsoft.Authorization/roleAssignments/...'
```

**Root Cause:**
The CLI tries to self-assign the "Website Contributor" role on the newly created web app via `az role assignment create`. This requires `Microsoft.Authorization/roleAssignments/write`, which is granted by **Owner** or **User Access Administrator** — not Contributor. The non-admin user has Contributor only.

**Code Location:**
`src/.../Commands/SetupSubcommands/InfrastructureSubcommand.cs` — `HandleIdentityAndPermissionsAsync()`

**Impact:**
Cannot access Azure diagnostic logs or log streams for the web app. Deployment and agent functionality are not affected.

**Remediation:**
Azure Portal → Web App → Access Control (IAM) → Add Role Assignment → "Website Contributor" → assign to the user.

**Improvement Needed:**
The error message is good. However, the code should detect `AuthorizationFailed` specifically and skip the verification step that follows (currently it still attempts to verify a role it knows wasn't assigned, producing a second redundant warning).

---

## Failure 2: Federated Identity Credential Creation (Warning, but functionally critical)

**Severity:** High — non-blocking warning in CLI, but **breaks agent authentication at runtime**
**Symptom:**
```
ERROR: Failed to create federated credential 'sk70devdotnetagentBlueprint-MSI': Insufficient privileges to complete the operation.
(retried 10 times, ~8 minutes total wait)
[WARN] Federated Identity Credential creation failed - you may need to create it manually in Entra ID
```

**Root Cause:**
Creating a federated identity credential on an `agentIdentityBlueprint` application requires specific Graph API permissions that are not delegated to a non-admin user, even if they are the app owner. The operation uses the delegated token of the interactive user, which lacks the necessary permission for this write operation on blueprint apps.

**Code Location:**
`src/.../Services/FederatedCredentialService.cs` — two endpoints attempted:
1. `/beta/applications/{blueprintObjectId}/federatedIdentityCredentials`
2. `/beta/applications/microsoft.graph.agentIdentityBlueprint/{blueprintObjectId}/federatedIdentityCredentials`

Both return `Insufficient privileges` for non-admin users.

**Impact:**
The managed identity (MSI) of the web app **cannot authenticate** to the Agent Blueprint using workload identity federation. The agent will fail to acquire tokens at runtime. This is a critical path for the deployed agent to function.

**Remediation:**
A Global Admin or an account with Application Administrator role must create the federated credential manually in Entra ID portal, or by running `a365 setup blueprint` with an elevated account.

**Improvement Needed:**
1. The retry loop (10 retries with exponential backoff up to 60s each) wastes ~8 minutes for a non-admin user — the 403 "Insufficient privileges" error is deterministic and should **not be retried**. The code should fail fast on this specific error.
2. The severity in the summary should be elevated — "may need to create it manually" understates the consequence (agent will not work at runtime).
3. Provide a direct Azure Portal link or `az` command for manual creation.

---

## Failure 3: Admin Consent Timeout (Warning)

**Severity:** Medium — non-blocking, but required for blueprint application scopes
**Symptom:**
```
Waiting for admin consent to be granted. Open the URL above in a browser... (timeout: 180s)
Still waiting for admin consent... (63s / 180s).
Still waiting for admin consent... (124s / 180s).
Admin consent was not detected within 180s. Continuing...
```

**Root Cause:**
The blueprint application requires admin consent for `Mail.ReadWrite`, `Mail.Send`, `Chat.ReadWrite`, `User.Read.All`, and `Sites.Read.All`. Granting admin consent via the `/adminconsent` endpoint requires a **Global Administrator**. A non-admin user opening this URL will either be blocked or prompted with a "request approval" flow that does not complete the consent.

The CLI polls a Graph API endpoint to detect consent completion — when the non-admin user clicks the consent URL, consent is never actually granted, so the poll times out.

**Code Location:**
`src/.../Commands/SetupSubcommands/BlueprintSubcommand.cs` — `EnsureAdminConsentAsync()`, lines ~1414–1475

**Impact:**
The blueprint application's delegated permissions are not consented. Agent instances will not be able to access Microsoft Graph resources (mail, chat, SharePoint) at runtime.

**Improvement Needed:**
1. Detect whether the authenticated user is a Global Admin **before** launching the browser and waiting 180 seconds. If not, immediately output a clear message: "Admin consent requires a Global Administrator. Please share this URL with your admin: <url>". Skip the polling loop entirely for non-admin users.
2. The 180-second timeout is a poor UX even for admins. Consider adding a keyboard interrupt to cancel and continue early.

---

## Failure 4: Microsoft Graph Inheritable Permissions (Warning, functionally critical)

**Severity:** High — non-blocking warning, but **breaks agent Graph access at runtime**
**Symptom (Summary only — no detailed log line):**
```
[WARN] Microsoft Graph inheritable permissions: Microsoft Graph inheritable permissions failed to configure
Recovery: Run 'a365 setup blueprint' to retry
```

**Root Cause:**
This is a **downstream consequence of Failure 3** (admin consent timeout). The CLI attempts to configure inheritable permissions on the blueprint for Microsoft Graph scopes after the consent step. Because admin consent was not granted, the Graph API call to set inheritable permissions on the `agentIdentityBlueprint` also fails with an authorization error. The failure is caught silently and reported only in the final summary.

**Code Location:**
`src/.../Commands/SetupSubcommands/BlueprintSubcommand.cs` `EnsureAdminConsentAsync()` → `SetupHelpers.EnsureResourcePermissionsAsync()` → `AgentBlueprintService.SetInheritablePermissionsAsync()`
`src/.../Services/AgentBlueprintService.cs` lines ~330–431

**Impact:**
Agent instances will not inherit Microsoft Graph permissions, so any Graph-dependent operations (reading mail, sending chat messages, accessing SharePoint) will fail at runtime.

**Improvement Needed:**
1. The summary message "Microsoft Graph inheritable permissions failed to configure" has no context in the log body — the actual error (HTTP status, response) is swallowed before reaching the user. Surface the underlying error.
2. This failure should be linked to Failure 3 in the output: "Inheritable permissions require admin consent to be granted first."

---

## Failure 5: Messaging Endpoint Registration (Hard Failure)

**Severity:** Critical — **blocking failure**, endpoint not registered
**Symptom:**
```
ERROR: Failed to call create endpoint. Status: BadRequest
ERROR: Error response: {"error":"Invalid roles","message":"User does not have a required role"}
ERROR: Failed to register blueprint messaging endpoint
Endpoint registration failed: [SETUP_VALIDATION_FAILED] Blueprint messaging endpoint registration failed
```

**Root Cause:**
The Agent 365 service (the external endpoint being called) rejects the request because the authenticated user (`sellakdev@a365preview070.onmicrosoft.com`) does not have a required role in the **Agent 365 service itself** — not in Azure. This is separate from Azure RBAC. The service enforces its own role requirements, and the non-admin/contributor-only user does not have those roles assigned in the Agent 365 backend.

**Code Location:**
`src/.../Services/BotConfigurator.cs` — `CreateEndpointWithAgentBlueprintAsync()`, lines ~129–176

**Impact:**
The messaging endpoint is not registered. The agent **cannot receive messages** from Copilot Studio or Teams. This is the most critical failure — the agent cannot be invoked at all.

**Improvement Needed:**
1. **The `BadRequest` error handler does not cover "Invalid roles"** — the existing error message says "ensure that the Agent 365 CLI is supported in the selected region... and that your web app name is globally unique", which is completely wrong guidance for this error. The `Invalid roles` response is a distinct case that needs its own handling branch.
2. The error message should explicitly state: "Your account does not have the required role in the Agent 365 service to register messaging endpoints. Contact your Agent 365 tenant administrator to assign the necessary role."
3. This failure should be clearly flagged as "Cannot proceed without resolving this" since the agent is non-functional without the endpoint.

---

## Summary Table

| # | Failure | Severity | Blocking | Root Cause | Retried? | Error Handling Quality |
|---|---------|----------|----------|------------|----------|----------------------|
| 1 | Website Contributor role assignment | Low | No | Contributor lacks `roleAssignments/write` | No | Acceptable |
| 2 | Federated Identity Credential creation | High | No (but runtime-critical) | Non-admin lacks Graph write permission on blueprint apps | Yes — 10x, ~8 min wasted | Poor — should fail fast on 403 |
| 3 | Admin consent timeout | Medium | No (but runtime-critical) | Non-admin cannot grant tenant-wide consent | N/A — poll times out | Poor — no pre-check for admin role |
| 4 | Microsoft Graph inheritable permissions | High | No (but runtime-critical) | Downstream of Failure 3; also authorization error | Yes — 5x verify | Poor — error swallowed, not surfaced |
| 5 | Messaging endpoint registration | Critical | Yes | Non-admin lacks Agent 365 service role | No | Poor — wrong error message for "Invalid roles" |

---

## Net Result for Non-Admin User

After `a365 setup all` completes, the following are true:
- Infrastructure (App Service, Web App, Managed Identity) was created successfully.
- Agent Blueprint application was created in Entra ID.
- MCP Tools, Messaging Bot API, and Observability API inheritable permissions were configured.
- **Federated credential (MSI → Blueprint) is missing** — agent cannot authenticate.
- **Admin consent not granted** — agent cannot access Microsoft Graph.
- **Microsoft Graph inheritable permissions not set** — agent cannot inherit Graph access.
- **Messaging endpoint not registered** — agent cannot receive messages.

The agent infrastructure exists but the agent is **entirely non-functional** for a non-admin user after running `setup all`.

---

## Recommended Actions

### For the Non-Admin User (Immediate)
1. Ask a **Global Administrator** to:
   - Grant admin consent via the URL shown in the log
   - Assign the required Agent 365 service role to the user account
2. Ask an account with **Application Administrator** to:
   - Create the federated identity credential manually (MSI `daf9cc09-...` on blueprint `51d7a5d6-...`)
3. Re-run `a365 setup blueprint --endpoint-only` after roles are granted.

### For the CLI (Code Improvements)
1. **Fail fast on deterministic 403s** in the FIC retry loop (Failure 2).
2. **Pre-check admin role** before launching the 180s consent poll (Failure 3).
3. **Surface underlying errors** from inheritable permissions failure in the log body, not just the summary (Failure 4).
4. **Add "Invalid roles" handler** to the endpoint registration error path with correct guidance (Failure 5).
5. **Upgrade severity** of Failures 2, 4, 5 in the summary — these are not "warnings", they result in a non-functional agent.
