using System.Buffers.Binary;
using System.Text;

namespace HexBridge.Devices;

/// <summary>
/// Wire constants for USB/IP 1.1.1 (https://docs.kernel.org/usb/usbip_protocol.html).
///
/// Every multi-byte field on this wire is big-endian, including the ones that are USB
/// descriptor values elsewhere: <c>idVendor</c> is little-endian inside a device
/// descriptor and big-endian inside <c>usbip_usb_device</c>. Mixing the two up produces a
/// device that enumerates as 0x4C05 and nothing else goes wrong until Windows says the
/// driver does not match, so the two worlds never share a codec here.
/// </summary>
public static class UsbIpProtocol
{
    public const ushort Version = 0x0111;

    // Operational phase.
    public const ushort OpRequest = 0x8000;
    public const ushort OpReqDevList = 0x8005;
    public const ushort OpRepDevList = 0x0005;
    public const ushort OpReqImport = 0x8003;
    public const ushort OpRepImport = 0x0003;

    // Reply status for the operational phase.
    public const uint StatusOk = 0;
    public const uint StatusNoDevice = 1;

    // URB phase.
    public const uint CmdSubmit = 1;
    public const uint CmdUnlink = 2;
    public const uint RetSubmit = 3;
    public const uint RetUnlink = 4;

    public const uint DirectionOut = 0;
    public const uint DirectionIn = 1;

    /// <summary>
    /// What <c>number_of_packets</c> carries for everything that is not isochronous.
    /// Read as a signed -1; older kernels sent 0 and both are accepted on the way in.
    /// </summary>
    public const uint NonIsochronous = 0xFFFFFFFF;

    // Sizes, all fixed by the protocol.
    public const int OpHeaderSize = 8;
    public const int UsbDeviceSize = 312;
    public const int UsbInterfaceSize = 4;
    public const int UrbBasicSize = 20;
    public const int UrbHeaderSize = 48;
    public const int SetupSize = 8;
    public const int BusIdSize = 32;
    public const int PathSize = 256;

    /// <summary><c>struct usbip_iso_packet_descriptor</c>: four big-endian words.</summary>
    public const int IsoPacketSize = 16;

    /// <summary>
    /// A ceiling on <c>number_of_packets</c>. One URB covers a few milliseconds of audio and
    /// a millisecond is eight microframes, so anything near this is already nonsense — but
    /// without the ceiling a corrupt header would have us allocate whatever it asked for
    /// before a single descriptor had been read.
    /// </summary>
    public const int MaxIsoPackets = 1024;

    // URB status codes, as negative Linux errnos — the only dialect vhci understands.
    public const int StatusSuccess = 0;
    public const int StatusStall = -32;        // -EPIPE, "the device refused this request"
    public const int StatusUnlinked = -104;    // -ECONNRESET
    public const int StatusShutdown = -108;    // -ESHUTDOWN
    public const int StatusNotFound = -2;      // -ENOENT

    // usb_device_speed.
    public const uint SpeedFull = 2;
    public const uint SpeedHigh = 3;

    internal static void WriteString(Span<byte> dst, string value)
    {
        dst.Clear();
        var bytes = Encoding.ASCII.GetBytes(value);
        var length = Math.Min(bytes.Length, dst.Length - 1);
        bytes.AsSpan(0, length).CopyTo(dst);
    }

    internal static string ReadString(ReadOnlySpan<byte> src)
    {
        var end = src.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? src : src[..end]);
    }
}

/// <summary>The 8-byte header every operational-phase packet starts with.</summary>
public readonly record struct OpHeader(ushort Version, ushort Code, uint Status)
{
    public static OpHeader Read(ReadOnlySpan<byte> src) => new(
        BinaryPrimitives.ReadUInt16BigEndian(src),
        BinaryPrimitives.ReadUInt16BigEndian(src[2..]),
        BinaryPrimitives.ReadUInt32BigEndian(src[4..]));

    public void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst, Version);
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..], Code);
        BinaryPrimitives.WriteUInt32BigEndian(dst[4..], Status);
    }

    public byte[] ToArray()
    {
        var bytes = new byte[UsbIpProtocol.OpHeaderSize];
        Write(bytes);
        return bytes;
    }

    public static OpHeader Reply(ushort code, uint status = UsbIpProtocol.StatusOk) =>
        new(UsbIpProtocol.Version, code, status);
}

