# GitHub Copilot Instructions for Agent365-devTools

## Agent365 CLI Development Guidelines

### Engineering Principles
- Follow KISS, DRY, SOLID, and YAGNI principles
- Align CLI patterns with Azure CLI (`az`) conventions
- Keep changes minimal and focused on the problem at hand
- Reuse existing functions across commands; avoid duplication
- Critically review all changes before committing

### Code Organization
- Keep files small and focused
- Use constants for strings and values (see `Constants/` folder)
- Use `ErrorCodes.cs` and `ErrorMessages.cs` for error handling

### File Organization Guidelines

#### Multiple Classes Per File - Allowed Cases
- **Model/DTO files**: Related model classes, records, or structs can be grouped in a single file
- **Request/Response pairs**: API request and response classes for the same endpoint
- **Small supporting types**: Enums, small records, or helper classes closely tied to a main class
- **Nested or related interfaces**: Interface and its related types

#### When to Separate
- Large classes with significant logic
- Classes that could be reused independently
- Classes with different lifecycle or ownership

### Cross-Platform Compatibility
- All code must work on Windows, macOS, and Linux
- Test file paths, line endings, and shell commands for compatibility

### Testing Standards
- Use xUnit, FluentAssertions, and NSubstitute
- Focus on quality over quantity of tests
- Add regression tests for bug fixes
- Tests should verify CLI reliability
- **Tests must assert requirements, not implementation** — when a test is changed to match new code behavior (rather than to reflect a changed requirement), that is a red flag. A test that silently tracks whatever the code does provides no regression protection. If a test needs to be updated, explicitly document the requirement the new assertion encodes (use `because:` in FluentAssertions). If you cannot articulate a requirement reason, the test change should be questioned.
- **FluentAssertions `because:` is mandatory for non-obvious assertions** — any assertion on a URL structure, encoding format, security-sensitive behavior, or protocol requirement must include a `because:` clause explaining the invariant being enforced.
- **Dispose IDisposable objects properly**:
  - `HttpResponseMessage` objects created in tests must be disposed
  - Even in mock/test handlers, follow proper disposal patterns
  - Consider using `using` statements or ensure test handlers dispose responses
  - This applies to all `IDisposable` test objects to avoid analyzer warnings
- **Disable parallel execution for tests with shared state**:
  - Tests that modify environment variables must disable parallelization
  - Tests that access shared file system resources must run sequentially
  - Use `[CollectionDefinition("TestName", DisableParallelization = true)]` pattern
  - Add `[Collection("TestName")]` attribute to test class
  - **Pattern to follow**:
    ```csharp
    /// <summary>
    /// Tests must run sequentially because they modify environment variables.
    /// </summary>
    [CollectionDefinition("EnvTests", DisableParallelization = true)]
    public class EnvTestCollection { }

    [Collection("EnvTests")]
    public class MyTests
    {
        [Fact]
        public void Test_ModifiesEnvironmentVariable()
        {
            Environment.SetEnvironmentVariable("VAR", "value");
            try
            {
                // Test logic
            }
            finally
            {
                Environment.SetEnvironmentVariable("VAR", null);
            }
        }
    }
    ```

### Resource Management
- **Always dispose IDisposable objects** to prevent resource leaks:
  - `HttpResponseMessage` returned by `HttpClient.GetAsync()`, `PostAsync()`, etc. must be disposed
  - Use `using` statements for automatic disposal: `using var response = await httpClient.GetAsync(...);`
  - Even when checking `IsSuccessStatusCode` or reading content, wrap in `using`
  - This applies to all HTTP responses, streams, file handles, and other disposable resources
  - **Pattern to follow**:
    ```csharp
    // CORRECT - Dispose HttpResponseMessage
    using var response = await httpClient.GetAsync(url, cancellationToken);
    if (!response.IsSuccessStatusCode) { return null; }
    var content = await response.Content.ReadAsStringAsync(cancellationToken);

    // INCORRECT - Resource leak
    var response = await httpClient.GetAsync(url, cancellationToken);
    if (!response.IsSuccessStatusCode) { return null; }
    var content = await response.Content.ReadAsStringAsync(cancellationToken);
    ```

### Output and Logging
- No emojis or special characters in logs, output, or comments
- The output should be plain text, and display properly in windows, macOS, and Linux terminals
- Keep user-facing messages clear and professional
- Follow client-facing help text conventions

