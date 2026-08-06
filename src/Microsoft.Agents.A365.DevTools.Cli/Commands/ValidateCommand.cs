// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements;
using Microsoft.Agents.A365.DevTools.Cli.Services.Requirements.RequirementChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Validation;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands;

/// <summary>
/// Validates the local Agent 365 CLI configuration and prerequisite state.
/// Writes a structured report to a365.validate.json.
/// </summary>
public sealed class ValidateCommand
{
    internal const string ReportFileName = "a365.validate.json";

    // Status markers — plain ASCII for consistent rendering across terminals and log files
    private const string PassMark = "PASS";
    private const string FailMark = "FAIL";
    private const string WarnMark = "WARN";
    private const string SkipMark = "SKIP";

    private static readonly JsonSerializerOptions ReportSerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public static Command CreateCommand(
        ILogger<ValidateCommand> logger,
        IConfigService configService,
        PlatformDetector? platformDetector = null,
        CommandExecutor? commandExecutor = null,
        IProcessService? processService = null,
        AuthenticationService? authService = null,
        GraphApiService? graphApiService = null,
        AgentBlueprintService? agentBlueprintService = null,
        IEnumerable<IRequirementCheck>? requirementChecksOverride = null)
    {
        var command = new Command(CommandNames.Validate,
            "Validate the local Agent 365 CLI configuration and prerequisite state\n" +
            "Checks config validity, code health, and blueprint registration. Run 'a365 setup all' before using this command.");

        var playgroundOption = new Option<bool>(
            "--playground",
            "Launch AgentsPlayground after automated conversation turns for interactive testing");
        command.AddOption(playgroundOption);

        command.SetHandler(async (InvocationContext context) =>
        {
            var ct = context.GetCancellationToken();
            var cwd = Directory.GetCurrentDirectory();
            var configPath = Path.Combine(cwd, ConfigConstants.DefaultConfigFileName);
            var report = new ValidateReport();
            var launchPlayground = context.ParseResult.GetValueForOption(playgroundOption);
            var disposableChecks = new List<IDisposable>();

            try
            {
                // Phase 1: Config validation (structural tier)
                var (config, configOk) = await ValidateConfigAsync(configService, configPath, logger, report);

                if (!configOk || config is null)
                {
                    report.Summary = new SummaryResult { Ok = false, Blocker = "structural" };
                    context.ExitCode = 1;
                    return;
                }

                logger.LogDebug("Configuration file validated successfully");

                // Populate agent info from config
                var projectPath = ResolveProjectPath(config);
                var language = platformDetector?.Detect(projectPath);
                report.Agent = new AgentInfo
                {
                    Path = projectPath,
                    Language = language is not null and not ProjectPlatform.Unknown
                        ? language.Value.ToString().ToLowerInvariant()
                        : null
                };

                // --- Run all checks ---

                // Phase 2: Run structural checks (manifest + bearer token + build)
                var structuralChecks = requirementChecksOverride?.ToList()
                    ?? BuildStructuralChecks(platformDetector, commandExecutor);

                var results = await RunChecksDetailedAsync(structuralChecks, config, logger, ct);
                MapResultsToTiers(results, report);

                var structuralPassed = report.Tiers.Structural is { Skipped: true } or { Ok: true };

                // Extract resolved uv command from build step for boot and conversation steps
                var buildResultEntry = results
                    .FirstOrDefault(r => r.Check is ProjectBuildRequirementCheck);
                var resolvedUvCommand = (buildResultEntry.Result?.Metadata as RequirementCheckMetadata)?.ResolvedUvCommand;

                // Phase 2b: Run boot check only if structural and build passed
                var buildPassed = report.Tiers.Build is { Skipped: true } or { Ok: true };
                if (structuralPassed && buildPassed && requirementChecksOverride is null)
                {
                    var bootChecks = BuildBootChecks(platformDetector, processService, resolvedUvCommand);
                    disposableChecks.AddRange(bootChecks.OfType<IDisposable>());
                    if (bootChecks.Count > 0)
                    {
                        var bootResults = await RunChecksDetailedAsync(bootChecks, config, logger, ct);
                        MapResultsToTiers(bootResults, report);
                        results.AddRange(bootResults);
                    }
                }
                else if (!structuralPassed || !buildPassed)
                {
                    var skipReason = !structuralPassed ? "structural checks failed" : "build failed";
                    report.Tiers.Boot = new BootTierResult
                    {
                        Skipped = true,
                        Reason = skipReason
                    };
                }

                // Phase 2c: Run conversation check only if boot tier passed
                var bootPassed = report.Tiers.Boot is { Skipped: false, Ok: true };
                if (bootPassed && requirementChecksOverride is null)
                {
                    var conversationChecks = BuildConversationChecks(platformDetector, processService, launchPlayground, resolvedUvCommand);
                    disposableChecks.AddRange(conversationChecks.OfType<IDisposable>());
                    if (conversationChecks.Count > 0)
                    {
                        var conversationResults = await RunChecksDetailedAsync(conversationChecks, config, logger, ct);
                        MapResultsToTiers(conversationResults, report);
                        results.AddRange(conversationResults);

                        // Run telemetry check using agent's console log file
                        var conversationResult = conversationResults
                            .FirstOrDefault(r => r.Check is ConversationRequirementCheck);
                        var agentLogPath = (conversationResult.Result?.Metadata as RequirementCheckMetadata)?.AgentConsoleLogPath;
                        report.AgentConsoleLogFile = agentLogPath;
                        var telemetryCheck = new TelemetryRequirementCheck(agentLogPath);
                        var telemetryResults = await RunChecksDetailedAsync(
                            new List<IRequirementCheck> { telemetryCheck }, config, logger, ct);
                        MapResultsToTiers(telemetryResults, report);
                        results.AddRange(telemetryResults);
                    }
                }
                else if (!bootPassed)
                {
                    var skipReason = report.Tiers.Boot?.Reason ?? "boot tier failed";
                    report.Tiers.Conversation = new ConversationTierResult
                    {
                        Skipped = true,
                        Reason = skipReason
                    };
                    report.Tiers.Telemetry = new TelemetryTierResult
                    {
                        Skipped = true,
                        Reason = skipReason
                    };
                }

                // For test overrides, also map conversation checks
                if (requirementChecksOverride is not null)
                {
                    // Conversation checks from override are already in results via MapResultsToTiers
                }

                // Phase 3: Blueprint registration check (tenant-level)
                if (requirementChecksOverride is null && graphApiService is not null)
                {
                    var registrationCheck = new BlueprintRegistrationRequirementCheck(graphApiService, agentBlueprintService);
                    var registrationResults = await RunChecksDetailedAsync(
                        new List<IRequirementCheck> { registrationCheck }, config, logger, ct);
                    MapResultsToTiers(registrationResults, report);
                    results.AddRange(registrationResults);
                }

                // Phase 4: Build summary — any failed check is a blocker
                var anyFailed = results.Any(r => !r.Result.Passed);
                var blocker = FindBlocker(report.Tiers);
                report.Summary = new SummaryResult
                {
                    Ok = !anyFailed && blocker is null,
                    Blocker = blocker
                };

                context.ExitCode = report.Summary.Ok ? 0 : 1;

                // Print formatted summary to console
                PrintSummary(report, logger);
            }
            finally
            {
                await WriteReportAsync(report, cwd, logger);

                foreach (var disposable in disposableChecks)
                {
                    disposable.Dispose();
                }
            }
        });

        return command;
    }

