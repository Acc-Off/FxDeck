using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace FxDeck.Web;

/// <summary>
/// The two Kestrel listeners (design memo §3.3): admin on loopback, deck on every interface.
/// Ports are resolved from the server once it has started (the admin port may be automatic).
/// </summary>
public sealed class ListenerInfo
{
    private readonly object _sync = new();
    private readonly IServer _server;
    private bool _resolved;

    public ListenerInfo(IServer server, int adminPort, int deckPort, int settingsAdminPort, int settingsDeckPort)
    {
        _server = server;
        AdminPort = adminPort;
        DeckPort = deckPort;
        SettingsAdminPort = settingsAdminPort;
        SettingsDeckPort = settingsDeckPort;
    }

    /// <summary>Admin listener port; 0 until resolved when automatic.</summary>
    public int AdminPort { get; private set; }

    public int DeckPort { get; private set; }

    /// <summary>Port values read from config.json at start-up (command-line overrides excluded), to detect edits that need a restart.</summary>
    public int SettingsAdminPort { get; }

    public int SettingsDeckPort { get; }

    /// <summary>True when <paramref name="settings"/> differs from what this process was started with.</summary>
    public bool RequiresRestart(Config.AppSettings settings) => settings.DeckPort != SettingsDeckPort || settings.AdminPort != SettingsAdminPort;

    public bool IsResolved => _resolved;

    /// <summary>Reads the bound addresses from the server. Safe to call repeatedly; a no-op once resolved.</summary>
    public void EnsureResolved()
    {
        if (_resolved)
        {
            return;
        }

        lock (_sync)
        {
            if (_resolved)
            {
                return;
            }

            var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
            var endpoints = addresses.Select(Parse).Where(e => e is not null).Select(e => e!).ToList();
            if (endpoints.Count == 0)
            {
                return; // not started yet
            }

            if (DeckPort == 0)
            {
                DeckPort = endpoints.FirstOrDefault(e => !IPAddress.IsLoopback(e.Address))?.Port
                    ?? endpoints.Select(e => e.Port).FirstOrDefault(p => p != AdminPort);
            }

            if (AdminPort == 0)
            {
                AdminPort = endpoints.FirstOrDefault(e => IPAddress.IsLoopback(e.Address) && e.Port != DeckPort)?.Port ?? 0;
            }

            _resolved = AdminPort != 0 && DeckPort != 0;
        }
    }

    /// <summary>True when the request arrived on the admin listener from the local machine.</summary>
    public bool IsAdminConnection(Microsoft.AspNetCore.Http.ConnectionInfo connection)
    {
        EnsureResolved();
        return AdminPort != 0
            && connection.LocalPort == AdminPort
            && connection.RemoteIpAddress is { } remote
            && IPAddress.IsLoopback(remote);
    }

    private static IPEndPoint? Parse(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.Trim('[', ']');
        if (!IPAddress.TryParse(host, out var ip))
        {
            ip = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback : IPAddress.Any;
        }

        return new IPEndPoint(ip, uri.Port);
    }
}
