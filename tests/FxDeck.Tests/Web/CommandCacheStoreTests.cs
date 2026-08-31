using FxDeck.Config;
using FxDeck.NuiInspect;

namespace FxDeck.Tests.Web;

public class CommandCacheStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fxdeck-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static CommandCache SampleCache() => new()
    {
        ExtractedAt = new DateTimeOffset(2026, 8, 31, 12, 34, 56, TimeSpan.FromHours(9)),
        Server = null,
        Count = 1,
        Commands = [new NuiCommand { Name = "jail", Help = "Jail a player", Params = [new NuiCommandParam { Name = "id", Optional = false }] }],
    };

    [Fact]
    public void RoundTripsThroughTheFile()
    {
        var store = new CommandCacheStore(_dir);
        Assert.Null(store.Current);
        store.Save(SampleCache());

        var reread = new CommandCacheStore(_dir);
        reread.Load();

        Assert.NotNull(reread.Current);
        Assert.Equal(1, reread.Current!.Count);
        var command = Assert.Single(reread.Current.Commands);
        Assert.Equal("jail", command.Name);
        Assert.Equal("Jail a player", command.Help);
        Assert.False(Assert.Single(command.Params!).Optional);
        Assert.Equal(TimeSpan.FromHours(9), reread.Current.ExtractedAt.Offset);
    }

    [Fact]
    public void WritesCamelCaseJson()
    {
        var store = new CommandCacheStore(_dir);
        store.Save(SampleCache());

        var json = File.ReadAllText(store.CachePath);
        Assert.Contains("\"extractedAt\"", json);
        Assert.Contains("\"commands\"", json);
        Assert.DoesNotContain("\"server\"", json); // nulls are omitted
    }

    [Fact]
    public void IgnoresABrokenFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, CommandCacheStore.FileName), "{ not json");

        var store = new CommandCacheStore(_dir);
        store.Load();

        Assert.Null(store.Current);
    }

    [Fact]
    public void DeleteRemovesFileAndMemory()
    {
        var store = new CommandCacheStore(_dir);
        store.Save(SampleCache());
        Assert.True(File.Exists(store.CachePath));

        store.Delete();

        Assert.Null(store.Current);
        Assert.False(File.Exists(store.CachePath));
        store.Delete(); // deleting again is harmless
    }

    [Fact]
    public void MissingFileLoadsAsEmpty()
    {
        var store = new CommandCacheStore(_dir);
        store.Load();
        Assert.Null(store.Current);
    }
}
