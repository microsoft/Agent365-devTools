// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Tests for ConfigurationWizardService.PromptForCustomBlueprintPermissions — the interactive
/// wizard step that collects optional custom blueprint permissions.
/// Uses reflection (same pattern as ConfigurationWizardServiceWebAppNameTests) to invoke the private method.
/// </summary>
[Collection("ConfigTests")]
public class ConfigurationWizardServicePermissionsTests
{
    private static readonly string ValidGuid = "00000003-0000-0000-c000-000000000000";
    private static readonly string AnotherValidGuid = "11111111-1111-1111-1111-111111111111";

    private static ConfigurationWizardService CreateService()
    {
        var azureCli = Substitute.For<IAzureCliService>();
        var platformDetector = Substitute.For<PlatformDetector>(Substitute.For<ILogger<PlatformDetector>>());
        var logger = Substitute.For<ILogger<ConfigurationWizardService>>();
        return new ConfigurationWizardService(azureCli, platformDetector, logger);
    }

    /// <summary>
    /// Invokes the private PromptForCustomBlueprintPermissions method via reflection.
    /// </summary>
    private static List<CustomResourcePermission> InvokePrompt(
        ConfigurationWizardService svc,
        List<CustomResourcePermission>? existing,
        string consoleInput)
    {
        var method = svc.GetType().GetMethod(
            "PromptForCustomBlueprintPermissions",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull("PromptForCustomBlueprintPermissions method must exist");

        var originalIn = Console.In;
        var originalOut = Console.Out;
        using var inputReader = new StringReader(consoleInput);
        using var outputWriter = new StringWriter();
        try
        {
            Console.SetIn(inputReader);
            Console.SetOut(outputWriter);
            return (List<CustomResourcePermission>)method!.Invoke(svc, new object?[] { existing })!;
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }

    /// <summary>
    /// Invokes the prompt and also captures the console output.
    /// </summary>
    private static (List<CustomResourcePermission> result, string output) InvokePromptWithOutput(
        ConfigurationWizardService svc,
        List<CustomResourcePermission>? existing,
        string consoleInput)
    {
        var method = svc.GetType().GetMethod(
            "PromptForCustomBlueprintPermissions",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull("PromptForCustomBlueprintPermissions method must exist");

        var originalIn = Console.In;
        var originalOut = Console.Out;
        using var inputReader = new StringReader(consoleInput);
        using var outputWriter = new StringWriter();
        try
        {
            Console.SetIn(inputReader);
            Console.SetOut(outputWriter);
            var result = (List<CustomResourcePermission>)method!.Invoke(svc, new object?[] { existing })!;
            return (result, outputWriter.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }

    // --- Decline (y/N = N) ---

    [Fact]
    public void Prompt_UserDeclines_ReturnsEmptyList()
    {
        var svc = CreateService();
        // "n" = decline configuration
        var result = InvokePrompt(svc, null, "n\n");
        result.Should().BeEmpty();
    }

    [Fact]
    public void Prompt_UserPressesEnter_DefaultDeclines_ReturnsEmptyList()
    {
        var svc = CreateService();
        // empty Enter = accept default (N)
        var result = InvokePrompt(svc, null, "\n");
        result.Should().BeEmpty();
    }

    [Fact]
    public void Prompt_UserDeclines_ExistingPermissionsPreserved()
    {
        var svc = CreateService();
        var existing = new List<CustomResourcePermission>
        {
            new CustomResourcePermission { ResourceAppId = ValidGuid, Scopes = new List<string> { "User.Read" } }
        };
        var result = InvokePrompt(svc, existing, "n\n");
        result.Should().HaveCount(1);
        result[0].ResourceAppId.Should().Be(ValidGuid);
    }

    // --- Accept and add permissions ---

    [Fact]
    public void Prompt_UserAddsOnePermission_ReturnsIt()
    {
        var svc = CreateService();
        // Accept: y\n, then GUID, scopes, then blank GUID to exit
        var input = $"y\n{ValidGuid}\nUser.Read,Mail.Send\n\n";
        var result = InvokePrompt(svc, null, input);

        result.Should().HaveCount(1);
        result[0].ResourceAppId.Should().Be(ValidGuid);
        result[0].Scopes.Should().BeEquivalentTo(new[] { "User.Read", "Mail.Send" });
    }

    [Fact]
    public void Prompt_UserAddsTwoPermissions_ReturnsBoth()
    {
        var svc = CreateService();
        var input = $"y\n{ValidGuid}\nUser.Read\n{AnotherValidGuid}\nMail.Send\n\n";
        var result = InvokePrompt(svc, null, input);

        result.Should().HaveCount(2);
        result.Should().Contain(p => p.ResourceAppId == ValidGuid);
        result.Should().Contain(p => p.ResourceAppId == AnotherValidGuid);
    }

    [Fact]
    public void Prompt_UserUpdatesExistingPermission_ScopesReplaced()
    {
        var svc = CreateService();
        var existing = new List<CustomResourcePermission>
        {
            new CustomResourcePermission { ResourceAppId = ValidGuid, Scopes = new List<string> { "User.Read" } }
        };
        // Accept, provide same GUID with new scopes, then blank to exit
        var input = $"y\n{ValidGuid}\nMail.Send\n\n";
        var result = InvokePrompt(svc, existing, input);

        result.Should().HaveCount(1);
        result[0].Scopes.Should().BeEquivalentTo(new[] { "Mail.Send" });
        result[0].Scopes.Should().NotContain("User.Read");
    }

    // --- Validation: invalid GUID re-prompts GUID ---

    [Fact]
    public void Prompt_InvalidGuid_RePromptsGuid()
    {
        var svc = CreateService();
        // Accept, invalid GUID, then valid GUID with scopes, blank to exit
        var input = $"y\nnot-a-guid\n{ValidGuid}\nUser.Read\n\n";
        var (result, output) = InvokePromptWithOutput(svc, null, input);

        result.Should().HaveCount(1);
        result[0].ResourceAppId.Should().Be(ValidGuid);
        output.Should().Contain("ERROR: Must be a valid GUID format");
    }

    // --- CR-009: invalid scopes re-prompts scopes only, not GUID ---

    [Fact]
    public void Prompt_EmptyScopes_RePromptsScopesNotGuid()
    {
        var svc = CreateService();
        // Accept, valid GUID, empty scopes (error), then valid scopes, blank to exit
        var input = $"y\n{ValidGuid}\n   \nUser.Read\n\n";
        var (result, output) = InvokePromptWithOutput(svc, null, input);

        result.Should().HaveCount(1);
        result[0].ResourceAppId.Should().Be(ValidGuid);
        result[0].Scopes.Should().BeEquivalentTo(new[] { "User.Read" });
        output.Should().Contain("ERROR: At least one scope is required");

        // With the CR-009 fix the GUID prompt appears exactly twice:
        //   1st — for the actual GUID entry
        //   2nd — for the empty-Enter exit from the outer loop
        // Without the fix it would appear 3× because the scopes retry path
        // fell through to the outer loop and consumed "User.Read" as an invalid GUID.
        var guidPromptCount = CountOccurrences(output, "Resource App ID (GUID)");
        guidPromptCount.Should().Be(2, "GUID prompt should appear once for entry and once for exit, not extra times due to scope validation errors");
    }

    [Fact]
    public void Prompt_DuplicateScopesInInput_RePromptsScopesNotGuid()
    {
        var svc = CreateService();
        // Accept, valid GUID, duplicate scopes (validation error), then valid scopes, blank to exit
        var input = $"y\n{ValidGuid}\nUser.Read,User.Read\nMail.Send\n\n";
        var (result, output) = InvokePromptWithOutput(svc, null, input);

        result.Should().HaveCount(1);
        result[0].Scopes.Should().BeEquivalentTo(new[] { "Mail.Send" });
        output.Should().Contain("Duplicate scopes");

        // Same reasoning as Prompt_EmptyScopes_RePromptsScopesNotGuid: 2 = entry + exit
        var guidPromptCount = CountOccurrences(output, "Resource App ID (GUID)");
        guidPromptCount.Should().Be(2, "GUID prompt should appear once for entry and once for exit, not extra times due to scope validation errors");
    }

    // --- Scopes whitespace trimming ---

    [Fact]
    public void Prompt_ScopesWithExtraWhitespace_TrimmedCorrectly()
    {
        var svc = CreateService();
        var input = $"y\n{ValidGuid}\n  User.Read  ,  Mail.Send  \n\n";
        var result = InvokePrompt(svc, null, input);

        result.Should().HaveCount(1);
        result[0].Scopes.Should().BeEquivalentTo(new[] { "User.Read", "Mail.Send" });
    }

    // --- Output verification ---

    [Fact]
    public void Prompt_ShowsPermissionsPromptBeforeAccepting()
    {
        var svc = CreateService();
        var (_, output) = InvokePromptWithOutput(svc, null, "n\n");

        output.Should().Contain("Optional: Custom Blueprint Permissions");
        output.Should().Contain("Most agents do not require this");
        output.Should().Contain("Configure custom blueprint permissions? (y/N)");
    }

    [Fact]
    public void Prompt_WithExistingPermissions_ShowsCurrentlyConfigured()
    {
        var svc = CreateService();
        var existing = new List<CustomResourcePermission>
        {
            new CustomResourcePermission
            {
                ResourceAppId = ValidGuid,
                ResourceName = "Microsoft Graph",
                Scopes = new List<string> { "User.Read" }
            }
        };
        var (_, output) = InvokePromptWithOutput(svc, existing, "n\n");

        output.Should().Contain("Currently configured");
        output.Should().Contain("Microsoft Graph");
        output.Should().Contain("User.Read");
    }

    [Fact]
    public void Prompt_PermissionAdded_PrintsConfirmation()
    {
        var svc = CreateService();
        var input = $"y\n{ValidGuid}\nUser.Read\n\n";
        var (_, output) = InvokePromptWithOutput(svc, null, input);

        output.Should().Contain("Permission added.");
    }

    [Fact]
    public void Prompt_PermissionUpdated_PrintsConfirmation()
    {
        var svc = CreateService();
        var existing = new List<CustomResourcePermission>
        {
            new CustomResourcePermission { ResourceAppId = ValidGuid, Scopes = new List<string> { "User.Read" } }
        };
        var input = $"y\n{ValidGuid}\nMail.Send\n\n";
        var (_, output) = InvokePromptWithOutput(svc, existing, input);

        output.Should().Contain("Permission updated.");
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
