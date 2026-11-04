// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services.Evaluate;

/// <summary>
/// Disables parallelization for <see cref="AzureOpenAiLauncherAvailabilityTests"/>, which
/// mutate the process-wide endpoint and deployment environment variables that
/// <c>IsAvailableAsync</c> reads.
/// </summary>
// This serializes tests WITHIN the collection only. No other test class in the suite reads
// A365_EVAL_AZURE_OPENAI_ENDPOINT or A365_EVAL_AZURE_OPENAI_DEPLOYMENT, so cross-collection
// races are not a concern; revisit if another suite begins reading those variables.
[CollectionDefinition("AzureOpenAiAvailability", DisableParallelization = true)]
public class AzureOpenAiAvailabilityCollection { }

/// <summary>
/// Tests <see cref="AzureOpenAiLauncher.IsAvailableAsync"/>, which gates whether the engine
/// can run by inspecting the endpoint and deployment environment variables. These mutate
/// process-wide state, so the suite runs in its own non-parallel collection and each test
/// saves and restores both variables in a finally block.
/// </summary>
[Collection("AzureOpenAiAvailability")]
public class AzureOpenAiLauncherAvailabilityTests
{
    private const string EndpointVar = EvalModelConstants.AzureOpenAiEndpointEnvVar;
    private const string DeploymentVar = EvalModelConstants.AzureOpenAiDeploymentEnvVar;

    private static AzureOpenAiLauncher CreateLauncher() =>
        new(NullLogger<AzureOpenAiLauncher>.Instance);

    /// <summary>
    /// Sets both environment variables, runs <paramref name="body"/>, then restores the
    /// original values regardless of outcome so no test leaks state into another.
    /// </summary>
    private static async Task WithEnvAsync(string? endpoint, string? deployment, Func<Task> body)
    {
        var originalEndpoint = Environment.GetEnvironmentVariable(EndpointVar);
        var originalDeployment = Environment.GetEnvironmentVariable(DeploymentVar);
        try
        {
            Environment.SetEnvironmentVariable(EndpointVar, endpoint);
            Environment.SetEnvironmentVariable(DeploymentVar, deployment);
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EndpointVar, originalEndpoint);
            Environment.SetEnvironmentVariable(DeploymentVar, originalDeployment);
        }
    }

    [Fact]
    public async Task IsAvailableAsync_BothUnset_ReturnsFalse()
    {
        await WithEnvAsync(endpoint: null, deployment: null, async () =>
        {
            var available = await CreateLauncher().IsAvailableAsync();

            available.Should().BeFalse(
                because: "with neither variable set the engine has nothing to connect to and must report unavailable");
        });
    }

    [Fact]
    public async Task IsAvailableAsync_OnlyEndpointSet_ReturnsFalse()
    {
        await WithEnvAsync(endpoint: "https://x.openai.azure.com/openai/v1", deployment: null, async () =>
        {
            var available = await CreateLauncher().IsAvailableAsync();

            available.Should().BeFalse(
                because: "a deployment name is also required; an endpoint alone is not enough to run the judge");
        });
    }

    [Fact]
    public async Task IsAvailableAsync_OnlyDeploymentSet_ReturnsFalse()
    {
        await WithEnvAsync(endpoint: null, deployment: "gpt-4.1", async () =>
        {
            var available = await CreateLauncher().IsAvailableAsync();

            available.Should().BeFalse(
                because: "an endpoint is also required; a deployment name alone is not enough to run the judge");
        });
    }

    [Fact]
    public async Task IsAvailableAsync_EndpointNotAbsoluteUrl_ReturnsFalse()
    {
        await WithEnvAsync(endpoint: "not-a-url", deployment: "gpt-4.1", async () =>
        {
            var available = await CreateLauncher().IsAvailableAsync();

            available.Should().BeFalse(
                because: "a non-absolute endpoint cannot be used to build a client, so the engine must report unavailable up front");
        });
    }

    [Fact]
    public async Task IsAvailableAsync_PlaintextHttpEndpoint_ReturnsFalse()
    {
        // A valid absolute URL, but http:// — the engine transmits tool names and descriptions to
        // this endpoint, so a plaintext scheme must be rejected rather than silently used.
        await WithEnvAsync(endpoint: "http://x.openai.azure.com/openai/v1", deployment: "gpt-4.1", async () =>
        {
            var available = await CreateLauncher().IsAvailableAsync();

            available.Should().BeFalse(
                because: "tool metadata is sent to the endpoint, so a non-HTTPS endpoint must be treated as unavailable to avoid plaintext transmission");
        });
    }

    [Fact]
    public async Task IsAvailableAsync_BothSetWithValidHttpsEndpoint_ReturnsTrue()
    {
        await WithEnvAsync(endpoint: "https://x.openai.azure.com/openai/v1", deployment: "gpt-4.1", async () =>
        {
            var available = await CreateLauncher().IsAvailableAsync();

            available.Should().BeTrue(
                because: "both variables are set and the endpoint is a valid absolute URL, so the engine is configured to run");
        });
    }

    [Fact]
    public async Task IsAvailableAsync_EndpointWithWrappingQuotes_ReturnsTrue()
    {
        // Copy-paste from a portal or `SET VAR="..."` can leave literal wrapping quotes.
        await WithEnvAsync(endpoint: "\"https://x.openai.azure.com/openai/v1\"", deployment: "gpt-4.1", async () =>
        {
            var available = await CreateLauncher().IsAvailableAsync();

            available.Should().BeTrue(
                because: "a single pair of wrapping quotes must be stripped so a quoted endpoint still resolves to a valid absolute URL");
        });
    }
}
