using System.Net;
using System.Net.Sockets;

namespace Bale.Core.Connection;

public sealed record DiscoveredServer(string ServerId, string Name, Uri Server);

/// <summary>LanServerDiscovery.kt: TXT identity, numeric-address bounds, de-duplication and missed refresh policy.</summary>
public static class LanDiscoveryRules
{
    public static string? ServerId(IEnumerable<string> attributes) => attributes
        .Select(value => value.Split('=', 2))
        .Where(pair => pair.Length == 2 && pair[0].Equals("id", StringComparison.OrdinalIgnoreCase))
        .Select(pair => pair[1].Trim()).FirstOrDefault(value => value.Length > 0);

    public static IReadOnlyList<Uri> Origins(IEnumerable<string> addresses, int port)
    {
        if (port is < 1 or > 65535) return [];
        return addresses.Select(value => value.Trim().TrimStart('/').Split('%')[0]).Distinct()
            .Select(value => IPAddress.TryParse(value, out var address) ? (Literal: value, Address: address) : default)
            .Where(pair => pair.Address is not null && Usable(pair.Literal, pair.Address))
            .OrderBy(pair => pair.Address!.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Select(pair => new UriBuilder("http", pair.Address!.ToString(), port).Uri).Distinct().Take(4).ToArray();
    }

    private static bool Usable(string literal, IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // IPAddress also accepts octal/abbreviated/single-integer hosts; DNS-SD must supply dotted literals.
            if (literal.Split('.').Length != 4 || literal.Any(c => c != '.' && !char.IsAsciiDigit(c))) return false;
            var first = address.GetAddressBytes()[0];
            return first != 0 && first != 127 && first < 224;
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(address)
            && !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6Multicast && !address.IsIPv6LinkLocal;
    }

    public static IReadOnlyList<DiscoveredServer> Rows(IEnumerable<(DiscoveredServer Row, int Misses)> entries) => entries
        .OrderBy(entry => entry.Misses).ThenBy(entry => ServerAddress.ToBaseUrl(entry.Row.Server), StringComparer.Ordinal)
        .DistinctBy(entry => entry.Row.ServerId).Select(entry => entry.Row)
        .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => ServerAddress.ToBaseUrl(row.Server), StringComparer.Ordinal).ToArray();
}

public sealed class LanDiscoveryHealth
{
    public DiscoveredServer? Row { get; private set; }
    public int Misses { get; private set; }

    public void Confirm(DiscoveredServer? row)
    {
        if (row is not null) { Row = row; Misses = 0; }
        else if (Row is not null && ++Misses >= 3) Row = null;
    }
}
