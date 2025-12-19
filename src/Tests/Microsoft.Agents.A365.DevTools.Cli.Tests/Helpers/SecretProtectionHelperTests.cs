// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Runtime.InteropServices;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

public class SecretProtectionHelperTests
{
    private readonly ILogger _logger;

    public SecretProtectionHelperTests()
    {
        _logger = Substitute.For<ILogger>();
    }

    [Fact]
    public void ProtectSecret_WithNullOrEmpty_ReturnsInput()
    {
        // Arrange & Act
        var resultNull = SecretProtectionHelper.ProtectSecret(null!, _logger);
        var resultEmpty = SecretProtectionHelper.ProtectSecret("", _logger);
        var resultWhitespace = SecretProtectionHelper.ProtectSecret("   ", _logger);

        // Assert
        resultNull.Should().BeNull();
        resultEmpty.Should().BeEmpty();
        resultWhitespace.Should().Be("   ");
    }

    [Fact]
    public void UnprotectSecret_WithNullOrEmpty_ReturnsInput()
    {
        // Arrange & Act
        var resultNull = SecretProtectionHelper.UnprotectSecret(null!, false, _logger);
        var resultEmpty = SecretProtectionHelper.UnprotectSecret("", false, _logger);
        var resultWhitespace = SecretProtectionHelper.UnprotectSecret("   ", false, _logger);

        // Assert
        resultNull.Should().BeNull();
        resultEmpty.Should().BeEmpty();
        resultWhitespace.Should().Be("   ");
    }

    [Fact]
    public void IsProtectionAvailable_ReturnsCorrectValue()
    {
        // Arrange
        var expectedAvailable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // Act
        var actual = SecretProtectionHelper.IsProtectionAvailable();

        // Assert
        actual.Should().Be(expectedAvailable);
    }

    [Fact]
    public void ProtectAndUnprotectSecret_OnWindows_RoundTripsCorrectly()
    {
        // This test only runs on Windows where DPAPI is available
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Skip on non-Windows platforms
            return;
        }

        // Arrange
        var plaintext = "MySecretPassword123!@#";

        // Act
        var protected_secret = SecretProtectionHelper.ProtectSecret(plaintext, _logger);
        var unprotected_secret = SecretProtectionHelper.UnprotectSecret(protected_secret, true, _logger);

        // Assert
        protected_secret.Should().NotBeNullOrEmpty();
        protected_secret.Should().NotBe(plaintext, "secret should be encrypted");
        unprotected_secret.Should().Be(plaintext, "decrypted secret should match original");
    }

    [Fact]
    public void ProtectSecret_OnWindows_ProducesBase64EncodedString()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // Arrange
        var plaintext = "TestSecret";

        // Act
        var protected_secret = SecretProtectionHelper.ProtectSecret(plaintext, _logger);

        // Assert
        protected_secret.Should().NotBeNullOrEmpty();
        
        // Should be valid Base64
        var isBase64 = IsBase64String(protected_secret);
        isBase64.Should().BeTrue("protected secret should be Base64 encoded on Windows");
    }

    [Fact]
    public void ProtectSecret_OnNonWindows_ReturnsPlaintext()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Skip this test on Windows
            return;
        }

        // Arrange
        var plaintext = "MySecret";

        // Act
        var result = SecretProtectionHelper.ProtectSecret(plaintext, _logger);

        // Assert
        result.Should().Be(plaintext, "on non-Windows platforms, secret should remain plaintext");
    }

    [Fact]
    public void UnprotectSecret_WithIsProtectedFalse_ReturnsPlaintext()
    {
        // Arrange
        var plaintext = "MyPlaintextSecret";

        // Act
        var result = SecretProtectionHelper.UnprotectSecret(plaintext, false, _logger);

        // Assert
        result.Should().Be(plaintext, "when isProtected is false, should return input as-is");
    }

    [Fact]
    public void UnprotectSecret_OnNonWindows_ReturnsInput()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // Arrange
        var input = "SomeData";

        // Act
        var result = SecretProtectionHelper.UnprotectSecret(input, true, _logger);

        // Assert
        result.Should().Be(input, "on non-Windows platforms, should return input as-is");
    }

    [Fact]
    public void UnprotectSecret_WithInvalidBase64_ReturnsInput()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // Arrange
        var invalidBase64 = "This is not valid base64!!!";

        // Act
        var result = SecretProtectionHelper.UnprotectSecret(invalidBase64, true, _logger);

        // Assert - should return input as-is when decryption fails
        result.Should().Be(invalidBase64, "should return input as-is when decryption fails");
    }

    [Theory]
    [InlineData("SimplePassword")]
    [InlineData("Complex!@#$%Password123")]
    [InlineData("")]
    [InlineData("Unicode-测试-🔒")]
    public void ProtectAndUnprotect_WithVariousInputs_HandlesCorrectly(string input)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // Act
        var protected_secret = SecretProtectionHelper.ProtectSecret(input, _logger);
        var unprotected_secret = SecretProtectionHelper.UnprotectSecret(protected_secret, true, _logger);

        // Assert
        if (string.IsNullOrWhiteSpace(input))
        {
            unprotected_secret.Should().Be(input);
        }
        else
        {
            protected_secret.Should().NotBe(input, "should be encrypted");
            unprotected_secret.Should().Be(input, "should decrypt back to original");
        }
    }

    private static bool IsBase64String(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        try
        {
            Convert.FromBase64String(value);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
