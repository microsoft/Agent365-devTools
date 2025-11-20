// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Interface for platform-specific build operations
/// </summary>
public interface IPlatformBuilder
{
    /// <summary>
    /// Validate that required tools are installed
    /// </summary>
    Task<bool> ValidateEnvironmentAsync();

    /// <summary>
    /// Clean previous build artifacts
    /// </summary>
    Task CleanAsync(string projectDir);

    /// <summary>
    /// Build the application and return the publish output path
    /// </summary>
    Task<string> BuildAsync(string projectDir, string outputPath, bool verbose);

    /// <summary>
    /// Create Oryx manifest for the platform
    /// </summary>
    Task<OryxManifest> CreateManifestAsync(string projectDir, string publishPath);

    /// <summary>
    /// Convert .env file to Azure App Settings for the deployed application
    /// </summary>
    Task<bool> ConvertEnvToAzureAppSettingsAsync(string projectDir, string resourceGroup, string webAppName, bool verbose);
}
