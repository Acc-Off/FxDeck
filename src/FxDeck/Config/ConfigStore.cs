using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FxDeck.Config;

/// <summary>
/// Owns <c>config.json</c>: creates the default on first run, saves atomically and reloads when the
/// file is edited by hand (hot reload). <see cref="Current"/> is replaced as a whole on every change;
/// never mutate the instance you read from it.
/// </summary>
public sealed class ConfigStore : IDisposable
{
    public const string FileName = "config.json";

    private readonly ILogger _logger;
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounce;
    private byte[] _lastHash = [];
    private AppConfig _current = new();

    public ConfigStore(string directory, ILogger<ConfigStore>? logger = null)
    {
        Directory = directory;
        ConfigPath = Path.Combine(directory, FileName);
        _logger = logger ?? NullLogger<ConfigStore>.Instance;
    }

    public string Directory { get; }

    public string ConfigPath { get; }

    /// <summary>True when <see cref="Load"/> had to create the default file (first run).</summary>
    public bool CreatedDefault { get; private set; }

    public AppConfig Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised (on a thread-pool thread) after <see cref="Current"/> was replaced by a save or a reload.</summary>
    public event EventHandler<AppConfig>? Changed;

    /// <summary>Loads the file, creating the default configuration when it does not exist.</summary>
    public void Load()
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (!File.Exists(ConfigPath))
        {
            _logger.LogInformation("No configuration found; creating {Path}", ConfigPath);
            CreatedDefault = true;
            Save(AppConfig.CreateDefault());
            return;
        }

        var bytes = File.ReadAllBytes(ConfigPath);
        var config = JsonSerializer.Deserialize<AppConfig>(bytes, FxJson.Options)
            ?? throw new InvalidDataException($"{ConfigPath} is empty.");
        lock (_sync)
        {
            _current = config;
            _lastHash = SHA256.HashData(bytes);
        }
    }

    /// <summary>Writes <paramref name="config"/> atomically and makes it <see cref="Current"/>.</summary>
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        System.IO.Directory.CreateDirectory(Directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(config, FxJson.Options);
        var temp = ConfigPath + ".tmp";
        lock (_sync)
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, ConfigPath, overwrite: true);
            _current = config;
            _lastHash = SHA256.HashData(bytes);
        }

        RaiseChanged(config);
    }

    /// <summary>Starts watching the file for external edits.</summary>
    public void StartWatching()
    {
        if (_watcher is not null)
        {
            return;
        }

        _watcher = new FileSystemWatcher(Directory, FileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Re-reads the file if its content changed. Returns <c>true</c> when <see cref="Current"/> was replaced.</summary>
    public bool Reload()
    {
        byte[] bytes;
        try
        {
            bytes = ReadWithRetry();
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Could not read {Path}: {Message}", ConfigPath, ex.Message);
            return false;
        }

        var hash = SHA256.HashData(bytes);
        AppConfig config;
        lock (_sync)
        {
            if (hash.AsSpan().SequenceEqual(_lastHash))
            {
                return false;
            }

            try
            {
                config = JsonSerializer.Deserialize<AppConfig>(bytes, FxJson.Options) ?? throw new JsonException("empty document");
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("Ignoring invalid {Path}: {Message}", ConfigPath, ex.Message);
                return false;
            }

            _current = config;
            _lastHash = hash;
        }

        _logger.LogInformation("Configuration reloaded from {Path}", ConfigPath);
        RaiseChanged(config);
        return true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        lock (_sync)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Editors write in several steps; wait for the burst to settle before reading.
        CancellationTokenSource debounce;
        lock (_sync)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = debounce = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, debounce.Token).ConfigureAwait(false);
                if (File.Exists(ConfigPath))
                {
                    Reload();
                }
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer event
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reloading {Path} failed", ConfigPath);
            }
        });
    }

    private byte[] ReadWithRetry()
    {
        IOException? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return File.ReadAllBytes(ConfigPath);
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(50);
            }
        }

        throw last!;
    }

    private void RaiseChanged(AppConfig config)
    {
        try
        {
            Changed?.Invoke(this, config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A configuration change handler threw");
        }
    }
}
