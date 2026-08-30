using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FxDeck.Config;
using FxDeck.Emulator;
using FxDeck.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static FxDeck.Tests.TestHelpers;

namespace FxDeck.Tests.Web;

/// <summary>Admin API added in roadmap step 3 (config PUT, export/import, about, adapters, game test).</summary>
public class AdminApiTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fxdeck-tests", Guid.NewGuid().ToString("N"));
    private FxConsoleEmulator _emulator = null!;
    private WebApplication _app = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _emulator = new FxConsoleEmulator(EmulatorOptions());
        await _emulator.StartAsync();
        var adminPort = GetFreePort();
        _app = FxDeckHost.Build(new FxDeckHostOptions
        {
            DataDirectory = _dir,
            AdminPort = adminPort,
            DeckPort = GetFreePort(),
            DeckBindAddress = IPAddress.Loopback,
            GamePort = _emulator.Port,
            WatchConfig = false,
            ConsoleLogging = false,
            FileLogging = false,
            MinimumLogLevel = LogLevel.None,
            WebRootDirectory = null,
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

    private ConfigStore Store => _app.Services.GetRequiredService<ConfigStore>();

    [Fact]
    public async Task PutConfigSavesAndReportsRestartRequirement()
    {
        var config = await _admin.GetFromJsonAsync<AppConfig>("/api/admin/config", FxJson.Options);
        config!.Profiles[0].Name = "Edited";
        config.Settings.Theme = "light";

        var response = await _admin.PutAsJsonAsync("/api/admin/config", config, FxJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("restartRequired").GetBoolean());
        Assert.Equal("Edited", Store.Current.Profiles[0].Name);
        Assert.Equal("light", Store.Current.Settings.Theme);
        Assert.Contains("\"Edited\"", await File.ReadAllTextAsync(Store.ConfigPath));

        config.Settings.DeckPort = 25555;
        response = await _admin.PutAsJsonAsync("/api/admin/config", config, FxJson.Options);
        body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("restartRequired").GetBoolean());
    }

    [Fact]
    public async Task PutConfigRejectsInvalidDocumentsWithoutSaving()
    {
        var config = await _admin.GetFromJsonAsync<AppConfig>("/api/admin/config", FxJson.Options);
        config!.Profiles[0].Keys[0].Col = 42;

        var response = await _admin.PutAsJsonAsync("/api/admin/config", config, FxJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("グリッドの外", body.GetProperty("errors")[0].GetString());
        Assert.NotEqual(42, Store.Current.Profiles[0].Keys[0].Col);
    }

    [Fact]
    public async Task PutConfigChangesTheGameEndpointLive()
    {
        var client = _app.Services.GetRequiredService<FxDeck.FxConsole.IFxConsoleClient>();
        await WaitForAsync(() => client.State == FxDeck.FxConsole.FxConsoleConnectionState.Connected, "connected to the first emulator");

        // The host was built with GamePort override, so live updates are ignored there; verify via a second host without override.
        await using var second = new FxConsoleEmulator(EmulatorOptions());
        await second.StartAsync();
        var dir = Path.Combine(Path.GetTempPath(), "fxdeck-tests", Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(dir);
        store.Load();
        var initial = store.Current;
        initial.Settings.Game.Port = _emulator.Port;
        store.Save(initial);
        store.Dispose();
        var app = FxDeckHost.Build(new FxDeckHostOptions
        {
            DataDirectory = dir,
            AdminPort = GetFreePort(),
            DeckPort = GetFreePort(),
            DeckBindAddress = IPAddress.Loopback,
            WatchConfig = false,
            ConsoleLogging = false,
            FileLogging = false,
            MinimumLogLevel = LogLevel.None,
        });
        try
        {
            await app.StartAsync();
            var liveClient = app.Services.GetRequiredService<FxDeck.FxConsole.IFxConsoleClient>();
            await WaitForAsync(() => liveClient.State == FxDeck.FxConsole.FxConsoleConnectionState.Connected, "connected");
            var liveStore = app.Services.GetRequiredService<ConfigStore>();
            var config = liveStore.Current;
            config.Settings.Game.Port = second.Port;

            liveStore.Save(config);

            await WaitForAsync(() => second.ActiveConnections == 1, "reconnected to the second emulator");
            await WaitForAsync(() => liveClient.State == FxDeck.FxConsole.FxConsoleConnectionState.Connected, "connected again");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAndImportRoundTrip()
    {
        var profileId = Store.Current.Profiles[0].Id;

        var export = await _admin.GetAsync($"/api/admin/export?profile={profileId}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".fxdeck", export.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await export.Content.ReadAsByteArrayAsync();

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "default.fxdeck");
        var import = await _admin.PostAsync("/api/admin/import?mode=profile", form);

        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        var body = await import.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("profilesAdded").GetInt32());
        Assert.Equal(2, Store.Current.Profiles.Count);
        Assert.NotEqual(profileId, Store.Current.Profiles[1].Id);

        var all = await _admin.GetAsync("/api/admin/export");
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
    }

    [Fact]
    public async Task ImportRejectsGarbage()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("hello"u8.ToArray()), "file", "x.json");

        var response = await _admin.PostAsync("/api/admin/import?mode=profile", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(Store.Current.Profiles);
    }

    [Fact]
    public async Task AboutIncludesVersionAndNotices()
    {
        var about = await _admin.GetFromJsonAsync<JsonElement>("/api/admin/about");

        Assert.Equal("FxDeck", about.GetProperty("name").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+", about.GetProperty("version").GetString());
        Assert.Equal("MIT", about.GetProperty("license").GetString());
        Assert.Contains("Font Awesome", about.GetProperty("thirdPartyNotices").GetString());
    }

    [Fact]
    public async Task AdaptersAndGameTestWork()
    {
        var adapters = await _admin.GetFromJsonAsync<JsonElement>("/api/admin/network/adapters");
        Assert.Equal(JsonValueKind.Array, adapters.GetProperty("adapters").ValueKind);

        var ok = await (await _admin.PostAsJsonAsync("/api/admin/game/test", new { host = "127.0.0.1", port = _emulator.Port })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(ok.GetProperty("ok").GetBoolean());

        var closed = await (await _admin.PostAsJsonAsync("/api/admin/game/test", new { host = "127.0.0.1", port = GetFreePort() })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(closed.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task FirewallStatusAnswers()
    {
        var response = await _admin.GetAsync("/api/admin/firewall/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("FxDeck", body.GetProperty("ruleName").GetString());
        Assert.Contains(body.GetProperty("ruleExists").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False }); // value depends on the machine
    }
}