/// <summary>One entry of <c>usbip_usb_interface</c>: 3 meaningful bytes and a pad.</summary>
public readonly record struct UsbIpInterfaceInfo(byte Class, byte SubClass, byte Protocol)
{
    public void Write(Span<byte> dst)
    {
        dst[0] = Class;
        dst[1] = SubClass;
        dst[2] = Protocol;
        dst[3] = 0;  // padding, must be zero
    }

    public static UsbIpInterfaceInfo Read(ReadOnlySpan<byte> src) => new(src[0], src[1], src[2]);
}

/// <summary>
/// <c>struct usbip_usb_device</c>, 312 bytes packed: 256 of path, 32 of busid, then the
/// numbers. The interface list that follows it in OP_REP_DEVLIST is carried here too, but
/// written separately — OP_REP_IMPORT sends the 312 bytes alone.
/// </summary>
public sealed record UsbIpDeviceInfo
{
    public string Path { get; init; } = "";
    public string BusId { get; init; } = "";
    public uint BusNum { get; init; }
    public uint DevNum { get; init; }
    public uint Speed { get; init; } = UsbIpProtocol.SpeedHigh;
    public ushort IdVendor { get; init; }
    public ushort IdProduct { get; init; }
    public ushort BcdDevice { get; init; }
    public byte DeviceClass { get; init; }
    public byte DeviceSubClass { get; init; }
    public byte DeviceProtocol { get; init; }
    public byte ConfigurationValue { get; init; }
    public byte NumConfigurations { get; init; } = 1;
    public byte NumInterfaces { get; init; } = 1;

    public IReadOnlyList<UsbIpInterfaceInfo> Interfaces { get; init; } = [];

    /// <summary>What vhci puts in <c>devid</c> on every URB for this device.</summary>
    public uint DevId => (BusNum << 16) | (DevNum & 0xFFFF);

    public void Write(Span<byte> dst)
    {
        dst[..UsbIpProtocol.UsbDeviceSize].Clear();
        UsbIpProtocol.WriteString(dst[..UsbIpProtocol.PathSize], Path);
        UsbIpProtocol.WriteString(dst.Slice(256, UsbIpProtocol.BusIdSize), BusId);
        BinaryPrimitives.WriteUInt32BigEndian(dst[288..], BusNum);
        BinaryPrimitives.WriteUInt32BigEndian(dst[292..], DevNum);
        BinaryPrimitives.WriteUInt32BigEndian(dst[296..], Speed);
        BinaryPrimitives.WriteUInt16BigEndian(dst[300..], IdVendor);
        BinaryPrimitives.WriteUInt16BigEndian(dst[302..], IdProduct);
        BinaryPrimitives.WriteUInt16BigEndian(dst[304..], BcdDevice);
        dst[306] = DeviceClass;
        dst[307] = DeviceSubClass;
        dst[308] = DeviceProtocol;
        dst[309] = ConfigurationValue;
        dst[310] = NumConfigurations;
        dst[311] = NumInterfaces;
    }

    public static UsbIpDeviceInfo Read(ReadOnlySpan<byte> src) => new()
    {
        Path = UsbIpProtocol.ReadString(src[..UsbIpProtocol.PathSize]),
        BusId = UsbIpProtocol.ReadString(src.Slice(256, UsbIpProtocol.BusIdSize)),
        BusNum = BinaryPrimitives.ReadUInt32BigEndian(src[288..]),
        DevNum = BinaryPrimitives.ReadUInt32BigEndian(src[292..]),
        Speed = BinaryPrimitives.ReadUInt32BigEndian(src[296..]),
        IdVendor = BinaryPrimitives.ReadUInt16BigEndian(src[300..]),
        IdProduct = BinaryPrimitives.ReadUInt16BigEndian(src[302..]),
        BcdDevice = BinaryPrimitives.ReadUInt16BigEndian(src[304..]),
        DeviceClass = src[306],
        DeviceSubClass = src[307],
        DeviceProtocol = src[308],
        ConfigurationValue = src[309],
        NumConfigurations = src[310],
        NumInterfaces = src[311],
    };

    /// <summary>The 312-byte struct on its own, as OP_REP_IMPORT carries it.</summary>
    public byte[] ToArray()
    {
        var bytes = new byte[UsbIpProtocol.UsbDeviceSize];
        Write(bytes);
        return bytes;
    }

