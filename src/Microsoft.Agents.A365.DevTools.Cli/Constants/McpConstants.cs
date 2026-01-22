// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;

/// <summary>
/// Constants for MCP (Model Context Protocol) operations
/// </summary>
public static class McpConstants
{

    // Agent 365 Tools App IDs for different environments
    public const string Agent365ToolsProdAppId = "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1";

    /// <summary>
    /// Name of the tooling manifest file
    /// </summary>
    public const string ToolingManifestFileName = "ToolingManifest.json";

    /// <summary>
    /// JSON-RPC version
    /// </summary>
    public const string JsonRpcVersion = "2.0";

    /// <summary>
    /// Method name for calling MCP tools
    /// </summary>
    public const string ToolsCallMethod = "tools/call";

    /// <summary>
    /// Name of the ListToolServers tool
    /// </summary>
    public const string ListToolServersToolName = "ListToolServers";

    // HTTP Headers
    public static class MediaTypes
    {
        public const string ApplicationJson = "application/json";
        public const string TextEventStream = "text/event-stream";
    }

    // Server-Sent Events (SSE) constants
    public static class ServerSentEvents
    {
        public const string EventPrefix = "event:";
        public const string DataPrefix = "data: ";
        public const int DataPrefixLength = 6;
    }

    // JSON property names for config file
    public static class ConfigProperties
    {
        public const string DeveloperMcpServer = "developerMCPServer";
        public const string Url = "url";
        public const string AgentUserId = "agentUserId";
        public const string Environment = "environment";
    }

    // JSON property names for ToolingManifest.json
    public static class ManifestProperties
    {
        public const string McpServers = "mcpServers";
        public const string McpServerName = "mcpServerName";
        public const string McpServerUniqueName = "mcpServerUniqueName";
        public const string Url = "url";
        public const string Scope = "scope";
        public const string Audience = "audience";
    }

    // MCP Server to Entra Scope mappings
    public static class ServerScopeMappings
    {
        public static readonly Dictionary<string, (string Scope, string Audience)> ServerToScope =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Email/Mail servers
                ["MCP_MailTools"] = ("McpServers.Mail.All", "api://mcp-mailtools"),
                ["mcp_MailTools"] = ("McpServers.Mail.All", "api://mcp-mailtools"),
                ["EmailAttachmentTools"] = ("McpServers.Mail.All", "api://mcp-mailtools"),

                // Calendar servers
                ["MCP_CalendarTools"] = ("McpServers.Calendar.All", "api://mcp-calendartools"),
                ["mcp_CalendarTools"] = ("McpServers.Calendar.All", "api://mcp-calendartools"),

                // Knowledge/Search servers
                ["MCP_NLWeb"] = ("McpServers.Knowledge.All", "api://mcp-nlweb"),
                ["mcp_NLWeb"] = ("McpServers.Knowledge.All", "api://mcp-nlweb"),
                ["mcp_KnowledgeTools"] = ("McpServers.Knowledge.All", "api://mcp-knowledgetools"),
                ["mcp_SearchTools"] = ("McpServers.Knowledge.All", "api://mcp-searchtools"),

                // Office document servers
                ["MCP_PowerpointTools"] = ("McpServers.Powerpoint.All", "api://mcp-powerpointtools"),
                ["MCP_WordTools"] = ("McpServers.Word.All", "api://mcp-wordtools"),
                ["MCPServerWord"] = ("McpServers.Word.All", "api://mcp-wordtools"),
                ["MCP_ExcelTools"] = ("McpServers.Excel.All", "api://mcp-exceltools"),
                ["McpServerExcel"] = ("McpServers.Excel.All", "api://mcp-exceltools"),

                // SharePoint/OneDrive servers
                ["MCP_SharepointListsTools"] = ("McpServers.SharepointLists.All", "api://mcp-sharepointliststools"),
                ["mcp_SharePointTools"] = ("McpServers.SharepointLists.All", "api://mcp-sharepointtools"),
                ["MCP_OneDriveSharepointTools"] = ("McpServers.OneDriveSharepoint.All", "api://mcp-onedrivesharepointtools"),
                ["mcp_OneDriveServer"] = ("McpServers.OneDriveSharepoint.All", "api://mcp-onedriveserver"),
                ["mcp_ODSPRemoteServer"] = ("McpServers.OneDriveSharepoint.All", "api://mcp-odspremoteserver"),

