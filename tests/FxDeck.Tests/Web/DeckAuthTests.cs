using FxDeck.Config;
using FxDeck.Web;

namespace FxDeck.Tests.Web;

public class DeckAuthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fxdeck-tests", Guid.NewGuid().ToString("N"));
    private readonly DeckTokenStore _tokens;
    private readonly DeckAuth _auth;

    public DeckAuthTests()
    {
        _tokens = new DeckTokenStore(_dir);
        _tokens.Load();
        _auth = new DeckAuth(_tokens);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void TokenIsRandomBase64UrlOfAtLeast32Bytes()
    {
        var token = _tokens.Token;

        Assert.True(token.Length >= 43, token);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.NotEqual(token, DeckTokenStore.Generate());
    }

    [Fact]
    public void TokenIsPersistedWithoutBom()
    {
        var reloaded = new DeckTokenStore(_dir);
        reloaded.Load();

        Assert.Equal(_tokens.Token, reloaded.Token);
        var bytes = File.ReadAllBytes(_tokens.TokenPath);
        Assert.Equal(_tokens.Token, System.Text.Encoding.ASCII.GetString(bytes)); // scripts read it raw
    }

    [Fact]
    public void ValidatesOnlyTheExactToken()
    {
        var token = _tokens.Token;

        Assert.True(_auth.ValidateToken(token));
        Assert.False(_auth.ValidateToken(null));
        Assert.False(_auth.ValidateToken(string.Empty));
        Assert.False(_auth.ValidateToken(token[..^1]));
        Assert.False(_auth.ValidateToken(token + "x"));
        Assert.False(_auth.ValidateToken(token.ToUpperInvariant() == token ? token.ToLowerInvariant() : token.ToUpperInvariant()));
    }

    [Fact]
    public void SessionIsDerivedDeterministicallyFromTheToken()
    {
        var session = _auth.SessionValue();

        Assert.Equal(session, DeckAuth.DeriveSession(_tokens.Token));
        Assert.NotEqual(session, _tokens.Token);
        Assert.True(_auth.ValidateSession(session));
        Assert.False(_auth.ValidateSession(_tokens.Token));
        Assert.False(_auth.ValidateSession(null));
    }

    [Fact]
    public void RotatingTheTokenInvalidatesTheSession()
    {
        var before = _auth.SessionValue();
        string? announced = null;
        _tokens.Rotated += (_, t) => announced = t;

        var rotated = _tokens.Rotate();

        Assert.Equal(rotated, announced);
        Assert.Equal(rotated, _tokens.Token);
        Assert.False(_auth.ValidateSession(before));
        Assert.True(_auth.ValidateSession(_auth.SessionValue()));
    }
}