    private static async Task<(Agent365Config? Config, bool Ok)> ValidateConfigAsync(
        IConfigService configService,
        string configPath,
        ILogger logger,
        ValidateReport report)
    {
        var structuralChecks = new List<StructuralCheck>();

        if (!await configService.ConfigExistsAsync(configPath))
        {
            structuralChecks.Add(new StructuralCheck { Name = "config-exists", Ok = false, Message = "a365.config.json not found" });
            report.Tiers.Structural = new StructuralTierResult { Ok = false, Checks = structuralChecks };

            logger.LogError("Fail: Configuration File");
            logger.LogInformation("  {Message}", "a365.config.json not found in the current directory.");
            logger.LogInformation("");
            logger.LogInformation("  {Step}", "Run 'a365 setup all --agent-name <name>' to set up first.");
            return (null, false);
        }

        structuralChecks.Add(new StructuralCheck { Name = "config-exists", Ok = true });

        Agent365Config config;
        try
        {
            config = await configService.LoadAsync(configPath);
        }
        catch (ConfigurationValidationException ex)
        {
            structuralChecks.Add(new StructuralCheck { Name = "config-format", Ok = false, Message = ex.IssueDescription });
            report.Tiers.Structural = new StructuralTierResult { Ok = false, Checks = structuralChecks };
            logger.LogError("Fail: Configuration File");
            logger.LogInformation("  {Message}", ex.IssueDescription);
            return (null, false);
        }
        catch (ConfigFileNotFoundException ex)
        {
            structuralChecks.Add(new StructuralCheck { Name = "config-format", Ok = false, Message = ex.IssueDescription });
            report.Tiers.Structural = new StructuralTierResult { Ok = false, Checks = structuralChecks };
            logger.LogError("Fail: Configuration File");
            logger.LogInformation("  {Message}", ex.IssueDescription);
            return (null, false);
        }
        catch (JsonException)
        {
            structuralChecks.Add(new StructuralCheck { Name = "config-format", Ok = false, Message = ErrorMessages.InvalidConfigFormat });
            report.Tiers.Structural = new StructuralTierResult { Ok = false, Checks = structuralChecks };
            logger.LogError("Fail: Configuration File");
            logger.LogInformation("  {Message}", ErrorMessages.InvalidConfigFormat);
            return (null, false);
        }

        structuralChecks.Add(new StructuralCheck { Name = "config-format", Ok = true });

        var configErrors = config.Validate();
        if (configErrors.Count > 0)
        {
            structuralChecks.Add(new StructuralCheck
            {
                Name = "config-schema",
                Ok = false,
                Message = string.Join("; ", configErrors)
            });
            report.Tiers.Structural = new StructuralTierResult { Ok = false, Checks = structuralChecks };

            logger.LogError("Fail: Configuration File");
            foreach (var error in configErrors)
            {
                logger.LogInformation("  {Message}", error);
            }
            logger.LogInformation("");
            logger.LogInformation("  {Step}", "Fix the configuration errors in a365.config.json and try again.");
            return (null, false);
        }

        structuralChecks.Add(new StructuralCheck { Name = "config-schema", Ok = true });

        report.Tiers.Structural = new StructuralTierResult
        {
            Ok = true,
            Checks = structuralChecks
        };

        return (config, true);
    }

