using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HexBridge.App.Controls;
using HexBridge.Devices;

namespace HexBridge.App.ViewModels;

/// <summary>
/// One forwarded device on the devices page.
///
/// Kept alive across ticks and updated in place rather than rebuilt: the visualisation
/// keeps a filter and a touch trail, and handing the view a new object ten times a second
/// would reset both.
/// </summary>
public sealed partial class ForwardedDeviceViewModel : ObservableObject
{
    public ForwardedDeviceViewModel(byte number) => Number = number;

    /// <summary>Device number 0…3. The identity of this row, and the sort key.</summary>
    public byte Number { get; }

    [ObservableProperty] private string _title = "—";
    [ObservableProperty] private string _kindText = "—";
    [ObservableProperty] private string _stateText = "—";

    [ObservableProperty] private string _rateText = "—";
    [ObservableProperty] private string _batteryText = "—";
    [ObservableProperty] private string _reportsText = "0";
    [ObservableProperty] private string _lostText = "0";
    [ObservableProperty] private string _outputsText = "0";
    [ObservableProperty] private string _droppedText = "0";

    /// <summary>
    /// True only for a model whose reports the visualisation can decode. Everything else
    /// gets a name, ids and a rate — an honest row rather than a drawing of a wheel we
    /// would have to invent.
    /// </summary>
    [ObservableProperty] private bool _canVisualise;
    [ObservableProperty] private bool _showsSummary = true;

    /// <summary>Windows has the device. The manual attach command is offered only when it does not.</summary>
    [ObservableProperty] private bool _isImported;

    /// <summary>
    /// Live input for the visualisation (§8). A reference to the publisher rather than a
    /// value: the screen polls it at its own frame rate instead of being pinned to the ten
    /// snapshots a second the rest of this view model is built from.
    /// </summary>
    [ObservableProperty] private HidInputSource? _input;

    /// <summary>
    /// §7.3 in one property. «Собран» and «проброшен» are different states and the outline
    /// says which: dim while Windows cannot see the device, accented once vhci has it.
    /// </summary>
    [ObservableProperty] private PadMood _mood = PadMood.Inactive;

    /// <summary>The line under the outline, which is what a decorative canvas owes somebody
    /// using a screen reader (§8.4).</summary>
    [ObservableProperty] private string _visualCaption = "—";

    [ObservableProperty] private string _attachCommand = "";

    public void Apply(ForwardedDeviceState device, string serverListen)
    {
        Title = device.Product;
        KindText = device.ProfileName ?? "HID-устройство";
        StateText = device.Imported ? "Windows видит устройство" : "готово, Windows его ещё не забрала";

        RateText = $"{device.ReportsPerSecond:F0} отч/с";
        BatteryText = device.Battery ?? "—";
        ReportsText = device.ReportsReceived.ToString("N0");
        LostText = device.ReportsLost.ToString("N0");
        OutputsText = device.OutputsSent.ToString("N0");
        DroppedText = device.ReportsDropped.ToString("N0");

        CanVisualise = device.CanVisualise;
        ShowsSummary = !device.CanVisualise;
        IsImported = device.Imported;
        Input = device.CanVisualise ? device.Input : null;
        Mood = !device.CanVisualise ? PadMood.Inactive
            : device.Imported ? PadMood.Forwarding
            : PadMood.Reading;
        VisualCaption = device.Imported
            ? $"{device.Product} — Windows видит его, {device.ReportsPerSecond:F0} отч/с"
            : $"{device.Product} — Windows его пока не видит";

        AttachCommand =
            $"usbip.exe attach --receive-mode=low-latency -r {DevicesViewModel.Host(serverListen)} -b {device.BusId}";
    }
}

/// <summary>
/// The devices page. Three questions in order of what blocks the user: is the driver there,
/// is anything forwarded, and what is each forwarded device doing.
/// </summary>
public sealed partial class DevicesViewModel : ObservableObject
{
    [ObservableProperty] private string _headline = "Проброс выключен";
    [ObservableProperty] private string _subline = "Включите приём устройств в настройках";

    [ObservableProperty] private bool _isGood;
    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private bool _isBad;

    // Driver. Only the missing case reaches the screen; the path to usbip.exe is
    // a setting and is shown on the settings page.
    [ObservableProperty] private bool _driverMissing;
    [ObservableProperty] private string _driverHint = UsbIpAttacher.InstallHint;

    // Forwarding. The USB/IP server address and the client state left the page:
    // they describe the plumbing, and every device card says what it is doing.
    [ObservableProperty] private string _slotsText = "—";
    [ObservableProperty] private bool _hasDevices;

    /// <summary>Up to four, in device-number order.</summary>
    public ObservableCollection<ForwardedDeviceViewModel> Devices { get; } = [];

    /// <summary>
    /// <paramref name="raw"/> is the same feature's plain state, which is all there is when
    /// the feature never started. It carries the reason — and «выключено в настройках» is
    /// not always the reason: on a machine giving its microphone away there is no switch to
    /// find, and sending somebody to look for one is the worst thing this page could do.
    /// </summary>
    public void Apply(DevicesState? s, FeatureState? raw = null)
    {
        if (s is null)
        {
            Headline = raw?.Headline is { Length: > 0 } headline ? headline : "Проброс выключен";
            Subline = raw?.Detail is { Length: > 0 } detail ? detail : "Включите приём устройств в настройках";
            IsGood = IsWaiting = IsBad = false;
            DriverMissing = false;
            Devices.Clear();
            HasDevices = false;
            SlotsText = "0 из 4";
            return;
        }

        Headline = s.Headline;
        Subline = s.Detail ?? "";
        IsGood = s.Status is FeatureStatus.Live;
        IsWaiting = s.Status is FeatureStatus.Waiting or FeatureStatus.Warning;
        IsBad = s.Status is FeatureStatus.Failed;

        DriverMissing = !s.DriverInstalled;
        DriverHint = s.DriverHint;

        SlotsText = $"{s.Devices.Count} из {s.MaxDevices}";

        Reconcile(s);
        HasDevices = Devices.Count > 0;
    }

    /// <summary>
    /// Matches the live rows to the snapshot by device number, in place. A device that goes
    /// away takes its row with it and the other three are left alone — which is the whole
    /// point of unplugging one of four controllers.
    /// </summary>
    private void Reconcile(DevicesState s)
    {
        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (!s.Devices.Any(d => d.Number == Devices[i].Number)) Devices.RemoveAt(i);
        }

        foreach (var device in s.Devices.OrderBy(d => d.Number))
        {
            var row = Devices.FirstOrDefault(r => r.Number == device.Number);
            if (row is null)
            {
                row = new ForwardedDeviceViewModel(device.Number);
                var at = 0;
                while (at < Devices.Count && Devices[at].Number < device.Number) at++;
                Devices.Insert(at, row);
            }
            row.Apply(device, s.ServerListen);
        }
    }

    internal static string Host(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon > 0 ? endpoint[..colon] : "127.0.0.1";
    }
}
