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
}
