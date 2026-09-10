using System.Net;

namespace HexBridge.DualSense;

/// <summary>
/// Turns the device channel into a virtual USB controller on this machine.
///
/// The Mac can only send HID: IOHIDFamily holds the interface exclusively and Apple does
/// not hand out the entitlement that would change that. So everything above HID — the USB
/// device, its descriptors, the control transfers, the endpoints — is assembled here, and
/// usbip-win2's vhci driver plugs the result into Windows.
///
/// Optional by design. A missing driver, a busy port or a controller that never appears all
/// leave the voice path untouched.
/// </summary>
public sealed class DualSenseFeature : IFeature
{
    private static readonly PacketType[] Types =
        [PacketType.DeviceAttach, PacketType.DeviceDetach, PacketType.DeviceInput];

    private readonly object _gate = new();
    private readonly RateMeter _rate = new();

    private FeatureContext? _context;
    private UsbIpServer? _server;
    private UsbIpAttacher? _attacher;
    private VirtualDualSense? _device;
    private CancellationTokenSource? _cancel;
    private IPEndPoint _listen = new(IPAddress.Loopback, 3240);
    private string? _fault;

    /// <summary>Input report counter from the sender, used to notice gaps.</summary>
    private uint _lastIndex;
    private bool _haveIndex;
    private long _lost;

    public string Id => "dualsense";
    public string Title => "DualSense";
    public bool IsOptional => true;
    public IReadOnlyList<PacketType> HandledTypes => Types;

    public bool IsEnabled(ReceiverConfig config) => config.Gamepad;

    public void Start(FeatureContext context)
    {
        _listen = ReceiverConfig.ParseEndpoint(context.Config.UsbIpListen, 3240);
        _attacher = new UsbIpAttacher(context.Config.UsbIpPath, context.Log);
        _cancel = new CancellationTokenSource();
        _fault = null;
        _rate.Reset();

        var server = new UsbIpServer(() => Volatile.Read(ref _device), context.Log);
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

    public async Task StopAsync()
    {
        UsbIpServer? server;
        VirtualDualSense? device;
        UsbIpAttacher? attacher;
        CancellationTokenSource? cancel;

        lock (_gate)
        {
            server = _server;
            device = _device;
            attacher = _attacher;
            cancel = _cancel;
            _server = null;
            _device = null;
            _attacher = null;
            _cancel = null;
            _context = null;
        }

        if (cancel is not null) await cancel.CancelAsync().ConfigureAwait(false);
        device?.Dispose();

        if (attacher is not null)
        {
            // A fresh token: the session one is already cancelled, and unplugging is exactly
            // the work that must still happen on the way out.
            using var detach = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await attacher.DetachAsync(detach.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Windows will drop the port when the socket dies anyway.
            }
        }

        if (server is not null) await server.StopAsync().ConfigureAwait(false);
        cancel?.Dispose();

        _haveIndex = false;
        _lastIndex = 0;
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

        var existing = Volatile.Read(ref _device);
        if (existing is not null && existing.DeviceNumber == attach.Device)
        {
            // The sender repeats DEV_ATTACH once a second until it is acknowledged, and the
            // ack is what stops it. Rebuilding the device on a repeat would tear down a
            // working usbip session to solve a problem that does not exist.
            Acknowledge(context, attach.Device);
            return;
        }

        VirtualDualSense device;
        try
        {
            device = new VirtualDualSense(attach, report => SendOutput(attach.Device, report));
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _fault, $"устройство не собрано: {ex.Message}");
            context.Log(LogLevel.Error, $"dualsense: {ex.Message}");
            return;
        }

        var missing = device.MissingFeatureReports;
        if (missing.Count > 0)
        {
            // 0x05 is the sensor calibration: zeros there are a divide by zero inside games,
            // so an incomplete snapshot is worth saying out loud even though we go on.
            context.Log(LogLevel.Warning,
                $"dualsense: нет снимков feature-репортов {string.Join(", ", missing.Select(id => $"0x{id:x2}"))}");
        }

        // A different device number means a different controller; the old virtual device
        // has to let go of its parked URBs before the new one takes over.
        Volatile.Read(ref _device)?.Dispose();

        Volatile.Write(ref _device, device);
        Volatile.Write(ref _fault, null);
        _haveIndex = false;
        Interlocked.Exchange(ref _lost, 0);
        _rate.Reset();

        context.Log(LogLevel.Info,
            $"dualsense: {device.ProductName} ({device.VendorId:X4}:{device.ProductId:X4}) собран, busid {device.Info.BusId}");

        Acknowledge(context, attach.Device);

        if (context.Config.UsbIpAutoAttach) _ = Task.Run(() => AutoAttach(device));
    }