### Comments
- Comments are crisp: state *why* in one or two lines, never an essay. A `//` or `///` `<summary>` that runs to a paragraph, restates the code, or narrates the change ("previously X, now Y because...") belongs in the commit message / PR, not in source.
- Keep an issue/PR reference (`(issue #460)`) but drop the surrounding narration. Trivial mechanical edits get zero or one comment line.

### CHANGELOG
- `CHANGELOG.md` `[Unreleased]` ships verbatim to nuget.org release notes. Each entry is one crisp consumer-facing sentence — no class/method names, no implementation mechanism, no multi-sentence rationale. Keep the `(#NNN)` reference; fix any sibling entry the change makes stale.

### Code Review Mindset
- Be cautious about deleting code; avoid `git restore` without review
- Do not create unnecessary documentation files
- For user-facing changes (features, bug fixes, behavioral changes): verify `CHANGELOG.md` has an entry in the `[Unreleased]` section

---

## Code Review Rules

### Rule 1: Check for "Kairo" Keyword
- **Description**: Scan code for any occurrence of the keyword "Kairo"
- **Action**: If "Kairo" is found in any code file:
  - Flag it for review
  - Suggest removal or replacement with appropriate terminology
  - Check if it's a legacy reference that needs to be updated
- **Files to check**: All `.cs`, `.csx` files in the repository

### Rule 2: Flag Tests Changed to Match Implementation
- **Description**: When a PR or staged change modifies a test assertion to match new code behavior, treat it as a high-priority review flag — not a routine update.
- **The anti-pattern**: A test previously asserted `X`. Code changed, so the test was updated to assert `not X` (or a different value of `X`) without documenting *why the requirement changed*.
- **Why it matters**: Tests that chase implementation provide zero regression protection. They give false confidence — all tests green, but the regression was in the test suite, not just the code. This is how silent regressions reach production.
- **Action**: For every test assertion change in the diff:
  1. Ask: "Did the *requirement* change, or just the implementation?"
  2. If the requirement changed: the PR must include a comment or `because:` clause stating the new requirement.
  3. If only the implementation changed: the test assertion should not need to change. Flag as **HIGH** if a test is weakened (e.g., `Contain` → `NotContain`, `Equal("x")` → `NotBeNull()`).
  4. If the assertion is on a security-sensitive, protocol-level, or external-API contract (OAuth URLs, HTTP headers, encoding format): flag as **CRITICAL** — require explicit documented justification.
- **Example of the failure mode** (from project history): Consent URL tests asserted `redirect_uri=` was present. When URL encoding was changed, tests were updated to match. No one asked whether `redirect_uri` was still required by the AAD protocol. The regression (`AADSTS500113`) reached the user before any test caught it.

### Rule 3: Input Validation on User-Controlled Values
- **Description**: Any CLI argument, config value, or external string that reaches a file system operation (path construction, file name interpolation, `File.ReadAllText`, `Directory.EnumerateFiles`) must be validated before use.
- **Action**: Flag as **HIGH** if:
  - A user-supplied value is passed directly to `Path.Combine`, `File.Exists`, or similar without an allowlist check
  - A string parameter that must be non-empty relies only on a non-nullable type — `ArgumentException.ThrowIfNullOrWhiteSpace` must also be present before the first use of the value inside a try/catch block
- **Pattern to require**:
  ```csharp
  // Validate CLI argument before use in file path
  if (!Regex.IsMatch(commandName, @"^[a-z0-9-]+$"))
  {
      logger.LogError("Invalid command name '{Name}'.", commandName);
      context.ExitCode = 1;
      return;
  }

  // Guard non-nullable string parameters against empty/whitespace before try/catch
  ArgumentException.ThrowIfNullOrWhiteSpace(generatedConfigPath);
  ```

### Rule 4: CLI Command Exit Code Completeness
- **Description**: Every failure branch in a `SetHandler` command handler must set `context.ExitCode = 1` before exiting. This includes `continue` statements inside loops and early `return` statements, not just the final else block.
- **Action**: Flag as **HIGH** if:
  - A `logger.LogWarning` or `logger.LogError` is followed by `continue` or `return` without setting `context.ExitCode = 1`
  - A post-loop summary block sets exit code only for one case (e.g. auto-discovery) but not another (e.g. named command with missing file)
- **Pattern to require**:
  ```csharp
  // Every failure exit must set ExitCode
  logger.LogWarning("No log found for '{Name}'.", name);
  context.ExitCode = 1;   // required before continue or return
  continue;
  ```

