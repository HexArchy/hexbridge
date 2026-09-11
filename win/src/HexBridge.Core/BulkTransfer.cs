using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HexBridge;

/// <summary>
/// What a bulk object is for. The kind is what lets one reliable channel carry several
/// features: the clipboard today, file transfer later, with no new packet types.
/// </summary>
public enum BulkKind : byte
{
    Clipboard = 1,

    /// <summary>A file somebody dropped on the other machine's window.</summary>
    File = 2,
}

/// <summary>How to interpret the bytes. Anything outside this list travels as <see cref="Opaque"/>.</summary>
public enum BulkFormat : byte
{
    Utf8Text = 1,
    Png = 2,
    Opaque = 3,
}

/// <summary>
/// The numbers docs/PROTOCOL.md fixes for «Надёжная передача крупных объектов». They are
/// constants rather than settings on purpose: both ends have to agree on every one of them,
/// and a knob is a way for them to disagree.
/// </summary>
public static class Bulk
{
    /// <summary>1024 bytes, so a chunk packet stays far below a typical MTU even with the
    /// 24-byte header, the 16-byte tag and the 8 bytes of chunk framing.</summary>
    public const int ChunkSize = 1024;

    /// <summary>
    /// 64 MiB, and only the clipboard is held to it. A clipboard object lives whole in
    /// memory at both ends — the machine taking it has to hand the bytes to the clipboard,
    /// which is not something that can be streamed — and a clipboard that large is a
    /// mistake rather than a use.
    ///
    /// <para>Must match <c>Bulk.maxClipboardSize</c> on the Mac: the machine taking an
    /// object refuses an offer larger than its own ceiling, so a mismatch is a transfer that
    /// silently never happens in one direction.</para>
    /// </summary>
    public const int MaxClipboardSize = 64 * 1024 * 1024;

    /// <summary>
    /// 4 GiB less one byte: the largest number the offer's <c>u32</c> size field can carry.
    /// The chunk count of such an object — 4 194 304 — still fits its own <c>u32</c>, and
    /// nothing else in the format has to change for it.
    ///
    /// <para>A file is never held whole in memory: one being sent is read off disk a chunk
    /// at a time, one arriving is written into a temporary file at each chunk's offset. So
    /// the format's own limit is the only one left to impose.</para>
    /// </summary>
    public const uint MaxFileSize = uint.MaxValue;

    /// <summary>
    /// The ceiling for one kind. A kind this build does not know — the byte comes off the
    /// wire — gets the smaller of the two: whatever it turns out to be, nothing here knows
    /// where to stream it, so it has to fit in memory.
    /// </summary>
    public static long MaxSizeFor(BulkKind kind) => kind == BulkKind.File ? MaxFileSize : MaxClipboardSize;

    /// <summary>How many missing chunk numbers a single ack may list after the first one.</summary>
    public const int MaxMissingListed = 256;

    /// <summary>An offer is repeated at most this many times before the transfer is given up.</summary>
    public const int MaxOfferAttempts = 10;

    /// <summary>
    /// The description is only ever shown in a UI, and the whole ack/offer packet has to fit
    /// the MTU. 256 bytes is well inside both.
    /// </summary>
    public const int MaxDescriptionBytes = 256;

    public static readonly TimeSpan OfferInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan AckInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Not in the contract: how long a half-finished incoming transfer is kept before the
    /// receiver forgets it. Without it a sender that dies mid-transfer would pin its bytes
    /// on the other machine forever.
    /// </summary>
    public static readonly TimeSpan IncomingIdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Also not in the contract: the sender gives up if the acks stop while it still has
    /// chunks outstanding. Retransmission is driven entirely by the receiver's 200 ms ack,
    /// so silence means the transfer can never finish on its own.
    /// </summary>
    public static readonly TimeSpan SendingSilenceTimeout = TimeSpan.FromSeconds(15);

    // MARK: - Pacing
    //
    // «Темп — местное решение, а не договор»: the format says nothing about how fast chunks
    // may leave, only what happens when they leave too fast — the missing lists come back
    // longer. So the rate is not a constant here but a loop: it climbs while chunks are
    // landing and is cut when they are not. The numbers below are that loop's, and every one
    // of them is a guess that the loop itself corrects; they matter only for how long it
    // takes to find the right answer, never for what the right answer is.

