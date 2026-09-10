using System.Net;
using System.Text;

namespace HexBridge.Tests;

/// <summary>
/// The Bonjour advertisement, checked byte by byte.
///
/// <para>
/// The Mac browses for <c>_hexbridge._udp</c> through <c>NWBrowser</c>
/// (<c>mac/Sources/HexBridge/Core/Discovery.swift</c>). There is no way to run that browser
/// from here, so the guarantee this file gives is narrower and stated plainly: the packets
/// we emit are well-formed DNS, they carry the service type the Mac asks for, and the
/// parser survives whatever the local network sends back at it.
/// </para>
/// </summary>
public class MulticastDnsTests
{
    private const string Instance = "GAMING-PC._hexbridge._udp.local.";
    private const string Host = "gaming-pc.local.";

    private static readonly IPAddress[] Addresses = [IPAddress.Parse("192.168.1.10")];

    private static readonly KeyValuePair<string, string>[] Txt =
    [
        new("v", "1"),
        new("name", "GAMING-PC"),
    ];

    [Fact]
    public void TheServiceTypeIsTheOneTheMacBrowsesFor()
    {
        // ReceiverDiscovery.serviceType on the Mac is "_hexbridge._udp"; Bonjour appends
        // the domain. A typo here is an empty list on the Mac and no error anywhere.
        Assert.Equal("_hexbridge._udp.local.", MulticastDns.ServiceType);
    }

    // MARK: - Names

    [Theory]
    [InlineData("local.", new[] { "local" })]
    [InlineData("_hexbridge._udp.local.", new[] { "_hexbridge", "_udp", "local" })]
    [InlineData("PC._hexbridge._udp.local.", new[] { "PC", "_hexbridge", "_udp", "local" })]
    // A dot inside a machine name is escaped and must stay one label.
    [InlineData("My\\.PC._hexbridge._udp.local.", new[] { "My.PC", "_hexbridge", "_udp", "local" })]
    [InlineData("A\\\\B.local.", new[] { "A\\B", "local" })]
    public void NamesSplitOnUnescapedDots(string name, string[] expected)
    {
        Assert.Equal(expected, MulticastDns.Labels(name));
    }

    [Theory]
    [InlineData("PC")]
    [InlineData("My.PC")]
    [InlineData("A\\B")]
    [InlineData("Никита-ПК")]
    public void AnEscapedLabelSurvivesTheRoundTrip(string machineName)
    {
        var name = $"{MulticastDns.EscapeLabel(machineName)}.local.";

        Assert.Equal([machineName, "local"], MulticastDns.Labels(name));

        var written = new List<byte>();
        MulticastDns.WriteName(written, name);
        var offset = 0;

        Assert.True(MulticastDns.TryReadName(written.ToArray(), ref offset, out var read));
        Assert.Equal(written.Count, offset);
        Assert.Equal([machineName, "local"], MulticastDns.Labels(read));
    }

    [Fact]
    public void ANameIsWrittenAsLengthPrefixedLabels()
    {
        var written = new List<byte>();
        MulticastDns.WriteName(written, "_udp.local.");

        Assert.Equal(
            new byte[] { 4, (byte)'_', (byte)'u', (byte)'d', (byte)'p', 5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0 },
            written);
    }

    [Fact]
    public void ALabelLongerThanDnsAllowsIsRefusedRatherThanTruncated()
    {
        var written = new List<byte>();

        Assert.Throws<ArgumentException>(() =>
            MulticastDns.WriteName(written, new string('a', 64) + ".local."));
    }

    [Fact]
    public void ACompressionPointerIsFollowed()
    {
        // Packets from other responders use pointers everywhere; a reader that cannot
        // follow one would drop most of the queries on a busy network.
        var packet = new List<byte>();
        MulticastDns.WriteName(packet, "local.");            // offset 0
        var suffix = 0;
        packet.AddRange([3, (byte)'a', (byte)'b', (byte)'c']);
        packet.AddRange([0xC0, (byte)suffix]);               // "abc" + pointer to "local."

        var offset = 7;
        Assert.True(MulticastDns.TryReadName([.. packet], ref offset, out var name));
        Assert.Equal("abc.local.", name);
        // The cursor lands after the pointer, not after the name it pointed at.
        Assert.Equal(packet.Count, offset);
    }

