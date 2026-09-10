using System.Security.Cryptography;
using System.Text;
using HexBridge;
using HexBridge.Clipboard;

namespace HexBridge.Tests;

/// <summary>
/// The half of the clipboard feature that has no Windows in it: deciding what to offer, what
/// to ignore, and — the whole reason this class exists apart from the feature — never handing
/// an object back to the machine it came from.
/// </summary>
public class ClipboardSyncTests
{
    private static ClipboardItem Text(string value) =>
        new(BulkFormat.Utf8Text, Encoding.UTF8.GetBytes(value));

    private static ClipboardItem Image(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        // A PNG signature, so the fixture is at least shaped like the thing it stands for.
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return new ClipboardItem(BulkFormat.Png, bytes);
    }

    [Fact]
    public void AnUntouchedClipboardOffersNothing()
    {
        var surface = new FakeClipboard();
        var sync = new ClipboardSync(surface);

        Assert.Null(sync.Poll());
        Assert.Null(sync.Poll());
    }

    [Fact]
    public void WhatTheUserCopiesIsOfferedExactlyOnce()
    {
        var surface = new FakeClipboard();
        var sync = new ClipboardSync(surface);

        surface.UserCopies(Text("привет"));

        var offered = sync.Poll();
        Assert.NotNull(offered);
        Assert.Equal(BulkFormat.Utf8Text, offered.Format);
        Assert.Null(sync.Poll());
        Assert.Null(sync.Poll());
    }

    [Fact]
    public void WhatArrivesIsNotOfferedBack()
    {
        var surface = new FakeClipboard();
        var sync = new ClipboardSync(surface);

        sync.Apply(Text("пришло с другой машины"));

        // The write bumped the change counter, the watcher will see it, and this is the
        // moment the two machines would start handing the object back and forth.
        Assert.Null(sync.Poll());
        Assert.Null(sync.Poll());
        Assert.Equal(1, surface.Writes);
    }

    [Fact]
    public void WhatArrivesIsNotOfferedBackEvenWhenTheSystemRewritesIt()
    {
        var surface = new FakeClipboard
        {
            // Windows and macOS both re-encode some formats, so the bytes that come back out
            // are not the bytes that went in — and it is those that the watcher will hash.
            Reencode = item => new ClipboardItem(item.Format, [.. item.Bytes, 0x00]),
        };
        var sync = new ClipboardSync(surface);

        sync.Apply(Image(4000, seed: 1));

        Assert.Null(sync.Poll());
        Assert.Null(sync.Poll());
    }

    [Fact]
    public void AnObjectCopiedAgainAfterSomethingElseIsOfferedAgain()
    {
        var surface = new FakeClipboard();
        var sync = new ClipboardSync(surface);
        var a = Text("первое");
        var b = Text("второе");

        surface.UserCopies(a);
        Assert.NotNull(sync.Poll());
        surface.UserCopies(b);
        Assert.NotNull(sync.Poll());

        // The peer's clipboard holds B now, so A really does have to travel a second time.
        // A «recently seen» suppression list would swallow this, which is why there is none.
        surface.UserCopies(a);
        var again = sync.Poll();
        Assert.NotNull(again);
        Assert.Equal(a.Hash, again.Hash);
    }

    [Fact]
    public void WhatThePeerAlreadyHoldsIsNotOfferedToIt()
    {
        var surface = new FakeClipboard();
        var sync = new ClipboardSync(surface);
        var item = Text("одно и то же");

        sync.NotePeerHas(item.Hash);
        surface.UserCopies(item);

        Assert.Null(sync.Poll());
    }

    [Fact]
    public void OwnsAnswersForWhateverTheClipboardCurrentlyHolds()
    {
        var surface = new FakeClipboard();
        var sync = new ClipboardSync(surface);
        var item = Text("моё");

        Assert.False(sync.Owns(item.Hash));
        surface.UserCopies(item);
        sync.Poll();

        Assert.True(sync.Owns(item.Hash));
        Assert.False(sync.Owns(SHA256.HashData("чужое"u8.ToArray())));
    }

