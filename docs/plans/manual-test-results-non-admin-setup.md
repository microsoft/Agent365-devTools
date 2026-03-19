# Manual Test Results — Non-Admin Setup & Cleanup

**Branch:** `users/sellak/non-admin`
**Date:** 2026-03-18/19
**Tenant:** `a365preview070.onmicrosoft.com`
**Sample project:** `Agent365-Samples/dotnet/agent-framework/sample-agent`

---

## Test 1 — `a365 cleanup` as Global Administrator

**User:** `sellak@a365preview070.onmicrosoft.com` (Global Administrator)
**Command:** `a365 cleanup`
**Result:** Pass

| Step | Outcome |
|------|---------|
| FIC deletion (`sk70aadmindotnetagentBlueprint-MSI`) | Succeeded |
| Blueprint deletion | Succeeded |
| Messaging endpoint deletion | Succeeded (idempotent — not found treated as success) |
| Web App deletion | Succeeded |
| App Service Plan deletion | Warning (Azure conflict retries — pre-existing Azure-side limitation, not a code issue) |
| Generated config backup and deletion | Succeeded |

---

## Test 2 — `a365 setup all` as Agent ID Administrator

**User:** `sellakagentadmin@a365preview070.onmicrosoft.com` (Agent ID Administrator role, not Global Administrator)
**Command:** `a365 setup all`

### Issue 1 — WAM ignores login hint, picks OS default account (Fixed)

**Symptom before fix:** Authenticated as `sellakdev` instead of `sellakagentadmin` despite running under the agent admin account.

```
Current user: Sellakumaran Developer <sellakdev@a365preview070.onmicrosoft.com>
```

**Root cause:** `WithLoginHint` is advisory only in WAM — WAM authenticates as the primary OS-level signed-in account and ignores the hint. `InteractiveGraphAuthService` was also creating its own `MsalBrowserCredential` without passing any login hint.

**Fix:**
- `MsalBrowserCredential`: resolves the MSAL-cached `IAccount` matching the login hint and calls `WithAccount(account)`. Falls back to `WithPrompt(Prompt.SelectAccount)` if no cached match.
- `InteractiveGraphAuthService`: now runs `az account show` to resolve the current user's UPN and passes it as the login hint when constructing its own `MsalBrowserCredential`.

**Result after fix:**
```
Current user: Sellakumaran AgentAdmin <sellakagentadmin@a365preview070.onmicrosoft.com>
```

---

### Issue 2 — Owner assignment fails: `Directory.AccessAsUser.All` in token (Fixed)

**Symptom before fix:**
```
ERROR: Failed to assign current user as blueprint owner: 400 Bad Request
Agent APIs do not support calls that include the Directory.AccessAsUser.All permission.
This request included Directory.AccessAsUser.All in the access token.
```

**Root cause:** The post-creation owner verification call used a token with `Application.ReadWrite.All`, which Entra automatically bundles with `Directory.AccessAsUser.All`. The Agent Blueprint API explicitly rejects any token carrying that scope.

**Fix:** When `owners@odata.bind` is set at blueprint creation time (sponsor user known), the post-creation owner verification step is skipped entirely — ownership is already set atomically during creation.

**Result after fix:**
```
Owner set at creation via owners@odata.bind — skipping post-creation verification
```

---

### Issue 3 — `Authorization.ReadWrite` scope not found on Messaging Bot API (Resolved as symptom of Issue 1)

**Symptom before fix:**
```
ERROR: Graph POST oauth2PermissionGrants failed:
The Entitlement: Authorization.ReadWrite can not be found on
resourceApp: 5a807f24-c9de-44ee-a3a7-329e88a00ffc.
```

**Resolution:** Once Issue 1 was fixed and the correct user was authenticated, all inheritable permissions configured successfully with no errors. The OAuth2 grant error was caused by the wrong user being authenticated, not an invalid scope name.

**Result after fix:** All 5 inheritable permissions configured with no errors.

---

### Issue 4 — Client secret creation fails: wrong scope bundles `Directory.AccessAsUser.All` (Fixed)

**Symptom before fix:**
```
ERROR: Failed to create client secret: Forbidden - Authorization_RequestDenied
```

