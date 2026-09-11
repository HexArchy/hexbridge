using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HexBridge.App.Controls;
using HexBridge.Devices;

using HexBridge.Localization;

namespace HexBridge.App.ViewModels;

/// <summary>
/// One forwarded device on the devices page.
///
/// Kept alive across ticks and updated in place rather than rebuilt: the visualisation
/// keeps a filter and a touch trail, and handing the view a new object ten times a second
/// would reset both.
/// </summary>
/// <summary>One thing the controller can or cannot do, and whether it is doing it.</summary>
public sealed record DeviceCapability(string Text, bool Live);

public sealed partial class ForwardedDeviceViewModel : ObservableObject
{
    public ForwardedDeviceViewModel(byte number) => Number = number;

    /// <summary>Device number 0…3. The identity of this row, and the sort key.</summary>
    public byte Number { get; }

    [ObservableProperty] private string _title = Strings.Common_Empty;
    [ObservableProperty] private string _kindText = Strings.Common_Empty;
    [ObservableProperty] private string _stateText = Strings.Common_Empty;

    [ObservableProperty] private string _rateText = Strings.Common_Empty;
    [ObservableProperty] private string _batteryText = Strings.Common_Empty;
    [ObservableProperty] private string _reportsText = Loc.Count(0);
    [ObservableProperty] private string _lostText = Loc.Count(0);
    [ObservableProperty] private string _outputsText = Loc.Count(0);
    [ObservableProperty] private string _droppedText = Loc.Count(0);

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
    [ObservableProperty] private string _visualCaption = Strings.Common_Empty;

    [ObservableProperty] private string _attachCommand = "";

    /// <summary>
    /// What this controller can actually do here, right now.
    ///
    /// <para>
    /// The page used to name the device and its update rate and stop there, which left the
    /// question people actually ask — "is this a proper DualSense with the triggers, or
    /// just a pad?" — with no answer on screen at all. Each line below is a fact the
    /// receiver already knew and was not saying.
    /// </para>
    /// </summary>
    public ObservableCollection<DeviceCapability> Capabilities { get; } = [];

    /// <summary>Set while the controller is forwarded but no game has addressed it yet.</summary>
    [ObservableProperty] private string _capabilityNote = "";

    private void DescribeCapabilities(ForwardedDeviceState device)
    {
        Capabilities.Clear();

        // Windows having imported it is the difference between "the Mac is reading this
        // pad" and "a game on this PC can see it".
        Capabilities.Add(new DeviceCapability(
            device.Imported ? Strings.Devices_Cap_Attached : Strings.Devices_Cap_NotAttached,
            device.Imported));

        // Trigger effects, rumble and lighting all ride the same output reports, so one
        // line covers them and the evidence is the same: something has been sent.
        Capabilities.Add(new DeviceCapability(Strings.Devices_Cap_Force, device.OutputsSent > 0));

        // Only worth a line when the audio function is actually presented; with haptics
        // switched off there is nothing here to be missing.
        if (device.Composite || device.HapticsAvailable)
        {
            Capabilities.Add(new DeviceCapability(
                Strings.Devices_Cap_Haptics, device.HapticsStreaming));
        }

        CapabilityNote = device.Imported && device.OutputsSent == 0
            ? Strings.Devices_Cap_NothingSentYet
            : "";
    }

