using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text;
using FxDeck.Config;

namespace FxDeck.Tests.Web;

/// <summary>User image store (design memo §3.8): normalisation, content addressing, pruning and packaging.</summary>
public class AssetStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fxdeck-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>A solid-colour test image of the given size, encoded with <paramref name="format"/>.</summary>
    internal static byte[] MakeImage(int width, int height, Color colour, ImageFormat? format = null)
    {
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(colour);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, format ?? ImageFormat.Png);
        return stream.ToArray();
    }

    [Fact]
    public void NormalizeFitsIntoA256SquareKeepingTheAspectRatio()
    {
        var png = AssetStore.Normalize(MakeImage(100, 50, Color.Red, ImageFormat.Jpeg));

        using var image = Image.FromStream(new MemoryStream(png));
        Assert.Equal(AssetStore.Size, image.Width);
        Assert.Equal(AssetStore.Size, image.Height);
        Assert.Equal(ImageFormat.Png.Guid, image.RawFormat.Guid);
        using var bitmap = new Bitmap(image);
        Assert.Equal(0, bitmap.GetPixel(128, 10).A); // letterboxed rows are transparent
        var centre = bitmap.GetPixel(128, 128);
        Assert.True(centre.R > 200 && centre.G < 60 && centre.B < 60, $"centre pixel should be red, was {centre}");
    }

    [Fact]
    public void NormalizeRejectsNonImages()
    {
        var ex = Assert.Throws<InvalidDataException>(() => AssetStore.Normalize(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>")));
        Assert.Contains("画像", ex.Message);
    }

    [Fact]
    public void SaveIsContentAddressedAndIdempotent()
    {
        var store = new AssetStore(_dir);
        var image = MakeImage(64, 64, Color.Blue);

        var hash = store.Save(image);
        var again = store.Save(image);

        Assert.Equal(hash, again);
        Assert.True(AssetStore.IsValidHash(hash));
        Assert.True(store.Exists(hash));
        Assert.Single(store.List());
        Assert.Equal(store.Read(hash), AssetStore.Normalize(image));
        Assert.NotEqual(hash, store.Save(MakeImage(64, 64, Color.Green)));
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void AlreadyNormalisedPngIsStoredByteForByte()
    {
        var store = new AssetStore(_dir);
        var normalised = AssetStore.Normalize(MakeImage(300, 120, Color.Teal));
        Assert.True(AssetStore.IsNormalizedPng(normalised));
        Assert.False(AssetStore.IsNormalizedPng(MakeImage(256, 256, Color.Teal, ImageFormat.Jpeg)));
        Assert.False(AssetStore.IsNormalizedPng(MakeImage(255, 256, Color.Teal)));

        var hash = store.Save(normalised);

        Assert.Equal(normalised, store.Read(hash));
        Assert.Equal(hash, store.Save(store.Read(hash)!)); // stable across export → import
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("../../etc/passwd")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public void MalformedHashesAreRejectedWithoutTouchingTheDisk(string? hash)
    {
        var store = new AssetStore(_dir);

        Assert.False(AssetStore.IsValidHash(hash));
        Assert.Null(store.PathOf(hash));
        Assert.Null(store.Read(hash));
    }

    [Fact]
    public void DeleteUnusedKeepsReferencedImages()
    {
        var store = new AssetStore(_dir);
        var used = store.Save(MakeImage(32, 32, Color.Red));
        var unused = store.Save(MakeImage(32, 32, Color.Blue));
        var config = AppConfig.CreateDefault();
        config.Profiles[0].Keys[0].Icon = new KeyIcon { Type = "image", Hash = used };

        var deleted = store.DeleteUnused(config);

        Assert.Equal(1, deleted);
        Assert.True(store.Exists(used));
        Assert.False(store.Exists(unused));
        Assert.Equal(0, store.DeleteUnused(config));
    }

    [Fact]
    public void ExportBundlesReferencedImagesAndImportStoresThem()
    {
        var source = new AssetStore(Path.Combine(_dir, "a"));
        var hash = source.Save(MakeImage(40, 40, Color.Orange));
        var config = AppConfig.CreateDefault();
        config.Profiles[0].Keys[0].Icon = new KeyIcon { Type = "image", Hash = hash };

        var package = ConfigPackage.ExportProfile(config.Profiles[0], source);

        using (var zip = new ZipArchive(new MemoryStream(package)))
        {
            Assert.Contains(zip.Entries, e => e.FullName == $"assets/{hash}.png");
        }

        var target = new AssetStore(Path.Combine(_dir, "b"));
        var result = ConfigPackage.Import(package, ImportMode.Profile, AppConfig.CreateDefault(), target);

        Assert.Empty(result.Warnings);
        Assert.True(target.Exists(hash)); // re-normalising a normalised PNG yields the same bytes → same hash
        Assert.Equal(hash, result.Config.Profiles[1].Keys[0].Icon!.Hash);
    }

    [Fact]
    public void ImportWithoutTheImageFallsBackToTheLabel()
    {
        var target = new AssetStore(Path.Combine(_dir, "b"));
        var hash = new string('a', 64);
        var json = Encoding.UTF8.GetBytes($$"""{ "name": "Img", "keys": [ { "row": 0, "col": 0, "title": { "text": "Pic" }, "icon": { "type": "image", "hash": "{{hash}}" }, "action": { "command": "a" } } ] }""");

        var result = ConfigPackage.Import(json, ImportMode.Profile, AppConfig.CreateDefault(), target);

        Assert.Null(result.Config.Profiles[1].Keys[0].Icon);
        Assert.Contains(result.Warnings, w => w.Contains("1 個"));
    }

    [Fact]
    public void ImportKeepsImagesTheStoreAlreadyHasWithoutABundle()
    {
        var store = new AssetStore(_dir);
        var hash = store.Save(MakeImage(20, 20, Color.Purple));
        var json = Encoding.UTF8.GetBytes($$"""{ "name": "Img", "keys": [ { "row": 0, "col": 0, "title": { "text": "Pic" }, "icon": { "type": "image", "hash": "{{hash}}" }, "action": { "command": "a" } } ] }""");

        var result = ConfigPackage.Import(json, ImportMode.Profile, AppConfig.CreateDefault(), store);

        Assert.Empty(result.Warnings);
        Assert.Equal(hash, result.Config.Profiles[1].Keys[0].Icon!.Hash);
        Assert.Empty(ConfigValidator.Validate(result.Config));
    }

    [Fact]
    public void ValidatorRequiresAWellFormedHash()
    {
        var config = AppConfig.CreateDefault();
        config.Profiles[0].Keys[0].Icon = new KeyIcon { Type = "image", Hash = "nope" };

        Assert.Contains(ConfigValidator.Validate(config), e => e.Contains("画像"));

        config.Profiles[0].Keys[0].Icon!.Hash = new string('0', 64);
        Assert.Empty(ConfigValidator.Validate(config));
    }
}