    /// <summary>
    /// Where the rate starts, before anything is known about the path. Half a megabyte a
    /// second: slow enough that it cannot hurt a link nobody has measured yet, fast enough
    /// that the first second of a transfer is not thrown away finding that out.
    /// </summary>
    public const double StartChunksPerSecond = 500;

    /// <summary>
    /// The floor the cuts stop at. A link that cannot carry 200 KB/s is a link a transfer
    /// will not finish on anyway, and a rate that can fall to nothing is a transfer that
    /// hangs instead of failing.
    /// </summary>
    public const double MinChunksPerSecond = 200;

    /// <summary>
    /// The absolute top, whatever the path turns out to be. 120 000 chunks a second is
    /// roughly a saturated gigabit link; above that the rate is no longer what limits the
    /// transfer, and a number with no ceiling at all is a loop that keeps doubling into a
    /// socket that stopped accepting long ago.
    /// </summary>
    public const double MaxChunksPerSecond = 120_000;

    /// <summary>
    /// Before the first hole, the rate doubles once per <see cref="AckInterval"/> — the
    /// interval is the shortest time in which the other end can tell us anything at all, so
    /// it is the shortest honest control loop there is. Doubling reaches a gigabit link in
    /// under two seconds; the additive climb below would take two minutes, which on a
    /// four-gigabyte file is two minutes of a fast link left unused.
    /// </summary>
    public const double RiseFactor = 2.0;

    /// <summary>
    /// After the first hole the climb is additive: the path has shown roughly where its
    /// limit is, and doubling past it again would spend the next minute losing chunks in
    /// order to find out what it already knows. A megabyte a second per second, against a
    /// halving that can happen every <see cref="BackoffPause"/> — so a link that keeps
    /// losing chunks settles at a few hundred a second and crawls, which is the honest
    /// answer for a link that is dropping a third of everything, and one that stops losing
    /// them is back at a megabyte a second within a second.
    /// </summary>
    public const double RiseChunksPerSecond = 1000;

    /// <summary>
    /// The multiplicative decrease, and the classic one. Halving is what makes the loop
    /// settle instead of oscillate: a cut has to give back more than the climb takes,
    /// or the rate spends its life above the path's limit — which is exactly the state
    /// where every lost chunk is sent twice and «faster» means slower.
    /// </summary>
    public const double FallFactor = 0.5;

    /// <summary>
    /// How long after a cut the next one waits. A hole named in one ack is usually still
    /// named in the next: the chunk that fills it has not crossed yet. Without the pause,
    /// one loss would be counted three or four times over and halve the rate into the
    /// floor. Two ack intervals is the shortest pause that covers the round trip.
    /// </summary>
    public static readonly TimeSpan BackoffPause = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// After this long with nothing going out, the pacing forgets what it learned and
    /// starts again from <see cref="StartChunksPerSecond"/>. What it learned was about a
    /// path, and a link that has been quiet for five seconds may be on another one — a
    /// laptop that moved rooms, a relay that took over from a direct route. Coming back at
    /// the rate the last transfer ended on would spend the first second of the next one
    /// flooding something that was never measured.
    /// </summary>
    public static readonly TimeSpan PacingIdle = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What a relay carries from one address before it starts dropping, unless the config
    /// says otherwise.
    ///
    /// <para>
    /// This is the relay's own default, which was raised to 20 000 the moment a file
    /// started riding the same socket — 2000 was sized for a voice call and a gamepad and
    /// would hold a transfer through a relay to about 1.5 MB/s. A machine here cannot see
    /// how the relay it dials was started, so it assumes the current default and someone
    /// who set a different one says so in the config.
    /// </para>
    ///
    /// <para>
    /// Assuming the higher number is the safe direction to be wrong in: against an older
    /// relay that still carries 2000, the loss this causes is exactly what the backoff
    /// reads, and the rate settles where the path actually is. Assuming the lower number
    /// has no such feedback — it is simply slow, for ever, and nothing says why.
    /// </para>
    /// </summary>
    public const double RelayPacketsPerSecond = 20_000;

