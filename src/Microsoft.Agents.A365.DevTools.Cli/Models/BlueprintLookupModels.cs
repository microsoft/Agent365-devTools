// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Result of blueprint application lookup operation.
/// </summary>
public class BlueprintLookupResult
{
    public bool Found { get; set; }
    public string? ObjectId { get; set; }
    public string? AppId { get; set; }
    public string? DisplayName { get; set; }
    public string? LookupMethod { get; set; }
    public bool RequiresPersistence { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result of service principal lookup operation.
/// </summary>
public class ServicePrincipalLookupResult
{
    public bool Found { get; set; }
    public string? ObjectId { get; set; }
    public string? AppId { get; set; }
    public string? DisplayName { get; set; }
    public string? LookupMethod { get; set; }
    public bool RequiresPersistence { get; set; }
    public string? ErrorMessage { get; set; }
}
