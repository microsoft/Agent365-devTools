# Now Goal — Agent ID Admin `setup all` Issues (2026-03-18)

Three issues observed when running `a365 setup all` as `sellakagentadmin@a365preview070.onmicrosoft.com`
(Agent ID Administrator role, not Global Administrator).

---

## Issue 1 — Wrong Graph user picked up (WAM ignores login hint)

**Status: FIXED**

**Symptom:**
```
Successfully authenticated to Microsoft Graph
Current user: Sellakumaran Developer <sellakdev@a365preview070.onmicrosoft.com>
```
Running as `sellakagentadmin` but the Graph token belongs to `sellakdev`.

**Root cause:**
`WithLoginHint` is advisory only — WAM authenticates as the primary OS-level signed-in
Windows account and ignores the hint. `InteractiveGraphAuthService` was also creating its
own `MsalBrowserCredential` without any login hint.

**Fix applied:**
- `MsalBrowserCredential.cs`: WAM path now uses `WithAccount(account)` when the account is
  found in the MSAL cache. Falls back to `WithPrompt(Prompt.SelectAccount)` when not found.
- `InteractiveGraphAuthService.cs`: Runs `az account show` to resolve current user UPN and
  passes it as login hint when constructing `MsalBrowserCredential`.

**Verified:** Log confirms `Current user: Sellakumaran AgentAdmin <sellakagentadmin@...>`.

---

## Issue 2 — Owner assignment fails: `Directory.AccessAsUser.All` in token

**Status: FIXED**

**Symptom:**
```
ERROR: Failed to assign current user as blueprint owner: 400 Bad Request
Agent APIs do not support calls that include the Directory.AccessAsUser.All permission.
```

**Root cause:**
Post-creation owner verification used a `.default` token which bundles `Application.ReadWrite.All`
→ Entra adds `Directory.AccessAsUser.All`. Agent Blueprint API rejects any token with this scope.

**Fix applied:**
`BlueprintSubcommand.cs`: When `owners@odata.bind` is set during blueprint creation (sponsor
user known), skip the post-creation owner verification entirely — ownership is set atomically
at creation. Portal confirms `sellakagentadmin` is listed as owner.

**Verified:** Log shows `Owner set at creation via owners@odata.bind — skipping post-creation verification`.

---

## Issue 3 — `Authorization.ReadWrite` scope not found on Messaging Bot API

**Status: RESOLVED (symptom of Issue 1)**

**Symptom:**
```
ERROR: Graph POST https://graph.microsoft.com/v1.0/oauth2PermissionGrants failed:
The Entitlement: Authorization.ReadWrite can not be found on resourceApp: 5a807f24-c9de-44ee-a3a7-329e88a00ffc.
```

**Resolution:** Once Issue 1 was fixed (correct user authenticated), all inheritable permissions
configured successfully with no errors. The error was caused by failed OAuth2 grants running
under the wrong user, not an invalid scope name.

---

## Issue 4 — Client secret creation fails

**Status: FIXED**

**Symptom:**
```
ERROR: Failed to create client secret: Forbidden - Authorization_RequestDenied
```

**Root cause (multi-step):**
1. Token acquired with `https://graph.microsoft.com/.default` bundles `Application.ReadWrite.All`
   → Entra adds `Directory.AccessAsUser.All` → Agent Blueprint API rejects → 403.
2. Switching to `AgentIdentityBlueprint.AddRemoveCreds.All` scope: not yet individually consented,
   MSAL fell back to cached `.default` token → same 403.
3. `AcquireMsalGraphTokenAsync` created `MsalBrowserCredential` **without a login hint** — WAM
   silently returned the cached `sellakdev` token (OS default account). `sellakdev` is not the
   blueprint owner → 403.
4. Entra eventual consistency: `addPassword` called ~8s after creation returns 404 ResourceNotFound
   (new app not yet replicated across all Graph API replicas).

**Fix applied:**
- Token acquired with specific scope `AgentIdentityBlueprint.ReadWrite.All` (already consented;
  does not bundle `Directory.AccessAsUser.All`).
- `AcquireMsalGraphTokenAsync` now accepts `loginHint` parameter; call site resolves it via
  `InteractiveGraphAuthService.ResolveAzLoginHintAsync()` so WAM targets the az-logged-in user.
- `addPassword` wrapped in `RetryHelper.ExecuteWithRetryAsync` with `shouldRetry: StatusCode == NotFound`,
  5 retries, 5s base delay (exponential backoff).

**Verified:** Log confirms `Client secret created successfully!` as `sellakagentadmin`.

---

## Issue 5 — Service Principal not created for Agent Blueprint

