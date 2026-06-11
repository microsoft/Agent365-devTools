# Documentation for Agent 365 CLI commands

Reference documentation about using commands is published at [Agent 365 CLI reference](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/).

There is reference documentation for each command.

| Command | Description |
| --- | --- |
| [cleanup](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/cleanup) | Cleans up ALL resources (blueprint, instance, and Azure). Use subcommands for granular cleanup. |
| [cleanup blueprint](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/cleanup#cleanup-blueprint) | Remove Entra ID blueprint application and service principal. |
| [cleanup azure](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/cleanup#cleanup-azure) | Remove Azure resources (App Service, App Service Plan). |
| [cleanup instance](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/cleanup#cleanup-instance) | Remove agent instance identity and user from Entra ID. |
| [create-instance](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/create-instance) | Create an agent instance and its associated identity resources. |
| [deploy](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/deploy) | Deploy Agent 365 application binaries to the configured Azure App Service and update Agent 365 Tool permissions |
| [deploy app](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/deploy#deploy-app) | Deploys your agent code to the Azure Web App created during setup. |
| [deploy mcp](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/deploy#deploy-mcp) | Updates MCP server permissions on your agent blueprint. |
| [develop](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/develop) | Manage MCP tool servers for agent development. |
| [develop list-available](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/develop#develop-list-available) | List all MCP servers available in the catalog (what you can install). |
| [develop list-configured](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/develop#develop-list-configured) | List currently configured MCP servers from your local ToolingManifest.json. |
| [develop add-mcp-servers](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/develop#develop-add-mcp-servers) | Add MCP Servers to the current agent configuration. |
| [develop remove-mcp-servers](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/develop#develop-remove-mcp-servers) | Remove MCP Servers from the current agent configuration. |
| [develop add-permissions](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/develop#develop-add-permissions) | Add MCP server API permissions to Microsoft Entra applications for development scenarios where you need to configure custom applications to access MCP servers. |
| [develop get-token](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/develop#develop-get-token) | Retrieve bearer tokens for testing MCP servers during development using interactive browser authentication. |
| [develop start-mock-tooling-server](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/develop#develop-start-mock-tooling-server) | Start a mock tooling server for testing and development purposes. |
| [develop-mcp](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/develop-mcp) | Manage MCP servers in Dataverse environments. |
| [develop-mcp list-environments](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/develop-mcp#develop-mcp-list-environments) | List all Dataverse environments available for MCP server management. |
| [develop-mcp list-servers](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/develop-mcp#develop-mcp-list-servers) | List MCP servers in a specific Dataverse environment. |
| [develop-mcp publish](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/develop-mcp#develop-mcp-publish) | Publish an MCP server to a Dataverse environment. |
| [develop-mcp unpublish](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/develop-mcp#develop-mcp-unpublish) | Unpublish an MCP server from a Dataverse environment. |
| [publish](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/publish) | Update manifest.json ID values and publish the package. Configure federated identity and app role assignments. |
| [query-entra](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/query-entra) | Query Microsoft Entra ID for agent information including scopes, permissions, and consent status. |
| [query-entra blueprint-scopes](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/query-entra#query-entra-blueprint-scopes) | List configured scopes and consent status for the agent blueprint. |
| [query-entra instance-scopes](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/query-entra#query-entra-instance-scopes) | List configured scopes and consent status for the agent instance. |
| [setup](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/setup) | Set up your Agent 365 environment with granular control over each step. |
| [setup requirements](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/setup#setup-requirements) | Validate prerequisites for Agent 365 setup. |
| [setup infrastructure](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/setup#setup-infrastructure) | Create Azure infrastructure. |
| [setup blueprint](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/setup#setup-blueprint) | Create agent blueprint (Entra ID application registration). |
| [setup permissions](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/setup#setup-permissions) | Configure OAuth2 permission grants and inheritable permissions. |
| [setup permissions mcp](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/setup#setup-permissions-mcp) | Configure MCP server OAuth2 grants and inheritable permissions. |
| [setup permissions bot](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/setup#setup-permissions-bot) | Configure Messaging Bot API OAuth2 grants and inheritable permissions. |
| [setup permissions copilotstudio](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/setup#setup-permissions-copilotstudio) | Configures OAuth2 permission grants and inheritable permissions for the agent blueprint to invoke Copilot Studio copilots via the Power Platform API. |
| [setup permissions custom](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/setup#setup-permissions-custom) | Applies custom API permissions to your agent blueprint that go beyond the standard permissions required for agent operation. |
| [setup all](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/setup#setup-all) | Perform all setup steps to set up your Agent 365 environment |

## Documentation policy

As new Agent CLI commands are developed you may find documentation about these commands in this folder. These temporary artifacts support the workflow for developers creating features. After the feature ships, docs must be created or updated on learn.microsoft.com and the documentation in this folder should be deleted.

The only documentation that will persist in this folder is to support developers creating and maintaining the code for commands in this repo, for example [Agent365-devTools Architecture](../design.md)