    /// <summary>The struct followed by one 4-byte entry per interface, as OP_REP_DEVLIST carries it.</summary>
    public byte[] ToDevListEntry()
    {
        var bytes = new byte[UsbIpProtocol.UsbDeviceSize + Interfaces.Count * UsbIpProtocol.UsbInterfaceSize];
        Write(bytes);
        for (var i = 0; i < Interfaces.Count; i++)
        {
            Interfaces[i].Write(bytes.AsSpan(UsbIpProtocol.UsbDeviceSize + i * UsbIpProtocol.UsbInterfaceSize));
        }
        return bytes;
    }
}

/// <summary>
/// <c>struct usbip_iso_packet_descriptor</c>: one per microframe, appended to both
/// CMD_SUBMIT and RET_SUBMIT of an isochronous transfer, after the data.
///
/// Isochronous is the one transfer type where a single URB describes many independent
/// deliveries, and the descriptors are how the two sides agree on which bytes belong to
/// which microframe. <c>offset</c> and <c>length</c> come from the host and say where in the
/// transfer buffer this packet lives; <c>actual_length</c> and <c>status</c> come back from
/// the device and say what really happened to it. A packet that misses its slot is not
/// retried — that is the whole bargain of isochronous — so a non-zero status is information,
/// not an error to recover from.
/// </summary>
public readonly record struct UsbIpIsoPacket(int Offset, int Length, int ActualLength, int Status)
{
    public void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteInt32BigEndian(dst, Offset);
        BinaryPrimitives.WriteInt32BigEndian(dst[4..], Length);
        BinaryPrimitives.WriteInt32BigEndian(dst[8..], ActualLength);
        BinaryPrimitives.WriteInt32BigEndian(dst[12..], Status);
    }

    public static UsbIpIsoPacket Read(ReadOnlySpan<byte> src) => new(
        BinaryPrimitives.ReadInt32BigEndian(src),
        BinaryPrimitives.ReadInt32BigEndian(src[4..]),
        BinaryPrimitives.ReadInt32BigEndian(src[8..]),
        BinaryPrimitives.ReadInt32BigEndian(src[12..]));

    /// <summary>Parses <paramref name="count"/> descriptors laid end to end.</summary>
    public static UsbIpIsoPacket[] ReadAll(ReadOnlySpan<byte> src, int count)
    {
        var packets = new UsbIpIsoPacket[count];
        for (var i = 0; i < count; i++)
        {
            packets[i] = Read(src.Slice(i * UsbIpProtocol.IsoPacketSize, UsbIpProtocol.IsoPacketSize));
        }
        return packets;
    }

    public static void WriteAll(Span<byte> dst, IReadOnlyList<UsbIpIsoPacket> packets)
    {
        for (var i = 0; i < packets.Count; i++)
        {
            packets[i].Write(dst.Slice(i * UsbIpProtocol.IsoPacketSize, UsbIpProtocol.IsoPacketSize));
        }
    }
}

/// <summary>The 20 bytes every URB packet starts with.</summary>
public readonly record struct UrbHeader(uint Command, uint Seqnum, uint DevId, uint Direction, uint Endpoint)
{
    public static UrbHeader Read(ReadOnlySpan<byte> src) => new(
        BinaryPrimitives.ReadUInt32BigEndian(src),
        BinaryPrimitives.ReadUInt32BigEndian(src[4..]),
        BinaryPrimitives.ReadUInt32BigEndian(src[8..]),
        BinaryPrimitives.ReadUInt32BigEndian(src[12..]),
        BinaryPrimitives.ReadUInt32BigEndian(src[16..]));

    public void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt32BigEndian(dst, Command);
        BinaryPrimitives.WriteUInt32BigEndian(dst[4..], Seqnum);
        BinaryPrimitives.WriteUInt32BigEndian(dst[8..], DevId);
        BinaryPrimitives.WriteUInt32BigEndian(dst[12..], Direction);
        BinaryPrimitives.WriteUInt32BigEndian(dst[16..], Endpoint);
    }

    public bool IsIn => Direction == UsbIpProtocol.DirectionIn;
}

/// <summary>USBIP_CMD_SUBMIT: the 48-byte header plus, for an OUT transfer, its data.</summary>
public sealed record UsbIpSubmit
{
    public UrbHeader Header { get; init; }
    public uint TransferFlags { get; init; }
    public int TransferBufferLength { get; init; }
    public int StartFrame { get; init; }
    public int NumberOfPackets { get; init; } = -1;
    public int Interval { get; init; }
    public byte[] Setup { get; init; } = new byte[UsbIpProtocol.SetupSize];
    public byte[] TransferBuffer { get; init; } = [];

