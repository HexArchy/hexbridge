using System.Buffers.Binary;

using HexBridge.Localization;

namespace HexBridge.Devices;

/// <summary>USB descriptor type codes we care about.</summary>
public static class UsbDescriptorType
{
    public const byte Device = 0x01;
    public const byte Configuration = 0x02;
    public const byte String = 0x03;
    public const byte Interface = 0x04;
    public const byte Endpoint = 0x05;
    public const byte InterfaceAssociation = 0x0B;
    public const byte Hid = 0x21;
    public const byte HidReport = 0x22;
}

/// <summary>The fields of an 18-byte device descriptor that anything downstream needs.</summary>
public sealed record UsbDeviceDescriptor
{
    public required byte[] Bytes { get; init; }
    public ushort IdVendor { get; init; }
    public ushort IdProduct { get; init; }
    public ushort BcdDevice { get; init; }
    public byte DeviceClass { get; init; }
    public byte DeviceSubClass { get; init; }
    public byte DeviceProtocol { get; init; }
    public byte ManufacturerString { get; init; }
    public byte ProductString { get; init; }
    public byte SerialString { get; init; }
    public byte NumConfigurations { get; init; }

    public static UsbDeviceDescriptor Parse(ReadOnlySpan<byte> src)
    {
        if (src.Length < 18 || src[1] != UsbDescriptorType.Device)
        {
            throw new FormatException(Loc.F(Strings.Err_Usb_DeviceDescriptor, Loc.Bytes(src.Length)));
        }

        return new UsbDeviceDescriptor
        {
            Bytes = src[..18].ToArray(),
            DeviceClass = src[4],
            DeviceSubClass = src[5],
            DeviceProtocol = src[6],
            IdVendor = BinaryPrimitives.ReadUInt16LittleEndian(src[8..]),
            IdProduct = BinaryPrimitives.ReadUInt16LittleEndian(src[10..]),
            BcdDevice = BinaryPrimitives.ReadUInt16LittleEndian(src[12..]),
            ManufacturerString = src[14],
            ProductString = src[15],
            SerialString = src[16],
            // Whatever the real controller says, the device we build has exactly one.
            NumConfigurations = 1,
        };
    }
}

/// <summary>An isochronous endpoint, as the configuration descriptor declares it.</summary>
public readonly record struct UsbIsochronousEndpoint(
    byte Address, int MaxPacketSize, byte Interface, byte AlternateSetting)
{
    public bool IsIn => (Address & 0x80) != 0;

    /// <summary>The number a URB addresses it by: the address without its direction bit.</summary>
    public int Number => Address & 0x0F;
}

/// <summary>
/// The PCM format one audio-streaming alternate setting carries.
///
/// Format type I only, which is the whole of what a DualSense uses and the only kind whose
/// frame size is a fixed number of bytes. Anything else is left unparsed: a format we cannot
/// take apart is a stream we cannot demultiplex, and guessing at it would put noise into
/// somebody's hands.
/// </summary>
public readonly record struct UsbAudioStreamFormat(
    byte Interface, byte AlternateSetting, byte Endpoint, int Channels, int BytesPerSample, int SampleRate)
{
    public int BytesPerFrame => Channels * BytesPerSample;
}

/// <summary>
/// The configuration descriptor as we present it to Windows.
///
/// Two shapes, chosen by the haptics switch and by nothing else.
///
/// **HID only**, the default. Plenty of forwardable hardware is composite: a DualSense puts
/// audio interfaces for the headset jack and the HD haptics ahead of its HID interface;
/// wheels bring force-feedback and hub interfaces along. Serving those would mean serving
/// endpoints the device channel does not carry, and Windows would sit waiting on a pin that
/// never speaks. So the configuration is rebuilt around the one interface we can honour,
/// renumbered to 0: a configuration that claims one interface and calls it number 3 is one
/// Windows is entitled to distrust.
///
/// **Composite**, when haptics are on. The descriptor goes through byte for byte, audio
/// function included, because HD haptics are not a HID report — they are PCM written to an
/// isochronous endpoint, and the endpoint only exists if the interface that declares it does.
/// Byte for byte rather than trimmed: usbaudio.sys builds its topology out of terminal and
/// unit descriptors that reference each other by id, and a configuration with one interface
/// cut out of it is a graph with a dangling edge. Passing the real thing through means
/// Windows sees exactly what it would see with the controller plugged into it directly.
///
/// The price is that the audio path exists at all, which is why it is opt-in: usbip-win2 has
/// an open bug (#181) in the lifetime of a request on that path, and a device with no audio
/// function cannot reach it.
///
/// Either way the Mac opens the lowest-numbered HID interface of a device: both ends have to
/// mean the same interface by "the HID one".
/// </summary>
public sealed record UsbConfigurationDescriptor
{
    /// <summary>What answers GET_DESCRIPTOR(configuration): rebuilt, or the original verbatim.</summary>
    public required byte[] Bytes { get; init; }

    public byte ConfigurationValue { get; init; }

    /// <summary>The HID interface. The only one there is unless this is a composite.</summary>
    public UsbIpInterfaceInfo Interface { get; init; }

