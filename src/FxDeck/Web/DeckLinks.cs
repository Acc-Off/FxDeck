using System.Net;

namespace FxDeck.Web;

/// <summary>Builds the URLs embedded in QR codes (design memo §3.4: <c>http(s)://host[:port]/?t=token</c>).</summary>
public static class DeckLinks
{
    public static string LanUrl(IPAddress address, int port, string token) =>
        $"http://{address}:{port}/?{DeckAuth.TokenQueryName}={Uri.EscapeDataString(token)}";

    public static string LanUrlWithoutToken(IPAddress address, int port) => $"http://{address}:{port}/";

    /// <param name="publicUrl">Tunnel origin without a trailing slash (<see cref="Services.TunnelService.NormalizeUrl"/>).</param>
    public static string TunnelUrl(string publicUrl, string token) =>
        $"{publicUrl.TrimEnd('/')}/?{DeckAuth.TokenQueryName}={Uri.EscapeDataString(token)}";

    public static string TunnelUrlWithoutToken(string publicUrl) => $"{publicUrl.TrimEnd('/')}/";
}
