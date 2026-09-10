using System.Net;
using System.Text;

namespace HexBridge.Tests;

/// <summary>
/// Autodiscovery, and mostly the one rule it exists for: a Mac dials a host it found by
/// itself <b>only</b> when that host's tag equals the tag of the Mac's own key
/// (PROTOCOL.md, «Автопоиск хоста»).
///
/// <para>
/// This is a security boundary, not a convenience. In a dormitory or a co-working space
/// there will be another HexBridge on the same subnet, its owner will have called the PC
/// something ordinary, and walking into their session must be impossible — not unlikely,
/// impossible. Every negative case below is a way that could happen, written down so it
/// cannot come back.
/// </para>
///
/// <para>
/// The vectors are shared with the Mac word for word: the same key produces the same
/// twenty-two characters in <c>mac/Tests/HexBridgeDiscoveryTests</c>. Two implementations
/// that agree with a constant agree with each other.
/// </para>
/// </summary>
public class DiscoveryTests
{
    /// <summary>Bytes 0…31. Nothing about it is secret; that is the point of a vector.</summary>
    private const string OurPsk = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string OurTag = "QftJm4C_xaQadblqBrCUpA";

    /// <summary>Somebody else's key, and the tag their PC therefore advertises.</summary>
    private const string TheirPsk = "AwoRGB8mLTQ7QklQV15lbHN6gYiPlp2kq7K5wMfO1dw=";
    private const string TheirTag = "wVDaMu3kwZo6E2yawqf5WQ";

    private static DiscoveredHost Host(string name, string address, string? tag, int port = 47702) => new()
    {
        Name = name,
        Address = address,
        Port = port,
        Tag = tag,
        Version = 1,
    };

    // MARK: - The tag itself

    [Fact]
    public void TheTagIsTheDocumentedHashOfTheKey()
    {
        // tag = base64url(SHA256("hexbridge-discovery-v1" || PSK)[0..16]).
        // A drift here is not a bug that shows up as an error anywhere: it is a Mac that
        // silently stops recognising its own PC.
        Assert.Equal(OurTag, DiscoveryTag.ForPsk(OurPsk));
        Assert.Equal(TheirTag, DiscoveryTag.ForPsk(TheirPsk));
    }

    [Fact]
    public void ATagIsTwentyTwoCharactersOfBase64Url()
    {
        var tag = DiscoveryTag.ForPsk(OurPsk)!;

        Assert.Equal(DiscoveryTag.Length, tag.Length);
        Assert.DoesNotContain('=', tag);
        Assert.DoesNotContain('+', tag);
        Assert.DoesNotContain('/', tag);
        Assert.True(DiscoveryTag.IsWellFormed(tag));
    }

