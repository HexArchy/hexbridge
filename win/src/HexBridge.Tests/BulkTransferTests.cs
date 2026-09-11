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
        Assert.Equal(64 * 1024 * 1024, Bulk.MaxClipboardSize);
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
        Assert.Equal(data, read.ToArray());

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
        Assert.Equal(65536u, Bulk.ChunkCountFor(Bulk.MaxClipboardSize));

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
        pair.B.Owns = (_, candidate) => candidate.SequenceEqual(hash);

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
    public void MoreHolesThanFitInOneAckAreAllFilledInTheEnd()
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

    /// <summary>
    /// An ack with a full list says «there may be more», and the answer to it must not be
    /// to send the object again. One chunk lost early costs one chunk sent twice — not a
    /// second pass over everything behind it, which on a four-gigabyte object would be four
    /// gigabytes every 200 ms.
    /// </summary>
    [Fact]
    public void AFullMissingListDoesNotMakeTheWholeObjectGoOutAgain()
    {
        var pair = new Pair();
        var dropped = false;
        pair.Drop = (toB, type, payload) =>
        {
            if (!toB || type != PacketType.BulkChunk || dropped || Index(payload) != 3) return false;
            dropped = true;
            return true;
        };

        // Long enough that every ack until the very end has a full list: until the last
        // chunk is on the wire, everything past the frontier is a hole the ack has no room
        // to name.
        var payload = Payload(2000 * Bulk.ChunkSize, seed: 30);
        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out _);
        pair.Run(seconds: 20);

        Assert.Equal(payload, Assert.Single(pair.DeliveredAtB).Bytes);
        Assert.True(dropped);
        // Measured on this harness: 2281 chunks the way it works now against 3021 for the
        // literal reading of «start over from the first missing chunk». The threshold sits
        // between the two, because what is being pinned is the strategy and not the exact
        // number — the rest of the excess is holes named while their chunks were still in
        // the air, which no strategy can tell from a loss.
        Assert.True(pair.ChunksSentToB < 2500, $"{pair.ChunksSentToB} chunks for an object of 2000");
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
    public void TheCeilingIsTheLastSizeThatFits()
    {
        var pair = new Pair();

        var ok = pair.A.Offer(BulkKind.Clipboard, BulkFormat.Opaque, new byte[Bulk.MaxClipboardSize], "предел", pair.Now, out var okError);
        Assert.NotNull(ok);
        Assert.Null(okError);

        var tooBig = pair.A.Offer(BulkKind.Clipboard, BulkFormat.Opaque, new byte[Bulk.MaxClipboardSize + 1], "перебор", pair.Now, out var error);
        Assert.Null(tooBig);
        Assert.Contains("64\u00a0МиБ", error);

        // The one that fits is offered with the chunk count the contract implies.
        var offer = pair.SentToB.Single(p => p.type == PacketType.BulkOffer);
        Assert.True(BulkCodec.TryReadOffer(offer.payload, out var read));
        Assert.Equal(65536u, read.ChunkCount);
        Assert.Equal((uint)Bulk.MaxClipboardSize, read.Size);
    }

    [Fact]
    public void AnOfferBiggerThanTheContractAllowsIsIgnoredByTheReceiver()
    {
        var pair = new Pair();
        var lies = BulkCodec.WriteOffer(new BulkOffer(
            1, BulkKind.Clipboard, BulkFormat.Opaque, (uint)Bulk.MaxClipboardSize + 1, 16385, new byte[32], "ложь"));

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

    // MARK: - Files, and the four gigabytes the format allows them

    [Fact]
    public void TheCeilingsAreTheOnesTheContractGivesEachKind()
    {
        Assert.Equal(64L * 1024 * 1024, Bulk.MaxSizeFor(BulkKind.Clipboard));
        Assert.Equal(uint.MaxValue, Bulk.MaxSizeFor(BulkKind.File));

        // 4 194 304 chunks, and the count still fits the u32 the offer carries.
        Assert.Equal(4_194_304u, Bulk.ChunkCountFor(Bulk.MaxFileSize));
    }

    /// <summary>
    /// The object is larger than the ceiling both ends used to hold in memory, which is the
    /// whole point: neither end holds it at all. It is read off one disk a chunk at a time
    /// and written to the other at each chunk's own offset, and what comes out is a file
    /// with the hash it was offered under.
    /// </summary>
    [Fact]
    public void AFileLargerThanTheClipboardCeilingCrossesIntact()
    {
        using var here = new TempFolder();
        using var there = new TempFolder();

        var pair = new Pair();
        pair.StageFilesAt(there.Path);

        // A few chunks past the old 64 MiB ceiling. Bigger would prove no more and cost
        // the suite a minute.
        var path = here.Write("big.bin", Bulk.MaxClipboardSize + 3000, seed: 21);
        pair.A.Offer(BulkKind.File, BulkFormat.Opaque, BulkSource.FromFile(path), "big.bin", pair.Now, out var error);
        Assert.Null(error);

        pair.Run(seconds: 30);

        var delivery = Assert.Single(pair.DeliveredAtB);
        // Not held: this is the assertion that the streaming path was the one taken, and
        // it is a better one than any number a test could read off the heap.
        Assert.Null(delivery.Bytes);
        Assert.NotNull(delivery.Path);
        Assert.Equal(Hash(path), Hash(delivery.Path));
        Assert.Equal(delivery.Hash, Hash(path));
        Assert.Equal(BulkOutcome.Delivered, Assert.Single(pair.FinishedAtA).Outcome);
    }

    /// <summary>
    /// Half-way through, the object is on disk and nowhere else — a file of its full length
    /// under a name that says it is not finished, and not one byte of it in the channel.
    /// </summary>
    [Fact]
    public void AFileBeingAssembledIsOnDiskRatherThanInMemory()
    {
        using var here = new TempFolder();
        using var there = new TempFolder();

        var pair = new Pair();
        pair.StageFilesAt(there.Path);
        // The last chunk never arrives, so the transfer stays half-finished for as long as
        // the test wants to look at it.
        pair.Drop = (toB, type, payload) => toB && type == PacketType.BulkChunk && Index(payload) == 99;

        var path = here.Write("half.bin", 100 * Bulk.ChunkSize, seed: 22);
        pair.A.Offer(BulkKind.File, BulkFormat.Opaque, BulkSource.FromFile(path), "half.bin", pair.Now, out _);
        pair.Run(seconds: 2);

        Assert.Empty(pair.DeliveredAtB);
        var partial = Assert.Single(Directory.GetFiles(there.Path, "*" + Bulk.PartialExtension));
        // Its full length from the moment it was created: a disk with no room for the
        // object says so at the offer rather than at ninety-nine per cent.
        Assert.Equal(100L * Bulk.ChunkSize, new FileInfo(partial).Length);

        var incoming = Assert.Single(pair.B.Progress());
        Assert.Equal(99u, incoming.ChunksDone);
        Assert.Equal(100u, incoming.ChunkCount);
    }

    /// <summary>
    /// Chunks arriving backwards and twice over: the bitmap has to answer «this one is
    /// already here» the second time, and the hash — taken as the object fills — has to
    /// come out the same as if they had arrived in order.
    /// </summary>
    [Fact]
    public void AFileSurvivesItsChunksArrivingBackwardsAndTwiceOver()
    {
        using var here = new TempFolder();
        using var there = new TempFolder();

        var pair = new Pair();
        pair.StageFilesAt(there.Path);
        pair.Duplicate = (toB, type, _) => toB && type == PacketType.BulkChunk;
        pair.Reverse = true;

        var path = here.Write("jumbled.bin", (300 * Bulk.ChunkSize) + 17, seed: 23);
        pair.A.Offer(BulkKind.File, BulkFormat.Opaque, BulkSource.FromFile(path), "jumbled.bin", pair.Now, out _);
        pair.Run(seconds: 10);

        var delivery = Assert.Single(pair.DeliveredAtB);
        Assert.Equal(Hash(path), Hash(delivery.Path!));
    }

    [Fact]
    public void AnAbandonedTransferTakesItsHalfWrittenFileWithIt()
    {
        using var here = new TempFolder();
        using var there = new TempFolder();

        var pair = new Pair();
        pair.StageFilesAt(there.Path);
        pair.Drop = (toB, type, _) => toB && type == PacketType.BulkChunk;

        var path = here.Write("gone.bin", 50 * Bulk.ChunkSize, seed: 24);
        pair.A.Offer(BulkKind.File, BulkFormat.Opaque, BulkSource.FromFile(path), "gone.bin", pair.Now, out _);
        pair.Run(seconds: 2);
        Assert.NotEmpty(Directory.GetFiles(there.Path, "*" + Bulk.PartialExtension));

        // Nothing for thirty seconds: the transfer is forgotten, and what it was writing
        // into must not be left in somebody's Downloads folder under a name they have
        // never seen.
        pair.Run(seconds: 31);
        Assert.Empty(Directory.GetFiles(there.Path));
    }

    [Fact]
    public void ResetTakesTheHalfWrittenFileWithItToo()
    {
        using var here = new TempFolder();
        using var there = new TempFolder();

        var pair = new Pair();
        pair.StageFilesAt(there.Path);
        pair.Drop = (toB, type, _) => toB && type == PacketType.BulkChunk;

        var path = here.Write("dropped.bin", 50 * Bulk.ChunkSize, seed: 25);
        pair.A.Offer(BulkKind.File, BulkFormat.Opaque, BulkSource.FromFile(path), "dropped.bin", pair.Now, out _);
        pair.Run(seconds: 2);
        Assert.NotEmpty(Directory.GetFiles(there.Path, "*" + Bulk.PartialExtension));

        pair.B.Reset();
        Assert.Empty(Directory.GetFiles(there.Path));
    }

    /// <summary>
    /// The clipboard shares the channel and keeps its own arrangement: held in memory, at
    /// most 64 MiB, and nothing of it on disk even while a folder is configured for files.
    /// </summary>
    [Fact]
    public void TheClipboardStillArrivesInMemoryOnTheSameChannel()
    {
        using var there = new TempFolder();

        var pair = new Pair();
        pair.StageFilesAt(there.Path);

        var payload = Payload(40_000, seed: 26);
        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Png, payload, "снимок", pair.Now, out _);
        pair.Run(seconds: 3);

        var delivery = Assert.Single(pair.DeliveredAtB);
        Assert.Equal(payload, delivery.Bytes);
        Assert.Null(delivery.Path);
        Assert.Empty(Directory.GetFiles(there.Path));
    }

    /// <summary>
    /// An object too large to hold, offered to a kind with nowhere to stream it, is not
    /// answered at all: the alternative is allocating gigabytes in order to throw them away.
    /// </summary>
    [Fact]
    public void AnObjectTooBigToHoldWithNowhereToStreamItIsNotAnswered()
    {
        var pair = new Pair();
        var size = (uint)Bulk.MaxClipboardSize + 1024;
        var lies = BulkCodec.WriteOffer(new BulkOffer(
            1, BulkKind.File, BulkFormat.Opaque, size, Bulk.ChunkCountFor(size), new byte[32], "огромный"));

        pair.B.OnPacket(PacketType.BulkOffer, lies, pair.Now);
        pair.Run(seconds: 1);

        Assert.Empty(pair.SentToA);
    }

    // MARK: - Pacing

    [Fact]
    public void TheCeilingIsTheLowestOfTheConfiguredOneAndTheRelaysOwn()
    {
        Assert.Equal(Bulk.MaxChunksPerSecond, Bulk.CeilingFor(0, null));
        Assert.Equal(4000, Bulk.CeilingFor(4000, null));

        // The relay carries 2000 packets a second and the call and the gamepad on it are
        // not ours to take.
        Assert.Equal(2000 - Bulk.RelayReserve, Bulk.CeilingFor(0, 2000));
        Assert.Equal(1000, Bulk.CeilingFor(1000, 2000));
        Assert.Equal(2000 - Bulk.RelayReserve, Bulk.CeilingFor(4000, 2000));

        // A relay configured absurdly low makes transfers slow, not impossible.
        Assert.Equal(Bulk.MinChunksPerSecond, Bulk.CeilingFor(0, 100));
    }

    /// <summary>
    /// The loop the contract asks for: faster while everything is landing, slower the
    /// moment the missing lists say it is not, and faster again once they stop.
    /// </summary>
    [Fact]
    public void TheRateClimbsWhileChunksLandFallsOnLossAndClimbsBackAfterwards()
    {
        var pair = new Pair();
        // A low ceiling so the object is still going when the third phase starts, and so
        // the numbers a person reading this test has to hold are small.
        pair.A.ChunkRateCeiling = 1000;

        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Opaque, Payload(20_000 * Bulk.ChunkSize, seed: 27), "большой", pair.Now, out _);

        pair.Run(seconds: 2);
        var climbed = pair.A.ChunksPerSecond;
        Assert.Equal(1000, climbed);

        // Every third chunk is lost. The holes come back in the acks, which is the only
        // signal the format has, and the rate has to come down on it.
        //
        // Sampled as it goes rather than read at the end: a cut and the climb after it are
        // a sawtooth, and where the last tick happens to fall on that tooth is not what
        // this test is about.
        var random = new Random(28);
        pair.Drop = (toB, type, _) => toB && type == PacketType.BulkChunk && random.Next(3) == 0;
        var backedOff = climbed;
        for (var sample = 0; sample < 30; sample++)
        {
            pair.RunSteps(5);
            backedOff = Math.Min(backedOff, pair.A.ChunksPerSecond);
        }
        Assert.True(backedOff <= climbed / 2, $"{backedOff} never came down from {climbed}");

        pair.Drop = (_, _, _) => false;
        pair.Run(seconds: 4);
        Assert.True(pair.A.ChunksPerSecond > backedOff, $"{pair.A.ChunksPerSecond} did not climb back from {backedOff}");
    }

    /// <summary>
    /// Pacing is a ceiling and not a promise of speed, so the one thing that must always
    /// hold is that nothing goes out faster than the ceiling says.
    /// </summary>
    [Fact]
    public void NothingGoesOutFasterThanTheCeiling()
    {
        var pair = new Pair();
        pair.A.ChunkRateCeiling = 500;
        pair.A.Offer(BulkKind.Clipboard, BulkFormat.Opaque, Payload(20_000 * Bulk.ChunkSize, seed: 29), "большой", pair.Now, out _);

        pair.Run(seconds: 4);

        // Four seconds at 500 a second, and the first of them was spent on the offer and
        // the first ack. A margin of one tick's worth, because a token bucket may hand out
        // a tick of credit it saved.
        Assert.True(pair.ChunksSentToB <= 4 * 500 + 25, $"{pair.ChunksSentToB} chunks in four seconds");
    }

    // MARK: - The bitmap under it

    [Fact]
    public void TheBitmapAnswersTheQuestionsAnAckAsks()
    {
        var map = new ChunkBitmap(100);

        Assert.Equal(0u, map.FirstMissing());
        Assert.False(map.IsFull);

        Assert.True(map.Add(0));
        // The same chunk twice is the wire repeating itself, not a second chunk.
        Assert.False(map.Add(0));
        Assert.Equal(1u, map.Have);
        Assert.Equal(1u, map.FirstMissing());

        for (uint i = 1; i < 100; i++) map.Add(i);
        Assert.True(map.IsFull);
        // One past the last chunk, which is the contract's «ничего не нужно».
        Assert.Equal(100u, map.FirstMissing());

        map.Clear();
        Assert.Equal(0u, map.Have);
        Assert.Equal(0u, map.FirstMissing());
    }

    [Fact]
    public void TheBitmapNamesHolesInOrderAndNoMoreThanAskedFor()
    {
        var map = new ChunkBitmap(1000);
        for (uint i = 0; i < 1000; i++)
        {
            if (i is not (7 or 9 or 500 or 999)) map.Add(i);
        }

        Assert.Equal(7u, map.FirstMissing());

        Span<uint> listed = stackalloc uint[Bulk.MaxMissingListed];
        var count = map.ListMissing(7, listed);
        Assert.Equal(3, count);
        Assert.Equal(new uint[] { 9, 500, 999 }, listed[..count].ToArray());

        // And never more than the caller has room for, which is what caps the ack.
        Span<uint> two = stackalloc uint[2];
        Assert.Equal(2, map.ListMissing(7, two));
    }

    /// <summary>
    /// A count that is not a multiple of 64 leaves bits in the last word that belong to no
    /// chunk. Counting them as holes would put a chunk number past the end of the object in
    /// every ack, and the other end would spend the transfer being asked for it.
    /// </summary>
    [Fact]
    public void TheBitmapDoesNotInventHolesPastTheLastChunk()
    {
        var map = new ChunkBitmap(70);
        for (uint i = 0; i < 70; i++) map.Add(i);

        Assert.True(map.IsFull);
        Assert.Equal(70u, map.FirstMissing());

        Span<uint> listed = stackalloc uint[Bulk.MaxMissingListed];
        Assert.Equal(0, map.ListMissing(0, listed));
    }

    // MARK: - Harness

    private static byte[] Payload(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Hash(string path)
    {
        using var file = File.OpenRead(path);
        return SHA256.HashData(file);
    }

    /// <summary>The chunk number inside a BULK_CHUNK payload, for a test that drops one.</summary>
    private static uint Index(byte[] payload) => BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));

    /// <summary>A folder of this test's own, and whatever it wrote in it, gone at the end.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "hexbridge-bulk", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        /// <summary>A file of <paramref name="size"/> bytes nobody could guess the contents of.</summary>
        public string Write(string name, int size, int seed)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, Payload(size, seed));
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A temporary directory that outlives the run is not a failed test.
            }
        }
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

        /// <summary>
        /// Hands each batch to the other end backwards. Not something a link does on
        /// purpose, but it is the shortest way to a hole that is filled from behind.
        /// </summary>
        public bool Reverse { get; set; }

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

        /// <summary>
        /// Files arriving at B are streamed into <paramref name="folder"/>, which is what
        /// the files feature does with the folder they are going to land in. Every other
        /// kind stays in memory, which is what the clipboard needs.
        /// </summary>
        public void StageFilesAt(string folder) =>
            B.Staging = kind => kind == BulkKind.File ? folder : null;

        /// <summary>Runs the virtual clock in 20 ms steps, the same cadence the app ticks at.</summary>
        public void Run(int seconds) => RunSteps(seconds * 50);

        /// <summary>The same, counted in ticks, for a test that wants to look in between.</summary>
        public void RunSteps(int steps)
        {
            Deliver();
            for (var step = 0; step < steps; step++)
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
                if (Reverse) Array.Reverse(batch);

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
