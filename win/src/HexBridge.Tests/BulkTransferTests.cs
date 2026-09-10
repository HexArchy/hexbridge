using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HexBridge;

namespace HexBridge.Tests;

/// <summary>
/// The reliable-delivery layer from docs/PROTOCOL.md: the byte layouts, and then the two
/// channels talking to each other over a link that can be told to lose or mangle whatever
/// the test wants.
///
/// The clock is a field rather than <c>DateTime.UtcNow</c>, so «ten offers over ten seconds»
/// is a test that runs in a millisecond.
/// </summary>
public class BulkTransferTests
{
    // MARK: - Layout

    [Fact]
    public void TypeNumbersMatchTheContract()
    {
        Assert.Equal(9, (byte)PacketType.BulkOffer);
        Assert.Equal(10, (byte)PacketType.BulkChunk);
        Assert.Equal(11, (byte)PacketType.BulkAck);
        Assert.Equal(12, (byte)PacketType.BulkDone);
    }

    [Fact]
    public void TheConstantsMatchTheContract()
    {
        Assert.Equal(1024, Bulk.ChunkSize);
        Assert.Equal(16 * 1024 * 1024, Bulk.MaxObjectSize);
        Assert.Equal(256, Bulk.MaxMissingListed);
        Assert.Equal(10, Bulk.MaxOfferAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), Bulk.OfferInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(200), Bulk.AckInterval);
    }

    [Fact]
    public void TheOfferLayoutIsTheOneInTheContract()
    {
        var hash = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
        var offer = new BulkOffer(0xAABBCCDD, BulkKind.Clipboard, BulkFormat.Png, 5000, 5, hash, "снимок");
        var bytes = BulkCodec.WriteOffer(offer);

        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(1, bytes[4]);                                         // вид: буфер обмена
        Assert.Equal(2, bytes[5]);                                         // формат: PNG
        Assert.Equal(5000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(6)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(10)));
        Assert.Equal(hash, bytes[14..46]);
        Assert.Equal(Encoding.UTF8.GetByteCount("снимок"), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(46)));
        Assert.Equal(48 + Encoding.UTF8.GetByteCount("снимок"), bytes.Length);

        Assert.True(BulkCodec.TryReadOffer(bytes, out var read));
        Assert.Equal(offer.TransferId, read.TransferId);
        Assert.Equal(offer.Kind, read.Kind);
        Assert.Equal(offer.Format, read.Format);
        Assert.Equal(offer.Size, read.Size);
        Assert.Equal(offer.ChunkCount, read.ChunkCount);
        Assert.Equal(offer.Hash, read.Hash);
        Assert.Equal("снимок", read.Description);
    }

    [Fact]
    public void TheAckLayoutIsTheOneInTheContract()
    {
        var ack = new BulkAck(7, true, 12, [13, 99]);
        var bytes = BulkCodec.WriteAck(ack);

        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(1, bytes[4]);
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(9)));
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(11)));
        Assert.Equal(99u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(15)));
        Assert.Equal(11 + 8, bytes.Length);

        Assert.True(BulkCodec.TryReadAck(bytes, out var read));
        Assert.True(read.Accepted);
        Assert.Equal(12u, read.FirstMissing);
        Assert.Equal(new uint[] { 13, 99 }, read.Missing);
        Assert.False(read.Truncated);
    }

    [Fact]
    public void TheChunkAndDoneLayoutsAreTheOnesInTheContract()
    {
        var data = new byte[] { 1, 2, 3 };
        var chunk = BulkCodec.WriteChunk(4, 5, data);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(chunk));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4)));
        Assert.True(BulkCodec.TryReadChunk(chunk, out var id, out var index, out var read));
        Assert.Equal(4u, id);
        Assert.Equal(5u, index);
        Assert.Equal(data, read);

        var done = BulkCodec.WriteDone(0xDEADBEEF);
        Assert.Equal(4, done.Length);
        Assert.True(BulkCodec.TryReadDone(done, out var doneId));
        Assert.Equal(0xDEADBEEFu, doneId);
    }

    [Fact]
    public void EveryBulkPacketFitsTheMtu()
    {
        var overhead = Wire.HeaderSize + Wire.TagSize;

        Assert.True(overhead + Bulk.ChunkHeaderSize + Bulk.ChunkSize <= Wire.MaxPacket);

        var full = BulkCodec.WriteAck(new BulkAck(1, true, 0, [.. Enumerable.Range(1, 400).Select(i => (uint)i)]));
        Assert.Equal(Bulk.AckHeaderSize + 4 * Bulk.MaxMissingListed, full.Length);
        Assert.True(overhead + full.Length <= Wire.MaxPacket);

        var offer = BulkCodec.WriteOffer(new BulkOffer(
            1, BulkKind.Clipboard, BulkFormat.Utf8Text, 1, 1, new byte[32], new string('я', 400)));
        Assert.True(offer.Length <= Bulk.OfferHeaderSize + Bulk.MaxDescriptionBytes);
        Assert.True(overhead + offer.Length <= Wire.MaxPacket);
    }

    [Fact]
    public void ATruncatedDescriptionIsStillValidUtf8()
    {
        var offer = BulkCodec.WriteOffer(new BulkOffer(
            1, BulkKind.Clipboard, BulkFormat.Utf8Text, 1, 1, new byte[32], new string('я', 400)));

        Assert.True(BulkCodec.TryReadOffer(offer, out var read));
        // Every «я» is two bytes, so a cut in the middle of one would come back as U+FFFD.
        Assert.DoesNotContain('�', read.Description);
        Assert.Equal(128, read.Description.Length);
    }

    [Fact]
    public void ATruncatedPacketIsRejectedRatherThanGuessedAt()
    {
        var offer = BulkCodec.WriteOffer(new BulkOffer(
            1, BulkKind.Clipboard, BulkFormat.Png, 10, 1, new byte[32], "текст"));

        Assert.False(BulkCodec.TryReadOffer(offer.AsSpan(0, 40), out _));
        Assert.False(BulkCodec.TryReadOffer(offer.AsSpan(0, Bulk.OfferHeaderSize + 1), out _));
        Assert.False(BulkCodec.TryReadAck([1, 2, 3], out _));
        Assert.False(BulkCodec.TryReadChunk([1, 2, 3], out _, out _, out _));
        Assert.False(BulkCodec.TryReadDone([1, 2], out _));
    }

    [Fact]
    public void AnAckClaimingMoreNumbersThanTheContractAllowsIsRejected()
    {
        var bytes = new byte[Bulk.AckHeaderSize + 4 * 300];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(9), 300);

        Assert.False(BulkCodec.TryReadAck(bytes, out _));
    }

    [Fact]
    public void ChunkArithmeticHandlesTheShortLastBlock()
    {
        Assert.Equal(1u, Bulk.ChunkCountFor(1));
        Assert.Equal(1u, Bulk.ChunkCountFor(1024));
        Assert.Equal(2u, Bulk.ChunkCountFor(1025));
        Assert.Equal(16384u, Bulk.ChunkCountFor(Bulk.MaxObjectSize));

        Assert.Equal(1024, Bulk.ChunkLength(2500, 0));
        Assert.Equal(1024, Bulk.ChunkLength(2500, 1));
        Assert.Equal(452, Bulk.ChunkLength(2500, 2));
        Assert.Equal(0, Bulk.ChunkLength(2500, 3));
    }

    // MARK: - Delivery

    [Fact]
    public void AnObjectIsReassembledFromItsChunks()
    {
        var pair = new Pair();
        var payload = Payload(100_000, seed: 1);

        var id = pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out var error);
        Assert.NotNull(id);
        Assert.Null(error);

        pair.Run(seconds: 3);

        var delivery = Assert.Single(pair.DeliveredAtB);
        Assert.Equal(payload, delivery.Bytes);
        Assert.Equal(SHA256.HashData(payload), delivery.Hash);
        Assert.Equal(BulkFormat.Png, delivery.Format);
        Assert.Equal(BulkKind.Clipboard, delivery.Kind);
        Assert.Equal("снимок", delivery.Description);

        var result = Assert.Single(pair.FinishedAtA);
        Assert.Equal(BulkOutcome.Delivered, result.Outcome);
        Assert.Equal(id, result.TransferId);
    }

    [Fact]
    public void ASingleShortChunkIsAWholeTransfer()
    {
        var pair = new Pair();
        var payload = Encoding.UTF8.GetBytes("привет");

        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Utf8Text, payload, "текст", pair.Now, out _);
        pair.Run(seconds: 2);

        Assert.Equal(payload, Assert.Single(pair.DeliveredAtB).Bytes);
    }

    [Fact]
    public void LostChunksAreRepeatedUntilTheObjectArrives()
    {
        var pair = new Pair();
        var random = new Random(7);
        // Every third chunk on average never makes it. Only chunks: losing the offer or the
        // ack is a different failure and has its own test.
        pair.Drop = (toB, type, _) => toB && type == PacketType.BulkChunk && random.Next(3) == 0;

        var payload = Payload(300_000, seed: 2);
        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out _);

        pair.Run(seconds: 20);

        Assert.Equal(payload, Assert.Single(pair.DeliveredAtB).Bytes);
        Assert.Equal(BulkOutcome.Delivered, Assert.Single(pair.FinishedAtA).Outcome);
        // The repeats are real repeats, not a second full pass by accident.
        Assert.True(pair.ChunksSentToB > Bulk.ChunkCountFor(payload.Length));
    }

    [Fact]
    public void AHalfLostLinkStillDelivers()
    {
        var pair = new Pair();
        var random = new Random(11);
        // Everything suffers, in both directions: offers, chunks, acks and the done.
        pair.Drop = (_, _, _) => random.Next(2) == 0;

        var payload = Payload(60_000, seed: 3);
        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out _);

        pair.Run(seconds: 30);

        Assert.Equal(payload, Assert.Single(pair.DeliveredAtB).Bytes);
    }

    [Fact]
    public void AnObjectTheReceiverAlreadyHasIsNotPulled()
    {
        var pair = new Pair();
        var payload = Payload(50_000, seed: 4);
        var hash = SHA256.HashData(payload);

        // Exactly the contract's loop breaker: the receiver recognises the hash.
        pair.B.Owns = candidate => candidate.SequenceEqual(hash);

        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out _);
        pair.Run(seconds: 3);

        Assert.Empty(pair.DeliveredAtB);
        Assert.Equal(0, pair.ChunksSentToB);
        var result = Assert.Single(pair.FinishedAtA);
        Assert.Equal(BulkOutcome.AlreadyThere, result.Outcome);
    }

    [Fact]
    public void CorruptionIsCaughtByTheHashAndTheObjectIsAskedForAgain()
    {
        var pair = new Pair();
        var payload = Payload(20_000, seed: 5);
        var mangled = 0;

        // One chunk arrives with the right length and the wrong bytes — the one kind of
        // damage a length check cannot see.
        pair.Mangle = (toB, type, bytes) =>
        {
            if (!toB || type != PacketType.BulkChunk || mangled > 0) return bytes;
            mangled++;
            var copy = bytes.ToArray();
            copy[^1] ^= 0xFF;
            return copy;
        };

        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out _);
        pair.Run(seconds: 10);

        Assert.Equal(1, mangled);
        var delivery = Assert.Single(pair.DeliveredAtB);
        // Never the corrupt bytes: the receiver threw the whole object away and re-asked.
        Assert.Equal(payload, delivery.Bytes);
        Assert.Contains(pair.NotesAtB, note => note.Contains("хеш не сошёлся"));
    }

    [Fact]
    public void AnObjectThatKeepsArrivingBrokenIsGivenUpOn()
    {
        var pair = new Pair();
        var payload = Payload(4_000, seed: 6);

        // Chunk 0 is damaged every single time, so the hash can never match.
        pair.Mangle = (toB, type, bytes) =>
        {
            if (!toB || type != PacketType.BulkChunk) return bytes;
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 0) return bytes;
            var copy = bytes.ToArray();
            copy[^1] ^= 0xFF;
            return copy;
        };

        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out _);
        pair.Run(seconds: 40);

        Assert.Empty(pair.DeliveredAtB);
        Assert.Contains(pair.NotesAtB, note => note.Contains("трижды"));
    }

    [Fact]
    public void AChunkOfTheWrongLengthIsIgnoredRatherThanStored()
    {
        var pair = new Pair();
        var payload = Payload(8_000, seed: 7);
        var trimmed = 0;

        pair.Mangle = (toB, type, bytes) =>
        {
            if (!toB || type != PacketType.BulkChunk || trimmed > 0) return bytes;
            trimmed++;
            return bytes[..^1];
        };

        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out _);
        pair.Run(seconds: 10);

        // It was dropped on the floor, asked for again, and the object still came out right —
        // and the hash was never given the chance to fail.
        Assert.Equal(payload, Assert.Single(pair.DeliveredAtB).Bytes);
        Assert.DoesNotContain(pair.NotesAtB, note => note.Contains("хеш не сошёлся"));
    }

    [Fact]
    public void RepeatedChunksDoNotDisturbTheBuffer()
    {
        var pair = new Pair();
        var payload = Payload(9_000, seed: 8);
        pair.Duplicate = (toB, type, _) => toB && type == PacketType.BulkChunk;

        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out _);
        pair.Run(seconds: 5);

        Assert.Equal(payload, Assert.Single(pair.DeliveredAtB).Bytes);
    }

    // MARK: - The missing list and its cap

    [Fact]
    public void TheMissingListNamesTheFirstHoleAndAtMostTwoHundredAndFiftySixMore()
    {
        var pair = new Pair();
        // 1000 chunks, none of which ever arrive: the ack has to say so within one packet.
        pair.Drop = (toB, type, _) => toB && type == PacketType.BulkChunk;

        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, Payload(1000 * Bulk.ChunkSize, seed: 9), "снимок", pair.Now, out _);
        pair.Run(seconds: 2);

        var acks = pair.SentToA.Where(p => p.type == PacketType.BulkAck).ToArray();
        Assert.NotEmpty(acks);
        foreach (var (_, payload) in acks)
        {
            Assert.True(BulkCodec.TryReadAck(payload, out var ack));
            Assert.Equal(0u, ack.FirstMissing);
            Assert.Equal(Bulk.MaxMissingListed, ack.Missing.Length);
            Assert.True(ack.Truncated);
            Assert.True(payload.Length <= Bulk.AckHeaderSize + 4 * Bulk.MaxMissingListed);
        }
    }

    [Fact]
    public void MoreHolesThanFitInOneAckMakeTheSenderStartOverFromTheFirst()
    {
        var pair = new Pair();
        var random = new Random(13);
        // Enough chunks and enough loss that the first ack cannot possibly name every hole.
        pair.Drop = (toB, type, _) => toB && type == PacketType.BulkChunk && random.Next(2) == 0;

        var payload = Payload(700 * Bulk.ChunkSize, seed: 10);
        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out _);
        pair.Run(seconds: 60);

        Assert.Equal(payload, Assert.Single(pair.DeliveredAtB).Bytes);
    }

    // MARK: - Offers

    [Fact]
    public void AnOfferIsRepeatedOncePerSecondAndTenTimesAtMost()
    {
        var pair = new Pair();
        // Nobody is listening: every packet towards B disappears.
        pair.Drop = (toB, _, _) => toB;

        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Utf8Text, Encoding.UTF8.GetBytes("текст"), "текст", pair.Now, out _);
        pair.Run(seconds: 12);

        var offers = pair.SentToB.Where(p => p.type == PacketType.BulkOffer).ToArray();
        Assert.Equal(Bulk.MaxOfferAttempts, offers.Length);

        var result = Assert.Single(pair.FinishedAtA);
        Assert.Equal(BulkOutcome.NoAnswer, result.Outcome);

        // And nothing was ever sent that the receiver had not agreed to take.
        Assert.Equal(0, pair.ChunksSentToB);
    }

    [Fact]
    public void ALostAckIsCuredByTheNextOffer()
    {
        var pair = new Pair();
        var swallowed = 0;
        // The receiver's first two answers never arrive, exactly like a lost DEV_ACK.
        pair.Drop = (toB, type, _) => !toB && type == PacketType.BulkAck && swallowed++ < 2;

        var payload = Payload(3_000, seed: 11);
        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out _);
        pair.Run(seconds: 5);

        Assert.Equal(payload, Assert.Single(pair.DeliveredAtB).Bytes);
    }

    [Fact]
    public void ALostDoneIsRepeatedRatherThanLeavingTheSenderHolding()
    {
        var pair = new Pair();
        var swallowed = 0;
        pair.Drop = (toB, type, _) => !toB && type == PacketType.BulkDone && swallowed++ < 3;

        var payload = Payload(2_000, seed: 12);
        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out _);
        pair.Run(seconds: 10);

        Assert.Single(pair.DeliveredAtB);
        // The object landed once and the sender was told about it, so it can drop the bytes.
        Assert.Equal(BulkOutcome.Delivered, Assert.Single(pair.FinishedAtA).Outcome);
    }

    [Fact]
    public void ASilentReceiverStopsTheTransferInsteadOfSpinningForever()
    {
        var pair = new Pair();
        var accepted = false;
        // Accept once, then go dead: no acks, no done. The sender must not sit on the
        // object for the rest of the session.
        pair.Drop = (toB, type, _) =>
        {
            if (toB) return false;
            if (type == PacketType.BulkAck && !accepted)
            {
                accepted = true;
                return false;
            }
            return true;
        };

        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, Payload(5_000, seed: 14), "снимок", pair.Now, out _);
        pair.Run(seconds: 30);

        Assert.Equal(BulkOutcome.Stalled, Assert.Single(pair.FinishedAtA).Outcome);
    }

    // MARK: - Size limits

    [Fact]
    public void AnEmptyObjectIsRefused()
    {
        var pair = new Pair();
        var id = pair.A.Offer(BulkKind.Clipboard, BulkFormat.Utf8Text, [], "пусто", pair.Now, out var error);

        Assert.Null(id);
        Assert.Contains("пуст", error);
        Assert.Empty(pair.SentToB);
    }

    [Fact]
    public void SixteenMebibytesIsTheLastSizeThatFits()
    {
        var pair = new Pair();

        var ok = pair.A.Offer(BulkKind.Clipboard, BulkFormat.Opaque, new byte[Bulk.MaxObjectSize], "предел", pair.Now, out var okError);
        Assert.NotNull(ok);
        Assert.Null(okError);

        var tooBig = pair.A.Offer(BulkKind.Clipboard, BulkFormat.Opaque, new byte[Bulk.MaxObjectSize + 1], "перебор", pair.Now, out var error);
        Assert.Null(tooBig);
        Assert.Contains("16 МиБ", error);

        // The one that fits is offered with the chunk count the contract implies.
        var offer = pair.SentToB.Single(p => p.type == PacketType.BulkOffer);
        Assert.True(BulkCodec.TryReadOffer(offer.payload, out var read));
        Assert.Equal(16384u, read.ChunkCount);
        Assert.Equal((uint)Bulk.MaxObjectSize, read.Size);
    }

    [Fact]
    public void AnOfferBiggerThanTheContractAllowsIsIgnoredByTheReceiver()
    {
        var pair = new Pair();
        var lies = BulkCodec.WriteOffer(new BulkOffer(
            1, BulkKind.Clipboard, BulkFormat.Opaque, (uint)Bulk.MaxObjectSize + 1, 16385, new byte[32], "ложь"));

        pair.B.OnPacket(PacketType.BulkOffer, lies, pair.Now);
        pair.Run(seconds: 1);

        // No ack at all: a receiver that answered would have to allocate the buffer first.
        Assert.Empty(pair.SentToA);
    }

    [Fact]
    public void AnOfferWhoseChunkCountDisagreesWithItsSizeIsIgnored()
    {
        var pair = new Pair();
        var lies = BulkCodec.WriteOffer(new BulkOffer(
            1, BulkKind.Clipboard, BulkFormat.Opaque, 5000, 3, new byte[32], "ложь"));

        pair.B.OnPacket(PacketType.BulkOffer, lies, pair.Now);
        pair.Run(seconds: 1);

        Assert.Empty(pair.SentToA);
    }

    [Fact]
    public void ProgressCountsChunksInBothDirections()
    {
        var pair = new Pair();
        pair.Drop = (toB, type, _) => toB && type == PacketType.BulkChunk;
        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, Payload(100 * Bulk.ChunkSize, seed: 15), "снимок", pair.Now, out _);
        pair.Run(seconds: 1);

        var outgoing = Assert.Single(pair.A.Progress());
        Assert.Equal(BulkDirection.Outgoing, outgoing.Direction);
        Assert.Equal(100u, outgoing.ChunkCount);

        var incoming = Assert.Single(pair.B.Progress());
        Assert.Equal(BulkDirection.Incoming, incoming.Direction);
        Assert.Equal(0u, incoming.ChunksDone);
        Assert.Equal(0, incoming.Fraction);
    }

    [Fact]
    public void AnObjectOfferedBeforeThePeerExistsIsStillDelivered()
    {
        var pair = new Pair();
        var open = false;
        // The peer is simply not there for the first two seconds — which is the ordinary
        // state of a receiver that started before the sender said anything.
        pair.Drop = (toB, _, _) => toB && !open;

        var payload = Payload(4_000, seed: 17);
        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out _);
        pair.Run(seconds: 2);
        Assert.Empty(pair.DeliveredAtB);

        open = true;
        pair.Run(seconds: 3);

        Assert.Equal(payload, Assert.Single(pair.DeliveredAtB).Bytes);
    }

    [Fact]
    public void APeerRestartOffersTheObjectAgainInsteadOfLosingIt()
    {
        var pair = new Pair();
        pair.Drop = (toB, type, _) => toB && type == PacketType.BulkChunk;

        var payload = Payload(30_000, seed: 18);
        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out _);
        pair.Run(seconds: 1);
        Assert.NotEmpty(pair.A.Progress());

        // Both ends learn the other one restarted. The half-assembled copy is worthless, the
        // object is not.
        pair.A.PeerRestarted();
        pair.B.PeerRestarted();
        pair.Drop = (_, _, _) => false;
        pair.Run(seconds: 5);

        Assert.Equal(payload, Assert.Single(pair.DeliveredAtB).Bytes);
        Assert.Equal(BulkOutcome.Delivered, Assert.Single(pair.FinishedAtA).Outcome);
    }

    [Fact]
    public void ResetDropsEverythingInFlight()
    {
        var pair = new Pair();
        pair.Drop = (toB, type, _) => toB && type == PacketType.BulkChunk;
        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, Payload(50 * Bulk.ChunkSize, seed: 16), "снимок", pair.Now, out _);
        pair.Run(seconds: 1);

        Assert.NotEmpty(pair.A.Progress());
        pair.A.Reset();
        pair.B.Reset();
        Assert.Empty(pair.A.Progress());
        Assert.Empty(pair.B.Progress());
    }

    // MARK: - Harness

    private static byte[] Payload(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Two channels and the wire between them. The wire is allowed to lose, duplicate and
    /// mangle whatever a test asks it to, which is the only way to reach the parts of the
    /// contract that exist because real links do all three.
    /// </summary>
    private sealed class Pair
    {
        private readonly List<(bool toB, PacketType type, byte[] payload)> _inFlight = [];

        public BulkChannel A { get; }
        public BulkChannel B { get; }

        public DateTime Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public Func<bool, PacketType, byte[], bool> Drop { get; set; } = (_, _, _) => false;
        public Func<bool, PacketType, byte[], bool> Duplicate { get; set; } = (_, _, _) => false;
        public Func<bool, PacketType, byte[], byte[]> Mangle { get; set; } = (_, _, bytes) => bytes;

        public List<BulkDelivery> DeliveredAtB { get; } = [];
        public List<BulkResult> FinishedAtA { get; } = [];
        public List<string> NotesAtB { get; } = [];
        public List<(PacketType type, byte[] payload)> SentToB { get; } = [];
        public List<(PacketType type, byte[] payload)> SentToA { get; } = [];

        public int ChunksSentToB => SentToB.Count(p => p.type == PacketType.BulkChunk);

        public Pair()
        {
            A = new BulkChannel((type, payload) =>
            {
                SentToB.Add((type, payload.ToArray()));
                _inFlight.Add((true, type, payload.ToArray()));
            });
            B = new BulkChannel((type, payload) =>
            {
                SentToA.Add((type, payload.ToArray()));
                _inFlight.Add((false, type, payload.ToArray()));
            });

            A.Finished += FinishedAtA.Add;
            B.Delivered += DeliveredAtB.Add;
            B.Note += NotesAtB.Add;
        }

        /// <summary>Runs the virtual clock in 20 ms steps, the same cadence the app ticks at.</summary>
        public void Run(int seconds)
        {
            Deliver();
            for (var step = 0; step < seconds * 50; step++)
            {
                Now = Now.AddMilliseconds(20);
                A.Tick(Now);
                B.Tick(Now);
                Deliver();
            }
        }

        private void Deliver()
        {
            // Bounded: handling a packet can produce more, and a test that never settles
            // should fail on its assertion rather than hang.
            for (var round = 0; round < 8 && _inFlight.Count > 0; round++)
            {
                var batch = _inFlight.ToArray();
                _inFlight.Clear();

                foreach (var (toB, type, payload) in batch)
                {
                    if (Drop(toB, type, payload)) continue;
                    var bytes = Mangle(toB, type, payload);
                    var target = toB ? B : A;
                    target.OnPacket(type, bytes, Now);
                    if (Duplicate(toB, type, payload)) target.OnPacket(type, bytes, Now);
                }
            }
        }
    }
}