**Root cause:** Token for `addPassword` was acquired with `https://graph.microsoft.com/.default`, which includes all consented scopes including `Application.ReadWrite.All`. That scope causes Entra to bundle `Directory.AccessAsUser.All` into the token, which the Agent Blueprint API rejects.

**Fix:**
- Token for `addPassword` is now acquired with the specific scope `AgentIdentityBlueprint.AddRemoveCreds.All`, which covers `passwordCredentials` per the [Agent ID permissions reference](agent-id-permissions-reference.md).
- `AgentIdentityBlueprint.AddRemoveCreds.All` added to `RequiredClientAppPermissions` so it is provisioned during `a365 setup clients`.

---

### Issue 5 — Client secret creation fails: Entra eventual consistency (Fixed)

**Symptom before fix:**
```
ERROR: Failed to create client secret: NotFound - Request_ResourceNotFound
Resource '1b22cbb8-218b-48c0-ab82-e690308deeae' does not exist or one of its
queried reference-property objects are not present.
```

**Root cause:** `addPassword` was called ~8 seconds after blueprint creation. Entra replication across Graph API replicas had not completed, so the new application object was not visible to the replica handling the `addPassword` request.

**Fix:** The `addPassword` call is now wrapped in `RetryHelper.ExecuteWithRetryAsync` with `shouldRetry: response.StatusCode == NotFound`, 5 retries, 5-second base delay (exponential backoff: 5s → 10s → 20s → 40s → 60s). Only the final error is logged — intermediate retries log only "Retry attempt X of Y. Waiting Z seconds...".

---

## Expected Behavior for Agent ID Administrator (not bugs)

| Behavior | Reason |
|----------|--------|
| OAuth2 consent grants skipped — consent URL generated instead | Creating `oauth2PermissionGrants` requires Global Administrator. Agent ID Admin can configure inheritable permissions but cannot grant consent. By design. |
| ATG endpoint registration fails: "User does not have a required role" | Agent ID Administrator does not have the internal ATG role required for endpoint registration. By design. |

---

---

## Test 3 — Role detection via `transitiveMemberOf`

**Date:** 2026-03-19
**Command:** `a365 setup all --dry-run --verbose`
**Purpose:** Verify `IsCurrentUserAdminAsync` / `IsCurrentUserAgentIdAdminAsync` correctly detect Entra built-in roles via `/me/transitiveMemberOf/microsoft.graph.directoryRole` for all three account types.

| Account | Role | Global Administrator | Agent ID Administrator |
|---------|------|---------------------|----------------------|
| `sellak@a365preview070.onmicrosoft.com` | Global Administrator | `HasRole` | `DoesNotHaveRole` |
| `sellakdev@a365preview070.onmicrosoft.com` | Agent ID Developer | `DoesNotHaveRole` | `DoesNotHaveRole` |
| `sellakagentadmin@a365preview070.onmicrosoft.com` | Agent ID Administrator | `DoesNotHaveRole` | `HasRole` |

**Result:** Pass — all three accounts detected correctly.

**Background:** The previous implementation used `/me/memberOf` which does not return built-in Entra role assignments in the unified RBAC model (only returns groups). The new endpoint returns only `microsoft.graph.directoryRole` objects, requires only `User.Read` (always implicit), and covers both direct and group-transitive assignments.

**New behavior for failed role check:** Return type changed from `bool` to `RoleCheckResult` (enum: `HasRole` / `DoesNotHaveRole` / `Unknown`). A failed check (network error, throttling) now returns `Unknown` and falls through to attempt the operation, rather than returning `false` and blocking the user with a consent URL only.

---

## Files Changed

| File | Change |
|------|--------|
| `Services/MsalBrowserCredential.cs` | WAM path uses `WithAccount(account)` / `WithPrompt(SelectAccount)` instead of `WithLoginHint` |
| `Services/InteractiveGraphAuthService.cs` | Resolves login hint via `az account show` before constructing `MsalBrowserCredential` |
| `Commands/SetupSubcommands/BlueprintSubcommand.cs` | Skip owner verification when `owners@odata.bind` set at creation; use `AddRemoveCreds.All` scope for `addPassword`; retry `addPassword` on 404 |
| `Constants/AuthenticationConstants.cs` | Added `AgentIdentityBlueprint.AddRemoveCreds.All` to `RequiredClientAppPermissions` |
