using HexBridge;
using HexBridge.Devices;

namespace HexBridge.Tests;

/// <summary>
/// Hand-built descriptors shaped like a real DualSense: a composite device whose audio
/// function comes first and whose HID interface is number 3. Everything the forwarding
/// code has to get right — skipping the audio interfaces, renumbering, finding endpoint
/// 0x84 — depends on that shape, so the fixture keeps it.
/// </summary>
public static class TestDevices
{
    public const ushort Vendor = 0x054C;
    public const ushort Product = 0x0CE6;
    public const ushort BcdDevice = 0x0100;

    public const int ReportDescriptorLength = 273;
    public const int InputReportLength = 64;
    public const int OutputReportLength = 48;

    /// <summary>A wheel-shaped stand-in: a real VID/PID that no profile knows.</summary>
    public const ushort UnknownVendor = 0x046D;
    public const ushort UnknownProduct = 0xC29B;

    public static byte[] DeviceDescriptor(ushort vendor = Vendor, ushort product = Product) =>
    [
        0x12, 0x01,             // bLength, bDescriptorType
        0x00, 0x02,             // bcdUSB 2.00
        0x00, 0x00, 0x00,       // class, subclass, protocol: per-interface
        0x40,                   // bMaxPacketSize0
        (byte)(vendor & 0xFF), (byte)(vendor >> 8),     // idVendor, little-endian
        (byte)(product & 0xFF), (byte)(product >> 8),   // idProduct
        0x00, 0x01,             // bcdDevice 0100
        0x01, 0x02, 0x00,       // iManufacturer, iProduct, iSerialNumber (none)
        0x01,                   // bNumConfigurations
    ];

    /// <summary>
    /// Four interfaces: audio control, audio streaming with an isochronous endpoint, an
    /// alternate setting, then HID as interface 3 with its interrupt pair.
    /// </summary>
    public static byte[] ConfigurationDescriptor()
    {
        var body = new List<byte>();

        // Interface association for the audio function.
        body.AddRange([0x08, 0x0B, 0x00, 0x03, 0x01, 0x01, 0x00, 0x00]);
        // Interface 0: audio control.
        body.AddRange([0x09, 0x04, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00]);
        // Interface 1: audio streaming, alternate 0, no endpoints.
        body.AddRange([0x09, 0x04, 0x01, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00]);
        // Interface 1, alternate 1, with the isochronous OUT that carries HD haptics.
        body.AddRange([0x09, 0x04, 0x01, 0x01, 0x01, 0x01, 0x02, 0x00, 0x00]);
        body.AddRange([0x09, 0x05, 0x01, 0x0D, 0x88, 0x01, 0x04, 0x00, 0x00]);
        // Interface 3: HID.
        body.AddRange([0x09, 0x04, 0x03, 0x00, 0x02, 0x03, 0x00, 0x00, 0x00]);
        body.AddRange([0x09, 0x21, 0x11, 0x01, 0x00, 0x01, 0x22, 0x11, 0x01]);
        body.AddRange([0x07, 0x05, 0x84, 0x03, 0x40, 0x00, 0x06]);
        body.AddRange([0x07, 0x05, 0x03, 0x03, 0x40, 0x00, 0x06]);

        var total = 9 + body.Count;
        var bytes = new List<byte>
        {
            0x09, 0x02,
            (byte)(total & 0xFF), (byte)(total >> 8),
            0x04,   // bNumInterfaces
            0x01,   // bConfigurationValue
            0x00,   // iConfiguration
            0xC0,   // bmAttributes: self-powered
            0xFA,   // bMaxPower
        };
        bytes.AddRange(body);
        return [.. bytes];
    }

    public static byte[] ReportDescriptor()
    {
        // The bytes themselves are opaque to everything under test; only their identity
        // and length matter, so a recognisable pattern beats 273 bytes of copied hex.
        var bytes = new byte[ReportDescriptorLength];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i ^ 0x5A);
        bytes[0] = 0x05;
        bytes[1] = 0x01;
        return bytes;
    }

    /// <summary>A feature report snapshot: report id first, then the report as read.</summary>
    public static byte[] FeatureReport(byte id, int length)
    {
        var bytes = new byte[length];
        bytes[0] = id;
        for (var i = 1; i < length; i++) bytes[i] = (byte)(id + i);
        return bytes;
    }

    public static byte[] InputReport(byte counter = 0, byte battery = 0x08)
    {
        var bytes = new byte[InputReportLength];
        bytes[0] = 0x01;
        bytes[1] = 0x80;  // left stick x, centred
        bytes[2] = 0x80;
        bytes[7] = counter;
        bytes[53] = battery;
        return bytes;
    }

    public static byte[] OutputReport(byte rumble = 0x40)
    {
        var bytes = new byte[OutputReportLength];
        bytes[0] = 0x02;
        bytes[3] = rumble;
        return bytes;
    }

    public static DeviceAttach Attach(
        byte device = 0, bool withFeatures = true, ushort vendor = Vendor, ushort product = Product)
    {
        var blocks = new List<DescriptorBlock>
        {
            new(DescriptorKind.Device, DeviceDescriptor(vendor, product)),
            new(DescriptorKind.Configuration, ConfigurationDescriptor()),
            new(DescriptorKind.HidReport, ReportDescriptor()),
        };

        if (withFeatures)
        {
            blocks.Add(new DescriptorBlock(DescriptorKind.FeatureReport, FeatureReport(0x05, 41)));
            blocks.Add(new DescriptorBlock(DescriptorKind.FeatureReport, FeatureReport(0x09, 20)));
            blocks.Add(new DescriptorBlock(DescriptorKind.FeatureReport, FeatureReport(0x20, 64)));
        }

        return new DeviceAttach(device, blocks);
    }

    public static VirtualHidDevice Device(
        Action<byte[]>? onOutput = null, byte number = 0,
        ushort vendor = Vendor, ushort product = Product) =>
        new(Attach(number, vendor: vendor, product: product), onOutput ?? (_ => { }));

    public static byte[] Setup(byte requestType, byte request, ushort value, ushort index, ushort length) =>
    [
        requestType, request,
        (byte)(value & 0xFF), (byte)(value >> 8),
        (byte)(index & 0xFF), (byte)(index >> 8),
        (byte)(length & 0xFF), (byte)(length >> 8),
    ];
}
