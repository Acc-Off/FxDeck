using CloudflaredKit;
using CloudflaredKit.Models;

namespace FxDeck.Tests.Fakes;

/// <summary>Stands in for the cloudflared process: the tests decide whether a start succeeds, fails or hangs.</summary>
internal sealed class FakeCloudflaredService : ICloudflaredService
{
    public TunnelInfo? ActiveTunnel { get; private set; }

    public event Action<int>? TunnelExitedUnexpectedly;

    /// <summary>URL reported for a successful start (null mimics a named tunnel).</summary>
    public string? PublicUrl { get; set; } = "https://fake-words-here.trycloudflare.com";

    /// <summary>Thrown by <see cref="StartAsync"/> when set.</summary>
    public Exception? StartException { get; set; }

    /// <summary>When set, <see cref="StartAsync"/> waits for this before returning (to test cancellation).</summary>
    public TaskCompletionSource? StartGate { get; set; }

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<TunnelInfo> StartAsync(CancellationToken cancellationToken = default)
    {
        StartCalls++;
        if (StartGate is not null)
        {
            await StartGate.Task.WaitAsync(cancellationToken);
        }

        if (StartException is not null)
        {
            throw StartException;
        }

        ActiveTunnel = new TunnelInfo { PublicUrl = PublicUrl };
        return ActiveTunnel;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCalls++;
        ActiveTunnel = null;
        return Task.CompletedTask;
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void SimulateCrash(int exitCode)
    {
        ActiveTunnel = null;
        TunnelExitedUnexpectedly?.Invoke(exitCode);
    }
}

internal sealed class FakeCloudflaredDownloader : ICloudflaredDownloader
{
    public Exception? Exception { get; set; }

    public int Calls { get; private set; }

    public Task<string> EnsureExecutableAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Exception is null ? Task.FromResult(@"C:\fake\cloudflared.exe") : Task.FromException<string>(Exception);
    }
}