    /// <summary>
    /// The descriptors that followed the data, empty for everything that is not isochronous.
    /// Read off the socket separately because they come after a payload whose length the
    /// fixed header is the only place to learn.
    /// </summary>
    public IReadOnlyList<UsbIpIsoPacket> IsoPackets { get; init; } = [];

    public uint Seqnum => Header.Seqnum;
    public bool IsIn => Header.IsIn;
    public bool IsControl => Header.Endpoint == 0;
    public bool IsIsochronous => NumberOfPackets >= 0;

    /// <summary>Bytes of descriptor that follow the data on the wire.</summary>
    public int IsoDescriptorBytes => IsIsochronous ? NumberOfPackets * UsbIpProtocol.IsoPacketSize : 0;

    /// <summary>Parses the fixed 48 bytes. The OUT data, if any, follows on the socket.</summary>
    public static UsbIpSubmit ReadHeader(ReadOnlySpan<byte> src)
    {
        var setup = new byte[UsbIpProtocol.SetupSize];
        src.Slice(40, UsbIpProtocol.SetupSize).CopyTo(setup);

        var packets = BinaryPrimitives.ReadUInt32BigEndian(src[32..]);
        return new UsbIpSubmit
        {
            Header = UrbHeader.Read(src),
            TransferFlags = BinaryPrimitives.ReadUInt32BigEndian(src[20..]),
            TransferBufferLength = BinaryPrimitives.ReadInt32BigEndian(src[24..]),
            StartFrame = BinaryPrimitives.ReadInt32BigEndian(src[28..]),
            // 0xFFFFFFFF is the modern "not isochronous"; a plain 0 means the same from
            // an older stack, and no real transfer has zero packets. The saturating cast
            // keeps a nonsense count from wrapping to a negative one, which would read as
            // "not isochronous" and leave the descriptors sitting unread on the socket.
            NumberOfPackets = packets is UsbIpProtocol.NonIsochronous or 0
                ? -1
                : (int)Math.Min(packets, int.MaxValue),
            Interval = BinaryPrimitives.ReadInt32BigEndian(src[36..]),
            Setup = setup,
        };
    }

    public byte[] ToArray()
    {
        var payload = IsIn ? Array.Empty<byte>() : TransferBuffer;
        // Header, then the data an OUT transfer carries, then one descriptor per packet.
        // That order is the specification's and it is also the only one that can be parsed:
        // the descriptor count is in the header and the data length is in the header too,
        // so whichever came last would still be findable — but vhci reads them in this one.
        var bytes = new byte[
            UsbIpProtocol.UrbHeaderSize + payload.Length + IsoPackets.Count * UsbIpProtocol.IsoPacketSize];
        var span = bytes.AsSpan();

        (Header with { Command = UsbIpProtocol.CmdSubmit }).Write(span);
        BinaryPrimitives.WriteUInt32BigEndian(span[20..], TransferFlags);
        BinaryPrimitives.WriteInt32BigEndian(span[24..], TransferBufferLength);
        BinaryPrimitives.WriteInt32BigEndian(span[28..], StartFrame);
        BinaryPrimitives.WriteUInt32BigEndian(span[32..],
            NumberOfPackets < 0 ? UsbIpProtocol.NonIsochronous : (uint)NumberOfPackets);
        BinaryPrimitives.WriteInt32BigEndian(span[36..], Interval);
        Setup.AsSpan(0, Math.Min(Setup.Length, UsbIpProtocol.SetupSize)).CopyTo(span[40..]);
        payload.CopyTo(span[UsbIpProtocol.UrbHeaderSize..]);
        UsbIpIsoPacket.WriteAll(span[(UsbIpProtocol.UrbHeaderSize + payload.Length)..], IsoPackets);
        return bytes;
    }
}

/// <summary>USBIP_RET_SUBMIT: the 48-byte header plus, for an IN transfer, the data read.</summary>
public sealed record UsbIpSubmitReply
{
    public uint Seqnum { get; init; }
    public int Status { get; init; }
    public int StartFrame { get; init; }
    public int NumberOfPackets { get; init; } = -1;
    public int ErrorCount { get; init; }
    public byte[] Data { get; init; } = [];

