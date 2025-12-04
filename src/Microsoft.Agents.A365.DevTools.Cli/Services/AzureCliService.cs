// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Models;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

public class AzureCliService : IAzureCliService
{
    private readonly CommandExecutor _commandExecutor;
    private readonly ILogger<AzureCliService> _logger;

    public AzureCliService(CommandExecutor commandExecutor, ILogger<AzureCliService> logger)
    {
        _commandExecutor = commandExecutor;
        _logger = logger;
    }

    public async Task<bool> IsLoggedInAsync()
    {
        try
        {
            var result = await _commandExecutor.ExecuteAsync(
                "az", 
                "account show",
                suppressErrorLogging: true
            );
            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error checking Azure CLI login status: {Error}", ex.Message);
            return false;
        }
    }

    public async Task<AzureAccountInfo?> GetCurrentAccountAsync()
    {
        try
        {
            var result = await _commandExecutor.ExecuteAsync(
                "az", 
                "account show --output json"
            );

            if (!result.Success)
            {
                _logger.LogError("Failed to get Azure account information. Ensure you are logged in with 'az login'");
                return null;
            }

            // Clean the output to remove any control characters or non-JSON content
            var cleanedOutput = CleanJsonOutput(result.StandardOutput);
            
            if (string.IsNullOrWhiteSpace(cleanedOutput))
            {
                _logger.LogError("Azure CLI returned empty output");
                return null;
            }

            var accountJson = JsonSerializer.Deserialize<JsonElement>(cleanedOutput);
            
            return new AzureAccountInfo
            {
                Id = accountJson.GetProperty("id").GetString() ?? string.Empty,
                Name = accountJson.GetProperty("name").GetString() ?? string.Empty,
                TenantId = accountJson.GetProperty("tenantId").GetString() ?? string.Empty,
                User = new AzureUser
                {
                    Name = accountJson.GetProperty("user").GetProperty("name").GetString() ?? string.Empty,
                    Type = accountJson.GetProperty("user").GetProperty("type").GetString() ?? string.Empty
                },
                State = accountJson.GetProperty("state").GetString() ?? string.Empty,
                IsDefault = accountJson.GetProperty("isDefault").GetBoolean()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Azure account information");
            return null;
        }
    }

    public async Task<List<AzureResourceGroup>> ListResourceGroupsAsync()
    {
        try
        {
            var result = await _commandExecutor.ExecuteAsync(
                "az", 
                "group list --output json"
            );

            if (!result.Success)
            {
                _logger.LogError("Failed to list resource groups");
                return new List<AzureResourceGroup>();
            }

            var cleanedOutput = CleanJsonOutput(result.StandardOutput);
            if (string.IsNullOrWhiteSpace(cleanedOutput))
            {
                return new List<AzureResourceGroup>();
            }

            var resourceGroupsJson = JsonSerializer.Deserialize<JsonElement[]>(cleanedOutput);
            
            return resourceGroupsJson?.Select(rg => new AzureResourceGroup
            {
                Name = rg.GetProperty("name").GetString() ?? string.Empty,
                Location = rg.GetProperty("location").GetString() ?? string.Empty,
                Id = rg.GetProperty("id").GetString() ?? string.Empty
            }).OrderBy(rg => rg.Name).ToList() ?? new List<AzureResourceGroup>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing resource groups");
            return new List<AzureResourceGroup>();
        }
    }

    public async Task<List<AzureAppServicePlan>> ListAppServicePlansAsync()
    {
        try
        {
            var result = await _commandExecutor.ExecuteAsync(
                "az", 
                "appservice plan list --output json"
            );

            if (!result.Success)
            {
                _logger.LogError("Failed to list app service plans");
                return new List<AzureAppServicePlan>();
            }

            var cleanedOutput = CleanJsonOutput(result.StandardOutput);
            if (string.IsNullOrWhiteSpace(cleanedOutput))
            {
                return new List<AzureAppServicePlan>();
            }

            var plansJson = JsonSerializer.Deserialize<JsonElement[]>(cleanedOutput);
            
            return plansJson?.Select(plan => new AzureAppServicePlan
            {
                Name = plan.GetProperty("name").GetString() ?? string.Empty,
                ResourceGroup = plan.GetProperty("resourceGroup").GetString() ?? string.Empty,
                Location = plan.GetProperty("location").GetString() ?? string.Empty,
                Sku = plan.GetProperty("sku").GetProperty("name").GetString() ?? string.Empty,
                Id = plan.GetProperty("id").GetString() ?? string.Empty
            }).OrderBy(plan => plan.Name).ToList() ?? new List<AzureAppServicePlan>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing app service plans");
            return new List<AzureAppServicePlan>();
        }
    }

    public async Task<List<AzureLocation>> ListLocationsAsync()
    {
        try
        {
            var result = await _commandExecutor.ExecuteAsync(
                "az", 
                "account list-locations --output json"
            );

            if (!result.Success)
            {
                _logger.LogError("Failed to list Azure locations");
                return new List<AzureLocation>();
            }

            var cleanedOutput = CleanJsonOutput(result.StandardOutput);
            if (string.IsNullOrWhiteSpace(cleanedOutput))
            {
                return new List<AzureLocation>();
            }

            var locationsJson = JsonSerializer.Deserialize<JsonElement[]>(cleanedOutput);
            
            return locationsJson?.Select(loc => new AzureLocation
            {
                Name = loc.GetProperty("name").GetString() ?? string.Empty,
                DisplayName = loc.GetProperty("displayName").GetString() ?? string.Empty,
                RegionalDisplayName = loc.TryGetProperty("regionalDisplayName", out var regional) 
                    ? regional.GetString() ?? string.Empty 
                    : string.Empty
            }).OrderBy(loc => loc.DisplayName).ToList() ?? new List<AzureLocation>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing Azure locations");
            return new List<AzureLocation>();
        }
    }

    /// <summary>
    /// Cleans JSON output from Azure CLI by removing control characters and non-JSON content
    /// </summary>
    private string CleanJsonOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        // Remove control characters (0x00-0x1F except \r, \n, \t)
        var cleaned = new System.Text.StringBuilder(output.Length);
        foreach (char c in output)
        {
            if (c >= 32 || c == '\n' || c == '\r' || c == '\t')
            {
                cleaned.Append(c);
            }
        }

        var result = cleaned.ToString().Trim();
        
        // Find the first { or [ to locate JSON start
        int jsonStart = result.IndexOfAny(new[] { '{', '[' });
        if (jsonStart > 0)
        {
            result = result.Substring(jsonStart);
        }

        return result;
    }
}
