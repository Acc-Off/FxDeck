using System.Text.Json;
using System.Text.Json.Serialization;

namespace FxDeck.Config;

/// <summary>Root of <c>config.json</c> (design memo §4).</summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    public AppSettings Settings { get; set; } = new();

    public List<DeckProfile> Profiles { get; set; } = [];

    /// <summary>Profiles in swipe order.</summary>
    [JsonIgnore]
    public IEnumerable<DeckProfile> OrderedProfiles => Profiles.OrderBy(p => p.Order);

    public DeckKey? FindKey(string keyId, out DeckProfile? profile)
    {
        foreach (var candidate in Profiles)
        {
            var key = candidate.Keys.FirstOrDefault(k => k.Id == keyId);
            if (key is not null)
            {
                profile = candidate;
                return key;
            }
        }

        profile = null;
        return null;
    }

    /// <summary>First-run configuration: one 5×3 profile with a few sample emotes.</summary>
    public static AppConfig CreateDefault() => new()
    {
        Profiles =
        [
            new DeckProfile
            {
                Name = "Default",
                Keys =
                [
                    Key(0, 0, "Wave", "e wave", KeyIcon.Mdi("hand-wave"), "#2f6fdb"),
                    Key(0, 1, "Dance", "e dance", KeyIcon.Mdi("dance-ballroom"), "#c2408f"),
                    Toggle(Key(0, 2, "Sit", "e sit", KeyIcon.Mdi("seat"), "#3c8d5a"), "Stand", "e c", KeyIcon.Mdi("human-handsup"), "#2a2a2a"),
                    Key(0, 3, "Think", "e think; {2000ms}; e c", KeyIcon.Mdi("head-lightbulb"), "#d08a2a"),
                    Key(0, 4, "Cancel", "e c", KeyIcon.Fa("solid", "ban"), "#8a2f2f"),
                    Key(1, 0, "Hello", "say hello 👋", KeyIcon.Emoji("👋"), "#2a2a2a"),
                ],
            },
        ],
    };

    private static DeckKey Key(int row, int col, string title, string command, KeyIcon icon, string background) => new()
    {
        Row = row,
        Col = col,
        Title = new KeyTitle { Text = title },
        Background = background,
        Icon = icon,
        Action = new KeyAction { Type = "command", Command = command },
    };

    /// <summary>Adds a second stage so the sample shows a two-stage toggle (design memo §3.2).</summary>
    private static DeckKey Toggle(DeckKey key, string title, string command, KeyIcon icon, string background)
    {
        key.Action.Stages = [new KeyStage { Title = new KeyTitle { Text = title }, Background = background, Icon = icon, Command = command }];
        return key;
    }
}

public sealed class AppSettings
{
    public GameSettings Game { get; set; } = new();

    /// <summary>Admin UI port (loopback only). 0 = automatic.</summary>
    public int AdminPort { get; set; }

    /// <summary>Deck UI port (all interfaces).</summary>
    public int DeckPort { get; set; } = 20200;

    /// <summary>Network adapter (name or id) whose IPv4 address goes into the QR code. null = automatic.</summary>
    public string? LanAdapter { get; set; }

    public TunnelSettings Tunnel { get; set; } = new();

    public bool AutoStart { get; set; }

    /// <summary>dark | light | system (shared by the admin and deck UIs).</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>auto | ja | en (design memo §3.9). Shared by the admin and deck UIs; "auto" follows the browser (UI) or the OS (server).</summary>
    public string Language { get; set; } = "auto";

    public bool DeckStatusBar { get; set; } = true;
}

public sealed class GameSettings
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 29200;
}

public sealed class TunnelSettings
{
    /// <summary>off | try | named. "off" = not started automatically; the admin UI can still start a TryCloudflare tunnel on demand.</summary>
    public string Mode { get; set; } = "off";

    /// <summary>Cloudflare Zero Trust tunnel token (secret; stripped from exports).</summary>
    public string? NamedToken { get; set; }

    /// <summary>Public URL of the named tunnel (cloudflared does not report it), e.g. https://deck.example.com.</summary>
    public string? NamedUrl { get; set; }

    /// <summary>Start the tunnel when the application starts (ignored when <see cref="Mode"/> is "off").</summary>
    public bool AutoStart { get; set; }

