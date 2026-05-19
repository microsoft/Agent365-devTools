// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

// Wire-format models for inheritablePermissions polymorphic properties.
// Schema: https://learn.microsoft.com/en-us/entra/agent-id/configure-inheritable-permissions-blueprints
//
// The agent-blueprint inheritablePermissions API takes a wildcard (allAllowed) form on each
// of inheritableScopes and inheritableRoles. The legacy enumerated form is being deprecated;
// the CLI sends allAllowed for both to make every scope and role granted to the blueprint SP
// available for inheritance by agent identities.

internal class AllAllowedScopes
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; set; } = "#microsoft.graph.allAllowedScopes";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "allAllowed";
}

internal class AllAllowedRoles
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; set; } = "#microsoft.graph.allAllowedRoles";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "allAllowed";
}
