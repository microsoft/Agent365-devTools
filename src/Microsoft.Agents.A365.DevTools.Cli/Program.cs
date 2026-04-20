// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Agents.A365.DevTools.Cli.Services.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Reflection;

namespace Microsoft.Agents.A365.DevTools.Cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // Detect which command is being run for log file naming
        var commandName = DetectCommandName(args);
        var logFilePath = ConfigService.GetCommandLogPath(commandName);

        // Check if verbose flag is present to adjust logging level
        var isVerbose = args.Contains("--verbose") || args.Contains("-v");
        var logLevel = isVerbose ? LogLevel.Debug : LogLevel.Information;

        // Configure Microsoft.Extensions.Logging with clean console formatter
        var loggerFactory = LoggerFactoryHelper.CreateCleanLoggerFactory(logLevel);
        var startupLogger = loggerFactory.CreateLogger("Program");

        try
        {
            // Log startup info (debug level - not shown to users by default on console, but always in log file)
            startupLogger.LogDebug("==========================================================");
            startupLogger.LogDebug("Agent 365 CLI - Command: {Command}", commandName);
            startupLogger.LogDebug("Version: {Version}", GetDisplayVersion());
            startupLogger.LogDebug("Log file: {LogFile}", logFilePath);
            startupLogger.LogDebug("Started at: {Time}", DateTime.Now);
            startupLogger.LogDebug("==========================================================");

            // Log version information
            var version = GetDisplayVersion();

            // Set up dependency injection
            var services = new ServiceCollection();
            ConfigureServices(services, logLevel, logFilePath);
            var serviceProvider = services.BuildServiceProvider();

            // Notice and version checks run concurrently — worst-case startup delay is ~2s, not ~4s.
            using var noticeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var versionCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            var noticeService = serviceProvider.GetRequiredService<INoticeService>();
            var versionCheckService = serviceProvider.GetRequiredService<IVersionCheckService>();

            var noticeTask = noticeService.CheckForNoticeAsync(noticeCts.Token);
            var versionTask = versionCheckService.CheckForUpdatesAsync(versionCts.Token);

            await Task.WhenAll(
                noticeTask.ContinueWith(_ => { }, TaskContinuationOptions.None),
                versionTask.ContinueWith(_ => { }, TaskContinuationOptions.None));

            // Display notice result
            try
            {
                var noticeResult = await noticeTask;
                if (noticeResult.HasNotice)
                {
                    const string separator = "------------------------------------------------------------";
                    startupLogger.LogWarning("");
                    startupLogger.LogWarning(separator);
                    startupLogger.LogWarning("URGENT NOTICE");
                    startupLogger.LogWarning(separator);
                    startupLogger.LogWarning("{Message}", noticeResult.Message);
                    startupLogger.LogWarning("");
                    startupLogger.LogWarning("To update, run: {Command}", noticeResult.UpdateCommand);
                    startupLogger.LogWarning(separator);
                    startupLogger.LogWarning("");
                }
            }
            catch (OperationCanceledException)
            {
                startupLogger.LogDebug("Notice check timed out");
            }
            catch (Exception ex)
            {
                startupLogger.LogDebug(ex, "Notice check failed: {Message}", ex.Message);
            }

            // Display version check result
            try
            {
                var result = await versionTask;
                if (result.UpdateAvailable)
                {
                    startupLogger.LogWarning("");
                    startupLogger.LogWarning("A newer version is available with bug fixes and improvements.");
                    startupLogger.LogWarning("  Current: {Current}", result.CurrentVersion);
                    startupLogger.LogWarning("  Latest:  {Latest}", result.LatestVersion);
                    startupLogger.LogWarning("");
                    startupLogger.LogWarning("What's new: https://github.com/microsoft/Agent365-devTools/releases");
                    startupLogger.LogWarning("To update, run: {Command}", result.UpdateCommand);
                    startupLogger.LogWarning("");
                }
            }
            catch (OperationCanceledException)
            {
                startupLogger.LogDebug("Version check timed out");
            }
            catch (Exception ex)
            {
                startupLogger.LogDebug(ex, "Version check failed: {Message}", ex.Message);
            }

            // Create root command
            var rootCommand = new RootCommand($"Agent 365 Developer Tools CLI v{version} – Build, deploy, and manage AI agents for Microsoft 365.");

            // Get loggers and services
            var setupLogger = serviceProvider.GetRequiredService<ILogger<SetupCommand>>();
            var createInstanceLogger = serviceProvider.GetRequiredService<ILogger<CreateInstanceCommand>>();
            var deployLogger = serviceProvider.GetRequiredService<ILogger<DeployCommand>>();
            var queryEntraLogger = serviceProvider.GetRequiredService<ILogger<QueryEntraCommand>>();
            var cleanupLogger = serviceProvider.GetRequiredService<ILogger<CleanupCommand>>();
            var publishLogger = serviceProvider.GetRequiredService<ILogger<PublishCommand>>();
            var developLogger = serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();
            var configService = serviceProvider.GetRequiredService<IConfigService>();
            var executor = serviceProvider.GetRequiredService<CommandExecutor>();
            var authService = serviceProvider.GetRequiredService<AuthenticationService>();
            var azureAuthValidator = serviceProvider.GetRequiredService<AzureAuthValidator>();
            var toolingService = serviceProvider.GetRequiredService<IAgent365ToolingService>();

            // Get services needed by commands
            services.AddSingleton<IMicrosoftGraphTokenProvider, MicrosoftGraphTokenProvider>();
            var deploymentService = serviceProvider.GetRequiredService<DeploymentService>();
            var botConfigurator = serviceProvider.GetRequiredService<IBotConfigurator>();
            var graphApiService = serviceProvider.GetRequiredService<GraphApiService>();
            var armApiService = serviceProvider.GetRequiredService<ArmApiService>();
            var agentBlueprintService = serviceProvider.GetRequiredService<AgentBlueprintService>();
            var blueprintLookupService = serviceProvider.GetRequiredService<BlueprintLookupService>();
            var federatedCredentialService = serviceProvider.GetRequiredService<FederatedCredentialService>();
            var platformDetector = serviceProvider.GetRequiredService<PlatformDetector>();
            var processService = serviceProvider.GetRequiredService<IProcessService>();
            var clientAppValidator = serviceProvider.GetRequiredService<IClientAppValidator>();

            // Add commands
            rootCommand.AddCommand(DevelopCommand.CreateCommand(developLogger, configService, executor, authService, graphApiService, agentBlueprintService, processService));
            rootCommand.AddCommand(DevelopMcpCommand.CreateCommand(developLogger, toolingService));
            var confirmationProvider = serviceProvider.GetRequiredService<IConfirmationProvider>();
            rootCommand.AddCommand(SetupCommand.CreateCommand(setupLogger, configService, executor,
                deploymentService, botConfigurator, azureAuthValidator, platformDetector, graphApiService, agentBlueprintService, blueprintLookupService, federatedCredentialService, clientAppValidator, confirmationProvider, armApiService));
            rootCommand.AddCommand(CreateInstanceCommand.CreateCommand(createInstanceLogger, configService, executor,
                graphApiService));
            rootCommand.AddCommand(DeployCommand.CreateCommand(deployLogger, configService, executor,
                deploymentService, azureAuthValidator, graphApiService, agentBlueprintService));

            // Register ConfigCommand
            var configLoggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var configLogger = configLoggerFactory.CreateLogger("ConfigCommand");
            var wizardService = serviceProvider.GetRequiredService<IConfigurationWizardService>();
            var manifestTemplateService = serviceProvider.GetRequiredService<ManifestTemplateService>();
            rootCommand.AddCommand(ConfigCommand.CreateCommand(configLogger, wizardService: wizardService, clientAppValidator: clientAppValidator));
            rootCommand.AddCommand(QueryEntraCommand.CreateCommand(queryEntraLogger, configService, executor, graphApiService, agentBlueprintService));
            rootCommand.AddCommand(CleanupCommand.CreateCommand(cleanupLogger, configService, botConfigurator, executor, agentBlueprintService, confirmationProvider, federatedCredentialService, azureAuthValidator, graphApiService));
            rootCommand.AddCommand(PublishCommand.CreateCommand(publishLogger, configService, manifestTemplateService, graphApiService));

            // Wrap all command handlers with exception handling
            // Build with middleware for global exception handling
            var builder = new CommandLineBuilder(rootCommand)
                .UseDefaults()
                .UseExceptionHandler((exception, context) =>
                {
                    if (exception is CleanExitException cleanExit)
                    {
                        context.ExitCode = cleanExit.ExitCode;
                    }
                    else if (exception is OperationCanceledException)
                    {
                        context.ExitCode = 1;
                    }
                    else if (exception is Agent365Exception myEx)
                    {
                        ExceptionHandler.HandleAgent365Exception(myEx, logFilePath: logFilePath);
                        context.ExitCode = myEx.ExitCode;
                    }
                    else
                    {
                        // Unexpected error - this is a BUG
                        startupLogger.LogCritical(exception, "Application terminated unexpectedly");
                        Console.Error.WriteLine("Unexpected error occurred. This may be a bug in the CLI.");
                        Console.Error.WriteLine("Please report this issue at: https://github.com/microsoft/Agent365-devTools/issues");
                        Console.Error.WriteLine();
                        if (!string.IsNullOrEmpty(logFilePath))
                        {
                            Console.Error.WriteLine($"For more details, see the log file at: {logFilePath}");
                            Console.Error.WriteLine();
                        }
                        context.ExitCode = 1;
                    }
                });

            // Validate the configured clientAppId still exists in the tenant before any command runs.
            // If not found, falls back to the well-known display name and patches a365.config.json.
            // Skip for help/version requests — these never make Graph calls and must work offline.
            var isHelpOrVersion = args.Length == 0
                || args.Any(a => a is "--help" or "-h" or "--version");
            if (!isHelpOrVersion)
            {
                try
                {
                    await configService.TryResolveClientAppIdAsync(graphApiService);
                }
                catch (Exception ex)
                {
                    startupLogger.LogDebug(ex, "Client app ID pre-resolution skipped: {Message}", ex.Message);
                }
            }

            var parser = builder.Build();
            return await parser.InvokeAsync(args);
        }
        catch (Exception ex)
        {
            // Catch anything that escapes before or after the System.CommandLine pipeline
            // (e.g. DI setup failures, exceptions in InvokeAsync itself).
            // Log the full details to the file; show only a clean one-liner to the user.
            startupLogger.LogCritical(ex, "Unhandled exception in CLI startup");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.ResetColor();
            Console.Error.WriteLine();
            if (!string.IsNullOrEmpty(logFilePath))
                Console.Error.WriteLine($"For details, see the log file at: {logFilePath}");
            Console.Error.WriteLine("If this error persists, please report it at: https://github.com/microsoft/Agent365-devTools/issues");
            return 1;
        }
        finally
        {
            Console.ResetColor();
            loggerFactory.Dispose();
        }
    }

    private static void ConfigureServices(IServiceCollection services, LogLevel minimumLevel = LogLevel.Information, string? logFilePath = null)
    {
        // Add logging with clean console formatter and optional file logging
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(minimumLevel);

            // Console logging with clean formatter
            builder.AddConsoleFormatter<CleanConsoleFormatter, Microsoft.Extensions.Logging.Console.SimpleConsoleFormatterOptions>();
            builder.AddConsole(options =>
            {
                options.FormatterName = "clean";
            });

            // File logging if path provided
            if (!string.IsNullOrEmpty(logFilePath))
            {
                // Always use Trace level for file logging to capture all diagnostic information
                // This ensures comprehensive logs for debugging, regardless of console verbosity
                builder.Services.AddSingleton<ILoggerProvider>(provider =>
                    new FileLoggerProvider(logFilePath));
            }
        });

        // Add core services
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<CommandExecutor>();
        services.AddSingleton<AuthenticationService>();
        services.AddSingleton<IAuthenticationService>(sp => sp.GetRequiredService<AuthenticationService>());
        services.AddSingleton<IClientAppValidator, ClientAppValidator>();
        services.AddSingleton<IVersionCheckService, VersionCheckService>();
        services.AddSingleton<INoticeService, NoticeService>();

        // Add Microsoft Agent 365 Tooling Service with environment detection
        services.AddSingleton<IAgent365ToolingService>(provider =>
        {
            var configService = provider.GetRequiredService<IConfigService>();
            var authService = provider.GetRequiredService<AuthenticationService>();
            var logger = provider.GetRequiredService<ILogger<Agent365ToolingService>>();

            // Determine environment: try to load from config if --config option is provided, otherwise default to prod
            string environment = "prod"; // Default

            // Check if --config argument was provided (for internal developers)
            var args = Environment.GetCommandLineArgs();
            var configIndex = Array.FindIndex(args, arg => arg == "--config" || arg == "-c");
            if (configIndex >= 0 && configIndex < args.Length - 1)
            {
                try
                {
                    // Try to load config file to get environment
                    var config = configService.LoadAsync(args[configIndex + 1]).Result;
                    environment = config.Environment;
                }
                catch
                {
                    // If config loading fails, stick with default "prod"
                    // This is fine - the service will work with default environment
                }
            }

            return new Agent365ToolingService(configService, authService, logger, environment);
        });

        // Add Azure validators (individual validators for composition)
        services.AddSingleton<AzureAuthValidator>();
        services.AddSingleton<IAzureEnvironmentValidator, AzureEnvironmentValidator>();


        // Add multi-platform deployment services
        services.AddSingleton<PlatformDetector>();
        services.AddSingleton<DeploymentService>();

        // Add other services
        services.AddSingleton<IBotConfigurator, BotConfigurator>();

        // Register process executor adapter and Microsoft Graph token provider before GraphApiService
        services.AddSingleton<IMicrosoftGraphTokenProvider, MicrosoftGraphTokenProvider>();

        services.AddSingleton<GraphApiService>();
        services.AddSingleton<ArmApiService>();
        services.AddSingleton<AgentBlueprintService>();
        services.AddSingleton<BlueprintLookupService>();
        services.AddSingleton<FederatedCredentialService>();
        services.AddSingleton<DelegatedConsentService>(); // For AgentApplication.Create permission
        services.AddSingleton<ManifestTemplateService>(); // For publish command template extraction

        // Register ProcessService for cross-platform process launching
        services.AddSingleton<IProcessService, ProcessService>();

        // Register Azure CLI service and Configuration Wizard
        services.AddSingleton<IAzureCliService, AzureCliService>();
        services.AddSingleton<IConfigurationWizardService, ConfigurationWizardService>();
        
        // Register confirmation provider for user prompts
        services.AddSingleton<IConfirmationProvider, ConsoleConfirmationProvider>();
    }

    public static string GetDisplayVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Fallback: AssemblyVersion if InformationalVersion is missing
        return infoVer ?? asm.GetName().Version?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Detects which command is being executed from command-line arguments.
    /// Used for command-specific log file naming.
    /// </summary>
    private static string DetectCommandName(string[] args)
    {
        if (args.Length == 0)
            return "default";

        // First non-option argument is typically the command
        // Skip arguments starting with - or --
        var command = args.FirstOrDefault(arg => !arg.StartsWith("-"));

        if (string.IsNullOrWhiteSpace(command))
            return "default";

        // Normalize command name for file system
        return command.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");
    }
}

