using System.Buffers.Binary;
using System.Text;

namespace HexBridge.DualSense;

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

    public uint Seqnum => Header.Seqnum;
    public bool IsIn => Header.IsIn;
    public bool IsControl => Header.Endpoint == 0;
    public bool IsIsochronous => NumberOfPackets >= 0;

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
            // an older stack, and no real transfer has zero packets.
            NumberOfPackets = packets is UsbIpProtocol.NonIsochronous or 0 ? -1 : (int)packets,
            Interval = BinaryPrimitives.ReadInt32BigEndian(src[36..]),
            Setup = setup,
        };
    }

    public byte[] ToArray()
    {
        var payload = IsIn ? Array.Empty<byte>() : TransferBuffer;
        var bytes = new byte[UsbIpProtocol.UrbHeaderSize + payload.Length];
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

    public byte[] ToArray()
    {
        var bytes = new byte[UsbIpProtocol.UrbHeaderSize + Data.Length];
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
        return bytes;
    }

    /// <summary>Parses the fixed 48 bytes; <paramref name="data"/> is what followed them.</summary>
    public static UsbIpSubmitReply Read(ReadOnlySpan<byte> src, ReadOnlySpan<byte> data)
    {
        var packets = BinaryPrimitives.ReadUInt32BigEndian(src[32..]);
        return new UsbIpSubmitReply
        {
            Seqnum = BinaryPrimitives.ReadUInt32BigEndian(src[4..]),
            Status = BinaryPrimitives.ReadInt32BigEndian(src[20..]),
            ActualLength = BinaryPrimitives.ReadInt32BigEndian(src[24..]),
            StartFrame = BinaryPrimitives.ReadInt32BigEndian(src[28..]),
            NumberOfPackets = packets is UsbIpProtocol.NonIsochronous or 0 ? -1 : (int)packets,
            ErrorCount = BinaryPrimitives.ReadInt32BigEndian(src[36..]),
            Data = data.ToArray(),
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
