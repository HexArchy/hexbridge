using System.Buffers.Binary;
using System.Text;

namespace HexBridge;

/// <summary>
/// What a bulk object is for. The kind is what lets one reliable channel carry several
/// features: the clipboard today, file transfer later, with no new packet types.
/// </summary>
public enum BulkKind : byte
{
    Clipboard = 1,
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

    /// <summary>16 MiB. Anything larger is file transfer, which will get a kind of its own.</summary>
    public const int MaxObjectSize = 16 * 1024 * 1024;

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

    public const int OfferHeaderSize = 48;
    public const int ChunkHeaderSize = 8;
    public const int AckHeaderSize = 11;

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
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, transferId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), index);
        data.CopyTo(bytes.AsSpan(Bulk.ChunkHeaderSize));
        return bytes;
    }

    public static bool TryReadChunk(ReadOnlySpan<byte> payload, out uint transferId, out uint index, out byte[] data)
    {
        transferId = 0;
        index = 0;
        data = [];
        if (payload.Length < Bulk.ChunkHeaderSize) return false;

        transferId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        index = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        data = payload[Bulk.ChunkHeaderSize..].ToArray();
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
