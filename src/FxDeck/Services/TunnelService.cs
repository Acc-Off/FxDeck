using System.Net.Http;
using CloudflaredKit;
using FxDeck.Config;
using FxDeck.Localization;
using FxDeck.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FxDeck.Services;

public enum TunnelStatus
{
    Stopped,
    Starting,
    Running,
    Error,
}

public enum TunnelErrorPhase
{
    /// <summary>The cloudflared binary could not be downloaded (no internet, proxy, GitHub unreachable).</summary>
    Download,

    /// <summary>cloudflared started but never became ready (no URL within 30 s, invalid token → immediate exit).</summary>
    Start,

    /// <summary>cloudflared died while the tunnel was running.</summary>
    Exited,
}

public sealed record TunnelError(TunnelErrorPhase Phase, string Message, int? ExitCode = null);

/// <summary>Snapshot of the tunnel (design memo §3.5). <see cref="Mode"/> is the mode the tunnel was (or is being) started with.</summary>
public sealed record TunnelState(TunnelStatus Status, string Mode, string? Url, TunnelError? Error)
{
    public static readonly TunnelState Stopped = new(TunnelStatus.Stopped, "off", null, null);

    public bool IsRunning => Status == TunnelStatus.Running;

    public bool IsBusy => Status is TunnelStatus.Starting;
}

/// <summary>
/// Supplies <see cref="CloudflaredOptions"/> to CloudflaredKit. The library reads <c>CurrentValue</c> on every start,
/// so <see cref="TunnelService"/> can rebuild the options from the current settings each time instead of at DI time.
/// </summary>
public sealed class TunnelOptionsMonitor : IOptionsMonitor<CloudflaredOptions>
{
    private volatile CloudflaredOptions _current = new();

    public CloudflaredOptions CurrentValue => _current;

    public CloudflaredOptions Get(string? name) => _current;

    public IDisposable? OnChange(Action<CloudflaredOptions, string?> listener) => null;

    public void Set(CloudflaredOptions options) => _current = options;
}

/// <summary>
/// Wraps CloudflaredKit's <see cref="ICloudflaredService"/> with the state machine the admin UI and the tray need:
/// stopped → starting → running | error, with the public URL and a phase-tagged error (design memo §3.5, UIUX §7).
/// Start and stop are serialised; a stop cancels a start in progress.
/// </summary>
public sealed class TunnelService : IAsyncDisposable
{
    public const string CacheDirectoryName = "cloudflared";

    private readonly ICloudflaredService _cloudflared;
    private readonly ICloudflaredDownloader _downloader;
    private readonly TunnelOptionsMonitor _options;
    private readonly ConfigStore _config;
    private readonly ListenerInfo _listeners;
    private readonly Localizer _l;
    private readonly string _dataDirectory;
    private readonly ILogger<TunnelService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _startCts;
    private TunnelState _state = TunnelState.Stopped;

    public TunnelService(
        ICloudflaredService cloudflared,
        ICloudflaredDownloader downloader,
        TunnelOptionsMonitor options,
        ConfigStore config,
        ListenerInfo listeners,
        Localizer localizer,
        FxDeckHostOptions hostOptions,
        ILogger<TunnelService> logger)
    {
        _l = localizer;
        _cloudflared = cloudflared;
        _downloader = downloader;
        _options = options;
        _config = config;
        _listeners = listeners;
        _dataDirectory = hostOptions.DataDirectory;
        _logger = logger;
        _cloudflared.TunnelExitedUnexpectedly += OnExitedUnexpectedly;
    }

    public TunnelState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>Raised on whichever thread changed the state (thread pool or a request thread); the tray marshals it.</summary>
    public event EventHandler<TunnelState>? Changed;

    public string CacheDirectory => Path.Combine(_dataDirectory, CacheDirectoryName);

