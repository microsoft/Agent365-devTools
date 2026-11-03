// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

/// <summary>
/// Tests for validating command lines passed to 'cmd.exe /c'.
/// </summary>
public class CommandArgumentGuardTests
{
    private const string SampleGuid = "37fc1c47-5eda-46ca-93b2-76baba49e605";

    [Fact]
    public void RequireGuid_ValidGuid_ReturnsCanonicalForm()
    {
        CommandArgumentGuard.RequireGuid("{37FC1C47-5EDA-46CA-93B2-76BABA49E605}", "agenticUserId")
            .Should().Be(SampleGuid, because: "only the canonical GUID text may reach the command line");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x & other")]
    [InlineData(SampleGuid + " & other")]
    public void RequireGuid_NonGuid_Throws(string? value)
    {
        var act = () => CommandArgumentGuard.RequireGuid(value, "agenticUserId");

        act.Should().Throw<ArgumentException>().WithMessage("*agenticUserId*GUID*");
    }

    [Theory]
    [InlineData("az ad user delete --id x & other")]
    [InlineData("az ad user delete --id x && other")]
    [InlineData("az ad user delete --id x | other")]
    [InlineData("az ad user delete --id x > out.txt")]
    [InlineData("az ad user delete --id x < in.txt")]
    [InlineData("az ad user delete --id x ^& other")]
    [InlineData("az ad sp list --filter \"appId eq 'x' \" & other & \"\"")]
    [InlineData("az ad sp list --filter \"appId eq 'x")]
    [InlineData("az ad user delete --id x\r\nother")]
    [InlineData("az ad user delete --id x\nother")]
    public void EnsureSafeForCmd_UnquotedMetacharacters_Throws(string commandLine)
    {
        var act = () => CommandArgumentGuard.EnsureSafeForCmd(commandLine);

        act.Should().Throw<ArgumentException>(because: "cmd.exe would run a second command or redirect output");
    }

    // Shapes of the real az command lines the CLI builds; a false positive here breaks setup or cleanup.
    [Theory]
    [InlineData("az ad app delete --id " + SampleGuid)]
    [InlineData("az ad user delete --id " + SampleGuid)]
    [InlineData("az ad sp create --id " + SampleGuid)]
    [InlineData("az ad sp list --filter \"appId eq '" + SampleGuid + "'\" --query \"[].{objectId:id,appId:appId,displayName:displayName}\" --output json")]
    [InlineData("az rest --method GET --url \"https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId eq '" + SampleGuid + "'&$select=id,appRoles\"")]
    [InlineData("az rest --method GET --url \"https://graph.microsoft.com/v1.0/oauth2PermissionGrants?$filter=clientId%20eq%20%27" + SampleGuid + "%27\"")]
    [InlineData("az rest --method PATCH --url \"https://graph.microsoft.com/v1.0/oauth2PermissionGrants/abc_DEF-123\" --body @\"C:\\Users\\me\\AppData\\Local\\Temp\\tmp1.json\" --headers Content-Type=application/json")]
    [InlineData("az account show --query tenantId -o tsv")]
    [InlineData("az login --tenant " + SampleGuid)]
    [InlineData("az --version")]
    public void EnsureSafeForCmd_RealAzCommandLines_DoNotThrow(string commandLine)
    {
        var act = () => CommandArgumentGuard.EnsureSafeForCmd(commandLine);

        act.Should().NotThrow(because: "metacharacters inside balanced double quotes are literal to cmd.exe");
    }

    [Fact]
    public void BuildCmdWrapperArguments_SafeArguments_PrefixesSlashC()
    {
        CommandExecutor.BuildCmdWrapperArguments("az", "ad user delete --id " + SampleGuid)
            .Should().Be("/c az ad user delete --id " + SampleGuid);
    }

    [Fact]
    public void BuildCmdWrapperArguments_UnsafeArguments_Throws()
    {
        var act = () => CommandExecutor.BuildCmdWrapperArguments("az", "ad user delete --id x & other");

        act.Should().Throw<ArgumentException>(because: "the executor must not pass an unsafe command line to cmd.exe");
    }
}
