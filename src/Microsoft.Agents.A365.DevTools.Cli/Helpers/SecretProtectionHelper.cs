// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Helper class for encrypting and decrypting sensitive data using platform-specific methods.
/// On Windows, uses DPAPI (Data Protection API) for user-level encryption.
/// On non-Windows platforms, secrets are stored in plaintext.
/// </summary>
public static class SecretProtectionHelper
{
    /// <summary>
    /// Encrypts a plaintext secret using platform-specific protection.
    /// On Windows: Uses DPAPI with CurrentUser scope.
    /// On other platforms: Returns plaintext (no encryption available).
    /// </summary>
    /// <param name="plaintext">The plaintext secret to protect</param>
    /// <param name="logger">Logger for warnings and errors</param>
    /// <returns>Protected secret (Base64 encoded on Windows, plaintext otherwise)</returns>
    public static string ProtectSecret(string plaintext, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return plaintext;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
                var protectedBytes = ProtectedData.Protect(
                    plaintextBytes,
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);

                return Convert.ToBase64String(protectedBytes);
            }
            else
            {
                logger.LogWarning("DPAPI encryption not available on this platform. Secret will be stored in plaintext.");
                return plaintext;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to encrypt secret, storing in plaintext: {Message}", ex.Message);
            return plaintext;
        }
    }

    /// <summary>
    /// Decrypts a protected secret using platform-specific methods.
    /// On Windows with protected data: Uses DPAPI to decrypt.
    /// Otherwise: Returns data as-is (already plaintext).
    /// </summary>
    /// <param name="protectedData">The protected secret (Base64 encoded DPAPI data or plaintext)</param>
    /// <param name="isProtected">Indicates whether the data was encrypted (true) or is plaintext (false)</param>
    /// <param name="logger">Logger for warnings and errors</param>
    /// <returns>Decrypted plaintext secret</returns>
    public static string UnprotectSecret(string protectedData, bool isProtected, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(protectedData))
        {
            return protectedData;
        }

        try
        {
            if (isProtected && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Decrypt using Windows DPAPI
                var protectedBytes = Convert.FromBase64String(protectedData);
                var plaintextBytes = ProtectedData.Unprotect(
                    protectedBytes,
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);
                
                return System.Text.Encoding.UTF8.GetString(plaintextBytes);
            }
            else
            {
                // Not protected or not on Windows - return as-is (plaintext)
                return protectedData;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to decrypt secret: {Message}", ex.Message);
            logger.LogWarning("Attempting to use the secret as-is (may be plaintext)");
            // Return the protected data as-is - caller will handle the error
            return protectedData;
        }
    }

    /// <summary>
    /// Indicates whether secret protection is available on the current platform.
    /// </summary>
    /// <returns>True if running on Windows (DPAPI available), false otherwise</returns>
    public static bool IsProtectionAvailable()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }
}
