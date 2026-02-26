// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Constants;

/// <summary>
/// JSON-RPC 2.0 specification constants
/// </summary>
public static class JsonRpcConstants
{
    /// <summary>
    /// JSON-RPC version string
    /// </summary>
    public const string Version = "2.0";

    /// <summary>
    /// JSON-RPC error codes per JSON-RPC 2.0 specification
    /// See: https://www.jsonrpc.org/specification#error_object
    /// </summary>
    public static class ErrorCodes
    {
        /// <summary>
        /// Invalid Request (-32600) - The JSON sent is not a valid Request object
        /// </summary>
        public const int InvalidRequest = -32600;

        /// <summary>
        /// Method not found (-32601) - The method does not exist / is not available
        /// </summary>
        public const int MethodNotFound = -32601;

        /// <summary>
        /// Invalid params (-32602) - Invalid method parameter(s)
        /// </summary>
        public const int InvalidParams = -32602;

        /// <summary>
        /// Internal error (-32603) - Internal JSON-RPC error
        /// </summary>
        public const int InternalError = -32603;
    }

    /// <summary>
    /// HTTP status codes for MCP protocol
    /// </summary>
    public static class HttpStatusCodes
    {
        /// <summary>
        /// Accepted (202) - Used for MCP notifications per Streamable HTTP spec
        /// Notifications do not expect a response body
        /// </summary>
        public const int Accepted = 202;
    }
}
