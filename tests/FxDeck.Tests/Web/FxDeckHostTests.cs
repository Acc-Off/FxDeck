using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FxDeck.Config;
using FxDeck.Emulator;
using FxDeck.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static FxDeck.Tests.TestHelpers;

namespace FxDeck.Tests.Web;

/// <summary>Integration tests of the two Kestrel listeners, the token/cookie flow and the deck WebSocket.</summary>
public class FxDeckHostTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fxdeck-tests", Guid.NewGuid().ToString("N"));
    private FxConsoleEmulator _emulator = null!;
    private WebApplication _app = null!;
    private int _adminPort;
    private int _deckPort;

    public async Task InitializeAsync()
    {
        _emulator = new FxConsoleEmulator(EmulatorOptions());
        await _emulator.StartAsync();
        _adminPort = GetFreePort();
        _deckPort = GetFreePort();
        _app = FxDeckHost.Build(new FxDeckHostOptions
        {
            DataDirectory = _dir,
            AdminPort = _adminPort,
            DeckPort = _deckPort,
            DeckBindAddress = IPAddress.Loopback,
            GamePort = _emulator.Port,
            WatchConfig = true,
            ConsoleLogging = false,
            MinimumLogLevel = LogLevel.None,
            WebRootDirectory = null,
        });
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _emulator.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private string Token => _app.Services.GetRequiredService<DeckTokenStore>().Token;

    private string Session => _app.Services.GetRequiredService<DeckAuth>().SessionValue();

    private HttpClient Client(int port, string? cookie = null)
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        if (cookie is not null)
        {
            client.DefaultRequestHeaders.Add("Cookie", $"{DeckAuth.CookieName}={cookie}");
        }

        return client;
    }

    private async Task<ClientWebSocket> OpenDeckSocketAsync(string? cookie = null)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Cookie", $"{DeckAuth.CookieName}={cookie ?? Session}");
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_deckPort}/api/deck/ws"), CancellationToken.None);
        return socket;
    }

    private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket socket, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        var buffer = new byte[64 * 1024];
        var total = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException($"closed: {(int?)result.CloseStatus} {result.CloseStatusDescription}");
            }

            total += result.Count;
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, total)).RootElement;
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, FxJson.Wire);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    [Fact]
    public async Task ExchangesTheTokenForASessionCookie()
    {
        using var client = Client(_deckPort);

        var bad = await client.PostAsync("/api/deck/session?t=wrong", null);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.False(bad.Headers.Contains("Set-Cookie"));

        var good = await client.PostAsync($"/api/deck/session?t={Uri.EscapeDataString(Token)}", null);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var cookie = Assert.Single(good.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{DeckAuth.CookieName}={Session};", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProfileRequiresTheSessionCookie()
    {
        using var anonymous = Client(_deckPort);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/deck/profile")).StatusCode);

        using var wrong = Client(_deckPort, "not-a-session");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/deck/profile")).StatusCode);

        using var authenticated = Client(_deckPort, Session);
        var response = await authenticated.GetAsync("/api/deck/profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var hello = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("hello", hello.GetProperty("type").GetString());
        Assert.Equal("Default", hello.GetProperty("profiles")[0].GetProperty("name").GetString());
        Assert.Equal("dark", hello.GetProperty("settings").GetProperty("theme").GetString());
        Assert.True(response.Headers.Contains("Set-Cookie"), "the cookie lifetime should slide");
    }

    [Fact]
    public async Task AdminApiOnlyExistsOnTheAdminListener()
    {
        using var admin = Client(_adminPort);
        var status = await admin.GetAsync("/api/admin/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var json = await status.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_deckPort, json.GetProperty("deckPort").GetInt32());
        Assert.Equal(_adminPort, json.GetProperty("adminPort").GetInt32());
        Assert.Equal(0, json.GetProperty("connectedDecks").GetInt32());

        using var deck = Client(_deckPort);
        Assert.Equal(HttpStatusCode.NotFound, (await deck.GetAsync("/api/admin/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await deck.GetAsync("/api/admin/config")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await deck.PostAsync("/api/admin/token/rotate", null)).StatusCode);
    }

    [Fact]
    public async Task AutomaticAdminPortIsResolvedAfterStart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fxdeck-tests", Guid.NewGuid().ToString("N"));
        var deckPort = GetFreePort();
        var app = FxDeckHost.Build(new FxDeckHostOptions
        {
            DataDirectory = dir,
            AdminPort = 0, // the production default
            DeckPort = deckPort,
            DeckBindAddress = IPAddress.Loopback,
            GamePort = _emulator.Port,
            WatchConfig = false,
            ConsoleLogging = false,
            MinimumLogLevel = LogLevel.None,
        });
        try
        {
            await app.StartAsync();
            var listeners = app.Services.GetRequiredService<ListenerInfo>();
            listeners.EnsureResolved();

            Assert.True(listeners.IsResolved);
            Assert.NotEqual(0, listeners.AdminPort);
            Assert.NotEqual(deckPort, listeners.AdminPort);
            Assert.Equal(deckPort, listeners.DeckPort);

            using var admin = Client(listeners.AdminPort);
            var status = await admin.GetFromJsonAsync<JsonElement>("/api/admin/status");
            Assert.Equal(listeners.AdminPort, status.GetProperty("adminPort").GetInt32());
            using var deck = Client(deckPort);
            Assert.Equal(HttpStatusCode.NotFound, (await deck.GetAsync("/api/admin/status")).StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AdminQrIsAPngOfTheDeckUrl()
    {
        using var admin = Client(_adminPort);

        var response = await admin.GetAsync("/api/admin/qr");

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return; // no LAN adapter on this machine (CI); nothing to encode
        }

        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var png = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }

    [Fact]
    public async Task AdminSendRunsAMacro()
    {
        using var admin = Client(_adminPort);
        await WaitForAsync(() => _emulator.ActiveConnections == 1, "game connected");

        var response = await admin.PostAsJsonAsync("/api/admin/send", new { command = "e wave; e dance" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        await WaitForAsync(() => _emulator.ReceivedCommands.Count == 2, "commands received");
        Assert.Equal(["e wave", "e dance"], _emulator.ReceivedCommands);
    }

    [Fact]
    public async Task WebSocketSendsHelloAndDeliversPresses()
    {
        using var socket = await OpenDeckSocketAsync();

        var hello = await ReceiveJsonAsync(socket);
        Assert.Equal("hello", hello.GetProperty("type").GetString());
        var keyId = hello.GetProperty("profiles")[0].GetProperty("keys")[0].GetProperty("id").GetString()!;
        await WaitForAsync(() => _emulator.ActiveConnections == 1, "game connected");

        await SendJsonAsync(socket, new { type = "press", keyId });

        JsonElement result;
        do
        {
            result = await ReceiveJsonAsync(socket);
        }
        while (result.GetProperty("type").GetString() != "result");
        Assert.Equal(keyId, result.GetProperty("keyId").GetString());
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.Equal(["e wave"], _emulator.ReceivedCommands);
        Assert.Equal(1, _app.Services.GetRequiredService<DeckHub>().ConnectedCount);
    }

    private static async Task<JsonElement> ReceiveUntilAsync(ClientWebSocket socket, string type)
    {
        JsonElement message;
        do
        {
            message = await ReceiveJsonAsync(socket);
        }
        while (message.GetProperty("type").GetString() != type);
        return message;
    }

    private static string KeyIdByTitle(JsonElement hello, string title) =>
        hello.GetProperty("profiles")[0].GetProperty("keys").EnumerateArray()
            .First(k => k.GetProperty("title").GetProperty("text").GetString() == title)
            .GetProperty("id").GetString()!;

    [Fact]
    public async Task StagedKeyAdvancesOnEverySuccessfulPressAndWraps()
    {
        using var socket = await OpenDeckSocketAsync();
        var hello = await ReceiveJsonAsync(socket);
        var keyId = KeyIdByTitle(hello, "Sit"); // the sample toggle: e sit → e c
        Assert.Empty(hello.GetProperty("stages").EnumerateObject());
        await WaitForAsync(() => _emulator.ActiveConnections == 1, "game connected");

        await SendJsonAsync(socket, new { type = "press", keyId });
        var result = await ReceiveUntilAsync(socket, "result");
        Assert.Equal("press", result.GetProperty("phase").GetString());
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        var stage = await ReceiveUntilAsync(socket, "stage");
        Assert.Equal(keyId, stage.GetProperty("keyId").GetString());
        Assert.Equal(1, stage.GetProperty("stage").GetInt32());

        // A phone that connects now learns the current stage from hello.
        using var second = await OpenDeckSocketAsync();
        var secondHello = await ReceiveJsonAsync(second);
        Assert.Equal(1, secondHello.GetProperty("stages").GetProperty(keyId).GetInt32());

        await SendJsonAsync(socket, new { type = "press", keyId });
        stage = await ReceiveUntilAsync(socket, "stage");
        Assert.Equal(0, stage.GetProperty("stage").GetInt32());
        await WaitForAsync(() => _emulator.ReceivedCommands.Count == 2, "commands received");
        Assert.Equal(["e sit", "e c"], _emulator.ReceivedCommands);
    }

    [Fact]
    public async Task StagedKeyDoesNotAdvanceWhenTheGameIsDown()
    {
        await _emulator.StopAsync();
        var client = _app.Services.GetRequiredService<FxDeck.FxConsole.IFxConsoleClient>();
        await WaitForAsync(() => client.State == FxDeck.FxConsole.FxConsoleConnectionState.Disconnected, "game disconnected");
        using var socket = await OpenDeckSocketAsync();
        var keyId = KeyIdByTitle(await ReceiveJsonAsync(socket), "Sit");

        await SendJsonAsync(socket, new { type = "press", keyId });

        var result = await ReceiveUntilAsync(socket, "result");
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Empty(_app.Services.GetRequiredService<DeckHub>().BuildHello().Stages);
    }

    private async Task MakeHoldKeyAsync(string title, string releaseCommand)
    {
        using var admin = Client(_adminPort);
        var config = (await admin.GetFromJsonAsync<AppConfig>("/api/admin/config", FxJson.Options))!;
        config.Profiles[0].Keys.First(k => k.Title.Text == title).Action.ReleaseCommand = releaseCommand;
        var response = await admin.PutAsJsonAsync("/api/admin/config", config, FxJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HoldKeySendsThePressAndTheReleaseMacros()
    {
        await MakeHoldKeyAsync("Wave", "e c");
        using var socket = await OpenDeckSocketAsync();
        var keyId = KeyIdByTitle(await ReceiveJsonAsync(socket), "Wave");
        await WaitForAsync(() => _emulator.ActiveConnections == 1, "game connected");

        await SendJsonAsync(socket, new { type = "press", keyId });
        var press = await ReceiveUntilAsync(socket, "result");
        Assert.Equal("press", press.GetProperty("phase").GetString());
        Assert.True(press.GetProperty("success").GetBoolean(), press.ToString());

        await SendJsonAsync(socket, new { type = "release", keyId });
        var release = await ReceiveUntilAsync(socket, "result");
        Assert.Equal("release", release.GetProperty("phase").GetString());
        Assert.True(release.GetProperty("success").GetBoolean(), release.ToString());

        await WaitForAsync(() => _emulator.ReceivedCommands.Count == 2, "commands received");
        Assert.Equal(["e wave", "e c"], _emulator.ReceivedCommands);
    }

    [Fact]
    public async Task ReleaseOfATapKeyIsIgnored()
    {
        using var socket = await OpenDeckSocketAsync();
        var keyId = KeyIdByTitle(await ReceiveJsonAsync(socket), "Wave");
        await WaitForAsync(() => _emulator.ActiveConnections == 1, "game connected");

        await SendJsonAsync(socket, new { type = "release", keyId });
        await SendJsonAsync(socket, new { type = "press", keyId });

        var result = await ReceiveUntilAsync(socket, "result");
        Assert.Equal("press", result.GetProperty("phase").GetString());
        await WaitForAsync(() => _emulator.ReceivedCommands.Count == 1, "command received");
        Assert.Equal(["e wave"], _emulator.ReceivedCommands);
    }

    [Fact]
    public async Task AVanishingPhoneReleasesTheKeysItWasHolding()
    {
        await MakeHoldKeyAsync("Wave", "e c");
        var socket = await OpenDeckSocketAsync();
        var keyId = KeyIdByTitle(await ReceiveJsonAsync(socket), "Wave");
        await WaitForAsync(() => _emulator.ActiveConnections == 1, "game connected");

        await SendJsonAsync(socket, new { type = "press", keyId });
        await ReceiveUntilAsync(socket, "result");
        socket.Abort(); // no close handshake, like a phone going to sleep
        socket.Dispose();

        await WaitForAsync(() => _emulator.ReceivedCommands.Count == 2, "release sent after the socket died");
        Assert.Equal(["e wave", "e c"], _emulator.ReceivedCommands);
    }

    [Fact]
    public async Task PressOfUnknownKeyFailsGracefully()
    {
        using var socket = await OpenDeckSocketAsync();
        await ReceiveJsonAsync(socket);

        await SendJsonAsync(socket, new { type = "press", keyId = "nope" });

        var result = await ReceiveJsonAsync(socket);
        Assert.Equal("result", result.GetProperty("type").GetString());
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("unknownKey", result.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task PressFailsWithNotConnectedWhenTheGameIsDown()
    {
        await _emulator.StopAsync();
        var client = _app.Services.GetRequiredService<FxDeck.FxConsole.IFxConsoleClient>();
        await WaitForAsync(() => client.State == FxDeck.FxConsole.FxConsoleConnectionState.Disconnected, "game disconnected");
        using var socket = await OpenDeckSocketAsync();
        var hello = await ReceiveJsonAsync(socket);
        Assert.Equal("disconnected", hello.GetProperty("game").GetString());
        var keyId = hello.GetProperty("profiles")[0].GetProperty("keys")[0].GetProperty("id").GetString()!;

        await SendJsonAsync(socket, new { type = "press", keyId });

        JsonElement result;
        do
        {
            result = await ReceiveJsonAsync(socket);
        }
        while (result.GetProperty("type").GetString() != "result");
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("notConnected", result.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task WebSocketRejectsMissingCookie()
    {
        var socket = new ClientWebSocket();

        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_deckPort}/api/deck/ws"), CancellationToken.None));
    }

    [Fact]
    public async Task RotatingTheTokenClosesDecksWith4001AndInvalidatesCookies()
    {
        using var socket = await OpenDeckSocketAsync();
        await ReceiveJsonAsync(socket);
        var oldSession = Session;
        using var admin = Client(_adminPort);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/admin/token/rotate", null)).StatusCode);

        var buffer = new byte[1024];
        using var cts = new CancellationTokenSource(DefaultTimeout);
        var close = await socket.ReceiveAsync(buffer, cts.Token);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(4001, (int)close.CloseStatus!.Value);

        using var stale = Client(_deckPort, oldSession);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/api/deck/profile")).StatusCode);
        Assert.NotEqual(oldSession, Session);
    }

    [Fact]
    public async Task EditingConfigJsonPushesProfilesToDecks()
    {
        using var socket = await OpenDeckSocketAsync();
        await ReceiveJsonAsync(socket);
        var store = _app.Services.GetRequiredService<ConfigStore>();

        var edited = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(store.ConfigPath), FxJson.Options)!;
        edited.Profiles[0].Name = "Renamed";
        File.WriteAllText(store.ConfigPath, JsonSerializer.Serialize(edited, FxJson.Options));

        JsonElement message;
        do
        {
            message = await ReceiveJsonAsync(socket);
        }
        while (message.GetProperty("type").GetString() != "profiles");
        Assert.Equal("Renamed", message.GetProperty("profiles")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task SessionExchangeIsRateLimitedPerIp()
    {
        using var client = Client(_deckPort);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            statuses.Add((await client.PostAsync("/api/deck/session?t=wrong", null)).StatusCode);
        }

        Assert.Equal(10, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task ServesTheSpaShellForDeckRoutes()
    {
        using var client = Client(_deckPort);
        var embedded = new EmbeddedWebRoot();
        if (embedded.FileCount == 0)
        {
            return; // built with SkipWebBuild
        }

        foreach (var path in new[] { "/", "/deck/", "/deck/anything", "/?t=abc" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("<div id=\"root\">", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/does-not-exist")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/")).StatusCode); // admin UI is loopback-listener only

        var manifest = await client.GetAsync("/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        var sw = await client.GetAsync("/sw.js");
        Assert.Equal(HttpStatusCode.OK, sw.StatusCode);
        Assert.Contains("no-cache", sw.Headers.CacheControl?.ToString());

        using var admin = Client(_adminPort);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin/")).StatusCode);
    }
}