    /// <summary>Every interface we serve, alternate setting 0, in descriptor order.</summary>
    public IReadOnlyList<UsbIpInterfaceInfo> Interfaces { get; init; } = [];

    /// <summary>True when the audio function came through with the HID interface.</summary>
    public bool IsComposite { get; init; }

    /// <summary>
    /// Which interface the HID descriptors belong to: 0 after a rebuild, whatever the
    /// hardware said in a composite. Class requests are routed by it.
    /// </summary>
    public byte HidInterfaceNumber { get; init; }

    /// <summary>The HID class descriptor (type 0x21) from inside the interface, if it has one.</summary>
    public byte[]? HidDescriptor { get; init; }

    /// <summary>Endpoint addresses, e.g. 0x84 and 0x03 on a DualSense.</summary>
    public int InterruptInEndpoint { get; init; }
    public int InterruptOutEndpoint { get; init; }

    /// <summary>Largest packet the IN endpoint declares; a report is never longer.</summary>
    public int InterruptInMaxPacket { get; init; }

    /// <summary>Empty unless this is a composite; a DualSense contributes two.</summary>
    public IReadOnlyList<UsbIsochronousEndpoint> IsochronousEndpoints { get; init; } = [];

    /// <summary>
    /// The outgoing PCM stream: four channels at 48 kHz on a DualSense, of which the last two
    /// are the voice coils. Null when there is no audio-streaming OUT to be had.
    /// </summary>
    public UsbAudioStreamFormat? OutputStream { get; init; }

    /// <summary>The incoming one — the controller's microphone. Answered with silence.</summary>
    public UsbAudioStreamFormat? InputStream { get; init; }

    public static UsbConfigurationDescriptor FromFullDescriptor(ReadOnlySpan<byte> full, bool withAudio = false)
    {
        if (full.Length < 9 || full[1] != UsbDescriptorType.Configuration)
        {
            throw new FormatException(Loc.F(Strings.Err_Usb_ConfigDescriptor, Loc.Bytes(full.Length)));
        }

        // wTotalLength is what the device meant to send; trust the shorter of the two so a
        // truncated read cannot walk off the end.
        var total = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(full[2..]), full.Length);

