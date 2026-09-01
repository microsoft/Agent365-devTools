// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// A custom TokenCredential that uses MSAL directly for interactive authentication.
/// On Windows, this uses WAM (Windows Authentication Broker) for a native sign-in experience
/// that doesn't require opening a browser. On other platforms, it falls back to system browser.
/// 
/// PERSISTENT TOKEN CACHE:
/// Uses Microsoft.Identity.Client.Extensions.Msal to persist tokens across all CLI instances.
/// This dramatically reduces authentication prompts during multi-step operations like 'a365 setup all'.
///
/// Cache Location: [LocalApplicationData]/Microsoft.Agents.A365.DevTools.Cli/msal-token-cache (all platforms)
/// Security: Tokens are stored using platform-appropriate mechanisms:
///   - Windows: DPAPI (Data Protection API) - tokens encrypted with user credentials, persisted to disk
///   - macOS: Keychain - tokens stored in secure keychain, persisted to disk
///   - Linux: Unprotected plaintext file (0600 permissions, owner-only), persisted to disk.
///            Same approach used by Azure CLI (~/.azure/msal_token_cache.json); tokens are protected
///            by filesystem permissions rather than at-rest encryption.
///
/// See: https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam
/// Enhancement: Improves the WAM authentication experience by reducing repeated login prompts.
/// </summary>
public sealed class MsalBrowserCredential : TokenCredential
{
    internal enum InteractiveAuthenticationMode
    {
        Wam,
        SystemBrowser,
        DeviceCode
    }

    internal interface IMsalTokenAcquirer
    {
        Task<IReadOnlyList<IAccount>> GetAccountsAsync();
        Task<AccessToken> AcquireTokenSilentAsync(
            string[] scopes,
            IAccount account,
            bool forceRefresh,
            CancellationToken cancellationToken);
        Task<AccessToken> AcquireOperatingSystemAccountSilentAsync(
            string[] scopes,
            bool forceRefresh,
            CancellationToken cancellationToken);
        Task<AccessToken> AcquireWamAsync(
            string[] scopes,
            IAccount? account,
            string? loginHint,
            CancellationToken cancellationToken);
        Task<AccessToken> AcquireSystemBrowserAsync(
            string[] scopes,
            string? loginHint,
            CancellationToken cancellationToken);
        Task<AccessToken> AcquireDeviceCodeAsync(
            string[] scopes,
            ILogger? logger,
            CancellationToken cancellationToken);
    }

    private sealed class MsalTokenAcquirer(IPublicClientApplication app) : IMsalTokenAcquirer
    {
        public async Task<IReadOnlyList<IAccount>> GetAccountsAsync() =>
            (await app.GetAccountsAsync()).ToList();

        public async Task<AccessToken> AcquireTokenSilentAsync(
            string[] scopes,
            IAccount account,
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            var result = await app
                .AcquireTokenSilent(scopes, account)
                .WithForceRefresh(forceRefresh)
                .ExecuteAsync(cancellationToken);
            return new AccessToken(result.AccessToken, result.ExpiresOn);
        }

        public async Task<AccessToken> AcquireOperatingSystemAccountSilentAsync(
            string[] scopes,
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            var result = await app
                .AcquireTokenSilent(scopes, PublicClientApplication.OperatingSystemAccount)
                .WithForceRefresh(forceRefresh)
                .ExecuteAsync(cancellationToken);
            return new AccessToken(result.AccessToken, result.ExpiresOn);
        }

        public async Task<AccessToken> AcquireWamAsync(
            string[] scopes,
            IAccount? account,
            string? loginHint,
            CancellationToken cancellationToken)
        {
            var builder = app.AcquireTokenInteractive(scopes);
            if (account != null && !string.IsNullOrWhiteSpace(loginHint))
                builder = builder.WithAccount(account);
            else if (!string.IsNullOrWhiteSpace(loginHint))
                builder = builder.WithLoginHint(loginHint);
            else
                builder = builder.WithPrompt(Prompt.SelectAccount);

            var result = await builder.ExecuteAsync(cancellationToken);
            return new AccessToken(result.AccessToken, result.ExpiresOn);
        }