    /// <summary>One per packet for an isochronous transfer, empty for everything else.</summary>
    public IReadOnlyList<UsbIpIsoPacket> IsoPackets { get; init; } = [];

    /// <summary>Bytes actually transferred. Never more than the request asked for.</summary>
    public int ActualLength { get; init; }

    /// <summary>
    /// Completes an IN transfer, clamping the answer to what the host asked for.
    ///
    /// This clamp is the whole reason this factory exists. usbip-win2 issue #187: a
    /// RET_SUBMIT whose actual_length exceeds the request's transfer_buffer_length makes
    /// vhci hand Windows an EOVERFLOW and the read that was waiting on the controller dies.
    /// Native xHCI forgives an over-long answer, so the bug only ever shows up here.
    /// </summary>
    public static UsbIpSubmitReply ForIn(uint seqnum, int status, ReadOnlySpan<byte> data, int transferBufferLength)
    {
        var limit = Math.Max(0, transferBufferLength);
        var length = Math.Min(data.Length, limit);
        return new UsbIpSubmitReply
        {
            Seqnum = seqnum,
            Status = status,
            ActualLength = length,
            Data = data[..length].ToArray(),
        };
    }

    /// <summary>Completes an OUT transfer: no data comes back, only a count of bytes taken.</summary>
    public static UsbIpSubmitReply ForOut(uint seqnum, int status, int accepted, int transferBufferLength) =>
        new()
        {
            Seqnum = seqnum,
            Status = status,
            ActualLength = Math.Clamp(accepted, 0, Math.Max(0, transferBufferLength)),
        };

    /// <summary>
    /// Completes an isochronous URB.
    ///
    /// Three rules hold here and vhci enforces all three. <c>number_of_packets</c> must equal
    /// what the request asked for — the driver compares it against the URB it still holds and
    /// tears the session down if they differ, so even a refusal has to carry a full set of
    /// descriptors. No packet's <c>actual_length</c> may exceed its own <c>length</c>. And the
    /// total may not exceed <c>transfer_buffer_length</c>, which is issue #187 again: an
    /// over-long answer is an EOVERFLOW handed to Windows rather than a forgiving host.
    ///
    /// Packets are dropped from the tail rather than scaled if the total would not fit. A
    /// short block of haptics is a moment that felt weaker than it should; a mangled one is a
    /// crack.
    /// </summary>
    public static UsbIpSubmitReply ForIsochronous(
        uint seqnum,
        int status,
        int startFrame,
        ReadOnlySpan<byte> data,
        IReadOnlyList<UsbIpIsoPacket> packets,
        int transferBufferLength)
    {
        var limit = Math.Max(0, transferBufferLength);
        var clamped = new UsbIpIsoPacket[packets.Count];
        var total = 0;
        var errors = 0;

        for (var i = 0; i < packets.Count; i++)
        {
            var packet = packets[i];
            var length = Math.Max(0, packet.Length);
            var actual = Math.Clamp(packet.ActualLength, 0, Math.Min(length, limit - total));
            total += actual;
            if (packet.Status != UsbIpProtocol.StatusSuccess) errors++;
            clamped[i] = packet with { Length = length, ActualLength = actual };
        }

        return new UsbIpSubmitReply
        {
            Seqnum = seqnum,
            Status = status,
            StartFrame = startFrame,
            NumberOfPackets = packets.Count,
            ErrorCount = errors,
            ActualLength = total,
            IsoPackets = clamped,
            Data = data[..Math.Min(data.Length, total)].ToArray(),
        };
    }

    public byte[] ToArray()
    {
        var bytes = new byte[
            UsbIpProtocol.UrbHeaderSize + Data.Length + IsoPackets.Count * UsbIpProtocol.IsoPacketSize];
        var span = bytes.AsSpan();

        // devid, direction and ep are zero in every reply: vhci matches on seqnum alone,
        // and that is what the reference implementation sends.
        new UrbHeader(UsbIpProtocol.RetSubmit, Seqnum, 0, 0, 0).Write(span);
        BinaryPrimitives.WriteInt32BigEndian(span[20..], Status);
        BinaryPrimitives.WriteInt32BigEndian(span[24..], ActualLength);
        BinaryPrimitives.WriteInt32BigEndian(span[28..], StartFrame);
        BinaryPrimitives.WriteUInt32BigEndian(span[32..],
            NumberOfPackets < 0 ? UsbIpProtocol.NonIsochronous : (uint)NumberOfPackets);
        BinaryPrimitives.WriteInt32BigEndian(span[36..], ErrorCount);
        // Bytes 40..47 are padding and must be zero.
        Data.CopyTo(span[UsbIpProtocol.UrbHeaderSize..]);
        UsbIpIsoPacket.WriteAll(span[(UsbIpProtocol.UrbHeaderSize + Data.Length)..], IsoPackets);
        return bytes;
    }