### Rule 5: Data Flow Consistency for Transformations
- **Description**: When a transformation (redaction, sanitization, encoding) is applied to a value, it must be applied everywhere that value is written — including header lines, summary output, and file names, not just the primary content block.
- **Action**: Flag as **HIGH** if:
  - A value is sanitized/redacted in one output path but written verbatim in another (e.g. redacted in log content but included raw in a file header)
  - A new sanitization step is added to content processing but the same value also flows into a header, summary line, or secondary output that was not updated

### Rule 6: CLI Option Whitespace Validation
- **Description**: `Option<string?>` values are null when omitted but can be explicitly passed as empty or whitespace. Code that uses `?? defaultValue` to handle null will pass empty/whitespace through to file system calls, producing confusing errors like "Output directory does not exist: {Dir}" with a blank Dir.
- **Action**: Flag as **HIGH** if an `Option<string?>` value is used in a file system call without first checking `string.IsNullOrWhiteSpace`. A targeted error message must be emitted when the option is explicitly provided as empty or whitespace.

### Rule 7: Command Handler Branch Coverage
- **Description**: Every `SetHandler` implementation defines a CLI contract (valid/invalid inputs, exit codes, output files). Without invocation tests, refactors silently break this contract.
- **Action**: Flag as **MEDIUM** if a new `SetHandler` block has no corresponding `System.CommandLine` invocation tests. The following branches must be covered:
  - Invalid input argument → exit code 1
  - Nonexistent or whitespace output path → exit code 1
  - Resource not found (missing file, etc.) → exit code 1
  - Successful path → exit code 0 and expected side effect (file written, correct name)

### Rule 8: Verify Copyright Headers
- **Description**: Ensure all C# files have proper Microsoft copyright headers
- **Action**: If a `.cs` file is missing a copyright header:
  - Add the Microsoft copyright header at the top of the file
  - The header should be placed before any using statements or code
  - Maintain proper formatting and spacing

#### Required Copyright Header Format
```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
```

### Implementation Guidelines

#### When Reviewing Code:
1. **Input validation** (Rule 3): For every user-supplied value used in a file path or file name, confirm an allowlist pattern check and/or `ThrowIfNullOrWhiteSpace` guard is present before use.
2. **Exit code completeness** (Rule 4): Scan every `continue` and early `return` in command handlers — each one on a failure path must be preceded by `context.ExitCode = 1`.
3. **Data flow consistency** (Rule 5): When redaction or sanitization is added, check header lines and summary output for the same raw value.
4. **Kairo Check** (Rule 1):
   - Search for case-insensitive matches of "Kairo"
   - Review context to determine if it's:
     - A class name
     - A namespace
     - A variable name
     - A comment reference
     - A using statement
     - A string literal
   - Suggest appropriate alternatives based on the context

2. **Header Check**:
   - Verify the first non-empty lines of C# files
   - If missing, prepend the copyright header
   - Ensure there's a blank line after the header before other content
   - Do not add headers to:
     - Auto-generated files (marked with `<auto-generated>` or `// <auto-generated />`)
     - Designer files (`.Designer.cs`)
     - Files with `#pragma warning disable` at the top for generated code

#### Example of Proper File Structure:
```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace MyNamespace
{
    /// <summary>
    /// Class documentation
    /// </summary>
    public class MyClass
    {
        // Rest of the code...
    }
}
```

#### Example with File-Scoped Namespace (C# 10+):
```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;

namespace MyNamespace;

/// <summary>
/// Class documentation
/// </summary>
public class MyClass
{
    // Rest of the code...
}
```

#### Example with Top-Level Statements (C# 9+):
```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

var builder = WebApplication.CreateBuilder(args);

// Rest of the code...
```

### Auto-fix Behavior
When Copilot detects violations:
- **Kairo keyword**: Suggest inline replacement or flag for manual review
- **Missing header**: Automatically suggest adding the copyright header

### Exclusions
- Test files in `Tests/`, `test/`, or files ending with `.Tests.cs`, `.Test.cs` may have relaxed header requirements (but headers are still recommended)
- Auto-generated files (`.g.cs`, `.designer.cs`, files with auto-generated markers)
- Third-party code or vendored dependencies should not be modified
- Project files (`.csproj`, `.sln`), configuration files (`.json`, `.xml`, `.yaml`, `.md`) do not require copyright headers
- Build output directories (`bin/`, `obj/`)
- AssemblyInfo.cs files that are auto-generated
