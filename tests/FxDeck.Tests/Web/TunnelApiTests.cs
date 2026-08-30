using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudflaredKit;
using FxDeck.Config;
using FxDeck.Emulator;
using FxDeck.Services;
using FxDeck.Tests.Fakes;
using FxDeck.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static FxDeck.Tests.TestHelpers;

namespace FxDeck.Tests.Web;

/// <summary>Roadmap step 4: the tunnel state machine and its admin API, with cloudflared replaced by fakes.</summary>
public class TunnelApiTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fxdeck-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeCloudflaredService _cloudflared = new();
    private readonly FakeCloudflaredDownloader _downloader = new();
    private FxConsoleEmulator _emulator = null!;
    private WebApplication _app = null!;
    private HttpClient _admin = null!;
    private int _deckPort;

    public async Task InitializeAsync()
    {
        _emulator = new FxConsoleEmulator(EmulatorOptions());
        await _emulator.StartAsync();
        var adminPort = GetFreePort();
        _deckPort = GetFreePort();
        _app = FxDeckHost.Build(new FxDeckHostOptions
        {
            DataDirectory = _dir,
            AdminPort = adminPort,
            DeckPort = _deckPort,
            DeckBindAddress = IPAddress.Loopback,
            GamePort = _emulator.Port,
            WatchConfig = false,
            ConsoleLogging = false,
            FileLogging = false,
            MinimumLogLevel = LogLevel.None,
            WebRootDirectory = null,
            ConfigureServices = services =>
            {
                services.AddSingleton<ICloudflaredService>(_cloudflared);
                services.AddSingleton<ICloudflaredDownloader>(_downloader);
            },
        });
        await _app.StartAsync();
        _admin = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{adminPort}/") };
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _emulator.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private static bool IsNullOrMissing(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null; // FxJson.Wire omits nulls

    private ConfigStore Store => _app.Services.GetRequiredService<ConfigStore>();

    private string Token => _app.Services.GetRequiredService<DeckTokenStore>().Token;

    private TunnelOptionsMonitor Options => _app.Services.GetRequiredService<TunnelOptionsMonitor>();

    private async Task<JsonElement> StatusTunnelAsync()
    {
        var status = await _admin.GetFromJsonAsync<JsonElement>("/api/admin/status");
        return status.GetProperty("tunnel");
    }

    private async Task<(HttpStatusCode Code, JsonElement Tunnel)> StartAsync()
    {
        var response = await _admin.PostAsync("/api/admin/tunnel/start", null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body.GetProperty("tunnel"));
    }

    private void SaveTunnelSettings(Action<TunnelSettings> mutate)
    {
        var config = Store.Current;
        mutate(config.Settings.Tunnel);
        Store.Save(config);
    }

    [Fact]
    public async Task TunnelIsStoppedByDefaultAndHasNoQr()
    {
        var tunnel = await StatusTunnelAsync();

        Assert.Equal("off", tunnel.GetProperty("mode").GetString());
        Assert.Equal("stopped", tunnel.GetProperty("status").GetString());
        Assert.True(IsNullOrMissing(tunnel, "url"));
        Assert.True(IsNullOrMissing(tunnel, "error"));

        var qr = await _admin.GetAsync("/api/admin/qr?kind=tunnel");
        Assert.Equal(HttpStatusCode.NotFound, qr.StatusCode);
        Assert.Contains("tunnelNotRunning", await qr.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StartWithModeOffRunsTryCloudflareAgainstTheDeckPort()
    {
        var (code, tunnel) = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("running", tunnel.GetProperty("status").GetString());
        Assert.Equal("try", tunnel.GetProperty("activeMode").GetString());
        Assert.Equal("off", tunnel.GetProperty("mode").GetString()); // the setting is untouched
        Assert.Equal(_cloudflared.PublicUrl, tunnel.GetProperty("url").GetString());
        Assert.Equal($"{_cloudflared.PublicUrl}/?t={Uri.EscapeDataString(Token)}", tunnel.GetProperty("deckUrl").GetString());

        Assert.Equal("127.0.0.1", Options.CurrentValue.LocalHostName);
        Assert.Equal(_deckPort, Options.CurrentValue.LocalPort);
        Assert.Null(Options.CurrentValue.TunnelToken);
        Assert.Equal(Path.Combine(_dir, TunnelService.CacheDirectoryName), Options.CurrentValue.CacheDirectory);
        Assert.Equal(1, _downloader.Calls);

        var status = await StatusTunnelAsync();
        Assert.Equal("running", status.GetProperty("status").GetString());

        var qr = await _admin.GetAsync("/api/admin/qr?kind=tunnel");
        Assert.Equal(HttpStatusCode.OK, qr.StatusCode);
        Assert.Equal("image/png", qr.Content.Headers.ContentType?.MediaType);

        // Starting again is a no-op.
        (code, _) = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(1, _cloudflared.StartCalls);

        var stop = await _admin.PostAsync("/api/admin/tunnel/stop", null);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        var stopped = (await stop.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tunnel");
        Assert.Equal("stopped", stopped.GetProperty("status").GetString());
        Assert.True(_cloudflared.StopCalls >= 1);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync("/api/admin/qr?kind=tunnel")).StatusCode);
    }

    [Fact]
    public async Task DownloadFailureIsReportedInTheDownloadPhaseAndCanBeRetried()
    {
        _downloader.Exception = new HttpRequestException("No such host is known (github.com)");

        var (code, tunnel) = await StartAsync();

        Assert.Equal(HttpStatusCode.BadGateway, code);
        Assert.Equal("error", tunnel.GetProperty("status").GetString());
        Assert.Equal("download", tunnel.GetProperty("error").GetProperty("phase").GetString());
        Assert.Contains("github.com", tunnel.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(0, _cloudflared.StartCalls);
        Assert.Equal("error", (await StatusTunnelAsync()).GetProperty("status").GetString());

        _downloader.Exception = null;
        (code, tunnel) = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("running", tunnel.GetProperty("status").GetString());
    }

    [Fact]
    public async Task StartFailureIsReportedInTheStartPhase()
    {
        _cloudflared.StartException = new TimeoutException("Timed out waiting for cloudflared to emit a TryCloudflare URL.");

        var (code, tunnel) = await StartAsync();

        Assert.Equal(HttpStatusCode.BadGateway, code);
        Assert.Equal("start", tunnel.GetProperty("error").GetProperty("phase").GetString());
        Assert.Contains("公開 URL", tunnel.GetProperty("error").GetProperty("message").GetString());
        Assert.True(IsNullOrMissing(tunnel, "url"));
    }

    [Fact]
    public async Task UnexpectedExitBecomesAnErrorWithTheExitCode()
    {
        await StartAsync();

        _cloudflared.SimulateCrash(3);

        var tunnel = await StatusTunnelAsync();
        Assert.Equal("error", tunnel.GetProperty("status").GetString());
        Assert.Equal("exited", tunnel.GetProperty("error").GetProperty("phase").GetString());
        Assert.Equal(3, tunnel.GetProperty("error").GetProperty("exitCode").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync("/api/admin/qr?kind=tunnel")).StatusCode);

        // A stale exit notification after a stop is ignored.
        await _admin.PostAsync("/api/admin/tunnel/stop", null);
        _cloudflared.SimulateCrash(4);
        Assert.Equal("stopped", (await StatusTunnelAsync()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task NamedModeRequiresATokenAndUsesTheConfiguredUrl()
    {
        SaveTunnelSettings(t => t.Mode = "named");

        var (code, tunnel) = await StartAsync();
        Assert.Equal(HttpStatusCode.BadGateway, code);
        Assert.Equal("start", tunnel.GetProperty("error").GetProperty("phase").GetString());
        Assert.Contains("トークン", tunnel.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(0, _cloudflared.StartCalls);

        _cloudflared.PublicUrl = null; // cloudflared reports no URL for named tunnels
        SaveTunnelSettings(t =>
        {
            t.NamedToken = "eyJhbGciOi.fake";
            t.NamedUrl = "https://deck.example.com/";
        });

        (code, tunnel) = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("running", tunnel.GetProperty("status").GetString());
        Assert.Equal("named", tunnel.GetProperty("activeMode").GetString());
        Assert.Equal("https://deck.example.com", tunnel.GetProperty("url").GetString());
        Assert.Equal($"https://deck.example.com/?t={Uri.EscapeDataString(Token)}", tunnel.GetProperty("deckUrl").GetString());
        Assert.Equal("eyJhbGciOi.fake", Options.CurrentValue.TunnelToken);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync("/api/admin/qr?kind=tunnel")).StatusCode);
    }

    [Fact]
    public async Task NamedModeWithoutAUrlRunsButHasNoQr()
    {
        _cloudflared.PublicUrl = null;
        SaveTunnelSettings(t =>
        {
            t.Mode = "named";
            t.NamedToken = "eyJhbGciOi.fake";
        });

        var (code, tunnel) = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("running", tunnel.GetProperty("status").GetString());
        Assert.True(IsNullOrMissing(tunnel, "url"));
        var qr = await _admin.GetAsync("/api/admin/qr?kind=tunnel");
        Assert.Equal(HttpStatusCode.NotFound, qr.StatusCode);
        Assert.Contains("tunnelUrlNotConfigured", await qr.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StopCancelsAStartInProgress()
    {
        _cloudflared.StartGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starting = _admin.PostAsync("/api/admin/tunnel/start", null);
        await WaitForAsync(() => _cloudflared.StartCalls == 1, "start to be in progress");
        Assert.Equal("starting", (await StatusTunnelAsync()).GetProperty("status").GetString());

        var stop = await _admin.PostAsync("/api/admin/tunnel/stop", null);

        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        var started = await starting;
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal("stopped", (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tunnel").GetProperty("status").GetString());
        Assert.Equal("stopped", (await StatusTunnelAsync()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ShutdownStopsTheTunnel()
    {
        await StartAsync();
        var before = _cloudflared.StopCalls;

        await _app.StopAsync();

        Assert.True(_cloudflared.StopCalls > before);
        Assert.Null(_cloudflared.ActiveTunnel);
    }

    [Fact]
    public async Task TunnelUrlsAreNormalised()
    {
        Assert.Equal("https://a.example.com", TunnelService.NormalizeUrl(" https://a.example.com/ "));
        Assert.Equal("https://a.example.com/deck", TunnelService.NormalizeUrl("https://a.example.com/deck/"));
        Assert.Null(TunnelService.NormalizeUrl(""));
        Assert.Null(TunnelService.NormalizeUrl("deck.example.com"));
        Assert.Null(TunnelService.NormalizeUrl("ftp://deck.example.com"));
        Assert.Equal("https://x.trycloudflare.com/?t=a%2Bb", DeckLinks.TunnelUrl("https://x.trycloudflare.com", "a+b"));
        await Task.CompletedTask;
    }
}

/// <summary>`tunnel.autoStart` starts the tunnel when the host starts.</summary>
public class TunnelAutoStartTests
{
    [Fact]
    public async Task AutoStartRunsTheTunnelAtBoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fxdeck-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = AppConfig.CreateDefault();
        config.Settings.Tunnel.Mode = "try";
        config.Settings.Tunnel.AutoStart = true;
        await File.WriteAllTextAsync(Path.Combine(dir, ConfigStore.FileName), JsonSerializer.Serialize(config, FxJson.Options));

        var cloudflared = new FakeCloudflaredService();
        await using var emulator = new FxConsoleEmulator(EmulatorOptions());
        await emulator.StartAsync();
        var app = FxDeckHost.Build(new FxDeckHostOptions
        {
            DataDirectory = dir,
            AdminPort = GetFreePort(),
            DeckPort = GetFreePort(),
            DeckBindAddress = IPAddress.Loopback,
            GamePort = emulator.Port,
            WatchConfig = false,
            ConsoleLogging = false,
            FileLogging = false,
            MinimumLogLevel = LogLevel.None,
            WebRootDirectory = null,
            ConfigureServices = services =>
            {
                services.AddSingleton<ICloudflaredService>(cloudflared);
                services.AddSingleton<ICloudflaredDownloader>(new FakeCloudflaredDownloader());
            },
        });
        try
        {
            await app.StartAsync();
            var tunnel = app.Services.GetRequiredService<TunnelService>();

            await WaitForAsync(() => tunnel.State.IsRunning, "tunnel to auto-start");
            Assert.Equal(cloudflared.PublicUrl, tunnel.State.Url);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }
}