**Status: FIXED**

**Symptom:**
Blueprint created by main CLI has both Application + Service Principal in Entra portal.
Blueprint created by this branch's CLI (as `sellakagentadmin`) has only Application — no Service Principal.

**Root cause:**
`AcquireMsalGraphTokenAsync` at blueprint creation call site (line 894 of `BlueprintSubcommand.cs`)
created `MsalBrowserCredential` without a login hint. WAM silently returned the cached `sellakdev`
token (OS default account). That token included newly-consented `AgentIdentityBlueprint.*` scopes,
which Entra rejects for `POST /v1.0/servicePrincipals` on multi-tenant apps with error:
"When using this permission, the backing application of the service principal being created must
in the local tenant."

**Fix applied:**
`BlueprintSubcommand.cs`: Blueprint creation call now resolves `blueprintLoginHint` via
`InteractiveGraphAuthService.ResolveAzLoginHintAsync()` and passes it to
`AcquireMsalGraphTokenAsync`. WAM now targets the az-logged-in user instead of OS default account.

**Verified:** Portal shows `sk70dotnetagent2 Blueprint` with both Application + Service Principal.

---

## Issue 6 — Consent URL includes non-Graph scopes (AADSTS650053 / AADSTS500011)

**Status: FIXED**

**Symptom:**
Opening the generated consent URL failed with:
- AADSTS650053: `McpServers.Mail.All` / `Authorization.ReadWrite` does not exist on resource `00000003-...` (Graph)
- AADSTS500011: Messaging Bot API SP not found via `api://{appId}` identifier URI

**Root cause:**
`BatchPermissionsOrchestrator.cs` Phase 3 was building the consent URL by iterating all resource specs.
Non-Graph scopes (`Authorization.ReadWrite`, `McpServers.Mail.All`, `AgentIdentityBlueprint.*`)
are blueprint-specific inheritable permissions — not standard OAuth2 delegated scopes.
They cannot appear in a `/v2.0/adminconsent` `scope=` parameter at all; only Microsoft Graph
delegated scopes are valid there.

**Fix applied:**
`BatchPermissionsOrchestrator.cs` Phase 3: replaced the multi-resource scope list with Graph-only
scopes formatted as `https://graph.microsoft.com/{scope}`. Non-Graph permissions (Bot API,
MCP server scopes) are handled by Phase 2 `oauth2PermissionGrants` — not the consent URL.

**Verified:** Consent URL opens successfully and proceeds to the admin consent grant page.
Also: SP creation (`POST /v1.0/servicePrincipals`) now retries on `400 BadRequest` with logged
reason, handling Entra replication lag where `appId` index lags `objectId` index after blueprint
creation.

---

## Issue 7 — Phase 2/3 should be role-aware (consentType parameterization)

**Status: OPEN**

**Design:**
Phase 2 (`CreateOrUpdateOauth2PermissionGrantAsync`) currently always uses
`consentType=AllPrincipals`, which requires Global Administrator. Agent ID Admin gets 403 and
falls through to Phase 3 (consent URL) having made no progress.

**Desired behavior:**

| User role      | Phase 2                                     | Phase 3                           |
|----------------|---------------------------------------------|-----------------------------------|
| Global Admin   | `consentType=AllPrincipals` (tenant-wide)   | Skip — already granted in Phase 2 |
| Agent ID Admin | `consentType=Principal, principalId=userId` | Show consent URL (GA needed)      |
| Developer      | `consentType=Principal, principalId=userId` | Show consent URL                  |

**Changes required:**
- `GraphApiService.CreateOrUpdateOauth2PermissionGrantAsync`: add `consentType` + optional `principalId` parameters.
- `BatchPermissionsOrchestrator`: resolve current user Object ID from Phase 1 prewarm response;
  pass `consentType=AllPrincipals` (GA) or `Principal + principalId` (non-admin) to Phase 2;
  skip Phase 3 when Global Admin.

---

## Notes

- OAuth2 grant failures for Microsoft Graph, Agent 365 Tools, and Power Platform API are
  **expected behavior** — creating `oauth2PermissionGrants` requires Global Administrator.
  Agent ID Admin can configure inheritable permissions (those all succeeded) but cannot
  grant consent. The consent URL is correctly generated.
- ATG endpoint registration failure ("User does not have a required role") is expected for
  Agent ID Admin — they lack the internal ATG role. By design.
- App ID and Object ID for Agent Blueprint apps appear to be the same GUID in the API
  response (`app["appId"]` == `app["id"]`). This is specific to the `AgentIdentityBlueprint`
  app type and is not a CLI parsing bug.
