using FxDeck.Config;

namespace FxDeck.Localization;

/// <summary>Resolves the current server-side language from <c>settings.language</c> on every call (the setting can change at runtime).</summary>
public sealed class Localizer
{
    private readonly ConfigStore _config;

    public Localizer(ConfigStore config)
    {
        _config = config;
    }

    public Lang Current => Strings.Resolve(_config.Current.Settings.Language);

    public string T(string key, params object?[] args) => Strings.Get(Current, key, args);
}
