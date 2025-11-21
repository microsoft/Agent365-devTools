// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Commands;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
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
        
        // Configure Serilog with both console and file output
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(isVerbose ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
            .WriteTo.Logger(lc => lc
                .Filter.ByExcluding(e => e.Level >= LogEventLevel.Error)  // ✅ Exclude Error/Fatal from console
                .WriteTo.Console())
            .WriteTo.File(      // File output (for debugging)
                path: logFilePath,
                rollingInterval: RollingInterval.Infinite,
                rollOnFileSizeLimit: false,
                fileSizeLimitBytes: 10_485_760,  // 10 MB max
                retainedFileCountLimit: 1,       // Only keep latest run
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}")
            .CreateLogger();

        try
        {
            // Log startup info to file only (debug level - not shown to users by default)
            Log.Debug("==========================================================");
            Log.Debug("Agent 365 CLI - Command: {Command}", commandName);
            Log.Debug("Version: {Version}", GetDisplayVersion());
            Log.Debug("Log file: {LogFile}", logFilePath);
            Log.Debug("Started at: {Time}", DateTime.Now);
            Log.Debug("==========================================================");
            
            // Log version information
            var version = GetDisplayVersion();

            // Set up dependency injection
            var services = new ServiceCollection();
            ConfigureServices(services);
            var serviceProvider = services.BuildServiceProvider();

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
            var azureValidator = serviceProvider.GetRequiredService<IAzureValidator>();
            var toolingService = serviceProvider.GetRequiredService<IAgent365ToolingService>();

            // Get services needed by commands
            services.AddSingleton<IMicrosoftGraphTokenProvider, MicrosoftGraphTokenProvider>();
            var deploymentService = serviceProvider.GetRequiredService<DeploymentService>();
            var botConfigurator = serviceProvider.GetRequiredService<IBotConfigurator>();
            var graphApiService = serviceProvider.GetRequiredService<GraphApiService>();
            var webAppCreator = serviceProvider.GetRequiredService<AzureWebAppCreator>();
            var platformDetector = serviceProvider.GetRequiredService<PlatformDetector>();

            // Add commands
            rootCommand.AddCommand(DevelopCommand.CreateCommand(developLogger, configService, executor, authService));
            rootCommand.AddCommand(DevelopMcpCommand.CreateCommand(developLogger, toolingService));
            rootCommand.AddCommand(SetupCommand.CreateCommand(setupLogger, configService, executor, 
                deploymentService, botConfigurator, azureValidator, webAppCreator, platformDetector, graphApiService));
            rootCommand.AddCommand(CreateInstanceCommand.CreateCommand(createInstanceLogger, configService, executor,
                botConfigurator, graphApiService, azureValidator));
            rootCommand.AddCommand(DeployCommand.CreateCommand(deployLogger, configService, executor,
                deploymentService, azureValidator, graphApiService));

            // Register ConfigCommand
            var configLoggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var configLogger = configLoggerFactory.CreateLogger("ConfigCommand");
            var wizardService = serviceProvider.GetRequiredService<IConfigurationWizardService>();
            var manifestTemplateService = serviceProvider.GetRequiredService<ManifestTemplateService>();
            rootCommand.AddCommand(ConfigCommand.CreateCommand(configLogger, wizardService: wizardService));
            rootCommand.AddCommand(QueryEntraCommand.CreateCommand(queryEntraLogger, configService, executor, graphApiService));
            rootCommand.AddCommand(CleanupCommand.CreateCommand(cleanupLogger, configService, botConfigurator, executor));
            rootCommand.AddCommand(PublishCommand.CreateCommand(publishLogger, configService, graphApiService, manifestTemplateService));

            // Wrap all command handlers with exception handling
            // Build with middleware for global exception handling
            var builder = new CommandLineBuilder(rootCommand)
                .UseDefaults()
                .UseExceptionHandler((exception, context) =>
                {
                    if (exception is Agent365Exception myEx)
                    {
                        ExceptionHandler.HandleAgent365Exception(myEx);
                        context.ExitCode = myEx.ExitCode;
                    }
                    else
                    {
                        // Unexpected error - this is a BUG
                        Log.Fatal(exception, "Application terminated unexpectedly");
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("Unexpected error occurred. This may be a bug in the CLI.");
                        Console.Error.WriteLine("Please report this issue at: https://github.com/microsoft/Agent365-devTools/issues");
                        Console.Error.WriteLine();
                        context.ExitCode = 1;
                    }
                });

            var parser = builder.Build();
            return await parser.InvokeAsync(args);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Add logging
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: false); // Prevent Serilog from disposing the console
        });

        // Add core services
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<CommandExecutor>();
        services.AddSingleton<AuthenticationService>();
        
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
        
        // Add unified Azure validator
        services.AddSingleton<IAzureValidator, AzureValidator>();
        
        // Add multi-platform deployment services
        services.AddSingleton<PlatformDetector>();
        services.AddSingleton<DeploymentService>();

        // Add other services
        services.AddSingleton<IBotConfigurator, BotConfigurator>();

        // Register process executor adapter and Microsoft Graph token provider before GraphApiService
        services.AddSingleton<IMicrosoftGraphTokenProvider, MicrosoftGraphTokenProvider>();

        services.AddSingleton<GraphApiService>();
        services.AddSingleton<DelegatedConsentService>(); // For AgentApplication.Create permission
        services.AddSingleton<ManifestTemplateService>(); // For publish command template extraction
        
        // Register AzureWebAppCreator for SDK-based web app creation
        services.AddSingleton<AzureWebAppCreator>();
        
        // Register Azure CLI service and Configuration Wizard
        services.AddSingleton<IAzureCliService, AzureCliService>();
        services.AddSingleton<IConfigurationWizardService, ConfigurationWizardService>();
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

