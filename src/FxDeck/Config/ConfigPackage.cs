using System.IO.Compression;
using System.Text.Json;
using FxDeck.Localization;

namespace FxDeck.Config;

public enum ImportMode
{
    /// <summary>Append the uploaded profile(s) to the existing ones.</summary>
    Profile,

    /// <summary>Replace settings (except secrets) and all profiles.</summary>
    All,
}

public sealed record ImportResult(AppConfig Config, int ProfilesAdded, IReadOnlyList<string> Warnings);

/// <summary>
/// <c>.fxdeck</c> export/import (design memo §3.8): a zip holding <c>profile.json</c> or <c>config.json</c>
/// plus <c>assets/&lt;hash&gt;.png</c> for the user images the exported keys refer to. Plain JSON is accepted on import too.
/// </summary>
public static class ConfigPackage
{
    public const string Extension = ".fxdeck";
    public const string ProfileEntry = "profile.json";
    public const string ConfigEntry = "config.json";
    public const string AssetsFolder = "assets/";
    private const long MaxJsonBytes = 8 * 1024 * 1024;
    private const long MaxAssetBytes = 4 * 1024 * 1024;
    private const int MaxAssetCount = 1000;

    public static byte[] ExportProfile(DeckProfile profile, AssetStore? assets = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Zip(ProfileEntry, JsonSerializer.SerializeToUtf8Bytes(profile, FxJson.Options), CollectAssets([profile], assets));
    }

    /// <summary>Whole configuration without secrets (the deck token lives elsewhere; the tunnel token is stripped).</summary>
    public static byte[] ExportAll(AppConfig config, AssetStore? assets = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var copy = Clone(config);
        copy.Settings.Tunnel.NamedToken = null;
        return Zip(ConfigEntry, JsonSerializer.SerializeToUtf8Bytes(copy, FxJson.Options), CollectAssets(copy.Profiles, assets));
    }

    public static string ExportFileName(DeckProfile? profile)
    {
        var stem = profile is null ? "fxdeck-all" : "fxdeck-" + string.Concat(profile.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return $"{stem}-{DateTime.Now:yyyyMMdd-HHmm}{Extension}";
    }

    /// <summary>
    /// Parses an uploaded <c>.fxdeck</c> or <c>.json</c> and merges it into <paramref name="current"/> (which is not modified).
    /// Images bundled in the zip are stored into <paramref name="assets"/>; keys whose image cannot be resolved lose their icon (with a warning).
    /// </summary>
    /// <exception cref="InvalidDataException">The upload is not a recognisable FxDeck file.</exception>
    public static ImportResult Import(byte[] upload, ImportMode mode, AppConfig current, AssetStore? assets = null, Lang lang = Lang.Ja)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(current);

        var (entryName, json, bundled) = Unwrap(upload, lang);
        var payload = Parse(json, entryName, lang);
        var result = Clone(current);
        var warnings = new List<string>();

        if (mode == ImportMode.All)
        {
            if (payload.Config is null)
            {
                throw new InvalidDataException(Strings.Get(lang, "package.needConfigForAll"));
            }

            if (payload.Config.Version != 1)
            {
                throw new InvalidDataException(Strings.Get(lang, "package.unsupportedVersion", payload.Config.Version));
            }

            result.Settings = payload.Config.Settings ?? new AppSettings();
            result.Settings.Tunnel.NamedToken = current.Settings.Tunnel.NamedToken; // never overwrite a secret with an export
            result.Profiles = payload.Config.Profiles ?? [];
            Normalize(result, warnings, bundled, assets, lang);
            return new ImportResult(result, result.Profiles.Count, warnings);
        }

        var incoming = payload.Profiles;
        if (incoming.Count == 0)
        {
            throw new InvalidDataException(Strings.Get(lang, "package.noProfiles"));
        }

        var nextOrder = result.Profiles.Count == 0 ? 0 : result.Profiles.Max(p => p.Order) + 1;
        foreach (var profile in incoming)
        {
            profile.Order = nextOrder++;
            result.Profiles.Add(profile);
        }

        Normalize(result, warnings, bundled, assets, lang);
        return new ImportResult(result, incoming.Count, warnings);
    }

    /// <summary>Images referenced by <paramref name="profiles"/> that the store actually has.</summary>
    private static Dictionary<string, byte[]> CollectAssets(IEnumerable<DeckProfile> profiles, AssetStore? assets)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (assets is null)
        {
            return result;
        }

        foreach (var icon in profiles.SelectMany(p => p.Keys ?? []).SelectMany(k => k.AllIcons()))
        {
            if (icon is { Type: "image", Hash: { } hash } && !result.ContainsKey(hash) && assets.Read(hash) is { } png)
            {
                result[hash] = png;
            }
        }

