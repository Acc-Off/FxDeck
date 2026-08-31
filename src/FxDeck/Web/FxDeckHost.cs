using System.Net;
using System.Threading.RateLimiting;
using CloudflaredKit;
using FxDeck.Commands;
using FxDeck.Config;
using FxDeck.FxConsole;
using FxDeck.Localization;
using FxDeck.Logging;
using FxDeck.NuiInspect;
using FxDeck.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace FxDeck.Web;

public sealed class FxDeckHostOptions
{
    public string DataDirectory { get; set; } = DataPaths.ResolveDataDirectory();

    /// <summary>Overrides <c>settings.adminPort</c> (0 = automatic).</summary>
    public int? AdminPort { get; set; }

    /// <summary>Overrides <c>settings.deckPort</c>.</summary>
    public int? DeckPort { get; set; }

    /// <summary>Interface for the deck listener. Tests bind loopback to avoid the Windows Firewall prompt.</summary>
    public IPAddress DeckBindAddress { get; set; } = IPAddress.Any;

    public string? GameHost { get; set; }

    public int? GamePort { get; set; }

    /// <summary>Hot-reload <c>config.json</c> when edited.</summary>
    public bool WatchConfig { get; set; } = true;

    public bool ConsoleLogging { get; set; } = true;

    /// <summary>Write <c>logs/fxdeck.log</c> under the data directory.</summary>
    public bool FileLogging { get; set; } = true;

    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Information;

    /// <summary>Serve the SPA from this directory instead of the embedded build (<c>FXDECK_WEBROOT</c>).</summary>
    public string? WebRootDirectory { get; set; } = Environment.GetEnvironmentVariable("FXDECK_WEBROOT");

    /// <summary>Runs after all registrations; tests use it to swap in fakes (e.g. the cloudflared process).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }
}

/// <summary>Wires configuration, the console client, the macro executor and the two Kestrel listeners into one <see cref="WebApplication"/>.</summary>
public static class FxDeckHost
{
    public const string SessionRateLimitPolicy = "deck-session";

