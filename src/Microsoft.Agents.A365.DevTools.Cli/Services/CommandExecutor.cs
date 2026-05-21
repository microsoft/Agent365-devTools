// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Service for executing external commands (dotnet, az, powershell, etc.)
/// </summary>
public class CommandExecutor
{
    private readonly ILogger<CommandExecutor> _logger;

    public CommandExecutor(ILogger<CommandExecutor> logger)
    {
        _logger = logger;
    }

    public virtual async Task<CommandResult> ExecuteAsync(
        string command,
        string arguments,
        string? workingDirectory = null,
        bool captureOutput = true,
        bool suppressErrorLogging = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing: {Command} {Arguments}", command, arguments);

        var fileName = command;
        var fileArguments = arguments;
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && 
            NeedsCmdWrapper(command))
        {
            _logger.LogTrace("Wrapping command with cmd.exe for Windows batch file");
            fileName = "cmd.exe";
            fileArguments = $"/c {command} {arguments}";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = fileArguments,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        if (captureOutput)
        {
            process.OutputDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    outputBuilder.AppendLine(args.Data);
                    _logger.LogTrace(args.Data);
                }
            };

            process.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    errorBuilder.AppendLine(args.Data);
                    _logger.LogTrace(args.Data);
                }
            };
        }

        process.Start();

        if (captureOutput)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            // WaitForExitAsync does not drain the async OutputDataReceived/ErrorDataReceived
            // event queue. Calling WaitForExit() (no-arg) after ensures all buffered output
            // lines are processed before we read the builders.
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            // User pressed Ctrl+C — kill the child immediately so stdin is
            // released and the console is not stuck on a zombie subprocess.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception killEx) { _logger.LogDebug(killEx, "Failed to kill process after cancellation"); }
            throw;
        }

        var result = new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = outputBuilder.ToString(),
            StandardError = errorBuilder.ToString()
        };

        if (result.ExitCode != 0 && !suppressErrorLogging)
        {
            _logger.LogError("Command failed with exit code {ExitCode}: {Error}",
                result.ExitCode, result.StandardError);
        }

        return result;
    }

    /// <summary>
    /// Execute a command and stream output to console in real-time.
    /// If interactive is true, child's STDIN is attached to the parent console (no manual forwarding).
    /// </summary>
    public virtual async Task<CommandResult> ExecuteWithStreamingAsync(
        string command,
        string arguments,
        string? workingDirectory = null,
        string outputPrefix = "",
        bool interactive = false,
        Func<string, string?>? outputTransform = null,
        bool suppressErrorLogging = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing with streaming: {Command} {Arguments} (Interactive={Interactive})", command, arguments, interactive);

        var fileName = command;
        var fileArguments = arguments;
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && 
            NeedsCmdWrapper(command))
        {
            _logger.LogTrace("Wrapping command with cmd.exe for Windows batch file");
            fileName = "cmd.exe";
            fileArguments = $"/c {command} {arguments}";
        }

        // In interactive mode we keep stdout/err redirected (so we can still display/prefix),
        // but we DO NOT redirect stdin so the child reads directly from the console.
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = fileArguments,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = !interactive, // only redirect if not interactive
            UseShellExecute = false,
            CreateNoWindow = !interactive // show window characteristics suitable for interactive mode
        };

        using var process = new Process { StartInfo = startInfo };
        
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, args) =>
        {
            if (args.Data != null)
            {
                outputBuilder.AppendLine(args.Data);
                // Don't print JWT tokens to console (security)
                if (!IsJwtToken(args.Data))
                {
                    var display = outputTransform?.Invoke(args.Data) ?? args.Data;
                    if (display != null)
                    {
                        // outputPrefix is prepended only to the first line; callers must not return multi-line strings from outputTransform.
                        Console.WriteLine($"{outputPrefix}{display}");
                    }
                }
                else
                {
                    _logger.LogDebug("JWT token filtered from console output for security");
                }
            }
        };

        process.ErrorDataReceived += (sender, args) =>
        {
            if (args.Data != null)
            {
                errorBuilder.AppendLine(args.Data);
                // Azure CLI writes informational messages to stderr with "WARNING:" prefix.
                // Strip it for cleaner output.
                var cleanData = IsAzureCliCommand(command)
                    ? StripAzureWarningPrefix(args.Data)
                    : args.Data;
                // Suppress blank lines and known non-actionable Python / az-CLI
                // diagnostic lines that leak onto stderr even on successful calls.
                if (!string.IsNullOrWhiteSpace(cleanData) && !IsNonActionableStderrLine(cleanData))
                {
                    Console.WriteLine($"{outputPrefix}{cleanData}");
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // If not interactive and we redirected stdin we could implement scripted input later.

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            // Drain remaining async OutputDataReceived/ErrorDataReceived events.
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            // User pressed Ctrl+C — kill the child immediately so stdin is
            // released and the console is not stuck on a zombie subprocess.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception killEx) { _logger.LogDebug(killEx, "Failed to kill process after cancellation"); }
            throw;
        }

        var result = new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = outputBuilder.ToString(),
            StandardError = errorBuilder.ToString()
        };

        if (result.ExitCode != 0 && !suppressErrorLogging)
        {
            _logger.LogError("Command failed with exit code {ExitCode}", result.ExitCode);
        }

        return result;
    }

    private bool NeedsCmdWrapper(string command)
    {
        var extension = Path.GetExtension(command).ToLowerInvariant();
        if (extension == ".cmd" || extension == ".bat")
        {
            return true;
        }

        var commandName = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();
        var batchCommands = new[] { "az", "func", "npm", "npx", "node" };
        
        return batchCommands.Contains(commandName);
    }

    private bool IsAzureCliCommand(string command)
    {
        var commandName = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();
        return commandName == "az";
    }

    private string StripAzureWarningPrefix(string message)
    {
        // Azure CLI writes normal informational output to stderr with "WARNING:" prefix
        // Strip this misleading prefix for cleaner output
        var trimmed = message.TrimStart();
        if (trimmed.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Substring(8).TrimStart(); // Remove "WARNING:" and trim
        }
        return message;
    }

    /// <summary>
    /// Returns true for well-known non-actionable stderr lines that should be suppressed
    /// from the console entirely. These originate from the Python interpreter bundled
    /// inside the Azure CLI and appear on stderr even during fully successful calls:
    ///
    /// 1. Python 32-bit-on-64-bit UserWarning — emitted by the cryptography package
    ///    (e.g. "UserWarning: You are using cryptography on a 32-bit Python...").
    ///    Cannot be silenced via az CLI flags; it is purely informational.
    ///
    /// 2. Azure SDK "Readonly attribute name will be ignored" reflection warning —
    ///    appears after StripAzureWarningPrefix() has removed the "WARNING:" prefix.
    ///    It is an internal SDK model issue, not actionable by end-users.
    ///
    /// Both classes of message are captured in StandardError for diagnostic purposes
    /// (visible in the log file) but must not surface on the console as fake ERRORs.
    /// </summary>
    private static bool IsNonActionableStderrLine(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        // Python warnings module: "UserWarning: ..."
        if (trimmed.StartsWith("UserWarning:", StringComparison.OrdinalIgnoreCase))
            return true;
        // Azure SDK model reflection warning (\"WARNING:\" prefix already stripped).
        if (trimmed.StartsWith("Readonly attribute name will be ignored", StringComparison.OrdinalIgnoreCase))
            return true;
        // Python warnings module call site line that follows a UserWarning line.
        if (trimmed.StartsWith("warnings.warn(", StringComparison.Ordinal))
            return true;
        return false;
    }

    private const string JwtTokenPrefix = "eyJ";
    private const int JwtTokenDotCount = 2;
    private const int MinimumJwtTokenLength = 100;

    /// <summary>
    /// Detects JWT tokens using a heuristic approach to prevent logging sensitive credentials.
    /// </summary>
    /// <remarks>
    /// HEURISTIC DETECTION LIMITATIONS:
    ///
    /// This method uses pattern matching rather than full JWT validation for performance reasons.
    /// Detection criteria:
    /// - Starts with "eyJ" (Base64url encoding of "{" - typical JWT header start)
    /// - Contains exactly 2 dots (separating header.payload.signature)
    /// - Length greater than 100 characters (typical JWT tokens are much longer)
    ///
    /// Known Limitations:
    /// 1. FALSE POSITIVES: May incorrectly flag non-JWT base64 strings that happen to match the pattern
    ///    - Example: A base64-encoded JSON starting with "{" that contains dots in the payload
    ///    - Impact: Harmless - such strings are simply hidden from console but still captured in output
    ///
    /// 2. FALSE NEGATIVES: Will NOT detect tokens that deviate from standard JWT format
    ///    - Custom token formats not starting with "eyJ"
    ///    - Malformed JWTs with incorrect dot count
    ///    - Very short test tokens (less than 100 chars)
    ///    - Tokens embedded in log messages (e.g., "Token: eyJ...")
    ///    - Impact: Such tokens would be displayed in console (security risk)
    ///    - Rationale: Only filtering standalone tokens (lines that are purely tokens) to avoid
    ///      accidentally filtering legitimate log messages that happen to contain token-like strings
    ///
    /// 3. NO STRUCTURAL VALIDATION: Does not decode or verify JWT structure
    ///    - Does not validate base64url encoding
    ///    - Does not verify header/payload are valid JSON
    ///    - Does not check signature validity
    ///    - Rationale: Full validation would require decoding overhead for every output line
    ///
    /// SECURITY TRADE-OFF:
    /// This heuristic approach prioritizes performance and simplicity over perfect detection.
    /// It effectively filters standard Microsoft Graph JWT tokens (the primary security concern)
    /// while avoiding expensive cryptographic operations on every console output line.
    ///
    /// For absolute security, tokens should be transmitted through secure channels (environment
    /// variables, key vaults) rather than command output. This filter is a defense-in-depth measure.
    /// </remarks>
    /// <param name="line">The output line to check for JWT token patterns</param>
    /// <returns>True if the line appears to contain a JWT token and should be filtered from console</returns>
    private static bool IsJwtToken(string line)
    {
        var trimmed = line?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        return trimmed.StartsWith(JwtTokenPrefix, StringComparison.Ordinal) &&
               trimmed.Count(c => c == '.') == JwtTokenDotCount &&
               trimmed.Length > MinimumJwtTokenLength;
    }
}

public class CommandResult
{
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public bool Success => ExitCode == 0;
}
