using System.Buffers.Binary;
using HexBridge.Devices;

namespace HexBridge.Tests;

/// <summary>
/// The isochronous half of the USB/IP wire format.
///
/// Every assertion is an offset, an endianness or an invariant vhci checks, because those
/// are the three things that cannot be tried from macOS and the three things whose failure
/// is not a wrong sound but a driver that gives up on the session — which takes the
/// controller with it, not only the haptics.
/// </summary>
public class IsochronousCodecTests
{
    [Fact]
    public void APacketDescriptorIsSixteenBigEndianBytes()
    {
        var bytes = new byte[UsbIpProtocol.IsoPacketSize];
        new UsbIpIsoPacket(0x11223344, 0x55667788, 0x00000001, -32).Write(bytes);

        Assert.Equal(16, bytes.Length);
        Assert.Equal(
            new byte[]
            {
                0x11, 0x22, 0x33, 0x44,
                0x55, 0x66, 0x77, 0x88,
                0x00, 0x00, 0x00, 0x01,
                0xFF, 0xFF, 0xFF, 0xE0,   // -32, as a negative errno
            },
            bytes);
        Assert.Equal(new UsbIpIsoPacket(0x11223344, 0x55667788, 1, -32), UsbIpIsoPacket.Read(bytes));
    }

    [Fact]
    public void DescriptorsFollowTheDataInACommandSubmit()
    {
        var data = new byte[8];
        data[0] = 0xAB;

        var submit = new UsbIpSubmit
        {
            Header = new UrbHeader(UsbIpProtocol.CmdSubmit, 7, 0x00010001, UsbIpProtocol.DirectionOut, 1),
            TransferBufferLength = data.Length,
            NumberOfPackets = 2,
            TransferBuffer = data,
            IsoPackets = [new UsbIpIsoPacket(0, 4, 0, 0), new UsbIpIsoPacket(4, 4, 0, 0)],
        };

        var bytes = submit.ToArray();
        Assert.Equal(UsbIpProtocol.UrbHeaderSize + 8 + 2 * UsbIpProtocol.IsoPacketSize, bytes.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(32)));
        Assert.Equal(0xAB, bytes[UsbIpProtocol.UrbHeaderSize]);

