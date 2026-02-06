# Models

This folder contains data models used throughout the CLI. Models are organized by their domain and usage pattern.

> **Parent:** [CLI Design](../design.md)

---

## Model Groups

### Configuration Models

| Model | File | Description |
|-------|------|-------------|
| **Agent365Config** | `Agent365Config.cs` | Unified configuration model (static + dynamic properties) |
| **ConfigDerivedNames** | `ConfigDerivedNames.cs` | Auto-generated names derived from agent name |
| **McpServerConfig** | `McpServerConfig.cs` | MCP server configuration settings |
| **ResourceConsent** | `ResourceConsent.cs` | Per-resource consent tracking model |

### Azure Models

| Model | File | Description |
|-------|------|-------------|
| **AzureModels** | `AzureModels.cs` | Azure resource response models |
| **OryxManifest** | `OryxManifest.cs` | Azure Oryx deployment manifest |
| **ProjectPlatform** | `ProjectPlatform.cs` | Platform enumeration (Unknown, DotNet, NodeJs, Python) |
| **EndpointRegistrationResult** | `EndpointRegistrationResult.cs` | Messaging endpoint registration result |

### Dataverse/MCP Models

| Model | File | Description |
|-------|------|-------------|
| **DataverseEnvironment** | `DataverseEnvironment.cs` | Dataverse environment information |
| **DataverseMcpServer** | `DataverseMcpServer.cs` | MCP server in Dataverse |
| **PublishMcpServerRequest** | `PublishMcpServerRequest.cs` | MCP server publish request |
| **PublishMcpServerResponse** | `PublishMcpServerResponse.cs` | MCP server publish response |
| **ToolingManifest** | `ToolingManifest.cs` | MCP tooling manifest structure |

### Permission Models

| Model | File | Description |
|-------|------|-------------|
| **EnumeratedScopes** | `EnumeratedScopes.cs` | Graph API scope enumeration results |

### Other Models

| Model | File | Description |
|-------|------|-------------|
| **BlueprintLookupModels** | `BlueprintLookupModels.cs` | Blueprint query response models |
| **FederatedCredentialModels** | `FederatedCredentialModels.cs` | Federated identity credential models |
| **PasswordCredentialInfo** | `PasswordCredentialInfo.cs` | Application password credential info |
| **VersionCheckModels** | `VersionCheckModels.cs` | CLI version check response models |

---

## Agent365Config

The primary configuration model uses C# property patterns to enforce immutability:

```csharp
public class Agent365Config
{
    // STATIC PROPERTIES (init-only) - from a365.config.json
    public string TenantId { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public string ResourceGroup { get; init; } = string.Empty;

    // DYNAMIC PROPERTIES (get/set) - from a365.generated.config.json
    public string? AgentBlueprintId { get; set; }
    public string? AgentIdentityId { get; set; }
}
```

- **`init`** properties: Set once, immutable after construction (static config)
- **`get; set`** properties: Mutable at runtime (dynamic state)

See [CLI Design - Configuration System](../design.md#configuration-system-architecture) for details.

---

## ProjectPlatform Enum

```csharp
public enum ProjectPlatform
{
    Unknown,    // Could not detect platform
    DotNet,     // .NET (*.csproj, *.fsproj, *.vbproj)
    NodeJs,     // Node.js (package.json)
    Python      // Python (pyproject.toml, requirements.txt, *.py)
}
```

Used by `PlatformDetector` to auto-detect project type.

---

## OryxManifest

Azure Oryx deployment manifest model:

```csharp
public class OryxManifest
{
    public string Platform { get; set; }      // dotnet, nodejs, python
    public string Version { get; set; }       // 8.0, 20, 3.11
    public string Command { get; set; }       // Startup command
    public string OutputPath { get; set; }    // Build output location
}
```

---

## Cross-References

- **[CLI Design](../design.md)** - Configuration system architecture
- **[Services/](../Services/README.md)** - Services that use these models
- **[DEVELOPER.md](../../DEVELOPER.md#adding-a-configuration-property)** - How to add new properties
