# How to mock notifications for custom activities

## Prerequisites
- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0

## Create mock
- POST http://localhost:5309/mcp-mock/admin/tools
```json
{
"name": "Send_Email",
"description": "Send an email (mock).",
"parameters":[
    { "name": "to", "type": "string", "isOptional": false },
    { "name": "subject", "type": "string", "isOptional": false },
    { "name": "body", "type": "string", "isOptional": true }
    ],
 "responseTemplate": "Email to \u007B\u007Bto\u007D\u007D with subject '\u007B\u007Bsubject\u007D\u007D' sent (mock).",
 "delayMs": 250,
 "statusCode": 200,
 "enabled": true
 }
```

Response -

```json
{
	"name": "sendemail3",
	"description": "Send an email (mock).",
	"parameters": [
		{
			"name": "to",
			"type": "string",
			"isOptional": false
		},
		{
			"name": "subject",
			"type": "string",
			"isOptional": false
		},
		{
			"name": "body",
			"type": "string",
			"isOptional": true
		}
	],
	"responseTemplate": "Email to {{to}} with subject '{{subject}}' sent (mock).",
	"delayMs": 250,
	"errorRate": 0,
	"statusCode": 200,
	"enabled": true
}
```

## Invoke the custom activity

POST http://localhost:5309/mcp-mock
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

Output-
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
From the `MockNotificationMCP` folder:

```pwsh
# Run (Debug)
dotnet run

# Or build and run
dotnet build -c Debug
.\nMockNotificationMCP\bin\Debug\net8.0\MockNotificationMCP.exe
```

The app hosts MCP over SSE and exposes default routes such as `/mcp/sse` and `/mcp/schema.json`.

## Configure your MCP client (VS Code example)
Add this to your VS Code MCP configuration (as provided):

```json
{
  "servers": {
    "documentTools": {
      "type": "sse",
      "url": "http://localhost:5000"
    }
  },
  "inputs": []
}
```

## Available tools (high level)
This server exposes a generic mock tool system. There are no fixed domain-specific tools baked in; instead you define any number of mock tools persisted in `mocks/tools.json` (managed through the admin endpoints). They are surfaced over a JSON-RPC interface that mimics an MCP tool catalog.

### 1. JSON-RPC tool methods (endpoint: POST /mcp-mock)
- tools/list
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

- tools/call
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
### 2. Admin REST endpoints (manage definitions)
Base path: /mcp-mock/admin/tools
- GET /mcp-mock/admin/tools : list raw definitions (including disabled).
- GET /mcp-mock/admin/tools/{name} : fetch one.
- POST /mcp-mock/admin/tools : create or overwrite (body = MockToolDefinition).
- PUT /mcp-mock/admin/tools/{name} : update (path name wins).
- DELETE /mcp-mock/admin/tools/{name} : remove.

File changes (including manual edits to `mocks/tools.json`) are auto-reloaded via a filesystem watcher.

### 3. Mock tool definition schema
Fields:
- name (string, required) : Unique identifier.
- description (string) : Human readable summary.
- parameters (array) : Each has name, type (string), isOptional (bool).
- responseTemplate (string) : Text with Handlebars-style placeholders `{{placeholder}}`.
- delayMs (int) : Artificial latency before responding.
- errorRate (double 0-1) : Probability of returning a simulated 500 error.
- statusCode (int) : Informational only (not currently enforcing an HTTP status on JSON-RPC).
- enabled (bool) : If false, tool is hidden from tools/list and cannot be called.

### 4. Template rendering & dynamic override
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
### 5. Error & latency simulation
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

### 6. Example definitions
Email style tool:
```json
{
	"name": "Send_Email",
	"description": "Send an email (mock).",
	"parameters":
	[
		{ "name": "to", "type": "string", "isOptional": false },
		{ "name": "subject", "type": "string", "isOptional": false },
		{ "name": "body", "type": "string", "isOptional": true }
	],
	"responseTemplate": "Email to {{to}} with subject '{{subject}}' sent (mock).",
	"delayMs": 250,
	"errorRate": 0,
	"statusCode": 200,
	"enabled": true
}
```