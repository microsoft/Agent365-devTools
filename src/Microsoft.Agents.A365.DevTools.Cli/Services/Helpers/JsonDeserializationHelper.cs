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
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to deserialize response directly, checking for double-serialization");

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
                catch (Exception unwrapEx)
                {
                    logger.LogError(unwrapEx, "Failed to unwrap double-serialized response");
                }
            }

            logger.LogError(ex, "Failed to deserialize response");
            logger.LogDebug("Response content: {Content}", responseContent);
            return null;
        }
    }

    /// <summary>
    /// Attempts deserialization with a fallback strategy.
    /// First tries to deserialize as T, then as TFallback if T fails.
    /// </summary>
    /// <typeparam name="T">Primary type to deserialize to</typeparam>
    /// <typeparam name="TFallback">Fallback type if primary deserialization fails</typeparam>
    /// <param name="responseContent">The raw JSON string from the API</param>
    /// <param name="logger">Logger for diagnostic information</param>
    /// <param name="options">Optional JSON serializer options</param>
    /// <returns>Result with the deserialized object and which type was used</returns>
    public static (T? result, bool usedFallback) DeserializeWithFallback<T, TFallback>(
        string responseContent,
        ILogger logger,
        JsonSerializerOptions? options = null)
        where T : class
        where TFallback : class
    {
        var primaryResult = DeserializeWithDoubleSerialization<T>(responseContent, logger, options);
        if (primaryResult != null)
        {
            return (primaryResult, false);
        }

        logger.LogDebug("Primary deserialization failed, attempting fallback type {FallbackType}", typeof(TFallback).Name);
        var fallbackResult = DeserializeWithDoubleSerialization<TFallback>(responseContent, logger, options);

        if (fallbackResult != null && fallbackResult is T converted)
        {
            return (converted, true);
        }

        return (null, false);
    }
}
