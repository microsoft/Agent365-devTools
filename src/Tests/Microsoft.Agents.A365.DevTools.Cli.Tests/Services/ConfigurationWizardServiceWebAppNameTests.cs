// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using FluentAssertions;
using Xunit;
using NSubstitute;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

public class ConfigurationWizardServiceWebAppNameTests
{
    [Theory]
    [InlineData("a", "01010000", 8)] 
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789", "01010000", 33)] // too long, should truncate
    [InlineData("abc", "01010000", 10)] // normal
    public void GenerateValidWebAppName_EnforcesLength(string cleanName, string timestamp, int expectedLength)
    {
        var method = typeof(ConfigurationWizardService)
            .GetMethod("GenerateValidWebAppName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull();
        var result = method!.Invoke(null, new object[] { cleanName, timestamp }) as string;
        result.Should().NotBeNull();
        result!.Length.Should().Be(expectedLength);
        result.Should().MatchRegex("^[a-z0-9-]+$");
    }

    [Fact]
    public void GenerateDerivedNames_WebAppName_AlwaysValidLength()
    {
        var azureCli = Substitute.For<IAzureCliService>();
        var platformDetector = Substitute.For<PlatformDetector>(Substitute.For<ILogger<PlatformDetector>>());
        var logger = Substitute.For<ILogger<ConfigurationWizardService>>();
        var svc = new ConfigurationWizardService(azureCli, platformDetector, logger);
        var method = svc.GetType().GetMethod("GenerateDerivedNames", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method.Should().NotBeNull();
        for (int i = 1; i < 60; i++)
        {
            var agentName = new string('a', i);
            var derived = method!.Invoke(svc, new object[] { agentName, "contoso.com" });
            derived.Should().NotBeNull();
            var webAppName = (string)derived!.GetType().GetProperty("WebAppName")!.GetValue(derived)!;
            webAppName.Length.Should().BeGreaterOrEqualTo(2);
            webAppName.Length.Should().BeLessOrEqualTo(33);
        }
    }

    [Theory]
    [InlineData("sellak@testcsaaa.onmicrosoft.com", "testcsaaa.onmicrosoft.com")]
    [InlineData("user@contoso.com", "contoso.com")]
    [InlineData("admin@sub.domain.com", "sub.domain.com")]
    [InlineData("invalid", "")]
    [InlineData("", "")]
    public void ExtractDomainFromAccount_HandlesVariousCases(string accountName, string expectedDomain)
    {
        var method = typeof(ConfigurationWizardService)
            .GetMethod("ExtractDomainFromAccount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull();
        var accountInfo = new AzureAccountInfo { Name = accountName, User = new AzureUser { Name = accountName } };
        var result = method!.Invoke(null, new object[] { accountInfo }) as string;
        result.Should().Be(expectedDomain);
    }

    [Fact]
    public void GenerateDerivedNames_UsesDomainInUPN()
    {
        var azureCli = Substitute.For<IAzureCliService>();
        var platformDetector = Substitute.For<PlatformDetector>(Substitute.For<ILogger<PlatformDetector>>());
        var logger = Substitute.For<ILogger<ConfigurationWizardService>>();
        var svc = new ConfigurationWizardService(azureCli, platformDetector, logger);
        var method = svc.GetType().GetMethod("GenerateDerivedNames", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method.Should().NotBeNull();
        var agentName = "agent";
        var domain = "contoso.com";
        var derived = method!.Invoke(svc, new object[] { agentName, domain });
        derived.Should().NotBeNull();
        var upn = (string)derived!.GetType().GetProperty("AgentUserPrincipalName")!.GetValue(derived)!;
        upn.Should().EndWith("@contoso.com");
    }
}