        var descriptors = UsbIpIsoPacket.ReadAll(bytes.AsSpan(UsbIpProtocol.UrbHeaderSize + 8), 2);
        Assert.Equal(new UsbIpIsoPacket(0, 4, 0, 0), descriptors[0]);
        Assert.Equal(new UsbIpIsoPacket(4, 4, 0, 0), descriptors[1]);
    }

    [Fact]
    public void ParsingAHeaderReadsTheNumberOfPackets()
    {
        var submit = new UsbIpSubmit
        {
            Header = new UrbHeader(UsbIpProtocol.CmdSubmit, 1, 0, UsbIpProtocol.DirectionOut, 1),
            NumberOfPackets = 8,
            TransferBufferLength = 0,
        };

        var parsed = UsbIpSubmit.ReadHeader(submit.ToArray());
        Assert.True(parsed.IsIsochronous);
        Assert.Equal(8, parsed.NumberOfPackets);
        Assert.Equal(8 * UsbIpProtocol.IsoPacketSize, parsed.IsoDescriptorBytes);
    }

    [Theory]
    [InlineData(0xFFFFFFFF)]  // the modern "not isochronous"
    [InlineData(0u)]          // what an older stack sent for the same thing
    public void ANonIsochronousCountReadsAsMinusOne(uint raw)
    {
        var bytes = new byte[UsbIpProtocol.UrbHeaderSize];
        new UrbHeader(UsbIpProtocol.CmdSubmit, 1, 0, 0, 1).Write(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32), raw);

        var parsed = UsbIpSubmit.ReadHeader(bytes);
        Assert.False(parsed.IsIsochronous);
        Assert.Equal(-1, parsed.NumberOfPackets);
        Assert.Equal(0, parsed.IsoDescriptorBytes);
    }

    /// <summary>
    /// A count above <c>int.MaxValue</c> must not wrap to a negative one. It would read as
    /// "not isochronous", the descriptors would be left on the socket, and every URB after it
    /// would be parsed out of the middle of somebody else's bytes.
    /// </summary>
    [Fact]
    public void AnAbsurdCountStaysPositiveSoItCanBeRefused()
    {
        var bytes = new byte[UsbIpProtocol.UrbHeaderSize];
        new UrbHeader(UsbIpProtocol.CmdSubmit, 1, 0, 0, 1).Write(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32), 0xFFFFFFFE);

        var parsed = UsbIpSubmit.ReadHeader(bytes);
        Assert.True(parsed.IsIsochronous);
        Assert.Equal(int.MaxValue, parsed.NumberOfPackets);
        Assert.True(parsed.NumberOfPackets > UsbIpProtocol.MaxIsoPackets);
    }

    [Fact]
    public void AReplyCarriesOneDescriptorPerPacketAndSaysSo()
    {
        var reply = UsbIpSubmitReply.ForIsochronous(
            seqnum: 4,
            status: UsbIpProtocol.StatusSuccess,
            startFrame: 100,
            data: ReadOnlySpan<byte>.Empty,
            packets:
            [
                new UsbIpIsoPacket(0, 8, 8, 0),
                new UsbIpIsoPacket(8, 8, 8, 0),
                new UsbIpIsoPacket(16, 8, 8, 0),
            ],
            transferBufferLength: 24);

        var bytes = reply.ToArray();
        Assert.Equal(UsbIpProtocol.UrbHeaderSize + 3 * UsbIpProtocol.IsoPacketSize, bytes.Length);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(32)));
        Assert.Equal(24, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(24)));
        Assert.Equal(100, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(28)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(36)));  // error_count
    }

    [Fact]
    public void NoPacketReportsMoreBytesThanItsOwnLength()
    {
        var reply = UsbIpSubmitReply.ForIsochronous(
            1, UsbIpProtocol.StatusSuccess, 0, ReadOnlySpan<byte>.Empty,
            [new UsbIpIsoPacket(0, 4, 999, 0)], transferBufferLength: 1024);

        Assert.Equal(4, reply.IsoPackets[0].ActualLength);
        Assert.Equal(4, reply.ActualLength);
    }

    /// <summary>
    /// usbip-win2 issue #187: an <c>actual_length</c> past <c>transfer_buffer_length</c> hands
    /// Windows an EOVERFLOW. On an isochronous URB the total is the sum of the packets, so the
    /// invariant has to hold across all of them and not one at a time.
    /// </summary>
    [Fact]
    public void TheTotalNeverExceedsTheTransferBufferLength()
    {
        var reply = UsbIpSubmitReply.ForIsochronous(
            1, UsbIpProtocol.StatusSuccess, 0, ReadOnlySpan<byte>.Empty,
            [
                new UsbIpIsoPacket(0, 100, 100, 0),
                new UsbIpIsoPacket(100, 100, 100, 0),
                new UsbIpIsoPacket(200, 100, 100, 0),
            ],
            transferBufferLength: 150);

        Assert.Equal(150, reply.ActualLength);
        Assert.True(reply.ActualLength <= 150);

        // Whole packets from the tail, not a proportional shave: a short haptic block is a
        // moment that felt weaker, a mangled one is a crack.
        Assert.Equal(100, reply.IsoPackets[0].ActualLength);
        Assert.Equal(50, reply.IsoPackets[1].ActualLength);
        Assert.Equal(0, reply.IsoPackets[2].ActualLength);
    }

    [Fact]
    public void TheDataIsTrimmedToWhatThePacketsAccountFor()
    {
        var payload = new byte[64];
        var reply = UsbIpSubmitReply.ForIsochronous(
            1, UsbIpProtocol.StatusSuccess, 0, payload,
            [new UsbIpIsoPacket(0, 16, 16, 0)], transferBufferLength: 64);

        Assert.Equal(16, reply.ActualLength);
        Assert.Equal(16, reply.Data.Length);
    }

    [Fact]
    public void ErrorCountIsHowManyPacketsFailed()
    {
        var reply = UsbIpSubmitReply.ForIsochronous(
            1, UsbIpProtocol.StatusSuccess, 0, ReadOnlySpan<byte>.Empty,
            [
                new UsbIpIsoPacket(0, 4, 4, UsbIpProtocol.StatusSuccess),
                new UsbIpIsoPacket(4, 4, 0, UsbIpProtocol.StatusNotFound),
                new UsbIpIsoPacket(8, 4, 0, UsbIpProtocol.StatusStall),
            ],
            transferBufferLength: 12);

        Assert.Equal(2, reply.ErrorCount);
    }

    [Fact]
    public void AnIsochronousReplyRoundTripsThroughTheWire()
    {
        var reply = UsbIpSubmitReply.ForIsochronous(
            9, UsbIpProtocol.StatusSuccess, 42, new byte[6],
            [new UsbIpIsoPacket(0, 3, 3, 0), new UsbIpIsoPacket(3, 3, 3, 0)],
            transferBufferLength: 6);

        var bytes = reply.ToArray();
        var parsed = UsbIpSubmitReply.Read(
            bytes.AsSpan(0, UsbIpProtocol.UrbHeaderSize), bytes.AsSpan(UsbIpProtocol.UrbHeaderSize));

        Assert.Equal(9u, parsed.Seqnum);
        Assert.Equal(42, parsed.StartFrame);
        Assert.Equal(2, parsed.NumberOfPackets);
        Assert.Equal(6, parsed.ActualLength);
        Assert.Equal(reply.IsoPackets, parsed.IsoPackets);
        Assert.Equal(6, parsed.Data.Length);
    }

    /// <summary>
    /// An isochronous OUT reply reports the bytes the device took and sends none of them
    /// back, so its descriptors are the whole of what follows the header. Finding them from
    /// the end is what makes one parser right for both directions.
    /// </summary>
    [Fact]
    public void AnOutReplyIsAllDescriptorsAndNoPayload()
    {
        var reply = UsbIpSubmitReply.ForIsochronous(
            3, UsbIpProtocol.StatusSuccess, 0, ReadOnlySpan<byte>.Empty,
            [new UsbIpIsoPacket(0, 392, 392, 0)], transferBufferLength: 392);

        var bytes = reply.ToArray();
        Assert.Equal(UsbIpProtocol.UrbHeaderSize + UsbIpProtocol.IsoPacketSize, bytes.Length);

        var parsed = UsbIpSubmitReply.Read(
            bytes.AsSpan(0, UsbIpProtocol.UrbHeaderSize), bytes.AsSpan(UsbIpProtocol.UrbHeaderSize));
        Assert.Equal(392, parsed.ActualLength);
        Assert.Empty(parsed.Data);
        Assert.Equal(392, Assert.Single(parsed.IsoPackets).ActualLength);
    }
}

/// <summary>
/// The composite configuration: what changes when haptics are switched on, and — more
/// importantly — what does not change when they are off.
/// </summary>
public class CompositeDescriptorTests
{
    private static UsbConfigurationDescriptor Composite() =>
        UsbConfigurationDescriptor.FromFullDescriptor(
            TestDevices.DualSenseConfigurationDescriptor(), withAudio: true);

    /// <summary>
    /// The claim the whole feature rests on: Windows is handed the controller's own bytes.
    /// Not equivalent bytes, not a reassembled version — the same 227.
    /// </summary>
    [Fact]
    public void TheCompositeConfigurationIsTheHardwaresOwnBytes()
    {
        var original = TestDevices.DualSenseConfigurationDescriptor();
        Assert.Equal(227, original.Length);
        Assert.Equal(original, Composite().Bytes);
    }

