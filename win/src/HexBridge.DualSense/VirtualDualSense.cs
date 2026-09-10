using System.Buffers.Binary;
using System.Text;

namespace HexBridge.DualSense;

/// <summary>
/// The virtual USB device Windows enumerates, assembled entirely from what one DEV_ATTACH
/// carried: the real controller's descriptors and a snapshot of its feature reports.
///
/// Nothing here is invented except the string descriptors, which the device channel does
/// not carry today. Everything a game or Steam asks for is either a descriptor byte the Mac
/// read off the hardware or a feature report it captured before the bridge came up.
/// </summary>
public sealed class VirtualDualSense : IUsbIpDevice, IDisposable
{
    /// <summary>US English. Windows asks for the language list before any string.</summary>
    private static readonly byte[] LanguageDescriptor = [0x04, UsbDescriptorType.String, 0x09, 0x04];

    private readonly UsbDeviceDescriptor _device;
    private readonly UsbConfigurationDescriptor _configuration;
    private readonly byte[] _reportDescriptor;
    private readonly Dictionary<byte, byte[]> _featureReports;
    private readonly Dictionary<byte, byte[]> _stringDescriptors;
    private readonly ReportPipe _pipe = new();
    private readonly Action<byte[]> _onOutputReport;

    private byte[]? _lastInput;
    private byte[]? _lastOutput;
    private byte _configurationValue;
    private byte _idle;
    private byte _protocol = 1;

    public UsbIpDeviceInfo Info { get; }
    public int InterruptInEndpoint => _configuration.InterruptInEndpoint;
    public int InterruptOutEndpoint => _configuration.InterruptOutEndpoint;

    /// <summary>Device number on the wire, 0..3.</summary>
    public byte DeviceNumber { get; }

    public string ProductName { get; }
    public ushort VendorId => _device.IdVendor;
    public ushort ProductId => _device.IdProduct;

    /// <summary>Input reports handed to the pipe; not the same as reports Windows collected.</summary>
    public long InputReports { get; private set; }

    /// <summary>Output reports sent back to the Mac, after coalescing.</summary>
    public long OutputReports { get; private set; }

    public long DroppedReports => _pipe.Dropped;

    /// <summary>Battery as the last input report described it, or null before the first one.</summary>
    public string? Battery { get; private set; }

    /// <summary>
    /// The live feed the DualSense screen draws from. Publishing into it is one volatile
    /// write, which is what keeps a 60 Hz visualisation off the 250 Hz report path.
    /// </summary>
    public DualSenseInputSource Input { get; } = new();

    public VirtualDualSense(DeviceAttach attach, Action<byte[]> onOutputReport)
    {
        _onOutputReport = onOutputReport;
        DeviceNumber = attach.Device;

        var deviceBytes = attach.First(DescriptorKind.Device)
            ?? throw new FormatException("в DEV_ATTACH нет дескриптора устройства");
        var configBytes = attach.First(DescriptorKind.Configuration)
            ?? throw new FormatException("в DEV_ATTACH нет дескриптора конфигурации");
        _reportDescriptor = attach.First(DescriptorKind.HidReport)
            ?? throw new FormatException("в DEV_ATTACH нет HID report descriptor");

        _device = UsbDeviceDescriptor.Parse(deviceBytes);
        _configuration = UsbConfigurationDescriptor.FromFullDescriptor(configBytes);
        _configurationValue = 0;

        _featureReports = [];
        foreach (var snapshot in attach.All(DescriptorKind.FeatureReport))
        {
            // The first byte is the report id and the rest is the report as the controller
            // returned it — which on a numbered-report device already starts with that id.
            if (snapshot.Length < 2) continue;
            _featureReports[snapshot[0]] = snapshot;
        }

        _stringDescriptors = new Dictionary<byte, byte[]> { [0] = LanguageDescriptor };
        foreach (var block in attach.All(DescriptorKind.StringDescriptor))
        {
            if (block.Length < 3) continue;
            _stringDescriptors[block[0]] = block[1..];
        }

        ProductName = SeedString(_device.ProductString, "Wireless Controller");
        SeedString(_device.ManufacturerString, "Sony Interactive Entertainment");

        Info = new UsbIpDeviceInfo
        {
            BusId = BusIdFor(attach.Device),
            Path = $"/sys/devices/hexbridge/usb1/{BusIdFor(attach.Device)}",
            BusNum = 1,
            DevNum = (uint)(attach.Device + 1),
            // The isochronous OUT of a real DualSense needs 392 bytes per millisecond,
            // which only fits on High Speed; that is what the controller reports and what
            // makes bInterval 6 mean 250 Hz rather than 4 Hz.
            Speed = UsbIpProtocol.SpeedHigh,
            IdVendor = _device.IdVendor,
            IdProduct = _device.IdProduct,
            BcdDevice = _device.BcdDevice,
            DeviceClass = _device.DeviceClass,
            DeviceSubClass = _device.DeviceSubClass,
            DeviceProtocol = _device.DeviceProtocol,
            ConfigurationValue = _configuration.ConfigurationValue,
            NumConfigurations = 1,
            NumInterfaces = 1,
            Interfaces = [_configuration.Interface],
        };
    }

