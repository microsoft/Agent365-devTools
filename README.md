# Microsoft Agent 365 DevTools CLI

[![NuGet](https://img.shields.io/badge/NuGet-v0.1.0-blue)](https://www.nuget.org/packages/Microsoft.Agents.A365.DevTools.Cli) [![Downloads](https://img.shields.io/nuget/dt/Microsoft.Agents.A365.DevTools.Cli?label=Downloads&color=green)](https://www.nuget.org/packages/Microsoft.Agents.A365.DevTools.Cli)
[![Build Status](https://img.shields.io/github/actions/workflow/status/microsoft/Agent365-devTools/.github/workflows/ci.yml?branch=main&label=Build&logo=github)](https://github.com/microsoft/Agent365-devTools/actions)
[![License](https://img.shields.io/badge/License-MIT-blue)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Contributors](https://img.shields.io/github/contributors/microsoft/Agent365-devTools?label=Contributors)](https://github.com/microsoft/Agent365-devTools/graphs/contributors)

> **Note:**  
> Use the information in this README to contribute to this open-source project. To learn about using this CLI in your projects, refer to the [Microsoft Agent 365 CLI documentation](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-cli).

The **Microsoft Agent 365 DevTools CLI** is a command-line interface tool designed to streamline the development, deployment, and management of Microsoft Agent 365 applications. This CLI provides comprehensive tooling for configuration management, Azure resource provisioning, MCP (Model Context Protocol) server integration, and agent deployment workflows.

## Features

The Microsoft Agent 365 DevTools CLI can be used through the developer journey of an Agent 365 developer. The CLI provides the following commands:

- **develop**: Manage MCP tool servers for agent development
- **develop-mcp**: Manage MCP servers in Dataverse environments
- **setup**: Set up your Agent 365 environment by creating Azure resources, configuring permissions, and registering your agent blueprint for deployment
- **create-instance**: Create and configure agent user identities with appropriate licenses and notification settings for your deployed agent
- **deploy**: Deploy Agent 365 application binaries to the configured Azure App Service and update Agent 365 Tool permissions
- **config**: Configure Azure subscription, resource settings, and deployment options for Agent 365 CLI commands
- **query-entra**: Query Microsoft Entra ID for agent information (scopes, permissions, consent status)
- **cleanup**: Clean up ALL resources (blueprint, instance, other Azure resources)
- **publish**: Update agent manifest and publish package; configure federated identity and app role assignments

## Current Project State

This project is currently in active development. The CLI is being actively developed and improved based on community feedback.

## Installation

### Prerequisites

Before using the Agent365 CLI, you must create a custom Entra ID app registration with specific Microsoft Graph API permissions:

1. **Custom Client App Registration**: Create an app in your Entra ID tenant
2. **Required Permissions**: Configure **delegated** permissions (NOT Application) as defined in `AuthenticationConstants.RequiredClientAppPermissions` in the codebase
3. **Admin Consent**: Grant admin consent for all permissions

⚠️ **Important**: Use **Delegated** permissions (you sign in, CLI acts on your behalf), NOT Application permissions (for background services).

📖 **Detailed Setup Guide**: [docs/guides/custom-client-app-registration.md](docs/guides/custom-client-app-registration.md)

> **Why is this required?** The CLI needs elevated permissions to create and manage Agent Identity Blueprints in your tenant. You maintain control over which permissions are granted, and the app stays within your tenant's security boundaries.

### Install the CLI

From NuGet (Production):

```powershell
dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli --prerelease
```

## Documentation

To know more about CLI and prerequisites:
- [Microsoft Agent 365 CLI Documentation](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-cli)

For usage and command reference:
- [CLI Command Reference](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/reference/cli)

## Support

For issues, questions, or feedback:

- **Issues**: Please file issues in the [GitHub Issues](https://github.com/microsoft/Agent365-devTools/issues) section
- **Documentation**: See the [Microsoft Agent 365 CLI Documentation](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-cli)
- **Security**: For security issues, please see [SECURITY.md](SECURITY.md)

## Contributing

This project welcomes contributions and suggestions. Most contributions require you to agree to a Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/). For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

## Useful Links

### How CLI is Used with Microsoft Agent 365 SDK

- [Microsoft Agent 365 Developer Documentation](https://learn.microsoft.com/en-us/microsoft-agent-365/developer)

### Additional Resources

- [.NET documentation](https://docs.microsoft.com/dotnet/)
- [Azure CLI documentation](https://docs.microsoft.com/cli/azure/)

## Trademarks

Microsoft, Windows, Microsoft Azure and/or other Microsoft products and services referenced in the documentation may be either trademarks or registered trademarks of Microsoft in the United States and/or other countries. The licenses for this project do not grant you rights to use any Microsoft names, logos, or trademarks. Microsoft's general trademark guidelines can be found at http://go.microsoft.com/fwlink/?LinkID=254653.

## License

Copyright (c) Microsoft Corporation. All rights reserved.

Licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
