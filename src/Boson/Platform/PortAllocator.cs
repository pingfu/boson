using System.Net;
using System.Net.Sockets;
using Boson.Github;

namespace Boson.Platform;

/// <summary>
/// Picks the loopback port a deployment gets. Docker publishes the container
/// on it and Caddy dials it, and the port the app listens on inside the
/// container never reaches boson. The number is host state shared with every
/// other deployment on the box, so boson owns it: a repository naming one
/// carries a value that is correct on exactly one server, and two repositories
/// naming the same one collide on whichever server runs both.
/// </summary>
public static class PortAllocator
{
    /// <summary>
    /// Below the 32768 start of Linux's ephemeral range, so an allocation can
    /// never collide with the source port of an outbound connection, and high
    /// enough to stay clear of the ports services conventionally use.
    /// </summary>
    public const int First = 30000;

    public const int Last = 32767;

    /// <summary>
    /// Lowest port in the range that no active deployment holds and nothing on
    /// the host is listening on. Both checks are needed: the database knows
    /// about boson's deployments, the bind probe knows about everything else.
    /// </summary>
    public static int Allocate(IEnumerable<int> claimed, Func<int, bool> isFreeOnHost)
    {
        var taken = claimed.ToHashSet();

        for (var port = First; port <= Last; port++)
            if (!taken.Contains(port) && isFreeOnHost(port))
                return port;

        throw new BosonValidationException($"no free port between {First} and {Last}");
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