    /// <summary>The busid usbip.exe is told to attach, one per device number.</summary>
    public static string BusIdFor(byte device) => $"1-{device + 1}";

    /// <summary>Whether the feature reports a game cannot start without are all present.</summary>
    public IReadOnlyList<byte> MissingFeatureReports =>
        [.. new byte[] { 0x05, 0x09, 0x20 }.Where(id => !_featureReports.ContainsKey(id))];

    // MARK: - Input and output

    /// <summary>A HID input report off the wire. Called from the socket thread at up to 250 Hz.</summary>
    public void PushInputReport(byte[] report)
    {
        if (report.Length == 0) return;

        _lastInput = report;
        InputReports++;
        Input.Publish(report);

        // Battery is the only decoded field anything here needs, and decoding it 250 times
        // a second for a display that redraws ten would be waste.
        if (InputReports % 25 == 0) Battery = DescribeBattery(report);

        _pipe.Push(report);
    }

    public ValueTask<byte[]> ReadInterruptAsync(CancellationToken token) => _pipe.ReadAsync(token);

    public void WriteInterrupt(ReadOnlySpan<byte> data) => SendOutputReport(data);

    private void SendOutputReport(ReadOnlySpan<byte> report)
    {
        if (report.Length == 0) return;

        // Rumble, triggers and lighting change rarely, and the report is absolute state:
        // resending an identical one buys nothing and costs a packet.
        if (_lastOutput is not null && report.SequenceEqual(_lastOutput)) return;

        _lastOutput = report.ToArray();
        OutputReports++;
        Input.PublishOutput(_lastOutput);
        _onOutputReport(_lastOutput);
    }

    /// <summary>Low nibble of byte 53 is the level, high nibble the charging state.</summary>
    internal static string? DescribeBattery(ReadOnlySpan<byte> report)
    {
        if (report.Length < 54 || report[0] != 0x01) return null;

        var level = report[53] & 0x0F;
        var status = (report[53] >> 4) & 0x0F;
        var percent = Math.Min(level * 10 + 5, 100);

        return status switch
        {
            0x0 => $"{percent}%",
            0x1 => $"{percent}%, заряжается",
            0x2 => "заряжен",
            _ => $"статус 0x{status:x}",
        };
    }

    // MARK: - Control transfers

    public UsbControlResult Control(ReadOnlySpan<byte> setup, ReadOnlySpan<byte> data, int requestedLength)
    {
        if (setup.Length < UsbIpProtocol.SetupSize) return UsbControlResult.Stall();

        var requestType = setup[0];
        var request = setup[1];
        var value = BinaryPrimitives.ReadUInt16LittleEndian(setup[2..]);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(setup[6..]);

        return ((requestType >> 5) & 0x03) switch
        {
            0 => Standard(request, value, length),
            1 => HidClass(request, value, length, data),
            _ => UsbControlResult.Stall(),
        };
    }

    private UsbControlResult Standard(byte request, ushort value, ushort length) => request switch
    {
        0x00 => UsbControlResult.Ok([0, 0]),                     // GET_STATUS
        0x01 => UsbControlResult.Ok(),                           // CLEAR_FEATURE
        0x03 => UsbControlResult.Ok(),                           // SET_FEATURE
        0x06 => GetDescriptor((byte)(value >> 8), (byte)value, length),
        0x08 => UsbControlResult.Ok([_configurationValue]),      // GET_CONFIGURATION
        0x09 => SetConfiguration((byte)value),
        0x0A => UsbControlResult.Ok([0]),                        // GET_INTERFACE
        0x0B => UsbControlResult.Ok(),                           // SET_INTERFACE
        _ => UsbControlResult.Stall(),
    };

