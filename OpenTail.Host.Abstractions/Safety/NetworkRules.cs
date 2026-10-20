using System.Net;
using System.Net.Sockets;

namespace OpenTail.Host.Abstractions;

/// <summary>Address and host-pattern checks shared by the Host's guarded HTTP clients and modules that fetch on their own.</summary>
public static class NetworkRules
{
    /// <summary>True only for addresses on the public internet.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10
                || b[0] == 127
                || b[0] == 0
                || (b[0] == 100 && b[1] is >= 64 and <= 127)            // carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                          // link-local, cloud metadata
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && b[1] is 18 or 19)                     // benchmarking
                || b[0] >= 224);                                         // multicast, reserved, broadcast
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal || ip.IsIPv6Teredo) return false;
            var b = ip.GetAddressBytes();
            if (b[0] == 0x20 && b[1] == 0x02) return false;              // 6to4 can embed private IPv4
            return true;
        }
        return false;
    }

    /// <summary>
    /// Does <paramref name="uri"/> match a host pattern? Patterns: <c>api.example.com</c> (https, default port), <c>*.example.com</c>
    /// (https, any subdomain but not the apex), or an origin <c>http://127.0.0.1:5123</c> / <c>https://ghe.corp:8443</c> (exact scheme, host and port).
    /// </summary>
    public static bool Matches(string pattern, Uri uri)
    {
        if (string.IsNullOrWhiteSpace(pattern) || !uri.IsAbsoluteUri) return false;
        if (pattern.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(pattern, UriKind.Absolute, out var origin)) return false;
            return origin.Scheme == uri.Scheme && origin.IdnHost.Equals(uri.IdnHost, StringComparison.OrdinalIgnoreCase) && origin.Port == uri.Port;
        }
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return false;
        var host = uri.IdnHost;
        return pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.Length > pattern.Length - 1 && host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : host.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when every URI <paramref name="inner"/> would match is also matched by <paramref name="outer"/>.</summary>
    public static bool Covers(string outer, string inner)
    {
        if (outer.Equals(inner, StringComparison.OrdinalIgnoreCase)) return true;
        if (inner.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(inner, UriKind.Absolute, out var u) && Matches(outer, u);
        if (outer.Contains("://", StringComparison.Ordinal)) return false;
        if (!outer.StartsWith("*.", StringComparison.Ordinal)) return false;
        var suffix = outer[1..];                                                   // ".example.com"
        var name = inner.StartsWith("*.", StringComparison.Ordinal) ? inner[1..] : inner; // ".a.example.com" or "a.example.com"
        return name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}