    /// <summary>
    /// Confirms the attach. Sent for every DEV_ATTACH, not only the first: the ack is a
    /// single UDP packet and losing one would otherwise leave the Mac repeating a 569-byte
    /// packet a second forever.
    /// </summary>
    private static void Acknowledge(FeatureContext context, byte device) =>
        context.Send(PacketType.DeviceAck, DeviceChannel.WriteAck(device));

    private void OnDetach(byte number)
    {
        var device = Volatile.Read(ref _device);
        if (device is null || device.DeviceNumber != number) return;

        Volatile.Write(ref _device, null);
        device.Dispose();
        _haveIndex = false;

        var context = Volatile.Read(ref _context);
        context?.Log(LogLevel.Info, "dualsense: контроллер отключён от Mac");

        var attacher = Volatile.Read(ref _attacher);
        if (attacher is null) return;

        _ = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await attacher.DetachAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context?.Log(LogLevel.Warning, $"usbip: не удалось отцепить: {ex.Message}");
            }
        });
    }

    private void OnInput(DeviceInput input)
    {
        var device = Volatile.Read(ref _device);
        if (device is null || device.DeviceNumber != input.Device) return;

        // Losing an input report costs nothing — the next one carries full state — but the
        // count is what tells the user their link is dropping packets.
        if (_haveIndex && input.Index > _lastIndex + 1)
        {
            Interlocked.Add(ref _lost, input.Index - _lastIndex - 1);
        }
        _lastIndex = input.Index;
        _haveIndex = true;

        device.PushInputReport(input.Report);
    }

    private void SendOutput(byte number, byte[] report)
    {
        var context = Volatile.Read(ref _context);
        context?.Send(PacketType.DeviceOutput, DeviceChannel.WriteOutput(number, report));
    }

    private async Task AutoAttach(VirtualDualSense device)
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

        var host = _listen.Address.ToString();
        for (var attempt = 0; attempt < 3 && !cancel.IsCancellationRequested; attempt++)
        {
            if (server.IsImported) return;

            try
            {
                if (await attacher.AttachAsync(device.Info.BusId, host, cancel.Token).ConfigureAwait(false)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                context.Log(LogLevel.Warning, $"usbip: attach: {ex.Message}");
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
        // The sender restarted, so whatever it told us about a controller is stale. The next
        // DEV_ATTACH rebuilds; until then there is nothing to hand Windows.
        var device = Volatile.Read(ref _device);
        if (device is null) return;
        OnDetach(device.DeviceNumber);
    }

    public DeliveryStats Delivery
    {
        get
        {
            var device = Volatile.Read(ref _device);
            return device is null ? default : new DeliveryStats(device.InputReports, Interlocked.Read(ref _lost));
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

        var device = Volatile.Read(ref _device);
        var fault = Volatile.Read(ref _fault);
        var installed = attacher?.IsInstalled ?? false;

        var status = fault is not null ? FeatureStatus.Failed
            : server is null ? FeatureStatus.Stopped
            : device is null ? FeatureStatus.Waiting
            : server.IsImported ? FeatureStatus.Live
            : FeatureStatus.Warning;

        var headline = fault is not null ? "Ошибка"
            : server is null ? "Остановлено"
            : device is null ? "Ждём контроллер"
            : server.IsImported ? "Контроллер проброшен"
            : "Собран, но не подключён к Windows";

        var detail = fault
            ?? (server is null ? null
                : device is null ? "Подключите DualSense к Mac по USB"
                : server.IsImported ? $"{device.ProductName} → vhci"
                : installed ? "usbip.exe не смог подключить устройство"
                : "Драйвер usbip-win2 не установлен");

        return new DualSenseState
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
            Imported = server?.IsImported ?? false,
            VhciPort = attacher?.Port,

            Attached = device is not null,
            Product = device?.ProductName,
            VendorId = device?.VendorId ?? 0,
            ProductId = device?.ProductId ?? 0,
            BusId = device?.Info.BusId,
            Battery = device?.Battery,
            Input = device?.Input,
            ReportsPerSecond = device is null ? 0 : _rate.Sample(device.InputReports, DateTime.UtcNow),
            ReportsReceived = device?.InputReports ?? 0,
            ReportsLost = Interlocked.Read(ref _lost),
            OutputsSent = device?.OutputReports ?? 0,
            ReportsDropped = device?.DroppedReports ?? 0,
        };
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
