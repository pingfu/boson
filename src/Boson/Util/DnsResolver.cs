using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Boson.Util;

public interface IDnsResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostname, CancellationToken ct = default);
    bool AnyMatchesLocalInterface(IReadOnlyList<IPAddress> addresses);
}

public sealed class DnsResolver : IDnsResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostname, CancellationToken ct = default)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(hostname, ct);
        }
        catch (SocketException)
        {
            return [];
        }
    }

    public bool AnyMatchesLocalInterface(IReadOnlyList<IPAddress> addresses)
    {
        try
        {
            var local = NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .ToHashSet();
                
            return addresses.Any(local.Contains);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }
}
