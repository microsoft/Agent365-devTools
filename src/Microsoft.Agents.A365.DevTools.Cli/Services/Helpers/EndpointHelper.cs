// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

public static class EndpointHelper
{
    public static string GetEndpointName(string name)
    {
        return name.Length > 42
            ? name.Substring(0, 42)
            : name;
    }

    /// <summary>
    /// Get create endpoint URL based on environment
    /// </summary>
    public static string GetCreateEndpointUrl(string environment)
    {
        // Check for custom endpoint in environment variable first
        var customEndpoint = Environment.GetEnvironmentVariable($"A365_CREATE_ENDPOINT_{environment?.ToUpper()}");
        if (!string.IsNullOrEmpty(customEndpoint))
            return customEndpoint;

        // Default to production endpoint
        return environment?.ToLower() switch
        {
            "prod" => ConfigConstants.ProductionCreateEndpointUrl,
            _ => ConfigConstants.ProductionCreateEndpointUrl
        };
    }

    /// <summary>
    /// Get delete endpoint URL based on environment
    /// </summary>
    public static string GetDeleteEndpointUrl(string environment)
    {
        // Check for custom endpoint in environment variable first
        var customEndpoint = Environment.GetEnvironmentVariable($"A365_DELETE_ENDPOINT_{environment?.ToUpper()}");
        if (!string.IsNullOrEmpty(customEndpoint))
            return customEndpoint;

        // Default to production endpoint
        return environment?.ToLower() switch
        {
            "prod" => ConfigConstants.ProductionDeleteEndpointUrl,
            _ => ConfigConstants.ProductionDeleteEndpointUrl
        };
    }

    /// <summary>
    /// Get deployment environment based on environment
    /// </summary>
    public static string GetDeploymentEnvironment(string environment)
    {
        // Check for custom deployment environment in environment variable first
        var customDeploymentEnvironment = Environment.GetEnvironmentVariable($"A365_DEPLOYMENT_ENVIRONMENT_{environment?.ToUpper()}");
        if (!string.IsNullOrEmpty(customDeploymentEnvironment))
            return customDeploymentEnvironment;

        // Default to production deployment environment
        return environment?.ToLower() switch
        {
            "prod" => ConfigConstants.ProductionDeploymentEnvironment,
            _ => ConfigConstants.ProductionDeploymentEnvironment
        };
    }

    /// <summary>
    /// Get cluster category based on environment
    /// </summary>
    public static string GetClusterCategory(string environment)
    {
        // Check for custom cluster category in environment variable first
        var customClusterCategory = Environment.GetEnvironmentVariable($"A365_CLUSTER_CATEGORY_{environment?.ToUpper()}");
        if (!string.IsNullOrEmpty(customClusterCategory))
            return customClusterCategory;

        // Default to production cluster category
        return environment?.ToLower() switch
        {
            "prod" => ConfigConstants.ProductionClusterCategory,
            _ => ConfigConstants.ProductionClusterCategory
        };
    }
}