    /// <summary>
    /// Starts the tunnel using the current settings (mode "off" starts a TryCloudflare tunnel) and returns once it is
    /// running or has failed. A no-op when already running or starting.
    /// </summary>
    public async Task<TunnelState> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource cts;
        try
        {
            var current = State;
            if (current.Status is TunnelStatus.Running or TunnelStatus.Starting)
            {
                return current;
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_sync)
            {
                _startCts = cts;
            }

            return await StartCoreAsync(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _startCts?.Dispose();
                _startCts = null;
            }

            _gate.Release();
        }
    }

    private async Task<TunnelState> StartCoreAsync(CancellationToken cancellationToken)
    {
        var settings = _config.Current.Settings.Tunnel;
        var mode = settings.IsNamed ? "named" : "try";
        if (settings.IsNamed && string.IsNullOrWhiteSpace(settings.NamedToken))
        {
            return Set(new TunnelState(TunnelStatus.Error, mode, null, new TunnelError(TunnelErrorPhase.Start, _l.T("tunnel.tokenMissing"))));
        }

        _listeners.EnsureResolved();
        _options.Set(new CloudflaredOptions
        {
            // Not "localhost": Go may resolve it to ::1 and the deck listener is IPv4.
            LocalHostName = "127.0.0.1",
            LocalPort = _listeners.DeckPort,
            TunnelToken = settings.IsNamed ? settings.NamedToken : null,
            CacheDirectory = CacheDirectory,
        });

        Set(new TunnelState(TunnelStatus.Starting, mode, null, null));
        _logger.LogInformation("Starting the {Mode} tunnel to 127.0.0.1:{Port}", mode, _listeners.DeckPort);

        try
        {
            await _downloader.EnsureExecutableAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Set(TunnelState.Stopped);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "cloudflared download failed");
            return Set(new TunnelState(TunnelStatus.Error, mode, null, new TunnelError(TunnelErrorPhase.Download, DescribeDownloadFailure(ex))));
        }

        try
        {
            var info = await _cloudflared.StartAsync(cancellationToken).ConfigureAwait(false);
            var url = settings.IsNamed ? NormalizeUrl(settings.NamedUrl) : NormalizeUrl(info.PublicUrl);
            if (cancellationToken.IsCancellationRequested)
            {
                // Stop raced with a successful start: tear down what we just started.
                await _cloudflared.StopAsync().ConfigureAwait(false);
                return Set(TunnelState.Stopped);
            }

            _logger.LogInformation("Tunnel running: {Url}", url ?? "(no public URL configured)");
            return Set(new TunnelState(TunnelStatus.Running, mode, url, null));
        }
        catch (OperationCanceledException)
        {
            return Set(TunnelState.Stopped);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "cloudflared did not start");
            return Set(new TunnelState(TunnelStatus.Error, mode, null, new TunnelError(TunnelErrorPhase.Start, DescribeStartFailure(ex, settings.IsNamed))));
        }
    }

    public async Task<TunnelState> StopAsync()
    {
        lock (_sync)
        {
            _startCts?.Cancel();
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await _cloudflared.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stopping cloudflared failed");
            }

            return Set(TunnelState.Stopped);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnExitedUnexpectedly(int exitCode)
    {
        var current = State;
        if (current.Status != TunnelStatus.Running)
        {
            return;
        }

        _logger.LogWarning("cloudflared exited unexpectedly (exit code {ExitCode})", exitCode);
        Set(new TunnelState(TunnelStatus.Error, current.Mode, null, new TunnelError(TunnelErrorPhase.Exited, _l.T("tunnel.exitedUnexpectedly", exitCode), exitCode)));
    }

    private TunnelState Set(TunnelState state)
    {
        lock (_sync)
        {
            _state = state;
        }

        Changed?.Invoke(this, state);
        return state;
    }

    /// <summary>Trailing slash removed so the deck URL can be appended; null when empty or not an http(s) URL.</summary>
    public static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !ConfigValidator.IsHttpUrl(url.Trim()))
        {
            return null;
        }

        return url.Trim().TrimEnd('/');
    }

    private string DescribeDownloadFailure(Exception ex) => ex switch
    {
        CloudflaredUnsupportedException => _l.T("tunnel.unsupported", ex.Message),
        HttpRequestException or TaskCanceledException or IOException => _l.T("tunnel.downloadFailed", ex.GetBaseException().Message),
        _ => _l.T("tunnel.downloadFailedGeneric", ex.GetBaseException().Message),
    };

    private string DescribeStartFailure(Exception ex, bool named) => ex switch
    {
        TimeoutException => _l.T("tunnel.timeout"),
        InvalidOperationException when named => _l.T("tunnel.exitedNamed", ex.Message),
        InvalidOperationException => _l.T("tunnel.exitedOnStart", ex.Message),
        _ => _l.T("tunnel.startFailed", ex.GetBaseException().Message),
    };

    public async ValueTask DisposeAsync()
    {
        _cloudflared.TunnelExitedUnexpectedly -= OnExitedUnexpectedly;
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