    /// <summary>
    /// Kept clear of the bulk rate on a relay. Voice is 51 packets a second, a forwarded
    /// gamepad another 250, and the acks coming back share the same budget. Aiming at the
    /// whole allowance would take the call down to move a file a little faster — and not
    /// even that, because what the relay drops comes back as a hole and is sent again.
    /// </summary>
    public const double RelayReserve = 500;

    /// <summary>
    /// The highest rate this machine will aim at: the configured ceiling, what a relay in
    /// the path will carry, and the absolute top, whichever is lowest.
    /// </summary>
    /// <param name="configured">Chunks per second from the config; zero or less means none.</param>
    /// <param name="relayPacketsPerSecond">
    /// What the relay in the path carries, or null when there is no relay in the path.
    /// </param>
    public static double CeilingFor(double configured, double? relayPacketsPerSecond)
    {
        var ceiling = configured > 0 ? Math.Min(configured, MaxChunksPerSecond) : MaxChunksPerSecond;
        if (relayPacketsPerSecond is { } relay)
        {
            // Never below the floor: a relay configured absurdly low would otherwise stop
            // file transfer altogether rather than make it slow.
            ceiling = Math.Min(ceiling, Math.Max(MinChunksPerSecond, relay - RelayReserve));
        }
        return ceiling;
    }

    public const int OfferHeaderSize = 48;
    public const int ChunkHeaderSize = 8;
    public const int AckHeaderSize = 11;

    /// <summary>
    /// What a half-received object is called while it is still arriving.
    ///
    /// <para>
    /// The transfer id is in the name, so two objects on the way at once cannot collide,
    /// and nothing of the name the other machine sent is in it — that name is not to be
    /// trusted, and this file is created before anybody has decided what it may become. The
    /// extension is what a sweep at startup looks for: a machine that loses power in the
    /// middle of a transfer must not leave the remains in somebody's Downloads folder for
    /// ever.
    /// </para>
    /// </summary>
    public const string PartialExtension = ".hexpart";

    public static string PartialName(uint transferId) =>
        "hexbridge-" + transferId.ToString("x8", CultureInfo.InvariantCulture) + PartialExtension;

    public static uint ChunkCountFor(long size) => (uint)((size + ChunkSize - 1) / ChunkSize);

    /// <summary>Length of chunk <paramref name="index"/> of an object of <paramref name="size"/> bytes.</summary>
    public static int ChunkLength(long size, uint index)
    {
        var start = (long)index * ChunkSize;
        if (start >= size) return 0;
        return (int)Math.Min(ChunkSize, size - start);
    }
}

/// <summary>The payload of one BULK_OFFER.</summary>
public sealed record BulkOffer(
    uint TransferId,
    BulkKind Kind,
    BulkFormat Format,
    uint Size,
    uint ChunkCount,
    byte[] Hash,
    string Description);

/// <summary>The payload of one BULK_ACK.</summary>
public sealed record BulkAck(uint TransferId, bool Accepted, uint FirstMissing, uint[] Missing)
{
    /// <summary>
    /// The receiver had more holes than one packet can name. The contract's answer to that
    /// is not a second ack but «начать заново с первого недостающего», and a full list is
    /// the only way the sender can tell the two cases apart.
    /// </summary>
    public bool Truncated => Missing.Length >= Bulk.MaxMissingListed;
}

/// <summary>
/// Byte-for-byte codecs for the four bulk packet payloads in docs/PROTOCOL.md, with no I/O
/// anywhere near them so every branch is reachable from a test.
/// </summary>
public static class BulkCodec
{
    // MARK: - BULK_OFFER

