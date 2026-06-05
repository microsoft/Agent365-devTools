---
name: review-staged
description: Generate structured code review for staged files AND all branch commits since main, using Claude Code agents. Provides full PR-equivalent coverage before pushing.
allowed-tools: Bash(git:*), Bash(dotnet:*), Bash(cd:*), Read, Write
---

# Review Staged Files Skill

Generate AI-powered code review comments covering **both** your currently staged changes and all commits on the branch since it diverged from `main`. This gives the same full-branch view that Copilot and other PR reviewers see — not just what you're about to commit.

## Usage

```bash
/review-staged              # Review staged changes + full branch diff against main
/review-staged --verbose    # Show detailed analysis
```

Examples:
- `/review-staged` - Review staged changes and all branch commits not yet on main
- `/review-staged --verbose` - Show detailed analysis with full context

## What this skill does

1. **Checks for staged files** using `git diff --staged --name-only`
2. **Fetches staged changes** using `git diff --staged`
3. **Fetches full branch diff** using `git diff $(git merge-base HEAD origin/main)...HEAD` — covers all commits on the branch, not just staged files. This prevents issues in prior commits from going unreviewed.
4. **Combines both diffs** for review: staged diff = what you're about to add; branch diff = full PR picture that Copilot will see
5. **Performs architectural review**: Questions design decisions, checks for scope creep, validates use cases
6. **Analyzes changes** for security, testing, design patterns, and code quality issues
7. **Differentiates contexts**: CLI code vs GitHub Actions code (different standards)
8. **Creates actionable feedback**: Specific refactoring suggestions based on file names and patterns
9. **Verifies documentation completeness** — detects user-visible surface changes (new CLI flags, renamed public types, payload contract changes, etc.) and verifies `CHANGELOG.md` + relevant `README.md`/`design.md` are updated in the same diff. For renames, greps all `*.md` files for stale references. See the Documentation Completeness section in `.claude/agents/pr-code-reviewer.md` for detection rules and severity calibration.
10. **Runs the test suite and measures per-test timing** — flags any test taking > 1 second as a performance regression
11. **Generates structured review document** saved to a markdown file
12. **Shows summary** of all issues found organized by severity

## Engineering Review Principles

This skill enforces the same principles as the PR review skill:

### Architectural Review
- **Design Decision Validation**: Questions "why" before reviewing "how"
- **Scope Creep Detection**: Flags expansions beyond Agent365 deployment/management
- **Use Case Validation**: Requires concrete scenarios for new features
- **Overlap Detection**: Identifies duplication with existing tools (Azure CLI, Portal)
- **YAGNI Enforcement**: Questions features without documented need

### Architecture & Patterns
- **.NET architect patterns**: Reviews follow .NET best practices
- **Azure CLI alignment**: Ensures consistency with az cli patterns and conventions
- **Cross-platform compatibility**: Validates Windows, Linux, and macOS compatibility (for CLI code)

