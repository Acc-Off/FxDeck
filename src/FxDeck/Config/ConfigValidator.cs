using FxDeck.Localization;

namespace FxDeck.Config;

/// <summary>Sanity checks applied before a configuration is saved (design memo §3.3). Messages are shown in the admin UI in <paramref name="lang"/>.</summary>
public static class ConfigValidator
{
    public const int MaxColumns = 12;
    public const int MaxRows = 8;

    private static readonly HashSet<string> Themes = ["dark", "light", "system"];
    private static readonly HashSet<string> Languages = ["auto", "ja", "en"];
    private static readonly HashSet<string> TitlePositions = ["top", "middle", "bottom"];
    private static readonly HashSet<string> ActionTypes = ["command"];
    private static readonly HashSet<string> IconTypes = ["mdi", "fa", "emoji", "image"];
    private static readonly HashSet<string> FaStyles = ["solid", "regular", "brands"];
    private static readonly HashSet<string> TunnelModes = ["off", "try", "named"];

    public static IReadOnlyList<string> Validate(AppConfig? config, Lang lang = Lang.Ja)
    {
        var errors = new List<string>();
        string T(string key, params object?[] args) => Strings.Get(lang, key, args);

        if (config is null)
        {
            errors.Add(T("validator.emptyConfig"));
            return errors;
        }

        if (config.Version != 1)
        {
            errors.Add(T("validator.unsupportedVersion", config.Version));
        }

        var s = config.Settings;
        if (s is null)
        {
            errors.Add(T("validator.noSettings"));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(s.Game?.Host)) errors.Add(T("validator.gameHostEmpty"));
            if (s.Game is not null && !IsPort(s.Game.Port)) errors.Add(T("validator.gamePortInvalid", s.Game.Port));
            if (!IsPort(s.DeckPort)) errors.Add(T("validator.deckPortInvalid", s.DeckPort));
            if (s.AdminPort is < 0 or > 65535) errors.Add(T("validator.adminPortInvalid", s.AdminPort));
            if (!Themes.Contains(s.Theme)) errors.Add(T("validator.themeInvalid", s.Theme));
            if (!Languages.Contains(s.Language)) errors.Add(T("validator.languageInvalid", s.Language));
            if (s.Tunnel is not null && !TunnelModes.Contains(s.Tunnel.Mode)) errors.Add(T("validator.tunnelModeInvalid", s.Tunnel.Mode));
            if (!string.IsNullOrWhiteSpace(s.Tunnel?.NamedUrl) && !IsHttpUrl(s.Tunnel.NamedUrl)) errors.Add(T("validator.tunnelUrlInvalid", s.Tunnel.NamedUrl));
        }

        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (profile, index) in (config.Profiles ?? []).Select((p, i) => (p, i)))
        {
            var label = string.IsNullOrWhiteSpace(profile.Name) ? T("validator.profileByIndex", index + 1) : T("validator.profileByName", profile.Name);
            if (string.IsNullOrWhiteSpace(profile.Id)) errors.Add(T("validator.profileIdEmpty", label));
            else if (!profileIds.Add(profile.Id)) errors.Add(T("validator.profileIdDuplicate", label));
            if (string.IsNullOrWhiteSpace(profile.Name)) errors.Add(T("validator.profileNameEmpty", label));
            if (profile.Columns is < 1 or > MaxColumns) errors.Add(T("validator.columnsRange", label, MaxColumns));
            if (profile.Rows is < 1 or > MaxRows) errors.Add(T("validator.rowsRange", label, MaxRows));

            var cells = new HashSet<(int, int)>();
            foreach (var key in profile.Keys ?? [])
            {
                var keyLabel = T("validator.keyLabel", label, key.Title?.Text);
                if (string.IsNullOrWhiteSpace(key.Id)) errors.Add(T("validator.keyIdEmpty", keyLabel));
                else if (!keyIds.Add(key.Id)) errors.Add(T("validator.keyIdDuplicate", keyLabel));
                if (key.Row < 0 || key.Row >= profile.Rows || key.Col < 0 || key.Col >= profile.Columns) errors.Add(T("validator.keyOutside", keyLabel, key.Row, key.Col));
                else if (!cells.Add((key.Row, key.Col))) errors.Add(T("validator.keyOverlap", keyLabel, key.Row, key.Col));
                if (key.Title is null) errors.Add(T("validator.keyNoTitle", keyLabel));
                else if (!TitlePositions.Contains(key.Title.Position)) errors.Add(T("validator.titlePositionInvalid", keyLabel, key.Title.Position));
                if (string.IsNullOrWhiteSpace(key.Background)) errors.Add(T("validator.backgroundEmpty", keyLabel));
                if (key.Action is null || !ActionTypes.Contains(key.Action.Type)) errors.Add(T("validator.actionUnsupported", keyLabel, key.Action?.Type));
                ValidateIcon(key.Icon, keyLabel, errors, T);

                var stages = key.Action?.Stages ?? [];
                if (stages.Count > KeyAction.MaxExtraStages) errors.Add(T("validator.tooManyStages", keyLabel, KeyAction.MaxExtraStages + 1));
                foreach (var (stage, stageIndex) in stages.Select((s, i) => (s, i)))
                {
                    var stageLabel = T("validator.stageLabel", keyLabel, stageIndex + 2);
                    if (stage is null)
                    {
                        errors.Add(T("validator.stageEmpty", stageLabel));
                        continue;
                    }

                    if (stage.Title is null) errors.Add(T("validator.keyNoTitle", stageLabel));
                    else if (!TitlePositions.Contains(stage.Title.Position)) errors.Add(T("validator.titlePositionInvalid", stageLabel, stage.Title.Position));
                    if (string.IsNullOrWhiteSpace(stage.Background)) errors.Add(T("validator.backgroundEmpty", stageLabel));
                    ValidateIcon(stage.Icon, stageLabel, errors, T);
                }
            }
        }

        return errors;
    }

    private static void ValidateIcon(KeyIcon? icon, string keyLabel, List<string> errors, Func<string, object?[], string> t)
    {
        if (icon is null)
        {
            return;
        }

        if (!IconTypes.Contains(icon.Type))
        {
            errors.Add(t("validator.iconTypeUnsupported", [keyLabel, icon.Type]));
            return;
        }

        switch (icon.Type)
        {
            case "mdi" when string.IsNullOrWhiteSpace(icon.Name):
            case "fa" when string.IsNullOrWhiteSpace(icon.Name):
                errors.Add(t("validator.iconNameEmpty", [keyLabel]));
                break;
            case "fa" when icon.Style is null || !FaStyles.Contains(icon.Style):
                errors.Add(t("validator.faStyleInvalid", [keyLabel, icon.Style]));
                break;
            case "emoji" when string.IsNullOrWhiteSpace(icon.Value):
                errors.Add(t("validator.emojiEmpty", [keyLabel]));
                break;
            case "image" when !AssetStore.IsValidHash(icon.Hash):
                errors.Add(t("validator.imageRefInvalid", [keyLabel]));
                break;
        }
    }

    private static bool IsPort(int port) => port is >= 1 and <= 65535;

    public static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && !string.IsNullOrEmpty(uri.Host);
}