    private static async Task<List<(IRequirementCheck Check, RequirementCheckResult Result)>> RunChecksDetailedAsync(
        List<IRequirementCheck> checks,
        Agent365Config config,
        ILogger logger,
        CancellationToken ct)
    {
        var results = new List<(IRequirementCheck Check, RequirementCheckResult Result)>();

        logger.LogDebug("Checking requirements...");

        // Pass NullLogger to checks to suppress their built-in logging.
        // Validate handles its own structured output via the report and PrintSummary.
        var checkLogger = NullLogger.Instance;

        foreach (var check in checks)
        {
            var result = await check.CheckAsync(config, checkLogger, ct);
            results.Add((check, result));
        }

        var passed = results.Count(r => r.Result.Passed && !r.Result.IsWarning);
        var warnings = results.Count(r => r.Result.IsWarning);
        var failed = results.Count(r => !r.Result.Passed);

        logger.LogDebug("Requirements: {Passed} passed, {Warning} warnings, {Failed} failed",
            passed, warnings, failed);

        return results;
    }

    private static void MapResultsToTiers(
        List<(IRequirementCheck Check, RequirementCheckResult Result)> results,
        ValidateReport report)
    {
        foreach (var (check, result) in results)
        {
            switch (check)
            {
                case ToolingManifestRequirementCheck:
                case BearerTokenRequirementCheck:
                    // Add to structural tier
                    var structural = report.Tiers.Structural;
                    if (structural.Skipped)
                    {
                        structural = new StructuralTierResult { Ok = true, Checks = new List<StructuralCheck>() };
                        report.Tiers.Structural = structural;
                    }
                    structural.Checks ??= new List<StructuralCheck>();
                    var checkName = check switch
                    {
                        ToolingManifestRequirementCheck => "tooling-manifest",
                        BearerTokenRequirementCheck => "bearer-token",
                        _ => check.Name.ToLowerInvariant().Replace(' ', '-')
                    };
                    structural.Checks.Add(new StructuralCheck
                    {
                        Name = checkName,
                        Ok = result.Passed,
                        Message = result.Passed ? result.Details : result.ErrorMessage
                    });
                    if (!result.Passed)
                    {
                        structural.Ok = false;
                    }
                    break;

                case ProjectBuildRequirementCheck:
                    if (result.IsWarning)
                    {
                        report.Tiers.Build = new BuildTierResult
                        {
                            Skipped = true,
                            Reason = result.ErrorMessage ?? result.Details
                        };
                    }
                    else
                    {
                        var buildMeta = result.Metadata as RequirementCheckMetadata;
                        report.Tiers.Build = new BuildTierResult
                        {
                            Ok = result.Passed,
                            ExitCode = buildMeta?.ExitCode,
                            ErrorSummary = result.Passed ? null : result.ErrorMessage,
                            BuildLogFile = buildMeta?.BuildLogFile
                        };
                    }
                    break;

                case LocalRuntimeRequirementCheck:
                    if (result.IsWarning)
                    {
                        report.Tiers.Boot = new BootTierResult
                        {
                            Skipped = true,
                            Reason = result.ErrorMessage ?? result.Details
                        };
                    }
                    else
                    {
                        var bootMeta = result.Metadata as RequirementCheckMetadata;
                        report.Tiers.Boot = new BootTierResult
                        {
                            Ok = result.Passed,
                            Port = bootMeta?.Port,
                            BootMs = bootMeta?.BootMs,
                            BootLogFile = bootMeta?.BootLogFile
                        };
                    }
                    break;

                case ConversationRequirementCheck:
                    if (result.IsWarning)
                    {
                        report.Tiers.Conversation = new ConversationTierResult
                        {
                            Skipped = true,
                            Reason = result.ErrorMessage ?? result.Details
                        };
                    }
                    else
                    {
                        var convMeta = result.Metadata as RequirementCheckMetadata;
                        report.Tiers.Conversation = new ConversationTierResult
                        {
                            Ok = result.Passed,
                            PlaygroundLaunched = convMeta?.PlaygroundLaunched,
                            ConversationLogFile = convMeta?.ConversationLogFile,
                            Turns = convMeta?.Turns?.Select(t => new ConversationTurnResult
                            {
                                Input = t.Input,
                                StatusCode = t.StatusCode,
                                ResponseSnippet = t.ResponseSnippet,
                                LatencyMs = t.LatencyMs,
                                Ok = t.Ok,
                                Error = t.Error,
                                AgentResponded = t.AgentResponded,
                                AgentResponseText = t.AgentResponseText
                            }).ToList()
                        };
                    }
                    break;

                case TelemetryRequirementCheck:
                    // ConsoleExporterActive is true if span blocks were found, even if some operations are missing.
                    // The Details field contains "Console exporter active" when spans were detected.
                    var exporterDetected = result.Details?.Contains("Console exporter active", StringComparison.OrdinalIgnoreCase) == true
                        || result.Details?.Contains("span(s)", StringComparison.OrdinalIgnoreCase) == true;

                    if (result.IsWarning)
                    {
                        report.Tiers.Telemetry = new TelemetryTierResult
                        {
                            Ok = true,
                            Warning = result.ErrorMessage,
                            ConsoleExporterActive = exporterDetected
                        };
                    }
                    else
                    {
                        report.Tiers.Telemetry = new TelemetryTierResult
                        {
                            Ok = result.Passed,
                            ConsoleExporterActive = exporterDetected || result.Passed
                        };

                        if (!result.Passed)
                        {
                            report.Tiers.Telemetry.Reason = result.ErrorMessage;
                        }
                    }
                    break;

                case BlueprintRegistrationRequirementCheck:
                    var blueprintTier = new BlueprintTierResult();
                    if (result.IsWarning)
                    {
                        blueprintTier.Ok = true;
                        blueprintTier.Warning = result.ErrorMessage;
                    }
                    else
                    {
                        blueprintTier.Ok = result.Passed;
                        blueprintTier.Reason = result.Passed ? null : result.ErrorMessage;
                    }

                    if (result.Metadata is RequirementCheckMetadata bpMeta)
                    {
                        blueprintTier.AppExists = bpMeta.AppExists;
                        blueprintTier.ServicePrincipalExists = bpMeta.ServicePrincipalExists;
                        blueprintTier.RegistrationExists = bpMeta.RegistrationExists;

                        if (bpMeta.ResourcePermissions is { Count: > 0 })
                        {
                            blueprintTier.Resources = bpMeta.ResourcePermissions.Select(rp =>
                                new BlueprintResourceResult
                                {
                                    ResourceName = rp.ResourceName,
                                    ResourceAppId = rp.ResourceAppId,
                                    ExpectedScopes = rp.ExpectedScopes,
                                    ActualScopes = rp.ActualScopes,
                                    MissingScopes = rp.MissingScopes.Count > 0 ? rp.MissingScopes : null,
                                    ConsentGranted = rp.ConsentGranted,
                                    InheritablePermissionsConfigured = rp.InheritablePermissionsConfigured,
                                    ScopesAllAllowed = rp.ScopesAllAllowed,
                                    RolesAllAllowed = rp.RolesAllAllowed,
                                    ActualAppRoles = rp.ActualAppRoles.Count > 0 ? rp.ActualAppRoles : null,
                                    EffectiveInheritance = rp.EffectiveInheritance
                                }).ToList();
                        }
                    }

                    report.Tiers.Blueprint = blueprintTier;
                    break;
            }
        }
    }

