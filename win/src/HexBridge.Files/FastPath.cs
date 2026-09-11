using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HexBridge.Files;

/// <summary>
/// The framing of docs/PROTOCOL.md, «The fast path for files (TCP, data port + 2)», and
/// nothing else. No sockets and no disk are anywhere near it, so every branch of the format
/// — including the ones a healthy connection never takes — is reachable from a test.
///
/// <para>
/// The path exists for one measurement: the chunked UDP channel moved a file between a Mac
/// and a Windows VM at 2.8 MB/s and a plain TCP stream between the same two machines moved
/// one at 370 MB/s. Nothing here replaces what the UDP channel guarantees; it sidesteps the
/// need for them. TCP already delivers every byte, in order, once, so there is no offer, no
/// acknowledgement, no bitmap and no pacing on this path — one write hands over sixty-four
/// kilobytes where the other path hands over one.
/// </para>
/// </summary>
public static class FastPath
{
    /// <summary>
    /// Magic (4) plus version (1) plus room (8), all in the clear.
    ///
    /// <para>
    /// In the clear for the same reason the UDP header is: something has to be readable
    /// before there is a key to read with. It buys a stranger nothing — everything after it
    /// is sealed under the pairing key, and a connection that cannot produce an opening
    /// record is dropped before a byte of any file is written.
    /// </para>
    /// </summary>
    public const int PrologueSize = 13;

    /// <summary>The most plaintext one record carries, which the contract fixes at 64 KiB.</summary>
    public const int MaxRecordPlaintext = 64 * 1024;

    /// <summary>
    /// Each record is preceded by the length of its sealed form, LE u32.
    ///
    /// <para>
    /// A stream has no packet boundaries. The contract fixes the sealing and the nonce, but
    /// something still has to say where one record stops, and only a length in front can:
    /// the end of a file is itself a record — an empty one — so «nothing more arrived» has
    /// to be distinguishable from «the connection died», and a reader that guessed at
    /// boundaries could not tell those apart.
    /// </para>
    /// </summary>
    public const int LengthSize = 4;

    /// <summary>The largest sealed record: full plaintext plus the GCM tag.</summary>
    public const int MaxRecordOnWire = MaxRecordPlaintext + Wire.TagSize;

    /// <summary>
    /// The opening record's nonce. Data records are numbered from one and a number is never
    /// reused, which is what makes the record number safe as a nonce: one connection carries
    /// one file.
    /// </summary>
    public const ulong OpeningRecord = 0;

    /// <summary>
    /// How long a file waits for the stream before it goes the slow way instead. The
    /// contract's three seconds: long enough to cross a network that is merely busy, short
    /// enough that somebody who dropped a file does not think nothing happened.
    /// </summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The shortest opening record that could be real: a length, no name, a size and a hash.
    /// </summary>
    private const int MinOpeningBody = 2 + 4 + 32;

    /// <summary>The byte that says «somebody is really here», and its framed size.</summary>
    public const byte ReadyByte = 0x01;

    /// <summary>Length prefix, one sealed byte, tag.</summary>
    public const int ReadyFrameSize = LengthSize + 1 + Wire.TagSize;

    /// <summary>How long the sender waits to hear it. The contract's two seconds.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The answer to an opening record, framed and ready to write.</summary>
    public static byte[] ReadyRecord(AesGcm aes)
    {
        var frame = new byte[ReadyFrameSize];
        Seal(aes, OpeningRecord, [ReadyByte], frame, backwards: true);
        return frame;
    }

    /// <summary>
    /// Whether a framed answer is the ready byte under this key. Everything else — a short
    /// frame, a length that disagrees with it, a record that will not open, any other
    /// plaintext — means the same thing: nobody holding the key is there.
    /// </summary>
    public static bool IsReady(AesGcm aes, ReadOnlySpan<byte> frame)
    {
        if (frame.Length != ReadyFrameSize) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(frame) != ReadyFrameSize - LengthSize) return false;