        public async Task<AccessToken> AcquireSystemBrowserAsync(
            string[] scopes,
            string? loginHint,
            CancellationToken cancellationToken)
        {
            var builder = app
                .AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false);
            if (!string.IsNullOrWhiteSpace(loginHint))
                builder = builder.WithLoginHint(loginHint);

            var result = await builder.ExecuteAsync(cancellationToken);
            return new AccessToken(result.AccessToken, result.ExpiresOn);
        }

        public async Task<AccessToken> AcquireDeviceCodeAsync(
            string[] scopes,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            var result = await app
                .AcquireTokenWithDeviceCode(scopes, MsalHelper.CreateDeviceCodeCallback(logger))
                .ExecuteAsync(cancellationToken);
            return new AccessToken(result.AccessToken, result.ExpiresOn);
        }
    }

    private readonly IMsalTokenAcquirer _tokenAcquirer;
    private readonly ILogger? _logger;
    private readonly string _clientAppId;
    private readonly string _tenantId;
    private InteractiveAuthenticationMode _authenticationMode;
    private readonly IntPtr _windowHandle;
    private readonly string? _loginHint;
    private readonly bool _forceRefresh;
    private readonly string _authorityHost;

    // Shared persistent cache helper - initialized once and reused across all instances.
    // This is the key to reducing multiple WAM prompts during setup operations.
    private static MsalCacheHelper? _cacheHelper;
    private static readonly object _cacheHelperLock = new();

    private static readonly string CacheFileName = AuthenticationConstants.MsalCacheFileName;
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AuthenticationConstants.ApplicationName);

    /// <summary>
    /// Full path to the OS-protected MSAL persistent token cache file — the single source of
    /// truth for callers that report or clear the cache location. The cache lives under
    /// LocalApplicationData (%LocalAppData% on Windows, ~/.local/share on Linux,
    /// ~/Library/Application Support on macOS) + the application name. This is deliberately NOT
    /// ConfigService.GetGlobalConfigDirectory(), which resolves to the XDG config dir
    /// (~/.config/a365) on Linux/macOS and would report the wrong location.
    /// </summary>
    public static string MsalCacheFilePath => Path.Combine(CacheDirectory, CacheFileName);

    // P/Invoke is required for WAM window handle in console applications.
    // There is no managed .NET API for console/desktop window handles - these are Windows-specific.
    // This is the standard approach documented by Microsoft for WAM integration:
    // https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    
    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    /// <summary>
    /// Creates a new instance of MsalBrowserCredential.
    /// </summary>
    /// <param name="clientId">The application (client) ID.</param>
    /// <param name="tenantId">The directory (tenant) ID.</param>
    /// <param name="redirectUri">The redirect URI for authentication callbacks.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <param name="useWam">Whether to use WAM on Windows. Default is true.</param>
    /// <param name="authority">Optional authority URL. When provided, overrides the default public-cloud authority.</param>
    /// <param name="loginHint">Optional UPN/email to pre-select the account for silent acquisition and interactive auth.
    /// When provided, WAM and silent auth will target this identity instead of the first cached account.</param>
    public MsalBrowserCredential(
        string clientId,
        string tenantId,
        string? redirectUri = null,
        ILogger? logger = null,
        bool useWam = true,
        string? authority = null,
        string? loginHint = null,
        bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentNullException(nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentNullException(nameof(tenantId));
        }

        _clientAppId = clientId;
        _tenantId = tenantId;
        _logger = logger;
        _loginHint = loginHint;
        _forceRefresh = forceRefresh;

        // Pin consent URLs (BuildAdminConsentUrl) to the cloud we authenticate against; default commercial.
        _authorityHost = Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri)
            ? authorityUri.GetLeftPart(UriPartial.Authority)
            : ConfigConstants.DefaultAuthorityHost;

        // Get window handle for WAM on Windows
        // Try multiple sources: console window, foreground window, or desktop window
        _windowHandle = IntPtr.Zero;
        _authenticationMode = SelectAuthenticationMode(
            clientId,
            useWam,
            OperatingSystem.IsWindows());
        
        if (OperatingSystem.IsWindows() &&
            _authenticationMode == InteractiveAuthenticationMode.Wam)
        {
            try
            {
                _windowHandle = GetWindowHandleForWam();
                _logger?.LogDebug("Window handle for WAM: {Handle}", _windowHandle);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to get window handle");
                _authenticationMode = AuthenticationConstants.IsWellKnownFirstPartyClientApp(clientId)
                    ? InteractiveAuthenticationMode.DeviceCode
                    : InteractiveAuthenticationMode.SystemBrowser;
                _logger?.LogWarning(
                    "Failed to get a WAM window handle; falling back to {AuthenticationMode}.",
                    _authenticationMode == InteractiveAuthenticationMode.DeviceCode
                        ? "device code authentication"
                        : "the system browser");
            }
        }

        var builder = string.IsNullOrWhiteSpace(authority)
            ? PublicClientApplicationBuilder
                .Create(clientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            : PublicClientApplicationBuilder
                .Create(clientId)
                .WithAuthority(authority);

        if (_authenticationMode == InteractiveAuthenticationMode.Wam)
        {
            // Use WAM broker on Windows for native authentication experience
            // WAM provides SSO with Windows accounts and doesn't require browser
            _logger?.LogDebug("Configuring WAM broker for Windows authentication");
            
            var brokerOptions = new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
            {
                Title = "Agent365 Tools Authentication"
            };
            
            builder = builder
                .WithBroker(brokerOptions)
                .WithParentActivityOrWindow(() => _windowHandle)
                .WithRedirectUri($"ms-appx-web://microsoft.aad.brokerplugin/{clientId}");
        }
        else if (_authenticationMode == InteractiveAuthenticationMode.DeviceCode)
        {
            // The Microsoft-managed CLI app uses WAM on Windows. Its MSAL system-browser flow
            // is rejected with AADSTS70007 in WSL/non-Windows environments, so use device code.
            _logger?.LogDebug("Configuring device code authentication for the first-party Agent 365 CLI application");
        }
        else
        {
            // Use system browser on non-Windows platforms or when WAM isn't available
            _logger?.LogDebug("Using system browser for authentication");
            var effectiveRedirectUri = redirectUri ?? AuthenticationConstants.LocalhostRedirectUri;
            builder = builder.WithRedirectUri(effectiveRedirectUri);
        }

        var publicClientApp = builder.Build();

        // Register persistent token cache to share tokens across all MsalBrowserCredential instances.
        // This is crucial for reducing multiple WAM prompts during 'a365 setup all' operations.
        RegisterPersistentCache(publicClientApp, _logger);
        _tokenAcquirer = new MsalTokenAcquirer(publicClientApp);
    }

    internal MsalBrowserCredential(
        string clientId,
        string tenantId,
        InteractiveAuthenticationMode authenticationMode,
        IMsalTokenAcquirer tokenAcquirer,
        ILogger? logger = null,
        string? loginHint = null,
        bool forceRefresh = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        _clientAppId = clientId;
        _tenantId = tenantId;
        _authenticationMode = authenticationMode;
        _tokenAcquirer = tokenAcquirer ?? throw new ArgumentNullException(nameof(tokenAcquirer));
        _logger = logger;
        _loginHint = loginHint;
        _forceRefresh = forceRefresh;
        _windowHandle = IntPtr.Zero;
        _authorityHost = ConfigConstants.DefaultAuthorityHost;
    }

    internal static InteractiveAuthenticationMode SelectAuthenticationMode(
        string clientId,
        bool useWam,
        bool isWindows)
    {
        if (useWam && isWindows)
            return InteractiveAuthenticationMode.Wam;

        return AuthenticationConstants.IsWellKnownFirstPartyClientApp(clientId)
            ? InteractiveAuthenticationMode.DeviceCode
            : InteractiveAuthenticationMode.SystemBrowser;
    }

    /// <summary>
    /// Registers a shared token cache with the MSAL application.
    /// The cache is shared across all MsalBrowserCredential instances within this CLI process.
    ///
    /// Security: Uses platform-appropriate storage:
    ///   - Windows: DPAPI-encrypted file, persisted across CLI invocations
    ///   - macOS: Keychain-backed file, persisted across CLI invocations
    ///   - Linux: Unprotected plaintext file (0600 permissions, owner-only), persisted across CLI invocations.
    ///             Same approach used by Azure CLI (~/.azure/msal_token_cache.json).
    /// </summary>
    private static void RegisterPersistentCache(IPublicClientApplication app, ILogger? logger)
    {
        try
        {
            // Linux: no DPAPI/Keychain equivalent, but persist tokens to disk using an unprotected
            // plaintext file (0600 permissions — owner-only). This is the same approach used by
            // Azure CLI (~/.azure/msal_token_cache.json) and eliminates repeated login prompts
            // across CLI invocations. Tokens remain protected by filesystem permissions.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (_cacheHelper == null)
                {
                    lock (_cacheHelperLock)
                    {
                        if (_cacheHelper == null)
                        {
                            Directory.CreateDirectory(CacheDirectory);
                            var storageProperties = new StorageCreationPropertiesBuilder(CacheFileName, CacheDirectory)
                                .WithLinuxUnprotectedFile()
                                .Build();
                            _cacheHelper = MsalCacheHelper.CreateAsync(storageProperties).GetAwaiter().GetResult();
                            _cacheHelper.VerifyPersistence();
                            logger?.LogDebug("Persistent MSAL token cache initialized at: {Path} (unprotected file)", CacheDirectory);
                        }
                    }
                }
                _cacheHelper.RegisterCache(app.UserTokenCache);
                return;
            }

            // Use double-check locking to ensure only one cache helper is created
            if (_cacheHelper == null)
            {
                lock (_cacheHelperLock)
                {
                    if (_cacheHelper == null)
                    {
                        logger?.LogDebug("Initializing persistent MSAL token cache at: {Path}", CacheDirectory);

                        // Ensure directory exists
                        Directory.CreateDirectory(CacheDirectory);

                        // Configure cache storage properties with platform-appropriate encryption
                        StorageCreationProperties storageProperties;

                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            // Windows: Use default behavior which automatically applies DPAPI encryption
                            // DPAPI (Data Protection API) encrypts tokens at rest, tied to user's Windows credentials
                            storageProperties = new StorageCreationPropertiesBuilder(CacheFileName, CacheDirectory)
                                .Build();
                            logger?.LogDebug("Using DPAPI encryption for token cache (Windows)");
                        }
                        else
                        {
                            // macOS: Use Keychain for secure storage
                            storageProperties = new StorageCreationPropertiesBuilder(CacheFileName, CacheDirectory)
                                .WithMacKeyChain(
                                    serviceName: AuthenticationConstants.ApplicationName,
                                    accountName: "MsalCache")
                                .Build();
                            logger?.LogDebug("Using macOS Keychain for token cache");
                        }

                        // Create the cache helper (this is thread-safe and returns same instance if already created)
                        _cacheHelper = MsalCacheHelper.CreateAsync(storageProperties).GetAwaiter().GetResult();

                        // Verify the cache can actually encrypt/decrypt data on this platform.
                        // If verification fails, MsalCacheHelper falls back to unprotected storage silently.
                        _cacheHelper.VerifyPersistence();

                        logger?.LogDebug("Persistent MSAL token cache initialized and verified successfully");
                    }
                }
            }

            // Register this app's token cache with the shared cache helper
            _cacheHelper.RegisterCache(app.UserTokenCache);
            logger?.LogDebug("Token cache registered for MSAL application");
        }
        catch (Exception ex)
        {
            // Cache registration failure is non-fatal — authentication still works and
            // the user can do nothing to remediate (no D-Bus/Keychain on headless Linux is
            // the common cause), so this stays at Debug rather than surfacing as a warning.
            logger?.LogDebug(ex, "Failed to register persistent token cache; auth prompts may be repeated within this session.");
        }
    }

    /// <summary>
    /// Reads the username (UPN) of the first account in the MSAL persistent token cache without
    /// triggering any interactive authentication. Used to resolve a login hint for pre-selecting
    /// the correct account when the Azure CLI is not available.
    /// </summary>
    /// <param name="clientId">The application (client) ID whose cache to inspect — must match the
    /// client used for interactive acquisition so the same persisted accounts are visible.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <returns>The UPN of the first cached account, or <c>null</c> if the cache is empty,
    /// unreadable, or any error occurs. This is a best-effort fallback and never throws.</returns>
    public static async Task<string?> TryGetCachedAccountUsernameAsync(string clientId, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        try
        {
            // Build a minimal PublicClientApplication on the common authority. We never call any
            // Acquire* method here — only GetAccountsAsync, which reads the persisted cache and
            // cannot prompt. The authority/tenant does not affect which accounts are enumerated.
            var app = PublicClientApplicationBuilder
                .Create(clientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, AuthenticationConstants.CommonTenantId)
                .Build();

            RegisterPersistentCache(app, logger);

            var accounts = await app.GetAccountsAsync();
            return accounts.FirstOrDefault()?.Username;
        }
        catch (Exception ex)
        {
            // Best-effort only — login-hint resolution falls back to no hint (account picker).
            logger?.LogDebug(ex, "Failed to read cached account username from MSAL cache");
            return null;
        }
    }


    /// <inheritdoc/>
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        return GetTokenAsync(requestContext, cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets a window handle for WAM authentication on Windows.
    /// For CLI apps, uses GetConsoleWindow() with GetDesktopWindow() as fallback.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IntPtr GetWindowHandleForWam()
    {
        // Try console window first (works for cmd.exe, PowerShell)
        var handle = GetConsoleWindow();

        // If no console window, try foreground window (works for Windows Terminal)
        if (handle == IntPtr.Zero)
        {
            handle = GetForegroundWindow();
        }

        // Last resort: use desktop window (always valid)
        if (handle == IntPtr.Zero)
        {
            handle = GetDesktopWindow();
        }


        return handle;
    }

    /// <inheritdoc/>
    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var scopes = requestContext.Scopes;

        try
        {
            // First, try to acquire token silently from cache.
            // When a login hint is provided, only attempt silent acquisition for the matching account.
            // Do NOT fall back to any other cached account — that would silently return a token for
            // the wrong user (e.g. sellak's cached token when sellakdev is the CLI identity).
            var accounts = (await _tokenAcquirer.GetAccountsAsync()).ToList();
            IAccount? account;
            if (!string.IsNullOrWhiteSpace(_loginHint))
            {
                account = accounts.FirstOrDefault(a =>
                    string.Equals(a.Username, _loginHint, StringComparison.OrdinalIgnoreCase));
                // If the hint account is not cached, skip silent path — go to interactive with hint.
            }
            else
            {
                account = accounts.FirstOrDefault();
            }

            if (account != null)
            {
                try
                {
                    _logger?.LogDebug("Attempting to acquire token silently from cache...");
                    var silentResult = await _tokenAcquirer.AcquireTokenSilentAsync(
                        scopes,
                        account,
                        _forceRefresh,
                        cancellationToken);

                    _logger?.LogDebug("Successfully acquired token from cache.");
                    return silentResult;
                }
                catch (MsalUiRequiredException ex)
                {
                    if (ex.Classification == UiRequiredExceptionClassification.ConsentRequired)
                        LogConsentRequiredAndThrow(ex);
                    _logger?.LogDebug("Token cache miss or expired, interactive authentication required.");
                }
            }

            // Before showing interactive WAM: probe silently using the OS account.
            // WAM can detect "Need admin approval" (consent required) without showing any dialog.
            // If detected, print the admin consent URL and exit — WAM dialog is never shown.
            if (_authenticationMode == InteractiveAuthenticationMode.Wam)
            {
                try
                {
                    _logger?.LogDebug("Probing consent status silently via WAM OS account...");
                    var probeResult = await _tokenAcquirer.AcquireOperatingSystemAccountSilentAsync(
                        scopes,
                        _forceRefresh,
                        cancellationToken);
                    _logger?.LogDebug("WAM OS account probe succeeded — consent is granted.");
                    // Only return the OS account token when no login hint is set.
                    // When a hint is provided, the caller wants a specific identity — fall through
                    // to interactive WAM with the hint so the correct user is authenticated.
                    if (string.IsNullOrWhiteSpace(_loginHint))
                        return probeResult;
                    _logger?.LogDebug("Login hint set — skipping OS account token, proceeding to interactive WAM for {LoginHint}.", _loginHint);
                }
                catch (MsalUiRequiredException ex) when (
                    ex.Classification == UiRequiredExceptionClassification.ConsentRequired)
                {
                    LogConsentRequiredAndThrow(ex);
                }
                catch (MsalUiRequiredException)
                {
                    // Interaction required for other reasons (first sign-in, MFA, etc.) — fall through to WAM.
                }
            }

            // Acquire token interactively.
            // When a login hint is provided, WAM and browser auth will pre-select that identity
            // instead of defaulting to the Windows account or cached account picker.
            if (_authenticationMode == InteractiveAuthenticationMode.DeviceCode)
            {
                return await AcquireTokenWithDeviceCodeFallbackAsync(
                    scopes,
                    cancellationToken,
                    trySilentFirst: false);
            }

            if (_authenticationMode == InteractiveAuthenticationMode.Wam)
            {
                // WAM on Windows - native authentication dialog, no browser needed
                _logger?.LogInformation("Authenticating via Windows Account Manager...");
                return await _tokenAcquirer.AcquireWamAsync(
                    scopes,
                    account,
                    _loginHint,
                    cancellationToken);
            }

            // System browser for tenant-owned custom applications.
            _logger?.LogInformation("Opening browser for authentication...");
            return await _tokenAcquirer.AcquireSystemBrowserAsync(
                scopes,
                _loginHint,
                cancellationToken);
        }
        catch (PlatformNotSupportedException ex)
        {
            // macOS: MSAL throws PlatformNotSupportedException when no browser is available
            _logger?.LogWarning("Browser authentication is not supported on this platform: {Message}", ex.Message);
            return await AcquireTokenWithDeviceCodeFallbackAsync(scopes, cancellationToken);
        }
        catch (MsalClientException ex) when (ex.ErrorCode == "linux_xdg_open_failed")
        {
            // Linux/WSL: MSAL throws MsalClientException when xdg-open and friends are unavailable
            _logger?.LogWarning("Browser cannot be opened on this platform: {Message}", ex.Message);
            return await AcquireTokenWithDeviceCodeFallbackAsync(scopes, cancellationToken);
        }
        catch (MsalServiceException ex) when (
            ex.Message.Contains(AuthenticationConstants.ConditionalAccessPolicyBlockedError, StringComparison.Ordinal) ||
            ex.Message.Contains(AuthenticationConstants.DeviceCompliancePolicyBlockedError, StringComparison.Ordinal))
        {
            // Conditional Access Policy (AADSTS53003) or device compliance policy (AADSTS53000)
            // blocks interactive browser/WAM authentication. Device code flow may still be affected
            // by these policies depending on your tenant configuration — attempting fallback.
            var aadErrorCode = ex.Message.Contains(AuthenticationConstants.ConditionalAccessPolicyBlockedError, StringComparison.Ordinal)
                ? AuthenticationConstants.ConditionalAccessPolicyBlockedError
                : AuthenticationConstants.DeviceCompliancePolicyBlockedError;
            _logger?.LogWarning(
                "Interactive authentication blocked by Conditional Access Policy ({ErrorCode}). " +
                "Falling back to device code authentication.",
                aadErrorCode);
            return await AcquireTokenWithDeviceCodeFallbackAsync(scopes, cancellationToken);
        }
        catch (MsalException ex) when (
            ex.Message.Contains(AuthenticationConstants.WamErrorPrefix, StringComparison.OrdinalIgnoreCase)
            || IsWamDeclinedScopesError(ex))
        {
            // WAM error 0xcaa90019 = "Need admin approval" (admin consent not granted).
            // Do NOT fall back to device code — device code shows the same browser consent page
            // and hangs if the user clicks "Return to application without granting consent".
            if (ex.Message.Contains(AuthenticationConstants.WamConsentRequiredError, StringComparison.OrdinalIgnoreCase))
                LogConsentRequiredAndThrow(ex);

            // "Declined scopes" (ApiContractViolation): the WAM broker rejects the request, reporting
            // declined scopes. Observed with Exchange-specific Graph delegated scopes such as
            // MailboxSettings.ReadWrite or ExchangeMessageTrace.Read.All. The scopes are valid and
            // grantable, and consent is in place — this is known broker behavior, not a consent
            // or scope-validity problem. Device code flow does not go through the WAM broker and
            // succeeds for these scopes.
            bool isDeclinedScopes = IsWamDeclinedScopesError(ex);
            if (isDeclinedScopes)
            {
                // Informational, not a warning: this is a successful auto-recovery on the
                // intended fallback path, not a failure the user needs to act on.
                _logger?.LogInformation(
                    "WAM could not complete authentication for the requested scopes " +
                    "(ApiContractViolation: declined scopes are present). Falling back to device code " +
                    "authentication, which does not use the broker.");
                return await AcquireTokenWithDeviceCodeFallbackAsync(scopes, cancellationToken);
            }

            // Other WAM errors (e.g. Conditional Access Policy, device compliance policy)
            // are not consent-related — device code flow bypasses the WAM broker and may succeed.
            _logger?.LogWarning(
                "WAM authentication blocked ({Error}). Falling back to device code authentication.",
                ex.Message.Split('\n').FirstOrDefault(l => l.Contains("0xcaa", StringComparison.OrdinalIgnoreCase))?.Trim() ?? "WAM error");
            return await AcquireTokenWithDeviceCodeFallbackAsync(scopes, cancellationToken);
        }
        catch (MsalException ex)
        {
            _logger?.LogDebug(ex, "MSAL authentication failed");
            if (ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
                ex.ErrorCode is "authentication_canceled" or "user_canceled")
            {
                _logger?.LogDebug("Sign-in was canceled.");
            }
            else
            {
                _logger?.LogError("MSAL authentication failed: {Message}", ex.Message);
            }
            throw new MsalAuthenticationFailedException($"Failed to acquire token: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Returns true when the MSAL exception represents WAM's "declined scopes" failure:
    /// the WAM broker rejects the request with ApiContractViolation, reporting declined scopes
    /// (observed with Exchange-specific Graph delegated scopes). This is distinct from a
    /// consent-not-granted failure (0xcaa90019): the scopes are valid and consent is in place,
    /// but the broker still refuses. Device code flow does not go through WAM and succeeds.
    /// </summary>
    internal static bool IsWamDeclinedScopesError(MsalException ex)
        => ex.Message.Contains(AuthenticationConstants.WamApiContractViolation, StringComparison.OrdinalIgnoreCase)
        && ex.Message.Contains(AuthenticationConstants.WamDeclinedScopesError, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Logs a consistent "admin consent required" message with the admin consent URL and throws.
    /// Used by all three consent-detection points: silent path, WAM OS probe, and WAM error backstop.
    /// </summary>
    private void LogConsentRequiredAndThrow(Exception inner)
    {
        var consentUrl = ClientAppValidationException.BuildAdminConsentUrl(_clientAppId, _tenantId, _authorityHost);
        _logger?.LogWarning("Admin consent has not been granted for this application.");
        _logger?.LogWarning("An administrator must grant tenant-wide consent to proceed.");
        if (consentUrl != null)
        {
            _logger?.LogWarning("Share this URL with an administrator to grant consent:");
            _logger?.LogWarning("  {ConsentUrl}", consentUrl);
        }
        _logger?.LogWarning("After consent is granted, re-run the command.");
        throw new MsalAuthenticationFailedException(
            consentUrl != null
                ? $"Admin consent required. Share this URL with an administrator: {consentUrl}"
                : "Admin consent required. An administrator must grant tenant-wide consent for this application.",
            inner);
    }

    private async Task<AccessToken> AcquireTokenWithDeviceCodeFallbackAsync(
        string[] scopes,
        CancellationToken cancellationToken,
        bool trySilentFirst = true)
    {
        // Before showing a device code, try to get a cached token.
        // On Linux, the shared in-process cache may already hold a token from an earlier
        // authentication step in the same CLI invocation (e.g., blueprint creation),
        // which can be reused silently without prompting the user again.
        var accountsList = trySilentFirst
            ? (await _tokenAcquirer.GetAccountsAsync()).ToList()
            : [];
        // Filter by tenant to avoid silently authenticating as the wrong identity when multiple accounts are cached.
        // If multiple accounts share the same tenant (rare), FirstOrDefault picks the first match; this is acceptable
        // since MSAL will re-prompt if the silent acquisition fails for the wrong account.
        var cachedAccount = accountsList.Count switch
        {
            0 => null,
            1 => accountsList[0],
            _ => accountsList.FirstOrDefault(a =>
                string.Equals(a.HomeAccountId?.TenantId, _tenantId, StringComparison.OrdinalIgnoreCase))
        };
        if (cachedAccount != null)
        {
            try
            {
                _logger?.LogDebug("Attempting silent token acquisition before device code...");
                var silentResult = await _tokenAcquirer.AcquireTokenSilentAsync(
                    scopes,
                    cachedAccount,
                    forceRefresh: false,
                    cancellationToken);
                _logger?.LogDebug("Acquired token silently, skipping device code prompt.");
                return silentResult;
            }
            catch (MsalUiRequiredException)
            {
                _logger?.LogDebug("Silent acquisition failed, proceeding with device code.");
            }
            catch (MsalException ex)
            {
                // The pre-device-code silent attempt reuses the broker-enabled client, so it can
                // re-hit the very failure that triggered this fallback (e.g. WAM ApiContractViolation
                // / declined scopes). The silent attempt is only an optimization — on ANY MSAL failure
                // fall through to the device code flow (which does not use the broker) rather than
                // letting the exception escape and abort the fallback.
                _logger?.LogDebug(ex, "Silent acquisition failed ({ErrorCode}); proceeding with device code.", ex.ErrorCode);
            }
        }

        _logger?.LogInformation(
            trySilentFirst
                ? "Falling back to device code authentication..."
                : "Using device code authentication...");
        _logger?.LogInformation("Please sign in with your Microsoft account");

        try
        {
            var deviceCodeResult = await _tokenAcquirer.AcquireDeviceCodeAsync(
                scopes,
                _logger,
                cancellationToken);

            _logger?.LogDebug("Successfully acquired token via device code authentication.");
            return deviceCodeResult;
        }
        catch (MsalException msalEx) when (
            msalEx.Message.Contains("AADSTS7000218", StringComparison.Ordinal) ||
            (msalEx is MsalServiceException svcEx && svcEx.ErrorCode == "invalid_client" &&
             msalEx.Message.Contains("client_assertion", StringComparison.Ordinal)))
        {
            // Do NOT pass msalEx as logger argument — avoids printing the full stack trace.
            // This error means "Allow public client flows" is disabled on the app registration.
            _logger?.LogError("Device code authentication failed: 'Allow public client flows' is not enabled on the app registration.");
            _logger?.LogError("Run 'a365 setup requirements' to detect and auto-fix this automatically.");
            _logger?.LogError("Or fix manually: Azure Portal > App registrations > Authentication > Settings > Enable 'Allow public client flows' > Save.");
            throw new MsalAuthenticationFailedException(
                "Device code authentication requires 'Allow public client flows' to be enabled. Run 'a365 setup requirements' to auto-fix, or enable it manually in Azure Portal > App registrations > Authentication.",
                msalEx);
        }
        catch (MsalException msalEx)
        {
            _logger?.LogDebug(msalEx, "Device code authentication failed");
            _logger?.LogError("Device code authentication failed: {Message}", msalEx.Message);
            throw new MsalAuthenticationFailedException($"Device code authentication failed: {msalEx.Message}", msalEx);
        }
    }
}

/// <summary>
/// Exception thrown when MSAL-based authentication fails.
/// </summary>
public class MsalAuthenticationFailedException : Exception
{
    public MsalAuthenticationFailedException(string message) : base(message) { }
    public MsalAuthenticationFailedException(string message, Exception innerException) : base(message, innerException) { }
}
