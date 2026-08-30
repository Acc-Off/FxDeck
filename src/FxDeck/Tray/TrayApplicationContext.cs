using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using FxDeck.Config;
using FxDeck.FxConsole;
using FxDeck.Localization;
using FxDeck.Services;
using FxDeck.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FxDeck.Tray;

/// <summary>Tray icon + context menu (design memo §3.6, UIUX §3). Owns the web host's lifetime from the UI thread.</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly WebApplication _app;
    private readonly IFxConsoleClient _client;
    private readonly ConfigStore _config;
    private readonly Localizer _l;
    private readonly ILogger _logger;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _open;
    private readonly ToolStripMenuItem _copyDeckUrl;
    private readonly ToolStripMenuItem _openDataDir;
    private readonly ToolStripMenuItem _exit;
    private readonly ToolStripMenuItem _gameStatus;
    private readonly TunnelService _tunnel;
    private readonly ToolStripMenuItem _tunnelToggle;
    private readonly ToolStripMenuItem _tunnelCopy;
    private readonly ToolStripMenuItem _tunnelStatus;
    private readonly Control _invoker = new();
    private readonly Icon _connectedIcon = TrayIcons.Create(connected: true);
    private readonly Icon _disconnectedIcon = TrayIcons.Create(connected: false);
    private Lang _lang;
    private bool _exiting;

    public TrayApplicationContext(WebApplication app, string adminUrl, bool openAdminOnStart)
    {
        _app = app;
        _client = app.Services.GetRequiredService<IFxConsoleClient>();
        _config = app.Services.GetRequiredService<ConfigStore>();
        _l = app.Services.GetRequiredService<Localizer>();
        _lang = _l.Current;
        _logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FxDeck.Tray");
        _invoker.CreateControl(); // gives us a handle to marshal events onto the UI thread
        AdminUrl = adminUrl;

        var menu = new ContextMenuStrip();
        _open = new ToolStripMenuItem(string.Empty, null, (_, _) => OpenAdmin()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(_open);
        _copyDeckUrl = new ToolStripMenuItem(string.Empty, null, (_, _) => CopyDeckUrl());
        menu.Items.Add(_copyDeckUrl);
        _openDataDir = new ToolStripMenuItem(string.Empty, null, (_, _) => OpenDataDirectory());
        menu.Items.Add(_openDataDir);
        menu.Items.Add(new ToolStripSeparator());
        _tunnel = app.Services.GetRequiredService<TunnelService>();
        _tunnelToggle = new ToolStripMenuItem(string.Empty, null, (_, _) => ToggleTunnel());
        _tunnelCopy = new ToolStripMenuItem(string.Empty, null, (_, _) => CopyTunnelUrl()) { Visible = false };
        _tunnelStatus = new ToolStripMenuItem(string.Empty) { Enabled = false };
        menu.Items.Add(_tunnelToggle);
        menu.Items.Add(_tunnelCopy);
        menu.Items.Add(new ToolStripSeparator());
        _gameStatus = new ToolStripMenuItem(string.Empty) { Enabled = false };
        menu.Items.Add(_gameStatus);
        menu.Items.Add(_tunnelStatus);
        menu.Items.Add(new ToolStripSeparator());
        _exit = new ToolStripMenuItem(string.Empty, null, (_, _) => Exit());
        menu.Items.Add(_exit);

        _icon = new NotifyIcon
        {
            Icon = _client.State == FxConsoleConnectionState.Connected ? _connectedIcon : _disconnectedIcon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenAdmin();
        ApplyTexts();

        _client.StateChanged += OnGameStateChanged;
        _tunnel.Changed += OnTunnelChanged;
        _config.Changed += OnConfigChanged;
        app.Services.GetRequiredService<AppLifecycle>().RestartRequested += (_, _) =>
        {
            if (_invoker.IsHandleCreated)
            {
                _invoker.BeginInvoke(Restart);
            }
        };

        if (openAdminOnStart)
        {
            OpenAdmin();
        }
        else
        {
            _icon.ShowBalloonTip(3000, "FxDeck", _l.T("tray.started"), ToolTipIcon.None);
        }
    }

    public string AdminUrl { get; }

    private string GameStatusText(FxConsoleConnectionState state) => state switch
    {
        FxConsoleConnectionState.Connected => T("tray.game.connected"),
        FxConsoleConnectionState.Connecting => T("tray.game.connecting"),
        _ => T("tray.game.disconnected"),
    };

    private string TooltipText(FxConsoleConnectionState state) => $"FxDeck — {GameStatusText(state)}";

    private string TunnelStatusText(TunnelState state) => state.Status switch
    {
        TunnelStatus.Starting => T("tray.tunnel.starting"),
        TunnelStatus.Running => T("tray.tunnel.running", state.Url ?? T("tray.tunnel.noUrl")),
        TunnelStatus.Error => T("tray.tunnel.error"),
        _ => T("tray.tunnel.stopped"),
    };

    /// <summary>Menu text in the language cached at the last (re)apply — the UI thread must not race the config store.</summary>
    private string T(string key, params object?[] args) => Strings.Get(_lang, key, args);

    /// <summary>(Re)writes every static and dynamic text; called at start and when the language setting changes.</summary>
    private void ApplyTexts()
    {
        _open.Text = T("tray.openAdmin");
        _copyDeckUrl.Text = T("tray.copyDeckUrl");
        _openDataDir.Text = T("tray.openDataDir");
        _exit.Text = T("tray.exit");
        _gameStatus.Text = GameStatusText(_client.State);
        _icon.Text = TooltipText(_client.State);
        ApplyTunnelState(_tunnel.State);
    }

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        var lang = Strings.Resolve(config.Settings.Language);
        if (lang == _lang || _exiting || !_invoker.IsHandleCreated)
        {
            return;
        }

        try
        {
            _invoker.BeginInvoke(() =>
            {
                _lang = lang;
                ApplyTexts();
            });
        }
        catch (InvalidOperationException)
        {
            // handle destroyed while exiting
        }
    }

    private void OnTunnelChanged(object? sender, TunnelState state)
    {
        if (_exiting || !_invoker.IsHandleCreated)
        {
            return;
        }

        try
        {
            _invoker.BeginInvoke(() => ApplyTunnelState(state));
        }
        catch (InvalidOperationException)
        {
            // handle destroyed while exiting
        }
    }

    private void ApplyTunnelState(TunnelState state)
    {
        _tunnelStatus.Text = TunnelStatusText(state);
        _tunnelToggle.Text = state.Status is TunnelStatus.Running or TunnelStatus.Starting ? T("tray.tunnelStop") : T("tray.tunnelStart");
        _tunnelToggle.Enabled = state.Status != TunnelStatus.Starting;
        _tunnelCopy.Text = T("tray.tunnelCopyUrl");
        _tunnelCopy.Visible = state.IsRunning && state.Url is not null;
    }

    private void ToggleTunnel()
    {
        var state = _tunnel.State;
        if (state.Status is TunnelStatus.Running or TunnelStatus.Starting)
        {
            _ = Task.Run(_tunnel.StopAsync);
            return;
        }

        _icon.ShowBalloonTip(3000, "FxDeck", T("tray.tunnelStarting"), ToolTipIcon.None);
        _ = Task.Run(async () =>
        {
            var result = await _tunnel.StartAsync().ConfigureAwait(false);
            if (_exiting || !_invoker.IsHandleCreated)
            {
                return;
            }

            _invoker.BeginInvoke(() => _icon.ShowBalloonTip(
                5000,
                "FxDeck",
                result.Status == TunnelStatus.Running
                    ? T("tray.tunnelRunning", result.Url ?? T("tray.tunnel.noUrl"))
                    : T("tray.tunnelFailed", result.Error?.Message),
                result.Status == TunnelStatus.Running ? ToolTipIcon.None : ToolTipIcon.Warning));
        });
    }

    private void CopyTunnelUrl()
    {
        var state = _tunnel.State;
        if (!state.IsRunning || state.Url is null)
        {
            return;
        }

        var tokens = _app.Services.GetRequiredService<DeckTokenStore>();
        Clipboard.SetText(DeckLinks.TunnelUrl(state.Url, tokens.Token));
        _icon.ShowBalloonTip(2000, "FxDeck", T("tray.tunnelUrlCopied"), ToolTipIcon.None);
    }

    private void OnGameStateChanged(object? sender, FxConsoleStateChangedEventArgs e)
    {
        if (_exiting || !_invoker.IsHandleCreated)
        {
            return;
        }

        try
        {
            _invoker.BeginInvoke(() =>
            {
                _gameStatus.Text = GameStatusText(e.Current);
                _icon.Icon = e.Current == FxConsoleConnectionState.Connected ? _connectedIcon : _disconnectedIcon;
                _icon.Text = TooltipText(e.Current);
            });
        }
        catch (InvalidOperationException)
        {
            // handle destroyed while exiting
        }
    }

    private void OpenAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AdminUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not open the browser: {Message}", ex.Message);
            _icon.ShowBalloonTip(5000, "FxDeck", T("tray.browserFailed", AdminUrl), ToolTipIcon.Warning);
        }
    }

    /// <summary>Opens the data directory (config.json, logs, assets) in Explorer.</summary>
    private void OpenDataDirectory()
    {
        var directory = _config.Directory;
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not open the data directory: {Message}", ex.Message);
            _icon.ShowBalloonTip(5000, "FxDeck", T("tray.openDataDirFailed", directory), ToolTipIcon.Warning);
        }
    }

    private void CopyDeckUrl()
    {
        var listeners = _app.Services.GetRequiredService<ListenerInfo>();
        var tokens = _app.Services.GetRequiredService<DeckTokenStore>();
        var lan = LanAddress.Detect(_config.Current.Settings.LanAdapter);
        if (lan is null)
        {
            _icon.ShowBalloonTip(5000, "FxDeck", T("tray.noLan"), ToolTipIcon.Warning);
            return;
        }

        Clipboard.SetText(DeckLinks.LanUrl(lan, listeners.DeckPort, tokens.Token));
        _icon.ShowBalloonTip(2000, "FxDeck", T("tray.deckUrlCopied"), ToolTipIcon.None);
    }

    /// <summary>Stops everything, then launches a fresh process with the same arguments (the mutex is released first).</summary>
    private void Restart()
    {
        if (_exiting)
        {
            return;
        }

        RestartRequested = true;
        Exit();
    }

    /// <summary>Set when <see cref="Restart"/> was used; Program relaunches after <c>Application.Run</c> returns.</summary>
    public bool RestartRequested { get; private set; }

    /// <summary>
    /// Stops the host without blocking the UI thread: the hosted services' StopAsync starts on the caller's
    /// thread, so blocking here with GetResult() would deadlock on the WinForms SynchronizationContext.
    /// </summary>
    private async void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _client.StateChanged -= OnGameStateChanged;
        _tunnel.Changed -= OnTunnelChanged;
        _config.Changed -= OnConfigChanged;
        _icon.Visible = false;

        var shutdown = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(cts.Token).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        });

        try
        {
            // Keep pumping messages while we wait; give up after 8 s so Exit always terminates the process.
            await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(8)));
            if (!shutdown.IsCompleted)
            {
                _logger.LogWarning("Shutdown timed out; exiting anyway");
            }
            else if (shutdown.IsFaulted)
            {
                _logger.LogWarning("Shutdown did not complete cleanly: {Message}", shutdown.Exception?.GetBaseException().Message);
            }
        }
        finally
        {
            ExitThread();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _invoker.Dispose();
        }

        base.Dispose(disposing);
    }
}
