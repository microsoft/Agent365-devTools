// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Shared helpers for MSAL-based authentication flows used across multiple services.
/// </summary>
public static class MsalHelper
{
    /// <summary>
    /// Creates the standard MSAL device code callback that logs the verification URL and user code.
    /// Shared across all interactive auth flows so the device code prompt is consistent.
    /// </summary>
    /// <param name="logger">Optional logger. When null, falls back to <see cref="Console.Error"/>.</param>
    public static Func<DeviceCodeResult, Task> CreateDeviceCodeCallback(ILogger? logger)
    {
        return deviceCode =>
        {
            if (logger != null)
            {
                logger.LogInformation("");
                logger.LogInformation("==========================================================================");
                logger.LogInformation("To sign in, use a web browser to open the page:");
                logger.LogInformation("    {VerificationUrl}", deviceCode.VerificationUrl);
                logger.LogInformation("");
                logger.LogInformation("And enter the code: {UserCode}", deviceCode.UserCode);
                logger.LogInformation("==========================================================================");
                logger.LogInformation("");
            }
            else
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("==========================================================================");
                Console.Error.WriteLine("To sign in, use a web browser to open the page:");
                Console.Error.WriteLine($"    {deviceCode.VerificationUrl}");
                Console.Error.WriteLine();
                Console.Error.WriteLine($"And enter the code: {deviceCode.UserCode}");
                Console.Error.WriteLine("==========================================================================");
                Console.Error.WriteLine();
            }
            return Task.CompletedTask;
        };
    }
}
