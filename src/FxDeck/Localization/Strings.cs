using System.Globalization;

namespace FxDeck.Localization;

public enum Lang
{
    Ja,
    En,
}

/// <summary>
/// Server-side UI strings (design memo §3.9): validation and import errors, API messages, tray, console output.
/// One dictionary per language in <c>Strings.&lt;lang&gt;.cs</c>; Japanese is the source of truth and the fallback.
/// Placeholders are <see cref="string.Format(string, object[])"/> style.
/// To add a language: add a <c>Strings.xx.cs</c> with the dictionary, a <see cref="Lang"/> member, a case in
/// <see cref="Lookup"/> and <see cref="Resolve"/>, the code in <c>ConfigValidator.Languages</c>, and <c>xx.ts</c> on the web side.
/// </summary>
public static partial class Strings
{
    // A switch rather than a static table: the per-language dictionaries live in other partial files, and static
    // field initialisers across partial files run in an unspecified order (a table built here could see nulls).
    private static IReadOnlyDictionary<string, string>? Lookup(Lang lang) => lang switch
    {
        Lang.Ja => Ja,
        Lang.En => En,
        _ => null,
    };

    /// <summary>The dictionary that is the source of truth; every key must exist here.</summary>
    private static IReadOnlyDictionary<string, string> Fallback => Ja;

    public static IReadOnlyCollection<Lang> Languages => Enum.GetValues<Lang>();

    public static IEnumerable<string> Keys => Fallback.Keys;

    /// <summary>Raw (unformatted) entries of one language, for completeness checks.</summary>
    public static IReadOnlyDictionary<string, string> Dictionary(Lang lang) =>
        Lookup(lang) ?? throw new ArgumentOutOfRangeException(nameof(lang), lang, "No dictionary is registered for this language.");

    /// <summary>The string in <paramref name="lang"/>, falling back to Japanese, then to the key itself.</summary>
    public static string Get(Lang lang, string key, params object?[] args)
    {
        var dictionary = Lookup(lang);
        if (dictionary is null || !dictionary.TryGetValue(key, out var template))
        {
            if (!Fallback.TryGetValue(key, out template))
            {
                return key;
            }
        }

        return args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);
    }

    /// <summary>Language for messages produced before the configuration is loaded: the OS UI culture.</summary>
    public static Lang FromCulture(CultureInfo? culture = null) =>
        (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName.Equals("ja", StringComparison.OrdinalIgnoreCase) ? Lang.Ja : Lang.En;

    /// <summary>Maps <c>settings.language</c> (auto | ja | en) to a language; "auto" follows the OS.</summary>
    public static Lang Resolve(string? setting) => setting switch
    {
        "ja" => Lang.Ja,
        "en" => Lang.En,
        _ => FromCulture(),
    };
}
