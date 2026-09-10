using System.Collections.Concurrent;
using System.Net;

namespace HexBridge.Devices;

/// <summary>
/// Turns the device channel into up to four virtual USB devices on this machine.
///
/// The Mac can only send HID: IOHIDFamily holds the interface exclusively and Apple does
/// not hand out the entitlement that would change that. So everything above HID — the USB
/// device, its descriptors, the control transfers, the endpoints — is assembled here, and
/// usbip-win2's vhci driver plugs the result into Windows.
///
/// Devices are told apart by the device number the protocol carries and by nothing else:
/// most pads report no serial number, and a busid derived from anything the hardware does
/// not guarantee would swap two controllers the first time somebody used a different port.
/// Number <c>n</c> is always busid <c>1-(n+1)</c>, and 0…3 is the whole of the range.
///
/// Optional by design. A missing driver, a busy port or a device that never appears all
/// leave the voice path untouched.
/// </summary>
public sealed class DevicesFeature : IFeature
{
    /// <summary>Device numbers 0…3. The protocol says four; so does this.</summary>
    public const int MaxDevices = 4;

    private static readonly PacketType[] Types =
        [PacketType.DeviceAttach, PacketType.DeviceDetach, PacketType.DeviceInput];

    private readonly object _gate = new();

    /// <summary>Everything forwarded right now, keyed by device number.</summary>
    private readonly ConcurrentDictionary<byte, Slot> _slots = new();

    private FeatureContext? _context;
    private UsbIpServer? _server;
    private UsbIpAttacher? _attacher;
    private CancellationTokenSource? _cancel;
    private IPEndPoint _listen = new(IPAddress.Loopback, 3240);
    private string? _fault;
    private long _rejected;

    /// <summary>One forwarded device and the accounting that belongs to it alone.</summary>
    private sealed class Slot(VirtualHidDevice device)
    {
        public VirtualHidDevice Device { get; } = device;

        /// <summary>Sampled on the host tick, which is single-threaded.</summary>
        public RateMeter Rate { get; } = new();

        /// <summary>Haptic bytes per second, so what the stream costs is visible while it runs.</summary>
        public RateMeter HapticRate { get; } = new();

        /// <summary>
        /// Input report counter from the sender, used to notice gaps. Per device: the
        /// sender's counter is shared across devices, so a global "last index" would
        /// report a loss every time two devices interleaved.
        /// </summary>
        public uint LastIndex;
        public bool HaveIndex;
        public long Lost;
    }

    public string Id => "devices";
    public string Title => "Устройства";
    public bool IsOptional => true;
    public IReadOnlyList<PacketType> HandledTypes => Types;

    /// <summary>
    /// Only ever on the machine that <b>accepts</b> devices. Forwarding one the other way is
    /// not a switch nobody has turned on yet — see <see cref="Unavailable"/>.
    /// </summary>
    public bool IsEnabled(ReceiverConfig config) =>
        config.Gamepad && config.Role == BridgeRole.Receiver;

    /// <summary>
    /// Why a machine that gives its microphone away cannot also give a gamepad away.
    ///
    /// <para>
    /// The contract needs the device's real USB descriptors — device, configuration, HID
    /// report — plus a snapshot of its feature reports, because the receiving end assembles a
    /// virtual USB device out of them and answers <c>GET_REPORT</c> from games without a
    /// network round trip. macOS hands all of that over for free: IOKit publishes the whole
    /// configuration descriptor and <c>kIOHIDReportDescriptorKey</c> without opening the
    /// device at all.
    /// </para>
    ///
    /// <para>
    /// Windows does not. Its HID class driver exposes <i>preparsed data</i>, not the report
    /// descriptor the device actually sent — the bytes are gone by the time anything in user
    /// space can look, and what can be rebuilt from <c>HidP_*</c> is a descriptor that
    /// describes the same fields, not the same descriptor. The device and configuration
    /// descriptors exist only behind an <c>IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION</c>
    /// on the parent hub, which means opening the hub with write access and knowing the port
    /// index — administrator rights, on a path that is empty for anything not on a plain USB
    /// hub. So the honest answer is that this direction does not work, and saying so is
    /// better than a switch that turns on and forwards nothing.
    /// </para>
    /// </summary>
    public string? Unavailable(ReceiverConfig config) => config.Role == BridgeRole.Sender
        ? "Отдавать геймпады умеет только Mac: Windows не выдаёт приложениям настоящие "
          + "USB-дескрипторы своих HID-устройств, а без них собрать устройство на другой "
          + "стороне не из чего. Принимать устройства этот компьютер по-прежнему умеет — "
          + "для этого переключите роль."
        : null;