    private static (string Description, string Suggestion) GetCodeHealthFailureInfo(ValidationTiers tiers)
    {
        var buildFailed = tiers.Build is { Skipped: false, Ok: false };
        var structuralFailed = tiers.Structural is { Skipped: false, Ok: false };

        if (buildFailed)
        {
            var buildLogFile = tiers.Build is BuildTierResult bt ? bt.BuildLogFile : null;
            var suggestion = buildLogFile is not null
                ? $"fix build errors, see: {buildLogFile}"
                : "fix build errors and re-run `a365 validate`";
            return ("build failed", suggestion);
        }

        if (structuralFailed)
        {
            var failedChecks = tiers.Structural is StructuralTierResult st
                ? st.Checks?.Where(c => !c.Ok).Select(c => c.Name).ToList()
                : null;

            var desc = failedChecks is { Count: > 0 }
                ? $"failed: {string.Join(", ", failedChecks)}"
                : "structural checks failed";

            var suggestion = failedChecks?.Contains("bearer-token") == true
                ? "run `a365 develop get-token` to retrieve a bearer token and add it to your launch settings"
                : "fix project structure issues and re-run `a365 validate`";

            return (desc, suggestion);
        }

        return ("code health check failed", "fix errors and re-run `a365 validate`");
    }

    private static string? FindBlocker(ValidationTiers tiers)
    {
        if (tiers.Structural is { Skipped: false, Ok: false }) return "structural";
        if (tiers.Build is { Skipped: false, Ok: false }) return "build";
        if (tiers.Boot is { Skipped: false, Ok: false }) return "boot";
        if (tiers.Conversation is { Skipped: false, Ok: false }) return "conversation";
        if (tiers.Telemetry is { Skipped: false, Ok: false }) return "telemetry";
        if (tiers.Blueprint is { Skipped: false, Ok: false }) return "blueprint";
        return null;
    }

