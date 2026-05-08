// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Services;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

public class LogsCommand
{
    public static Command CreateCommand(
        ILogger<LogsCommand> logger,
        ILogRedactionService logRedactionService)
    {
        var logsCommand = new Command(CommandNames.Logs, "Manage CLI diagnostic logs");

        logsCommand.AddCommand(CreateExportCommand(logger, logRedactionService));

        return logsCommand;
    }

    private static Command CreateExportCommand(
        ILogger<LogsCommand> logger,
        ILogRedactionService logRedactionService)
    {
        var commandArg = new Argument<string?>(
            name: "command",
            description: "Name of the command whose log to export (e.g. setup, cleanup). Omit to export all available logs.",
            getDefaultValue: () => null);

        var outputOption = new Option<string?>(
            ["--output", "-o"],
            description: "Directory to write redacted log file(s) to. Defaults to the current directory.");

        var exportCommand = new Command("export", "Export a redacted copy of a log file safe to share with Microsoft support");
        exportCommand.AddArgument(commandArg);
        exportCommand.AddOption(outputOption);

        exportCommand.SetHandler(async (InvocationContext context) =>
        {
            var commandName = context.ParseResult.GetValueForArgument(commandArg);
            var outputDir = context.ParseResult.GetValueForOption(outputOption);
            var ct = context.GetCancellationToken();

            var outputDirectory = outputDir ?? Environment.CurrentDirectory;

            if (!Directory.Exists(outputDirectory))
            {
                logger.LogError("Output directory does not exist: {Dir}", outputDirectory);
                context.ExitCode = 1;
                return;
            }

            var logsDir = ConfigService.GetLogsDirectory();

            IEnumerable<(string name, string logPath)> targets;

            if (!string.IsNullOrWhiteSpace(commandName))
            {
                targets = [(commandName, ConfigService.GetCommandLogPath(commandName))];
            }
            else
            {
                // Discover all a365.*.log files, excluding previously exported redacted copies
                // (a365.*.redacted.log). The glob alone matches both, so filter out names whose
                // base ends with ".redacted" (e.g. a365.setup.redacted) before processing.
                targets = Directory.EnumerateFiles(logsDir, "a365.*.log")
                    .Where(path => !Path.GetFileNameWithoutExtension(path)
                        .EndsWith(".redacted", StringComparison.OrdinalIgnoreCase))
                    .Select(path =>
                    {
                        var fileName = Path.GetFileNameWithoutExtension(path); // e.g. a365.setup
                        var name = fileName.StartsWith("a365.", StringComparison.OrdinalIgnoreCase)
                            ? fileName["a365.".Length..]
                            : fileName;
                        return (name, path);
                    });
            }

            var exported = 0;
            foreach (var (name, logPath) in targets)
            {
                if (!File.Exists(logPath))
                {
                    logger.LogWarning("No log found for '{CliCommand}'. Run the command first to generate one.", name);
                    continue;
                }

                try
                {
                    var content = await File.ReadAllTextAsync(logPath, ct);
                    var result = logRedactionService.Redact(content, logPath);

                    var outputFileName = $"a365.{name}.redacted.log";
                    var outputPath = Path.Combine(outputDirectory, outputFileName);
                    await File.WriteAllTextAsync(outputPath, result.RedactedContent, ct);

                    logger.LogInformation("Exporting redacted log for: {CliCommand}", name);
                    logger.LogInformation("  Redacted: {Emails} email(s), {Ids} id(s), {Tokens} JWT token(s)",
                        result.EmailsRedacted, result.IdsRedacted, result.TokensRedacted);
                    logger.LogInformation("  Output:   {OutputPath}", outputPath);
                    logger.LogInformation("");
                    exported++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    logger.LogError("Failed to export log for '{CliCommand}': {Message}", name, ex.Message);
                    context.ExitCode = 1;
                }
            }

            if (exported > 0)
            {
                logger.LogInformation("Share the redacted file(s) above when reporting issues.");
            }
            else if (string.IsNullOrWhiteSpace(commandName))
            {
                logger.LogWarning("No log files found in {LogsDir}. Run a command first.", logsDir);
                context.ExitCode = 1;
            }
        });

        return exportCommand;
    }
}
