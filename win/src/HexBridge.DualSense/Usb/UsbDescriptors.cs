using System.Buffers.Binary;

namespace HexBridge.DualSense;

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
            throw new FormatException($"дескриптор устройства повреждён ({src.Length} байт)");
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

/// <summary>
/// The configuration descriptor as we present it to Windows: the HID interface and nothing
/// else.
///
/// The real DualSense is a composite device — audio interfaces for the headset jack and the
/// HD haptics, then HID. Passing those through would mean serving isochronous endpoints we
/// do not carry over the wire, and Windows would wait on an audio pin that never speaks. So
/// the configuration is rebuilt around the one interface we can honour, renumbered to 0:
/// a configuration that claims one interface and calls it number 3 is one Windows is
/// entitled to distrust.
/// </summary>
public sealed record UsbConfigurationDescriptor
{
    /// <summary>The rebuilt descriptor, ready to answer GET_DESCRIPTOR(configuration).</summary>
    public required byte[] Bytes { get; init; }

    public byte ConfigurationValue { get; init; }
    public UsbIpInterfaceInfo Interface { get; init; }

    /// <summary>The HID class descriptor (type 0x21) from inside the interface, if it has one.</summary>
    public byte[]? HidDescriptor { get; init; }

    /// <summary>Endpoint addresses, e.g. 0x84 and 0x03 on a DualSense.</summary>
    public int InterruptInEndpoint { get; init; }
    public int InterruptOutEndpoint { get; init; }

    /// <summary>Largest packet the IN endpoint declares; a report is never longer.</summary>
    public int InterruptInMaxPacket { get; init; }

    public static UsbConfigurationDescriptor FromFullDescriptor(ReadOnlySpan<byte> full)
    {
        if (full.Length < 9 || full[1] != UsbDescriptorType.Configuration)
        {
            throw new FormatException($"дескриптор конфигурации повреждён ({full.Length} байт)");
        }

        // wTotalLength is what the device meant to send; trust the shorter of the two so a
        // truncated read cannot walk off the end.
        var total = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(full[2..]), full.Length);

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

        if (kept.Count == 0) throw new FormatException("в конфигурации нет HID-интерфейса");

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
            HidDescriptor = hid,
            // A HID interface without an interrupt IN endpoint is not a gamepad, but the
            // fallbacks keep the device usable rather than refusing to build at all.
            InterruptInEndpoint = endpointIn != 0 ? endpointIn : 0x84,
            InterruptOutEndpoint = endpointOut != 0 ? endpointOut : 0x03,
            InterruptInMaxPacket = maxPacket,
        };
    }
}
