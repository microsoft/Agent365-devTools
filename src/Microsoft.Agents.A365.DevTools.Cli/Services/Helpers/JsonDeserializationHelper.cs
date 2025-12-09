// Copyright (c) Microsoft Corporation.  
// Licensed under the MIT License.  
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;

/// <summary>
/// Helper for deserializing JSON responses that may be double-serialized
/// (where the entire JSON object is itself serialized as a JSON string)
/// </summary>
public static class JsonDeserializationHelper
{
    /// <summary>
    /// Deserializes JSON content, handling both normal and double-serialized JSON.
    /// Double-serialized JSON is when the API returns a JSON string that contains escaped JSON.
    /// </summary>
    /// <typeparam name="T">The type to deserialize to</typeparam>
    /// <param name="responseContent">The raw JSON string from the API</param>
    /// <param name="logger">Logger for diagnostic information</param>
    /// <param name="options">Optional JSON serializer options</param>
    /// <returns>The deserialized object, or null if deserialization fails</returns>
    public static T? DeserializeWithDoubleSerialization<T>(
        string responseContent,
        ILogger logger,
        JsonSerializerOptions? options = null) where T : class
    {
        options ??= new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        try
        {
            // First, try to deserialize directly (normal case - single serialization)
            return JsonSerializer.Deserialize<T>(responseContent, options);
        }
        catch (JsonException)
        {
            // Check if response is double-serialized JSON (starts with quote and contains escaped JSON)
            if (responseContent.Length > 0 && responseContent[0] == '"')
            {
                try
                {
                    logger.LogDebug("Detected double-serialized JSON. Attempting to unwrap...");
                    var actualJson = JsonSerializer.Deserialize<string>(responseContent);
                    if (!string.IsNullOrWhiteSpace(actualJson))
                    {
                        var result = JsonSerializer.Deserialize<T>(actualJson, options);
                        logger.LogDebug("Successfully deserialized double-encoded response");
                        return result;
                    }
                }
                catch (JsonException)
                {
                    // Fall through to final error logging
                }
            }

            // Only log as error when all deserialization attempts fail
            logger.LogWarning("Failed to deserialize response as {Type}", typeof(T).Name);
            logger.LogDebug("Response content: {Content}", responseContent);
            return null;
        }
    }

    /// <summary>
    /// Cleans JSON output from Azure CLI by removing control characters and non-JSON content.
    /// Azure CLI on Windows can output control characters (like 0x0C - form feed) and warning messages
    /// that need to be stripped before JSON parsing.
    /// </summary>
    /// <param name="output">The raw output from Azure CLI</param>
    /// <returns>Cleaned JSON string ready for parsing</returns>
    public static string CleanAzureCliJsonOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        // Remove control characters (0x00-0x1F except \r, \n, \t)
        // These characters can appear in Azure CLI output on Windows
        var cleaned = new System.Text.StringBuilder(output.Length);
        foreach (char c in output)
        {
            if (c >= 32 || c == '\n' || c == '\r' || c == '\t')
            {
                cleaned.Append(c);
            }
        }

        var result = cleaned.ToString().Trim();
        
        // Find the first { or [ to locate JSON start
        // This handles cases where Azure CLI outputs warnings or other text before the JSON
        int jsonStart = result.IndexOfAny(new[] { '{', '[' });
        if (jsonStart > 0)
        {
            result = result.Substring(jsonStart);
        }

        return result;
    }
}
