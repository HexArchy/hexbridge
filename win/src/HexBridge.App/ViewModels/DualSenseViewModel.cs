using CommunityToolkit.Mvvm.ComponentModel;
using HexBridge.DualSense;

namespace HexBridge.App.ViewModels;

/// <summary>
/// The DualSense page. Three questions in order of what blocks the user: is the driver
/// there, is the controller forwarded, and what is the controller doing.
/// </summary>
public sealed partial class DualSenseViewModel : ObservableObject
{
    [ObservableProperty] private string _headline = "Проброс выключен";
    [ObservableProperty] private string _subline = "Включите проброс DualSense в настройках";

    [ObservableProperty] private bool _isGood;
    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private bool _isBad;

    // Driver.
    [ObservableProperty] private bool _driverInstalled;
    [ObservableProperty] private bool _driverMissing;
    [ObservableProperty] private string _driverText = "—";
    [ObservableProperty] private string _driverHint = UsbIpAttacher.InstallHint;

    /// <summary>The command the user can run by hand if the automatic attach did not work.</summary>
    [ObservableProperty] private string _attachCommand = "usbip.exe attach --receive-mode=low-latency -r 127.0.0.1 -b 1-1";

    // Forwarding.
    [ObservableProperty] private string _serverText = "—";
    [ObservableProperty] private string _clientText = "нет";
    [ObservableProperty] private string _portText = "—";
    [ObservableProperty] private string _busIdText = "—";

    // Controller.
    [ObservableProperty] private string _productText = "—";
    [ObservableProperty] private string _identityText = "—";
    [ObservableProperty] private string _batteryText = "—";
    [ObservableProperty] private string _rateText = "—";
    [ObservableProperty] private string _reportsText = "0";
    [ObservableProperty] private string _lostText = "0";
    [ObservableProperty] private string _outputsText = "0";
    [ObservableProperty] private string _droppedText = "0";

    public void Apply(DualSenseState? s)
    {
        if (s is null)
        {
            Headline = "Проброс выключен";
            Subline = "Включите проброс DualSense в настройках и перезапустите приём";
            IsGood = IsWaiting = IsBad = false;
            return;
        }

        Headline = s.Headline;
        Subline = s.Detail ?? "";
        IsGood = s.Status is FeatureStatus.Live;
        IsWaiting = s.Status is FeatureStatus.Waiting or FeatureStatus.Warning;
        IsBad = s.Status is FeatureStatus.Failed;

        DriverInstalled = s.DriverInstalled;
        DriverMissing = !s.DriverInstalled;
        DriverText = s.DriverPath ?? "не найден";
        DriverHint = s.DriverHint;

        ServerText = string.IsNullOrEmpty(s.ServerListen) ? "—" : s.ServerListen;
        ClientText = s.Imported ? "подключён, устройство импортировано"
            : s.ClientConnected ? "подключён"
            : "нет";
        PortText = s.VhciPort is { } port ? $"порт {port}" : "—";
        BusIdText = s.BusId ?? "—";
        if (s.BusId is not null)
        {
            AttachCommand = $"usbip.exe attach --receive-mode=low-latency -r {Host(s.ServerListen)} -b {s.BusId}";
        }

        ProductText = s.Product ?? "—";
        IdentityText = s.Identity;
        BatteryText = s.Battery ?? "—";
        RateText = s.Attached ? $"{s.ReportsPerSecond:F0} отч/с" : "—";
        ReportsText = s.ReportsReceived.ToString("N0");
        LostText = s.ReportsLost.ToString("N0");
        OutputsText = s.OutputsSent.ToString("N0");
        DroppedText = s.ReportsDropped.ToString("N0");
    }

    private static string Host(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon > 0 ? endpoint[..colon] : "127.0.0.1";
    }
}
