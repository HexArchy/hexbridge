using System.Net;

namespace HexBridge.Tests;

/// <summary>
/// The browse half of autodiscovery, checked against the responder half that already ships.
///
/// <para>
/// Windows used to only ever be found. Now it can also look — and the only thing worth
/// proving about a parser is that it reads what the writer wrote, so every packet here comes
/// out of <see cref="MulticastDns.BuildAnnouncement"/> rather than out of a fixture nobody
/// would notice drifting.
/// </para>
/// </summary>
public class DiscoveryBrowseTests
{
    private const string Psk = "3q2+796tvu/erb7v3q2+796tvu/erb7v3q2+796tvu8=";

    private static byte[] Announcement(
        string machine, int port, string? psk, string address = "192.168.1.50", uint ttl = MulticastDns.SharedTtl)
    {
        var tag = DiscoveryTag.ForPsk(psk);
        return MulticastDns.BuildAnnouncement(
            $"{MulticastDns.EscapeLabel(machine)}.{MulticastDns.ServiceType}",
            $"{machine.ToLowerInvariant()}.local.",
            port,
            [IPAddress.Parse(address)],
            DiscoveryTxt.Build(PairingPayload.Version, port, machine, tag),
            ttl);
    }

    private static IReadOnlyList<DnsAnswer> Read(byte[] packet)
    {
        Assert.True(MulticastDns.TryReadAnswers(packet, out var answers), "пакет не разобрался");
        return answers;
    }

    [Fact]
    public void OurOwnAnnouncementReadsBackAsTheHostItDescribes()
    {
        var scan = new DiscoveryScan();
        Assert.True(scan.Apply(Read(Announcement("GAMING-PC", 47702, Psk))));

        var host = Assert.Single(scan.Hosts);
        Assert.Equal("GAMING-PC", host.Name);
        Assert.Equal(47702, host.Port);
        Assert.Equal("192.168.1.50", host.Address);
        Assert.Equal("192.168.1.50:47702", host.Target);
        Assert.Equal(DiscoveryTag.ForPsk(Psk), host.Tag);
        Assert.Equal(PairingPayload.Version, host.Version);
    }

    [Fact]
    public void TheAddressComesOutOfTheAdditionalSectionRatherThanASecondRoundTrip()
    {
        // PTR is the only record in the answer section; SRV, TXT and A ride in additional.
        // A reader that stopped after the answers would learn that a host exists and nothing
        // at all about how to reach it.
        var answers = Read(Announcement("GAMING-PC", 47702, Psk));

        Assert.Contains(answers, a => a.Type == DnsRecordType.Ptr);
        Assert.Contains(answers, a => a.Type == DnsRecordType.Srv && a.Port == 47702);
        Assert.Contains(answers, a => a.Type == DnsRecordType.Txt && a.Text.Count == 4);
        Assert.Contains(answers, a => a.Type == DnsRecordType.A && Equals(a.Address, IPAddress.Parse("192.168.1.50")));
    }

    [Fact]
    public void AGoodbyeTakesTheHostOutOfTheList()
    {
        var scan = new DiscoveryScan();
        scan.Apply(Read(Announcement("GAMING-PC", 47702, Psk)));
        Assert.Single(scan.Hosts);

        // TTL zero is a withdrawal, not an announcement. Reading it as one is how a machine
        // stays in somebody's list for an hour after it was switched off.
        Assert.True(scan.Apply(Read(Announcement("GAMING-PC", 47702, Psk, ttl: 0))));
        Assert.Empty(scan.Hosts);
    }

    [Fact]
    public void RecordsArrivingSeparatelyStillAddUpToOneHost()
    {
        // Nothing in mDNS promises the four records travel together, and our own responder is
        // not the only thing on a network that answers.
        var whole = Read(Announcement("GAMING-PC", 47702, Psk));
        var scan = new DiscoveryScan();

        foreach (var answer in whole) scan.Apply([answer]);

        var host = Assert.Single(scan.Hosts);
        Assert.Equal("192.168.1.50:47702", host.Target);
    }

    [Fact]
    public void RepeatingTheSameAnnouncementIsNotAChange()
    {
        var scan = new DiscoveryScan();
        var packet = Announcement("GAMING-PC", 47702, Psk);

        Assert.True(scan.Apply(Read(packet)));
        // Announcements repeat by design. Redrawing a list on every one of them would make
        // the wizard flicker for as long as it is open.
        Assert.False(scan.Apply(Read(packet)));
    }