                // Teams servers
                ["MCP_TeamsTools"] = ("McpServers.Teams.All", "api://mcp-teamstools"),
                ["mcp_TeamsServer"] = ("McpServers.Teams.All", "api://mcp-teamsserver"),
                ["mcp_TeamsCanaryServer"] = ("McpServers.Teams.All", "api://mcp-teamscanaryserver"),

                // User/Me servers
                ["MCP_MeTools"] = ("McpServers.Me.All", "api://mcp-metools"),
                ["MCP_MeServer"] = ("McpServers.Me.All", "api://mcp-meserver"),
                ["mcp_meserver"] = ("McpServers.Me.All", "api://mcp-meserver"),
                ["MeMCPServer"] = ("McpServers.Me.All", "api://mcp-meserver"),

                // Admin servers
                ["mcp_Admin365_GraphTools"] = ("McpServers.Admin365.All", "api://mcp-admin365graphtools")
            };

        /// <summary>
        /// Gets the scope and audience for a given MCP server name
        /// </summary>
        /// <param name="serverName">The MCP server name</param>
        /// <returns>Tuple containing scope and audience, or null values if not found</returns>
        public static (string? Scope, string? Audience) GetScopeAndAudience(string serverName)
        {
            if (ServerToScope.TryGetValue(serverName, out var mapping))
            {
                return (mapping.Scope, mapping.Audience);
            }
            return (null, null);
        }

        /// <summary>
        /// Gets all available scopes from the mapping
        /// </summary>
        /// <returns>Array of all available scopes</returns>
        public static string[] GetAllScopes()
        {
            return ServerToScope.Values.Select(v => v.Scope).Distinct().OrderBy(s => s).ToArray();
        }
    }

    // PackageMCPServer constants
    public static class PackageMCPServer
    {
        public const string OutlinePngIconFileName = "outline.png";
        public const string ColorPngIconFileName = "color.png";
        public const string ManifestFileName = "manifest.json";
        public const string TemplateManifestJson =
        @"
        {
            ""$schema"": ""https://developer.microsoft.com/en-us/json-schemas/teams/vDevPreview/MicrosoftTeams.schema.json"",
            ""manifestVersion"": ""devPreview"",
            ""agentConnectors"": [
                {
                    ""id"": ""11111111-1111-1111-1111-111111111112"",
                    ""displayName"": ""DUMMY_DISPLAY_NAME"",
                    ""description"": ""DUMMY_DESCRIPTION"",
                    ""toolSource"": {
                        ""remoteMcpServer"": {
                            ""mcpServerUrl"": ""https://example.com/mcpServer"",
                            ""authorization"": {
                                ""type"": ""None""
                            }
                        }
                    }
                }
            ],
            ""version"": ""1.0.0"",
            ""id"": ""11111111-1111-1111-1111-111111111112"",
            ""developer"": {
                ""name"": ""DUMMY_DEVELOPER"",
                ""websiteUrl"": ""https://go.microsoft.com/fwlink/?linkid=2138949"",
                ""privacyUrl"": ""https://go.microsoft.com/fwlink/?linkid=2138865"",
                ""termsOfUseUrl"": ""https://go.microsoft.com/fwlink/?linkid=2138950""
            },
            ""name"": {
                ""short"": ""DUMMY_SHORT_NAME"",
                ""full"": ""DUMMY_FULL_NAME""
            },
            ""description"": {
                ""short"": ""DUMMY_SHORT_DESCRIPTION"",
                ""full"": ""DUMMY_FULL_DESCRIPTION""
            },
            ""icons"": {
                ""outline"": ""outline.png"",
                ""color"": ""color.png""
            },
            ""accentColor"": ""#E0F6FC""
        }";
    }

}
