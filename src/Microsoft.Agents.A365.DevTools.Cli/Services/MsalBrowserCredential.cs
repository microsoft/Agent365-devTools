// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
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
/// Cache Location: [LocalApplicationData]/Agent365/msal-token-cache (Windows/macOS)
/// Security: Tokens are stored using platform-appropriate mechanisms:
///   - Windows: DPAPI (Data Protection API) - tokens encrypted with user credentials, persisted to disk
///   - macOS: Keychain - tokens stored in secure keychain, persisted to disk
///   - Linux: Shared in-memory cache (static, in-process) - tokens never written to disk, shared
///            across all MsalBrowserCredential instances in the same CLI process to avoid repeated prompts
///
/// See: https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam
/// Enhancement: Improves the WAM authentication experience by reducing repeated login prompts.
/// </summary>
public sealed class MsalBrowserCredential : TokenCredential
{
    private readonly IPublicClientApplication _publicClientApp;
    private readonly ILogger? _logger;
    private readonly string _tenantId;
    private readonly bool _useWam;
    private readonly IntPtr _windowHandle;

    // Shared persistent cache helper - initialized once and reused across all instances.
    // This is the key to reducing multiple WAM prompts during setup operations.
    private static MsalCacheHelper? _cacheHelper;
    private static readonly object _cacheHelperLock = new();

