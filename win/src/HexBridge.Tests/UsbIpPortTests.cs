using System.Net;
using System.Net.Sockets;

using HexBridge.Devices;

namespace HexBridge.Tests;

/// <summary>
/// Getting out of the way of whoever already holds the USB/IP port.
///
/// <para>
/// usbip-win2 installs a <c>usbipd</c> service that listens on 3240 — the port this
/// server wants and the one the whole USB/IP world defaults to. So installing the
/// driver broke the feature the driver is for, and the message was "an attempt was made
/// to access a socket in a way forbidden by its access permissions", which is Windows
/// for "taken" and is not something a person can act on. It happened on a real machine
/// the moment the driver went in.
/// </para>
///
/// <para>
/// Nothing needs the standard port: both ends are ours, on loopback, and the client is
/// told where to look. These pin that the feature starts anyway and that the client is
/// told the truth about where.
/// </para>
/// </summary>
public class UsbIpPortTests
{
    private static TcpListener Squat(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return listener;
    }

    /// <summary>The five arguments a feature is handed; only the config and the log matter here.</summary>
    private static FeatureContext Context(int port) => new(
        new ReceiverConfig { Gamepad = true, UsbIpListen = $"127.0.0.1:{port}" },
        (_, _) => { },
        (_, _, _) => { },
        () => false,
        _ => { });

    /// <summary>Where the server actually ended up, as the feature reports it.</summary>
    private static IPEndPoint Bound(DevicesFeature feature)
    {
        var state = Assert.IsType<DevicesState>(feature.CaptureState());
        return IPEndPoint.Parse(state.ServerListen);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public void TheFeatureStartsEvenWhenItsPortIsHeld()
    {
        var wanted = FreePort();
        using var squatter = Squat(wanted);

        var feature = new DevicesFeature();
        feature.Start(Context(wanted));
        try
        {
            var listening = Bound(feature);

            Assert.NotEqual(wanted, listening.Port);
            Assert.InRange(listening.Port, wanted + 1, wanted + 16);
        }
        finally
        {
            feature.StopAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// A run of held ports is stepped over rather than giving up on the first one, because
    /// a machine with several of these installed is exactly where this goes wrong.
    /// </summary>
    [Fact]
    public void ARunOfHeldPortsIsSteppedOver()
    {
        var wanted = FreePort();
        var squatters = new List<TcpListener>();
        try
        {
            for (var offset = 0; offset < 3; offset++)
            {
                try { squatters.Add(Squat(wanted + offset)); }
                catch (SocketException) { /* someone else's; the gap is still a gap */ }
            }

            var feature = new DevicesFeature();
            feature.Start(Context(wanted));
            try
            {
                var listening = Bound(feature);

                Assert.DoesNotContain(listening.Port, squatters.Select(s => ((IPEndPoint)s.LocalEndpoint).Port));
            }
            finally
            {
                feature.StopAsync().GetAwaiter().GetResult();
            }
        }
        finally
        {
            foreach (var squatter in squatters) squatter.Stop();
        }
    }

    [Fact]
    public void AFreePortIsTakenAsAsked()
    {
        var wanted = FreePort();

        var feature = new DevicesFeature();
        feature.Start(Context(wanted));
        try
        {
            Assert.Equal(wanted, Bound(feature).Port);
        }
        finally
        {
            feature.StopAsync().GetAwaiter().GetResult();
        }
    }
}
