// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.TestSupport
{
    // Minimal shim used by tests to represent command execution result
    public class CommandResult
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
    }
}
