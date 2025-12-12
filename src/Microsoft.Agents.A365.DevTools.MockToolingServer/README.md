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