    public static byte[] WriteOffer(BulkOffer offer)
    {
        if (offer.Hash.Length != 32) throw new ArgumentException("SHA-256 — это 32 байта", nameof(offer));

        var description = Truncate(offer.Description);
        var bytes = new byte[Bulk.OfferHeaderSize + description.Length];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes, offer.TransferId);
        bytes[4] = (byte)offer.Kind;
        bytes[5] = (byte)offer.Format;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6), offer.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), offer.ChunkCount);
        offer.Hash.CopyTo(bytes, 14);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(46), (ushort)description.Length);
        description.CopyTo(bytes, Bulk.OfferHeaderSize);
        return bytes;
    }

    public static bool TryReadOffer(ReadOnlySpan<byte> payload, out BulkOffer offer)
    {
        offer = null!;
        if (payload.Length < Bulk.OfferHeaderSize) return false;

        int descriptionLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[46..]);
        if (payload.Length < Bulk.OfferHeaderSize + descriptionLength) return false;

        offer = new BulkOffer(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            (BulkKind)payload[4],
            (BulkFormat)payload[5],
            BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[10..]),
            payload.Slice(14, 32).ToArray(),
            Encoding.UTF8.GetString(payload.Slice(Bulk.OfferHeaderSize, descriptionLength)));
        return true;
    }

    // MARK: - BULK_CHUNK

    public static byte[] WriteChunk(uint transferId, uint index, ReadOnlySpan<byte> data)
    {
        var bytes = new byte[Bulk.ChunkHeaderSize + data.Length];
        WriteChunkHeader(bytes, transferId, index);
        data.CopyTo(bytes.AsSpan(Bulk.ChunkHeaderSize));
        return bytes;
    }

    /// <summary>
    /// The eight bytes in front of the data, written into a buffer somebody else owns.
    ///
    /// <para>
    /// A four-gigabyte object is four million chunk packets. Building each one as its own
    /// array, filling it from a second array read off disk, is two allocations per packet
    /// and eight gigabytes through the collector for one transfer — so the chunk is read
    /// straight into the packet buffer and this writes the header around it.
    /// </para>
    /// </summary>
    public static void WriteChunkHeader(Span<byte> packet, uint transferId, uint index)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(packet, transferId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[4..], index);
    }

    /// <summary>
    /// The data stays a span into the caller's packet: copying it out would be an
    /// allocation for every chunk that arrives, and the only thing waiting for it is a
    /// write to disk or a memcpy that can take a span as happily as an array.
    /// </summary>
    public static bool TryReadChunk(ReadOnlySpan<byte> payload, out uint transferId, out uint index, out ReadOnlySpan<byte> data)
    {
        transferId = 0;
        index = 0;
        data = default;
        if (payload.Length < Bulk.ChunkHeaderSize) return false;

        transferId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        index = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        data = payload[Bulk.ChunkHeaderSize..];
        return true;
    }

    // MARK: - BULK_ACK

    public static byte[] WriteAck(BulkAck ack)
    {
        var listed = Math.Min(ack.Missing.Length, Bulk.MaxMissingListed);
        var bytes = new byte[Bulk.AckHeaderSize + 4 * listed];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes, ack.TransferId);
        bytes[4] = ack.Accepted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), ack.FirstMissing);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(9), (ushort)listed);
        for (var i = 0; i < listed; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(Bulk.AckHeaderSize + 4 * i), ack.Missing[i]);
        }
        return bytes;
    }

    public static bool TryReadAck(ReadOnlySpan<byte> payload, out BulkAck ack)
    {
        ack = null!;
        if (payload.Length < Bulk.AckHeaderSize) return false;

        int listed = BinaryPrimitives.ReadUInt16LittleEndian(payload[9..]);
        if (listed > Bulk.MaxMissingListed) return false;
        if (payload.Length < Bulk.AckHeaderSize + 4 * listed) return false;

        var missing = new uint[listed];
        for (var i = 0; i < listed; i++)
        {
            missing[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload[(Bulk.AckHeaderSize + 4 * i)..]);
        }

        ack = new BulkAck(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            payload[4] == 1,
            BinaryPrimitives.ReadUInt32LittleEndian(payload[5..]),
            missing);
        return true;
    }

    // MARK: - BULK_DONE

    public static byte[] WriteDone(uint transferId)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, transferId);
        return bytes;
    }

    public static bool TryReadDone(ReadOnlySpan<byte> payload, out uint transferId)
    {
        transferId = 0;
        if (payload.Length < 4) return false;
        transferId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        return true;
    }

    /// <summary>
    /// UTF-8, cut to <see cref="Bulk.MaxDescriptionBytes"/> without splitting a character —
    /// half a code point in a status line is worse than a shorter status line.
    /// </summary>
    private static byte[] Truncate(string description)
    {
        var bytes = Encoding.UTF8.GetBytes(description);
        if (bytes.Length <= Bulk.MaxDescriptionBytes) return bytes;

        var cut = Bulk.MaxDescriptionBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
        return bytes[..cut];
    }
}
