using FxDeck.Commands;
using FxDeck.Config;
using FxDeck.FxConsole;

namespace FxDeck.Web;

/// <summary>Wire shapes of the deck WebSocket (design memo §3.3). Property names are camelCased by <see cref="FxJson.Wire"/>.</summary>
public static class DeckMessages
{
    /// <summary>Phone → PC: <c>press</c> or <c>release</c> (design memo §3.2).</summary>
    public sealed record ClientMessage(string Type, string? KeyId);

    /// <summary><paramref name="Stages"/> lists the keys currently on a stage other than the first (0-based index).</summary>
    public sealed record Hello(string Type, IReadOnlyList<DeckProfile> Profiles, DeckSettings Settings, string Game, IReadOnlyDictionary<string, int> Stages);

    public sealed record Status(string Type, string Game);

    /// <summary><paramref name="Phase"/> is <c>press</c> or <c>release</c>.</summary>
    public sealed record Result(string Type, string KeyId, string Phase, bool Success, string Reason, string? Message);

    public sealed record StageChanged(string Type, string KeyId, int Stage);

    public sealed record ProfilesChanged(string Type, IReadOnlyList<DeckProfile> Profiles);

    public sealed record SettingsChanged(string Type, DeckSettings Settings);

    public sealed record ConsoleLine(string Type, string Line);

    /// <summary>The subset of settings a phone needs.</summary>
    public sealed record DeckSettings(string Theme, bool DeckStatusBar, string Language)
    {
        public static DeckSettings From(AppSettings settings) => new(settings.Theme, settings.DeckStatusBar, settings.Language);
    }

    /// <summary>Close code sent to every phone when the token is rotated.</summary>
    public const int TokenRevokedCloseCode = 4001;

    public static string GameState(FxConsoleConnectionState state) => state switch
    {
        FxConsoleConnectionState.Connected => "connected",
        FxConsoleConnectionState.Connecting => "connecting",
        _ => "disconnected",
    };

    public static string ReasonName(MacroFailureReason reason) => reason switch
    {
        MacroFailureReason.None => "none",
        MacroFailureReason.NotConnected => "notConnected",
        MacroFailureReason.InvalidCommand => "invalidCommand",
        MacroFailureReason.Cancelled => "cancelled",
        MacroFailureReason.Disposed => "disposed",
        _ => "unknown",
    };
}
