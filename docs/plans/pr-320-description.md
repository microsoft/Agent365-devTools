# PR #320 — Title and Description

## Suggested Title

```
fix: non-admin setup failures, unclear summary, noisy output, and cleanup 403 on shared machines
```

---

## Description

### Issues fixed

This PR addresses five problems that existed before this change:

**1. `a365 setup all` failed with multiple errors for Agent ID Developers (non-admin)**
An Agent ID Developer cannot set inheritable permissions on a blueprint or configure OAuth2
permission grants — those operations require Agent ID Administrator role or higher. Running
`setup all` as a Developer attempted all of these steps anyway, producing a series of 403 errors
with no explanation of which steps require elevation and no guidance on what to do next.

**2. `a365 setup all` failed with multiple errors for Agent ID Administrators (non-admin)**
An Agent ID Administrator can set inheritable permissions and configure OAuth2 grants, but cannot
grant tenant-wide admin consent — that requires Global Administrator. Running `setup all` as an
Agent ID Admin succeeded on the first two steps but failed on consent, again with no clear
indication that the failure was a role boundary and not a bug, and no actionable next step
(e.g., a consent URL to hand to a Global Admin).

**3. Setup summary did not give actionable next steps**
After a failed or partially successful `setup all` run, the summary section either showed a generic
retry instruction or referenced a command that does not exist (`a365 setup admin`). Users had no
clear path forward.

**4. CLI output was noisy and unclear**
Multiple redundant log lines, inconsistent spacing, and unhelpful error messages (e.g., a 60-second
timeout waiting for a browser consent that would never succeed for non-admin users) made it
difficult to understand what the CLI was doing and whether each step succeeded.

**5. `a365 cleanup` failed with 403 errors — three separate root causes**

- **Wrong Graph scope**: blueprint deletion was using `AgentIdentityBlueprint.ReadWrite.All`.
  Per the Agent ID permissions reference, `ReadWrite.All` is not the correct scope for DELETE —
  `AgentIdentityBlueprint.DeleteRestore.All` is required.
- **Wrong URL pattern**: the DELETE request used an incorrect URL shape for the blueprint endpoint,
  which caused Graph to reject the request.
- **Cross-user token contamination on shared machines**: PowerShell `Connect-MgGraph` caches tokens
  by `(tenant + clientId + scopes)` with no user identity in the key. On a shared machine where a
  developer had previously run `a365 setup`, a Global Administrator running `a365 cleanup` silently
  reused the developer's cached token. The token contained the right scope but the wrong user
  identity (`oid`), so Graph returned 403 — a non-admin cannot delete another user's blueprint.

---

### Behavior after fix

| Persona | Before | After |
|---------|--------|-------|
| **Agent ID Developer** runs `a365 setup all` | Multiple failures; summary unclear | Completes the steps it can; immediately outputs a consent URL to share with an admin instead of timing out |
| **Agent ID Developer** runs `a365 cleanup` | Succeeds for own blueprint (no change) | Same — own blueprint deletion still works |
| **Agent ID Admin** runs `a365 setup all` | Same failures as Developer; unclear which steps need escalation | Completes OAuth2 grants and inheritable permissions; outputs consent URL for the one step that needs a Global Admin |
| **Global Admin** runs `a365 setup all` | Multiple browser prompts, one per resource | At most one browser prompt covering all resources; missing client app permissions are auto-patched |
| **Global Admin** runs `a365 cleanup` on a shared machine | 403 — wrong user's cached token used | Succeeds — MSAL/WAM acquires a token for the current user, not the last user who ran the CLI |
| **Any user** on corporate tenant with Conditional Access | Browser blocked by CAP policy → auth failure | WAM authenticates via OS broker without a browser, satisfying device-trust requirements |

---

### Technical details for reviewers

#### Core new component: `BatchPermissionsOrchestrator`

`src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupSubcommands/BatchPermissionsOrchestrator.cs`

Replaces the per-resource permission loop with a three-phase flow:
1. **Resolve** — pre-warm the delegated token; look up all required service principals once
2. **Grant** — set OAuth2 grants and inheritable permissions in bulk; 403s are caught silently (insufficient role, not an error)
3. **Consent** — check existing consent state; open one browser prompt for Global Admins or return a pre-built consent URL for non-admins

The orchestrator does **not** update `requiredResourceAccess` on Agent Blueprint service principals — that property is not writable for Agent ID entities.

#### Cross-user token fix: `MicrosoftGraphTokenProvider`

`src/Microsoft.Agents.A365.DevTools.Cli/Services/Internal/MicrosoftGraphTokenProvider.cs`

MSAL/WAM is now the primary token path; PowerShell `Connect-MgGraph` is the fallback. MSAL's token
cache is keyed by `HomeAccountId` (user identity + tenant), so tokens for different users never
collide. On Windows, WAM uses the OS broker — no browser, CAP-compliant.
A test seam (`MsalTokenAcquirerOverride`) keeps unit tests free of WAM/browser.

#### Blueprint deletion scope fix: `AgentBlueprintService`

`src/Microsoft.Agents.A365.DevTools.Cli/Services/AgentBlueprintService.cs`

DELETE now uses `AgentIdentityBlueprint.DeleteRestore.All` (correct per permissions reference) and
the correct URL pattern: `/beta/applications/microsoft.graph.agentIdentityBlueprint/{id}`.

#### Summary and output: `SetupHelpers`, `SetupResults`, `AllSubcommand`

`src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupSubcommands/SetupHelpers.cs`
`src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupSubcommands/SetupResults.cs`
`src/Microsoft.Agents.A365.DevTools.Cli/Commands/SetupSubcommands/AllSubcommand.cs`

`SetupResults` now tracks batch phase outcomes, the admin consent URL, and FIC status. The summary
section shows the consent URL when available and references real follow-up commands. Separator lines
removed; output aligned with `az cli` conventions.

#### Scope decisions

| Operation | Scope | Rationale |
|-----------|-------|-----------|
| Blueprint deletion | `AgentIdentityBlueprint.DeleteRestore.All` | Correct scope per permissions reference; `ReadWrite.All` does not cover DELETE |
| FIC create/delete | `Application.ReadWrite.All` | Ownership-based — works for app owners without a role requirement; `AddRemoveCreds.All` reserved for follow-up once validated in TSE |
| GA and Agent ID Admin role detection | `Directory.Read.All` (already consented) | Both role checks use this scope; avoids an additional consent prompt for `RoleManagement.Read.Directory` |