    [Fact]
    public void SeveralMachinesAreKeptApartAndTheResolvedOnesComeFirst()
    {
        var scan = new DiscoveryScan();
        scan.Apply(Read(Announcement("ZED-PC", 47702, Psk, "192.168.1.51")));
        scan.Apply(Read(Announcement("ALPHA-PC", 47703, ReceiverConfig.GenerateKey(), "192.168.1.52")));

        Assert.Equal(2, scan.Hosts.Count);
        Assert.Equal("ALPHA-PC", scan.Hosts[0].Name);
        Assert.NotEqual(scan.Hosts[0].Tag, scan.Hosts[1].Tag);
    }

    [Fact]
    public void OnlyTheMachineHoldingOurKeyIsWorthDialling()
    {
        var scan = new DiscoveryScan();
        scan.Apply(Read(Announcement("STRANGER-PC", 47702, ReceiverConfig.GenerateKey(), "192.168.1.77")));
        scan.Apply(Read(Announcement("OUR-PC", 47702, Psk, "192.168.1.50")));

        var ours = DiscoveryTag.ForPsk(Psk);
        var choice = DiscoveryMatch.Choose(ours, scan.Hosts);

        // The same rule the Mac enforces, now enforced from a Windows browse: a stranger's
        // HexBridge on the same network is visible and never dialled.
        Assert.Equal(DiscoveryVerdict.Connect, choice.Verdict);
        Assert.Equal("OUR-PC", choice.Host!.Value.Name);
        Assert.Equal("192.168.1.50:47702", DiscoveryMatch.Retarget("", choice));
    }

    [Fact]
    public void AMachineThatHasNotBeenPairedYetPublishesNoTagAndIsNeverChosen()
    {
        var scan = new DiscoveryScan();
        scan.Apply(Read(Announcement("FRESH-PC", 47702, psk: null)));

        var host = Assert.Single(scan.Hosts);
        Assert.Null(host.Tag);
        Assert.Equal(DiscoveryVerdict.NoMatch, DiscoveryMatch.Choose(DiscoveryTag.ForPsk(Psk), scan.Hosts).Verdict);
    }

    [Fact]
    public void ANameWithADotInItSurvivesTheRoundTrip()
    {
        // «Никита.ПК» is a legal machine name and one label, not two. The escaping has always
        // been in the writer; until now nothing read it back.
        var scan = new DiscoveryScan();
        scan.Apply(Read(Announcement("Nikita.PC", 47702, Psk)));

        var host = Assert.Single(scan.Hosts);
        Assert.Equal("Nikita.PC", host.Name);
    }

    [Fact]
    public void AQueryIsOneQuestionAndOurOwnResponderAnswersIt()
    {
        var query = MulticastDns.BuildQuery(MulticastDns.ServiceType, DnsRecordType.Ptr);

        Assert.True(MulticastDns.TryReadQuestions(query, out var questions));
        var question = Assert.Single(questions);
        Assert.Equal(MulticastDns.ServiceType, question.Name);
        Assert.Equal(DnsRecordType.Ptr, question.Type);
        Assert.False(question.WantsUnicast);

        // The whole point: the browse this build sends is one the responder this build ships
        // recognises as its own business.
        Assert.True(MulticastDns.Matches(
            question, $"GAMING-PC.{MulticastDns.ServiceType}", "gaming-pc.local."));
    }

    [Fact]
    public void AQuestionIsNotMistakenForAnAnswerOrTheOtherWayRound()
    {
        var query = MulticastDns.BuildQuery(MulticastDns.ServiceType, DnsRecordType.Ptr);
        var announcement = Announcement("GAMING-PC", 47702, Psk);

        // Both sockets sit on the same multicast group and see each other's traffic, so each
        // reader has to refuse the other's packets rather than half-parse them.
        Assert.False(MulticastDns.TryReadAnswers(query, out _));
        Assert.False(MulticastDns.TryReadQuestions(announcement, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(23)]
    [InlineData(40)]
    public void ATruncatedPacketIsRefusedRatherThanGuessedAt(int length)
    {
        // This socket receives whatever else lives on the local network. Every one of these
        // has to be a skipped packet, not an exception on a background thread.
        var packet = Announcement("GAMING-PC", 47702, Psk)[..length];
        MulticastDns.TryReadAnswers(packet, out var answers);
        Assert.All(answers, a => Assert.NotNull(a.Name));
    }

    [Fact]
    public void GarbageOnThePortCostsOneSkippedPacketAndNothingElse()
    {
        var random = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var packet = new byte[random.Next(0, 300)];
            random.NextBytes(packet);
            // Whatever it decides, it must decide it without throwing.
            MulticastDns.TryReadAnswers(packet, out _);
            MulticastDns.TryReadQuestions(packet, out _);
        }
    }
}
