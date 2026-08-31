using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxDeck.Config;
using FxDeck.NuiInspect;
using FxDeck.Tests.Fakes;
using FxDeck.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static FxDeck.Tests.TestHelpers;

namespace FxDeck.Tests.Web;

/// <summary>The three command-cache admin endpoints (design memo §3.3 / §3.10).</summary>
public class CommandApiTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fxdeck-tests", Guid.NewGuid().ToString("N"));
    private FakeCdpServer _cdp = null!;
    private WebApplication _app = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _cdp = new FakeCdpServer();
        _cdp.ChildFrames.Add(new FakeCdpFrame { Id = "chatframe", Name = "chat", Url = "nui://chat/dist/ui.html", ContextId = 5 });
        _cdp.EvaluateValue = """{"found":true,"commands":[{"name":"/jail","help":"Jail a player","params":[{"name":"id"}]},{"name":"/fix","help":"","params":[]}]}""";
        await _cdp.StartAsync();

        var adminPort = GetFreePort();
        _app = FxDeckHost.Build(new FxDeckHostOptions
        {
            DataDirectory = _dir,
            AdminPort = adminPort,
            DeckPort = GetFreePort(),
            DeckBindAddress = IPAddress.Loopback,
            GamePort = GetFreePort(), // no game; irrelevant here
            WatchConfig = false,
            ConsoleLogging = false,
            FileLogging = false,
            MinimumLogLevel = LogLevel.None,
            WebRootDirectory = null,
            ConfigureServices = services => services.AddSingleton(new NuiInspectOptions
            {
                BaseAddress = _cdp.BaseAddress,
                ContextEventDelay = TimeSpan.FromMilliseconds(100),
                ConnectTimeout = TimeSpan.FromSeconds(2),
                OverallTimeout = TimeSpan.FromSeconds(5),
            }),
        });
        await _app.StartAsync();
        _admin = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{adminPort}/") };
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _cdp.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task LifecycleExtractReadClear()
    {
        // Nothing extracted yet.
        var empty = await _admin.GetFromJsonAsync<JsonElement>("/api/admin/commands");
        Assert.Equal(0, empty.GetProperty("commands").GetArrayLength());
        Assert.False(empty.TryGetProperty("extractedAt", out _));

        // Extract caches the normalised list and reports it.
        var extract = await _admin.PostAsync("/api/admin/commands/extract", null);
        Assert.Equal(HttpStatusCode.OK, extract.StatusCode);
        var body = await extract.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("count").GetInt32());
        Assert.Equal("fix", body.GetProperty("commands")[0].GetProperty("name").GetString());
        Assert.Equal("jail", body.GetProperty("commands")[1].GetProperty("name").GetString());
        Assert.True(body.TryGetProperty("extractedAt", out _));

        var cachePath = Path.Combine(_dir, CommandCacheStore.FileName);
        Assert.True(File.Exists(cachePath));

        // GET serves the cache (from memory, but also present on disk for the next start).
        var cached = await _admin.GetFromJsonAsync<JsonElement>("/api/admin/commands");
        Assert.Equal(2, cached.GetProperty("commands").GetArrayLength());

        // DELETE clears both.
        var delete = await _admin.DeleteAsync("/api/admin/commands");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.False(File.Exists(cachePath));
        var cleared = await _admin.GetFromJsonAsync<JsonElement>("/api/admin/commands");
        Assert.Equal(0, cleared.GetProperty("commands").GetArrayLength());
    }

    [Fact]
    public async Task ExtractFailureIsConflictWithAReasonCode()
    {
        await _cdp.DisposeAsync(); // game "quits": the debug port stops answering
        _cdp = new FakeCdpServer(); // recreated so the teardown's DisposeAsync stays valid

        var response = await _admin.PostAsync("/api/admin/commands/extract", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gameNotRunning", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
    }
}
