using System.Buffers.Binary;
using HexBridge.DualSense;

namespace HexBridge.Tests;

/// <summary>
/// Byte-level tests for the USB/IP wire format. Every assertion here is an offset or an
/// endianness, because those are the two things that cannot be checked from macOS any
/// other way and the two things that silently produce a device Windows will not touch.
/// </summary>
public class UsbIpCodecTests
{
    [Fact]
    public void OpHeaderIsEightBigEndianBytes()
    {
        var bytes = new OpHeader(UsbIpProtocol.Version, UsbIpProtocol.OpRepImport, 0).ToArray();

        Assert.Equal(8, bytes.Length);
        Assert.Equal(new byte[] { 0x01, 0x11, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00 }, bytes);
    }

    [Fact]
    public void OpHeaderRoundTrips()
    {
        var header = new OpHeader(UsbIpProtocol.Version, UsbIpProtocol.OpRepDevList, UsbIpProtocol.StatusNoDevice);
        Assert.Equal(header, OpHeader.Read(header.ToArray()));
    }

    [Fact]
    public void RequestCodesMatchTheSpecification()
    {
        Assert.Equal(0x8005, UsbIpProtocol.OpReqDevList);
        Assert.Equal(0x0005, UsbIpProtocol.OpRepDevList);
        Assert.Equal(0x8003, UsbIpProtocol.OpReqImport);
        Assert.Equal(0x0003, UsbIpProtocol.OpRepImport);
        Assert.Equal(0x0111, UsbIpProtocol.Version);
    }