    private UsbControlResult SetConfiguration(byte value)
    {
        // Configuration 0 means "unconfigured", which is legal and not our business to refuse.
        if (value != 0 && value != _configuration.ConfigurationValue) return UsbControlResult.Stall();
        _configurationValue = value;
        return UsbControlResult.Ok();
    }

    private UsbControlResult GetDescriptor(byte type, byte index, int length) => type switch
    {
        UsbDescriptorType.Device => Truncate(_device.Bytes, length),
        UsbDescriptorType.Configuration => Truncate(_configuration.Bytes, length),
        UsbDescriptorType.String => _stringDescriptors.TryGetValue(index, out var s)
            ? Truncate(s, length)
            : UsbControlResult.Stall(),
        UsbDescriptorType.Hid => _configuration.HidDescriptor is { } hid
            ? Truncate(hid, length)
            : UsbControlResult.Stall(),
        UsbDescriptorType.HidReport => Truncate(_reportDescriptor, length),
        _ => UsbControlResult.Stall(),
    };

    private UsbControlResult HidClass(byte request, ushort value, ushort length, ReadOnlySpan<byte> data)
    {
        var reportType = (byte)(value >> 8);
        var reportId = (byte)value;

        switch (request)
        {
            case 0x01:  // GET_REPORT
                return reportType switch
                {
                    // Steam reads firmware, MAC and calibration before it will take the
                    // controller. Answering with zeros makes it visible in Steam Input and
                    // invisible to the game, so a missing snapshot stalls instead.
                    3 => _featureReports.TryGetValue(reportId, out var feature)
                        ? Truncate(feature, length)
                        : UsbControlResult.Stall(),
                    1 => _lastInput is { } input ? Truncate(input, length) : UsbControlResult.Stall(),
                    2 => _lastOutput is { } output ? Truncate(output, length) : UsbControlResult.Stall(),
                    _ => UsbControlResult.Stall(),
                };

            case 0x09:  // SET_REPORT
                if (reportType == 2 && data.Length > 0) SendOutputReport(data);
                return UsbControlResult.Ok();

            case 0x02: return UsbControlResult.Ok([_idle]);            // GET_IDLE
            case 0x0A: _idle = (byte)(value >> 8); return UsbControlResult.Ok();  // SET_IDLE
            case 0x03: return UsbControlResult.Ok([_protocol]);        // GET_PROTOCOL
            case 0x0B: _protocol = (byte)value; return UsbControlResult.Ok();     // SET_PROTOCOL
            default: return UsbControlResult.Stall();
        }
    }

    private static UsbControlResult Truncate(byte[] source, int length) =>
        UsbControlResult.Ok(length >= source.Length ? source : source[..Math.Max(0, length)]);

    // MARK: - Strings

    /// <summary>
    /// Registers a fallback for a string index the device descriptor points at. The Mac does
    /// not read string descriptors, and Windows shows the product name in Device Manager, so
    /// a plausible name beats an index that stalls.
    /// </summary>
    private string SeedString(byte index, string fallback)
    {
        if (index == 0) return fallback;

        if (_stringDescriptors.TryGetValue(index, out var existing)) return DecodeString(existing) ?? fallback;

        _stringDescriptors[index] = EncodeString(fallback);
        return fallback;
    }

    internal static byte[] EncodeString(string value)
    {
        var text = Encoding.Unicode.GetBytes(value);
        var length = Math.Min(text.Length, 252);
        var bytes = new byte[2 + length];
        bytes[0] = (byte)bytes.Length;
        bytes[1] = UsbDescriptorType.String;
        text.AsSpan(0, length).CopyTo(bytes.AsSpan(2));
        return bytes;
    }

    internal static string? DecodeString(byte[] descriptor) =>
        descriptor.Length < 4 || descriptor[1] != UsbDescriptorType.String
            ? null
            : Encoding.Unicode.GetString(descriptor, 2, Math.Min(descriptor[0], descriptor.Length) - 2);

    public void Dispose() => _pipe.Close();
}
