// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

public class CommandStringHelperTests
{
    [Theory]
    [InlineData("simple-guid", "simple-guid")]
    [InlineData("12345678-1234-1234-1234-123456789012", "12345678-1234-1234-1234-123456789012")]
    [InlineData("value'with'quotes", "value''with''quotes")]
    [InlineData("'; rm -rf /; echo '", "''; rm -rf /; echo ''")]
    [InlineData("test'; Invoke-Expression 'malicious", "test''; Invoke-Expression ''malicious")]
    public void EscapePowerShellString_EscapesSingleQuotesCorrectly(string input, string expected)
    {
        // Act
        var result = CommandStringHelper.EscapePowerShellString(input);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void EscapePowerShellString_WithNullInput_ReturnsNull()
    {
        // Act
        var result = CommandStringHelper.EscapePowerShellString(null!);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void EscapePowerShellString_WithEmptyString_ReturnsEmptyString()
    {
        // Act
        var result = CommandStringHelper.EscapePowerShellString(string.Empty);

        // Assert
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData("simple-value", false)]
    [InlineData("12345678-1234-1234-1234-123456789012", false)]
    [InlineData("value'with'quote", true)]
    [InlineData("value\"with\"doublequote", true)]
    [InlineData("value;with;semicolon", true)]
    [InlineData("value`with`backtick", true)]
    [InlineData("value$with$dollar", true)]
    [InlineData("value&with&ampersand", true)]
    [InlineData("value|with|pipe", true)]
    [InlineData("value<with>angle", true)]
    [InlineData("value\nwith\nnewline", true)]
    [InlineData("value\rwith\rcarriagereturn", true)]
    [InlineData("value\twith\ttab", true)]
    public void ContainsDangerousCharacters_DetectsCorrectly(string input, bool expected)
    {
        // Act
        var result = CommandStringHelper.ContainsDangerousCharacters(input);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void ContainsDangerousCharacters_WithNullInput_ReturnsFalse()
    {
        // Act
        var result = CommandStringHelper.ContainsDangerousCharacters(null!);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void ContainsDangerousCharacters_WithEmptyString_ReturnsFalse()
    {
        // Act
        var result = CommandStringHelper.ContainsDangerousCharacters(string.Empty);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void EscapePowerShellString_PreventsSQLInjectionStyle()
    {
        // Arrange - simulate an attack attempting to inject commands
        var maliciousClientAppId = "abc123'; Remove-Item -Path 'C:\\*' -Recurse; Write-Host 'pwned";

        // Act
        var escaped = CommandStringHelper.EscapePowerShellString(maliciousClientAppId);

        // Assert
        // The single quotes should be doubled, making the attack harmless
        escaped.Should().Be("abc123''; Remove-Item -Path ''C:\\*'' -Recurse; Write-Host ''pwned");
        
        // Verify the escaped string would be treated as literal text in PowerShell
        // When used in a single-quoted string context like: 'appId eq '{escaped}''
        // PowerShell will interpret it as a literal string, not as code to execute
        escaped.Should().NotBe(maliciousClientAppId);
        escaped.Should().Contain("''"); // Doubled quotes indicate proper escaping
    }

    [Fact]
    public void EscapePowerShellString_WorksInRealWorldScenario()
    {
        // Arrange - simulate building an az rest command with escaped values
        var clientAppId = "test-app'; Write-Host 'injected";
        var graphToken = "token'; Invoke-Expression 'malicious";

        // Act
        var escapedAppId = CommandStringHelper.EscapePowerShellString(clientAppId);
        var escapedToken = CommandStringHelper.EscapePowerShellString(graphToken);

        var command = $"rest --method GET --url \"https://graph.microsoft.com/v1.0/applications?$filter=appId eq '{escapedAppId}'\" --headers \"Authorization=Bearer {escapedToken}\"";

        // Assert
        command.Should().Contain("test-app''; Write-Host ''injected");
        command.Should().Contain("token''; Invoke-Expression ''malicious");
        
        // The command string is now safe - the injected code will be treated as literal text
        // because single quotes within PowerShell single-quoted strings are escaped as ''
    }
}