    public void Start(FeatureContext context)
    {
        _listen = ReceiverConfig.ParseEndpoint(context.Config.UsbIpListen, 3240);
        _attacher = new UsbIpAttacher(context.Config.UsbIpPath, context.Log);
        _cancel = new CancellationTokenSource();
        _fault = null;
        Interlocked.Exchange(ref _rejected, 0);

        var server = new UsbIpServer(Exported, context.Log);
        try
        {
            server.Start(_listen);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"не удалось занять {_listen} для USB/IP: {ex.Message}. " +
                "Обычно это значит, что порт уже слушает другой экземпляр HexBridge или сам usbipd.", ex);
        }

        lock (_gate)
        {
            _context = context;
            _server = server;
        }

        context.Log(LogLevel.Info, $"usbip: сервер слушает {server.LocalEndPoint}");
        if (_attacher.IsInstalled)
        {
            context.Log(LogLevel.Info, $"usbip: клиент найден — {_attacher.ClientPath}");
        }
        else
        {
            context.Log(LogLevel.Warning, "usbip: usbip.exe не найден, автоподключение недоступно");
        }
    }

    /// <summary>What the USB/IP server exports right now, in device-number order.</summary>
    private IReadOnlyList<IUsbIpDevice> Exported() =>
        [.. _slots.OrderBy(pair => pair.Key).Select(pair => (IUsbIpDevice)pair.Value.Device)];

    public async Task StopAsync()
    {
        UsbIpServer? server;
        UsbIpAttacher? attacher;
        CancellationTokenSource? cancel;

        lock (_gate)
        {
            server = _server;
            attacher = _attacher;
            cancel = _cancel;
            _server = null;
            _attacher = null;
            _cancel = null;
            _context = null;
        }

        if (cancel is not null) await cancel.CancelAsync().ConfigureAwait(false);

        foreach (var number in _slots.Keys.ToArray())
        {
            if (_slots.TryRemove(number, out var slot)) slot.Device.Dispose();
        }

        if (attacher is not null)
        {
            // A fresh token: the session one is already cancelled, and unplugging is exactly
            // the work that must still happen on the way out.
            using var detach = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await attacher.DetachAllAsync(detach.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Windows will drop the ports when the sockets die anyway.
            }
        }

        if (server is not null) await server.StopAsync().ConfigureAwait(false);
        cancel?.Dispose();
    }

    public void OnPacket(in Header header, ReadOnlySpan<byte> payload)
    {
        switch (header.Type)
        {
            case PacketType.DeviceAttach:
                OnAttach(payload);
                break;

            case PacketType.DeviceDetach:
                if (DeviceChannel.TryReadDetach(payload, out var detached)) OnDetach(detached);
                break;

            case PacketType.DeviceInput:
                if (DeviceChannel.TryReadInput(payload, out var input)) OnInput(input);
                break;
        }
    }

    private void OnAttach(ReadOnlySpan<byte> payload)
    {
        if (!DeviceChannel.TryReadAttach(payload, out var attach))
        {
            Volatile.Write(ref _fault, "DEV_ATTACH не разобран");
            return;
        }

        var context = Volatile.Read(ref _context);
        if (context is null) return;

        // The protocol's device number is 0…3, and every number is a virtual USB port that
        // has to exist. A fifth device is refused here rather than silently overwriting a
        // slot, because overwriting would unplug a controller somebody was holding.
        if (attach.Device >= MaxDevices)
        {
            Interlocked.Increment(ref _rejected);
            context.Log(LogLevel.Warning,
                $"устройства: номер {attach.Device} вне диапазона 0…{MaxDevices - 1}, отклонено");
            return;
        }

        if (_slots.TryGetValue(attach.Device, out var existing) && SameHardware(existing.Device, attach))
        {
            // The sender repeats DEV_ATTACH once a second until it is acknowledged, and the
            // ack is what stops it. Rebuilding on a repeat would tear down a working usbip
            // session to solve a problem that does not exist.
            Acknowledge(context, attach.Device);
            return;
        }

        VirtualHidDevice device;
        try
        {
            device = new VirtualHidDevice(
                attach,
                report => SendOutput(attach.Device, report),
                haptics: context.Config.Haptics,
                onHaptic: payload => context.Send(PacketType.Haptic, payload));
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _fault, $"устройство {attach.Device} не собрано: {ex.Message}");
            context.Log(LogLevel.Error, $"устройства: {ex.Message}");
            return;
        }

        var missing = device.MissingFeatureReports;
        if (missing.Count > 0)
        {
            // 0x05 is the sensor calibration: zeros there are a divide by zero inside games,
            // so an incomplete snapshot is worth saying out loud even though we go on.
            context.Log(LogLevel.Warning,
                $"устройства: нет снимков feature-репортов {string.Join(", ", missing.Select(id => $"0x{id:x2}"))}");
        }

        // A different device on the same number means the pad in that slot was swapped; the
        // old virtual device has to let go of its parked URBs before the new one takes over.
        if (_slots.TryRemove(attach.Device, out var replaced)) replaced.Device.Dispose();

        _slots[attach.Device] = new Slot(device);
        Volatile.Write(ref _fault, null);

        context.Log(LogLevel.Info,
            $"устройства: {device.ProductName} ({device.Identity}) собран, busid {device.Info.BusId}");

        if (context.Config.Haptics)
        {
            // Asking for haptics and getting none is not a failure, but it is the difference
            // between "the actuators are silent because nothing is playing" and "there was
            // never an endpoint to play into", and only the log can tell the two apart.
            context.Log(device.Haptics is not null ? LogLevel.Info : LogLevel.Warning,
                device.Haptics is not null
                    ? $"хаптика: {device.ProductName} отдан композитом, изохронный OUT готов"
                    : $"хаптика: у {device.ProductName} нет пригодного изохронного OUT — "
                      + "триггеры и вибрация работают, HD-хаптики не будет");
        }

        Acknowledge(context, attach.Device);

        if (context.Config.UsbIpAutoAttach) _ = Task.Run(() => AutoAttach(device));
    }

    /// <summary>
    /// Whether a repeated DEV_ATTACH describes the device already in that slot.
    ///
    /// Number alone is not enough: unplug one pad and plug another, and the Mac frees the
    /// number and hands it straight back out. If the DEV_DETACH in between went missing,
    /// treating the repeat as "nothing changed" would leave Windows driving the wrong
    /// descriptors forever.
    /// </summary>
    private static bool SameHardware(VirtualHidDevice device, DeviceAttach attach)
    {
        var bytes = attach.First(DescriptorKind.Device);
        if (bytes is null) return false;
        try
        {
            var descriptor = UsbDeviceDescriptor.Parse(bytes);
            return descriptor.IdVendor == device.VendorId
                && descriptor.IdProduct == device.ProductId
                && descriptor.BcdDevice == device.BcdDevice;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Confirms the attach. Sent for every DEV_ATTACH, not only the first: the ack is a
    /// single UDP packet and losing one would otherwise leave the Mac repeating a 700-byte
    /// packet a second forever.
    /// </summary>
    private static void Acknowledge(FeatureContext context, byte device) =>
        context.Send(PacketType.DeviceAck, DeviceChannel.WriteAck(device));

    private void OnDetach(byte number)
    {
        if (!_slots.TryRemove(number, out var slot)) return;

        var busId = slot.Device.Info.BusId;
        var product = slot.Device.ProductName;
        slot.Device.Dispose();

        var context = Volatile.Read(ref _context);
        context?.Log(LogLevel.Info, $"устройства: «{product}» отключено от Mac");

        var attacher = Volatile.Read(ref _attacher);
        if (attacher is null) return;

        _ = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                // By busid, never `detach --all`: the other three devices are somebody's
                // hands on a wheel and a stick.
                await attacher.DetachAsync(busId, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context?.Log(LogLevel.Warning, $"usbip: не удалось отцепить {busId}: {ex.Message}");
            }
        });
    }

    private void OnInput(DeviceInput input)
    {
        if (!_slots.TryGetValue(input.Device, out var slot)) return;

        // Losing an input report costs nothing — the next one carries full state — but the
        // count is what tells the user their link is dropping packets.
        if (slot.HaveIndex && input.Index > slot.LastIndex + 1)
        {
            Interlocked.Add(ref slot.Lost, input.Index - slot.LastIndex - 1);
        }
        slot.LastIndex = input.Index;
        slot.HaveIndex = true;

        slot.Device.PushInputReport(input.Report);
    }

    private void SendOutput(byte number, byte[] report)
    {
        var context = Volatile.Read(ref _context);
        context?.Send(PacketType.DeviceOutput, DeviceChannel.WriteOutput(number, report));
    }

    private async Task AutoAttach(VirtualHidDevice device)
    {
        var attacher = Volatile.Read(ref _attacher);
        var server = Volatile.Read(ref _server);
        var context = Volatile.Read(ref _context);
        var cancel = Volatile.Read(ref _cancel);
        if (attacher is null || server is null || context is null || cancel is null) return;

        attacher.Rescan();
        if (!attacher.IsInstalled)
        {
            context.Log(LogLevel.Warning, "usbip: " + UsbIpAttacher.InstallHint);
            return;
        }

        var busId = device.Info.BusId;
        var host = _listen.Address.ToString();
        for (var attempt = 0; attempt < 3 && !cancel.IsCancellationRequested; attempt++)
        {
            if (server.IsImported(busId)) return;

            try
            {
                if (await attacher.AttachAsync(busId, host, cancel.Token).ConfigureAwait(false)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                context.Log(LogLevel.Warning, $"usbip: attach {busId}: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void OnSessionReset()
    {
        // The sender restarted, so whatever it told us about its devices is stale. The next
        // DEV_ATTACH rebuilds; until then there is nothing to hand Windows.
        foreach (var number in _slots.Keys.ToArray()) OnDetach(number);
    }

    public DeliveryStats Delivery
    {
        get
        {
            var total = default(DeliveryStats);
            foreach (var slot in _slots.Values)
            {
                total += new DeliveryStats(slot.Device.InputReports, Interlocked.Read(ref slot.Lost));
            }
            return total;
        }
    }

    public FeatureState CaptureState()
    {
        UsbIpServer? server;
        UsbIpAttacher? attacher;
        lock (_gate)
        {
            server = _server;
            attacher = _attacher;
        }

        var fault = Volatile.Read(ref _fault);
        var installed = attacher?.IsInstalled ?? false;
        var now = DateTime.UtcNow;

        var devices = _slots
            .OrderBy(pair => pair.Key)
            .Select(pair => Describe(pair.Key, pair.Value, server, attacher, now))
            .ToArray();

        var imported = devices.Count(d => d.Imported);

        var status = fault is not null ? FeatureStatus.Failed
            : server is null ? FeatureStatus.Stopped
            : devices.Length == 0 ? FeatureStatus.Waiting
            : imported == devices.Length ? FeatureStatus.Live
            : FeatureStatus.Warning;

        var headline = fault is not null ? "Ошибка"
            : server is null ? "Остановлено"
            : devices.Length == 0 ? "Ждём устройства"
            : imported == devices.Length
                ? devices.Length == 1 ? "Устройство проброшено" : $"Проброшено устройств: {devices.Length}"
            : $"Собрано {devices.Length}, подключено к Windows {imported}";

        var detail = fault
            ?? (server is null ? null
                : devices.Length == 0 ? "Выберите устройства на Mac и подключите их по USB"
                : imported == devices.Length ? string.Join(", ", devices.Select(d => $"{d.Product} → {d.BusId}"))
                : installed ? "usbip.exe не смог подключить часть устройств"
                : "Драйвер usbip-win2 не установлен");

        return new DevicesState
        {
            Status = status,
            Headline = headline,
            Detail = detail,
            Fault = fault,

            DriverInstalled = installed,
            DriverPath = attacher?.ClientPath,

            ServerRunning = server is not null,
            ServerListen = server?.LocalEndPoint?.ToString() ?? _listen.ToString(),
            ClientConnected = server?.HasClient ?? false,

            Devices = devices,
            HapticsEnabled = Volatile.Read(ref _context)?.Config.Haptics ?? false,
        };
    }

    private static ForwardedDeviceState Describe(
        byte number, Slot slot, UsbIpServer? server, UsbIpAttacher? attacher, DateTime now)
    {
        var device = slot.Device;
        var busId = device.Info.BusId;
        var haptics = device.Haptics;
        return new ForwardedDeviceState
        {
            Composite = device.IsComposite,
            HapticsAvailable = haptics is not null,
            HapticsStreaming = device.HapticsStreaming,
            HapticBlocksSent = haptics?.BlocksSent ?? 0,
            HapticBlocksSilent = haptics?.BlocksSkippedAsSilent ?? 0,
            HapticBlocksDropped = haptics?.BlocksDropped ?? 0,
            HapticKilobytesPerSecond = haptics is null
                ? 0
                : slot.HapticRate.Sample(haptics.BytesSent, now) / 1024,

            Number = number,
            BusId = busId,
            Product = device.ProductName,
            VendorId = device.VendorId,
            ProductId = device.ProductId,
            ProfileName = device.ProfileName,
            CanVisualise = device.CanVisualise,
            Battery = device.Battery,
            ReportsPerSecond = slot.Rate.Sample(device.InputReports, now),
            ReportsReceived = device.InputReports,
            ReportsLost = Interlocked.Read(ref slot.Lost),
            OutputsSent = device.OutputReports,
            ReportsDropped = device.DroppedReports,
            Imported = server?.IsImported(busId) ?? false,
            VhciPort = attacher?.Port(busId),
            Input = device.Input,
        };
    }

    /// <summary>DEV_ATTACH packets refused because their device number was out of range.</summary>
    public long RejectedAttaches => Interlocked.Read(ref _rejected);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
