using System.Security.Cryptography;
using System.Text;

namespace FxDeck.Tray;

/// <summary>
/// One FxDeck per data directory (design memo §3.6). The running instance publishes its admin URL in
/// <c>admin-url</c>; a second launch opens that URL in the browser and exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string AdminUrlFileName = "admin-url";

    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex, bool isFirst)
    {
        _mutex = mutex;
        IsFirst = isFirst;
    }

    public bool IsFirst { get; }

    public static SingleInstance Acquire(string dataDirectory)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).ToLowerInvariant())))[..16];
        var mutex = new Mutex(initiallyOwned: true, $"Local\\FxDeck-{hash}", out var createdNew);
        return new SingleInstance(mutex, createdNew);
    }

    public static string AdminUrlPath(string dataDirectory) => Path.Combine(dataDirectory, AdminUrlFileName);

    public static void PublishAdminUrl(string dataDirectory, string url) =>
        File.WriteAllText(AdminUrlPath(dataDirectory), url, new UTF8Encoding(false));

    public static string? ReadAdminUrl(string dataDirectory)
    {
        var path = AdminUrlPath(dataDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        var url = File.ReadAllText(path).Trim();
        return url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) ? url : null;
    }

    public void Dispose()
    {
        if (IsFirst)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // released on another thread; ignore
            }
        }

        _mutex.Dispose();
    }
}
