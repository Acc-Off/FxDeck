using System.Text.Json;
using FxDeck.NuiInspect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FxDeck.Config;

/// <summary>The single command cache written by an extraction (design memo §3.10).</summary>
public sealed class CommandCache
{
    /// <summary>Shown as the staleness hint in the admin UI.</summary>
    public DateTimeOffset ExtractedAt { get; set; }

    /// <summary>Display label of the server the commands came from. Best-effort; no source yet, so null.</summary>
    public string? Server { get; set; }

    public int Count { get; set; }

    public List<NuiCommand> Commands { get; set; } = [];
}

/// <summary>
/// Owns <c>commands-cache.json</c>: derived data, deliberately separate from <see cref="ConfigStore"/> —
/// not hot-reloaded, not validated, not exported, and replaced whole on every extraction (design memo §3.10, §4).
/// </summary>
public sealed class CommandCacheStore
{
    public const string FileName = "commands-cache.json";

    private readonly ILogger _logger;
    private readonly object _sync = new();
    private CommandCache? _current;

    public CommandCacheStore(string directory, ILogger<CommandCacheStore>? logger = null)
    {
        CachePath = Path.Combine(directory, FileName);
        _logger = logger ?? NullLogger<CommandCacheStore>.Instance;
    }

    public string CachePath { get; }

    /// <summary>The cached extraction, or <c>null</c> when nothing was extracted yet. Never mutate the instance.</summary>
    public CommandCache? Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>Loads the cache if present; a broken file is ignored (it is derived data — just re-extract).</summary>
    public void Load()
    {
        if (!File.Exists(CachePath))
        {
            return;
        }

        try
        {
            var cache = JsonSerializer.Deserialize<CommandCache>(File.ReadAllBytes(CachePath), FxJson.Options);
            lock (_sync)
            {
                _current = cache;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning("Ignoring unreadable {Path}: {Message}", CachePath, ex.Message);
        }
    }

    /// <summary>Replaces the whole cache atomically.</summary>
    public void Save(CommandCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cache, FxJson.Options);
        lock (_sync)
        {
            var temp = CachePath + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, CachePath, overwrite: true);
            _current = cache;
        }
    }

    public void Delete()
    {
        lock (_sync)
        {
            try
            {
                File.Delete(CachePath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning("Could not delete {Path}: {Message}", CachePath, ex.Message);
            }

            _current = null;
        }
    }
}
