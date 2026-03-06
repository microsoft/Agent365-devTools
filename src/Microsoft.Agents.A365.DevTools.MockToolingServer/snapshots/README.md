# Mock Tooling Server Snapshots

## What Are Snapshot Files?

Snapshot files capture the real tool catalog from live M365 MCP servers at a specific point in time. They serve as a ground-truth reference for verifying that the mock tool definitions in `../mocks/` accurately reflect the tools exposed by the production M365 MCP endpoints.

Each snapshot file records the complete set of tool names, descriptions, and input schemas returned by one M365 MCP server. By comparing snapshots against the mock definitions, fidelity tests can detect drift: tools that have been added, removed, or changed upstream but not yet reflected in the mocks.

## Snapshot File Schema

Each snapshot file follows this JSON structure:

```json
{
  "$schema": "mock-snapshot-schema",
  "capturedAt": "<ISO 8601 UTC timestamp, or \"UNPOPULATED\">",
  "serverName": "<MCP server name, e.g. mcp_CalendarTools>",
  "tools": [
    {
      "name": "<tool name>",
      "description": "<tool description>",
      "inputSchema": { <JSON Schema object> }
    }
  ]
}
```

| Field | Description |
|---|---|
| `$schema` | Always `"mock-snapshot-schema"`. Reserved for future formal JSON Schema validation. |
| `capturedAt` | ISO 8601 UTC timestamp of when the snapshot was captured. `"UNPOPULATED"` means the file has never been populated with real data. |
| `serverName` | The M365 MCP server name this snapshot corresponds to (e.g., `mcp_CalendarTools`, `mcp_MailTools`, `mcp_MeServer`, `mcp_KnowledgeTools`). |
| `tools` | Array of tool definitions. Each entry has `name` (string), `description` (string), and `inputSchema` (JSON Schema object). |

## How to Update Snapshots

Snapshots are refreshed using `MockToolSnapshotCaptureTests` — a set of integration
tests that query live M365 MCP servers and either detect drift or write updated
snapshot files.

Prerequisites:
- A valid M365 bearer token for the Agent 365 Tools resource

### Obtain a bearer token

With an agent project (ToolingManifest.json present):
```powershell
$env:MCP_BEARER_TOKEN = a365 develop get-token --output raw
```

Without an agent project — pass your app registration client ID and explicit scopes.
Your app registration must have delegated permissions on the Agent 365 Tools resource
(`ea9ffc3e-8a23-4a7d-836d-234d7c7565c1`):
```powershell
$env:MCP_BEARER_TOKEN = a365 develop get-token `
    --app-id <your-client-app-id> `
    --scopes McpServers.Mail.All McpServers.Calendar.All McpServers.Me.All McpServers.Knowledge.All `
    --output raw
```

### Detect drift (read-only)

Fails the test if the live server differs from the snapshot — nothing is written:
```bash
dotnet test --filter "FullyQualifiedName~MockToolSnapshotCaptureTests"
```

### Refresh snapshot files

Writes updated snapshot files **and auto-updates the corresponding mock files** in `../mocks/`:
```bash
MCP_UPDATE_SNAPSHOTS=true dotnet test --filter "FullyQualifiedName~MockToolSnapshotCaptureTests"
```

The mock auto-merge:
- **Existing tools**: `inputSchema` updated from snapshot; `responseTemplate`, `delayMs`, `errorRate`, `statusCode`, and `enabled` preserved from the current mock entry.
- **New tools**: added with schema from snapshot and sensible defaults (`responseTemplate` is auto-generated, `delayMs=250`, `enabled=true`).
- **Removed tools**: kept in the mock file with `enabled=false` for developer review — delete them explicitly once confirmed.

After refreshing, run `MockToolFidelityTests` to confirm coverage, then review the diff
(especially `responseTemplate` for any new tools) before committing.

## UNPOPULATED Snapshots

When `capturedAt` is `"UNPOPULATED"`, the snapshot file contains no real tool data. This is the initial state of all snapshot files when they are first created.

Fidelity tests (`MockToolFidelityTests`) will **skip** (not fail) when a snapshot is UNPOPULATED. Once a snapshot has been populated with real data from a live M365 server, fidelity tests will enforce full coverage, flagging any tools present in the snapshot but missing from the mocks, and vice versa.

To populate snapshots, run the update script as described above.
