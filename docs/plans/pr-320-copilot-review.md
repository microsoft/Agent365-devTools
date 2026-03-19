# PR #320 — Copilot Unresolved Comments (Latest Review — 2026-03-18)

7 unresolved comments from the latest Copilot review pass.

| # | File | Line | Comment Summary | Analysis | Fix? |
|---|------|------|-----------------|----------|------|
| 1 | `ClientAppValidator.cs` | 103-107 | New self-healing PATCH behavior (auto-provision missing permissions) has no tests — needs coverage for PATCH success/failure, re-validation loop, and grant-extension best-effort | Valid concern — but test authoring is out of scope for this bug-fix PR; tracked as follow-up | Skip |
| 2 | `MicrosoftGraphTokenProvider.cs` | 139 | Non-Windows log says "A device code prompt will appear below" but MSAL uses interactive browser on macOS — should say "A browser window or device code prompt may appear" | Valid — fix message to reflect that the experience varies by platform and MSAL path | Fix |
| 3 | `FederatedCredentialService.cs` | 440 | Manual remediation message says `Entra portal > App registrations > {CredentialId}` — should reference the blueprint app and the Federated credentials blade | Valid — `{CredentialId}` is a FIC ID, not the app; message should guide user to blueprint app → Certificates & secrets → Federated credentials | Fix |
| 4 | `AllSubcommand.cs` | 375 | `BatchPermissionsOrchestrator` called with `CancellationToken.None` instead of real CT | Pre-existing pattern used throughout AllSubcommand.cs (lines 162, 197, 317) — `SetHandler` lambda has no CT param; fixing requires broader refactor out of scope for this PR | Skip |
| 5 | `MicrosoftGraphTokenProvider.cs` | 132-136 | Info-level logs say auth dialog "will appear" but MSAL may succeed silently from cache — misleading when no dialog shows | Valid — these logs fire after in-memory cache miss but MSAL still has its own internal cache; move to LogDebug | Fix |
| 6 | `MicrosoftGraphTokenProvider.cs` | 93-97 | `loginHint` accepted but `MakeCacheKey` ignores it — two users with same tenant/scopes share the same cached token | Valid — add `loginHint` to the cache key so per-user tokens are stored separately | Fix |
| 7 | `AgentBlueprintService.cs` | 88-92 | Blueprint deletion still uses `AgentIdentityBlueprint.ReadWrite.All` scope — PR description says `DeleteRestore.All` should be used | Decided to keep main branch version — manually tested and verified working; `DeleteRestore.All` is a future-proofing change not needed now | Skip |
