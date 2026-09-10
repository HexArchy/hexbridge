using System.Buffers.Binary;
using System.Text;

namespace HexBridge.Devices;

/// <summary>
/// The virtual USB device Windows enumerates, assembled entirely from what one DEV_ATTACH
/// carried: the real device's descriptors and a snapshot of its feature reports.
///
/// Nothing here knows what kind of device it is building. A pad, a wheel, a HOTAS and a
/// keyboard all arrive as the same three descriptor blocks and leave as the same virtual
/// USB device; the only model-specific thing in the file is a <see cref="DeviceProfile"/>
/// lookup that supplies a display name and a battery decoder, and both are optional.
///
/// Nothing here is invented except the string descriptors, which the device channel does
/// not carry today. Everything a game or Steam asks for is either a descriptor byte the Mac
/// read off the hardware or a feature report it captured before the bridge came up.
/// </summary>
public sealed class VirtualHidDevice : IUsbIpDevice, IDisposable
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
    private readonly DeviceProfile? _profile;

    /// <summary>Non-null only on a composite: there is no audio class without an audio function.</summary>
    private readonly UsbAudioControls? _audio;

    /// <summary>Alternate setting per interface. Everything starts at zero, as USB says.</summary>
    private readonly Dictionary<byte, byte> _alternates = [];

    private byte[]? _lastInput;
    private byte[]? _lastOutput;
    private byte _configurationValue;
    private byte _idle;
    private byte _protocol = 1;
    private bool _hapticsStreaming;

    public UsbIpDeviceInfo Info { get; }
    public int InterruptInEndpoint => _configuration.InterruptInEndpoint;
    public int InterruptOutEndpoint => _configuration.InterruptOutEndpoint;

    /// <summary>Device number on the wire, 0..3.</summary>
    public byte DeviceNumber { get; }

    public string ProductName { get; }
    public ushort VendorId => _device.IdVendor;
    public ushort ProductId => _device.IdProduct;
    public ushort BcdDevice => _device.BcdDevice;

    /// <summary>VID:PID, the way the picker on the Mac writes it.</summary>
    public string Identity => $"{VendorId:X4}:{ProductId:X4}";

    /// <summary>Whether anything can honestly be drawn beyond a name and a rate.</summary>
    public bool CanVisualise => _profile?.CanVisualise ?? false;

    /// <summary>The model, when it was recognised. Null for the ordinary case.</summary>
    public string? ProfileName => _profile?.Name;

    /// <summary>Input reports handed to the pipe; not the same as reports Windows collected.</summary>
    public long InputReports { get; private set; }

    /// <summary>Output reports sent back to the Mac, after coalescing.</summary>
    public long OutputReports { get; private set; }

    public long DroppedReports => _pipe.Dropped;

    /// <summary>Battery as the last input report described it, or null before the first one.</summary>
    public string? Battery { get; private set; }

    /// <summary>
    /// The live feed the device screen draws from. Publishing into it is one volatile
    /// write, which is what keeps a 60 Hz visualisation off the 250 Hz report path.
    /// </summary>
    public HidInputSource Input { get; } = new();

    /// <summary>
    /// The PCM the host writes to the actuators, on its way to the Mac. Null unless haptics
    /// were asked for and the device turned out to have an audio-streaming OUT to carry them.
    /// </summary>
    public HapticStream? Haptics { get; }

    /// <summary>The audio function came through, so an isochronous endpoint exists to address.</summary>
    public bool IsComposite => _configuration.IsComposite;

    /// <summary>Windows has selected the streaming alternate setting and PCM is arriving.</summary>
    public bool HapticsStreaming => Volatile.Read(ref _hapticsStreaming);

    public VirtualHidDevice(DeviceAttach attach, Action<byte[]> onOutputReport)
        : this(attach, onOutputReport, haptics: false, onHaptic: null)
    {
    }

    /// <param name="haptics">
    /// Serve the whole composite device — audio function included — so HD haptics have an
    /// endpoint to arrive on. Off by default, and off is not a degraded mode: it is the
    /// device that has been shipping, with none of the audio path that usbip-win2's issue
    /// #181 lives on.
    /// </param>
    /// <param name="onHaptic">Where a filled HAPTIC block goes. Ignored when haptics are off.</param>
    public VirtualHidDevice(
        DeviceAttach attach, Action<byte[]> onOutputReport, bool haptics, Action<byte[]>? onHaptic)
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
        _configuration = UsbConfigurationDescriptor.FromFullDescriptor(configBytes, withAudio: haptics);
        _configurationValue = 0;
        _profile = DeviceProfile.Of(_device.IdVendor, _device.IdProduct);

        if (_configuration.IsComposite)
        {
            _audio = new UsbAudioControls(_configuration.OutputStream?.SampleRate ?? 48000);
            if (_configuration.OutputStream is { } stream && onHaptic is not null)
            {
                var built = new HapticStream(attach.Device, stream, onHaptic);
                // A format we cannot take apart is worse than no haptics: the endpoint would
                // still have to swallow every packet, and what came out the other end would
                // be noise. Better to serve the audio function and forward nothing.
                Haptics = built.IsSupported ? built : null;
            }
        }

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

        // The Mac does not read string descriptors, so Device Manager would otherwise show
        // an index that stalls. A recognised model gets its real name; anything else gets
        // its identity, which is at least true.
        ProductName = SeedString(_device.ProductString, _profile?.Name ?? $"USB {Identity}");
        SeedString(_device.ManufacturerString, _profile?.Manufacturer ?? "HexBridge");

        Info = new UsbIpDeviceInfo
        {
            BusId = BusIdFor(attach.Device),
            Path = $"/sys/devices/hexbridge/usb1/{BusIdFor(attach.Device)}",
            BusNum = 1,
            DevNum = (uint)(attach.Device + 1),
            // High Speed for everything. A DualSense reports it because its isochronous
            // OUT needs 392 bytes per millisecond, and it is also what makes bInterval 6
            // mean 250 Hz rather than 4 Hz; a full-speed device loses nothing by being
            // presented on a faster bus, while the reverse would throttle it.
            Speed = UsbIpProtocol.SpeedHigh,
            IdVendor = _device.IdVendor,
            IdProduct = _device.IdProduct,
            BcdDevice = _device.BcdDevice,
            DeviceClass = _device.DeviceClass,
            DeviceSubClass = _device.DeviceSubClass,
            DeviceProtocol = _device.DeviceProtocol,
            ConfigurationValue = _configuration.ConfigurationValue,
            NumConfigurations = 1,
            // One after a rebuild, four for a DualSense served whole. usbip lists one entry
            // per interface and the count has to agree with the list, or `usbip list` reads
            // the next device's bytes as this one's interfaces.
            NumInterfaces = (byte)_configuration.Interfaces.Count,
            Interfaces = _configuration.Interfaces,
        };
    }

    /// <summary>The busid usbip.exe is told to attach, one per device number.</summary>
    public static string BusIdFor(byte device) => $"1-{device + 1}";

    /// <summary>
    /// Feature reports this model needs and this attach did not carry. Empty for a device
    /// with no profile: we have no idea what an unknown wheel considers essential, and
    /// inventing a list would only produce warnings nobody can act on.
    /// </summary>
    public IReadOnlyList<byte> MissingFeatureReports =>
        [.. (_profile?.RequiredFeatureReports ?? []).Where(id => !_featureReports.ContainsKey(id))];

    // MARK: - Input and output

    /// <summary>A HID input report off the wire. Called from the socket thread at up to 250 Hz.</summary>
    public void PushInputReport(byte[] report)
    {
        if (report.Length == 0) return;

        _lastInput = report;
        InputReports++;
        Input.Publish(report);

        // Battery is the only decoded field anything here needs, and decoding it 250 times
        // a second for a display that redraws ten would be waste. A model with no profile
        // has no battery byte we could point at, so it simply has none.
        if (_profile?.Battery is { } battery && InputReports % 25 == 0) Battery = battery(report);

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

    // MARK: - Isochronous

    /// <summary>
    /// The two isochronous endpoints a composite DualSense declares.
    ///
    /// The OUT one is the point of the whole feature: the PCM Windows writes there is the HD
    /// haptics, and it goes on the wire. The IN one is the controller's own microphone, and
    /// it answers with silence — the Mac's microphone is a separate feature travelling the
    /// other way, so there is nothing to put in it. Silence rather than a stall, because a
    /// capture pin that fails to open is a pin usbaudio.sys then closes, and closing an audio
    /// pin is the exact path usbip-win2's issue #181 crashes on. A stream that runs and
    /// carries nothing never goes near it.
    /// </summary>
    public UsbIsoResult? Isochronous(UsbIpSubmit submit)
    {
        var endpoint = (int)submit.Header.Endpoint;

        if (!submit.IsIn && _configuration.OutputStream is { } output && endpoint == (output.Endpoint & 0x0F))
        {
            return AcceptHaptics(submit);
        }
        if (submit.IsIn && _configuration.InputStream is { } input && endpoint == (input.Endpoint & 0x0F))
        {
            return Silence(submit, input);
        }
        return null;
    }

    private UsbIsoResult AcceptHaptics(UsbIpSubmit submit)
    {
        var buffer = submit.TransferBuffer.AsSpan();
        var results = new UsbIsoPacketResult[submit.IsoPackets.Count];

        for (var i = 0; i < submit.IsoPackets.Count; i++)
        {
            var packet = submit.IsoPackets[i];
            // The offsets are the host's, so they are clamped rather than trusted: a URB that
            // pointed past its own buffer would otherwise be an exception on the URB loop and
            // a controller that vanished mid-game.
            var start = Math.Clamp(packet.Offset, 0, buffer.Length);
            var end = Math.Clamp(start + Math.Max(0, packet.Length), start, buffer.Length);

            Haptics?.Write(buffer[start..end]);
            // The endpoint took every byte it was handed. An isochronous OUT has no other
            // answer available to it — there is no flow control on this pipe, which is why
            // the far end is where a block is allowed to be dropped.
            results[i] = UsbIsoPacketResult.Ok(end - start);
        }

        return UsbIsoResult.Accepted(results);
    }

    private static UsbIsoResult Silence(UsbIpSubmit submit, UsbAudioStreamFormat format)
    {
        // One millisecond per packet: the endpoint's bInterval says so at high speed, which
        // is the speed everything here is presented at.
        var perPacket = Math.Max(1, format.SampleRate / 1000) * format.BytesPerFrame;

        var results = new UsbIsoPacketResult[submit.IsoPackets.Count];
        var total = 0;
        for (var i = 0; i < submit.IsoPackets.Count; i++)
        {
            var length = Math.Min(perPacket, Math.Max(0, submit.IsoPackets[i].Length));
            results[i] = UsbIsoPacketResult.Ok(length);
            total += length;
        }

        return new UsbIsoResult(UsbIpProtocol.StatusSuccess, new byte[total], results);
    }

    // MARK: - Control transfers

    public UsbControlResult Control(ReadOnlySpan<byte> setup, ReadOnlySpan<byte> data, int requestedLength)
    {
        if (setup.Length < UsbIpProtocol.SetupSize) return UsbControlResult.Stall();

        var requestType = setup[0];
        var request = setup[1];
        var value = BinaryPrimitives.ReadUInt16LittleEndian(setup[2..]);
        var index = BinaryPrimitives.ReadUInt16LittleEndian(setup[4..]);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(setup[6..]);

        return ((requestType >> 5) & 0x03) switch
        {
            0 => Standard(request, value, index, length),
            1 => ClassRequest((byte)(requestType & 0x1F), request, value, index, length, data),
            _ => UsbControlResult.Stall(),
        };
    }

    /// <summary>
    /// A class request, routed by who it is addressed to.
    ///
    /// On a rebuilt HID-only device there is one interface and everything class-specific is
    /// HID, which is the behaviour this has always had. On a composite the recipient decides:
    /// the HID interface still speaks HID, the audio ones speak the audio class, and an
    /// endpoint recipient is the sampling-frequency control on the isochronous pipe.
    /// </summary>
    private UsbControlResult ClassRequest(
        byte recipient, byte request, ushort value, ushort index, ushort length, ReadOnlySpan<byte> data)
    {
        const byte toInterface = 1;
        const byte toEndpoint = 2;

        if (recipient == toEndpoint)
        {
            return _audio?.Endpoint(request, value, index, length, data) ?? UsbControlResult.Stall();
        }
        if (recipient != toInterface) return UsbControlResult.Stall();

        return IsHidInterface(index)
            ? HidClass(request, value, length, data)
            : _audio?.Entity(request, value, index, length, data) ?? UsbControlResult.Stall();
    }

    /// <summary>
    /// Whether a wIndex names the HID interface. Always true on a rebuilt device: there is
    /// only one interface there, and a host that names another is naming one we invented.
    /// </summary>
    private bool IsHidInterface(ushort index) =>
        !_configuration.IsComposite || (byte)index == _configuration.HidInterfaceNumber;

    private UsbControlResult Standard(byte request, ushort value, ushort index, ushort length) => request switch
    {
        0x00 => UsbControlResult.Ok([0, 0]),                     // GET_STATUS
        0x01 => UsbControlResult.Ok(),                           // CLEAR_FEATURE
        0x03 => UsbControlResult.Ok(),                           // SET_FEATURE
        0x06 => GetDescriptor((byte)(value >> 8), (byte)value, index, length),
        0x08 => UsbControlResult.Ok([_configurationValue]),      // GET_CONFIGURATION
        0x09 => SetConfiguration((byte)value),
        0x0A => UsbControlResult.Ok([_alternates.GetValueOrDefault((byte)index)]),
        0x0B => SetInterface((byte)index, (byte)value),
        _ => UsbControlResult.Stall(),
    };

    private UsbControlResult SetConfiguration(byte value)
    {
        // Configuration 0 means "unconfigured", which is legal and not our business to refuse.
        if (value != 0 && value != _configuration.ConfigurationValue) return UsbControlResult.Stall();
        _configurationValue = value;
        return UsbControlResult.Ok();
    }

    /// <summary>
    /// SET_INTERFACE. On a HID-only device this is bookkeeping and nothing more, but on a
    /// composite it is the switch that starts and stops the haptics: an audio-streaming
    /// interface has a zero-bandwidth alternate 0 and a real one above it, and moving between
    /// them is exactly how usbaudio.sys says "I am opening the pin" and "I have closed it".
    /// </summary>
    private UsbControlResult SetInterface(byte number, byte alternate)
    {
        _alternates[number] = alternate;

        if (_configuration.OutputStream is { } stream && number == stream.Interface)
        {
            var streaming = alternate == stream.AlternateSetting;
            Volatile.Write(ref _hapticsStreaming, streaming);
            // Whatever was half-collected belongs to a stream that has ended. Padding it out
            // would deliver a fragment of an old moment on top of the next one.
            if (!streaming) Haptics?.Reset();
        }

        return UsbControlResult.Ok();
    }

    private UsbControlResult GetDescriptor(byte type, byte index, ushort target, int length) => type switch
    {
        UsbDescriptorType.Device => Truncate(_device.Bytes, length),
        UsbDescriptorType.Configuration => Truncate(_configuration.Bytes, length),
        UsbDescriptorType.String => _stringDescriptors.TryGetValue(index, out var s)
            ? Truncate(s, length)
            : UsbControlResult.Stall(),
        // wIndex is the interface for both of these, and on a composite only one interface
        // has a report descriptor to give.
        UsbDescriptorType.Hid => IsHidInterface(target) && _configuration.HidDescriptor is { } hid
            ? Truncate(hid, length)
            : UsbControlResult.Stall(),
        UsbDescriptorType.HidReport => IsHidInterface(target)
            ? Truncate(_reportDescriptor, length)
            : UsbControlResult.Stall(),
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
    /// Registers a fallback for a string index the device descriptor points at.
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
