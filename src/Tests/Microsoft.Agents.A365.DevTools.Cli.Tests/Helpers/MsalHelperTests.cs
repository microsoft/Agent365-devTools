// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using NSubstitute;
using System.Reflection;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

public class MsalHelperTests
{
    private const string TestVerificationUrl = "https://microsoft.com/devicelogin";
    private const string TestUserCode = "ABCD1234";

    /// <summary>
    /// DeviceCodeResult has an internal constructor in MSAL 4.x.
    /// Create one via reflection so we can invoke the callback in tests.
    /// </summary>
    private static DeviceCodeResult CreateDeviceCodeResult(string verificationUrl = TestVerificationUrl, string userCode = TestUserCode)
    {
        var ctor = typeof(DeviceCodeResult)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Cannot find DeviceCodeResult internal constructor.");

        // MSAL 4.x internal constructor parameter order:
        // (string userCode, string deviceCode, string verificationUrl,
        //  DateTimeOffset expiresOn, long interval, string message,
        //  string clientId, ISet<string> scopes)
        return (DeviceCodeResult)ctor.Invoke(new object[]
        {
            userCode,
            "device-code-value",
            verificationUrl,
            DateTimeOffset.UtcNow.AddMinutes(15),
            5L,
            $"To sign in, use {verificationUrl} and enter {userCode}",
            "test-client-id",
            new HashSet<string> { "test.scope" }
        });
    }

    #region Delegate Creation Tests

    [Fact]
    public void CreateDeviceCodeCallback_WithLogger_ReturnsNonNullDelegate()
    {
        var logger = Substitute.For<ILogger>();
        var callback = MsalHelper.CreateDeviceCodeCallback(logger);
        Assert.NotNull(callback);
    }

    [Fact]
    public void CreateDeviceCodeCallback_WithNullLogger_ReturnsNonNullDelegate()
    {
        var callback = MsalHelper.CreateDeviceCodeCallback(null);
        Assert.NotNull(callback);
    }

    #endregion

    #region Logger Branch Tests

    [Fact]
    public async Task CreateDeviceCodeCallback_WithLogger_CallsLogInformationWithVerificationUrl()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<Microsoft.Extensions.Logging.LogLevel>()).Returns(true);

        var callback = MsalHelper.CreateDeviceCodeCallback(logger);
        var result = CreateDeviceCodeResult();
        await callback(result);

        // Verify LogInformation was called at least once (for the verification URL line and user code line)
        logger.Received().Log(
            Microsoft.Extensions.Logging.LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CreateDeviceCodeCallback_WithLogger_CompletesSuccessfully()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<Microsoft.Extensions.Logging.LogLevel>()).Returns(true);

        var callback = MsalHelper.CreateDeviceCodeCallback(logger);
        var result = CreateDeviceCodeResult();

        // Should not throw
        var task = callback(result);
        await task;

        Assert.True(task.IsCompletedSuccessfully);
    }

    #endregion

    #region Console.Error Branch Tests

    [Fact]
    public async Task CreateDeviceCodeCallback_WithNullLogger_WritesVerificationUrlToConsoleError()
    {
        var callback = MsalHelper.CreateDeviceCodeCallback(null);
        var result = CreateDeviceCodeResult(TestVerificationUrl, TestUserCode);

        using var sw = new StringWriter();
        var original = Console.Error;
        Console.SetError(sw);
        try
        {
            await callback(result);
        }
        finally
        {
            Console.SetError(original);
        }

        var output = sw.ToString();
        Assert.Contains(TestVerificationUrl, output);
    }

    [Fact]
    public async Task CreateDeviceCodeCallback_WithNullLogger_WritesUserCodeToConsoleError()
    {
        var callback = MsalHelper.CreateDeviceCodeCallback(null);
        var result = CreateDeviceCodeResult(TestVerificationUrl, TestUserCode);

        using var sw = new StringWriter();
        var original = Console.Error;
        Console.SetError(sw);
        try
        {
            await callback(result);
        }
        finally
        {
            Console.SetError(original);
        }

        var output = sw.ToString();
        Assert.Contains(TestUserCode, output);
    }

    [Fact]
    public async Task CreateDeviceCodeCallback_WithNullLogger_CompletesSuccessfully()
    {
        var callback = MsalHelper.CreateDeviceCodeCallback(null);
        var result = CreateDeviceCodeResult();

        using var sw = new StringWriter();
        var original = Console.Error;
        Console.SetError(sw);
        try
        {
            var task = callback(result);
            await task;
            Assert.True(task.IsCompletedSuccessfully);
        }
        finally
        {
            Console.SetError(original);
        }
    }

    #endregion
}
