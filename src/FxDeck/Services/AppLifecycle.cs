namespace FxDeck.Services;

/// <summary>Lets the admin UI ask the tray host to restart the application (after a port change).</summary>
public sealed class AppLifecycle
{
    /// <summary>Raised on a thread-pool thread; the tray context marshals it to the UI thread.</summary>
    public event EventHandler? RestartRequested;

    public void RequestRestart() => RestartRequested?.Invoke(this, EventArgs.Empty);
}
