// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// Bundles the mutable step state and shared services for setup orchestration.
/// Passed to each extracted step method so state flows cleanly between steps
/// without scattered local variables.
///
/// <see cref="Config"/> is intentionally mutable — the blueprint step reloads
/// configuration from disk after writing <c>AgentBlueprintId</c>, and the updated
/// instance must be visible to subsequent steps.
/// </summary>
internal sealed class SetupContext
{
    /// <summary>Mutable config — reloaded by blueprint step after it writes to disk.</summary>
    public Agent365Config Config { get; set; }

    /// <summary>Per-step result tracking for summary display.</summary>
    public SetupResults Results { get; }

    public ILogger Logger { get; }

    /// <summary>The static config file (a365.config.json).</summary>
    public FileInfo ConfigFile { get; }

    /// <summary>Full path to a365.generated.config.json.</summary>
    public string GeneratedConfigPath { get; }

    /// <summary>Correlation ID generated at workflow entry for distributed tracing.</summary>
    public string CorrelationId { get; }

    /// <summary>When true, Step 1 (infrastructure) is skipped. Always true for non-DW blueprint.</summary>
    public bool SkipInfrastructure { get; }

    /// <summary>When true, requirements validation is skipped.</summary>
    public bool SkipRequirements { get; }

    /// <summary>
    /// Overrides the az CLI login hint resolver used during blueprint creation.
    /// Null in production — injected as a no-op in tests to avoid spawning 'az account show'.
    /// </summary>
    public Func<Task<string?>>? LoginHintResolver { get; }

    public CancellationToken CancellationToken { get; }

    // Services
    public IConfigService ConfigService { get; }
    public CommandExecutor Executor { get; }
    public IBotConfigurator BotConfigurator { get; }
    public AzureAuthValidator AuthValidator { get; }
    public PlatformDetector PlatformDetector { get; }
    public GraphApiService GraphApiService { get; }
    public AgentBlueprintService BlueprintService { get; }
    public BlueprintLookupService BlueprintLookupService { get; }
    public FederatedCredentialService FederatedCredentialService { get; }
    public IClientAppValidator ClientAppValidator { get; }

    public SetupContext(
        Agent365Config config,
        SetupResults results,
        ILogger logger,
        FileInfo configFile,
        string generatedConfigPath,
        string correlationId,
        bool skipInfrastructure,
        bool skipRequirements,
        CancellationToken cancellationToken,
        IConfigService configService,
        CommandExecutor executor,
        IBotConfigurator botConfigurator,
        AzureAuthValidator authValidator,
        PlatformDetector platformDetector,
        GraphApiService graphApiService,
        AgentBlueprintService blueprintService,
        BlueprintLookupService blueprintLookupService,
        FederatedCredentialService federatedCredentialService,
        IClientAppValidator clientAppValidator,
        Func<Task<string?>>? loginHintResolver = null)
    {
        Config = config;
        Results = results;
        Logger = logger;
        ConfigFile = configFile;
        GeneratedConfigPath = generatedConfigPath;
        CorrelationId = correlationId;
        SkipInfrastructure = skipInfrastructure;
        SkipRequirements = skipRequirements;
        CancellationToken = cancellationToken;
        ConfigService = configService;
        Executor = executor;
        BotConfigurator = botConfigurator;
        AuthValidator = authValidator;
        PlatformDetector = platformDetector;
        GraphApiService = graphApiService;
        BlueprintService = blueprintService;
        BlueprintLookupService = blueprintLookupService;
        FederatedCredentialService = federatedCredentialService;
        ClientAppValidator = clientAppValidator;
        LoginHintResolver = loginHintResolver;
    }
}