    public static WebApplication Build(FxDeckHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "FxDeck",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(options.MinimumLogLevel);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        if (options.ConsoleLogging)
        {
            builder.Logging.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        }

        if (options.FileLogging)
        {
            builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(options.DataDirectory, "logs", "fxdeck.log"), options.MinimumLogLevel));
        }

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.DefaultIgnoreCondition = FxJson.Wire.DefaultIgnoreCondition;
            json.SerializerOptions.Encoder = FxJson.Wire.Encoder;
            json.SerializerOptions.ReadCommentHandling = FxJson.Options.ReadCommentHandling;
            json.SerializerOptions.AllowTrailingCommas = true;
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<FirewallService>();
        builder.Services.AddSingleton(_ => new AutoStartService());
        builder.Services.AddSingleton<AppLifecycle>();
        builder.Services.AddSingleton(sp =>
        {
            var store = new ConfigStore(options.DataDirectory, sp.GetRequiredService<ILogger<ConfigStore>>());
            store.Load();
            return store;
        });
        builder.Services.AddSingleton(sp =>
        {
            var store = new DeckTokenStore(options.DataDirectory, sp.GetRequiredService<ILogger<DeckTokenStore>>());
            store.Load();
            return store;
        });
        builder.Services.AddSingleton(new AssetStore(options.DataDirectory));
        builder.Services.AddSingleton(sp =>
        {
            var store = new CommandCacheStore(options.DataDirectory, sp.GetRequiredService<ILogger<CommandCacheStore>>());
            store.Load();
            return store;
        });
        // NUI command extraction (design memo §3.10). Tests swap NuiInspectOptions to point at a fake CDP server.
        builder.Services.AddSingleton<NuiInspectOptions>();
        builder.Services.AddSingleton(sp => new ChatCommandExtractor(sp.GetRequiredService<NuiInspectOptions>(), sp.GetRequiredService<ILogger<ChatCommandExtractor>>()));
        builder.Services.AddSingleton<Localizer>();
        builder.Services.AddSingleton<DeckAuth>();
        builder.Services.AddSingleton<IFxConsoleClient>(sp =>
        {
            var game = sp.GetRequiredService<ConfigStore>().Current.Settings.Game;
            return new TcpFxConsoleClient(
                new FxConsoleClientOptions { Host = options.GameHost ?? game.Host, Port = options.GamePort ?? game.Port },
                sp.GetRequiredService<ILogger<TcpFxConsoleClient>>());
        });
        builder.Services.AddSingleton(sp => new MacroExecutor(sp.GetRequiredService<IFxConsoleClient>(), logger: sp.GetRequiredService<ILogger<MacroExecutor>>()));
        builder.Services.AddSingleton<DeckHub>();
        builder.Services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<ConfigStore>().Current.Settings;
            return new ListenerInfo(sp.GetRequiredService<IServer>(), options.AdminPort ?? settings.AdminPort, options.DeckPort ?? settings.DeckPort, settings.AdminPort, settings.DeckPort);
        });
        builder.Services.AddHostedService<DeckHostedService>();

        // Cloudflare tunnel (design memo §3.5). CloudflaredKit reads its options through IOptionsMonitor on every
        // start, so TunnelOptionsMonitor replaces the default one and is filled in from the settings per start.
        builder.Services.AddTryCloudflare();
        builder.Services.AddSingleton<TunnelOptionsMonitor>();
        builder.Services.AddSingleton<IOptionsMonitor<CloudflaredOptions>>(sp => sp.GetRequiredService<TunnelOptionsMonitor>());
        builder.Services.AddSingleton<TunnelService>();

        builder.Services.AddOptions<KestrelServerOptions>().Configure<ConfigStore>((kestrel, store) =>
        {
            var settings = store.Current.Settings;
            kestrel.AddServerHeader = false;
            // Not ListenLocalhost: it rejects port 0 (dynamic), and 127.0.0.1 alone is enough for the browser on this PC.
            kestrel.Listen(IPAddress.Loopback, options.AdminPort ?? settings.AdminPort);
            kestrel.Listen(options.DeckBindAddress, options.DeckPort ?? settings.DeckPort);
        });

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(SessionRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                RateLimitPartitionKey(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        options.ConfigureServices?.Invoke(builder.Services);

        var app = builder.Build();

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.UseRateLimiter();

        // Security boundary: anything admin-ish only exists on the loopback listener.
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if ((path.StartsWithSegments("/api/admin") || path.StartsWithSegments("/admin"))
                && !context.RequestServices.GetRequiredService<ListenerInfo>().IsAdminConnection(context.Connection))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next();
        });

        var webRoot = ResolveWebRoot(options, app.Logger);
        if (webRoot is not null)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = webRoot,
                OnPrepareResponse = ctx =>
                {
                    var headers = ctx.Context.Response.GetTypedHeaders();
                    headers.CacheControl = ctx.Context.Request.Path.StartsWithSegments("/assets")
                        ? new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(365), Extensions = { new NameValueHeaderValue("immutable") } }
                        : new CacheControlHeaderValue { NoCache = true };
                },
            });
        }

        DeckEndpoints.Map(app);
        AdminEndpoints.Map(app);
        MapSpaFallback(app, webRoot);

        return app;
    }

    /// <summary>
    /// Per-client key for the session rate limiter. Requests through the tunnel all arrive from loopback (cloudflared),
    /// so for those the client address Cloudflare reports is used instead. Only loopback is trusted to send the header.
    /// </summary>
    internal static string RateLimitPartitionKey(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && IPAddress.IsLoopback(remote)
            && context.Request.Headers.TryGetValue("CF-Connecting-IP", out var forwarded)
            && IPAddress.TryParse(forwarded.ToString(), out var client))
        {
            return "cf:" + client;
        }

        return remote?.ToString() ?? "unknown";
    }

    private static IFileProvider? ResolveWebRoot(FxDeckHostOptions options, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.WebRootDirectory))
        {
            if (Directory.Exists(options.WebRootDirectory))
            {
                logger.LogInformation("Serving the web UI from {Directory}", options.WebRootDirectory);
                return new PhysicalFileProvider(Path.GetFullPath(options.WebRootDirectory));
            }

            logger.LogWarning("FXDECK_WEBROOT directory {Directory} does not exist; falling back to the embedded build", options.WebRootDirectory);
        }

        var embedded = new EmbeddedWebRoot();
        if (embedded.FileCount == 0)
        {
            logger.LogWarning("No embedded web UI found (was the frontend built?); only the API is available");
            return null;
        }

        return embedded;
    }

    private static void MapSpaFallback(WebApplication app, IFileProvider? webRoot)
    {
        app.MapFallback(async context =>
        {
            var path = context.Request.Path;
            if (webRoot is null || !HttpMethods.IsGet(context.Request.Method) || path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var index = webRoot.GetFileInfo("/index.html");
            if (!index.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await using var stream = index.CreateReadStream();
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
    }
}

/// <summary>Starts the console client with the host and forwards its events to the decks.</summary>
internal sealed class DeckHostedService : IHostedService
{
    private readonly FxDeckHostOptions _options;
    private readonly ConfigStore _config;
    private readonly DeckTokenStore _tokens;
    private readonly IFxConsoleClient _client;
    private readonly MacroExecutor _executor;
    private readonly DeckHub _hub;
    private readonly ListenerInfo _listeners;
    private readonly TunnelService _tunnel;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DeckHostedService> _logger;

    public DeckHostedService(
        FxDeckHostOptions options,
        ConfigStore config,
        DeckTokenStore tokens,
        IFxConsoleClient client,
        MacroExecutor executor,
        DeckHub hub,
        ListenerInfo listeners,
        TunnelService tunnel,
        IHostApplicationLifetime lifetime,
        ILogger<DeckHostedService> logger)
    {
        _options = options;
        _config = config;
        _tokens = tokens;
        _client = client;
        _executor = executor;
        _hub = hub;
        _listeners = listeners;
        _tunnel = tunnel;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _client.StateChanged += OnGameStateChanged;
        _client.LineReceived += OnConsoleLine;
        _config.Changed += OnConfigChanged;
        _tokens.Rotated += OnTokenRotated;
        _client.Start();

        if (_options.WatchConfig)
        {
            _config.StartWatching();
        }

        _lifetime.ApplicationStarted.Register(() =>
        {
            _listeners.EnsureResolved();
            _logger.LogInformation("Admin listener: http://127.0.0.1:{AdminPort}  Deck listener: {DeckPort}", _listeners.AdminPort, _listeners.DeckPort);

            var tunnel = _config.Current.Settings.Tunnel;
            if (tunnel.AutoStart && !tunnel.IsOff)
            {
                _ = Task.Run(() => _tunnel.StartAsync(_lifetime.ApplicationStopping));
            }
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _client.StateChanged -= OnGameStateChanged;
        _client.LineReceived -= OnConsoleLine;
        _config.Changed -= OnConfigChanged;
        _tokens.Rotated -= OnTokenRotated;
        // ConfigureAwait(false): StopAsync may start on a thread with a SynchronizationContext (the tray's UI thread).
        await _hub.CloseAllAsync((int)System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable, "shutdown").ConfigureAwait(false);
        await _tunnel.StopAsync().ConfigureAwait(false); // never leave a cloudflared process behind
        await _executor.DisposeAsync().ConfigureAwait(false);
        await _client.StopAsync().ConfigureAwait(false);
        // ConfigStore (and its file watcher) is disposed by the container.
    }

    private void OnGameStateChanged(object? sender, FxConsoleStateChangedEventArgs e) => _ = _hub.BroadcastGameStateAsync(e.Current);

    private void OnConsoleLine(object? sender, FxConsoleLineEventArgs e) => _ = _hub.BroadcastConsoleLineAsync(e.Line);

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        if (_options.GameHost is null && _options.GamePort is null)
        {
            _client.UpdateEndpoint(config.Settings.Game.Host, config.Settings.Game.Port);
        }

        _ = _hub.BroadcastConfigAsync(config);
    }

    private void OnTokenRotated(object? sender, string token) => _ = _hub.CloseAllAsync(DeckMessages.TokenRevokedCloseCode, "token revoked");
}