    [Fact]
    public void AllFourInterfacesSurvive()
    {
        var config = Composite();

        Assert.True(config.IsComposite);
        Assert.Equal(4, config.Interfaces.Count);
        Assert.Equal(new UsbIpInterfaceInfo(0x01, 0x01, 0x00), config.Interfaces[0]);  // audio control
        Assert.Equal(new UsbIpInterfaceInfo(0x01, 0x02, 0x00), config.Interfaces[1]);  // streaming OUT
        Assert.Equal(new UsbIpInterfaceInfo(0x01, 0x02, 0x00), config.Interfaces[2]);  // streaming IN
        Assert.Equal(new UsbIpInterfaceInfo(0x03, 0x00, 0x00), config.Interfaces[3]);  // HID
    }

    /// <summary>
    /// Renumbering is what the HID-only rebuild does because it has to: a configuration that
    /// claims one interface and calls it number 3 is one Windows may distrust. A composite has
    /// no such problem and must not renumber, or the HID interface would answer to a number
    /// the descriptor gives to the audio-control one.
    /// </summary>
    [Fact]
    public void TheHidInterfaceKeepsItsRealNumber()
    {
        Assert.Equal(3, Composite().HidInterfaceNumber);
        Assert.Equal(0, UsbConfigurationDescriptor
            .FromFullDescriptor(TestDevices.DualSenseConfigurationDescriptor())
            .HidInterfaceNumber);
    }

    [Fact]
    public void TheInterruptPairIsStillFoundInsideTheComposite()
    {
        var config = Composite();

        Assert.Equal(0x84, config.InterruptInEndpoint);
        Assert.Equal(0x03, config.InterruptOutEndpoint);
        Assert.Equal(64, config.InterruptInMaxPacket);
        Assert.NotNull(config.HidDescriptor);
        Assert.Equal(UsbDescriptorType.Hid, config.HidDescriptor![1]);
    }

    [Fact]
    public void BothIsochronousEndpointsAreFound()
    {
        var endpoints = Composite().IsochronousEndpoints;
        Assert.Equal(2, endpoints.Count);

        var output = Assert.Single(endpoints, e => !e.IsIn);
        Assert.Equal(0x01, output.Address);
        Assert.Equal(1, output.Number);
        Assert.Equal(392, output.MaxPacketSize);   // 4 channels × 2 bytes × 49 frames
        Assert.Equal(1, output.Interface);
        Assert.Equal(1, output.AlternateSetting);

        var input = Assert.Single(endpoints, e => e.IsIn);
        Assert.Equal(0x82, input.Address);
        Assert.Equal(196, input.MaxPacketSize);
    }

    [Fact]
    public void TheOutgoingStreamIsFourChannelsOfSixteenBitAt48kHz()
    {
        var stream = Composite().OutputStream;
        Assert.NotNull(stream);
        Assert.Equal(4, stream!.Value.Channels);
        Assert.Equal(2, stream.Value.BytesPerSample);
        Assert.Equal(48000, stream.Value.SampleRate);
        Assert.Equal(8, stream.Value.BytesPerFrame);
        Assert.Equal(0x01, stream.Value.Endpoint);
        Assert.Equal(1, stream.Value.Interface);
        Assert.Equal(1, stream.Value.AlternateSetting);
    }

    [Fact]
    public void TheIncomingStreamIsTheControllersMicrophone()
    {
        var stream = Composite().InputStream;
        Assert.NotNull(stream);
        Assert.Equal(2, stream!.Value.Channels);
        Assert.Equal(48000, stream.Value.SampleRate);
        Assert.Equal(0x82, stream.Value.Endpoint);
    }

    /// <summary>
    /// With haptics off there is no audio function in the configuration at all, so there is
    /// no isochronous endpoint for Windows to address and nothing that could reach the driver
    /// path issue #181 lives on. That is the whole point of the switch.
    /// </summary>
    [Fact]
    public void WithHapticsOffTheRealDescriptorIsStillCutDownToHid()
    {
        var config = UsbConfigurationDescriptor.FromFullDescriptor(
            TestDevices.DualSenseConfigurationDescriptor());

        Assert.False(config.IsComposite);
        Assert.Empty(config.IsochronousEndpoints);
        Assert.Null(config.OutputStream);
        Assert.Equal(1, config.Bytes[4]);                       // bNumInterfaces
        Assert.Single(config.Interfaces);
        Assert.Equal(0x03, config.Interface.Class);
        Assert.Equal(config.Bytes.Length,
            BinaryPrimitives.ReadUInt16LittleEndian(config.Bytes.AsSpan(2)));

        // Not one isochronous endpoint descriptor left in the bytes we serve.
        var offset = (int)config.Bytes[0];
        while (offset + 2 <= config.Bytes.Length)
        {
            int length = config.Bytes[offset];
            if (length < 2) break;
            if (config.Bytes[offset + 1] == UsbDescriptorType.Endpoint)
            {
                Assert.Equal(0x03, config.Bytes[offset + 3] & 0x03);
            }
            offset += length;
        }
    }
}

/// <summary>The composite device answering the requests Windows makes of it.</summary>
public class CompositeDeviceTests
{
    private static byte[] Setup(byte type, byte request, ushort value, ushort index, ushort length) =>
        [type, request, (byte)value, (byte)(value >> 8), (byte)index, (byte)(index >> 8),
         (byte)length, (byte)(length >> 8)];

    [Fact]
    public void TheDeviceListOffersFourInterfaces()
    {
        using var device = TestDevices.CompositeDevice();

        Assert.True(device.IsComposite);
        Assert.Equal(4, device.Info.NumInterfaces);
        Assert.Equal(4, device.Info.Interfaces.Count);

        // The entry usbip writes has to agree with the list that follows it, or `usbip list`
        // reads the next device's bytes as this one's interfaces.
        var entry = device.Info.ToDevListEntry();
        Assert.Equal(UsbIpProtocol.UsbDeviceSize + 4 * UsbIpProtocol.UsbInterfaceSize, entry.Length);
        Assert.Equal(4, entry[311]);
    }

