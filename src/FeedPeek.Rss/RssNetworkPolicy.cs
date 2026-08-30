using System.Net;
using System.Net.Sockets;

namespace FeedPeek.Rss;

internal static class RssNetworkPolicy
{
    public static async Task ValidateAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("RSS URLs must use HTTP or HTTPS.");
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Local network RSS addresses are not allowed.");

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
        }
        catch (SocketException ex)
        {
            throw new HttpRequestException("The RSS host could not be resolved.", ex);
        }

        if (addresses.Length == 0 || addresses.Any(IsPrivateOrLocal))
            throw new InvalidOperationException("Local or private network RSS addresses are not allowed.");
    }

    private static bool IsPrivateOrLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }

        var first = address.GetAddressBytes()[0];
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
            || address.Equals(IPAddress.IPv6Loopback) || first is 0xfc or 0xfd;
    }
}