    /// <summary>
    /// Parses the fixed 48 bytes; <paramref name="data"/> is what followed them, descriptors
    /// included.
    ///
    /// The descriptors are the tail of that block, however much payload came before them —
    /// which is not always <c>actual_length</c>. An isochronous OUT reply reports the bytes
    /// the device took and sends none of them back, so its descriptors are the whole of what
    /// followed the header. Reading from the end rather than from the offset is what makes
    /// one parser right for both directions, since nothing in the reply says which it is.
    /// </summary>
    public static UsbIpSubmitReply Read(ReadOnlySpan<byte> src, ReadOnlySpan<byte> data)
    {
        var raw = BinaryPrimitives.ReadUInt32BigEndian(src[32..]);
        var count = raw is UsbIpProtocol.NonIsochronous or 0
            ? -1
            : (int)Math.Min(raw, int.MaxValue);
        var actual = BinaryPrimitives.ReadInt32BigEndian(src[24..]);

        var descriptors = Array.Empty<UsbIpIsoPacket>();
        var payload = data;
        var descriptorBytes = count > 0 ? count * UsbIpProtocol.IsoPacketSize : 0;
        if (descriptorBytes > 0 && data.Length >= descriptorBytes)
        {
            descriptors = UsbIpIsoPacket.ReadAll(data[^descriptorBytes..], count);
            payload = data[..^descriptorBytes];
        }

        return new UsbIpSubmitReply
        {
            Seqnum = BinaryPrimitives.ReadUInt32BigEndian(src[4..]),
            Status = BinaryPrimitives.ReadInt32BigEndian(src[20..]),
            ActualLength = actual,
            StartFrame = BinaryPrimitives.ReadInt32BigEndian(src[28..]),
            NumberOfPackets = count,
            ErrorCount = BinaryPrimitives.ReadInt32BigEndian(src[36..]),
            IsoPackets = descriptors,
            Data = payload.ToArray(),
        };
    }
}

/// <summary>USBIP_CMD_UNLINK: cancel a submitted URB.</summary>
public sealed record UsbIpUnlink
{
    public UrbHeader Header { get; init; }

    /// <summary>The seqnum of the CMD_SUBMIT being cancelled, not of this packet.</summary>
    public uint UnlinkSeqnum { get; init; }

    public static UsbIpUnlink Read(ReadOnlySpan<byte> src) => new()
    {
        Header = UrbHeader.Read(src),
        UnlinkSeqnum = BinaryPrimitives.ReadUInt32BigEndian(src[20..]),
    };

    public byte[] ToArray()
    {
        var bytes = new byte[UsbIpProtocol.UrbHeaderSize];
        var span = bytes.AsSpan();
        (Header with { Command = UsbIpProtocol.CmdUnlink }).Write(span);
        BinaryPrimitives.WriteUInt32BigEndian(span[20..], UnlinkSeqnum);
        return bytes;
    }
}

/// <summary>USBIP_RET_UNLINK.</summary>
public sealed record UsbIpUnlinkReply
{
    public uint Seqnum { get; init; }

    /// <summary>-ECONNRESET when the URB was found and cancelled, 0 when it had already completed.</summary>
    public int Status { get; init; }

    public byte[] ToArray()
    {
        var bytes = new byte[UsbIpProtocol.UrbHeaderSize];
        var span = bytes.AsSpan();
        new UrbHeader(UsbIpProtocol.RetUnlink, Seqnum, 0, 0, 0).Write(span);
        BinaryPrimitives.WriteInt32BigEndian(span[20..], Status);
        return bytes;
    }

    public static UsbIpUnlinkReply Read(ReadOnlySpan<byte> src) => new()
    {
        Seqnum = BinaryPrimitives.ReadUInt32BigEndian(src[4..]),
        Status = BinaryPrimitives.ReadInt32BigEndian(src[20..]),
    };
}