        return withAudio ? Composite(full, total) : HidOnly(full, total);
    }

    private static UsbConfigurationDescriptor HidOnly(ReadOnlySpan<byte> full, int total)
    {
        var kept = new List<byte[]>();
        var keeping = false;
        var done = false;
        byte[]? hid = null;
        var endpointIn = 0;
        var endpointOut = 0;
        var maxPacket = 64;
        var iface = default(UsbIpInterfaceInfo);

        var offset = (int)full[0];  // skip the configuration header
        while (offset + 2 <= total)
        {
            int length = full[offset];
            var type = full[offset + 1];
            if (length < 2 || offset + length > total) break;

            var descriptor = full.Slice(offset, length);
            offset += length;

            if (type == UsbDescriptorType.Interface)
            {
                if (keeping)
                {
                    // The next interface starts here, so the one we kept is complete.
                    done = true;
                    keeping = false;
                    continue;
                }
                if (done || length < 9) continue;
                if (descriptor[5] != 0x03 || descriptor[3] != 0) continue;

                iface = new UsbIpInterfaceInfo(descriptor[5], descriptor[6], descriptor[7]);

                var copy = descriptor.ToArray();
                copy[2] = 0;  // bInterfaceNumber
                copy[3] = 0;  // bAlternateSetting
                kept.Add(copy);
                keeping = true;
                continue;
            }

            if (!keeping) continue;

            // An interface association only ever describes the audio function here.
            if (type == UsbDescriptorType.InterfaceAssociation) continue;

            if (type == UsbDescriptorType.Hid)
            {
                hid = descriptor.ToArray();
            }
            else if (type == UsbDescriptorType.Endpoint && length >= 7 && (descriptor[3] & 0x03) == 0x03)
            {
                var address = descriptor[2];
                if ((address & 0x80) != 0)
                {
                    endpointIn = address;
                    maxPacket = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[4..]) & 0x7FF;
                }
                else
                {
                    endpointOut = address;
                }
            }

            kept.Add(descriptor.ToArray());
        }

        if (kept.Count == 0) throw new FormatException(Strings.Err_Usb_NoHidInterface);

        var body = kept.Sum(d => d.Length);
        var bytes = new byte[9 + body];
        full[..9].CopyTo(bytes);
        bytes[4] = 1;  // bNumInterfaces
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)bytes.Length);

        var at = 9;
        foreach (var descriptor in kept)
        {
            descriptor.CopyTo(bytes, at);
            at += descriptor.Length;
        }

        return new UsbConfigurationDescriptor
        {
            Bytes = bytes,
            ConfigurationValue = bytes[5],
            Interface = iface,
            Interfaces = [iface],
            HidInterfaceNumber = 0,
            HidDescriptor = hid,
            // A HID interface without an interrupt IN endpoint is not a gamepad, but the
            // fallbacks keep the device usable rather than refusing to build at all.
            InterruptInEndpoint = endpointIn != 0 ? endpointIn : 0x84,
            InterruptOutEndpoint = endpointOut != 0 ? endpointOut : 0x03,
            InterruptInMaxPacket = maxPacket,
        };
    }

    /// <summary>
    /// The whole configuration, verbatim, read rather than rewritten.
    ///
    /// Nothing is copied out and reassembled here, so there is nothing to get wrong in the
    /// bytes: the walk only has to find the four things the rest of the code needs — which
    /// interface is the HID one, where its interrupt pair is, which endpoints are
    /// isochronous, and what PCM format the streaming interfaces declare.
    /// </summary>
    private static UsbConfigurationDescriptor Composite(ReadOnlySpan<byte> full, int total)
    {
        var bytes = full[..total].ToArray();

        var interfaces = new List<UsbIpInterfaceInfo>();
        var isochronous = new List<UsbIsochronousEndpoint>();
        UsbAudioStreamFormat? outputStream = null;
        UsbAudioStreamFormat? inputStream = null;

        byte[]? hid = null;
        byte hidInterface = 0;
        var haveHid = false;
        var endpointIn = 0;
        var endpointOut = 0;
        var maxPacket = 64;

        // The interface descriptor currently in scope. Everything between one interface
        // descriptor and the next belongs to it, which is the whole of USB's grammar for
        // "these endpoints are that interface's".
        byte number = 0, alternate = 0, interfaceClass = 0, interfaceSubClass = 0;
        var inInterface = false;
        // Format of the alternate setting in scope, filled in before its endpoint appears.
        int channels = 0, bytesPerSample = 0, sampleRate = 0;

        var offset = (int)full[0];
        while (offset + 2 <= total)
        {
            int length = full[offset];
            var type = full[offset + 1];
            if (length < 2 || offset + length > total) break;

            var descriptor = full.Slice(offset, length);
            offset += length;

            if (type == UsbDescriptorType.Interface && length >= 9)
            {
                number = descriptor[2];
                alternate = descriptor[3];
                interfaceClass = descriptor[5];
                interfaceSubClass = descriptor[6];
                inInterface = true;
                channels = bytesPerSample = sampleRate = 0;

                if (alternate == 0)
                {
                    interfaces.Add(new UsbIpInterfaceInfo(descriptor[5], descriptor[6], descriptor[7]));
                }

                // The first HID interface, exactly as the HID-only path picks it: both ends
                // have to mean the same one by "the HID interface".
                if (!haveHid && interfaceClass == 0x03 && alternate == 0)
                {
                    haveHid = true;
                    hidInterface = number;
                }
                continue;
            }

            if (!inInterface) continue;

            if (type == UsbDescriptorType.Hid && number == hidInterface && haveHid)
            {
                hid = descriptor.ToArray();
                continue;
            }

            // CS_INTERFACE inside an audio-streaming interface: subtype 2 is FORMAT_TYPE,
            // and format type I is the only one whose frames are a fixed number of bytes.
            if (type == 0x24 && interfaceClass == 0x01 && interfaceSubClass == 0x02
                && length >= 11 && descriptor[2] == 0x02 && descriptor[3] == 0x01)
            {
                channels = descriptor[4];
                bytesPerSample = descriptor[5];
                // bSamFreqType 1 means one discrete rate, spelled out in three bytes. A
                // continuous range would need a SET_CUR to pin down and no controller uses one.
                sampleRate = descriptor[7] == 1
                    ? descriptor[8] | (descriptor[9] << 8) | (descriptor[10] << 16)
                    : 0;
                continue;
            }

            if (type != UsbDescriptorType.Endpoint || length < 7) continue;

            var address = descriptor[2];
            var transfer = descriptor[3] & 0x03;
            var packet = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[4..]) & 0x7FF;

            if (transfer == 0x03 && number == hidInterface && haveHid)
            {
                if ((address & 0x80) != 0)
                {
                    endpointIn = address;
                    maxPacket = packet;
                }
                else
                {
                    endpointOut = address;
                }
                continue;
            }

            if (transfer != 0x01) continue;

            isochronous.Add(new UsbIsochronousEndpoint(address, packet, number, alternate));
            if (channels <= 0 || bytesPerSample <= 0) continue;

            var format = new UsbAudioStreamFormat(
                number, alternate, address, channels, bytesPerSample, sampleRate);
            if ((address & 0x80) != 0) inputStream ??= format; else outputStream ??= format;
        }

        if (!haveHid) throw new FormatException(Strings.Err_Usb_NoHidInterface);

        return new UsbConfigurationDescriptor
        {
            Bytes = bytes,
            ConfigurationValue = bytes[5],
            Interface = interfaces.FirstOrDefault(i => i.Class == 0x03),
            Interfaces = interfaces,
            IsComposite = true,
            HidInterfaceNumber = hidInterface,
            HidDescriptor = hid,
            InterruptInEndpoint = endpointIn != 0 ? endpointIn : 0x84,
            InterruptOutEndpoint = endpointOut != 0 ? endpointOut : 0x03,
            InterruptInMaxPacket = maxPacket,
            IsochronousEndpoints = isochronous,
            OutputStream = outputStream,
            InputStream = inputStream,
        };
    }
}
