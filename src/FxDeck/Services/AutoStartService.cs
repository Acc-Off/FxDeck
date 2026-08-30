using Microsoft.Win32;

namespace FxDeck.Services;

/// <summary>"Start with Windows" via the per-user Run key (design memo §3.6).</summary>
public sealed class AutoStartService
{
    public const string ValueName = "FxDeck";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _executable;

    public AutoStartService(string? executable = null)
    {
        _executable = executable ?? Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the executable path.");
    }

    public string Command => $"\"{_executable}\"";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Cannot open the Run registry key.");
        if (enabled)
        {
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
