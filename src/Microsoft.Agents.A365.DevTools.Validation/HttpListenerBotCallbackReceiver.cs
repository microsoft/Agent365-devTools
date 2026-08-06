// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Microsoft.Agents.A365.DevTools.Validation;

/// <summary>
/// HttpListener-based implementation of <see cref="IBotCallbackReceiver"/>.
/// Listens on a random available localhost port for Bot Framework callback activities
/// sent by the agent to POST /v3/conversations/{id}/activities.
/// </summary>
public sealed class HttpListenerBotCallbackReceiver : IBotCallbackReceiver
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly SemaphoreSlim _responseReceived = new(0);
    private readonly object _lock = new();
    private readonly List<BotCallbackResponse> _responses = new();
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    /// <summary>
    /// After each received message, wait this long for another message before settling.
    /// Each new arrival resets this timer, so fast bursts are collected together.
    /// </summary>
    internal static readonly TimeSpan SettlePeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum total time to collect messages after the first one arrives.
    /// Prevents indefinite waiting when an agent keeps sending messages.
    /// </summary>
    internal static readonly TimeSpan MaxCollectionWindow = TimeSpan.FromSeconds(30);

    public string ServiceUrl => $"http://localhost:{_port}";

    public HttpListenerBotCallbackReceiver()
    {
        _port = FindAvailablePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_port}/");
    }

    /// <summary>
    /// Initializes with a specific port (for testing).
    /// </summary>
    public HttpListenerBotCallbackReceiver(int port)
    {
        _port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_port}/");
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener.Start();
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task<BotCallbackResponse?> WaitForResponseAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            // Wait for the first callback activity
            await _responseReceived.WaitAsync(timeoutCts.Token);

            // First message received — start the collection window.
            // Each new message resets the settle timer; collection caps at MaxCollectionWindow.
            var collectionDeadline = DateTime.UtcNow + MaxCollectionWindow;

            while (DateTime.UtcNow < collectionDeadline && !timeoutCts.Token.IsCancellationRequested)
            {
                var untilDeadline = collectionDeadline - DateTime.UtcNow;
                var settleWait = untilDeadline < SettlePeriod ? untilDeadline : SettlePeriod;

                if (settleWait <= TimeSpan.Zero)
                {
                    break;
                }

                try
                {
                    if (!await _responseReceived.WaitAsync(settleWait, timeoutCts.Token))
                    {
                        break; // No new message within settle period — done collecting
                    }
                    // New message arrived — loop resets the settle timer
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Overall timeout or cancellation — return whatever we collected
        }

        lock (_lock)
        {
            return SelectBestResponse();
        }
    }

    public void ClearResponses()
    {
        lock (_lock)
        {
            _responses.Clear();
        }

        while (_responseReceived.Wait(0))
        {
            // Drain any pending signals
        }
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                await HandleRequestAsync(context);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            // Bot Framework sends responses to POST /v3/conversations/{id}/activities[/{id}]
            if (context.Request.HttpMethod == "POST" &&
                context.Request.Url?.AbsolutePath.Contains("/v3/conversations/", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var text = doc.RootElement.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
                    var type = doc.RootElement.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

                    lock (_lock)
                    {
                        _responses.Add(new BotCallbackResponse(text, type));
                    }

                    _responseReceived.Release();
                }
                catch (JsonException)
                {
                    lock (_lock)
                    {
                        _responses.Add(new BotCallbackResponse(null, null));
                    }

                    _responseReceived.Release();
                }
            }

            // Return 200 OK with a ResourceResponse (required by Bot Framework SDK)
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            var responseJson = JsonSerializer.SerializeToUtf8Bytes(new { id = Guid.NewGuid().ToString("N") });
            await context.Response.OutputStream.WriteAsync(responseJson);
        }
        finally
        {
            context.Response.Close();
        }
    }

    /// <summary>
    /// Selects the best response from collected callbacks.
    /// Returns the last message-type response with text, skipping typing indicators.
    /// Returns null if no substantive message was received.
    /// </summary>
    private BotCallbackResponse? SelectBestResponse()
    {
        if (_responses.Count == 0)
        {
            return null;
        }

        return _responses.LastOrDefault(r => IsSubstantiveMessage(r));
    }

    /// <summary>
    /// Determines whether a response is a substantive message (not a typing indicator).
    /// </summary>
    public static bool IsSubstantiveMessage(BotCallbackResponse response)
    {
        if (string.Equals(response.Type, "typing", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(response.Type, "message", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(response.Text);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        try { _listener.Stop(); }
        catch { /* best effort */ }

        if (_listenTask is not null)
        {
            try { await _listenTask; }
            catch { /* best effort */ }
        }

        try { _listener.Close(); }
        catch { /* best effort */ }

        _cts?.Dispose();
        _responseReceived.Dispose();
    }

    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
