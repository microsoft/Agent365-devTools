// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Contains derived names generated from an agent name during configuration initialization
/// </summary>
public class ConfigDerivedNames
{
    public string WebAppName { get; set; } = string.Empty;
    public string AgentIdentityDisplayName { get; set; } = string.Empty;
    public string AgentBlueprintDisplayName { get; set; } = string.Empty;
    public string AgentUserPrincipalName { get; set; } = string.Empty;
    public string AgentUserDisplayName { get; set; } = string.Empty;
}
