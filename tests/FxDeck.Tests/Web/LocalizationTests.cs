using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxDeck.Config;
using FxDeck.Emulator;
using FxDeck.Localization;
using FxDeck.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static FxDeck.Tests.TestHelpers;

namespace FxDeck.Tests.Web;

/// <summary>Roadmap step 6: server-side strings, the language setting and what the deck / admin APIs expose.</summary>
public class LocalizationTests
{
    [Fact]
    public void EveryLanguageHasExactlyTheJapaneseKeys()
    {
        var reference = Strings.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(reference);
        foreach (var lang in Strings.Languages)
        {
            var dictionary = Strings.Dictionary(lang);
            Assert.Equal(reference, dictionary.Keys.OrderBy(k => k, StringComparer.Ordinal)); // no missing and no stray keys
            Assert.All(dictionary, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), $"{lang}:{entry.Key}"));
        }

    }

    [Fact]
    public void MissingTranslationFallsBackToJapanese()
    {
        Assert.Equal(Strings.Get(Lang.Ja, "validator.emptyConfig"), Strings.Get((Lang)999, "validator.emptyConfig"));
    }

    [Fact]
    public void UnknownKeysFallBackToTheKeyItself()
    {
        Assert.Equal("nope.missing", Strings.Get(Lang.En, "nope.missing"));
    }

    [Fact]
    public void PlaceholdersAreFormatted()
    {
        Assert.Equal("Invalid deck port: 70000", Strings.Get(Lang.En, "validator.deckPortInvalid", 70000));
        Assert.Equal("デッキのポートが不正です: 70000", Strings.Get(Lang.Ja, "validator.deckPortInvalid", 70000));
    }

    [Theory]
    [InlineData("ja", "en-US", Lang.Ja)]
    [InlineData("en", "ja-JP", Lang.En)]
    [InlineData("auto", "ja-JP", Lang.Ja)]
    [InlineData("auto", "en-GB", Lang.En)]
    [InlineData(null, "de-DE", Lang.En)]
    public void SettingOverridesTheCultureAndAutoFollowsIt(string? setting, string culture, Lang expected)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            Assert.Equal(expected, Strings.Resolve(setting));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void ValidatorSpeaksTheRequestedLanguage()
    {
        var config = AppConfig.CreateDefault();
        config.Settings.DeckPort = 0;
        config.Settings.Language = "klingon";

        var ja = ConfigValidator.Validate(config, Lang.Ja);
        var en = ConfigValidator.Validate(config, Lang.En);

        Assert.Contains(ja, e => e.Contains("デッキのポート"));
        Assert.Contains(ja, e => e.Contains("言語"));
        Assert.Contains(en, e => e.Contains("Invalid deck port"));
        Assert.Contains(en, e => e.Contains("Invalid language: klingon"));
        Assert.Equal(ja.Count, en.Count);
    }

    [Fact]
    public void ImportErrorsSpeakTheRequestedLanguage()
    {
        var upload = "[1,2,3]"u8.ToArray();

        var ja = Assert.Throws<InvalidDataException>(() => ConfigPackage.Import(upload, ImportMode.Profile, AppConfig.CreateDefault(), null, Lang.Ja));
        var en = Assert.Throws<InvalidDataException>(() => ConfigPackage.Import(upload, ImportMode.Profile, AppConfig.CreateDefault(), null, Lang.En));

        Assert.Contains("オブジェクト", ja.Message);
        Assert.Contains("not an object", en.Message);
    }

    [Fact]
    public void LanguageDefaultsToAutoAndRoundTrips()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("""{"version":1,"settings":{},"profiles":[]}""", FxJson.Options)!;
        Assert.Equal("auto", config.Settings.Language);

        config.Settings.Language = "en";
        var json = JsonSerializer.Serialize(config, FxJson.Options);
        Assert.Contains("\"language\": \"en\"", json);
        Assert.Equal("en", DeckMessages.DeckSettings.From(config.Settings).Language);
    }
}

/// <summary>The admin API answers in the configured language and tags single errors with a machine-readable code.</summary>
public class LocalizedApiTests : IAsyncLifetime
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

    private void SetLanguage(string language)
    {
        var store = _app.Services.GetRequiredService<ConfigStore>();
        var config = store.Current;
        config.Settings.Language = language;
        store.Save(config);
    }

    [Fact]
    public async Task ErrorsFollowTheLanguageSettingAndCarryACode()
    {
        SetLanguage("en");
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("[1]"u8.ToArray()), "file", "x.json");

        var response = await _admin.PostAsync("/api/admin/import?mode=nope", form);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("importModeInvalid", body.GetProperty("code").GetString());
        Assert.Equal("mode must be profile or all.", body.GetProperty("error").GetString());

        SetLanguage("ja");
        using var form2 = new MultipartFormDataContent();
        form2.Add(new ByteArrayContent("[1]"u8.ToArray()), "file", "x.json");
        body = await (await _admin.PostAsync("/api/admin/import?mode=nope", form2)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mode は profile か all です。", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PutConfigValidatesInTheLanguageBeingSaved()
    {
        var config = AppConfig.CreateDefault();
        config.Settings.Language = "en";
        config.Settings.DeckPort = 0;

        var response = await _admin.PutAsJsonAsync("/api/admin/config", config, FxJson.Options);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("Invalid deck port"));
    }

    [Fact]
    public async Task DeckHelloCarriesTheLanguage()
    {
        SetLanguage("en");
        var hello = _app.Services.GetRequiredService<DeckHub>().BuildHello();

        Assert.Equal("en", hello.Settings.Language);
    }
}