    [JsonIgnore]
    public bool IsOff => Mode == "off";

    [JsonIgnore]
    public bool IsNamed => Mode == "named";
}

public sealed class DeckProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = "Default";

    public int Order { get; set; }

    /// <summary>Fixed grid, landscape orientation.</summary>
    public int Columns { get; set; } = 5;

    public int Rows { get; set; } = 3;

    public List<DeckKey> Keys { get; set; } = [];
}

public sealed class DeckKey
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public int Row { get; set; }

    public int Col { get; set; }

    public KeyTitle Title { get; set; } = new();

    /// <summary>CSS colour of the key background.</summary>
    public string Background { get; set; } = "#2a2a2a";

    public KeyIcon? Icon { get; set; }

    public KeyAction Action { get; set; } = new();

    public bool HoldToConfirm { get; set; }

    /// <summary>1 + the number of extra stages.</summary>
    [JsonIgnore]
    public int StageCount => 1 + (Action?.Stages?.Count ?? 0);

    /// <summary>The macros of stage <paramref name="stage"/> (0 = the key itself). Out-of-range stages fall back to the key.</summary>
    public (string? Command, string? ReleaseCommand) MacrosAt(int stage)
    {
        if (stage > 0 && Action?.Stages is { } stages && stage <= stages.Count)
        {
            var s = stages[stage - 1];
            return (s.Command, s.ReleaseCommand);
        }

        return (Action?.Command, Action?.ReleaseCommand);
    }

    /// <summary>The key's own icon followed by the icons of its extra stages.</summary>
    public IEnumerable<KeyIcon?> AllIcons()
    {
        yield return Icon;
        foreach (var stage in Action?.Stages ?? [])
        {
            yield return stage.Icon;
        }
    }
}

public sealed class KeyTitle
{
    public string Text { get; set; } = string.Empty;

    /// <summary>top | middle | bottom</summary>
    public string Position { get; set; } = "bottom";

    public bool Visible { get; set; } = true;
}

/// <summary>
/// Icon reference (design memo §3.8). Flat shape so it round-trips through JSON:
/// <c>{type:"mdi",name}</c>, <c>{type:"fa",style,name}</c>, <c>{type:"emoji",value}</c>, <c>{type:"image",hash}</c>.
/// </summary>
public sealed class KeyIcon
{
    public string Type { get; set; } = "mdi";

    public string? Name { get; set; }

    public string? Style { get; set; }

    public string? Value { get; set; }

    public string? Hash { get; set; }

    public static KeyIcon Mdi(string name) => new() { Type = "mdi", Name = name };

    public static KeyIcon Fa(string style, string name) => new() { Type = "fa", Style = style, Name = name };

    public static KeyIcon Emoji(string value) => new() { Type = "emoji", Value = value };
}

public sealed class KeyAction
{
    /// <summary>Extra stages allowed on top of the key itself (design memo §3.2): 5 stages in total.</summary>
    public const int MaxExtraStages = 4;

    /// <summary>command (future: folder / switchProfile)</summary>
    public string Type { get; set; } = "command";

    /// <summary>Macro sent when the key is pressed (stage 1). With <see cref="ReleaseCommand"/> set it goes out on pointer-down instead of on tap.</summary>
    public string? Command { get; set; }

    /// <summary>Macro sent when the finger lifts; makes the key a "hold key" (design memo §3.2).</summary>
    public string? ReleaseCommand { get; set; }

    /// <summary>Stages 2..5, each with its own look and macros. Stage 1 is the key itself.</summary>
    public List<KeyStage>? Stages { get; set; }
}

/// <summary>One of the extra stages of a key (design memo §3.2). Carries a full look; nothing is inherited from the key.</summary>
public sealed class KeyStage
{
    public KeyTitle Title { get; set; } = new();

    public string Background { get; set; } = "#2a2a2a";

    public KeyIcon? Icon { get; set; }

    public string? Command { get; set; }

    public string? ReleaseCommand { get; set; }
}

/// <summary>Shared JSON conventions: camelCase, nulls omitted, comments and trailing commas tolerated on read.</summary>
public static class FxJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Compact variant for the WebSocket.</summary>
    public static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
