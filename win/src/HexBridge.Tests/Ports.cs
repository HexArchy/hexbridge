using System.Net;
using System.Net.Sockets;

namespace HexBridge.Tests;

/// <summary>
/// Picking a port a test can actually use.
///
/// <para>
/// Four test classes had grown the same helper: bind a TCP listener to port 0, note what
/// the OS handed out, close it, and give the number to whatever was being tested. Two
/// things are wrong with that, and both of them only ever bite on the Windows runner.
/// </para>
///
/// <para>
/// A port that is free for TCP says nothing about UDP, and most of these callers hand the
/// number to something that binds UDP. And Windows keeps ranges of ports reserved — for
/// Hyper-V, for WinNAT — where a bind is refused outright with "an attempt was made to
/// access a socket in a way forbidden by its access permissions", which is not "in use"
/// and does not mean what it sounds like. The ephemeral range the probe draws from
/// overlaps them.
/// </para>
///
/// <para>
/// So this checks the candidate against every protocol the caller is going to need, and
/// moves on to another one when Windows objects. The race with another process is still
/// there — it cannot be closed without holding the socket — but it is now a race rather
/// than a coin toss, and three CI runs in a row died on the old coin.
/// </para>
/// </summary>
internal static class Ports
{
    /// <summary>How many candidates to try before giving up and letting the test fail honestly.</summary>
    private const int Attempts = 20;

    /// <summary>A port that binds for both protocols right now. The usual case here.</summary>
    public static int Free() => Free(tcp: true, udp: true);

    public static int Free(bool tcp, bool udp)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var candidate = Candidate();
            if (candidate == 0) continue;
            if (tcp && !CanBindTcp(candidate)) continue;
            if (udp && !CanBindUdp(candidate)) continue;

            return candidate;
        }

        throw new InvalidOperationException(
            $"no port bindable for {(tcp ? "tcp" : "")}{(tcp && udp ? "+" : "")}{(udp ? "udp" : "")} after {Attempts} tries");
    }

    private static int Candidate()
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
        catch (SocketException)
        {
            return 0;
        }
    }

    private static bool CanBindTcp(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool CanBindUdp(int port)
    {
        try
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
