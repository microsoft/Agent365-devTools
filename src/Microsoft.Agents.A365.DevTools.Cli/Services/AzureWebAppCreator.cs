// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services
{
    public class AzureWebAppCreator
    {
        private readonly ILogger<AzureWebAppCreator> _logger;

        public AzureWebAppCreator(ILogger<AzureWebAppCreator> logger)
        {
            _logger = logger;
        }

        public async Task<bool> CreateWebAppAsync(
            string subscriptionId,
            string resourceGroupName,
            string appServicePlanName,
            string webAppName,
            string location,
            string? tenantId = null)
        {
            try
            {
                ArmClient armClient;
                // Use DefaultAzureCredential which tries credentials in this order:
                // 1. Environment variables
                // 2. Managed Identity
                // 3. Visual Studio / VS Code
                // 4. Azure CLI (az login)
                // 5. Interactive Browser (if needed)
                var credentialOptions = new DefaultAzureCredentialOptions
                {
                    ExcludeInteractiveBrowserCredential = false
                };

                if (!string.IsNullOrWhiteSpace(tenantId))
                {
                    credentialOptions.TenantId = tenantId;
                }

                armClient = new ArmClient(new DefaultAzureCredential(credentialOptions), subscriptionId);

                var subscription = armClient.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}"));
                var resourceGroup = await subscription.GetResourceGroups().GetAsync(resourceGroupName);

                // Get the App Service plan
                var appServicePlan = await resourceGroup.Value.GetAppServicePlans().GetAsync(appServicePlanName);

                // Prepare the web app data
                var webAppData = new WebSiteData(location)
                {
                    AppServicePlanId = appServicePlan.Value.Id,
                    SiteConfig = new SiteConfigProperties
                    {
                        LinuxFxVersion = "DOTNETCORE|8.0"
                    },
                    Kind = "app,linux"
                };

                // Create the web app
                var webAppLro = await resourceGroup.Value.GetWebSites().CreateOrUpdateAsync(
                    Azure.WaitUntil.Completed,
                    webAppName,
                    webAppData);

                _logger.LogInformation("Web app '{WebAppName}' created successfully.", webAppName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create web app '{WebAppName}'.", webAppName);
                return false;
            }
        }
    }
}
