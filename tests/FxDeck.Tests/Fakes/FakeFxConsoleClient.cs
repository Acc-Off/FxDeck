using System.Diagnostics;
using FxDeck.FxConsole;

namespace FxDeck.Tests.Fakes;

/// <summary>In-memory <see cref="IFxConsoleClient"/> that records what was sent.</summary>
internal sealed class FakeFxConsoleClient : IFxConsoleClient
{
    private readonly List<(string Command, long Timestamp)> _sent = [];

    public bool Connected { get; set; } = true;

    /// <summary>Simulated time each send takes (cancellable).</summary>
    public TimeSpan SendLatency { get; set; }

    public IReadOnlyList<(string Command, long Timestamp)> Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToArray();
            }
        }
    }

    public IReadOnlyList<string> SentCommands => Sent.Select(s => s.Command).ToArray();

    public FxConsoleConnectionState State => Connected ? FxConsoleConnectionState.Connected : FxConsoleConnectionState.Disconnected;

#pragma warning disable CS0067 // never raised by the fake
    public event EventHandler<FxConsoleStateChangedEventArgs>? StateChanged;

    public event EventHandler<FxConsoleLineEventArgs>? LineReceived;
#pragma warning restore CS0067

    public void Start()
    {
    }

    public Task StopAsync() => Task.CompletedTask;

    public void UpdateEndpoint(string host, int port)
    {
    }

    public async Task<bool> SendAsync(string command, CancellationToken cancellationToken = default)
    {
        // Mirror the real client's validation.
        FxConsoleProtocol.EncodeCommand(command);

        if (SendLatency > TimeSpan.Zero)
        {
            await Task.Delay(SendLatency, cancellationToken);
        }

        if (!Connected)
        {
            return false;
        }

        lock (_sent)
        {
            _sent.Add((command, Stopwatch.GetTimestamp()));
        }

        return true;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