    [Fact]
    public void GetDescriptorReturnsTheCompositeVerbatim()
    {
        using var device = TestDevices.CompositeDevice();
        var result = device.Control(
            Setup(0x80, 0x06, 0x0200, 0, 512), ReadOnlySpan<byte>.Empty, 512);

        Assert.Equal(UsbIpProtocol.StatusSuccess, result.Status);
        Assert.Equal(TestDevices.DualSenseConfigurationDescriptor(), result.Data);
    }

    [Fact]
    public void TheReportDescriptorBelongsToTheHidInterfaceAlone()
    {
        using var device = TestDevices.CompositeDevice();

        var hid = device.Control(Setup(0x81, 0x06, 0x2200, 3, 512), ReadOnlySpan<byte>.Empty, 512);
        Assert.Equal(UsbIpProtocol.StatusSuccess, hid.Status);
        Assert.Equal(TestDevices.ReportDescriptorLength, hid.Data.Length);

        // Interface 0 is audio control. A report descriptor is not its to give.
        var audio = device.Control(Setup(0x81, 0x06, 0x2200, 0, 512), ReadOnlySpan<byte>.Empty, 512);
        Assert.Equal(UsbIpProtocol.StatusStall, audio.Status);
    }

    [Fact]
    public void SetInterfaceIsRememberedPerInterface()
    {
        using var device = TestDevices.CompositeDevice();

        Assert.Equal(UsbIpProtocol.StatusSuccess,
            device.Control(Setup(0x01, 0x0B, 1, 1, 0), ReadOnlySpan<byte>.Empty, 0).Status);

        var chosen = device.Control(Setup(0x81, 0x0A, 0, 1, 1), ReadOnlySpan<byte>.Empty, 1);
        Assert.Equal(new byte[] { 1 }, chosen.Data);

        // Interface 3 was never set, so it is still on its default.
        var hid = device.Control(Setup(0x81, 0x0A, 0, 3, 1), ReadOnlySpan<byte>.Empty, 1);
        Assert.Equal(new byte[] { 0 }, hid.Data);
    }

    /// <summary>
    /// The alternate setting is how usbaudio.sys says "I am opening the pin" and "I have
    /// closed it", and it is the only signal the device gets. Without it a stream that ended
    /// would look exactly like one that had gone quiet.
    /// </summary>
    [Fact]
    public void SelectingTheStreamingAlternateStartsAndStopsTheHaptics()
    {
        using var device = TestDevices.CompositeDevice();
        Assert.False(device.HapticsStreaming);

        device.Control(Setup(0x01, 0x0B, 1, 1, 0), ReadOnlySpan<byte>.Empty, 0);
        Assert.True(device.HapticsStreaming);

        device.Control(Setup(0x01, 0x0B, 0, 1, 0), ReadOnlySpan<byte>.Empty, 0);
        Assert.False(device.HapticsStreaming);
    }

    [Fact]
    public void PcmOnTheOutgoingEndpointReachesTheHapticStream()
    {
        using var device = TestDevices.CompositeDevice();
        Assert.NotNull(device.Haptics);

        var stream = device.Haptics!;
        var frames = stream.FramesPerBlock;
        var buffer = new byte[frames * 8];   // 4 channels of 16-bit

        var result = device.Isochronous(new UsbIpSubmit
        {
            Header = new UrbHeader(UsbIpProtocol.CmdSubmit, 1, 0, UsbIpProtocol.DirectionOut, 1),
            TransferBufferLength = buffer.Length,
            NumberOfPackets = 1,
            TransferBuffer = buffer,
            IsoPackets = [new UsbIpIsoPacket(0, buffer.Length, 0, 0)],
        });

        Assert.NotNull(result);
        Assert.Equal(UsbIpProtocol.StatusSuccess, result!.Value.Status);
        Assert.Equal(buffer.Length, Assert.Single(result.Value.Packets).ActualLength);
        Assert.Equal(frames, stream.FramesReceived);
    }

    [Fact]
    public void TheMicrophoneEndpointAnswersWithSilenceRatherThanAStall()
    {
        using var device = TestDevices.CompositeDevice();

        var result = device.Isochronous(new UsbIpSubmit
        {
            Header = new UrbHeader(UsbIpProtocol.CmdSubmit, 1, 0, UsbIpProtocol.DirectionIn, 2),
            TransferBufferLength = 2 * 196,
            NumberOfPackets = 2,
            IsoPackets = [new UsbIpIsoPacket(0, 196, 0, 0), new UsbIpIsoPacket(196, 196, 0, 0)],
        });

        Assert.NotNull(result);
        Assert.Equal(UsbIpProtocol.StatusSuccess, result!.Value.Status);
        // 48 frames of two 16-bit channels: one millisecond at the rate the interface declares.
        Assert.All(result.Value.Packets, p => Assert.Equal(192, p.ActualLength));
        Assert.Equal(384, result.Value.Data.Length);
        Assert.All(result.Value.Data, b => Assert.Equal(0, b));
    }

    [Fact]
    public void AnEndpointWeNeverAdvertisedIsNotOurs()
    {
        using var device = TestDevices.CompositeDevice();

        Assert.Null(device.Isochronous(new UsbIpSubmit
        {
            Header = new UrbHeader(UsbIpProtocol.CmdSubmit, 1, 0, UsbIpProtocol.DirectionOut, 7),
            NumberOfPackets = 1,
            IsoPackets = [new UsbIpIsoPacket(0, 8, 0, 0)],
        }));
    }

