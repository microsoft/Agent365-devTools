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
/// Cache Location: %LocalApplicationData%\Agent365\msal-token-cache (Windows)
/// Security: Tokens are encrypted at rest using platform-appropriate mechanisms:
///   - Windows: DPAPI (Data Protection API) - tokens encrypted with user credentials
///   - macOS: Keychain - tokens stored in secure keychain
///   - Linux: Persistent caching is disabled (no platform encryption available); tokens remain in-memory only
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
    /// Registers a persistent cross-process token cache with the MSAL application.
    /// The cache is shared across all instances of MsalBrowserCredential within this CLI process
    /// and persists to disk for reuse across CLI invocations.
    ///
    /// Security: Uses platform-appropriate encryption (DPAPI on Windows, Keychain on macOS).
    /// On Linux, persistent caching is skipped to avoid storing tokens in plaintext on disk.
    /// </summary>
    private static void RegisterPersistentCache(IPublicClientApplication app, ILogger? logger)
    {
        try
        {
            // Skip persistent caching on Linux to avoid storing tokens in plaintext on disk.
            // Linux lacks a platform-provided encryption mechanism (libsecret/Keyring requires
            // additional setup that cannot be guaranteed). Users on Linux will see repeated
            // authentication prompts but their tokens remain safely in-memory only.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                logger?.LogDebug("Skipping persistent token cache on Linux - no platform encryption available. Tokens will be in-memory only.");
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
            _logger?.LogWarning("Browser authentication is not supported on this platform: {Message}", ex.Message);
            throw new MsalAuthenticationFailedException($"Browser authentication is not supported on this platform ({ex.Message})", ex);
        }
        catch (MsalException ex)
        {
            _logger?.LogError(ex, "MSAL authentication failed: {Message}", ex.Message);
            throw new MsalAuthenticationFailedException($"Failed to acquire token: {ex.Message}", ex);
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