    internal static void PrintSummary(ValidateReport report, ILogger logger)
    {
        logger.LogInformation("");

        // Group related tiers into user-facing rows
        var rows = BuildDisplayRows(report);

        int passCount = 0;
        int failCount = 0;
        int warnCount = 0;
        int localChecks = 0;

        foreach (var row in rows)
        {
            if (row.Skipped)
            {
                var reason = row.Reason ?? "not configured";
                logger.LogInformation("  {Skip}  {Name,-20} skipped ({Reason})", SkipMark, row.Label, reason);
            }
            else if (row.IsWarning)
            {
                warnCount++;
                localChecks++;
                logger.LogInformation("  {Warn} {Name,-20} {Description}", WarnMark, row.Label, row.Description);

                if (row.Suggestion is not null)
                {
                    logger.LogInformation("       -> suggestion: {Suggestion}", row.Suggestion);
                }
            }
            else if (row.Ok)
            {
                passCount++;
                localChecks++;
                logger.LogInformation("  {Pass} {Name,-20} {Description}", PassMark, row.Label, row.Description);
            }
            else
            {
                failCount++;
                localChecks++;
                logger.LogInformation("  {Fail} {Name,-20} {Description}", FailMark, row.Label, row.Description);

                if (row.Suggestion is not null)
                {
                    logger.LogInformation("       -> suggestion: {Suggestion}", row.Suggestion);
                }
            }
        }

        logger.LogInformation("");

        if (failCount == 0 && localChecks > 0)
        {
            var warnSuffix = warnCount > 0 ? $" ({warnCount} warning(s))" : "";
            logger.LogInformation("  All {PassCount} checks passed.{WarnSuffix}", passCount, warnSuffix);
        }
        else if (failCount > 0)
        {
            logger.LogInformation(
                "  {FailCount} of {LocalChecks} checks failed.",
                failCount, localChecks);
        }

        logger.LogInformation("");
    }