    [Fact]
    public void WithHapticsOffThereIsNoIsochronousEndpointAtAll()
    {
        using var device = TestDevices.Device();

        Assert.False(device.IsComposite);
        Assert.Null(device.Haptics);
        Assert.Null(device.Isochronous(new UsbIpSubmit
        {
            Header = new UrbHeader(UsbIpProtocol.CmdSubmit, 1, 0, UsbIpProtocol.DirectionOut, 1),
            NumberOfPackets = 1,
            IsoPackets = [new UsbIpIsoPacket(0, 392, 0, 0)],
        }));
    }

    // MARK: - Audio class requests

    [Fact]
    public void TheSamplingFrequencyIsTheOneTheInterfaceDeclares()
    {
        using var device = TestDevices.CompositeDevice();

        // GET_CUR, endpoint recipient, sampling frequency control, endpoint 0x01.
        var result = device.Control(Setup(0xA2, 0x81, 0x0100, 0x0001, 3), ReadOnlySpan<byte>.Empty, 3);
        Assert.Equal(UsbIpProtocol.StatusSuccess, result.Status);
        Assert.Equal(new byte[] { 0x80, 0xBB, 0x00 }, result.Data);   // 48000, three bytes LE
    }

    [Fact]
    public void SettingTheDeclaredRateIsAcceptedAndAnyOtherIsRefused()
    {
        using var device = TestDevices.CompositeDevice();
        var setup = Setup(0x22, 0x01, 0x0100, 0x0001, 3);

        Assert.Equal(UsbIpProtocol.StatusSuccess,
            device.Control(setup, new byte[] { 0x80, 0xBB, 0x00 }, 3).Status);
        // 44100 is not on offer: the alternate setting names one discrete rate.
        Assert.Equal(UsbIpProtocol.StatusStall,
            device.Control(setup, new byte[] { 0x44, 0xAC, 0x00 }, 3).Status);
    }

    [Fact]
    public void VolumeReportsARangeAndRemembersWhatWasWritten()
    {
        using var device = TestDevices.CompositeDevice();
        const ushort feature = 0x0200;         // volume control, master channel
        const ushort unit = 0x0200;            // feature unit 2 on interface 0

        Assert.Equal(new byte[] { 0x00, 0xC4 },
            device.Control(Setup(0xA1, 0x82, feature, unit, 2), ReadOnlySpan<byte>.Empty, 2).Data);
        Assert.Equal(new byte[] { 0x00, 0x00 },
            device.Control(Setup(0xA1, 0x83, feature, unit, 2), ReadOnlySpan<byte>.Empty, 2).Data);
        Assert.Equal(new byte[] { 0x00, 0x01 },
            device.Control(Setup(0xA1, 0x84, feature, unit, 2), ReadOnlySpan<byte>.Empty, 2).Data);

        device.Control(Setup(0x21, 0x01, feature, unit, 2), new byte[] { 0x00, 0xF0 }, 2);
        Assert.Equal(new byte[] { 0x00, 0xF0 },
            device.Control(Setup(0xA1, 0x81, feature, unit, 2), ReadOnlySpan<byte>.Empty, 2).Data);
    }

    [Fact]
    public void MuteHasACurrentValueAndNoRange()
    {
        using var device = TestDevices.CompositeDevice();
        const ushort mute = 0x0100;
        const ushort unit = 0x0200;

        Assert.Equal(new byte[] { 0 },
            device.Control(Setup(0xA1, 0x81, mute, unit, 1), ReadOnlySpan<byte>.Empty, 1).Data);
        Assert.Equal(UsbIpProtocol.StatusStall,
            device.Control(Setup(0xA1, 0x82, mute, unit, 1), ReadOnlySpan<byte>.Empty, 1).Status);
    }

    /// <summary>
    /// The controller's feature units carry controls on the master channel only — their
    /// per-channel bitmaps are zero. Answering for channel 2 would be inventing a control the
    /// descriptor never claimed.
    /// </summary>
    [Fact]
    public void APerChannelControlIsRefusedBecauseTheDescriptorClaimsNone()
    {
        using var device = TestDevices.CompositeDevice();

        Assert.Equal(UsbIpProtocol.StatusStall,
            device.Control(Setup(0xA1, 0x81, 0x0202, 0x0200, 2), ReadOnlySpan<byte>.Empty, 2).Status);
    }

    [Fact]
    public void HidClassRequestsStillReachTheHidInterface()
    {
        using var device = TestDevices.CompositeDevice();

        // GET_REPORT(feature 0x05), the calibration snapshot Steam wants, on interface 3.
        var result = device.Control(Setup(0xA1, 0x01, 0x0305, 3, 41), ReadOnlySpan<byte>.Empty, 41);
        Assert.Equal(UsbIpProtocol.StatusSuccess, result.Status);
        Assert.Equal(0x05, result.Data[0]);
    }
}

/// <summary>
/// The demultiplexer: four channels of audio in, two channels of haptics on the wire.
/// </summary>
public class HapticStreamTests
{
    private static readonly UsbAudioStreamFormat DualSense = new(
        Interface: 1, AlternateSetting: 1, Endpoint: 0x01,
        Channels: 4, BytesPerSample: 2, SampleRate: 48000);

    private static HapticStream Stream(List<byte[]> sent, byte device = 0) =>
        new(device, DualSense, sent.Add);

