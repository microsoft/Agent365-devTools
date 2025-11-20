// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Helpers;

public class BuilderHelperTests
{
    private readonly ILogger<BuilderHelper> _logger;
    private readonly CommandExecutor _mockExecutor;
    private readonly BuilderHelper _builderHelper;

    public BuilderHelperTests()
    {
        _logger = Substitute.For<ILogger<BuilderHelper>>();

        var executorLogger = Substitute.For<ILogger<CommandExecutor>>();
        _mockExecutor = Substitute.ForPartsOf<CommandExecutor>(executorLogger);

        _builderHelper = new BuilderHelper(
            _logger,
            _mockExecutor);
    }

    [Fact]
    public async Task ConvertEnvToAzureAppSettingsIfExistsAsync_EnvFileDoesNotExist()
    {
        // Arrange
        var projectDir = "C:\\NonExistentPath\\Path";
        var resourceGroup = "test-rg";
        var webAppName = "TestWebApp";
        var expected = true;

        // Act
        var result = await _builderHelper.ConvertEnvToAzureAppSettingsIfExistsAsync(projectDir, resourceGroup, webAppName, false);

        // Assert
        Assert.Equal(expected, result);
    }
}