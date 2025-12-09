// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// WebApplication for SSE hosting
var builder = WebApplication.CreateBuilder(args);

// Send logs to stderr so stdout stays clean for the protocol
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

Console.WriteLine($"[Program.cs] MCP Server starting at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC");

// MCP services with tools; add both HTTP and SSE transport
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

// Get MCP server names from existing .json files in the mocks folder
var mocksDirectory = Path.Combine(AppContext.BaseDirectory, "mocks");
Directory.CreateDirectory(mocksDirectory); // Ensure directory exists

var mcpServerNames = Directory.Exists(mocksDirectory)
    ? Directory.GetFiles(mocksDirectory, "*.json")
        .Select(Path.GetFileNameWithoutExtension)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .ToArray()
    : Array.Empty<string>();

// If no existing files, fall back to configuration or default
if (mcpServerNames.Length == 0)
{
    mcpServerNames = builder.Configuration.GetSection("Mcp:ServerNames").Get<string[]>()
        ?? new[] { builder.Configuration["Mcp:ServerName"] ?? "MockToolingServer" };
}

// Mock tool stores + executor. Each server gets its own store with file name <mcpServerName>.json under /mocks
foreach (var serverName in mcpServerNames)
{
    builder.Services.AddSingleton<IMockToolStore>(provider => new FileMockToolStore(serverName!, new MockToolStoreOptions()));
}

builder.Services.AddSingleton<IMockToolExecutor>(provider =>
    new MockToolExecutor(provider.GetServices<IMockToolStore>()));

var app = builder.Build();

// Log startup information
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("===== MCP SERVER STARTING =====");
logger.LogInformation("Startup Time: {StartupTime} UTC", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
logger.LogInformation("Server will be available on: http://localhost:5309");
foreach (var serverName in mcpServerNames)
{
    logger.LogInformation("Mock tools file for '{ServerName}': {File}", serverName, Path.Combine(AppContext.BaseDirectory, "mocks", serverName + ".json"));
}
logger.LogInformation("===== END STARTUP INFO =====");

// Map MCP SSE endpoints at the default route ("/mcp")
// Available routes include: /mcp/sse (server-sent events) and /mcp/schema.json
app.MapMcp();

// Log that MCP is mapped
logger.LogInformation("MCP endpoints mapped: /mcp/sse, /mcp/schema.json");

// Optional minimal health endpoint for quick check
// app.MapGet("/", () => Results.Ok(new { status = "ok", mcp = "/mcp" }));
app.MapGet("/health", () => Results.Ok(new { status = "ok", mcp = "/mcp", mock = "/mcp-mock" }));

// ===================== MOCK MCP ENDPOINTS =====================
// JSON-RPC over HTTP for mock tools at /mcp-mock
app.MapPost("/agents/servers/{mcpServerName}", async (string mcpServerName, HttpRequest httpRequest, IMockToolExecutor executor, ILogger<Program> log) =>
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(httpRequest.Body);
        var root = doc.RootElement;
        object? idValue = null;
        if (root.TryGetProperty("id", out var idProp))
        {
            if (idProp.ValueKind == JsonValueKind.Number)
            {
                idValue = idProp.TryGetInt64(out var longVal) ? (object?)longVal : idProp.GetDouble();
            }
            else if (idProp.ValueKind == JsonValueKind.String)
            {
                idValue = idProp.GetString();
            }
            else
            {
                idValue = null;
            }
        }

        if (!root.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
        {
            return Results.BadRequest(new { error = "Invalid or missing 'method' property." });
        }

        var method = methodProp.GetString();

        if (string.Equals(method, "initialize", StringComparison.OrdinalIgnoreCase))
        {
            var initializeResult = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new
                {
                    logging = new { },
                    prompts = new
                    {
                        listChanged = true
                    },
                    resources = new
                    {
                        subscribe = true,
                        listChanged = true
                    },
                    tools = new
                    {
                        listChanged = true
                    }
                },
                serverInfo = new
                {
                    name = "ExampleServer",
                    title = "Example Server Display Name",
                    version = "1.0.0",
                },
                instructions = "Optional instructions for the client"
            };
            return Results.Json(new { jsonrpc = "2.0", id = idValue, result = initializeResult });
        }
        if (string.Equals(method, "logging/setLevel", StringComparison.OrdinalIgnoreCase))
        {
            // Acknowledge but do nothing
            return Results.Json(new { jsonrpc = "2.0", id = idValue, result = new { } });
        }
        if (string.Equals(method, "tools/list", StringComparison.OrdinalIgnoreCase))
        {
            var listResult = await executor.ListToolsAsync(mcpServerName);
            return Results.Json(new { jsonrpc = "2.0", id = idValue, result = listResult });
        }
        if (string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase))
        {
            var name = root.GetProperty("params").GetProperty("name").GetString() ?? string.Empty;
            var argsDict = new Dictionary<string, object?>();
            if (root.GetProperty("params").TryGetProperty("arguments", out var argsList) && argsList.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in argsList.EnumerateObject())
                {
                    object? converted = null;
                    switch (prop.Value.ValueKind)
                    {
                        case JsonValueKind.String:
                            converted = prop.Value.GetString();
                            break;
                        case JsonValueKind.Number:
                            if (prop.Value.TryGetInt64(out var lnum)) converted = lnum; else converted = prop.Value.GetDouble();
                            break;
                        case JsonValueKind.True:
                            converted = true; break;
                        case JsonValueKind.False:
                            converted = false; break;
                        case JsonValueKind.Null:
                            converted = null; break;
                        default:
                            converted = prop.Value.GetRawText();
                            break;
                    }
                    argsDict[prop.Name] = converted;
                }
            }
            var callResult = await executor.CallToolAsync(mcpServerName, name, argsDict!);
            // Detect error shape
            var errorProp = callResult.GetType().GetProperty("error");
            if (errorProp != null)
            {
                return Results.Json(new { jsonrpc = "2.0", id = idValue, error = errorProp.GetValue(callResult) });
            }
            return Results.Json(new { jsonrpc = "2.0", id = idValue, result = callResult });
        }

        return Results.Json(new { jsonrpc = "2.0", id = idValue, error = new { code = -32601, message = $"Method ({method}) not found" } });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Mock JSON-RPC failure");
        return Results.Json(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32603, message = ex.Message } });
    }
});

logger.LogInformation("[Program.cs] Starting MCP server... Watch for tool calls in the logs!");

await app.RunAsync();