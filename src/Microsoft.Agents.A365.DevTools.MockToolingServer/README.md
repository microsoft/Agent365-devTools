# Mock Tooling Server

A lightweight MCP (Model Context Protocol) server that provides mock implementations of common Microsoft Graph and M365 tools for local agent development and testing.

## Available Mock Servers

The following mock server definitions are included out of the box:

| Server | File | Description |
|--------|------|-------------|
| `mcp_CalendarTools` | `mocks/mcp_CalendarTools.json` | Calendar operations (ListEvents, CreateEvent, FindMeetingTimes, AcceptEvent, etc.) |
| `mcp_MailTools` | `mocks/mcp_MailTools.json` | Email operations (SendEmailWithAttachments, SearchMessages, FlagEmail, ReplyToMessage, etc.) |
| `mcp_MeServer` | `mocks/mcp_MeServer.json` | User/directory operations (GetMyDetails, GetUserDetails, GetManagerDetails, etc.) |
| `mcp_KnowledgeTools` | `mocks/mcp_KnowledgeTools.json` | Federated knowledge operations (configure_federated_knowledge, query_federated_knowledge, etc.) |

### mcp_MeServer Tools

Tools for user profile and directory search operations:

| Tool | Description |
|------|-------------|
| `getMyProfile` | Get the currently signed-in user's profile (displayName, mail, jobTitle, etc.) |
| `listUsers` | Search for users in the directory by name or email. Essential for finding email addresses when only a name is provided. |
| `getUser` | Get a specific user's profile by ID or userPrincipalName |
| `getManager` | Get a user's manager |
| `getDirectReports` | Get a user's direct reports |

### mcp_CalendarTools Tools

Tools for calendar and scheduling operations including `createEvent`, `listEvents`, `getSchedule`, `findMeetingTimes`, `updateEvent`, `deleteEvent`, `cancelEvent`, `acceptEvent`, `declineEvent`, and more.

### mcp_MailTools Tools

Tools for email operations including `SendEmail`, `SendEmailWithAttachments`, and related mail functionality.

---

## Fidelity Contract

### What the mock guarantees

Every tool exposed by a real M365 MCP server is present in the corresponding mock with the same name, same casing, same required input fields, and the same set of input property names. This ensures that agents developed against the mock will not encounter missing-tool or schema-mismatch errors when switched to a real server.

The fidelity contract is CI-enforced: `MockToolFidelityTests` compares each mock tool's `inputSchema` (required fields and property names) against the corresponding snapshot.

### What the mock does not guarantee

The mock does **not** provide real data, real authentication, or real side effects. Responses are rendered from templates and are not backed by Microsoft Graph or any live service.

### Snapshot-based verification

The `snapshots/` directory contains authoritative tool catalogs captured from real M365 MCP servers. Each snapshot file records the tool names, descriptions, and input schemas as they exist on the real server at the time of capture.

To verify that mock definitions match the real server contracts locally:

```bash
dotnet test --filter "FullyQualifiedName~MockToolFidelityTests"
```

To refresh snapshots when real servers change (requires M365 credentials):

```bash
$env:MCP_BEARER_TOKEN = a365 develop get-token --output raw
MCP_UPDATE_SNAPSHOTS=true dotnet test --filter "FullyQualifiedName~MockToolSnapshotCaptureTests"
```

---

## Keeping Mocks Current

### When to update snapshots

- When real M365 MCP servers add, rename, or remove tools
- Before a release, to confirm mocks still match production
- When agent tests pass locally against mocks but fail against a real environment

### How to update

1. Obtain a bearer token using the CLI:

   ```pwsh
   # With an agent project present:
   $env:MCP_BEARER_TOKEN = a365 develop get-token --output raw

   # Without an agent project — pass app ID and explicit scopes:
   $env:MCP_BEARER_TOKEN = a365 develop get-token --app-id <your-app-id> --scopes McpServers.Mail.All McpServers.Calendar.All McpServers.Me.All McpServers.Knowledge.All --output raw
   ```

2. Run the snapshot capture tests. This refreshes both the snapshot files **and** the mock files in one step:

   ```bash
   MCP_UPDATE_SNAPSHOTS=true dotnet test --filter "FullyQualifiedName~MockToolSnapshotCaptureTests"
   ```

   The mock auto-merge preserves existing `responseTemplate` / `delayMs` / `errorRate` values for unchanged tools, adds new tools with sensible defaults, and marks removed tools as `enabled=false` for review.

3. Review the diff — no manual schema editing is required. Check `responseTemplate` for any newly added tools and customise if your agent tests need specific data shapes.

4. Run fidelity tests to confirm coverage:

   ```bash
   dotnet test --filter "FullyQualifiedName~MockToolFidelityTests"
   ```

---

# How to mock notifications for custom activities

## Prerequisites
- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0

## Invoke the custom activity

POST http://localhost:5309/agents/servers/mcp_MailTools
```json
{
"jsonrpc":"2.0",
"id":2,
"method":"tools/call",
"params":
    {
    "name":"Send_Email",
    "arguments":
        {
        "to":"user@contoso.com",
        "subject":"POC",
        "body":"Test"
        }
    }
}
```

Output:
```json
{
	"jsonrpc": "2.0",
	"id": 2,
	"result": {
		"content": [
			{
				"type": "text",
				"text": "Email to user@contoso.com with subject 'POC' sent (mock)."
			}
		],
		"isMock": true,
		"tool": "sendemail3",
		"usedArguments": {
			"to": "user@contoso.com",
			"subject": "POC",
			"body": "Test"
		},
		"template": "Email to {{to}} with subject '{{subject}}' sent (mock).",
		"missingPlaceholders": []
	}
}
```

