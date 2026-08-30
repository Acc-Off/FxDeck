using System.Security.Cryptography;
using System.Text;
using FxDeck.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace FxDeck.Web;

/// <summary>
/// Deck authentication (design memo §3.4): the QR token is exchanged for a cookie whose value is derived
/// from the token with HMAC-SHA256. No server-side session state; rotating the token invalidates every cookie.
/// </summary>
public sealed class DeckAuth
{
    public const string CookieName = "fxdeck_session";
    public const string TokenQueryName = "t";
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(90);

    private static readonly byte[] SessionPurpose = "fxdeck-session-v1"u8.ToArray();

    private readonly DeckTokenStore _tokens;

    public DeckAuth(DeckTokenStore tokens)
    {
        _tokens = tokens;
    }

    /// <summary>Constant-time comparison of a presented token with the current one.</summary>
    public bool ValidateToken(string? candidate) => FixedTimeEquals(candidate, _tokens.Token);

    /// <summary>Cookie value for the current token.</summary>
    public string SessionValue() => DeriveSession(_tokens.Token);

    public bool ValidateSession(string? cookie) => FixedTimeEquals(cookie, SessionValue());

    public bool IsAuthenticated(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CookieName, out var cookie) && ValidateSession(cookie);

    /// <summary>Sets (or refreshes — the lifetime is sliding) the session cookie.</summary>
    public void IssueCookie(HttpContext context)
    {
        context.Response.Cookies.Append(CookieName, SessionValue(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.Add(CookieLifetime),
            IsEssential = true,
        });
    }

    public static string DeriveSession(string token)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), SessionPurpose);
        return WebEncoders.Base64UrlEncode(mac);
    }

    private static bool FixedTimeEquals(string? candidate, string expected)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(candidate);
        var b = Encoding.UTF8.GetBytes(expected);
        // Compare hashes so the length of the candidate never short-circuits the comparison.
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(a), SHA256.HashData(b)) && a.Length == b.Length;
    }
}
