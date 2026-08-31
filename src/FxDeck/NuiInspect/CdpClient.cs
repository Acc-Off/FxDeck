using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using FxDeck.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FxDeck.NuiInspect;

/// <summary>One entry of <c>GET /json</c> on the CEF remote-debugging port.</summary>
public sealed record CdpPage(string? Type, string? Title, string? Url, string? WebSocketDebuggerUrl);

/// <summary>A CDP command failed (<c>{id, error}</c> response) or the connection went away mid-request.</summary>
public sealed class CdpException : Exception
{
    public CdpException(string message) : base(message)
    {
    }
}

/// <summary>
/// Minimal Chrome DevTools Protocol client: JSON messages <c>{id, method, params}</c> over a WebSocket,
/// responses correlated by <c>id</c>, events surfaced through <see cref="EventReceived"/>.
/// Like <c>FxConsoleClient</c> this contains an undocumented dependency (design memo §3.10) — keep
/// everything CDP-shaped inside <c>NuiInspect</c>.
/// </summary>
public sealed class CdpClient : IAsyncDisposable
{
    /// <summary>A command list of ~600 entries serialises to a few hundred KB; anything bigger than this is not ours.</summary>
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    private readonly ILogger _logger;
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private Task _receiveLoop = Task.CompletedTask;
    private long _nextId;

    public CdpClient(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised from the receive loop for every CDP event (<c>{method, params}</c>).</summary>
    public event Action<string, JsonElement>? EventReceived;

    /// <summary>Lists the debuggable pages. Failure to connect means the game is not running.</summary>
    public static async Task<IReadOnlyList<CdpPage>> ListPagesAsync(HttpClient http, Uri baseAddress, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(baseAddress, "json"), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<List<CdpPage>>(stream, FxJson.Options, cancellationToken) ?? [];
    }

    /// <summary>
    /// The in-game NUI is one page, <c>CitizenFX root UI</c> (<c>nui://game/ui/root.html</c>), hosting every
    /// resource NUI as a child iframe. Prefer it by url/title, fall back to the first page-typed entry.
    /// </summary>
    public static CdpPage? PickPage(IReadOnlyList<CdpPage> pages)
    {
        var candidates = pages.Where(p => p.WebSocketDebuggerUrl is not null && (p.Type is null or "page")).ToList();
        return candidates.FirstOrDefault(p =>
                (p.Url?.Contains("root.html", StringComparison.OrdinalIgnoreCase) ?? false)
                || (p.Title?.Contains("CitizenFX", StringComparison.OrdinalIgnoreCase) ?? false))
            ?? candidates.FirstOrDefault();
    }

    public async Task ConnectAsync(Uri webSocketUrl, CancellationToken cancellationToken)
    {
        await _socket.ConnectAsync(webSocketUrl, cancellationToken);
        _receiveLoop = Task.Run(ReceiveLoopAsync, CancellationToken.None);
    }

    /// <summary>Sends one CDP command and waits for its response. <paramref name="args"/> becomes <c>params</c>.</summary>
    public async Task<JsonElement> SendAsync(string method, object? args, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        try
        {
            var message = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = args }, FxJson.Wire);
            await _socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            return await pending.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closed.Cancel();
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // closing best-effort; the socket is disposed below either way
            }
        }

        try
        {
            await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // the loop ends when the socket is disposed
        }

        _socket.Dispose();
        _closed.Dispose();
        FailPending("the CDP connection was closed");
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open && !_closed.IsCancellationRequested)
            {
                message.SetLength(0);
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(), _closed.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        FailPending("the CDP endpoint closed the connection");
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxMessageBytes)
                    {
                        throw new CdpException("CDP message exceeds the size limit");
                    }
                }
                while (!result.EndOfMessage);

                HandleMessage(message);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _logger.LogDebug("CDP receive loop ended: {Message}", ex.Message);
        }
        finally
        {
            FailPending("the CDP connection was lost");
        }
    }

    private void HandleMessage(MemoryStream message)
    {
        using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var idProperty) && idProperty.TryGetInt64(out var id))
        {
            if (!_pending.TryGetValue(id, out var pending))
            {
                return;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var text = error.TryGetProperty("message", out var errorMessage) ? errorMessage.GetString() : null;
                pending.TrySetException(new CdpException(text ?? "CDP command failed"));
                return;
            }

            pending.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            return;
        }

        if (root.TryGetProperty("method", out var method) && method.GetString() is { } name)
        {
            var args = root.TryGetProperty("params", out var eventArgs) ? eventArgs.Clone() : default;
            try
            {
                EventReceived?.Invoke(name, args);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "A CDP event handler threw");
            }
        }
    }

    private void FailPending(string reason)
    {
        foreach (var (id, pending) in _pending)
        {
            _pending.TryRemove(id, out _);
            pending.TrySetException(new CdpException(reason));
        }
    }
}
