// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Information about a password credential (client secret).
/// Note: The actual secret value cannot be retrieved from Graph API.
/// </summary>
public class PasswordCredentialInfo
{
    public string? DisplayName { get; set; }
    public string? Hint { get; set; }
    public string? KeyId { get; set; }
    public DateTime? EndDateTime { get; set; }

    public bool IsExpired => EndDateTime.HasValue && EndDateTime.Value < DateTime.UtcNow;
    public bool IsExpiringSoon => EndDateTime.HasValue && EndDateTime.Value < DateTime.UtcNow.AddDays(30);
}
