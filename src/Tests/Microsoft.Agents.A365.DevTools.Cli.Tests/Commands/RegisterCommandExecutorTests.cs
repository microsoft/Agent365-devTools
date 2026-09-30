// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands;

/// <summary>
/// Invocation tests for the register-external-mcp-server command's pre-flight validation of
/// --secret-lifetime-months and --connectivity. Exercises the guards in
/// <see cref="RegisterCommandExecutor"/> via the full System.CommandLine pipeline so the
/// resulting exit code is asserted as the user would observe it.
/// </summary>
public class RegisterCommandExecutorTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("25")]
    [InlineData("-1")]
    [InlineData("48")]
    public async Task RegisterExternalMcpServer_WithOutOfRangeSecretLifetimeMonths_ReturnsExitCode1AndDoesNotCallTooling(string lifetimeArg)
    {
        // Arrange
        var logger = Substitute.For<ILogger>();
        var toolingService = Substitute.For<IAgent365ToolingService>();
        var command = DevelopMcpCommand.CreateCommand(logger, toolingService, graphApiService: null);

        var args = new[]
        {
            "register-external-mcp-server",
            "--server-name", "ext_Test",
            "--server-url", "https://example.com/mcp",
            "--secret-lifetime-months", lifetimeArg,
        };

        // Act
        var exitCode = await command.InvokeAsync(args);

        // Assert — exit code surfaces failure as the user would observe it
        exitCode.Should().Be(1);

        // Assert — error log mentions the valid range so the user knows how to recover
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null
                && state.ToString()!.Contains("--secret-lifetime-months")
                && state.ToString()!.Contains("between 1 and 24")
                && state.ToString()!.Contains($"Got: {lifetimeArg}")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Assert — validation short-circuits before any downstream tooling call
        await toolingService.DidNotReceive().AddMcpServerAsync(
            Arg.Any<Microsoft.Agents.A365.DevTools.Cli.Models.AddMcpServerRequest>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await toolingService.DidNotReceive().LogRegisterUsageAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("publik")]
    [InlineData("vnet")]
    [InlineData("Public Internet")]
    public async Task RegisterExternalMcpServer_WithInvalidConnectivity_ReturnsExitCode1AndDoesNotCallTooling(string connectivityArg)
    {
        // Arrange
        var logger = Substitute.For<ILogger>();
        var toolingService = Substitute.For<IAgent365ToolingService>();
        var command = DevelopMcpCommand.CreateCommand(logger, toolingService, graphApiService: null);

        var args = new[]
        {
            "register-external-mcp-server",
            "--server-name", "ext_Test",
            "--server-url", "https://example.com/mcp",
            "--connectivity", connectivityArg,
        };

        // Act
        var exitCode = await command.InvokeAsync(args);

        // Assert — exit code surfaces failure as the user would observe it
        exitCode.Should().Be(1);

        // Assert — error log names the option and both accepted values
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null
                && state.ToString()!.Contains("--connectivity")
                && state.ToString()!.Contains("public")
                && state.ToString()!.Contains("private")
                && state.ToString()!.Contains($"Got: '{connectivityArg.Trim()}'")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Assert — validation short-circuits before any downstream tooling call
        await toolingService.DidNotReceive().AddMcpServerAsync(
            Arg.Any<Microsoft.Agents.A365.DevTools.Cli.Models.AddMcpServerRequest>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await toolingService.DidNotReceive().LogRegisterUsageAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("public")]
    [InlineData("PUBLIC")]
    [InlineData("Public ")]
    [InlineData(" private")]
    public async Task RegisterExternalMcpServer_WithValidConnectivity_PassesConnectivityGuardAndReachesAuthTypeValidation(string connectivityArg)
    {
        // Arrange — a deliberately invalid --auth-type stops the run at the guard immediately
        // after the connectivity guard, so the run terminates without prompting and reaching
        // that guard proves connectivity was accepted.
        var logger = Substitute.For<ILogger>();
        var toolingService = Substitute.For<IAgent365ToolingService>();
        var command = DevelopMcpCommand.CreateCommand(logger, toolingService, graphApiService: null);

        var args = new[]
        {
            "register-external-mcp-server",
            "--server-name", "ext_Test",
            "--server-url", "https://example.com/mcp",
            "--connectivity", connectivityArg,
            "--auth-type", "NotAnAuthType",
        };

        // Act
        var exitCode = await command.InvokeAsync(args);

        // Assert
        exitCode.Should().Be(1);

        // Assert — surrounding whitespace and casing are tolerated, so the connectivity guard
        // never fires
        logger.DidNotReceive().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null
                && state.ToString()!.Contains("--connectivity")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Assert — execution reached the next guard, which is what stopped the run
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null
                && state.ToString()!.Contains("Invalid auth type")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ─────────── Connectivity as it is resolved onto the platform request ───────────

    /// <summary>
    /// Everything the command would otherwise prompt for is supplied through an input file, so
    /// ResolveInputsAsync runs to completion without touching the console.
    /// </summary>
    private static string WriteInputFile(string? connectivity)
    {
        var path = Path.Combine(Path.GetTempPath(), $"a365-connectivity-{Guid.NewGuid():N}.json");
        var connectivityLine = connectivity is null
            ? string.Empty
            : $"\"connectivity\": {System.Text.Json.JsonSerializer.Serialize(connectivity)},";
        File.WriteAllText(path, $$"""
            {
              "serverName": "ext_Test",
              "serverUrl": "https://example.com/mcp",
              "authType": "NoAuth",
              "publisherName": "Contoso",
              "description": "Test server",
              {{connectivityLine}}
              "tools": [ { "name": "search", "description": "Searches things" } ]
            }
            """);
        return path;
    }

    private static RawRegisterArgs FileBackedArgs(string? connectivity, string inputFile) =>
        new(
            ServerName: null,
            ServerUrl: null,
            AuthType: null,
            IdpAuthUrl: null,
            IdpTokenUrl: null,
            IdpScopes: null,
            IdpClientId: null,
            IdpClientSecret: null,
            ApiKeyLocation: null,
            ApiKeyName: null,
            ToolsInput: null,
            InputFile: inputFile,
            RemoteScopes: null,
            TenantId: null,
            ServiceTreeId: null,
            SecretLifetimeMonths: null,
            PublisherName: null,
            Description: null,
            Connectivity: connectivity,
            DryRun: true);

    private static RegisterCommandExecutor CreateExecutor(ILogger logger) =>
        new(logger, Substitute.For<IAgent365ToolingService>(), graphApiService: null);

    private static async Task<RegisterCommandExecutor.ResolvedInput?> ResolveAsync(
        ILogger logger, string? cliConnectivity, string? fileConnectivity)
    {
        var inputFile = WriteInputFile(fileConnectivity);
        try
        {
            return await CreateExecutor(logger).ResolveInputsAsync(FileBackedArgs(cliConnectivity, inputFile));
        }
        finally
        {
            File.Delete(inputFile);
        }
    }

    [Theory]
    [InlineData("public", "public")]
    [InlineData("PUBLIC", "public")]
    [InlineData("Public ", "public")]
    [InlineData(" private", "private")]
    [InlineData("PrIvAtE", "private")]
    public async Task ResolveInputsAsync_NormalisesAnAcceptedConnectivityToLowercase(string supplied, string expected)
    {
        var resolved = await ResolveAsync(Substitute.For<ILogger>(), supplied, fileConnectivity: null);

        resolved.Should().NotBeNull();
        resolved!.Connectivity.Should().Be(expected);
    }

    [Fact]
    public async Task ResolveInputsAsync_WhenConnectivityOmittedEverywhere_LeavesItUnsetSoThePlatformDefaultApplies()
    {
        var resolved = await ResolveAsync(Substitute.For<ILogger>(), cliConnectivity: null, fileConnectivity: null);

        resolved.Should().NotBeNull();
        resolved!.Connectivity.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveInputsAsync_WhenConnectivitySuppliedButBlank_Rejects(string supplied)
    {
        var logger = Substitute.For<ILogger>();

        var resolved = await ResolveAsync(logger, supplied, fileConnectivity: "public");

        resolved.Should().BeNull(because: "a blank value is a mistake, not a request for the default");
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null && state.ToString()!.Contains("--connectivity")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ResolveInputsAsync_WhenConnectivityOmittedOnTheCommandLine_TakesTheInputFileValue()
    {
        var resolved = await ResolveAsync(
            Substitute.For<ILogger>(), cliConnectivity: null, fileConnectivity: "PUBLIC");

        resolved.Should().NotBeNull();
        resolved!.Connectivity.Should().Be("public", because: "the file value is normalised by the same guard");
    }

    [Fact]
    public async Task ResolveInputsAsync_WhenConnectivityGivenOnBoth_PrefersTheCommandLine()
    {
        var resolved = await ResolveAsync(
            Substitute.For<ILogger>(), cliConnectivity: "private", fileConnectivity: "public");

        resolved.Should().NotBeNull();
        resolved!.Connectivity.Should().Be("private");
    }

    [Fact]
    public async Task ResolveInputsAsync_WhenTheInputFileConnectivityIsInvalid_NamesTheInputFileNotTheOption()
    {
        var logger = Substitute.For<ILogger>();

        var resolved = await ResolveAsync(logger, cliConnectivity: null, fileConnectivity: "publik");

        resolved.Should().BeNull();
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null
                && state.ToString()!.Contains("connectivity in the input file")
                && !state.ToString()!.Contains("--connectivity")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ResolveInputsAsync_WhenTheCommandLineConnectivityIsInvalid_NamesTheOption()
    {
        var logger = Substitute.For<ILogger>();

        var resolved = await ResolveAsync(logger, cliConnectivity: "publik", fileConnectivity: null);

        resolved.Should().BeNull();
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null && state.ToString()!.Contains("--connectivity")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ───────────────────── Serialized request property name ────────────────────

    [Theory]
    [InlineData("public")]
    [InlineData("private")]
    [InlineData(null)]
    public void BuildRequest_SerialisesConnectivityUnderTheNameThePlatformReads(string? connectivity)
    {
        var input = new RegisterCommandExecutor.ResolvedInput
        {
            ServerName = "ext_Test",
            ServerUrl = "https://example.com/mcp",
            AuthType = "NoAuth",
            IsEntra = false,
            IsExternalIdp = false,
            IsNoAuth = true,
            IsApiKey = false,
            ToolList = [],
            ToolDescriptions = [],
            PublisherName = "Contoso",
            Description = "Test server",
            DryRun = false,
            Connectivity = connectivity,
        };

        var apps = new RegisterCommandExecutor.EntraAppSet(
            "a365-client-id", "a365-secret", "a365-object-id", "a365-name",
            null, null, null, "remote-proxy-name", null, null, "public-clients-name");

        var request = RegisterCommandExecutor.BuildRequest(input, apps);

        request.Connectivity.Should().Be(connectivity);

        // The platform binds on the JSON name, so a rename here silently drops the value: the
        // request still succeeds and the connector is created private.
        var json = JsonSerializer.Serialize(request);
        using var doc = JsonDocument.Parse(json);

        if (connectivity == null)
        {
            // Null may be written or omitted depending on the serializer options in force; either
            // way the platform sees no value and its default applies.
            if (doc.RootElement.TryGetProperty("connectivity", out var written))
            {
                written.ValueKind.Should().Be(JsonValueKind.Null);
            }
        }
        else
        {
            doc.RootElement.GetProperty("connectivity").GetString().Should().Be(connectivity);
        }
    }
}