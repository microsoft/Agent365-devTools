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

### Rule 3: Verify Copyright Headers
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
1. **Kairo Check**:
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