    [Fact]
    public void ForgettingThePeerDoesNotForgetOurOwnClipboard()
    {
        var surface = new FakeClipboard();
        var sync = new ClipboardSync(surface);
        var item = Text("моё");

        surface.UserCopies(item);
        sync.Poll();
        sync.ForgetPeer();

        Assert.True(sync.Owns(item.Hash));
        Assert.Null(sync.Poll());
    }

    [Fact]
    public void ADescriptionSaysTheShapeAndTheSizeAndNothingElse()
    {
        var secret = Text("hunter2-this-is-a-password");
        Assert.DoesNotContain("hunter2", secret.Describe());
        Assert.StartsWith("текст,", secret.Describe());
        Assert.StartsWith("изображение,", Image(200_000, seed: 2).Describe());
    }

    // MARK: - Two machines

    [Fact]
    public void TextCrossesOnceAndTheLoopStops()
    {
        var link = new SyncLink();
        link.Mac.Copy(Text("скопировано на маке"));

        link.Run(seconds: 5);

        Assert.Equal("скопировано на маке", Encoding.UTF8.GetString(link.Windows.Holding!.Bytes));
        // One offer on the wire, one BULK_DONE, and then nothing at all: the object is not
        // handed back, and a second round would show up here as a second offer.
        Assert.Equal(1, link.OffersMacToWindows);
        Assert.Equal(0, link.OffersWindowsToMac);
    }

    [Fact]
    public void AnImageCrossesTheOtherWayAndTheLoopStops()
    {
        var link = new SyncLink();
        var image = Image(300_000, seed: 3);
        link.Windows.Copy(image);

        link.Run(seconds: 10);

        Assert.Equal(image.Bytes, link.Mac.Holding!.Bytes);
        Assert.Equal(1, link.OffersWindowsToMac);
        Assert.Equal(0, link.OffersMacToWindows);
    }

    [Fact]
    public void TheLoopStopsEvenWhenTheReceivingSystemRewritesTheBytes()
    {
        var link = new SyncLink();
        link.Windows.Surface.Reencode = item => new ClipboardItem(item.Format, [.. item.Bytes, 0x00]);

        link.Mac.Copy(Image(20_000, seed: 4));
        link.Run(seconds: 10);

        Assert.Equal(1, link.OffersMacToWindows);
        Assert.Equal(0, link.OffersWindowsToMac);
    }

    [Fact]
    public void ObjectsKeepCrossingAfterTheFirstOne()
    {
        var link = new SyncLink();

        link.Mac.Copy(Text("раз"));
        link.Run(seconds: 3);
        link.Windows.Copy(Text("два"));
        link.Run(seconds: 3);
        link.Mac.Copy(Text("три"));
        link.Run(seconds: 3);

        Assert.Equal("три", Encoding.UTF8.GetString(link.Windows.Holding!.Bytes));
        Assert.Equal("три", Encoding.UTF8.GetString(link.Mac.Holding!.Bytes));
        Assert.Equal(2, link.OffersMacToWindows);
        Assert.Equal(1, link.OffersWindowsToMac);
    }

    [Fact]
    public void ALossyLinkDeliversTheObjectAndStillDoesNotLoop()
    {
        var link = new SyncLink();
        var random = new Random(21);
        link.Drop = (_, type, _) => type == PacketType.BulkChunk && random.Next(4) == 0;

        var image = Image(150_000, seed: 5);
        link.Mac.Copy(image);
        link.Run(seconds: 20);

        Assert.Equal(image.Bytes, link.Windows.Holding!.Bytes);
        Assert.Equal(1, link.OffersMacToWindows);
        Assert.Equal(0, link.OffersWindowsToMac);
    }

    // MARK: - Harness

    private sealed class FakeClipboard : IClipboardSurface
    {
        private ClipboardItem? _item;