    private static List<DisplayRow> BuildDisplayRows(ValidateReport report)
    {
        var rows = new List<DisplayRow>();
        var tiers = report.Tiers;

        // Row 1: Code health (structural + build + manifest)
        var codeHealthTiers = new[] { tiers.Structural, tiers.Build as TierResult };
        var codeHealthActive = codeHealthTiers.Where(t => !t.Skipped).ToList();
        if (codeHealthActive.Count > 0)
        {
            var allOk = codeHealthActive.All(t => t.Ok == true);
            string description;
            string? suggestion = null;

            if (allOk)
            {
                description = "project structure, manifest, build";
            }
            else
            {
                var (desc, sug) = GetCodeHealthFailureInfo(tiers);
                description = desc;
                suggestion = sug;
            }

            rows.Add(new DisplayRow
            {
                Label = "Code health",
                Ok = allOk,
                Description = description,
                Suggestion = suggestion
            });
        }
        else
        {
            rows.Add(new DisplayRow { Label = "Code health", Skipped = true, Reason = "not configured" });
        }

        // Row 2: Boot (api/health)
        if (!tiers.Boot.Skipped)
        {
            var bootOk = tiers.Boot.Ok == true;
            rows.Add(new DisplayRow
            {
                Label = "Runs locally",
                Ok = bootOk,
                Description = bootOk
                    ? $"/api/health OK{(tiers.Boot is BootTierResult b && b.Port is not null ? $" (port {b.Port})" : "")}"
                    : "health check failed",
                Suggestion = bootOk ? null : "ensure the agent starts locally with `dotnet run` or `npm start`"
            });
        }
        else
        {
            rows.Add(new DisplayRow
            {
                Label = "Runs locally",
                Skipped = true,
                Reason = tiers.Boot.Reason ?? "boot skipped"
            });
        }

        // Row 3: Conversation
        if (!tiers.Conversation.Skipped)
        {
            var conv = tiers.Conversation;
            var convOk = conv.Ok == true;
            var turnCount = conv.Turns?.Count ?? 0;
            var respondedCount = conv.Turns?.Count(t => t.AgentResponded == true) ?? 0;
            var failedCount = conv.Turns?.Count(t => !t.Ok) ?? 0;

            rows.Add(new DisplayRow
            {
                Label = "Conversation",
                Ok = convOk,
                Description = convOk
                    ? $"{turnCount}-turn conversation OK, {respondedCount} agent responses"
                    : $"{turnCount}-turn conversation, {failedCount} failed",
                Suggestion = convOk ? null : "check agent logs or a365.validate.json for details"
            });
        }
        else
        {
            rows.Add(new DisplayRow
            {
                Label = "Conversation",
                Skipped = true,
                Reason = tiers.Conversation.Reason ?? "boot tier failed"
            });
        }

        // Row 4: Telemetry
        var telemetry = tiers.Telemetry;
        if (!telemetry.Skipped)
        {
            var telOk = telemetry.Ok == true;
            string telDesc;
            string? telSuggestion = null;

            if (telOk && telemetry.Warning is not null)
            {
                // Warning state: console exporter not detected
                telDesc = telemetry.Warning;
                telSuggestion = "configure OpenTelemetry console exporter to output traces";
            }
            else if (telOk)
            {
                telDesc = "console exporter active, all GenAI operation spans detected";
            }
            else
            {
                telDesc = telemetry.Reason ?? "telemetry check failed";
                telSuggestion = "ensure Agent365Sdk console exporter is enabled with invoke_agent, chat, and execute_tool spans";
            }

            rows.Add(new DisplayRow
            {
                Label = "Telemetry",
                Ok = telOk && telemetry.Warning is null,
                Description = telDesc,
                Suggestion = telSuggestion,
                IsWarning = telemetry.Warning is not null
            });
        }
        else
        {
            rows.Add(new DisplayRow
            {
                Label = "Telemetry",
                Skipped = true,
                Reason = telemetry.Reason ?? "not yet run"
            });
        }

        rows.Add(CreateTierRow("Registered", tiers.Blueprint,
            "blueprint registered in Entra ID",
            tiers.Blueprint.Reason?.Contains("permissions/consent", StringComparison.OrdinalIgnoreCase) == true
                ? "run 'a365 setup permissions' to configure inheritable permissions"
                : "run 'a365 setup blueprint' to register the blueprint"));

        return rows;
    }

