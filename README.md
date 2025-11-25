# Microsoft Agent 365 DevTools CLI

![Top Contributor](https://img.shields.io/badge/dynamic/json?color=blue&label=Top%20Contributor&query=$.top&url=https://raw.githubusercontent.com/microsoft/Agent365-devTools/main/leaderboard.json)

[![NuGet](https://img.shields.io/badge/NuGet-v0.1.0-blue)](https://www.nuget.org/packages/Microsoft.Agents.A365.DevTools.Cli) [![Downloads](https://img.shields.io/nuget/dt/Microsoft.Agents.A365.DevTools.Cli?label=Downloads&color=green)](https://www.nuget.org/packages/Microsoft.Agents.A365.DevTools.Cli)
[![Build Status](https://img.shields.io/github/actions/workflow/status/microsoft/Agent365-devTools/.github/workflows/ci.yml?branch=main&label=Build&logo=github)](https://github.com/microsoft/Agent365-devTools/actions)
[![License](https://img.shields.io/badge/License-MIT-blue)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Contributors](https://img.shields.io/github/contributors/microsoft/Agent365-devTools?label=Contributors)](https://github.com/microsoft/Agent365-devTools/graphs/contributors)

> **Note:**  
> Use the information in this README to contribute to this open-source project. To learn about using this CLI in your projects, refer to the [Microsoft Agent 365 Developer documentation](https://aka.ms/agents365/docs).

The **Microsoft Agent 365 DevTools CLI** is a command-line interface tool designed to streamline the development, deployment, and management of Microsoft Agent 365 applications. This CLI provides comprehensive tooling for configuration management, Azure resource provisioning, MCP (Model Context Protocol) server integration, and agent deployment workflows.

## Features

The Microsoft Agent 365 DevTools CLI focuses on these core areas:

- **Configuration Management**: Initialize and manage Agent 365 project configurations with interactive wizards
- **Azure Integration**: Seamless authentication, resource provisioning, and deployment to Azure
- **MCP Server Support**: Package, deploy, and manage Model Context Protocol servers
- **Development Tools**: Local development support with hot-reload and debugging capabilities
- **Deployment Automation**: Streamlined deployment workflows for production and development environments

## Current Project State

This project is currently in active development. The CLI is being actively developed and improved based on community feedback.

## Installation

### Install the CLI

From NuGet (Production):

```powershell
dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli
```

## Documentation

To know more about CLI and prerequisites:
- [Microsoft Agent 365 CLI Documentation](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-cli?tabs=windows)

For usage and command reference:
- [CLI Command Reference](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/reference/cli)

## Support

For issues, questions, or feedback:

- **Issues**: Please file issues in the [GitHub Issues](https://github.com/microsoft/Agent365-devTools/issues) section
- **Documentation**: See the [Microsoft Agent 365 Developer documentation](https://aka.ms/agents365/docs)
- **Security**: For security issues, please see [SECURITY.md](SECURITY.md)

## Contributing

This project welcomes contributions and suggestions. Most contributions require you to agree to a Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/). For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

## Useful Links

### How CLI is Used with Microsoft Agent 365 SDK

- [Microsoft Agent 365 Developer Documentation](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/?tabs=python)

### Additional Resources

- [.NET documentation](https://docs.microsoft.com/dotnet/)
- [Azure CLI documentation](https://docs.microsoft.com/cli/azure/)

## Trademarks

Microsoft, Windows, Microsoft Azure and/or other Microsoft products and services referenced in the documentation may be either trademarks or registered trademarks of Microsoft in the United States and/or other countries. The licenses for this project do not grant you rights to use any Microsoft names, logos, or trademarks. Microsoft's general trademark guidelines can be found at http://go.microsoft.com/fwlink/?LinkID=254653.

## License

Copyright (c) Microsoft Corporation. All rights reserved.

Licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
