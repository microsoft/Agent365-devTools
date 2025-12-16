// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Tests;

/// <summary>
/// Constants used across test files
/// </summary>
public static class TestConstants
{
    /// <summary>
    /// Test skip reasons
    /// </summary>
    public static class SkipReasons
    {
        public const string RequiresInteractiveFileOpening = 
            "Requires interactive file opening - causes Windows 'Select app' dialog during test execution";
        
        public const string RequiresAzureCliIntegration = 
            "Integration test - depends on external Azure CLI authentication state";
        
        public const string RequiresInteractiveConfirmation = 
            "Requires interactive user confirmation - command enforces user prompts";
        
        public const string RequiresHttpMocking = 
            "Requires HTTP mocking infrastructure - uses HttpClient directly";
        
        public const string RequiresCommandLineInvocation = 
            "Disabled due to System.CommandLine invocation overhead when running full test suite";
    }
}
