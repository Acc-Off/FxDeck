using FxDeck.Config;

namespace FxDeck.Tests.Web;

public class ConfigValidatorTests
{
    [Fact]
    public void DefaultConfigurationIsValid()
    {
        Assert.Empty(ConfigValidator.Validate(AppConfig.CreateDefault()));
    }

    [Fact]
    public void NullIsRejected()
    {
        Assert.Single(ConfigValidator.Validate(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void RejectsBadDeckPort(int port)
    {
        var config = AppConfig.CreateDefault();
        config.Settings.DeckPort = port;

        Assert.Contains(ConfigValidator.Validate(config), e => e.Contains("デッキのポート"));
    }

    [Fact]
    public void RejectsUnknownThemeAndTunnelMode()
    {
        var config = AppConfig.CreateDefault();
        config.Settings.Theme = "neon";
        config.Settings.Tunnel.Mode = "maybe";

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("テーマ"));
        Assert.Contains(errors, e => e.Contains("トンネル"));
    }

    [Fact]
    public void RejectsMoreThanFiveStagesAndBadStageContent()
    {
        var config = AppConfig.CreateDefault();
        var key = config.Profiles[0].Keys[0];
        key.Action.Stages = Enumerable.Range(0, 5).Select(_ => new KeyStage { Command = "x" }).ToList();
        key.Action.Stages[1].Icon = new KeyIcon { Type = "image", Hash = "nope" };
        key.Action.Stages[2].Background = "";

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("ステージは最大 5"));
        Assert.Contains(errors, e => e.Contains("ステージ 3") && e.Contains("画像の参照"));
        Assert.Contains(errors, e => e.Contains("ステージ 4") && e.Contains("背景色"));
    }

    [Fact]
    public void AcceptsHoldKeysAndStagesWithoutACommand()
    {
        var config = AppConfig.CreateDefault();
        var key = config.Profiles[0].Keys[0];
        key.Action.Command = null;
        key.Action.ReleaseCommand = "e c";
        key.Action.Stages = [new KeyStage()];

        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void RejectsKeysOutsideTheGridAndOverlaps()
    {
        var config = AppConfig.CreateDefault();
        var profile = config.Profiles[0];
        profile.Keys[0].Col = 99;
        profile.Keys[1].Row = profile.Keys[2].Row;
        profile.Keys[1].Col = profile.Keys[2].Col;

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("グリッドの外"));
        Assert.Contains(errors, e => e.Contains("同じマス"));
    }

    [Fact]
    public void RejectsDuplicateIdsAcrossProfiles()
    {
        var config = AppConfig.CreateDefault();
        var second = new DeckProfile { Id = config.Profiles[0].Id, Name = "Dup", Order = 1, Keys = [new DeckKey { Id = config.Profiles[0].Keys[0].Id, Action = new KeyAction { Command = "x" } }] };
        config.Profiles.Add(second);

        var errors = ConfigValidator.Validate(config);

        Assert.Equal(2, errors.Count(e => e.Contains("id が重複")));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(13, 3)]
    [InlineData(5, 9)]
    public void RejectsGridSizesOutOfRange(int columns, int rows)
    {
        var config = AppConfig.CreateDefault();
        config.Profiles[0].Columns = columns;
        config.Profiles[0].Rows = rows;
        config.Profiles[0].Keys.Clear();

        Assert.NotEmpty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void RejectsMalformedIcons()
    {
        var config = AppConfig.CreateDefault();
        var keys = config.Profiles[0].Keys;
        keys[0].Icon = new KeyIcon { Type = "svg" };
        keys[1].Icon = new KeyIcon { Type = "fa", Name = "ban", Style = "duotone" };
        keys[2].Icon = new KeyIcon { Type = "emoji" };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("未対応のアイコン種別"));
        Assert.Contains(errors, e => e.Contains("スタイルが不正"));
        Assert.Contains(errors, e => e.Contains("絵文字が空"));
    }

    [Fact]
    public void RejectsUnknownActionType()
    {
        var config = AppConfig.CreateDefault();
        config.Profiles[0].Keys[0].Action.Type = "folder";

        Assert.Contains(ConfigValidator.Validate(config), e => e.Contains("未対応の動作"));
    }
}
