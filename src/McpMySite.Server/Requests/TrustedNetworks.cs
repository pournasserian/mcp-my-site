using System.Net;
using System.Net.Sockets;

namespace McpMySite.Server.Requests;

/// <summary>
/// A set of IPs and CIDR ranges a connection may come from before a client-address header is believed. Shared by
/// <see cref="CloudflareClientIpMiddleware"/> and <see cref="ClientIpResolver"/> so both trust checks and both
/// header parsers behave the same.
/// </summary>
public sealed class TrustedNetworks
{
    private readonly System.Net.IPNetwork[] _networks;

    private TrustedNetworks(System.Net.IPNetwork[] networks)
    {
        _networks = networks;
    }

    /// <summary>
    /// Parses every entry up front, so a mistyped one is found when the setting is read rather than the day it is
    /// turned on. <paramref name="settingName"/> names the setting in the error.
    /// </summary>
    public static TrustedNetworks Parse(IEnumerable<string> entries, string settingName) =>
        new(entries.Select(entry => ParseEntry(entry, settingName)).ToArray());

    public bool Contains(IPAddress peer)
    {
        var address = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        return _networks.Any(network => network.Contains(address));
    }

    private static System.Net.IPNetwork ParseEntry(string entry, string settingName)
    {
        if (System.Net.IPNetwork.TryParse(entry, out var network))
        {
            return network;
        }

        if (TryParseAddress(entry, out var address))
        {
            return new System.Net.IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        }

        throw new InvalidOperationException($"{settingName} entry '{entry}' is not an IP address or CIDR range such as 173.245.48.0/20.");
    }

    // IPAddress.TryParse also accepts "1" and "1.2" as shorthand; a client address from a proxy is always written in full.
    public static bool TryParseAddress(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value.Trim(), out var parsed))
        {
            return false;
        }

        var complete = parsed.AddressFamily switch
        {
            AddressFamily.InterNetwork => value.Count(c => c == '.') == 3,
            AddressFamily.InterNetworkV6 => value.Contains(':'),
            _ => false,
        };
        if (complete)
        {
            address = parsed;
        }

        return complete;
    }
}