    [Fact]
    public void TheTagIsNotTheFingerprintAndNotTheRoom()
    {
        // Three values are derived from the same key for three different audiences. Sharing
        // a domain string between any two of them would let one of them be computed from
        // another — the room id travels in every packet header, in clear.
        var key = Convert.FromBase64String(OurPsk);
        var tag = DiscoveryTag.For(key);

        Assert.NotEqual(PairingPayload.FingerprintOf(key).Replace(" · ", ""), tag);
        Assert.DoesNotContain(Wire.RoomId(key).ToString("X16"), tag, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("hexbridge-discovery-v1", DiscoveryTag.Domain);
    }

    [Fact]
    public void EveryKeyGetsItsOwnTag()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 200; i++)
        {
            Assert.True(seen.Add(DiscoveryTag.ForPsk(ReceiverConfig.GenerateKey())!));
        }
    }

    [Fact]
    public void OneBitOfKeyIsAWholeDifferentTag()
    {
        var key = Convert.FromBase64String(OurPsk);
        var nudged = (byte[])key.Clone();
        nudged[31] ^= 0x01;

        Assert.NotEqual(DiscoveryTag.For(key), DiscoveryTag.For(nudged));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не base64")]
    [InlineData("YWJj")]                                   // three bytes, not thirty-two
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gIQ==")]  // thirty-four
    public void AKeyThatIsNotAKeyHasNoTag(string? psk)
    {
        // Null is the honest answer, and it is what makes an unconfigured machine refuse to
        // match: see the unpaired cases below.
        Assert.Null(DiscoveryTag.ForPsk(psk));
    }

    // MARK: - Comparing tags

    [Fact]
    public void OurOwnTagMatches()
    {
        Assert.True(DiscoveryTag.Same(OurTag, DiscoveryTag.ForPsk(OurPsk)));
    }

    [Fact]
    public void SomebodyElsesTagDoesNotMatch()
    {
        Assert.False(DiscoveryTag.Same(OurTag, TheirTag));
        Assert.False(DiscoveryTag.Same(TheirTag, OurTag));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "QftJm4C_xaQadblqBrCUpA")]
    [InlineData("QftJm4C_xaQadblqBrCUpA", null)]
    [InlineData("", "")]
    [InlineData("QftJm4C_xaQadblqBrCUp", "QftJm4C_xaQadblqBrCUp")]     // twenty-one characters
    [InlineData("QftJm4C_xaQadblqBrCUpA=", "QftJm4C_xaQadblqBrCUpA=")] // padded, so not our shape
    public void TwoMachinesThatCannotStateATagAreNotPaired(string? mine, string? theirs)
    {
        // «Ни у кого нет метки» must never read as «метки совпали». That equality is how an
        // unconfigured Mac would end up trusting an unconfigured stranger.
        Assert.False(DiscoveryTag.Same(mine, theirs));
    }

    [Fact]
    public void TagComparisonIsCaseSensitive()
    {
        // base64url distinguishes case, so folding it would make roughly one key in a
        // billion collide with another — and would do it silently.
        Assert.False(DiscoveryTag.Same(OurTag, OurTag.ToLowerInvariant()));
        Assert.False(DiscoveryTag.Same(OurTag, OurTag.ToUpperInvariant()));
    }

    // MARK: - TXT

    [Fact]
    public void TheTxtRecordCarriesTheFourDocumentedKeys()
    {
        var txt = DiscoveryTxt.Build(1, 47702, "GAMING-PC", OurTag);

        Assert.Equal(
            [("v", "1"), ("port", "47702"), ("name", "GAMING-PC"), ("tag", OurTag)],
            txt.Select(entry => (entry.Key, entry.Value)));
    }

    [Fact]
    public void AHostWithNoKeyPublishesNoTagRatherThanAnEmptyOne()
    {
        var txt = DiscoveryTxt.Build(1, 47702, "GAMING-PC", null);

        Assert.DoesNotContain(txt, entry => entry.Key == "tag");
    }

    [Fact]
    public void ATxtRecordSurvivesTheRoundTrip()
    {
        var txt = DiscoveryTxt.Build(1, 47702, "Никита-ПК", OurTag);
        var entries = txt.Select(entry => $"{entry.Key}={entry.Value}");

        Assert.True(DiscoveryTxt.TryParse(entries, out var host));
        Assert.Equal(1, host.Version);
        Assert.Equal(47702, host.Port);
        Assert.Equal("Никита-ПК", host.Name);
        Assert.Equal(OurTag, host.Tag);
    }

    [Fact]
    public void KeysWeDoNotKnowAreIgnoredRatherThanFatal()
    {
        // Another responder, or a later version of us, may add entries. Refusing the whole
        // record over one unknown key would turn a forward-compatible change into an empty
        // list on every Mac in the field.
        string[] entries = ["v=1", "port=47702", "name=PC", $"tag={OurTag}", "future=whatever", "bare"];

        Assert.True(DiscoveryTxt.TryParse(entries, out var host));
        Assert.Equal(OurTag, host.Tag);
    }

    [Fact]
    public void TheFirstValueOfADuplicateKeyWins()
    {
        // RFC 6763 §6.4. A record that repeats `tag` must not let the second one decide:
        // that would be a way to append a chosen tag to somebody else's advertisement.
        string[] entries = ["v=1", "port=47702", $"tag={OurTag}", $"tag={TheirTag}"];

        Assert.True(DiscoveryTxt.TryParse(entries, out var host));
        Assert.Equal(OurTag, host.Tag);
    }

    [Theory]
    [InlineData("port=47702")]                     // no version
    [InlineData("v=1")]                            // no port
    [InlineData("v=x", "port=47702")]
    [InlineData("v=1", "port=0")]
    [InlineData("v=1", "port=70000")]
    [InlineData("v=1", "port=-5")]
    [InlineData("v=1", "port=477 02")]
    public void ATxtRecordWeCannotDialIsRefused(params string[] entries)
    {
        Assert.False(DiscoveryTxt.TryParse(entries, out _));
    }

    [Fact]
    public void AnEmptyTxtRecordIsRefused()
    {
        // Something else on the link advertising a service that happens to share our name
        // is not a HexBridge host, and must not become a row in the list.
        Assert.False(DiscoveryTxt.TryParse([], out _));
    }

    [Theory]
    [InlineData("tag=")]
    [InlineData("tag=QftJm4C_xaQadblqBrCUp")]        // one character short
    [InlineData("tag=QftJm4C_xaQadblqBrCUpAA")]      // one character long
    [InlineData("tag=QftJm4C/xaQadblqBrCUpA")]       // base64, not base64url
    [InlineData("tag=Qft Jm4C_xaQadblqBrCUp")]
    public void AMalformedTagIsDroppedAndTheHostStaysUnmatchable(string entry)
    {
        // Keeping it would only give some later comparison a chance to accept it. A host
        // whose tag we could not read is a host we have no reason to think is ours.
        Assert.True(DiscoveryTxt.TryParse(["v=1", "port=47702", "name=PC", entry], out var host));

        Assert.Null(host.Tag);
        Assert.Equal(DiscoveryVerdict.NoMatch, DiscoveryMatch.Choose(OurTag, [host]).Verdict);
    }

    [Fact]
    public void AnOverlongNameIsCutToWhatATxtStringHolds()
    {
        var txt = DiscoveryTxt.Build(1, 47702, new string('Я', 400), OurTag);
        var name = txt.Single(entry => entry.Key == "name").Value;

        Assert.True(Encoding.UTF8.GetByteCount($"name={name}") <= 255);
        // And it is still whole characters: half of «Я» is not a name.
        Assert.DoesNotContain('�', name);
    }

    [Fact]
    public void TheAnnouncementOnTheWireCarriesTheTag()
    {
        // The bytes a Mac actually reads, not just the list we handed the builder.
        var packet = MulticastDns.BuildAnnouncement(
            "GAMING-PC._hexbridge._udp.local.",
            "gaming-pc.local.",
            47702,
            [IPAddress.Parse("192.168.1.10")],
            DiscoveryTxt.Build(1, 47702, "GAMING-PC", OurTag));

        var text = Encoding.UTF8.GetString(packet);
        Assert.Contains($"tag={OurTag}", text, StringComparison.Ordinal);
        Assert.Contains("port=47702", text, StringComparison.Ordinal);
        // And never the key, in any encoding of it.
        Assert.DoesNotContain(OurPsk, text, StringComparison.Ordinal);
        Assert.DoesNotContain(PairingPayload.ToBase64Url(Convert.FromBase64String(OurPsk)), text, StringComparison.Ordinal);
    }

    // MARK: - The rule

    [Fact]
    public void AHostWithOurTagIsTheOneWeDial()
    {
        var choice = DiscoveryMatch.Choose(OurTag, [Host("GAMING-PC", "192.168.1.10", OurTag)]);

        Assert.True(choice.ShouldConnect);
        Assert.Equal("192.168.1.10:47702", choice.Host!.Value.Target);
    }

    [Fact]
    public void AHostWithSomebodyElsesTagDoesNotExistForUs()
    {
        var choice = DiscoveryMatch.Choose(OurTag, [Host("GAMING-PC", "192.168.1.77", TheirTag)]);

        Assert.False(choice.ShouldConnect);
        Assert.Equal(DiscoveryVerdict.NoMatch, choice.Verdict);
        Assert.Null(choice.Host);
    }

    [Fact]
    public void TheNameCountsForNothing()
    {
        // The stranger's PC is called exactly what ours is called — which is not far-fetched,
        // «GAMING-PC» is a default — and it is first in the list. The tag decides, alone.
        var choice = DiscoveryMatch.Choose(OurTag,
        [
            Host("GAMING-PC", "192.168.1.77", TheirTag),
            Host("GAMING-PC", "192.168.1.10", OurTag),
        ]);

        Assert.True(choice.ShouldConnect);
        Assert.Equal("192.168.1.10:47702", choice.Host!.Value.Target);
    }

    [Fact]
    public void AHostThatPublishesNoTagIsNeverDialled()
    {
        var choice = DiscoveryMatch.Choose(OurTag, [Host("PC", "192.168.1.50", null)]);

        Assert.Equal(DiscoveryVerdict.NoMatch, choice.Verdict);
    }

    [Fact]
    public void ARoomFullOfStrangersIsStillNobody()
    {
        var crowd = Enumerable.Range(1, 20)
            .Select(i => Host($"PC-{i}", $"192.168.1.{i}", DiscoveryTag.ForPsk(ReceiverConfig.GenerateKey())))
            .ToList();

        Assert.Equal(DiscoveryVerdict.NoMatch, DiscoveryMatch.Choose(OurTag, crowd).Verdict);
    }

    [Fact]
    public void AnUnpairedMacConnectsToNothingAtAll()
    {
        // The whole point: no key means no tag means no comparison. Not «the only host
        // wins», not «the first host wins» — nothing, and the short code off the host's
        // screen is still required.
        var hosts = new List<DiscoveredHost>
        {
            Host("GAMING-PC", "192.168.1.10", OurTag),
            Host("PC", "192.168.1.77", TheirTag),
        };

        foreach (var ownTag in new string?[] { null, "", "   ", "не метка" })
        {
            var choice = DiscoveryMatch.Choose(ownTag, hosts);

            Assert.Equal(DiscoveryVerdict.Unpaired, choice.Verdict);
            Assert.False(choice.ShouldConnect);
            Assert.Null(choice.Host);
            Assert.Null(DiscoveryMatch.Retarget("", choice));
        }
    }

    [Fact]
    public void AnUnpairedMacStillGetsAReasonItCanShow()
    {
        var choice = DiscoveryMatch.Choose(null, [Host("GAMING-PC", "192.168.1.10", OurTag)]);

        // §9.3: the list is shown, the connection is not made, and the user is told which
        // of those two is happening.
        Assert.Contains("код", choice.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AMatchWithNoAddressYetIsNotAMatchYet()
    {
        // Browse results arrive before they resolve. Taking one then would blank out a
        // target that is working perfectly well.
        var choice = DiscoveryMatch.Choose(OurTag, [Host("GAMING-PC", "", OurTag)]);

        Assert.Equal(DiscoveryVerdict.NoMatch, choice.Verdict);
    }

    [Fact]
    public void AnEmptyNetworkSaysSoRatherThanBlaming()
    {
        var choice = DiscoveryMatch.Choose(OurTag, []);

        Assert.Equal(DiscoveryVerdict.NoMatch, choice.Verdict);
        Assert.Contains("не видно", choice.Reason, StringComparison.Ordinal);
    }

    // MARK: - The host moved

    [Fact]
    public void AHostThatCameBackOnANewAddressIsFollowed()
    {
        // The router rebooted overnight and DHCP handed out .23 instead of .10. This is the
        // single most common «вчера работало, сегодня нет», and the tag is not tied to an
        // address precisely so that it fixes itself.
        var choice = DiscoveryMatch.Choose(OurTag, [Host("GAMING-PC", "192.168.1.23", OurTag)]);

        Assert.Equal("192.168.1.23:47702", DiscoveryMatch.Retarget("192.168.1.10:47702", choice));
    }

    [Fact]
    public void FollowingTheHostNeverTouchesTheKey()
    {
        // Re-pairing would mean a new key. The address changing is not a reason for one,
        // and asking the user for the short code again over a DHCP lease would be the bug
        // this whole mechanism exists to avoid.
        var moved = Host("GAMING-PC", "192.168.1.23", OurTag);
        var choice = DiscoveryMatch.Choose(OurTag, [moved]);

        Assert.Equal(OurTag, choice.Host!.Value.Tag);
        Assert.Equal(OurTag, DiscoveryTag.ForPsk(OurPsk));
    }

    [Fact]
    public void AHostThatMovedPortIsFollowedToo()
    {
        var choice = DiscoveryMatch.Choose(OurTag, [Host("GAMING-PC", "192.168.1.10", OurTag, port: 47800)]);

        Assert.Equal("192.168.1.10:47800", DiscoveryMatch.Retarget("192.168.1.10:47702", choice));
    }

    [Fact]
    public void AHostThatHasNotMovedIsLeftAlone()
    {
        // Returning the same address every browse cycle would restart the pipeline every
        // browse cycle, which is a dropped word of speech each time.
        var choice = DiscoveryMatch.Choose(OurTag, [Host("GAMING-PC", "192.168.1.10", OurTag)]);

        Assert.Null(DiscoveryMatch.Retarget("192.168.1.10:47702", choice));
        Assert.Null(DiscoveryMatch.Retarget("192.168.1.10:47702 ", choice));
    }

    [Fact]
    public void AStrangerThatMovedIsStillNotFollowed()
    {
        var choice = DiscoveryMatch.Choose(OurTag, [Host("GAMING-PC", "192.168.1.23", TheirTag)]);

        Assert.Null(DiscoveryMatch.Retarget("192.168.1.10:47702", choice));
    }

    [Fact]
    public void AMacWithNoTargetYetIsGivenTheOneThatMatches()
    {
        var choice = DiscoveryMatch.Choose(OurTag, [Host("GAMING-PC", "10.0.0.5", OurTag)]);

        Assert.Equal("10.0.0.5:47702", DiscoveryMatch.Retarget("", choice));
        Assert.Equal("10.0.0.5:47702", DiscoveryMatch.Retarget(null, choice));
    }

    // MARK: - What the host publishes

    [Fact]
    public void ThePublisherAdvertisesTheTagOfTheKeyInTheConfig()
    {
        // The tag on the wire has to be the tag of the key the receiver is actually
        // decrypting with, or a Mac would find a PC it cannot talk to.
        var config = new ReceiverConfig { Psk = OurPsk, Listen = "0.0.0.0:47702" };
        var txt = DiscoveryTxt.Build(
            PairingPayload.Version, 47702, "GAMING-PC", DiscoveryTag.ForPsk(config.Psk));

        Assert.Equal(OurTag, txt.Single(entry => entry.Key == "tag").Value);
    }

    [Fact]
    public void AnUnconfiguredHostAdvertisesItselfWithoutATag()
    {
        // It still belongs in the list — that is how a first pairing starts — but it can
        // never be the answer to «is this mine».
        var config = new ReceiverConfig { Psk = "" };
        var txt = DiscoveryTxt.Build(
            PairingPayload.Version, 47702, "NEW-PC", DiscoveryTag.ForPsk(config.Psk));

        Assert.DoesNotContain(txt, entry => entry.Key == "tag");
        Assert.True(DiscoveryTxt.TryParse(txt.Select(e => $"{e.Key}={e.Value}"), out var host));
        Assert.Equal(DiscoveryVerdict.NoMatch, DiscoveryMatch.Choose(OurTag, [host]).Verdict);
    }

    [Fact]
    public void RepairingTheHostChangesWhatItAdvertises()
    {
        // After the user pairs this PC with a different Mac, the old Mac must stop finding
        // it. That happens by itself, because the tag follows the key.
        var before = DiscoveryTag.ForPsk(OurPsk);
        var after = DiscoveryTag.ForPsk(TheirPsk);

        Assert.NotEqual(before, after);
        Assert.Equal(
            DiscoveryVerdict.NoMatch,
            DiscoveryMatch.Choose(before, [Host("GAMING-PC", "192.168.1.10", after)]).Verdict);
    }
}