        Span<byte> plaintext = stackalloc byte[1];
        if (Open(aes, OpeningRecord, frame[LengthSize..], plaintext, backwards: true) != 1) return false;
        return plaintext[0] == ReadyByte;
    }

    /// <summary>Writes the 13 plaintext bytes every connection starts with.</summary>
    public static void WritePrologue(Span<byte> dst, ulong room)
    {
        Wire.Magic.CopyTo(dst);
        dst[4] = Wire.Version;
        BinaryPrimitives.WriteUInt64LittleEndian(dst[5..], room);
    }

    /// <summary>
    /// True when the prologue is ours and names the room we are in. A connection that fails
    /// this is somebody else's — or a port scanner — and is dropped without an answer.
    /// </summary>
    public static bool PrologueMatches(ReadOnlySpan<byte> src, ulong room)
    {
        if (src.Length < PrologueSize) return false;
        if (!src[..4].SequenceEqual(Wire.Magic)) return false;
        if (src[4] != Wire.Version) return false;

        return BinaryPrimitives.ReadUInt64LittleEndian(src[5..]) == room;
    }

    /// <summary>
    /// The plaintext of the opening record: name length LE u16, name UTF-8, size LE u32,
    /// SHA-256.
    /// </summary>
    public static byte[] Opening(string name, uint size, ReadOnlySpan<byte> hash)
    {
        var utf8 = Encoding.UTF8.GetBytes(name);
        var body = new byte[2 + utf8.Length + 4 + 32];

        BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)utf8.Length);
        utf8.CopyTo(body, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2 + utf8.Length), size);
        hash.CopyTo(body.AsSpan(2 + utf8.Length + 4));
        return body;
    }

    /// <summary>
    /// Reads an opening record back. False for anything that does not measure up, which is
    /// the same answer as «this is not a file being offered» — the name is read but never
    /// believed, and what is allowed to become a file on this disk is
    /// <see cref="FileNames"/>'s decision alone.
    /// </summary>
    public static bool TryReadOpening(ReadOnlySpan<byte> body, out string name, out uint size, out byte[] hash)
    {
        name = "";
        size = 0;
        hash = [];
        if (body.Length < MinOpeningBody) return false;

        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(body);
        if (body.Length != MinOpeningBody + nameLength) return false;

        name = Encoding.UTF8.GetString(body.Slice(2, nameLength));
        size = BinaryPrimitives.ReadUInt32LittleEndian(body[(2 + nameLength)..]);
        hash = body[(2 + nameLength + 4)..].ToArray();
        return true;
    }

    /// <summary>
    /// Seals one record into <paramref name="frame"/> as length LE u32, ciphertext, tag, and
    /// answers how many bytes that was. The caller's buffer must hold
    /// <see cref="LengthSize"/> + plaintext + <see cref="Wire.TagSize"/>.
    /// </summary>
    public static int Seal(
        AesGcm aes, ulong record, ReadOnlySpan<byte> plaintext, Span<byte> frame,
        bool backwards = false)
    {
        var sealedLength = plaintext.Length + Wire.TagSize;
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)sealedLength);

        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, record, backwards);

        aes.Encrypt(
            nonce,
            plaintext,
            frame.Slice(LengthSize, plaintext.Length),
            frame.Slice(LengthSize + plaintext.Length, Wire.TagSize));
        return LengthSize + sealedLength;
    }

    /// <summary>
    /// Opens one sealed record — ciphertext and tag, without the length that preceded it —
    /// and answers the plaintext length, or -1 when it does not open. A record that does not
    /// open was not written by whoever holds the pairing key, and there is nothing further
    /// to be done with the connection it came on.
    /// </summary>
    public static int Open(
        AesGcm aes, ulong record, ReadOnlySpan<byte> sealedRecord, Span<byte> plaintext,
        bool backwards = false)
    {
        if (sealedRecord.Length < Wire.TagSize) return -1;

        var length = sealedRecord.Length - Wire.TagSize;
        if (length > plaintext.Length) return -1;

        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, record, backwards);

        try
        {
            aes.Decrypt(
                nonce,
                sealedRecord[..length],
                sealedRecord[length..],
                plaintext[..length]);
        }
        catch (CryptographicException)
        {
            return -1;
        }

        return length;
    }

    /// <summary>
    /// The record number, little-endian, in a nonce of zeros.
    ///
    /// <para>
    /// Eight bytes rather than four so that the number cannot silently wrap, and the four
    /// above them stay zero — for every record number this path can produce, that is the
    /// same twelve bytes either width would give, so the two readings of «nonce = record
    /// number» cannot disagree on the wire.
    /// </para>
    ///
    /// <para>
    /// One key seals both directions, so the record travelling backwards — the ready byte,
    /// and nothing else ever does — sets the twelfth byte to <c>0x80</c>. A nonce repeated
    /// over two different plaintexts under one key is the one mistake GCM does not forgive,
    /// and this keeps the two streams apart without either side tracking the other's count.
    /// </para>
    /// </summary>
    private static void Nonce(Span<byte> nonce, ulong record, bool backwards = false)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, record);
        if (backwards) nonce[11] = 0x80;
    }
}
