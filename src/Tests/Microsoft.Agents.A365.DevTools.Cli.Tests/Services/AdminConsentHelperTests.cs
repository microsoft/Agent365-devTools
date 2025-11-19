// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services
{
    public class AdminConsentHelperTests
    {
        [Fact]
        public async Task PollAdminConsentAsync_ReturnsTrue_WhenGrantExists()
        {
            var executor = Substitute.For<CommandExecutor>(Substitute.For<Microsoft.Extensions.Logging.ILogger<CommandExecutor>>());
            var logger = Substitute.For<ILogger>();

            // Mock service principal lookup
            var spJson = JsonDocument.Parse("{\"value\":[{\"id\":\"sp-123\"}]}", new JsonDocumentOptions()).RootElement.GetRawText();
            executor.ExecuteAsync("az", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = spJson }));

            // On the grants call, return a grant
            var grantsJson = JsonDocument.Parse("{\"value\":[{\"id\":\"grant-1\"}]}", new JsonDocumentOptions()).RootElement.GetRawText();
            executor.ExecuteAsync("az", Arg.Is<string>(s => s.Contains("oauth2PermissionGrants")), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = grantsJson }));

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await AdminConsentHelper.PollAdminConsentAsync(executor, logger, "appId-1", "Test", 10, 1, cts.Token);

            result.Should().BeTrue();
        }

        [Fact]
        public async Task PollAdminConsentAsync_ReturnsFalse_WhenNoGrant()
        {
            var executor = Substitute.For<CommandExecutor>(Substitute.For<Microsoft.Extensions.Logging.ILogger<CommandExecutor>>());
            var logger = Substitute.For<ILogger>();

            // service principal not found
            executor.ExecuteAsync("az", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new Microsoft.Agents.A365.DevTools.Cli.Services.CommandResult { ExitCode = 0, StandardOutput = "{\"value\":[]}" }));

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = await AdminConsentHelper.PollAdminConsentAsync(executor, logger, "appId-1", "Test", 3, 1, cts.Token);

            result.Should().BeFalse();
        }
    }
}

