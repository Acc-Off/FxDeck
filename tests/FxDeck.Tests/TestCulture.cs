using System.Globalization;
using System.Runtime.CompilerServices;

namespace FxDeck.Tests;

/// <summary>
/// Several tests assert the Japanese wording that <c>language: auto</c> produces, which follows the OS UI culture
/// (design memo §3.9). Pin the test process to ja-JP so they behave the same on an English CI runner as on a
/// Japanese developer machine. Tests that exercise other cultures set <see cref="CultureInfo.CurrentUICulture"/> themselves.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var japanese = CultureInfo.GetCultureInfo("ja-JP");
        CultureInfo.DefaultThreadCurrentUICulture = japanese;
        CultureInfo.CurrentUICulture = japanese;
    }
}
