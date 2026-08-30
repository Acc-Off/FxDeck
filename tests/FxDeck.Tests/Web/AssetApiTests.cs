using System.Drawing;
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

/// <summary>Image upload / listing / pruning on the admin API and delivery on the deck API (design memo §3.8).</summary>
public class AssetApiTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fxdeck-tests", Guid.NewGuid().ToString("N"));
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

    private async Task<string> UploadAsync(byte[] image, string name = "icon.png")
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(image);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", name);
        var response = await _admin.PostAsync("/api/admin/assets", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hash").GetString()!;
    }

    [Fact]
    public async Task UploadListPruneRoundTrip()
    {
        var hash = await UploadAsync(AssetStoreTests.MakeImage(50, 50, Color.Red));
        var other = await UploadAsync(AssetStoreTests.MakeImage(50, 50, Color.Blue));
        Assert.Equal(hash, await UploadAsync(AssetStoreTests.MakeImage(50, 50, Color.Red))); // deduplicated

        var list = await _admin.GetFromJsonAsync<JsonElement>("/api/admin/assets");
        var assets = list.GetProperty("assets").EnumerateArray().ToList();
        Assert.Equal(2, assets.Count);
        Assert.All(assets, a => Assert.False(a.GetProperty("referenced").GetBoolean()));

        // Reference one of them from a key, then prune.
        var store = _app.Services.GetRequiredService<ConfigStore>();
        var config = store.Current;
        config.Profiles[0].Keys[0].Icon = new KeyIcon { Type = "image", Hash = hash };
        store.Save(config);

        list = await _admin.GetFromJsonAsync<JsonElement>("/api/admin/assets");
        Assert.Contains(list.GetProperty("assets").EnumerateArray(), a => a.GetProperty("hash").GetString() == hash && a.GetProperty("referenced").GetBoolean());

        var prune = await _admin.PostAsync("/api/admin/assets/prune", null);
        Assert.Equal(1, (await prune.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deleted").GetInt32());
        var remaining = (await _admin.GetFromJsonAsync<JsonElement>("/api/admin/assets")).GetProperty("assets").EnumerateArray().Select(a => a.GetProperty("hash").GetString()).ToList();
        Assert.Equal([hash], remaining);
        Assert.DoesNotContain(other, remaining);
    }

    [Fact]
    public async Task NonImagesAreRejected()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray()), "file", "evil.svg");

        var response = await _admin.PostAsync("/api/admin/assets", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("画像", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeckServesImagesToSessionsAndToTheAdminListener()
    {
        var hash = await UploadAsync(AssetStoreTests.MakeImage(30, 30, Color.Green));

        // Admin listener (no deck cookie): allowed, for the admin UI's previews.
        var viaAdmin = await _admin.GetAsync($"/api/deck/assets/{hash}");
        Assert.Equal(HttpStatusCode.OK, viaAdmin.StatusCode);
        Assert.Equal("image/png", viaAdmin.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", viaAdmin.Headers.CacheControl?.ToString());

        // Deck listener: cookie required.
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        using var deck = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{_deckPort}/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await deck.GetAsync($"/api/deck/assets/{hash}")).StatusCode);

        var token = _app.Services.GetRequiredService<DeckTokenStore>().Token;
        Assert.Equal(HttpStatusCode.OK, (await deck.PostAsync($"/api/deck/session?t={Uri.EscapeDataString(token)}", null)).StatusCode);
        var viaDeck = await deck.GetAsync($"/api/deck/assets/{hash}");
        Assert.Equal(HttpStatusCode.OK, viaDeck.StatusCode);
        Assert.Equal(await viaAdmin.Content.ReadAsByteArrayAsync(), await viaDeck.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await deck.GetAsync($"/api/deck/assets/{new string('0', 64)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await deck.GetAsync("/api/deck/assets/..%2Fconfig.json")).StatusCode);
    }

    [Fact]
    public async Task ExportCarriesImagesAndImportRestoresThem()
    {
        var hash = await UploadAsync(AssetStoreTests.MakeImage(30, 30, Color.Gold));
        var store = _app.Services.GetRequiredService<ConfigStore>();
        var config = store.Current;
        config.Profiles[0].Keys[0].Icon = new KeyIcon { Type = "image", Hash = hash };
        store.Save(config);

        var package = await _admin.GetByteArrayAsync($"/api/admin/export?profile={config.Profiles[0].Id}");
        var assets = _app.Services.GetRequiredService<AssetStore>();
        Assert.Equal(1, assets.DeleteUnused(new AppConfig())); // wipe the store so the import has to restore the file

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(package), "file", "profile.fxdeck");
        var response = await _admin.PostAsync("/api/admin/import?mode=profile", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("warnings").EnumerateArray());
        Assert.True(assets.Exists(hash));
        Assert.Equal(hash, store.Current.Profiles[1].Keys[0].Icon!.Hash);
    }
}