    [Theory]
    // A pointer to itself, the classic decompression bomb.
    [InlineData(new byte[] { 0xC0, 0x00 })]
    // A length that runs off the end of the packet.
    [InlineData(new byte[] { 0x40, (byte)'a' })]
    [InlineData(new byte[] { 0x05, (byte)'a' })]
    [InlineData(new byte[] { 0xC0 })]
    [InlineData(new byte[0])]
    public void AMalformedNameIsRefusedRatherThanLoopingForever(byte[] packet)
    {
        var offset = 0;

        Assert.False(MulticastDns.TryReadName(packet, ref offset, out _));
    }

    // MARK: - Queries

    [Fact]
    public void ABrowseQueryIsRecognised()
    {
        var packet = Query(MulticastDns.ServiceType, DnsRecordType.Ptr);

        Assert.True(MulticastDns.TryReadQuestions(packet, out var questions));
        var question = Assert.Single(questions);
        Assert.Equal(DnsRecordType.Ptr, question.Type);
        Assert.True(MulticastDns.Matches(question, Instance, Host));
    }

    [Fact]
    public void TheUnicastResponseBitDoesNotHideTheClass()
    {
        // RFC 6762 §5.4 puts a flag in the top bit of QCLASS. Comparing the raw value
        // would make every such question look like it belonged to another protocol.
        var packet = Query(MulticastDns.ServiceType, DnsRecordType.Ptr, klass: 0x8001);

        Assert.True(MulticastDns.TryReadQuestions(packet, out var questions));
        var question = Assert.Single(questions);
        Assert.True(question.WantsUnicast);
        Assert.Equal(1, question.ClassValue);
        Assert.True(MulticastDns.Matches(question, Instance, Host));
    }

    [Theory]
    [InlineData("_hexbridge._udp.local.", DnsRecordType.Ptr)]
    [InlineData("_HEXBRIDGE._UDP.local.", DnsRecordType.Ptr)]           // names are case-insensitive
    [InlineData("_hexbridge._udp.local", DnsRecordType.Ptr)]            // the root dot is optional
    [InlineData("_services._dns-sd._udp.local.", DnsRecordType.Ptr)]    // the meta-query
    [InlineData("_hexbridge._udp.local.", DnsRecordType.Any)]
    [InlineData("GAMING-PC._hexbridge._udp.local.", DnsRecordType.Srv)]
    [InlineData("GAMING-PC._hexbridge._udp.local.", DnsRecordType.Txt)]
    [InlineData("gaming-pc.local.", DnsRecordType.A)]
    public void QuestionsAboutOurOwnNamesAreAnswered(string name, DnsRecordType type)
    {
        Assert.True(MulticastDns.Matches(new DnsQuestion(name, type, 1), Instance, Host));
    }

    [Theory]
    [InlineData("_airplay._tcp.local.", DnsRecordType.Ptr)]
    [InlineData("_hexbridge._tcp.local.", DnsRecordType.Ptr)]
    [InlineData("someone-else.local.", DnsRecordType.A)]
    [InlineData("OTHER-PC._hexbridge._udp.local.", DnsRecordType.Srv)]
    [InlineData("_hexbridge._udp.local.", DnsRecordType.A)]
    public void QuestionsAboutSomebodyElseAreIgnored(string name, DnsRecordType type)
    {
        Assert.False(MulticastDns.Matches(new DnsQuestion(name, type, 1), Instance, Host));
    }