    [Fact]
    public void UsbDeviceStructIsThreeHundredAndTwelveBytesAtTheRightOffsets()
    {
        var info = new UsbIpDeviceInfo
        {
            Path = "/sys/devices/hexbridge/usb1/1-1",
            BusId = "1-1",
            BusNum = 1,
            DevNum = 2,
            Speed = UsbIpProtocol.SpeedHigh,
            IdVendor = TestDevices.Vendor,
            IdProduct = TestDevices.Product,
            BcdDevice = TestDevices.BcdDevice,
            DeviceClass = 0,
            DeviceSubClass = 0,
            DeviceProtocol = 0,
            ConfigurationValue = 1,
            NumConfigurations = 1,
            NumInterfaces = 1,
            Interfaces = [new UsbIpInterfaceInfo(0x03, 0x00, 0x00)],
        };

        var bytes = info.ToArray();
        Assert.Equal(312, bytes.Length);

        Assert.Equal("/sys/devices/hexbridge/usb1/1-1", UsbIpProtocol.ReadString(bytes.AsSpan(0, 256)));
        Assert.Equal("1-1", UsbIpProtocol.ReadString(bytes.AsSpan(256, 32)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(288)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(292)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(296)));

        // Big-endian here, little-endian inside the device descriptor: 054C must not come
        // back as 4C05.
        Assert.Equal(0x054C, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(300)));
        Assert.Equal(0x0CE6, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(302)));
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(304)));

        Assert.Equal(0, bytes[306]);   // bDeviceClass
        Assert.Equal(0, bytes[307]);
        Assert.Equal(0, bytes[308]);
        Assert.Equal(1, bytes[309]);   // bConfigurationValue
        Assert.Equal(1, bytes[310]);   // bNumConfigurations
        Assert.Equal(1, bytes[311]);   // bNumInterfaces
    }

    [Fact]
    public void UsbDeviceStructRoundTrips()
    {
        var info = TestDevices.Device().Info;
        var read = UsbIpDeviceInfo.Read(info.ToArray());

        Assert.Equal(info.BusId, read.BusId);
        Assert.Equal(info.Path, read.Path);
        Assert.Equal(info.IdVendor, read.IdVendor);
        Assert.Equal(info.IdProduct, read.IdProduct);
        Assert.Equal(info.BcdDevice, read.BcdDevice);
        Assert.Equal(info.NumInterfaces, read.NumInterfaces);
        Assert.Equal(info.Speed, read.Speed);
    }

    [Fact]
    public void BusIdIsTruncatedRatherThanOverflowing()
    {
        var info = new UsbIpDeviceInfo { BusId = new string('9', 64), Path = new string('p', 512) };
        var bytes = info.ToArray();

        Assert.Equal(312, bytes.Length);
        Assert.Equal(0, bytes[255]);  // path stays NUL-terminated
        Assert.Equal(0, bytes[287]);  // and so does busid
    }

    [Fact]
    public void DevListEntryAppendsOneInterfacePerDeclaredInterface()
    {
        var info = new UsbIpDeviceInfo
        {
            BusId = "1-1",
            NumInterfaces = 1,
            Interfaces = [new UsbIpInterfaceInfo(0x03, 0x01, 0x02)],
        };

        var entry = info.ToDevListEntry();
        Assert.Equal(312 + 4, entry.Length);
        Assert.Equal(0x03, entry[312]);
        Assert.Equal(0x01, entry[313]);
        Assert.Equal(0x02, entry[314]);
        Assert.Equal(0x00, entry[315]);  // padding must be zero
    }

    [Fact]
    public void UrbHeaderIsTwentyBigEndianBytes()
    {
        var header = new UrbHeader(UsbIpProtocol.CmdSubmit, 7, 0x00010002, UsbIpProtocol.DirectionIn, 4);
        var bytes = new byte[20];
        header.Write(bytes);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(bytes));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)));
        Assert.Equal(0x00010002u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)));
        Assert.Equal(header, UrbHeader.Read(bytes));
    }

    [Fact]
    public void SubmitHeaderIsFortyEightBytesWithSetupLast()
    {
        var setup = TestDevices.Setup(0x80, 0x06, 0x0100, 0x0000, 18);
        var submit = new UsbIpSubmit
        {
            Header = new UrbHeader(UsbIpProtocol.CmdSubmit, 11, 0x00010001, UsbIpProtocol.DirectionIn, 0),
            TransferFlags = 0x200,
            TransferBufferLength = 18,
            StartFrame = 0,
            NumberOfPackets = -1,
            Interval = 0,
            Setup = setup,
        };

        var bytes = submit.ToArray();
        Assert.Equal(48, bytes.Length);
        Assert.Equal(0x200u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)));
        Assert.Equal(18, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(24)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(28)));
        Assert.Equal(setup, bytes[40..48]);
    }

    [Fact]
    public void NonIsochronousTransfersCarryMinusOneAsAllOnes()
    {
        var bytes = new UsbIpSubmit { NumberOfPackets = -1 }.ToArray();
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(32)));

        var reply = new UsbIpSubmitReply { Seqnum = 1 }.ToArray();
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(32)));
    }

    [Theory]
    [InlineData(0xFFFFFFFF)]
    [InlineData(0u)]
    public void BothSpellingsOfNotIsochronousAreUnderstood(uint onTheWire)
    {
        var header = new byte[48];
        new UrbHeader(UsbIpProtocol.CmdSubmit, 1, 0, 1, 4).Write(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(32), onTheWire);

        var submit = UsbIpSubmit.ReadHeader(header);
        Assert.Equal(-1, submit.NumberOfPackets);
        Assert.False(submit.IsIsochronous);
    }

    [Fact]
    public void SubmitRoundTripsThroughItsOwnCodec()
    {
        var original = new UsbIpSubmit
        {
            Header = new UrbHeader(UsbIpProtocol.CmdSubmit, 42, 0x00010001, UsbIpProtocol.DirectionOut, 3),
            TransferFlags = 0,
            TransferBufferLength = TestDevices.OutputReportLength,
            Interval = 6,
            Setup = new byte[8],
            TransferBuffer = TestDevices.OutputReport(),
        };

        var bytes = original.ToArray();
        var parsed = UsbIpSubmit.ReadHeader(bytes.AsSpan(0, 48));

        Assert.Equal(original.Header, parsed.Header with { Command = UsbIpProtocol.CmdSubmit });
        Assert.Equal(original.TransferBufferLength, parsed.TransferBufferLength);
        Assert.Equal(6, parsed.Interval);
        Assert.Equal(TestDevices.OutputReportLength, bytes.Length - 48);
        Assert.Equal(original.TransferBuffer, bytes[48..]);
    }

    [Fact]
    public void SubmitReplyPutsStatusAndActualLengthWhereTheDriverLooks()
    {
        var reply = UsbIpSubmitReply.ForIn(9, UsbIpProtocol.StatusSuccess, TestDevices.InputReport(), 64);
        var bytes = reply.ToArray();

        Assert.Equal(48 + 64, bytes.Length);
        Assert.Equal(UsbIpProtocol.RetSubmit, BinaryPrimitives.ReadUInt32BigEndian(bytes));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)));
        Assert.Equal(64, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(24)));

        // devid, direction and ep are zero in a reply, exactly as the kernel sends them.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)));
        // Bytes 40..47 are padding.
        Assert.All(bytes[40..48], b => Assert.Equal(0, b));
    }

    [Fact]
    public void StallIsMinusEpipe()
    {
        var bytes = UsbIpSubmitReply
            .ForIn(3, UsbIpProtocol.StatusStall, ReadOnlySpan<byte>.Empty, 64).ToArray();

        Assert.Equal(-32, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(24)));
    }

    // usbip-win2 issue #187: an over-long answer is an EOVERFLOW in Windows, not a
    // forgiving read. This is the invariant the whole server is built around.
    [Theory]
    [InlineData(64, 64, 64)]
    [InlineData(64, 32, 32)]
    [InlineData(64, 0, 0)]
    [InlineData(64, 8, 8)]
    [InlineData(18, 64, 18)]
    public void ActualLengthNeverExceedsTheRequestedLength(int available, int requested, int expected)
    {
        var data = new byte[available];
        var reply = UsbIpSubmitReply.ForIn(1, UsbIpProtocol.StatusSuccess, data, requested);

        Assert.Equal(expected, reply.ActualLength);
        Assert.Equal(expected, reply.Data.Length);
        Assert.True(reply.ActualLength <= requested);
        Assert.Equal(48 + expected, reply.ToArray().Length);
    }

    [Fact]
    public void ANegativeRequestedLengthIsTreatedAsZero()
    {
        var reply = UsbIpSubmitReply.ForIn(1, 0, new byte[16], -4);
        Assert.Equal(0, reply.ActualLength);
        Assert.Empty(reply.Data);
    }

    [Fact]
    public void OutRepliesReportBytesTakenAndCarryNoData()
    {
        var reply = UsbIpSubmitReply.ForOut(5, UsbIpProtocol.StatusSuccess, 48, 48);

        Assert.Equal(48, reply.ActualLength);
        Assert.Empty(reply.Data);
        Assert.Equal(48, reply.ToArray().Length);

        Assert.Equal(16, UsbIpSubmitReply.ForOut(5, 0, 64, 16).ActualLength);
    }

    [Fact]
    public void UnlinkCarriesTheSeqnumOfItsVictim()
    {
        var unlink = new UsbIpUnlink
        {
            Header = new UrbHeader(UsbIpProtocol.CmdUnlink, 100, 0x00010001, 0, 0),
            UnlinkSeqnum = 99,
        };

        var bytes = unlink.ToArray();
        Assert.Equal(48, bytes.Length);
        Assert.Equal(UsbIpProtocol.CmdUnlink, BinaryPrimitives.ReadUInt32BigEndian(bytes));

        var parsed = UsbIpUnlink.Read(bytes);
        Assert.Equal(100u, parsed.Header.Seqnum);
        Assert.Equal(99u, parsed.UnlinkSeqnum);
    }

    [Fact]
    public void UnlinkReplyUsesMinusEconnresetWhenTheUrbWasKilled()
    {
        var bytes = new UsbIpUnlinkReply { Seqnum = 100, Status = UsbIpProtocol.StatusUnlinked }.ToArray();

        Assert.Equal(48, bytes.Length);
        Assert.Equal(UsbIpProtocol.RetUnlink, BinaryPrimitives.ReadUInt32BigEndian(bytes));
        Assert.Equal(-104, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)));
        Assert.Equal(100u, UsbIpUnlinkReply.Read(bytes).Seqnum);
    }
}