        return result;
    }

    private static (string Entry, byte[] Json, Dictionary<string, byte[]> Assets) Unwrap(byte[] upload, Lang lang)
    {
        var bundled = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (upload.Length >= 4 && upload[0] == 0x50 && upload[1] == 0x4B)
        {
            using var zip = new ZipArchive(new MemoryStream(upload), ZipArchiveMode.Read);
            var entry = zip.GetEntry(ProfileEntry) ?? zip.GetEntry(ConfigEntry)
                ?? throw new InvalidDataException(Strings.Get(lang, "package.zipMissingEntries", ProfileEntry, ConfigEntry));
            if (entry.Length > MaxJsonBytes)
            {
                throw new InvalidDataException(Strings.Get(lang, "package.jsonTooLarge"));
            }

            foreach (var asset in zip.Entries)
            {
                if (bundled.Count >= MaxAssetCount || !asset.FullName.StartsWith(AssetsFolder, StringComparison.Ordinal) || asset.Length == 0 || asset.Length > MaxAssetBytes)
                {
                    continue;
                }

                var hash = Path.GetFileNameWithoutExtension(asset.Name);
                if (!AssetStore.IsValidHash(hash) || !asset.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bundled[hash] = ReadEntry(asset);
            }

            return (entry.Name, ReadEntry(entry), bundled);
        }

        if (upload.Length > MaxJsonBytes)
        {
            throw new InvalidDataException(Strings.Get(lang, "package.jsonTooLarge"));
        }

        return ("upload.json", upload, bundled);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed record Payload(AppConfig? Config, List<DeckProfile> Profiles);

    /// <summary>A profile has <c>keys</c>; a configuration has <c>profiles</c>. Anything else is rejected.</summary>
    private static Payload Parse(byte[] json, string entryName, Lang lang)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Strings.Get(lang, "package.notJson", entryName, ex.Message));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(Strings.Get(lang, "package.notObject", entryName));
            }

            try
            {
                if (root.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
                {
                    var config = root.Deserialize<AppConfig>(FxJson.Options) ?? throw new InvalidDataException(Strings.Get(lang, "package.emptyConfig"));
                    return new Payload(config, config.Profiles);
                }

                if (root.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
                {
                    var profile = root.Deserialize<DeckProfile>(FxJson.Options) ?? throw new InvalidDataException(Strings.Get(lang, "package.emptyProfile"));
                    return new Payload(null, [profile]);
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(Strings.Get(lang, "package.invalidContent", entryName, ex.Message));
            }

            throw new InvalidDataException(Strings.Get(lang, "package.notFxDeck", entryName));
        }
    }

    /// <summary>Regenerates colliding or missing ids, fixes orders, stores bundled images and drops icons whose image is unavailable.</summary>
    private static void Normalize(AppConfig config, List<string> warnings, Dictionary<string, byte[]> bundled, AssetStore? assets, Lang lang)
    {
        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal); // bundled hash → stored hash
        var missingImages = 0;
        foreach (var profile in config.Profiles.OrderBy(p => p.Order).Select((p, i) => { p.Order = i; return p; }))
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || !profileIds.Add(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString();
                profileIds.Add(profile.Id);
            }

            profile.Keys ??= [];
            foreach (var key in profile.Keys)
            {
                if (string.IsNullOrWhiteSpace(key.Id) || !keyIds.Add(key.Id))
                {
                    key.Id = Guid.NewGuid().ToString();
                    keyIds.Add(key.Id);
                }

                key.Title ??= new KeyTitle();
                key.Action ??= new KeyAction();
                key.Icon = ResolveIcon(key.Icon, bundled, assets, resolved, ref missingImages);
                foreach (var stage in key.Action.Stages ?? [])
                {
                    stage.Title ??= new KeyTitle();
                    stage.Icon = ResolveIcon(stage.Icon, bundled, assets, resolved, ref missingImages);
                }
            }
        }

        if (missingImages > 0)
        {
            warnings.Add(Strings.Get(lang, "package.missingImages", missingImages));
        }
    }

    /// <summary>Image icons get their stored hash, or are dropped (counted in <paramref name="missingImages"/>) when the image is nowhere to be found.</summary>
    private static KeyIcon? ResolveIcon(KeyIcon? icon, Dictionary<string, byte[]> bundled, AssetStore? assets, Dictionary<string, string> resolved, ref int missingImages)
    {
        if (icon?.Type != "image")
        {
            return icon;
        }

        var stored = ResolveImage(icon.Hash, bundled, assets, resolved);
        if (stored is null)
        {
            missingImages++;
            return null;
        }

        icon.Hash = stored;
        return icon;
    }

    /// <summary>The hash to keep: already stored → itself; bundled in the zip → stored (re-normalised) hash; otherwise null.</summary>
    private static string? ResolveImage(string? hash, Dictionary<string, byte[]> bundled, AssetStore? assets, Dictionary<string, string> resolved)
    {
        if (assets is null || hash is null)
        {
            return null;
        }

        if (resolved.TryGetValue(hash, out var known))
        {
            return known;
        }

        if (assets.Exists(hash))
        {
            return resolved[hash] = hash;
        }

        if (bundled.TryGetValue(hash, out var png))
        {
            try
            {
                return resolved[hash] = assets.Save(png);
            }
            catch (InvalidDataException)
            {
                // corrupt entry: treat as missing
            }
        }

        return null;
    }

    private static byte[] Zip(string entryName, byte[] json, Dictionary<string, byte[]> assets)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            {
                stream.Write(json); // must be closed before the next entry is created
            }

            zip.CreateEntry(AssetsFolder);
            foreach (var (hash, png) in assets.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                var image = zip.CreateEntry($"{AssetsFolder}{hash}.png", CompressionLevel.NoCompression); // PNG is already compressed
                using var stream = image.Open();
                stream.Write(png);
            }
        }

        return buffer.ToArray();
    }

    private static AppConfig Clone(AppConfig config) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.SerializeToUtf8Bytes(config, FxJson.Options), FxJson.Options)!;
}
