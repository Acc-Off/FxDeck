using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FxDeck.Config;

namespace FxDeck.Tests.Web;

public class ConfigPackageTests
{
    private static AppConfig TwoProfiles()
    {
        var config = AppConfig.CreateDefault();
        config.Profiles.Add(new DeckProfile
        {
            Name = "Second",
            Order = 1,
            Columns = 3,
            Rows = 2,
            Keys = [new DeckKey { Row = 0, Col = 0, Title = new KeyTitle { Text = "X" }, Action = new KeyAction { Command = "x" }, Icon = KeyIcon.Emoji("🎉") }],
        });
        config.Settings.Tunnel.NamedToken = "secret";
        return config;
    }

    [Fact]
    public void ProfileExportIsAZipWithProfileJsonAndAssetsFolder()
    {
        var config = TwoProfiles();

        var bytes = ConfigPackage.ExportProfile(config.Profiles[1]);

        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Contains(zip.Entries, e => e.FullName == "profile.json");
        Assert.Contains(zip.Entries, e => e.FullName == "assets/");
        using var reader = new StreamReader(zip.GetEntry("profile.json")!.Open());
        var profile = JsonSerializer.Deserialize<DeckProfile>(reader.ReadToEnd(), FxJson.Options)!;
        Assert.Equal("Second", profile.Name);
        Assert.Equal("🎉", profile.Keys[0].Icon!.Value);
    }

    [Fact]
    public void FullExportStripsTheTunnelToken()
    {
        var config = TwoProfiles();

        var bytes = ConfigPackage.ExportAll(config);

        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var reader = new StreamReader(zip.GetEntry("config.json")!.Open());
        var json = reader.ReadToEnd();
        Assert.DoesNotContain("secret", json);
        Assert.Contains("\"profiles\"", json);
        Assert.Equal("secret", config.Settings.Tunnel.NamedToken); // the original is untouched
    }

    [Fact]
    public void ProfileImportAppendsWithFreshIdsOnCollision()
    {
        var config = TwoProfiles();
        var exported = ConfigPackage.ExportProfile(config.Profiles[0]);

        var result = ConfigPackage.Import(exported, ImportMode.Profile, config);

        Assert.Equal(1, result.ProfilesAdded);
        Assert.Equal(3, result.Config.Profiles.Count);
        var imported = result.Config.Profiles[2];
        Assert.Equal("Default", imported.Name);
        Assert.Equal(2, imported.Order);
        Assert.NotEqual(config.Profiles[0].Id, imported.Id);
        Assert.All(imported.Keys, k => Assert.DoesNotContain(k.Id, config.Profiles[0].Keys.Select(x => x.Id)));
        Assert.Equal(2, config.Profiles.Count); // input untouched
        Assert.Empty(ConfigValidator.Validate(result.Config));
    }

    [Fact]
    public void PlainJsonProfileIsAccepted()
    {
        var config = AppConfig.CreateDefault();
        var json = Encoding.UTF8.GetBytes("""{ "name": "Plain", "columns": 3, "rows": 2, "keys": [ { "row": 0, "col": 1, "title": { "text": "A" }, "action": { "command": "a" } } ] }""");

        var result = ConfigPackage.Import(json, ImportMode.Profile, config);

        var profile = result.Config.Profiles[1];
        Assert.Equal("Plain", profile.Name);
        Assert.False(string.IsNullOrEmpty(profile.Id));
        Assert.False(string.IsNullOrEmpty(profile.Keys[0].Id));
        Assert.Equal("bottom", profile.Keys[0].Title.Position);
        Assert.Empty(ConfigValidator.Validate(result.Config));
    }

