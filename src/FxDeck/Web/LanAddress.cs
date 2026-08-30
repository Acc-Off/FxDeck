using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FxDeck.Web;

/// <summary>Picks the IPv4 address that goes into the LAN QR code (design memo §3.5).</summary>
public static class LanAddress
{
    public sealed record Candidate(string AdapterId, string AdapterName, IPAddress Address, bool HasGateway);

    /// <summary>Every usable IPv4 address, best first.</summary>
    public static IReadOnlyList<Candidate> ListCandidates()
    {
        var list = new List<Candidate>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var properties = nic.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || IsLinkLocal(address))
                {
                    continue;
                }

                list.Add(new Candidate(nic.Id, nic.Name, address, hasGateway));
            }
        }

        return list
            .OrderByDescending(c => c.HasGateway)
            .ThenBy(c => IsLikelyVirtual(c.AdapterName) ? 1 : 0)
            .ThenBy(c => c.AdapterName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The address of <paramref name="preferredAdapter"/> (id or name), or the best automatic choice.</summary>
    public static IPAddress? Detect(string? preferredAdapter = null)
    {
        var candidates = ListCandidates();
        if (!string.IsNullOrWhiteSpace(preferredAdapter))
        {
            var preferred = candidates.FirstOrDefault(c =>
                string.Equals(c.AdapterId, preferredAdapter, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.AdapterName, preferredAdapter, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return preferred.Address;
            }
        }

        return candidates.FirstOrDefault()?.Address;
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool IsLikelyVirtual(string name) =>
        name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
        || name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
        || name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
        || name.Contains("WSL", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase);
}