        public long ChangeCount { get; private set; }
        public int Writes { get; private set; }

        /// <summary>Stands in for a system that does not store what it was handed verbatim.</summary>
        public Func<ClipboardItem, ClipboardItem>? Reencode { get; set; }

        public ClipboardItem? Read() => _item;

        public void Write(ClipboardItem item)
        {
            _item = Reencode?.Invoke(item) ?? item;
            ChangeCount++;
            Writes++;
        }

        /// <summary>Somebody pressed Cmd-C.</summary>
        public void UserCopies(ClipboardItem item)
        {
            _item = item;
            ChangeCount++;
        }
    }

    /// <summary>One machine: a clipboard, the loop breaker, and the reliable channel.</summary>
    private sealed class Node
    {
        private readonly Queue<BulkDelivery> _arrivals = new();

        public FakeClipboard Surface { get; } = new();
        public ClipboardSync Sync { get; }
        public BulkChannel Channel { get; }

        public ClipboardItem? Holding => Surface.Read();

        public Node(Action<PacketType, ReadOnlyMemory<byte>> send)
        {
            Sync = new ClipboardSync(Surface);
            Channel = new BulkChannel(send) { Owns = Sync.Owns };
            Channel.Delivered += _arrivals.Enqueue;
            Channel.Finished += result =>
            {
                if (result.Outcome is BulkOutcome.Delivered or BulkOutcome.AlreadyThere)
                {
                    Sync.NotePeerHas(result.Hash);
                }
            };
        }

        public void Copy(ClipboardItem item) => Surface.UserCopies(item);

        /// <summary>Exactly what the feature's worker thread does, one turn of it.</summary>
        public void Step(DateTime now, bool poll)
        {
            while (_arrivals.Count > 0)
            {
                var delivery = _arrivals.Dequeue();
                Sync.Apply(new ClipboardItem(delivery.Format, delivery.Bytes));
            }

            if (poll && Sync.Poll() is { } item)
            {
                Channel.Offer(BulkKind.Clipboard, item.Format, item.Bytes, item.Describe(), now, out _);
            }

            Channel.Tick(now);
        }
    }

    /// <summary>Both machines and the wire between them, on a clock the test owns.</summary>
    private sealed class SyncLink
    {
        private readonly List<(bool toWindows, PacketType type, byte[] payload)> _inFlight = [];
        private int _pollCountdown;

        public Node Mac { get; }
        public Node Windows { get; }
        public DateTime Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public Func<bool, PacketType, byte[], bool> Drop { get; set; } = (_, _, _) => false;

        public int OffersMacToWindows { get; private set; }
        public int OffersWindowsToMac { get; private set; }

        public SyncLink()
        {
            Mac = new Node((type, payload) =>
            {
                if (type == PacketType.BulkOffer) OffersMacToWindows++;
                _inFlight.Add((true, type, payload.ToArray()));
            });
            Windows = new Node((type, payload) =>
            {
                if (type == PacketType.BulkOffer) OffersWindowsToMac++;
                _inFlight.Add((false, type, payload.ToArray()));
            });
        }

        public void Run(int seconds)
        {
            for (var step = 0; step < seconds * 50; step++)
            {
                Now = Now.AddMilliseconds(20);
                // The feature polls the clipboard every 250 ms, not on every tick.
                var poll = --_pollCountdown <= 0;
                if (poll) _pollCountdown = 12;

                Mac.Step(Now, poll);
                Windows.Step(Now, poll);
                Deliver();
            }
        }

        private void Deliver()
        {
            for (var round = 0; round < 8 && _inFlight.Count > 0; round++)
            {
                var batch = _inFlight.ToArray();
                _inFlight.Clear();
                foreach (var (toWindows, type, payload) in batch)
                {
                    if (Drop(toWindows, type, payload)) continue;
                    (toWindows ? Windows : Mac).Channel.OnPacket(type, payload, Now);
                }
            }
        }
    }
}
