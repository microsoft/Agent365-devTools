using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers
{
    public class PackageMCPServerHelper
    {
        public static string GenerateManifestJson(ServerInfo p, string developerName, ILogger logger)
        {
            JsonNode root;
            try
            {
                root = JsonNode.Parse(McpConstants.PackageMCPServer.TemplateManifestJson) ?? new JsonObject();
            }
            catch
            {
                root = new JsonObject();
            }

            var obj = root as JsonObject ?? new JsonObject();

            var displayName = p.McpServerDisplayName ?? string.Empty;
            var shortDisplayName = displayName.Length <= 30 ? displayName : displayName.Substring(0, 30);
            var fullDisplayName = displayName.Length <= 100 ? displayName : displayName.Substring(0, 100);
            if (displayName.Length > 30)
            {
                logger.LogWarning("Short name truncated to 30 characters. Original '{Original}' -> '{Short}'",
                    displayName, shortDisplayName);
            }
            if (displayName.Length > 100)
            {
                logger.LogWarning("Full name truncated to 100 characters. Original '{Original}' -> '{Full}'",
                    displayName, fullDisplayName);
            }

            var description = p.McpServerDescription ?? string.Empty;
            var shortDescription = description.Length <= 80 ? description : description.Substring(0, 80);
            if (description.Length > 80)
            {
                logger.LogWarning("Short description truncated to 80 characters. Original '{Original}' -> '{Short}'",
                    description, shortDescription);
            }

            // Replace values.
            if (obj["agentConnectors"] is JsonArray connectors && connectors.Count > 0 && connectors[0] is JsonObject c0)
            {
                Set(c0, "id", p.McpServerId);
                Set(c0, "displayName", shortDisplayName);
                Set(c0, "description", description);

                if (c0["toolSource"]?["remoteMcpServer"] is JsonObject rs)
                {
                    Set(rs, "mcpServerUrl", p.McpServerUrl);
                }
            }
            Set(obj, "id", p.McpServerId);
            var developerObj = obj["developer"] as JsonObject ?? (JsonObject)(obj["developer"] = new JsonObject());
            var nameObj = obj["name"] as JsonObject ?? (JsonObject)(obj["name"] = new JsonObject());
            var descriptionObj = obj["description"] as JsonObject ?? (JsonObject)(obj["description"] = new JsonObject());

            Set(developerObj, "name", developerName);
            Set(nameObj, "short", shortDisplayName);
            Set(nameObj, "full", fullDisplayName);
            Set(descriptionObj, "short", shortDescription);
            Set(descriptionObj, "full", description);

            return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            static void Set(JsonNode? parent, string prop, string? value)
            {
                if (parent is JsonObject o)
                {
                    o[prop] = value ?? string.Empty;
                }
            }
        }

        /// <summary>
        /// The method to build the MCP package as a zip file.
        /// </summary>
        public static string BuildPackage(string manifestJson, ServerInfo info, String iconUrl, String outputPath)
        {
            Directory.CreateDirectory(outputPath);

            // Derive package name from server id
            var baseName = "Package_" + info.McpServerId;

            // Basic sanitization for file name.
            var invalidChars = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(baseName.Length);
            foreach (var ch in baseName)
            {
                sb.Append(invalidChars.Contains(ch) ? '_' : ch);
            }
            var safeName = sb.ToString();
            var zipFilePath = Path.Combine(outputPath, $"{safeName}.zip");

            var fileMode = FileMode.Create;

            // Download icon (both outline.png and color.png will use same bytes)
            byte[] iconBytes;
            using (var httpClient = new HttpClient())
            {
                using var response = httpClient.GetAsync(iconUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Failed to download icon from '{iconUrl}'. HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                iconBytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (iconBytes.Length == 0)
                {
                    throw new InvalidOperationException($"Downloaded icon from '{iconUrl}' is empty.");
                }
            }

            using (var fileStream = new FileStream(zipFilePath, fileMode, FileAccess.Write, FileShare.None))
            {
                using (var zipArchive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false))
                {
                    // manifest.json
                    WriteTextToArchive(zipArchive, McpConstants.PackageMCPServer.ManifestFileName, manifestJson);

                    // Icons
                    WriteByteToArchive(zipArchive, McpConstants.PackageMCPServer.OutlinePngIconFileName, iconBytes);
                    WriteByteToArchive(zipArchive, McpConstants.PackageMCPServer.ColorPngIconFileName, iconBytes);
                }
            }

            return zipFilePath;
        }

        private static void WriteTextToArchive(ZipArchive zipArchive, string fileName, string text)
        {
            var entry = zipArchive.CreateEntry(fileName);
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write(text);
        }

        private static void WriteByteToArchive(ZipArchive zipArchive, string fileName, byte[] binary)
        {
            var entry = zipArchive.CreateEntry(fileName);
            using var entryStream = entry.Open();
            using var bw = new BinaryWriter(entryStream);
            bw.Write(binary);
        }

        public sealed class ServerInfo
        {
            public string McpServerId { get; init; } = string.Empty;
            public string McpServerDisplayName { get; init; } = string.Empty;
            public string McpServerDescription { get; init; } = string.Empty;
            public string McpServerUrl { get; init; } = string.Empty;
        }
    }
}