## Run the MCP server
From the `mcp` folder:

```pwsh
dotnet build
dotnet run
```

The app hosts MCP over SSE and exposes default routes such as `/mcp/sse`, `/mcp/schema.json`, and `/health`.

## Configure your MCP client (VS Code example)
Add this to your VS Code MCP configuration (as provided):

```json
{
  "servers": {
    "documentTools": {
      "type": "sse",
	"url": "http://localhost:5309/mcp/sse"
    }
  },
  "inputs": []
}
```

## Available tools (high level)
This server exposes a generic mock tool system. There are no fixed domain-specific tools baked in; instead you define any number of mock tools persisted in `mocks/{serverName}.json`. They are surfaced over a JSON-RPC interface that mimics an MCP tool catalog.

### 1. JSON-RPC tool methods (endpoint: POST /agents/servers/{mcpServerName})
tools/list
	Returns all enabled mock tools. Shape:
```json
{
"tools":
	[
		{
		"name": "Send_Email",
		"description": "Send an email (mock).",
		"responseTemplate": "Email to {{to}} ...",
		"placeholders": ["to","subject"],
		"inputSchema":
			{
				"type": "object",
				"properties":
				{
					"to": { "type":"string" },
					"subject": { "type":"string" },
					"body": { "type":"string" }
				},
				"required": ["to","subject"]
			}
		}
	]
}

tools/call
	Executes a mock tool and returns a rendered response:

```json
{
	"content":
	[
		{
			"type":"text",
			"text":"..."
		}
	],
	"isMock": true,
	"tool": "Send_Email",
	"usedArguments": { ... },
	"template": "<original stored template>",
	"missingPlaceholders": ["anyPlaceholderNotSupplied"]
}
```

File changes (including manual edits to `mocks/{serverName}.json`) are auto-reloaded via a filesystem watcher.

### 2. Mock tool definition schema
Fields:
- name (string, required) : Unique identifier.
- description (string) : Human readable summary.
- inputSchema (array) : Describes the input schema for the tool call.
- responseTemplate (string) : Text with Handlebars-style placeholders `{{placeholder}}`.
- delayMs (int) : Artificial latency before responding.
- errorRate (double 0-1) : Probability of returning a simulated 500 error.
- statusCode (int) : Informational only (not currently enforcing an HTTP status on JSON-RPC).
- enabled (bool) : If false, tool is hidden from tools/list and cannot be called.

### 3. Template rendering & dynamic override
- Placeholders: Any `{{key}}` is replaced with the argument value (case-insensitive).
- Unresolved placeholders are left intact and also reported in `missingPlaceholders`.
- If the stored template equals the default literal `Mock response from {{name}}`, you can override it ad-hoc per call by supplying one of these argument keys: `responseTemplate`, `response`, `mockResponse`, `text`, `value`, or `output`.
- Example override call:
```json
{
	"jsonrpc":"2.0",
	"id":1,
	"method":"tools/call",
	"params":
	{
		"name":"AnyTool",
		"arguments":{ "responseTemplate":"Hello {{user}}", "user":"Ada" }
	}
}
```
### 4. Error & latency simulation
- If `errorRate` > 0 and a random draw is below it, the response is:

```json
{
	"error":
	{
		"code": 500,
		"message": "Simulated error for mock tool 'X'"
	}
}
```
- `delayMs` awaits before forming the result, letting you test client-side spinners/timeouts.

### 5. Example definitions
Email style tool:
```json
{
"name": "SendEmailWithAttachmentsAsync",
"description": "Create and send an email with optional attachments. Supports both file URIs (OneDrive/SharePoint) and direct file uploads (base64-encoded). IMPORTANT: If recipient names are provided instead of email addresses, you MUST first search for users to find their email addresses.",
"inputSchema": {
	"type": "object",
	"properties": {
	"to": {
		"type": "array",
		"description": "List of To recipients (MUST be email addresses - if you only have names, search for users first to get their email addresses)",
		"items": {
		"type": "string"
		}
	},
	"cc": {
		"type": "array",
		"description": "List of Cc recipients (MUST be email addresses - if you only have names, search for users first to get their email addresses)",
		"items": {
		"type": "string"
		}
	},
	"bcc": {
		"type": "array",
		"description": "List of Bcc recipients (MUST be email addresses - if you only have names, search for users first to get their email addresses)",
		"items": {
		"type": "string"
		}
	},
	"subject": {
		"type": "string",
		"description": "Subject of the email"
	},
	"body": {
		"type": "string",
		"description": "Body of the email"
	},
	"attachmentUris": {
		"type": "array",
		"description": "List of file URIs to attach (OneDrive, SharePoint, Teams, or Graph /drives/{id}/items/{id})",
		"items": {
		"type": "string"
		}
	},
	"directAttachments": {
		"type": "array",
		"description": "List of direct file attachments with format: [{\"fileName\": \"file.pdf\", \"contentBase64\": \"base64data\", \"contentType\": \"application/pdf\"}]",
		"items": {
		"type": "object",
		"properties": {
			"FileName": {
			"type": "string"
			},
			"ContentBase64": {
			"type": "string"
			},
			"ContentType": {
			"type": "string"
			}
		},
		"required": []
		}
	},
	"directAttachmentFilePaths": {
		"type": "array",
		"description": "List of local file system paths to attach; will be read and base64 encoded automatically.",
		"items": {
		"type": "string"
		}
	}
	},
	"required": []
},
"responseTemplate": "Email with subject '{{subject}}' sent successfully (mock).",
"delayMs": 250,
"errorRate": 0,
"statusCode": 200,
"enabled": true
}
```