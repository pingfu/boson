using System.Net;
using System.Net.Sockets;
using Boson.Github;

namespace Boson.Platform;

/// <summary>
/// Picks the loopback port a project publishes on. The number is host state
/// shared with every other project on the box, so boson owns it: a repo that
/// names one carries a value that is correct on exactly one server, and two
/// repos naming the same one collide on whichever server runs both.
/// </summary>
public static class HostPortAllocator
{
    /// <summary>
    /// Below the 32768 start of Linux's ephemeral range, so an allocation can
    /// never collide with the source port of an outbound connection, and high
    /// enough to stay clear of the ports services conventionally use.
    /// </summary>
    public const int First = 30000;

    public const int Last = 32767;

    /// <summary>
    /// Lowest port in the range that no active project holds and nothing on the
    /// host is listening on. Both checks are needed: the database knows about
    /// boson's projects, the bind probe knows about everything else.
    /// </summary>
    public static int Allocate(IEnumerable<int> claimed, Func<int, bool> isFreeOnHost)
    {
        var taken = claimed.ToHashSet();

        for (var port = First; port <= Last; port++)
            if (!taken.Contains(port) && isFreeOnHost(port))
                return port;

        throw new BosonValidationException(
            $"no free host port between {First} and {Last}");
    }

    public static bool IsFreeOnHost(int port)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            probe.Bind(new IPEndPoint(IPAddress.Loopback, port));

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
