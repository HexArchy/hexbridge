using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HexBridge;

public enum PacketType : byte
{
    Audio = 1,
    Hello = 2,
    Pong = 3,

    /// <summary>Mac announces a HID device and hands over its descriptors.</summary>
    DeviceAttach = 4,

    /// <summary>Mac says the device is gone.</summary>
    DeviceDetach = 5,

    /// <summary>One HID input report, at up to 250 Hz.</summary>
    DeviceInput = 6,

    /// <summary>Rumble, triggers and lighting, on change only. Receiver to sender.</summary>
    DeviceOutput = 7,

    /// <summary>Confirms an attach so the sender stops repeating it. Receiver to sender.</summary>
    DeviceAck = 8,

    /// <summary>Offer of a whole object that has to arrive intact, unlike voice.</summary>
    BulkOffer = 9,
    BulkChunk = 10,
    BulkAck = 11,
    BulkDone = 12,

    /// <summary>Continuous PCM for the voice-coil actuators. Receiver to sender.</summary>
    Haptic = 13,
}

[Flags]
public enum PacketFlags : ushort
{
    None = 0,
    Muted = 1 << 0,
    DtxGap = 1 << 1,
}

public enum Direction : uint
{
    SenderToReceiver = 0,
    ReceiverToSender = 1,
}

public readonly record struct Header(PacketType Type, PacketFlags Flags, ulong Room, uint Session, uint Seq);

/// <summary>Wire format shared with the macOS sender. See docs/PROTOCOL.md.</summary>
public static class Wire
{
    public const int HeaderSize = 24;
    public const int TagSize = 16;
    public const int MaxPacket = 1400;
    public const byte Version = 1;

    private static ReadOnlySpan<byte> Magic => "MBG1"u8;

    /// <summary>Room ids let the relay pair endpoints without ever holding the PSK.</summary>
    public static ulong RoomId(byte[] psk)
    {
        var input = new byte[Encoding.UTF8.GetByteCount("hexbridge-room-v1") + psk.Length];
        var written = Encoding.UTF8.GetBytes("hexbridge-room-v1", input);
        psk.CopyTo(input, written);
        return BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(input));
    }

    public static void WriteHeader(Span<byte> dst, in Header header)
    {
        Magic.CopyTo(dst);
        dst[4] = Version;
        dst[5] = (byte)header.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[6..], (ushort)header.Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(dst[8..], header.Room);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[16..], header.Session);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[20..], header.Seq);
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> src, out Header header)
    {
        header = default;
        if (src.Length < HeaderSize) return false;
        if (!src[..4].SequenceEqual(Magic)) return false;
        if (src[4] != Version) return false;

        // Every type the protocol defines is accepted here; deciding what to do with one
        // is the feature host's business, not the codec's.
        var type = (PacketType)src[5];
        if (!Enum.IsDefined(type)) return false;

        header = new Header(
            type,
            (PacketFlags)BinaryPrimitives.ReadUInt16LittleEndian(src[6..]),
            BinaryPrimitives.ReadUInt64LittleEndian(src[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[20..]));
        return true;
    }

    private static void BuildNonce(Span<byte> nonce, uint session, uint seq, Direction direction)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, session);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[4..], seq);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[8..], (uint)direction);
    }

    /// <summary>Builds a complete <c>header || ciphertext || tag</c> datagram.</summary>
    public static byte[] Seal(AesGcm aes, in Header header, ReadOnlySpan<byte> payload, Direction direction)
    {
        var packet = new byte[HeaderSize + payload.Length + TagSize];
        WriteHeader(packet, header);

        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(nonce, header.Session, header.Seq, direction);

        aes.Encrypt(
            nonce,
            payload,
            packet.AsSpan(HeaderSize, payload.Length),
            packet.AsSpan(HeaderSize + payload.Length, TagSize),
            packet.AsSpan(0, HeaderSize));
        return packet;
    }

    /// <summary>
    /// Verifies and decrypts a datagram in place. Returns the plaintext length, or -1 if the
    /// packet is malformed or fails authentication.
    /// </summary>
    public static int Open(AesGcm aes, ReadOnlySpan<byte> datagram, Span<byte> plaintext, Direction direction, out Header header)
    {
        if (!TryReadHeader(datagram, out header)) return -1;
        if (datagram.Length < HeaderSize + TagSize) return -1;

        var cipherLength = datagram.Length - HeaderSize - TagSize;
        if (cipherLength > plaintext.Length) return -1;

        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(nonce, header.Session, header.Seq, direction);

        try
        {
            aes.Decrypt(
                nonce,
                datagram.Slice(HeaderSize, cipherLength),
                datagram.Slice(HeaderSize + cipherLength, TagSize),
                plaintext[..cipherLength],
                datagram[..HeaderSize]);
        }
        catch (CryptographicException)
        {
            return -1;
        }

        return cipherLength;
    }
}

/// <summary>
/// Rejects replayed or badly stale sequence numbers within a 1024-packet window.
/// </summary>
public sealed class ReplayWindow
{
    private const int WindowBits = 1024;
    private readonly bool[] _seen = new bool[WindowBits];
    private uint _highest;
    private bool _started;

    public bool Accept(uint seq)
    {
        if (!_started)
        {
            _started = true;
            _highest = seq;
            _seen[seq % WindowBits] = true;
            return true;
        }

        if (seq > _highest)
        {
            // Clear the slots we skip over so old marks cannot alias onto new sequences.
            if (seq - _highest >= WindowBits)
            {
                Array.Clear(_seen);
            }
            else
            {
                for (var s = _highest + 1; s <= seq; s++)
                {
                    _seen[s % WindowBits] = false;
                }
            }
            _highest = seq;
            _seen[seq % WindowBits] = true;
            return true;
        }

        if (_highest - seq >= WindowBits) return false;
        if (_seen[seq % WindowBits]) return false;

        _seen[seq % WindowBits] = true;
        return true;
    }
}