### Design Patterns
- **KISS (Keep It Simple, Stupid)**: Prefers simple, straightforward solutions
- **DRY (Don't Repeat Yourself)**: Identifies code duplication
- **SOLID principles**: Especially Single Responsibility Principle
- **YAGNI (You Aren't Gonna Need It)**: Avoids over-engineering
- **One class per file**: Enforces clean code organization

### Code Quality
- **No large files**: Flags files over 500 additions
- **Function reuse**: Encourages reusing functions across commands
- **No special characters**: Avoids emojis in logs/output (Windows compatibility)
- **Self-documenting code**: Prefers clear code over excessive comments
- **Minimal changes**: Makes only necessary changes to solve the problem

### Testing Standards
- **Framework**: xUnit, FluentAssertions, NSubstitute for .NET; pytest/unittest for Python
- **Quality over quantity**: Focus on critical paths and edge cases
- **CLI reliability**: CLI code without tests is BLOCKING
- **GitHub Actions tests**: Strongly recommended (HIGH severity) but not blocking
- **Mock external dependencies**: Proper mocking patterns
- **Test performance — measured by running, not just static analysis**: The review ALWAYS runs the full test suite and reports per-test timing. Any test method taking **> 1 second** is flagged as a performance regression (HIGH severity). The finding must include:
  - The slow test class and method name(s) with their measured time
  - The root cause (cold `AzCliHelper` token cache, missing `WarmAzCliTokenCache` call, real subprocess not mocked, etc.)
  - The fix (warmup call pattern, `loginHintResolver` injection, etc.)
  - Expected time after fix

  If all tests complete in < 1 second each: emit an **INFO — PASS** finding with the total suite time.

  **Do not skip the test run.** Static code analysis alone missed the regression in `da6f750`; only measurement catches it reliably.

### Security
- **No hardcoded secrets**: Use environment variables or Azure Key Vault
- **Credential management**: Follow az cli patterns for CLI code; use GitHub Secrets for Actions

### Documentation Completeness
The review does not assume docs will be updated later — a user-visible surface change without matching doc updates is a finding in its own right. Full detection rules live in `.claude/agents/pr-code-reviewer.md` (Step 7). Short version:
- **Detect**: new `Option<...>`, renamed public class/interface, new/deleted `public` API, changed payload shape or validation rules, changed observable log messages.
- **Require**: matching entry in `CHANGELOG.md` under `[Unreleased]`, updated `README.md` in the relevant folder, and — for renames — zero stale hits from `grep -rn "<OldName>" --include="*.md"`.
- **Severity**: missing CHANGELOG for user-visible change = **HIGH**; stale rename in markdown = **HIGH**; missing folder README update = **MEDIUM**.
- **Not excuses**: "it's preview/opt-in/temporary", "Microsoft Learn will cover it", "you can see it in the diff". The CHANGELOG is the release's source of truth.

### Cross-Cutting Contract Checks
Nine checks that static per-file analysis tends to miss — each requires tracing across multiple files or comparing code against docs/descriptions:

- **Return-value null semantics (Rule N)**: When a method documents that `null` (or a sentinel) carries a special meaning (e.g., "null = verified, safe to persist"), grep every call site and verify the producer returns null in exactly the documented cases. A path that returns non-null when the contract says "verified" silently breaks the caller's persistence gate.
- **CHANGELOG vs code numeric consistency (Rule O)**: After reading CHANGELOG `[Unreleased]`, extract every numeric claim (interval, retry count, timeout). Grep production code for the corresponding literals. Flag any mismatch — the CHANGELOG and the code must agree.
- **Swallowed `OperationCanceledException` (Rule P)**: For every `catch (OperationCanceledException)` that returns a value instead of rethrowing, verify the swallow is intentional and documented. In long-running interactive flows (setup, consent polling), a swallowed cancellation means Ctrl+C has no effect.
- **Test-only escape hatch declared `public` (Rule Q)**: When a property/field named `*ForTests*` or `*TestOverride*` is declared `public` in a production assembly, check the `.csproj` for `InternalsVisibleTo`. If present, `public` is unnecessary and widens the security surface — flag as MEDIUM and suggest `internal`.
- **`--help` text accuracy (Rule R)**: When the diff changes how a command surfaces output (new URL handoff, removed PowerShell path, added fallback), read every description string in the same file and its parent command. If the description still names an output form that no longer matches the code, flag as MEDIUM.

**Added 2026-05-29 after PR #432 Copilot review surfaced gaps Rules N–R didn't catch. See [.codereviews/why-review-staged-missed-copilot-findings-20260529T190832Z.md](../../codereviews/why-review-staged-missed-copilot-findings-20260529T190832Z.md) for the full miss analysis.**

- **Branch-wide stale-mechanism sweep (Rule S)**: When the staged diff replaces mechanism X with mechanism Y (e.g., PowerShell fallback → az rest, `api://{appId}` → bare appId GUID, `per-app admin-consent URLs` → `az ad sp create`), enumerate the terms being replaced and `grep -rn` the **entire branch** (not just the staged delta) for each term. For every hit outside the staged delta, classify:
  - Stale comment (FIX — code/comment lie about behavior).
  - Stale user-facing log/warning/exception text (FIX — operators read these in production).
  - Stale doc comment / XML doc (FIX — IDE surfaces these to consumers).
  - Intentional historical reference (KEEP — e.g., commit message archaeology, CHANGELOG `[Released]` entries).

  This is the analog of "rename hygiene" applied to behavior changes. It catches the family of issues where the implementation moves but the surrounding documentation lies. **Treat each surviving reference as MEDIUM if user-facing (LogWarning, LogError, exception messages, Warnings collection entries), LOW if internal-only (private comments).**
  
  Six of the nine unique findings in PR #432's Copilot review were of this exact shape — all stale `"PowerShell fallback"` and `"api://{appId}"` references on lines outside the staged delta.

- **Test-class parallelism safety (Rule T)**: For every test class in the diff (or anywhere on the branch), check whether the class reads or writes any static property or field whose name matches the pattern `*ForTests*` or `*TestOverride*` (these are the conventional escape hatches in this codebase). If yes, the class **must** carry either:
  - `[Collection("Sequential")]` attribute, OR
  - `[CollectionDefinition(..., DisableParallelization = true)]` for a class-specific collection it owns.

  Without one of these, xUnit may run the class in parallel with other classes that also mutate the same static state, producing flaky cross-class races. **Severity: MEDIUM.** The fix is one line. Two PR #432 findings (`BatchPermissionsOrchestratorTests`, `BatchPermissionsOrchestratorMissingSpTests`) were exactly this — both mutated `BypassSpProvisioningForTests` without `[Collection]` until Copilot flagged it.

  Implementation: `grep -rn "BypassConsentChecksForTests\|BypassSpProvisioningForTests\|OpenUrlOverrideForTests\|<OtherKnownForTestsName>" <test_files>`. For each hit, read the enclosing test class and verify the attribute is present.

- **Branch-scope completeness checkpoint (Rule U)**: **The single biggest miss category in the PR #432 review.** Before declaring the review complete, list every file that appears in the **branch diff** (`git diff $(git merge-base HEAD origin/main)...HEAD --name-only`), not just the staged delta. The review must touch each file in that list at least to the extent of running the other rules against it.

  In practice this means: if the staged delta is 4 files but the branch diff is 22 files, the review surface is 22, not 4. Prior `/review-staged` runs do **not** absolve the current run of covering the rest of the branch — assume nothing has been covered until you've explicitly read it in this run.

- **Hardening-bypass detection (Rule V)**: **Added 2026-05-29 after PR #432's second Copilot review caught a regression Rules N–U missed.** When the staged diff hardens an entry-point helper by making a parameter required (so the compiler forces it through one path), the lower-level function it delegates to typically still keeps the parameter optional — and **any direct caller of the lower-level function bypasses the hardening**. The hardening only protects the path that goes through the helper.

  Detection: when the diff includes a signature change of the shape "parameter X was optional with `= null` default, now required (no default)", identify the **lower-level function** the helper delegates to. If that function still has X optional, run `grep -rn "<LowerLevelFunctionName>" --include="*.cs" src/<ProductionPath>/` (production callers only) and verify each direct caller passes X. Any caller that doesn't is a recurrence of the exact bug the hardening was meant to prevent.

  Concrete PR #432 example: commit `7a1e317` made `mcpScopesByAudience` required on `ExecuteBatchPermissionsStepAsync` so the AllSubcommand and NonDwBlueprintSetupOrchestrator entry points couldn't forget it. But `ConfigureAllPermissionsAsync` (the lower-level orchestrator method) still had `knownMcpAudienceAppIds` optional, and `PermissionsSubcommand.ConfigureMcpPermissionsAsync` called it directly — bypassing the hardening and re-introducing the AADSTS500011 V2 routing regression. Copilot flagged it as HIGH; the skill missed it.

  **Severity: HIGH** when the bypassed call site is in production code (live regression risk). LOW when it's a test fixture (tests routinely use null defaults). Treat the parameter-becoming-required signature change in the diff as a trigger: the moment you see one, follow the call chain down and grep direct callers of the lower-level function.

  Implementation: at the start of the review, emit the list of branch-level files and treat them as the review surface. At the end, verify each file was read or explicitly justified as "no rule applies."

- **CLI option precondition enforcement (Rule W)**: **Added 2026-06-02 after PR #440's Copilot review.** When a new or modified `Option<...>` has a description that constrains its applicability — phrases like `(--X only)`, `use with --Y`, `Only meaningful with --Z`, `requires an existing ...` — the handler **must validate that precondition and fail fast** (`logger.LogError(...) + context.ExitCode = 1 + return`) when the option is supplied outside that context. A flag that is accepted and then silently ignored (because the code path it feeds is skipped) is a bug: the user gets exit 0 and assumes it took effect.

  Detection: for every option whose description contains a constraint keyword, grep the handler for a guard that errors when the option is present but the precondition is false. Use `context.ParseResult.CommandResult.FindResultFor(<option>) != null` to detect "supplied" (not the value, which can't tell omitted from empty). If no such guard exists, flag it. **Severity: MEDIUM.** PR #440 example: `--messaging-endpoint` was documented `(--m365 only)` and `with --endpoint-only`, but both handlers accepted it unconditionally and dropped it when the messaging step / endpoint-only path was skipped.

- **`Option<string?>` empty-vs-omitted conflation (Rule X)**: **Added 2026-06-02 after PR #440.** When a string option is read as `GetValueForOption(opt)?.Trim()` and then gated only with `string.IsNullOrWhiteSpace(...)`, an **explicitly-passed empty value** (`--opt ""` or `--opt "   "`) is indistinguishable from **omitted**. This is a defect whenever "omitted" triggers a *different* behavior than "empty should" — e.g. omitted falls back to config / prompts / defers, so `--opt ""` silently does the fallback instead of erroring.

  Detection: for each `Option<string?>` whose omitted-path does something non-trivial (prompt, default, defer, config fallback), verify the handler distinguishes "specified" via `FindResultFor(opt) != null` and emits a targeted error + `ExitCode = 1` when specified-but-whitespace. This is the CLAUDE.md "Input Validation" rule applied to the explicit-empty case. **Severity: MEDIUM.** PR #440 example: `--messaging-endpoint ""` was `.Trim()`ed to `""`, read as omitted, and triggered prompting/deferral instead of a clear error.

- **Ignored result-bearing call / missing exit code (Rule Y)**: **Added 2026-06-02 after PR #440.** Extends the exit-code-completeness check (CLAUDE.md item 8) to *discarded return values*. When a command handler `await`s an operation that returns a success/failure-bearing result (an enum like `*Result`, a `bool`, a tuple with an outcome) and **does not capture or branch on it**, any failure that result encodes will still exit 0 — breaking scripting/CI.

  Detection: in every command handler, find `await SomeOp(...)` calls whose return type is non-`void`/non-`Task` and whose result is not assigned or checked. For each, confirm the failure values map to `context.ExitCode = 1`. **Severity: MEDIUM** (HIGH if the command is commonly scripted). PR #440 example: the `--endpoint-only` path called `RegisterEndpointAndSyncAsync(...)` (returns `EndpointRegistrationResult`) and discarded it, so `NotConfigured` / contract-mismatch / `Failed` all exited 0.

- **Dead local / unused assignment the compiler won't flag (Rule Z)**: **Added 2026-06-03 after PR #440's second review.** A local that is assigned but never read is dead code. The trap is that `TreatWarningsAsErrors` does **not** catch most of these: the C# unused-variable warning (CS0219) fires only for **constant** initializers. When the initializer is a property access, method call, LINQ expression, or `new`, the compiler stays silent (it can't assume the access is side-effect-free), so the build is green and the dead local survives. This means a green build/test run — the skill's one objective signal — gives **no** indication, so the sweep must be done by reading.

  Detection: for every local declared in the diff (and the surrounding method), confirm it is read at least once after assignment. Pay special attention to locals whose initializer is `x.Count` / `x.ToList()` / `SomeMethod(...)` / `new T(...)` — these are exactly the ones the compiler won't warn about. A common shape is a "summary" or "counter" local that was meant to be used in an output/log line but the line ended up using the individual counters instead. **Severity: LOW** (dead code, not a runtime defect). Fix by deleting the local, unless the intent was to surface it (e.g. a total in a summary line) — in which case wire it in. PR #440 example: `var totalChecks = requirementChecks.Count;` in `RequirementsSubcommand` was never read (the summary used `passedChecks`/`warningChecks`/`failedChecks`); the build stayed green because `.Count` is a property access, and the skill walked past it.

- **CHANGELOG entry crispness (Rule AA)**: `CHANGELOG.md` `[Unreleased]` feeds straight into the nuget.org release notes, so each entry must be one crisp sentence about the user-visible change — not an essay. Flag entries that explain internal mechanism, name classes/methods/internals, restate rationale, or run multiple sentences of background. Fix by cutting to what a package consumer needs to know. **Severity: LOW.**

- **Code comment crispness (Rule BB)**: Comments added in the diff must be crisp and follow .NET conventions — `///` XML doc on public members, short `//` only on non-obvious logic. Flag multi-line rationale essays, comments that restate what the code already says, and commit-message-style narration in source. A comment states *why* in one line; it does not retell the change (that belongs in the commit/PR). **Severity: LOW.**

- **Cancellation reachability — Ctrl+C must reach every blocking await (Rule CC)**: The CLI uses cooperative cancellation (`CancelOnProcessTermination` in `Program.cs`), so Ctrl+C only works on a path that *observes* the command's token. Existing checks (`.None` in a handler, `Task.Delay` without a token, retry loops that don't check `IsCancellationRequested`) assume a token is already in scope. This rule adds the **missing-parameter** case: a service/helper method whose body awaits a blocking operation — an HTTP send (`PostAsync`/`SendAsync`/`GetAsync`), a retry wrapper (`ExecuteWithRetryAsync`), a `Task.Delay` backoff, or a network poll loop — but **exposes no `CancellationToken` parameter at all**, so callers cannot thread one even though a token is available upstream (`SetupContext.CancellationToken`, `context.GetCancellationToken()`). Symptom: Ctrl+C appears to hang for the full retry/timeout window. Detection: for each such method added/modified in the diff, confirm its signature takes a `CancellationToken` AND forwards it into every blocking await (`cancellationToken: ct`, `..., ct)`); if absent, trace the call chain to the command handler and confirm a token exists to thread. When adding the parameter, verify **every** intermediate helper between the handler and the leaf is updated (partial threading leaves a gap), and append `Arg.Any<CancellationToken>()` to that method's NSubstitute setups/`Received`/`DidNotReceive` matchers — otherwise they silently stop matching once the real token flows. **Severity: MEDIUM** (HIGH when the operation has a long retry+timeout window, e.g. HTTP with exponential backoff, where the hang is most acute). Real example (PR adding retries to `TeamsGraphBackendConfigurator`): `SetBackendConfigurationAsync` / `ClearBackendConfigurationAsync` wrapped the HTTP send in `RetryHelper` but took no `CancellationToken`, so the retry + backoff ran with `CancellationToken.None` and Ctrl+C did nothing until the attempts were exhausted.

Full detection rules and real examples are in `.claude/agents/pr-code-reviewer.md` Step 9, Rules N through V (plus W–CC above).

### Context Awareness
The skill differentiates between:
- **CLI code** (strict requirements): Cross-platform, reliable, must have tests
- **GitHub Actions code** (GitHub-specific): Linux-only is acceptable, tests strongly recommended

## Review Output

Generated review is saved to:
```
.codereviews/claude-staged-<timestamp>.md
```

The review includes:
- **Summary**: Overview of changes and key concerns — includes both staged and branch coverage
- **Critical Issues**: Blocking issues that must be fixed (labeled `[staged]` or `[branch]`)
- **High Priority**: Important issues that should be addressed
- **Medium Priority**: Issues that improve code quality
- **Low Priority**: Suggestions for enhancement
- **Informational**: Best practices and recommendations

## Implementation

The skill uses **Claude Code directly** for semantic code analysis (same as review-pr):

1. Claude Code reads `.claude/agents/pr-code-reviewer.md` for review process guidelines
2. Claude Code reads `.github/copilot-instructions.md` for coding standards
3. Claude Code gets staged files: `git diff --staged --name-only`
4. Claude Code gets staged changes: `git diff --staged`
5. **Claude Code gets the full branch diff against main:**
   ```bash
   git diff $(git merge-base HEAD origin/main)...HEAD --name-only   # files changed on branch
   git diff $(git merge-base HEAD origin/main)...HEAD               # full branch diff
   ```
   This covers ALL commits on the branch since it diverged from `origin/main` — including prior commits that are no longer in the staged diff. The union of staged files and branch-diff files is the full review surface. This is the same view Copilot and PR reviewers see.

   **When there are no staged files**, skip step 4 and use only the branch diff. When both exist, label findings clearly — `[staged]` for changes only in the staged diff, `[branch]` for changes from prior commits, so the developer knows which issues are still ahead of them vs. already committed.

6. **Claude Code reads the complete current content of every file that appears in either diff** (staged or branch) to enable full-file semantic analysis.
6a. **If any file in the combined diff is a test file** (path contains `.Tests.` or filename ends in `Tests.cs`), Claude Code MUST also invoke the `pr-test-analyzer` agent as a subagent to review test coverage quality. Pass the combined diff and list of test files as context. The `pr-test-analyzer` agent focuses on:
   - NSubstitute predicate precision (predicates on mutable reference types evaluated at assertion time vs. call time)
   - Missing test scenarios for new orchestration code paths (e.g., 409 idempotent path, tri-state verification results)
   - Assertions that track implementation rather than requirements
   - Test method names that accurately describe the contract being verified
   Include the `pr-test-analyzer` findings in the review document under a dedicated **Test Coverage** section. This is critical for catching issues that exist in unchanged sections of modified files, such as:
   - Duplicate hardcoded constants or magic values that already exist elsewhere
   - Parallel code structures that should be consolidated (e.g., a method building the same spec list as a shared helper)
   - Unused or dead code that was already there but not touched by the diff
   - Missing calls to shared helpers — where the diff adds a new use but existing code still has the old duplicate pattern
7. Claude Code performs semantic analysis using its own capabilities
8. Claude Code identifies specific issues with line numbers and code references
9. **Claude Code runs the full test suite with per-test timing:**
   ```bash
   cd src && dotnet test tests.proj --configuration Release --logger "console;verbosity=normal" 2>&1
   ```
   Parse the output for lines matching `[X s]` or `[X,XXX ms]` patterns. Extract test class name, method name, and duration. Flag any test method taking **> 1 second**. Group findings by test class and include the measured times in the review.
10. Claude Code writes markdown file to `.codereviews/claude-staged-<timestamp>.md`

**Test timing output format** (from `dotnet test --logger "console;verbosity=normal"`):
```
  Passed SomeTests.Method_Scenario_ExpectedResult [< 1 ms]
  Passed OtherTests.Method_Slow [22 s]
```
Any line showing `[X s]` where X ≥ 1 is a slow test. Report all such tests in a dedicated finding.

**Key Advantages**:
- ✅ No API key required - uses Claude Code's existing authentication
- ✅ Better semantic analysis - Claude Code has full context
- ✅ Catch issues before committing
- ✅ Same rigorous review standards as PR reviews
- ✅ Works offline (no GitHub required)

## Workflow

1. **Stage your changes**: `git add <files>`

2. **Review staged files**: `/review-staged`
   - Analyzes staged changes AND all commits on the branch since `main`
   - Generates review document with `[staged]` / `[branch]` labels
   - Shows summary of issues

3. **Address issues**: Fix any blocking or high-priority issues

4. **Re-review if needed**: `/review-staged`

5. **Commit**: `git commit -m "your message"`

## When to Use

- **Before any commit**: Catch issues before they land in the branch history
- **Before creating a PR**: Get the same full-branch view that Copilot will see — no surprises
- **After addressing PR comments**: Verify fixes across the entire branch, not just the latest staged diff
- **During code cleanup**: Validate refactoring changes
- **When learning**: Get feedback on coding patterns

## Requirements

- Git repository (staged changes optional — branch diff is always produced)
- Repository must follow Agent365 DevTools coding standards
- `.claude/agents/pr-code-reviewer.md` must exist (for review guidelines)
- `.github/copilot-instructions.md` must exist (for coding standards)

## See Also

- [README.md](README.md) - Detailed documentation
- `/review-pr` - Review pull requests on GitHub
