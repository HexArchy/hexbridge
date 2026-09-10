using System.Buffers.Binary;

namespace HexBridge.Devices;

/// <summary>Descriptor block kinds inside a DEV_ATTACH. See docs/PROTOCOL.md.</summary>
public enum DescriptorKind : byte
{
    Device = 1,
    Configuration = 2,
    HidReport = 3,

    /// <summary>A feature report read off the real controller: first byte is the report id.</summary>
    FeatureReport = 4,

    /// <summary>
    /// A string descriptor, first byte the index. Not in the contract today — the Mac has
    /// no reason to send one yet — but accepted so the day it does, nothing here changes.
    /// </summary>
    StringDescriptor = 5,
}

public readonly record struct DescriptorBlock(DescriptorKind Kind, byte[] Bytes);

/// <summary>The payload of one DEV_ATTACH.</summary>
public sealed record DeviceAttach(byte Device, IReadOnlyList<DescriptorBlock> Blocks)
{
    public byte[]? First(DescriptorKind kind) =>
        Blocks.FirstOrDefault(b => b.Kind == kind).Bytes;

    public IEnumerable<byte[]> All(DescriptorKind kind) =>
        Blocks.Where(b => b.Kind == kind).Select(b => b.Bytes);
}

/// <summary>The payload of one DEV_IN.</summary>
public readonly record struct DeviceInput(byte Device, uint Index, byte[] Report);

/// <summary>
/// One block of HD haptics: 5 ms of interleaved S16LE PCM for the voice-coil actuators.
///
/// <see cref="Index"/> counts blocks rather than packets, and it counts the ones that were
/// never sent as well. A run of silence is skipped on the wire, so the gap the Mac sees in
/// the numbering is what tells it how long that silence was — the same arithmetic that turns
/// a lost block into the right amount of nothing.
/// </summary>
public readonly record struct HapticBlock(byte Device, byte Channels, uint Index, byte[] Pcm);

/// <summary>
/// Codecs for the device channel payloads. Byte-for-byte the layouts in docs/PROTOCOL.md,
/// with no I/O anywhere near them so a test can drive every branch.
/// </summary>
public static class DeviceChannel
{
    public static bool TryReadAttach(ReadOnlySpan<byte> payload, out DeviceAttach attach)
    {
        attach = null!;
        if (payload.Length < 2) return false;

        var device = payload[0];
        int count = payload[1];
        var blocks = new List<DescriptorBlock>(count);

        var offset = 2;
        for (var i = 0; i < count; i++)
        {
            if (offset + 3 > payload.Length) return false;
            var kind = (DescriptorKind)payload[offset];
            int length = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 1)..]);
            offset += 3;
            if (offset + length > payload.Length) return false;

            blocks.Add(new DescriptorBlock(kind, payload.Slice(offset, length).ToArray()));
            offset += length;
        }

        attach = new DeviceAttach(device, blocks);
        return true;
    }

    public static byte[] WriteAttach(DeviceAttach attach)
    {
        var size = 2 + attach.Blocks.Sum(b => 3 + b.Bytes.Length);
        var bytes = new byte[size];
        bytes[0] = attach.Device;
        bytes[1] = (byte)attach.Blocks.Count;

        var offset = 2;
        foreach (var block in attach.Blocks)
        {
            bytes[offset] = (byte)block.Kind;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 1), (ushort)block.Bytes.Length);
            block.Bytes.CopyTo(bytes, offset + 3);
            offset += 3 + block.Bytes.Length;
        }
        return bytes;
    }

    public static bool TryReadDetach(ReadOnlySpan<byte> payload, out byte device)
    {
        device = payload.Length >= 1 ? payload[0] : (byte)0;
        return payload.Length >= 1;
    }

    public static bool TryReadInput(ReadOnlySpan<byte> payload, out DeviceInput input)
    {
        input = default;
        if (payload.Length < 6) return false;

        input = new DeviceInput(
            payload[0],
            BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]),
            payload[5..].ToArray());
        return true;
    }

    public static byte[] WriteInput(DeviceInput input)
    {
        var bytes = new byte[5 + input.Report.Length];
        bytes[0] = input.Device;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), input.Index);
        input.Report.CopyTo(bytes, 5);
        return bytes;
    }

    /// <summary>DEV_OUT: the device number then the HID output report, report id included.</summary>
    public static byte[] WriteOutput(byte device, ReadOnlySpan<byte> report)
    {
        var bytes = new byte[1 + report.Length];
        bytes[0] = device;
        report.CopyTo(bytes.AsSpan(1));
        return bytes;
    }

    public static bool TryReadOutput(ReadOnlySpan<byte> payload, out byte device, out byte[] report)
    {
        device = 0;
        report = [];
        if (payload.Length < 2) return false;

        device = payload[0];
        report = payload[1..].ToArray();
        return true;
    }

    /// <summary>
    /// DEV_ACK: one byte, the device number. Sent for every DEV_ATTACH and not only the
    /// first — a lost ack has to be curable by the sender's next repeat.
    /// </summary>
    public static byte[] WriteAck(byte device) => [device];

    public static bool TryReadAck(ReadOnlySpan<byte> payload, out byte device)
    {
        device = payload.Length >= 1 ? payload[0] : (byte)0;
        return payload.Length >= 1;
    }

    // MARK: - Haptics

    /// <summary>Bytes before the PCM: device, channel count, block number.</summary>
    public const int HapticHeaderSize = 6;

    /// <summary>
    /// HAPTIC: the device, how many channels are interleaved in this block, the block number,
    /// then the samples.
    /// </summary>
    public static byte[] WriteHaptic(byte device, byte channels, uint index, ReadOnlySpan<byte> pcm)
    {
        var bytes = new byte[HapticHeaderSize + pcm.Length];
        bytes[0] = device;
        bytes[1] = channels;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), index);
        pcm.CopyTo(bytes.AsSpan(HapticHeaderSize));
        return bytes;
    }

    public static bool TryReadHaptic(ReadOnlySpan<byte> payload, out HapticBlock block)
    {
        block = default;
        if (payload.Length < HapticHeaderSize) return false;

        var channels = payload[1];
        // A block that does not divide into whole frames is a block we would have to guess
        // at, and a guess here is a click in somebody's hand.
        if (channels is 0 or > 8) return false;
        var pcm = payload[HapticHeaderSize..];
        if (pcm.Length % (channels * 2) != 0) return false;

        block = new HapticBlock(
            payload[0],
            channels,
            BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]),
            pcm.ToArray());
        return true;
    }
}