    private static DisplayRow CreateTierRow(string label, TierResult tier, string description, string? suggestion)
    {
        if (tier.Skipped)
        {
            return new DisplayRow { Label = label, Skipped = true, Reason = tier.Reason ?? "not yet implemented" };
        }

        return new DisplayRow
        {
            Label = label,
            Ok = tier.Ok == true,
            Description = tier.Ok == true ? description : (tier.Reason ?? tier.Warning ?? "check failed"),
            Suggestion = tier.Ok == true ? null : suggestion
        };
    }

    private sealed class DisplayRow
    {
        public string Label { get; init; } = string.Empty;
        public bool Skipped { get; init; }
        public string? Reason { get; init; }
        public bool Ok { get; init; }
        public bool IsWarning { get; init; }
        public string? Description { get; init; }
        public string? Suggestion { get; init; }
    }

    private static async Task WriteReportAsync(ValidateReport report, string directory, ILogger logger)
    {
        try
        {
            var reportPath = Path.Combine(directory, ReportFileName);
            var json = JsonSerializer.Serialize(report, ReportSerializerOptions);
            await File.WriteAllTextAsync(reportPath, json);
            logger.LogInformation("Report written to {ReportPath}", reportPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write validation report");
        }
    }

    private static string ResolveProjectPath(Agent365Config config)
    {
        return string.IsNullOrWhiteSpace(config.DeploymentProjectPath)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(config.DeploymentProjectPath);
    }

    private static List<IRequirementCheck> BuildStructuralChecks(
        PlatformDetector? platformDetector,
        CommandExecutor? commandExecutor)
    {
        var checks = new List<IRequirementCheck>
        {
            new ToolingManifestRequirementCheck()
        };

        if (platformDetector is not null)
        {
            checks.Add(new BearerTokenRequirementCheck(platformDetector));
        }

        if (platformDetector is not null && commandExecutor is not null)
        {
            checks.Add(new ProjectBuildRequirementCheck(platformDetector, commandExecutor));
        }

        return checks;
    }

    private static List<IRequirementCheck> BuildBootChecks(
        PlatformDetector? platformDetector,
        IProcessService? processService,
        string? resolvedUvCommand = null)
    {
        var checks = new List<IRequirementCheck>();

        if (platformDetector is not null && processService is not null)
        {
            checks.Add(new LocalRuntimeRequirementCheck(platformDetector, processService,
                resolvedUvCommand: resolvedUvCommand));
        }

        return checks;
    }

    private static List<IRequirementCheck> BuildConversationChecks(
        PlatformDetector? platformDetector,
        IProcessService? processService,
        bool launchPlayground = false,
        string? resolvedUvCommand = null)
    {
        var checks = new List<IRequirementCheck>();

        if (platformDetector is not null && processService is not null)
        {
            checks.Add(new ConversationRequirementCheck(
                platformDetector, processService, launchPlayground: launchPlayground,
                resolvedUvCommand: resolvedUvCommand));
        }

        return checks;
    }
}