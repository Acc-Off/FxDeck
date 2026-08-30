using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FxDeck.Config;

public sealed record AssetInfo(string Hash, long Size, DateTime ModifiedUtc);

/// <summary>
/// User images for key icons (design memo §3.8): <c>&lt;dataDir&gt;/assets/&lt;sha256&gt;.png</c>, every file a
/// 256×256 PNG normalised by <see cref="Normalize"/>. Content-addressed, so identical uploads share one file.
/// </summary>
public sealed partial class AssetStore
{
    public const int Size = 256;
    public const string DirectoryName = "assets";

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    public AssetStore(string dataDirectory)
    {
        Directory = Path.Combine(dataDirectory, DirectoryName);
    }

    public string Directory { get; }

    public static bool IsValidHash(string? hash) => hash is not null && HashPattern().IsMatch(hash);

    /// <summary>Path of the stored PNG, or null when the hash is malformed or unknown.</summary>
    public string? PathOf(string? hash)
    {
        if (!IsValidHash(hash))
        {
            return null;
        }

        var path = Path.Combine(Directory, hash + ".png");
        return File.Exists(path) ? path : null;
    }

    public bool Exists(string? hash) => PathOf(hash) is not null;

    public byte[]? Read(string? hash) => PathOf(hash) is { } path ? File.ReadAllBytes(path) : null;

    /// <summary>Largest already-normalised PNG stored as-is; anything bigger is redrawn (which also strips junk chunks).</summary>
    public const int MaxPassThroughBytes = 512 * 1024;

    /// <summary>
    /// Normalises the image and stores it; returns the hash. Idempotent for identical content. A file that already is
    /// a 256×256 PNG is stored byte-for-byte (GDI+ re-encoding is not deterministic), so images that travel through an
    /// export and back keep their hash and deduplicate.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a decodable image.</exception>
    public string Save(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var png = IsNormalizedPng(image) ? image : Normalize(image);
        var hash = Convert.ToHexStringLower(SHA256.HashData(png));
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, hash + ".png");
        if (!File.Exists(path))
        {
            var temp = Path.Combine(Directory, $"{hash}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(temp, png);
            File.Move(temp, path, overwrite: true);
        }

        return hash;
    }

    /// <summary>True for a decodable PNG that is already exactly 256×256 and not suspiciously large.</summary>
    public static bool IsNormalizedPng(byte[] image)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (image.Length > MaxPassThroughBytes || image.Length < signature.Length || !image.AsSpan(0, signature.Length).SequenceEqual(signature))
        {
            return false;
        }

        try
        {
            using var input = new MemoryStream(image);
            using var decoded = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
            return decoded.Width == Size && decoded.Height == Size && decoded.RawFormat.Guid == ImageFormat.Png.Guid;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException or IOException)
        {
            return false;
        }
    }

    /// <summary>Decodes PNG / JPEG / GIF (first frame) and redraws it centred into a 256×256 transparent PNG, keeping the aspect ratio.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a decodable image.</exception>
    public static byte[] Normalize(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var input = new MemoryStream(image);
        Image source;
        try
        {
            source = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException or IOException)
        {
            throw new InvalidDataException(Localization.Strings.Get(Localization.Strings.FromCulture(), "asset.notImage"), ex);
        }

        using (source)
        using (var bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                var scale = Math.Min((double)Size / source.Width, (double)Size / source.Height);
                var width = Math.Max(1, (int)Math.Round(source.Width * scale));
                var height = Math.Max(1, (int)Math.Round(source.Height * scale));
                graphics.DrawImage(source, new Rectangle((Size - width) / 2, (Size - height) / 2, width, height));
            }

            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
    }

    /// <summary>Stored images, newest first.</summary>
    public IReadOnlyList<AssetInfo> List()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(Directory, "*.png")
            .Select(path => new FileInfo(path))
            .Where(file => IsValidHash(Path.GetFileNameWithoutExtension(file.Name)))
            .Select(file => new AssetInfo(Path.GetFileNameWithoutExtension(file.Name), file.Length, file.LastWriteTimeUtc))
            .OrderByDescending(a => a.ModifiedUtc)
            .ThenBy(a => a.Hash, StringComparer.Ordinal)
            .ToList();
    }

    public static HashSet<string> ReferencedHashes(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Profiles
            .SelectMany(p => p.Keys ?? [])
            .SelectMany(k => k.AllIcons())
            .Where(i => i is { Type: "image", Hash: not null })
            .Select(i => i!.Hash!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Deletes every stored image no key refers to; returns how many were removed.</summary>
    public int DeleteUnused(AppConfig config)
    {
        var referenced = ReferencedHashes(config);
        var deleted = 0;
        foreach (var asset in List())
        {
            if (referenced.Contains(asset.Hash))
            {
                continue;
            }

            File.Delete(Path.Combine(Directory, asset.Hash + ".png"));
            deleted++;
        }

        return deleted;
    }
}
