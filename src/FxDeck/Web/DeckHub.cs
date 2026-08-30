using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using FxDeck.Commands;
using FxDeck.Config;
using FxDeck.FxConsole;
using Microsoft.Extensions.Logging;
using static FxDeck.Web.DeckMessages;

namespace FxDeck.Web;

/// <summary>
/// All connected phones. Turns key presses and releases into macros, keeps the current stage of every
/// staged key (design memo §3.2) and pushes game state, profiles, settings and console output to every deck.
/// </summary>
public sealed class DeckHub
{
    private const int MaxMessageBytes = 64 * 1024;

    private readonly ConfigStore _config;
    private readonly MacroExecutor _executor;
    private readonly IFxConsoleClient _client;
    private readonly ILogger<DeckHub> _logger;
    private readonly ConcurrentDictionary<Guid, DeckSession> _sessions = new();
    private readonly ConcurrentDictionary<string, byte> _runningKeys = new();
    /// <summary>Current stage per key id (0-based). Keys on stage 0 are absent. In memory only: FxDeck restarts reset every key.</summary>
    private readonly ConcurrentDictionary<string, int> _stages = new();

    public DeckHub(ConfigStore config, MacroExecutor executor, IFxConsoleClient client, ILogger<DeckHub> logger)
    {
        _config = config;
        _executor = executor;
        _client = client;
        _logger = logger;
    }

    public int ConnectedCount => _sessions.Count;

    /// <summary>Raised when a phone connects or disconnects.</summary>
    public event EventHandler? SessionsChanged;

    /// <summary>Serves one WebSocket connection until it closes.</summary>
    public async Task HandleAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var session = new DeckSession(socket);
        _sessions[session.Id] = session;
        _logger.LogInformation("Deck connected ({Count} online)", _sessions.Count);
        SessionsChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            await session.SendAsync(BuildHello(), cancellationToken).ConfigureAwait(false);