    public void Apply(ForwardedDeviceState device, string serverListen)
    {
        Title = device.Product;
        KindText = device.ProfileName ?? Strings.Devices_Kind_Hid;
        StateText = device.Imported ? Strings.Devices_State_Imported : Strings.Devices_State_Waiting;

        RateText = Loc.Updates(device.ReportsPerSecond);
        BatteryText = device.Battery ?? Strings.Common_Empty;
        ReportsText = Loc.Count(device.ReportsReceived);
        LostText = Loc.Count(device.ReportsLost);
        OutputsText = Loc.Count(device.OutputsSent);
        DroppedText = Loc.Count(device.ReportsDropped);

        CanVisualise = device.CanVisualise;
        ShowsSummary = !device.CanVisualise;
        IsImported = device.Imported;
        Input = device.CanVisualise ? device.Input : null;
        Mood = !device.CanVisualise ? PadMood.Inactive
            : device.Imported ? PadMood.Forwarding
            : PadMood.Reading;
        VisualCaption = device.Imported
            ? Loc.F(Strings.Devices_Caption_Imported, device.Product, Loc.Updates(device.ReportsPerSecond))
            : Loc.F(Strings.Devices_Caption_Waiting, device.Product);

        DescribeCapabilities(device);

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
    [ObservableProperty] private string _headline = Strings.Devices_Headline_Off;
    [ObservableProperty] private string _subline = Strings.Devices_Sub_Off;

    [ObservableProperty] private bool _isGood;
    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private bool _isBad;

    // Driver. Only the missing case reaches the screen; the path to usbip.exe is
    // a setting and is shown on the settings page.
    [ObservableProperty] private bool _driverMissing;
    [ObservableProperty] private string _driverHint = UsbIpAttacher.InstallHint;

    /// <summary>
    /// True for a few seconds after the driver appears, so the screen that asked for it
    /// says so instead of merely going quiet. Without this the reward for installing a
    /// driver is a warning that vanishes, which reads like the app lost interest.
    /// </summary>
    [ObservableProperty] private bool _driverJustInstalled;

    /// <summary>When the driver first showed up, or default while it has never been seen.</summary>
    private DateTime _driverAppeared;

    /// <summary>How long the confirmation stays up. Long enough to read, short enough not to nag.</summary>
    private static readonly TimeSpan DriverPraiseFor = TimeSpan.FromSeconds(8);

    [RelayCommand]
    private void OpenDriverDownload()
    {
        try
        {
            Process.Start(new ProcessStartInfo(UsbIpAttacher.InstallerUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser, or the shell refused. The address is on screen next to the
            // button for exactly this case, so there is nothing to report and nothing
            // worth crashing a settings page over.
        }
    }

    /// <summary>Shown under the button, so a browser that refuses to open is not a dead end.</summary>
    public string DriverUrl => UsbIpAttacher.InstallerUrl;

    /// <summary>«Занято номеров: 2 из 4» — one line, so the count and its frame stay together.</summary>
    [ObservableProperty] private string _slotsLine = "";

    // Forwarding. The USB/IP server address and the client state left the page:
    // they describe the plumbing, and every device card says what it is doing.
    [ObservableProperty] private string _slotsText = Strings.Common_Empty;
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
            Headline = raw?.Headline is { Length: > 0 } headline ? headline : Strings.Devices_Headline_Off;
            Subline = raw?.Detail is { Length: > 0 } detail ? detail : Strings.Devices_Sub_Off;
            IsGood = IsWaiting = IsBad = false;
            DriverMissing = false;
            DriverJustInstalled = false;
            Devices.Clear();
            HasDevices = false;
            SetSlots(0, 4);
            return;
        }

        Headline = s.Headline;
        Subline = s.Detail ?? "";
        IsGood = s.Status is FeatureStatus.Live;
        IsWaiting = s.Status is FeatureStatus.Waiting or FeatureStatus.Warning;
        IsBad = s.Status is FeatureStatus.Failed;

        NoteDriver(s.DriverInstalled);
        DriverHint = s.DriverHint;

        SetSlots(s.Devices.Count, s.MaxDevices);

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

    /// <summary>
    /// Turns "is the driver there" into the three things the screen actually shows: still
    /// missing, just arrived, or long since sorted. Driven by the ordinary UI tick, so the
    /// confirmation ages out on its own without a timer of its own.
    /// </summary>
    private void NoteDriver(bool installed)
    {
        if (!installed)
        {
            DriverMissing = true;
            DriverJustInstalled = false;
            return;
        }

        // Only a transition earns the confirmation. A driver that was already in place when
        // the app started has nothing to celebrate.
        if (DriverMissing) _driverAppeared = DateTime.UtcNow;

        DriverMissing = false;
        DriverJustInstalled = _driverAppeared != default
            && DateTime.UtcNow - _driverAppeared < DriverPraiseFor;
    }

    private void SetSlots(int used, int of)
    {
        SlotsText = Loc.F(Strings.Devices_Slots_Value, used, of);
        SlotsLine = Loc.F(Strings.Devices_Slots, SlotsText);
    }

    internal static string Host(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon > 0 ? endpoint[..colon] : "127.0.0.1";
    }
}
