// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Constants;

/// <summary>
/// Test constants used across unit tests.
/// Provides consistent, predictable values for testing.
/// </summary>
public static class TestConstants
{
    #region Azure Configuration Test Values

    public const string TestTenantId = "12345678-1234-1234-1234-123456789012";
    public const string TestSubscriptionId = "87654321-4321-4321-4321-210987654321";
    public const string TestResourceGroup = "rg-test";
    public const string TestLocation = "eastus";
    public const string TestAppServicePlanName = "asp-test";
    public const string TestAppServicePlanSku = "B1";
    public const string TestWebAppName = "webapp-test";

    #endregion

    #region Agent Configuration Test Values

    public const string TestAgentDisplayName = "Test Agent";
    public const string TestBotName = "test-bot";
    public const string TestBotDescription = "Test Bot Description";

    #endregion

    #region Project Configuration Test Values

    public const string TestDeploymentProjectPath = "./test/path";

    #endregion

    #region API Endpoint Test Values

    public const string TestAgent365ToolsEndpoint = "https://test.mcp.example.com";
    public const string TestTeamGraphApiUrl = "https://test.teamsgraph.example.com";

    #endregion

    #region Common Test Scopes

    public static readonly List<string> TestAgentScopes = new() { "User.Read" };
    public static readonly List<string> TestExtendedScopes = new() { "User.Read", "Mail.Send" };

    #endregion

    #region Dynamic Property Test Values

    public const string TestManagedIdentityPrincipalId = "abcd1234-5678-90ef-ghij-klmnopqrstuv";
    public const string TestAgentIdentityId = "efgh5678-90ab-cdef-1234-567890abcdef";
    public const string TestBotId = "ijkl9012-3456-7890-abcd-ef1234567890";
    public const string TestBotMsaAppId = "mnop3456-7890-1234-5678-90abcdef1234";
    public const string TestBotMessagingEndpoint = "https://test.bot.example.com/api/messages";

    #endregion
}