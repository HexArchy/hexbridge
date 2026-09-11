using System.Buffers.Binary;
using System.Net;
using System.Text;

using HexBridge;

namespace HexBridge.Tests;

/// <summary>
/// Reading the relay's introduction — the one packet on the wire whose payload is not
/// encrypted, because the relay that sends it has no key.
///
/// <para>
/// It is believed about nothing. All it does is aim a keepalive, so that this machine's
/// mapping is open when the other end tries a direct path. Everything that decides where
/// data goes still runs on packets that decrypted. These tests are therefore mostly about
/// what must be <em>rejected</em>: the parser reads an address out of an unauthenticated
/// packet, and it is reachable from anywhere on the internet.
/// </para>
///
/// <para>The relay's half is <c>relay/introduce_test.go</c>, against the same layout.</para>
/// </summary>
public class IntroductionTests
{
    private const ulong Room = 0x1122334455667788;

    private static byte[] Introduction(IPAddress address, ushort port, ulong room = Room,
                                       byte version = 1, byte type = (byte)PacketType.Peer,
                                       string magic = "MBG1")
    {
        var raw = address.GetAddressBytes();
        var packet = new byte[Wire.HeaderSize + 1 + raw.Length + 2];

        Encoding.ASCII.GetBytes(magic).CopyTo(packet, 0);
        packet[4] = version;
        packet[5] = type;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), room);

        packet[Wire.HeaderSize] = (byte)(raw.Length == 4 ? 4 : 6);
        raw.CopyTo(packet, Wire.HeaderSize + 1);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(Wire.HeaderSize + 1 + raw.Length), port);

        return packet;
    }

    [Fact]
    public void AnIntroductionYieldsTheAddress()
    {
        var packet = Introduction(IPAddress.Parse("203.0.113.7"), 47702);

        var named = Wire.PeerIntroduction(packet, Room);

        Assert.NotNull(named);
        Assert.Equal(IPAddress.Parse("203.0.113.7"), named.Address);
        Assert.Equal(47702, named.Port);
    }

    [Fact]
    public void AnIPv6IntroductionIsUnderstood()
    {
        var packet = Introduction(IPAddress.Parse("2001:db8::1"), 47702);

        var named = Wire.PeerIntroduction(packet, Room);

        Assert.NotNull(named);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), named.Address);
    }

    /// <summary>Somebody else's relay, or somebody else's room, is not our business.</summary>
    [Fact]
    public void AnotherRoomIsNotOurIntroduction() =>
        Assert.Null(Wire.PeerIntroduction(Introduction(IPAddress.Parse("203.0.113.7"), 47702, room: 99), Room));

    [Fact]
    public void OnlyThisTypeIsAnIntroduction() =>
        Assert.Null(Wire.PeerIntroduction(
            Introduction(IPAddress.Parse("203.0.113.7"), 47702, type: (byte)PacketType.Audio), Room));

    [Fact]
    public void AWrongMagicIsNotOurs() =>
        Assert.Null(Wire.PeerIntroduction(
            Introduction(IPAddress.Parse("203.0.113.7"), 47702, magic: "XXXX"), Room));

    [Fact]
    public void AFutureVersionIsNotGuessedAt() =>
        Assert.Null(Wire.PeerIntroduction(
            Introduction(IPAddress.Parse("203.0.113.7"), 47702, version: 2), Room));

    [Fact]
    public void PortZeroNamesNothing() =>
        Assert.Null(Wire.PeerIntroduction(Introduction(IPAddress.Parse("203.0.113.7"), 0), Room));

    /// <summary>
    /// The length fields come off the wire unauthenticated, so every one of these is a
    /// packet somebody could send from anywhere.
    /// </summary>
    [Fact]
    public void TruncatedAndMalformedPacketsAreRefusedRatherThanRead()
    {
        var whole = Introduction(IPAddress.Parse("203.0.113.7"), 47702);

        for (var length = 0; length < whole.Length; length++)
        {
            Assert.Null(Wire.PeerIntroduction(whole.AsSpan(0, length), Room));
        }

        // A family byte that is neither 4 nor 6, with a body long enough to tempt a reader.
        var bogus = (byte[])whole.Clone();
        bogus[Wire.HeaderSize] = 9;
        Assert.Null(Wire.PeerIntroduction(bogus, Room));

        // Says IPv6, carries an IPv4 body.
        var lying = (byte[])whole.Clone();
        lying[Wire.HeaderSize] = 6;
        Assert.Null(Wire.PeerIntroduction(lying, Room));
    }
}
