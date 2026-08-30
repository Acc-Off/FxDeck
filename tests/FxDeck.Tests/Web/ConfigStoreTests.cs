using System.Text.Json;
using FxDeck.Config;
using static FxDeck.Tests.TestHelpers;

namespace FxDeck.Tests.Web;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fxdeck-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void CreatesTheDefaultConfigurationOnFirstRun()
    {
        using var store = new ConfigStore(_dir);

        store.Load();

        Assert.True(File.Exists(store.ConfigPath));
        var profile = Assert.Single(store.Current.Profiles);
        Assert.Equal("Default", profile.Name);
        Assert.Equal(5, profile.Columns);
        Assert.Equal(3, profile.Rows);
        Assert.Contains(profile.Keys, k => k.Action.Command == "e wave");
        Assert.Equal(20200, store.Current.Settings.DeckPort);
        Assert.Equal("dark", store.Current.Settings.Theme);
    }

    [Fact]
    public void DefaultFileUsesCamelCaseAndOmitsNulls()
    {
        using var store = new ConfigStore(_dir);
        store.Load();

        var json = File.ReadAllText(store.ConfigPath);

        Assert.Contains("\"deckPort\": 20200", json);
        Assert.Contains("\"holdToConfirm\": false", json);
        Assert.DoesNotContain("\"style\": null", json);
        Assert.DoesNotContain("\"namedToken\"", json);
        Assert.DoesNotContain("orderedProfiles", json); // computed, never persisted

        // Non-ASCII text (a Japanese profile name) is written as-is, not \uXXXX-escaped, so the file stays hand-editable.
        var config = store.Current;
        config.Profiles[0].Name = "デフォルト";
        store.Save(config);
        Assert.Contains("デフォルト", File.ReadAllText(store.ConfigPath));
    }

    [Fact]
    public void RoundTripsThroughSaveAndLoad()
    {
        using var store = new ConfigStore(_dir);
        store.Load();
        var config = store.Current;
        config.Settings.Theme = "light";
        config.Profiles[0].Keys[0].HoldToConfirm = true;
        config.Profiles.Add(new DeckProfile { Name = "Second", Order = 1, Columns = 3, Rows = 2 });

        store.Save(config);

        using var reloaded = new ConfigStore(_dir);
        reloaded.Load();
        Assert.Equal("light", reloaded.Current.Settings.Theme);
        Assert.True(reloaded.Current.Profiles[0].Keys[0].HoldToConfirm);
        Assert.Equal(["Default", "Second"], reloaded.Current.OrderedProfiles.Select(p => p.Name));
    }

    [Fact]
    public async Task ReloadsWhenTheFileIsEditedByHand()
    {
        using var store = new ConfigStore(_dir);
        store.Load();
        store.StartWatching();
        AppConfig? announced = null;
        store.Changed += (_, c) => announced = c;

        var edited = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(store.ConfigPath), FxJson.Options)!;
        edited.Profiles[0].Name = "Edited";
        File.WriteAllText(store.ConfigPath, JsonSerializer.Serialize(edited, FxJson.Options));

        await WaitForAsync(() => announced is not null, "Changed event");
        Assert.Equal("Edited", store.Current.Profiles[0].Name);
    }

    [Fact]
    public void IgnoresInvalidJsonAndKeepsThePreviousConfiguration()
    {
        using var store = new ConfigStore(_dir);
        store.Load();
        var changed = 0;
        store.Changed += (_, _) => changed++;

        File.WriteAllText(store.ConfigPath, "{ this is not json");

        Assert.False(store.Reload());
        Assert.Equal(0, changed);
        Assert.Equal("Default", store.Current.Profiles[0].Name);
    }

    [Fact]
    public void ReloadIsANoOpWhenTheContentDidNotChange()
    {
        using var store = new ConfigStore(_dir);
        store.Load();

        Assert.False(store.Reload());
    }

    [Fact]
    public void FindKeyLocatesKeysAcrossProfiles()
    {
        var config = AppConfig.CreateDefault();
        config.Profiles.Add(new DeckProfile { Name = "Second", Order = 1, Keys = [new DeckKey { Id = "k2", Action = new KeyAction { Command = "x" } }] });

        var key = config.FindKey("k2", out var profile);

        Assert.NotNull(key);
        Assert.Equal("Second", profile!.Name);
        Assert.Null(config.FindKey("missing", out _));
    }
}