    /// <summary>One frame of four channels, so the demultiplexer has something to get wrong.</summary>
    private static byte[] Frames(int count, Func<int, (short, short, short, short)> make)
    {
        var bytes = new byte[count * 8];
        for (var i = 0; i < count; i++)
        {
            var (a, b, c, d) = make(i);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 8), a);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 8 + 2), b);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 8 + 4), c);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 8 + 6), d);
        }
        return bytes;
    }

    [Fact]
    public void ABlockIsFiveMillisecondsOfTwoChannels()
    {
        var sent = new List<byte[]>();
        var stream = Stream(sent);

        Assert.True(stream.IsSupported);
        Assert.Equal(240, stream.FramesPerBlock);

        stream.Write(Frames(240, i => (1, 2, (short)(i + 1), (short)-(i + 1))));

        var payload = Assert.Single(sent);
        Assert.Equal(DeviceChannel.HapticHeaderSize + 240 * 2 * 2, payload.Length);
        Assert.True(payload.Length + 24 + 16 <= 1400, "блок обязан помещаться в датаграмму");
    }

    /// <summary>
    /// Channels 0 and 1 are the speaker and the headset. Forwarding them instead of the
    /// actuators would be a mistake nothing downstream could detect: the Mac would happily
    /// play a game's dialogue into the voice coils.
    /// </summary>
    [Fact]
    public void OnlyTheLastTwoChannelsAreForwarded()
    {
        var sent = new List<byte[]>();
        Stream(sent).Write(Frames(240, i => (100, 200, (short)(1000 + i), (short)(2000 + i))));

        Assert.True(DeviceChannel.TryReadHaptic(Assert.Single(sent), out var block));
        Assert.Equal(2, block.Channels);
        Assert.Equal(240 * 4, block.Pcm.Length);

        for (var i = 0; i < 240; i++)
        {
            Assert.Equal(1000 + i, BinaryPrimitives.ReadInt16LittleEndian(block.Pcm.AsSpan(i * 4)));
            Assert.Equal(2000 + i, BinaryPrimitives.ReadInt16LittleEndian(block.Pcm.AsSpan(i * 4 + 2)));
        }
    }

    [Fact]
    public void APartialBlockWaitsForTheRestOfIt()
    {
        var sent = new List<byte[]>();
        var stream = Stream(sent);

        // Two 49-frame packets, the way the endpoint really delivers them, then enough to
        // finish the block.
        stream.Write(Frames(49, _ => (0, 0, 1, 1)));
        stream.Write(Frames(49, _ => (0, 0, 1, 1)));
        Assert.Empty(sent);

        stream.Write(Frames(142, _ => (0, 0, 1, 1)));
        Assert.Single(sent);
        Assert.Equal(240, stream.FramesReceived - 0);
    }

    [Fact]
    public void ResetThrowsAwayThePartOfABlockThatWillNeverBeFinished()
    {
        var sent = new List<byte[]>();
        var stream = Stream(sent);

        stream.Write(Frames(200, _ => (0, 0, 1, 1)));
        stream.Reset();
        stream.Write(Frames(200, _ => (0, 0, 1, 1)));
        Assert.Empty(sent);

        stream.Write(Frames(40, _ => (0, 0, 1, 1)));
        Assert.Single(sent);
    }

    /// <summary>
    /// A game writes to this endpoint whether anything is happening or not, so forwarding
    /// silence would cost 1.6 Mbit/s of a link that is also carrying voice — to deliver zeros.
    /// One trailing block still goes, so the far end lands on silence rather than stopping on
    /// whatever it was holding.
    /// </summary>
    [Fact]
    public void SilenceGoesOutOnceAndThenStops()
    {
        var sent = new List<byte[]>();
        var stream = Stream(sent);

        for (var i = 0; i < 10; i++) stream.Write(Frames(240, _ => (0, 0, 0, 0)));

        Assert.Single(sent);
        Assert.Equal(9, stream.BlocksSkippedAsSilent);
        Assert.Equal(1, stream.BlocksSent);
    }

    /// <summary>
    /// Blocks are numbered, not counted, and the numbering runs through the silence that was
    /// never sent. So the gap the far end sees is exactly the time that belongs between two
    /// blocks — which is the same arithmetic that covers a block that was sent and lost.
    /// </summary>
    [Fact]
    public void TheBlockNumberCountsTheSilenceThatWasNotSent()
    {
        var sent = new List<byte[]>();
        var stream = Stream(sent);

        stream.Write(Frames(240, _ => (0, 0, 500, 500)));       // block 0, sent
        for (var i = 0; i < 20; i++) stream.Write(Frames(240, _ => (0, 0, 0, 0)));  // 1 sent, 19 skipped
        stream.Write(Frames(240, _ => (0, 0, 500, 500)));       // block 21, sent

        Assert.Equal(3, sent.Count);
        Assert.True(DeviceChannel.TryReadHaptic(sent[0], out var first));
        Assert.True(DeviceChannel.TryReadHaptic(sent[2], out var last));
        Assert.Equal(0u, first.Index);
        Assert.Equal(21u, last.Index);

        // 21 blocks of 5 ms is 105 ms of nothing, which is what the receiver has to insert.
        Assert.Equal(105u, (last.Index - first.Index) * (uint)HapticStream.BlockMilliseconds);
    }

    /// <summary>
    /// Real hardware cannot exceed the nominal rate — the endpoint is clocked by the bus — so
    /// this only catches a host that has gone wrong. Catching it matters anyway: the same
    /// socket is carrying somebody's voice and 250 input reports a second.
    /// </summary>
    [Fact]
    public void TheRateCeilingSheddsRatherThanCrowdingOutTheVoiceChannel()
    {
        var sent = new List<byte[]>();
        var stream = Stream(sent);

        var loud = Frames(240, i => (0, 0, (short)(i + 1), (short)(i + 1)));
        for (var i = 0; i < HapticStream.MaxBlocksPerSecond + 50; i++) stream.Write(loud);

        Assert.Equal(HapticStream.MaxBlocksPerSecond, sent.Count);
        Assert.Equal(50, stream.BlocksDropped);
    }

    /// <summary>
    /// The budget, checked rather than asserted in prose. Haptics travel beside voice and
    /// 250 input reports a second, over a relay that refuses more than 2000 packets a second
    /// from one address — so what this costs is a number somebody has to be able to look up.
    /// </summary>
    [Fact]
    public void ASecondOfHapticsFitsBesideTheVoiceAndTheInput()
    {
        var sent = new List<byte[]>();
        var stream = Stream(sent);

        var blocksPerSecond = 1000 / HapticStream.BlockMilliseconds;
        Assert.Equal(200, blocksPerSecond);

        stream.Write(Frames(240, i => (0, 0, (short)(i + 1), (short)(i + 1))));
        var datagram = Assert.Single(sent).Length + 24 + 16;   // header and GCM tag

        Assert.True(datagram <= 1400, $"датаграмма {datagram} байт не помещается в MTU");

        // 1.6 Mbit/s, and only while something is actually happening: a silent run costs
        // nothing at all, which is what makes this affordable next to a voice channel.
        var bytesPerSecond = datagram * blocksPerSecond;
        Assert.InRange(bytesPerSecond, 190_000, 210_000);

        // Voice is 51 packets a second and a forwarded pad is 250. Together with this the
        // relay's ceiling is still four times away.
        Assert.True(blocksPerSecond + 250 + 51 < 2000);
    }

    [Fact]
    public void AStreamWeCannotTakeApartForwardsNothingAndSaysSo()
    {
        var sent = new List<byte[]>();
        var stream = new HapticStream(
            0, DualSense with { BytesPerSample = 3 }, sent.Add);

        Assert.False(stream.IsSupported);
        stream.Write(new byte[240 * 12]);
        Assert.Empty(sent);
    }

    [Fact]
    public void ATwoChannelStreamIsAlreadyThePair()
    {
        var sent = new List<byte[]>();
        var stream = new HapticStream(0, DualSense with { Channels = 2 }, sent.Add);

        var frames = new byte[240 * 4];
        for (var i = 0; i < 240; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(frames.AsSpan(i * 4), (short)(i + 1));
            BinaryPrimitives.WriteInt16LittleEndian(frames.AsSpan(i * 4 + 2), (short)-(i + 1));
        }
        stream.Write(frames);

        Assert.True(DeviceChannel.TryReadHaptic(Assert.Single(sent), out var block));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(block.Pcm));
    }
}