    [Fact]
    public void FullImportReplacesEverythingButKeepsSecrets()
    {
        var config = TwoProfiles();
        var other = AppConfig.CreateDefault();
        other.Settings.Theme = "light";
        other.Settings.DeckPort = 25000;
        other.Profiles[0].Name = "Imported";
        var exported = ConfigPackage.ExportAll(other);

        var result = ConfigPackage.Import(exported, ImportMode.All, config);

        Assert.Equal("light", result.Config.Settings.Theme);
        Assert.Equal(25000, result.Config.Settings.DeckPort);
        Assert.Equal("secret", result.Config.Settings.Tunnel.NamedToken);
        Assert.Equal(["Imported"], result.Config.Profiles.Select(p => p.Name));
    }

    [Fact]
    public void FullImportOfAProfileFileIsRejectedWithGuidance()
    {
        var config = AppConfig.CreateDefault();
        var exported = ConfigPackage.ExportProfile(config.Profiles[0]);

        var ex = Assert.Throws<InvalidDataException>(() => ConfigPackage.Import(exported, ImportMode.All, config));

        Assert.Contains("プロファイルを追加", ex.Message);
    }

    [Fact]
    public void FullImportAcceptsAConfigWhenAddingProfiles()
    {
        var config = AppConfig.CreateDefault();
        var exported = ConfigPackage.ExportAll(TwoProfiles());

        var result = ConfigPackage.Import(exported, ImportMode.Profile, config);

        Assert.Equal(2, result.ProfilesAdded);
        Assert.Equal(3, result.Config.Profiles.Count);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{ \"hello\": \"world\" }")]
    public void GarbageIsRejected(string body)
    {
        Assert.Throws<InvalidDataException>(() => ConfigPackage.Import(Encoding.UTF8.GetBytes(body), ImportMode.Profile, AppConfig.CreateDefault()));
    }

    [Fact]
    public void ZipWithoutKnownEntriesIsRejected()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("readme.txt");
        }

        Assert.Throws<InvalidDataException>(() => ConfigPackage.Import(buffer.ToArray(), ImportMode.Profile, AppConfig.CreateDefault()));
    }

    [Fact]
    public void ImageIconsFallBackToLabelsWithAWarning()
    {
        var config = AppConfig.CreateDefault();
        var json = Encoding.UTF8.GetBytes("""{ "name": "Img", "keys": [ { "row": 0, "col": 0, "title": { "text": "Pic" }, "icon": { "type": "image", "hash": "abc" }, "action": { "command": "a" } } ] }""");

        var result = ConfigPackage.Import(json, ImportMode.Profile, config);

        Assert.Null(result.Config.Profiles[1].Keys[0].Icon);
        Assert.Contains(result.Warnings, w => w.Contains("1 個"));
    }

    [Fact]
    public void StageImageIconsAreResolvedAndCountedLikeKeyIcons()
    {
        var config = AppConfig.CreateDefault();
        var json = Encoding.UTF8.GetBytes("""
            { "name": "Img", "keys": [ { "row": 0, "col": 0, "title": { "text": "Pic" }, "action": { "command": "a",
              "stages": [ { "title": { "text": "Two" }, "background": "#000", "icon": { "type": "image", "hash": "abc" }, "command": "b" } ] } } ] }
            """);

        var result = ConfigPackage.Import(json, ImportMode.Profile, config);

        var stage = Assert.Single(result.Config.Profiles[1].Keys[0].Action.Stages!);
        Assert.Null(stage.Icon);
        Assert.Contains(result.Warnings, w => w.Contains("1 個"));
    }

    [Fact]
    public void ReferencedHashesIncludeStageIcons()
    {
        var config = AppConfig.CreateDefault();
        var hash = new string('a', 64);
        config.Profiles[0].Keys[0].Action.Stages = [new KeyStage { Icon = new KeyIcon { Type = "image", Hash = hash } }];

        Assert.Contains(hash, AssetStore.ReferencedHashes(config));
    }

    [Fact]
    public void ExportFileNameIsSafe()
    {
        var name = ConfigPackage.ExportFileName(new DeckProfile { Name = "a/b:c" });

        Assert.StartsWith("fxdeck-a_b_c-", name);
        Assert.EndsWith(".fxdeck", name);
    }
}