            var buffer = new byte[4096];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, "bye").ConfigureAwait(false);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxMessageBytes)
                    {
                        await TryCloseAsync(socket, WebSocketCloseStatus.MessageTooBig, "too big").ConfigureAwait(false);
                        return;
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleMessage(session, message.ToArray());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug("Deck socket closed: {Message}", ex.Message);
        }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            _logger.LogInformation("Deck disconnected ({Count} online)", _sessions.Count);
            SessionsChanged?.Invoke(this, EventArgs.Empty);
            // A phone that vanished while holding a key must not leave "e sit" running (design memo §3.2).
            _ = ReleaseHeldAsync(session);
        }
    }

    public Hello BuildHello()
    {
        var config = _config.Current;
        var stages = _stages.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return new Hello("hello", config.OrderedProfiles.ToList(), DeckSettings.From(config.Settings), GameState(_client.State), stages);
    }

    public Task BroadcastGameStateAsync(FxConsoleConnectionState state) => BroadcastAsync(new Status("status", GameState(state)));

    public Task BroadcastConsoleLineAsync(string line) => BroadcastAsync(new ConsoleLine("console", line));

    /// <summary>Pushes the current profiles and settings (after config.json changed) and resets stages that no longer fit their key.</summary>
    public async Task BroadcastConfigAsync(AppConfig config)
    {
        await BroadcastAsync(new ProfilesChanged("profiles", config.OrderedProfiles.ToList())).ConfigureAwait(false);
        await BroadcastAsync(new SettingsChanged("settings", DeckSettings.From(config.Settings))).ConfigureAwait(false);

        foreach (var (keyId, stage) in _stages.ToArray())
        {
            var key = config.FindKey(keyId, out _);
            if (key is not null && stage < key.StageCount)
            {
                continue;
            }

            _stages.TryRemove(keyId, out _);
            if (key is not null)
            {
                await BroadcastAsync(new StageChanged("stage", keyId, 0)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Closes every connection, e.g. with <see cref="TokenRevokedCloseCode"/> after a token rotation.</summary>
    public async Task CloseAllAsync(int closeCode, string reason)
    {
        foreach (var session in _sessions.Values.ToArray())
        {
            await session.CloseAsync((WebSocketCloseStatus)closeCode, reason).ConfigureAwait(false);
        }
    }

    private void HandleMessage(DeckSession session, byte[] payload)
    {
        ClientMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<ClientMessage>(payload, FxJson.Wire);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Ignoring malformed deck message: {Message}", ex.Message);
            return;
        }

        if (string.IsNullOrEmpty(message?.KeyId))
        {
            _logger.LogDebug("Ignoring deck message of type {Type}", message?.Type);
            return;
        }

        switch (message.Type)
        {
            case "press":
                _ = PressAsync(session, message.KeyId);
                break;
            case "release":
                _ = ReleaseAsync(session, message.KeyId);
                break;
            default:
                _logger.LogDebug("Ignoring deck message of type {Type}", message.Type);
                break;
        }
    }

    /// <summary>Runs the current stage's press macro. Tap keys advance their stage here; hold keys advance on release.</summary>
    private async Task PressAsync(DeckSession session, string keyId)
    {
        try
        {
            var key = _config.Current.FindKey(keyId, out _);
            if (key is null)
            {
                await session.SendAsync(new Result("result", keyId, "press", false, "unknownKey", "The key does not exist."), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var (command, releaseCommand) = key.MacrosAt(CurrentStage(key));
            var hold = !string.IsNullOrWhiteSpace(releaseCommand);
            if (key.Action.Type != "command" || (string.IsNullOrWhiteSpace(command) && !hold))
            {
                await session.SendAsync(new Result("result", keyId, "press", false, "noCommand", "The key has no command."), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (!_runningKeys.TryAdd(keyId, 0))
            {
                _logger.LogDebug("Key {KeyId} is already running; press ignored", keyId);
                return;
            }

            try
            {
                if (hold)
                {
                    session.Held[keyId] = 0; // before the macro runs, so a close during it still releases
                }

                var success = true;
                if (!string.IsNullOrWhiteSpace(command))
                {
                    var result = await _executor.ExecuteAsync(command).ConfigureAwait(false);
                    success = result.Success;
                    await session.SendAsync(new Result("result", keyId, "press", result.Success, ReasonName(result.Reason), result.Message), CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await session.SendAsync(new Result("result", keyId, "press", true, "none", null), CancellationToken.None).ConfigureAwait(false);
                }

                if (!hold && success)
                {
                    await AdvanceStageAsync(key).ConfigureAwait(false);
                }
            }
            finally
            {
                _runningKeys.TryRemove(keyId, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling a press of key {KeyId} failed", keyId);
        }
    }

    /// <summary>Runs the current stage's release macro (hold keys only) and then advances the stage.</summary>
    private async Task ReleaseAsync(DeckSession session, string keyId)
    {
        try
        {
            session.Held.TryRemove(keyId, out _);
            var key = _config.Current.FindKey(keyId, out _);
            if (key is null)
            {
                await session.SendAsync(new Result("result", keyId, "release", false, "unknownKey", "The key does not exist."), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var (_, releaseCommand) = key.MacrosAt(CurrentStage(key));
            if (string.IsNullOrWhiteSpace(releaseCommand))
            {
                return; // a tap key: nothing to do on release
            }

            var result = await _executor.ExecuteAsync(releaseCommand).ConfigureAwait(false);
            await session.SendAsync(new Result("result", keyId, "release", result.Success, ReasonName(result.Reason), result.Message), CancellationToken.None).ConfigureAwait(false);
            if (result.Success)
            {
                await AdvanceStageAsync(key).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling a release of key {KeyId} failed", keyId);
        }
    }

    /// <summary>Releases every key the session was still holding when it went away.</summary>
    private async Task ReleaseHeldAsync(DeckSession session)
    {
        try
        {
            foreach (var keyId in session.Held.Keys.ToArray())
            {
                session.Held.TryRemove(keyId, out _);
                var key = _config.Current.FindKey(keyId, out _);
                if (key is null)
                {
                    continue;
                }

                var (_, releaseCommand) = key.MacrosAt(CurrentStage(key));
                if (string.IsNullOrWhiteSpace(releaseCommand))
                {
                    continue;
                }

                _logger.LogInformation("Deck went away while holding key {KeyId}; releasing it", keyId);
                var result = await _executor.ExecuteAsync(releaseCommand).ConfigureAwait(false);
                if (result.Success)
                {
                    await AdvanceStageAsync(key).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Releasing held keys failed");
        }
        finally
        {
            session.Dispose();
        }
    }

    private int CurrentStage(DeckKey key)
    {
        if (!_stages.TryGetValue(key.Id, out var stage))
        {
            return 0;
        }

        if (stage >= key.StageCount)
        {
            _stages.TryRemove(key.Id, out _);
            return 0;
        }

        return stage;
    }

    private async Task AdvanceStageAsync(DeckKey key)
    {
        if (key.StageCount <= 1)
        {
            return;
        }

        var next = (CurrentStage(key) + 1) % key.StageCount;
        if (next == 0)
        {
            _stages.TryRemove(key.Id, out _);
        }
        else
        {
            _stages[key.Id] = next;
        }

        await BroadcastAsync(new StageChanged("stage", key.Id, next)).ConfigureAwait(false);
    }

    private async Task BroadcastAsync(object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), FxJson.Wire);
        foreach (var session in _sessions.Values)
        {
            await session.SendRawAsync(bytes, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task TryCloseAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(status, description, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // already gone
        }
    }

    private sealed class DeckSession : IDisposable
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private bool _disposed;

        public DeckSession(WebSocket socket)
        {
            _socket = socket;
        }

        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>Hold keys this phone has pressed and not yet released.</summary>
        public ConcurrentDictionary<string, byte> Held { get; } = new(StringComparer.Ordinal);

        public Task SendAsync(object message, CancellationToken cancellationToken) =>
            SendRawAsync(JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), FxJson.Wire), cancellationToken);

        public async Task SendRawAsync(byte[] payload, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException)
            {
                // the receive loop will notice and drop the session
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task CloseAsync(WebSocketCloseStatus status, string reason)
        {
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _socket.CloseOutputAsync(status, reason, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                // already gone
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _sendLock.Dispose();
        }
    }
}