/// <summary>The HAPTIC payload codec, both directions of it.</summary>
public class HapticChannelTests
{
    [Fact]
    public void ABlockRoundTrips()
    {
        var pcm = new byte[240 * 4];
        for (var i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i * 7);

        var payload = DeviceChannel.WriteHaptic(2, 2, 0xDEADBEEF, pcm);
        Assert.Equal(2, payload[0]);
        Assert.Equal(2, payload[1]);
        Assert.Equal(0xDEADBEEF, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(2)));

        Assert.True(DeviceChannel.TryReadHaptic(payload, out var block));
        Assert.Equal(2, block.Device);
        Assert.Equal(2, block.Channels);
        Assert.Equal(0xDEADBEEF, block.Index);
        Assert.Equal(pcm, block.Pcm);
    }

    [Fact]
    public void AnEmptyBlockIsStillAValidOne()
    {
        Assert.True(DeviceChannel.TryReadHaptic(DeviceChannel.WriteHaptic(0, 2, 5, []), out var block));
        Assert.Empty(block.Pcm);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void ATruncatedPayloadIsRefused(int length) =>
        Assert.False(DeviceChannel.TryReadHaptic(new byte[length], out _));

    /// <summary>
    /// A payload that does not divide into whole frames is one we would have to guess at, and
    /// a guess here is a click in somebody's hand.
    /// </summary>
    [Fact]
    public void APayloadThatDoesNotDivideIntoFramesIsRefused()
    {
        // Ten bytes of a two-channel block is two and a half frames.
        Assert.False(DeviceChannel.TryReadHaptic(DeviceChannel.WriteHaptic(0, 2, 1, new byte[10]), out _));
        Assert.True(DeviceChannel.TryReadHaptic(DeviceChannel.WriteHaptic(0, 2, 1, new byte[12]), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void AnImpossibleChannelCountIsRefused(byte channels)
    {
        var payload = DeviceChannel.WriteHaptic(0, channels, 1, new byte[8]);
        Assert.False(DeviceChannel.TryReadHaptic(payload, out _));
    }
}

/// <summary>
/// The isochronous path driven over a real loopback socket, by a client written from the
/// specification. The thing being tested is the framing: an isochronous URB carries a
/// variable amount of data and a variable number of descriptors, and a server that
/// miscounts either one leaves a TCP stream that never resynchronises.
/// </summary>
public class IsochronousServerTests : IAsyncLifetime
{
    private readonly List<byte[]> _haptics = [];
    private VirtualHidDevice _device = null!;
    private UsbIpServer _server = null!;

    public Task InitializeAsync()
    {
        _device = TestDevices.CompositeDevice(onHaptic: _haptics.Add);
        _server = new UsbIpServer(() => [_device], (_, _) => { });
        _server.Start(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _device.Dispose();
    }

    private async Task<UsbIpTestClient> ImportedAsync()
    {
        var client = await UsbIpTestClient.ConnectAsync(_server.LocalEndPoint!);
        var (header, device) = await client.ImportAsync(_device.Info.BusId);
        Assert.Equal(UsbIpProtocol.StatusOk, header.Status);
        Assert.NotNull(device);
        return client;
    }

    [Fact]
    public async Task AnIsochronousOutIsAcceptedWithOneDescriptorPerMicroframe()
    {
        await using var client = await ImportedAsync();

        var seqnum = await client.IsochronousAsync(
            0x01, UsbIpProtocol.DirectionOut, packets: 8, packetLength: 392);
        var reply = await client.ReadSubmitReplyAsync();

        Assert.Equal(seqnum, reply.Seqnum);
        Assert.Equal(UsbIpProtocol.StatusSuccess, reply.Status);
        Assert.Equal(8, reply.NumberOfPackets);
        Assert.Equal(8, reply.IsoPackets.Count);
        Assert.Equal(0, reply.ErrorCount);
        Assert.Equal(8 * 392, reply.ActualLength);
        Assert.All(reply.IsoPackets, p => Assert.Equal(392, p.ActualLength));

        // The offsets an OUT reply reports are the host's own: it laid the buffer out.
        Assert.Equal(0, reply.IsoPackets[0].Offset);
        Assert.Equal(392, reply.IsoPackets[1].Offset);
    }

    [Fact]
    public async Task AnIsochronousInComesBackPackedWithItsOffsetsComputed()
    {
        await using var client = await ImportedAsync();

        await client.IsochronousAsync(0x82, UsbIpProtocol.DirectionIn, packets: 4, packetLength: 196);
        var reply = await client.ReadSubmitReplyAsync();

        Assert.Equal(UsbIpProtocol.StatusSuccess, reply.Status);
        Assert.Equal(4, reply.IsoPackets.Count);
        Assert.Equal(4 * 192, reply.ActualLength);
        Assert.Equal(4 * 192, reply.Data.Length);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(i * 192, reply.IsoPackets[i].Offset);
            Assert.Equal(192, reply.IsoPackets[i].ActualLength);
        }
    }

    /// <summary>
    /// The whole path, over the socket: five milliseconds of PCM in on the isochronous
    /// endpoint, one HAPTIC packet out on the wire, with only the actuator channels in it.
    /// </summary>
    [Fact]
    public async Task PcmOnTheEndpointBecomesAHapticPacket()
    {
        await using var client = await ImportedAsync();

        var frames = 240;
        var buffer = new byte[frames * 8];
        for (var i = 0; i < frames; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 8), 7);              // speaker L
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 8 + 2), 7);          // speaker R
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 8 + 4), (short)(i + 1));
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 8 + 6), (short)-(i + 1));
        }

        await client.IsochronousAsync(
            0x01, UsbIpProtocol.DirectionOut, packets: 1, packetLength: buffer.Length, data: buffer);
        var reply = await client.ReadSubmitReplyAsync();
        Assert.Equal(UsbIpProtocol.StatusSuccess, reply.Status);

        var payload = Assert.Single(_haptics);
        Assert.True(DeviceChannel.TryReadHaptic(payload, out var block));
        Assert.Equal(2, block.Channels);
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(block.Pcm));
        Assert.Equal(-1, BinaryPrimitives.ReadInt16LittleEndian(block.Pcm.AsSpan(2)));
    }

    /// <summary>
    /// The stream has to survive an isochronous URB whatever the answer was. If the server
    /// left one descriptor unread, the next URB would be parsed out of the middle of it — and
    /// the symptom would be a controller that vanished, not a sound that did not play.
    /// </summary>
    [Fact]
    public async Task TheStreamStaysInSyncAfterAnIsochronousUrb()
    {
        await using var client = await ImportedAsync();

        await client.IsochronousAsync(0x01, UsbIpProtocol.DirectionOut, packets: 8, packetLength: 392);
        await client.ReadSubmitReplyAsync();

        // GET_DESCRIPTOR(device) right behind it: a header parsed one byte out of place
        // cannot possibly answer this.
        await client.ControlInAsync(TestDevices.Setup(0x80, 0x06, 0x0100, 0, 18), 18);
        var reply = await client.ReadSubmitReplyAsync();

        Assert.Equal(UsbIpProtocol.StatusSuccess, reply.Status);
        Assert.Equal(18, reply.ActualLength);
        Assert.Equal(0x12, reply.Data[0]);
        Assert.Equal(UsbDescriptorType.Device, reply.Data[1]);
    }

    /// <summary>
    /// A HID-only device has no isochronous endpoint, and a URB for one is refused. It still
    /// comes back with a full set of descriptors: vhci compares the count against the URB it
    /// is still holding and abandons the session when they disagree, so a refusal that
    /// dropped them would cost the controller as well as the sound.
    /// </summary>
    [Fact]
    public async Task ARefusedIsochronousUrbStillCarriesItsDescriptors()
    {
        using var hidOnly = TestDevices.Device(number: 1);
        await using var server = new UsbIpServer(() => [hidOnly], (_, _) => { });
        server.Start(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));

        await using var client = await UsbIpTestClient.ConnectAsync(server.LocalEndPoint!);
        var (header, _) = await client.ImportAsync(hidOnly.Info.BusId);
        Assert.Equal(UsbIpProtocol.StatusOk, header.Status);

        await client.IsochronousAsync(0x01, UsbIpProtocol.DirectionOut, packets: 4, packetLength: 64);
        var reply = await client.ReadSubmitReplyAsync();

        Assert.Equal(UsbIpProtocol.StatusStall, reply.Status);
        Assert.Equal(4, reply.NumberOfPackets);
        Assert.Equal(4, reply.IsoPackets.Count);
        Assert.Equal(4, reply.ErrorCount);
        Assert.Equal(0, reply.ActualLength);
        Assert.All(reply.IsoPackets, p => Assert.Equal(UsbIpProtocol.StatusStall, p.Status));

        // And the session is still usable.
        await client.ControlInAsync(TestDevices.Setup(0x80, 0x06, 0x0100, 0, 18), 18);
        Assert.Equal(18, (await client.ReadSubmitReplyAsync()).ActualLength);
    }

    /// <summary>
    /// A count that large is a corrupt header, and the descriptors it claims are the only
    /// thing that says where the next URB begins. There is nothing to do but close the
    /// session — and closing it is what must happen, rather than allocating what was asked
    /// for or reading past the frame.
    /// </summary>
    [Fact]
    public async Task AnAbsurdPacketCountClosesTheSessionInsteadOfAllocating()
    {
        await using var client = await ImportedAsync();

        var bytes = new byte[UsbIpProtocol.UrbHeaderSize];
        new UrbHeader(UsbIpProtocol.CmdSubmit, 99, 0x00010001, UsbIpProtocol.DirectionOut, 1).Write(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32), 0x40000000);
        await client.WriteRawAsync(bytes);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            // Either the read fails because the server hung up, or nothing ever arrives.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ReadReplyAsync().WaitAsync(timeout.Token);
        });
    }
}
