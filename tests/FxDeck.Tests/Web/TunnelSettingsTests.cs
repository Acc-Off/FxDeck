using System.Text.Json;
using FxDeck.Config;

namespace FxDeck.Tests.Web;

/// <summary>`settings.tunnel` fields added in roadmap step 4 (namedUrl, autoStart).</summary>
public class TunnelSettingsTests
{
    [Fact]
    public void NamedUrlMustBeAnHttpUrl()
    {
        var config = AppConfig.CreateDefault();
        config.Settings.Tunnel.NamedUrl = "deck.example.com";

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("固定 URL"));

        config.Settings.Tunnel.NamedUrl = "https://deck.example.com";
        Assert.Empty(ConfigValidator.Validate(config));

        config.Settings.Tunnel.NamedUrl = "   ";
        Assert.Empty(ConfigValidator.Validate(config)); // blank = not configured
    }

    [Fact]
    public void ComputedFlagsAreNotSerialised()
    {
        var config = AppConfig.CreateDefault();
        config.Settings.Tunnel.Mode = "named";
        config.Settings.Tunnel.AutoStart = true;
        config.Settings.Tunnel.NamedUrl = "https://deck.example.com";

        var json = JsonSerializer.Serialize(config, FxJson.Options);

        Assert.DoesNotContain("isOff", json);
        Assert.DoesNotContain("isNamed", json);
        Assert.Contains("\"autoStart\": true", json);
        var back = JsonSerializer.Deserialize<AppConfig>(json, FxJson.Options)!;
        Assert.True(back.Settings.Tunnel.IsNamed);
        Assert.True(back.Settings.Tunnel.AutoStart);
        Assert.Equal("https://deck.example.com", back.Settings.Tunnel.NamedUrl);
    }

    [Fact]
    public void OlderConfigWithoutTheNewFieldsStillLoads()
    {
        const string json = """{"version":1,"settings":{"tunnel":{"mode":"try"}},"profiles":[]}""";

        var config = JsonSerializer.Deserialize<AppConfig>(json, FxJson.Options)!;

        Assert.Equal("try", config.Settings.Tunnel.Mode);
        Assert.False(config.Settings.Tunnel.AutoStart);
        Assert.Null(config.Settings.Tunnel.NamedUrl);
    }
}
