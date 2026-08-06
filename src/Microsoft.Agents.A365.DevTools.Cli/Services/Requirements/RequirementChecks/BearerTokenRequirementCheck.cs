// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;

/// <summary>
/// Validates that a bearer token is configured in the agent's launch settings.
/// Checks launchSettings.json (for .NET) or .env files (for Node.js/Python)
/// for the BEARER_TOKEN environment variable required for MCP tool authentication.
/// </summary>
public class BearerTokenRequirementCheck : RequirementCheck
{
    private readonly PlatformDetector _platformDetector;

    public BearerTokenRequirementCheck(PlatformDetector platformDetector)
    {
        _platformDetector = platformDetector;
    }

    /// <inheritdoc />
    public override string Name => "Bearer Token";

    /// <inheritdoc />
    public override string Description => "Validates that a bearer token is configured for MCP tool authentication";

    /// <inheritdoc />
    public override string Category => "Configuration";

    /// <inheritdoc />
    public override async Task<RequirementCheckResult> CheckAsync(
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteCheckWithLoggingAsync(config, logger, CheckImplementationAsync, cancellationToken);
    }

    private Task<RequirementCheckResult> CheckImplementationAsync(
        Agent365Config config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var projectPath = ResolveProjectPath(config);

        if (!Directory.Exists(projectPath))
        {
            return Task.FromResult(RequirementCheckResult.Failure(
                $"Project path does not exist: {projectPath}",
                "Ensure the project directory exists, or set deploymentProjectPath in a365.config.json"));
        }

        var platform = _platformDetector.Detect(projectPath);

        var tokenEnvVar = AuthenticationConstants.BearerTokenEnvironmentVariable;

        return platform switch
        {
            ProjectPlatform.DotNet => Task.FromResult(CheckLaunchSettings(projectPath, tokenEnvVar)),
            ProjectPlatform.NodeJs => Task.FromResult(CheckEnvFile(projectPath, tokenEnvVar)),
            ProjectPlatform.Python => Task.FromResult(CheckEnvFile(projectPath, tokenEnvVar)),
            _ => Task.FromResult(
                CheckLaunchSettings(projectPath, tokenEnvVar) is { Passed: true } launchResult
                    ? launchResult
                    : CheckEnvFile(projectPath, tokenEnvVar))
        };
    }

    /// <summary>
    /// Checks Properties/launchSettings.json for the bearer token in environmentVariables.
    /// </summary>
    internal static RequirementCheckResult CheckLaunchSettings(string projectPath, string envVarName)
    {
        var launchSettingsPath = Path.Combine(projectPath, "Properties", "launchSettings.json");
        if (!File.Exists(launchSettingsPath))
        {
            return RequirementCheckResult.Failure(
                "No launchSettings.json found",
                $"Create Properties/launchSettings.json with {envVarName} in environmentVariables. " +
                $"Run 'a365 develop get-token' to retrieve a token.");
        }

        try
        {
            var json = File.ReadAllText(launchSettingsPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("profiles", out var profiles))
            {
                return RequirementCheckResult.Failure(
                    "launchSettings.json has no profiles section",
                    $"Add a profile with {envVarName} in environmentVariables.");
            }

            foreach (var profile in profiles.EnumerateObject())
            {
                if (profile.Value.TryGetProperty("environmentVariables", out var envVars) &&
                    envVars.TryGetProperty(envVarName, out var tokenValue))
                {
                    var token = tokenValue.GetString();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        return RequirementCheckResult.Success(
                            details: $"Found {envVarName} in launchSettings.json profile '{profile.Name}'");
                    }
                }
            }
        }
        catch (JsonException)
        {
            return RequirementCheckResult.Failure(
                "launchSettings.json is not valid JSON",
                "Fix the JSON syntax in Properties/launchSettings.json.");
        }

        return RequirementCheckResult.Failure(
            $"{envVarName} not found in launchSettings.json",
            $"Add {envVarName} to environmentVariables in a launchSettings.json profile. " +
            $"Run 'a365 develop get-token' to retrieve a token.");
    }

    /// <summary>
    /// Checks .env file for the bearer token variable.
    /// </summary>
    internal static RequirementCheckResult CheckEnvFile(string projectPath, string envVarName)
    {
        var envPath = Path.Combine(projectPath, ".env");
        if (!File.Exists(envPath))
        {
            return RequirementCheckResult.Failure(
                "No .env file found",
                $"Create a .env file with {envVarName}=<token>. " +
                $"Run 'a365 develop get-token' to retrieve a token.");
        }

        try
        {
            foreach (var line in File.ReadLines(envPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith($"{envVarName}=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed.Substring(envVarName.Length + 1).Trim().Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return RequirementCheckResult.Success(
                            details: $"Found {envVarName} in .env file");
                    }
                }
            }
        }
        catch (IOException)
        {
            return RequirementCheckResult.Failure(
                "Could not read .env file",
                "Ensure the .env file is accessible and not locked by another process.");
        }

        return RequirementCheckResult.Failure(
            $"{envVarName} not found in .env file",
            $"Add {envVarName}=<token> to your .env file. " +
            $"Run 'a365 develop get-token' to retrieve a token.");
    }

    private static string ResolveProjectPath(Agent365Config config)
    {
        return !string.IsNullOrWhiteSpace(config.DeploymentProjectPath)
            ? config.DeploymentProjectPath
            : Directory.GetCurrentDirectory();
    }
}