    // Linux-only: shared in-memory token cache, serialized as MSAL V3 format.
    // Shared across all MsalBrowserCredential instances in the same CLI process so that
    // a second auth call (e.g., client secret creation) can reuse the token from the first
    // interactive sign-in without triggering another device code prompt.
    private static byte[] _linuxInMemoryCacheBytes = Array.Empty<byte>();
    private static readonly object _linuxCacheLock = new();
    private static readonly string CacheFileName = "msal-token-cache";
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AuthenticationConstants.ApplicationName);

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
    public MsalBrowserCredential(
        string clientId,
        string tenantId,
        string? redirectUri = null,
        ILogger? logger = null,
        bool useWam = true)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentNullException(nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentNullException(nameof(tenantId));
        }

        _tenantId = tenantId;
        _logger = logger;
        
        // Get window handle for WAM on Windows
        // Try multiple sources: console window, foreground window, or desktop window
        _windowHandle = IntPtr.Zero;
        _useWam = useWam && OperatingSystem.IsWindows();
        
        if (OperatingSystem.IsWindows() && _useWam)
        {
            try
            {
                _windowHandle = GetWindowHandleForWam();
                _logger?.LogDebug("Window handle for WAM: {Handle}", _windowHandle);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get window handle, falling back to system browser");
                _useWam = false;
            }
        }

        var builder = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId);

        if (_useWam)
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
        else
        {
            // Use system browser on non-Windows platforms or when WAM isn't available
            _logger?.LogDebug("Using system browser for authentication");
            var effectiveRedirectUri = redirectUri ?? AuthenticationConstants.LocalhostRedirectUri;
            builder = builder.WithRedirectUri(effectiveRedirectUri);
        }

        _publicClientApp = builder.Build();

        // Register persistent token cache to share tokens across all MsalBrowserCredential instances.
        // This is crucial for reducing multiple WAM prompts during 'a365 setup all' operations.
        RegisterPersistentCache(_publicClientApp, _logger);
    }

    /// <summary>
    /// Registers a shared token cache with the MSAL application.
    /// The cache is shared across all MsalBrowserCredential instances within this CLI process.
    ///
    /// Security: Uses platform-appropriate storage:
    ///   - Windows: DPAPI-encrypted file, persisted across CLI invocations
    ///   - macOS: Keychain-backed file, persisted across CLI invocations
    ///   - Linux: In-memory only (shared in-process via static bytes), not persisted to disk
    /// </summary>
    private static void RegisterPersistentCache(IPublicClientApplication app, ILogger? logger)
    {
        try
        {
            // Linux: no secure file storage available, but share tokens across all instances
            // within this CLI process using a static in-memory serialized cache.
            // This eliminates repeated device code prompts during multi-step operations
            // (e.g., blueprint creation followed by client secret creation in 'setup blueprint').
            // Tokens are never written to disk on Linux.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                RegisterSharedInMemoryCache(app, logger);
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
            // Cache registration failure is non-fatal - authentication will still work,
            // but users may see more prompts during multi-step operations
            logger?.LogWarning(ex, "Failed to register persistent token cache. Authentication prompts may be repeated.");
        }
    }

    /// <summary>
    /// Registers a shared in-memory token cache for Linux platforms.
    /// Tokens are not persisted to disk, but are shared across all MsalBrowserCredential
    /// instances within the current CLI process, eliminating repeated auth prompts within
    /// a single command invocation (e.g., blueprint creation + client secret creation).
    /// </summary>
    private static void RegisterSharedInMemoryCache(IPublicClientApplication app, ILogger? logger)
    {
        app.UserTokenCache.SetBeforeAccess(args =>
        {
            lock (_linuxCacheLock)
            {
                args.TokenCache.DeserializeMsalV3(_linuxInMemoryCacheBytes);
            }
        });

        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged)
            {
                lock (_linuxCacheLock)
                {
                    _linuxInMemoryCacheBytes = args.TokenCache.SerializeMsalV3();
                }
            }
        });

        logger?.LogDebug("Registered shared in-memory token cache for Linux (in-process only, not persisted to disk).");
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
            // First, try to acquire token silently from cache
            var accounts = await _publicClientApp.GetAccountsAsync();
            var account = accounts.FirstOrDefault();

            if (account != null)
            {
                try
                {
                    _logger?.LogDebug("Attempting to acquire token silently from cache...");
                    var silentResult = await _publicClientApp
                        .AcquireTokenSilent(scopes, account)
                        .ExecuteAsync(cancellationToken);

                    _logger?.LogDebug("Successfully acquired token from cache.");
                    return new AccessToken(silentResult.AccessToken, silentResult.ExpiresOn);
                }
                catch (MsalUiRequiredException)
                {
                    _logger?.LogDebug("Token cache miss or expired, interactive authentication required.");
                }
            }

            // Acquire token interactively
            AuthenticationResult interactiveResult;
            
            if (_useWam)
            {
                // WAM on Windows - native authentication dialog, no browser needed
                _logger?.LogInformation("Authenticating via Windows Account Manager...");
                interactiveResult = await _publicClientApp
                    .AcquireTokenInteractive(scopes)
                    .ExecuteAsync(cancellationToken);
            }
            else
            {
                // System browser on Mac/Linux
                _logger?.LogInformation("Opening browser for authentication...");
                interactiveResult = await _publicClientApp
                    .AcquireTokenInteractive(scopes)
                    .WithUseEmbeddedWebView(false)
                    .ExecuteAsync(cancellationToken);
            }

            _logger?.LogDebug("Successfully acquired token via interactive authentication.");
            return new AccessToken(interactiveResult.AccessToken, interactiveResult.ExpiresOn);
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
        catch (MsalException ex)
        {
            _logger?.LogError(ex, "MSAL authentication failed: {Message}", ex.Message);
            throw new MsalAuthenticationFailedException($"Failed to acquire token: {ex.Message}", ex);
        }
    }

    private async Task<AccessToken> AcquireTokenWithDeviceCodeFallbackAsync(
        string[] scopes,
        CancellationToken cancellationToken)
    {
        // Before showing a device code, try to get a cached token.
        // On Linux, the shared in-process cache may already hold a token from an earlier
        // authentication step in the same CLI invocation (e.g., blueprint creation),
        // which can be reused silently without prompting the user again.
        var accountsList = (await _publicClientApp.GetAccountsAsync()).ToList();
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
                var silentResult = await _publicClientApp
                    .AcquireTokenSilent(scopes, cachedAccount)
                    .ExecuteAsync(cancellationToken);
                _logger?.LogDebug("Acquired token silently, skipping device code prompt.");
                return new AccessToken(silentResult.AccessToken, silentResult.ExpiresOn);
            }
            catch (MsalUiRequiredException)
            {
                _logger?.LogDebug("Silent acquisition failed, proceeding with device code.");
            }
        }

        _logger?.LogInformation("Falling back to device code authentication...");
        _logger?.LogInformation("Please sign in with your Microsoft account");

        try
        {
            var deviceCodeResult = await _publicClientApp
                .AcquireTokenWithDeviceCode(scopes, deviceCode =>
                {
                    _logger?.LogInformation("");
                    _logger?.LogInformation("==========================================================================");
                    _logger?.LogInformation("To sign in, use a web browser to open the page:");
                    _logger?.LogInformation("    {VerificationUrl}", deviceCode.VerificationUrl);
                    _logger?.LogInformation("");
                    _logger?.LogInformation("And enter the code: {UserCode}", deviceCode.UserCode);
                    _logger?.LogInformation("==========================================================================");
                    _logger?.LogInformation("");
                    return Task.CompletedTask;
                })
                .ExecuteAsync(cancellationToken);

            _logger?.LogDebug("Successfully acquired token via device code authentication.");
            return new AccessToken(deviceCodeResult.AccessToken, deviceCodeResult.ExpiresOn);
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
            _logger?.LogError(msalEx, "Device code authentication failed: {Message}", msalEx.Message);
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