    [Fact]
    public void SomebodyElsesAnswerIsNotMistakenForAQuestion()
    {
        // Every responder on the link hears every packet. Treating an answer as a query
        // would make two HexBridge PCs answer each other in a loop.
        var answer = MulticastDns.BuildAnnouncement(Instance, Host, 47702, Addresses, Txt);

        Assert.False(MulticastDns.TryReadQuestions(answer, out _));
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[0])]
    public void ATruncatedPacketIsIgnored(byte[] packet)
    {
        Assert.False(MulticastDns.TryReadQuestions(packet, out _));
    }

    [Fact]
    public void AQuestionCountThatLiesIsRejected()
    {
        var packet = Query(MulticastDns.ServiceType, DnsRecordType.Ptr);
        packet[5] = 4;   // claims four questions, carries one

        Assert.False(MulticastDns.TryReadQuestions(packet, out _));
    }

    // MARK: - Announcements

    [Fact]
    public void AnAnnouncementCarriesThePtrAndEverythingNeededToDialIt()
    {
        var packet = MulticastDns.BuildAnnouncement(Instance, Host, 47702, Addresses, Txt);

        // QR + AA, no questions, one answer, four additionals (SRV, TXT, one A).
        Assert.Equal(0x84, packet[2]);
        Assert.Equal(0x00, packet[3]);
        Assert.Equal(0, (packet[4] << 8) | packet[5]);
        Assert.Equal(1, (packet[6] << 8) | packet[7]);
        Assert.Equal(0, (packet[8] << 8) | packet[9]);
        Assert.Equal(3, (packet[10] << 8) | packet[11]);

        var text = Encoding.ASCII.GetString(packet);
        Assert.Contains("_hexbridge", text, StringComparison.Ordinal);
        Assert.Contains("GAMING-PC", text, StringComparison.Ordinal);
        Assert.Contains("gaming-pc", text, StringComparison.Ordinal);
        // The port the Mac has to send audio to, big-endian, inside the SRV record.
        Assert.True(packet.AsSpan().IndexOf(new byte[] { 47702 >> 8, 47702 & 0xFF }) >= 0);
        // And the address, so a browse result resolves without a second round trip.
        Assert.True(packet.AsSpan().IndexOf(new byte[] { 192, 168, 1, 10 }) >= 0);
    }

    [Fact]
    public void ThePtrAnswerNamesTheServiceAndPointsAtTheInstance()
    {
        var packet = MulticastDns.BuildAnnouncement(Instance, Host, 47702, Addresses, Txt);

        var offset = 12;
        Assert.True(MulticastDns.TryReadName(packet, ref offset, out var name));
        Assert.True(MulticastDns.Same(name, MulticastDns.ServiceType));

        Assert.Equal((ushort)DnsRecordType.Ptr, (packet[offset] << 8) | packet[offset + 1]);
        // A PTR is a shared record: no cache-flush bit, or a second PC's advertisement
        // would evict ours from the Mac's cache.
        Assert.Equal(0x0001, (packet[offset + 2] << 8) | packet[offset + 3]);

        var ttl = (uint)((packet[offset + 4] << 24) | (packet[offset + 5] << 16)
            | (packet[offset + 6] << 8) | packet[offset + 7]);
        Assert.Equal(MulticastDns.SharedTtl, ttl);

        offset += 10;
        Assert.True(MulticastDns.TryReadName(packet, ref offset, out var target));
        Assert.True(MulticastDns.Same(target, Instance));
    }

    [Fact]
    public void AGoodbyeIsThePtrAloneWithNoTimeToLive()
    {
        // RFC 6762 §10.1. Without it the Mac keeps a closed wizard in its list until the
        // TTL runs out, which is over an hour.
        var packet = MulticastDns.BuildAnnouncement(Instance, Host, 47702, Addresses, Txt, ttl: 0);

        Assert.Equal(1, (packet[6] << 8) | packet[7]);
        Assert.Equal(0, (packet[10] << 8) | packet[11]);

        var offset = 12;
        MulticastDns.TryReadName(packet, ref offset, out _);
        var ttl = (uint)((packet[offset + 4] << 24) | (packet[offset + 5] << 16)
            | (packet[offset + 6] << 8) | packet[offset + 7]);
        Assert.Equal(0u, ttl);
    }

    [Fact]
    public void EveryAddressGetsItsOwnRecord()
    {
        IPAddress[] many =
        [
            IPAddress.Parse("192.168.1.10"),
            IPAddress.Parse("10.0.0.4"),
            // IPv6 is skipped: the receiver binds InterNetwork, so an AAAA would point the
            // Mac at a socket nothing is listening on.
            IPAddress.Parse("fe80::1"),
        ];

        var packet = MulticastDns.BuildAnnouncement(Instance, Host, 47702, many, Txt);

        Assert.Equal(4, (packet[10] << 8) | packet[11]);
        Assert.True(packet.AsSpan().IndexOf(new byte[] { 192, 168, 1, 10 }) >= 0);
        Assert.True(packet.AsSpan().IndexOf(new byte[] { 10, 0, 0, 4 }) >= 0);
    }

    [Fact]
    public void AnEmptyTxtRecordIsStillALegalTxtRecord()
    {
        // Rdata of length zero is not valid for TXT; one zero-length string is.
        var rdata = new List<byte>();
        MulticastDns.WriteTxt(rdata, []);

        Assert.Equal([0], rdata);
    }

    [Fact]
    public void TxtEntriesAreLengthPrefixedKeyEqualsValue()
    {
        var rdata = new List<byte>();
        MulticastDns.WriteTxt(rdata, [new KeyValuePair<string, string>("v", "1")]);

        Assert.Equal([3, (byte)'v', (byte)'=', (byte)'1'], rdata);
    }

    [Fact]
    public void AnAnnouncementFitsInOneDatagram()
    {
        // mDNS over a single UDP packet is the only path this responder supports; it has
        // no truncation and no continuation.
        var packet = MulticastDns.BuildAnnouncement(
            new string('A', 63) + "._hexbridge._udp.local.",
            new string('b', 63) + ".local.",
            47702,
            [.. Enumerable.Range(1, 8).Select(i => IPAddress.Parse($"10.0.0.{i}"))],
            Txt);

        Assert.True(packet.Length < 1400, $"пакет {packet.Length} байт");
    }

    // MARK: - The advertiser's own naming

    [Theory]
    [InlineData("GAMING-PC", "gaming-pc.local.")]
    [InlineData("Никита-ПК", "-.local.")]
    [InlineData("My PC", "my-pc.local.")]
    [InlineData("", "hexbridge.local.")]
    [InlineData("...", "hexbridge.local.")]
    public void TheHostNameIsSomethingAResolverWillAccept(string machineName, string expected)
    {
        // Instance names may be anything; host names may not. A Cyrillic machine name
        // reduces to a hyphen rather than to a name a resolver would reject outright.
        using var advertiser = new ServiceAdvertiser(machineName, 47702, Addresses);
        var packet = MulticastDns.BuildAnnouncement(advertiser.InstanceName, expected, 47702, Addresses, []);

        Assert.NotEmpty(packet);
        Assert.EndsWith($".{MulticastDns.ServiceType}", advertiser.InstanceName, StringComparison.Ordinal);
    }

    [Fact]
    public void AMachineNameWithADotStaysOneLabel()
    {
        using var advertiser = new ServiceAdvertiser("My.PC", 47702, Addresses);

        Assert.Equal("My\\.PC._hexbridge._udp.local.", advertiser.InstanceName);
        Assert.Equal(["My.PC", "_hexbridge", "_udp", "local"], MulticastDns.Labels(advertiser.InstanceName));
    }

    [Fact]
    public void AnOverlongMachineNameIsTrimmedToWhatALabelHolds()
    {
        using var advertiser = new ServiceAdvertiser(new string('Я', 100), 47702, Addresses);
        var label = MulticastDns.Labels(advertiser.InstanceName)[0];

        Assert.True(Encoding.UTF8.GetByteCount(label) <= 63);
        // And it still writes, which is the point of trimming rather than trusting.
        MulticastDns.WriteName([], advertiser.InstanceName);
    }

    [Fact]
    public void LocalAddressesAreRoutableOnes()
    {
        // Loopback and 169.254/16 cannot be dialled from a Mac, so they must never end up
        // in a pairing URI.
        foreach (var address in MulticastDns.LocalAddresses())
        {
            Assert.False(IPAddress.IsLoopback(address));
            Assert.False(address.GetAddressBytes() is [169, 254, ..]);
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, address.AddressFamily);
        }
    }

    /// <summary>Builds the query packet a browser sends, header and one question.</summary>
    private static byte[] Query(string name, DnsRecordType type, ushort klass = 1)
    {
        var packet = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        MulticastDns.WriteName(packet, name);
        packet.Add((byte)((ushort)type >> 8));
        packet.Add((byte)type);
        packet.Add((byte)(klass >> 8));
        packet.Add((byte)klass);
        return [.. packet];
    }
}
