namespace FxDeck.Config;

/// <summary>Where FxDeck keeps its files: <c>%LOCALAPPDATA%\FxDeck</c>, or <c>FXDECK_DATA_DIR</c> when set.</summary>
public static class DataPaths
{
    public const string EnvironmentVariable = "FXDECK_DATA_DIR";

    public static string ResolveDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FxDeck");
    }
}
