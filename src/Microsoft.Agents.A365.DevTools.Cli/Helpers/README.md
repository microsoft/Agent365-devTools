# Helpers

This folder contains utility helper classes that provide common functionality used across the CLI. Unlike service helpers (in `Services/Helpers/`), these are general-purpose utilities not tied to specific services.

> **Parent:** [CLI Design](../design.md)

---

## Helper Reference

| Helper | File | Description |
|--------|------|-------------|
| **FileHelper** | `FileHelper.cs` | File system operations (read, write, copy, delete) |
| **CommandStringHelper** | `CommandStringHelper.cs` | Command-line argument building and escaping |
| **ManifestHelper** | `ManifestHelper.cs` | Teams app manifest parsing and modification |
| **PublishHelpers** | `PublishHelpers.cs` | MOS publishing workflow helpers |
| **SecretProtectionHelper** | `SecretProtectionHelper.cs` | Mask secrets in logs and output |
| **TenantDetectionHelper** | `TenantDetectionHelper.cs` | Detect tenant from Azure CLI or environment |
| **PackageMCPServerHelper** | `PackageMCPServerHelper.cs` | Package MCP servers for deployment |
| **ProjectSettingsSyncHelper** | `ProjectSettingsSyncHelper.cs` | Sync settings between project files |

---

## FileHelper

File system operations with error handling:

```csharp
public static class FileHelper
{
    public static async Task<string> ReadAllTextAsync(string path);
    public static async Task WriteAllTextAsync(string path, string content);
    public static void EnsureDirectoryExists(string path);
    public static void DeleteFileIfExists(string path);
    public static void CopyDirectory(string source, string destination, string[] excludePatterns);
}
```

---

## CommandStringHelper

Build and escape command-line arguments:

```csharp
public static class CommandStringHelper
{
    // Escape argument for shell
    public static string EscapeArgument(string arg);

    // Build command string from parts
    public static string BuildCommand(string command, params string[] args);

    // Parse command output
    public static (string stdout, string stderr) ParseOutput(string output);
}
```

---

## SecretProtectionHelper

Mask sensitive values in logs and output:

```csharp
public static class SecretProtectionHelper
{
    // Mask a secret value
    public static string Mask(string secret)
        => secret.Length > 4
            ? new string('*', secret.Length - 4) + secret[^4..]
            : new string('*', secret.Length);

    // Mask secrets in a string
    public static string MaskSecrets(string text, IEnumerable<string> secrets);
}
```

---

## PublishHelpers

MOS publishing workflow helpers:

```csharp
public static class PublishHelpers
{
    // Ensure MOS prerequisites (service principals) exist
    public static async Task EnsureMosPrerequisitesAsync(
        GraphApiService graphService,
        string tenantId,
        ILogger logger);

    // Check if admin consent is granted
    public static async Task<bool> CheckAdminConsentAsync(
        GraphApiService graphService,
        string clientAppId);

    // Get admin consent URL
    public static string GetAdminConsentUrl(string tenantId, string clientAppId);
}
```

---

## TenantDetectionHelper

Detect tenant ID from various sources:

```csharp
public static class TenantDetectionHelper
{
    // Detection priority:
    // 1. Explicit configuration
    // 2. Azure CLI (az account show)
    // 3. Environment variable (AZURE_TENANT_ID)
    public static async Task<string?> DetectTenantIdAsync(
        IProcessService processService,
        Agent365Config? config);
}
```

---

## Cross-References

- **[CLI Design](../design.md)** - Architecture overview
- **[Services/Helpers/](../Services/Helpers/README.md)** - Service-specific helpers
- **[Commands/](../Commands/README.md)** - Commands that use these helpers
