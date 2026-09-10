using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HexBridge.Microphone;

namespace HexBridge.App.ViewModels;

public sealed record OutputMode(string Value, string Title);

public sealed record DeviceOption(string? Selector, string Title, string? Paired, bool Recommended);

/// <summary>
/// An editable copy of <see cref="ReceiverConfig"/>. Nothing here touches the running
/// receiver until the user saves, so a half-typed port cannot knock the audio out.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private ReceiverConfig _saved = new();
    private bool _loading;

    public event Action? SaveRequested;

    /// <summary>
    /// «Связать заново» and «Проверить связь». The wizard is owned by the shell, so the
    /// settings page asks for it rather than holding one — §9.5 keeps it reachable from
    /// here forever, and this is the whole of that connection.
    /// </summary>
    public event Action? PairRequested;
    public event Action? CheckRequested;

    public Func<string, Task>? CopyToClipboard { get; set; }

    public ObservableCollection<DeviceOption> Devices { get; } = [];

    public IReadOnlyList<OutputMode> OutputModes { get; } =
    [
        new("wasapi", "Виртуальный кабель (WASAPI)"),
        new("null", "Никуда — только диагностика"),
        new("wav:hexbridge.wav", "Запись в файл hexbridge.wav"),
    ];

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(ThemePreference.System, "Системная"),
        new(ThemePreference.Light, "Светлая"),
        new(ThemePreference.Dark, "Тёмная"),
    ];

    [ObservableProperty] private string _listen = "0.0.0.0:47702";
    [ObservableProperty] private string _psk = "";
    [ObservableProperty] private DeviceOption? _device;
    [ObservableProperty] private string _relay = "";
    [ObservableProperty] private OutputMode? _output;
    [ObservableProperty] private int _jitterMs = 60;
    [ObservableProperty] private int _maxJitterMs = 240;
    [ObservableProperty] private double _gain = 1.0;
    [ObservableProperty] private int _latencyMs = 50;

    [ObservableProperty] private bool _gamepad = true;

    /// <summary>Off by default, and the view says out loud what turning it on means.</summary>
    [ObservableProperty] private bool _clipboard;

    [ObservableProperty] private bool _usbIpAutoAttach = true;
    [ObservableProperty] private string _usbIpListen = "127.0.0.1:3240";
    [ObservableProperty] private string _usbIpPath = "";

    [ObservableProperty] private bool _pskRevealed;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string? _validationError;
    [ObservableProperty] private string? _deviceNotice;

    [ObservableProperty] private ThemeOption? _theme;
    [ObservableProperty] private bool _startOnLaunch = true;
    [ObservableProperty] private bool _startMinimised;
    [ObservableProperty] private bool _autostart;
    [ObservableProperty] private string? _autostartError;

    /// <summary>Gain shown the way the user thinks about it, rather than as a multiplier.</summary>
    public string GainText => Gain <= 0.001 ? "тишина" : $"{20 * Math.Log10(Gain):+0.0;-0.0;0.0} дБ";

    public string JitterText => $"{JitterMs} мс";
    public string MaxJitterText => $"{MaxJitterMs} мс";
    public string LatencyText => $"{LatencyMs} мс";

    public void Load(ReceiverConfig config, AppSettings ui)
    {
        _loading = true;
        _saved = config.Clone();

        Listen = config.Listen;
        Psk = config.Psk;
        Relay = config.Relay ?? "";
        JitterMs = config.JitterMs;
        MaxJitterMs = config.MaxJitterMs;
        Gain = config.Gain;
        LatencyMs = config.LatencyMs;
        Gamepad = config.Gamepad;
        Clipboard = config.Clipboard;
        UsbIpAutoAttach = config.UsbIpAutoAttach;
        UsbIpListen = config.UsbIpListen;
        UsbIpPath = config.UsbIpPath ?? "";

        Output = OutputModes.FirstOrDefault(m => m.Value == config.Output)
            ?? OutputModes.FirstOrDefault(m => config.Output.StartsWith("wav:", StringComparison.Ordinal) && m.Value.StartsWith("wav:", StringComparison.Ordinal))
            ?? OutputModes[0];

        RefreshDevices();
        Device = Devices.FirstOrDefault(d => d.Selector == config.Device) ?? Devices[0];

        Theme = Themes.First(t => t.Value == ui.Theme);
        StartOnLaunch = ui.StartOnLaunch;
        StartMinimised = ui.StartMinimised;
        // Read back from the registry rather than ui.json: the Run key is the
        // real state, and it can be changed outside this app.
        Autostart = HexBridge.App.Autostart.IsEnabled;

        _loading = false;
        IsDirty = false;
    }

    public ReceiverConfig Build()
    {
        var config = _saved.Clone();
        config.Listen = Listen.Trim();
        config.Psk = Psk.Trim();
        config.Device = Device?.Selector;
        config.Relay = string.IsNullOrWhiteSpace(Relay) ? null : Relay.Trim();
        config.Output = Output?.Value ?? "wasapi";
        config.JitterMs = JitterMs;
        config.MaxJitterMs = MaxJitterMs;
        config.Gain = (float)Gain;
        config.LatencyMs = LatencyMs;
        config.Gamepad = Gamepad;
        config.Clipboard = Clipboard;
        config.UsbIpAutoAttach = UsbIpAutoAttach;
        config.UsbIpListen = string.IsNullOrWhiteSpace(UsbIpListen) ? "127.0.0.1:3240" : UsbIpListen.Trim();
        config.UsbIpPath = string.IsNullOrWhiteSpace(UsbIpPath) ? null : UsbIpPath.Trim();
        return config;
    }

    public void MarkSaved(ReceiverConfig config)
    {
        _saved = config.Clone();
        IsDirty = false;
    }

    [RelayCommand]
    public void RefreshDevices()
    {
        Devices.Clear();
        Devices.Add(new DeviceOption(null, "Определять автоматически", null, true));

        if (!OperatingSystem.IsWindows())
        {
            DeviceNotice = "Список устройств доступен только на Windows.";
            return;
        }

        try
        {
            AddWindowsDevices();
            DeviceNotice = Devices.Count > 1
                ? null
                : "Виртуальный кабель не найден. Установите Steam или VB-Audio Virtual Cable.";
        }
        catch (Exception ex)
        {
            DeviceNotice = $"Не удалось прочитать список устройств: {ex.Message}";
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void AddWindowsDevices()
    {
        foreach (var device in DeviceCatalog.RenderDevices())
        {
            var name = device.FriendlyName;
            var recommended = DeviceCatalog.PreferredPatterns.Any(p =>
                name.Contains(p, StringComparison.OrdinalIgnoreCase));
            Devices.Add(new DeviceOption(name, name, DeviceCatalog.PairedCaptureName(device), recommended));
        }
    }

    [RelayCommand]
    private void GeneratePsk()
    {
        Psk = ReceiverConfig.GenerateKey();
        PskRevealed = true;
    }

    [RelayCommand]
    private async Task CopyPskAsync()
    {
        if (CopyToClipboard is not null) await CopyToClipboard(Psk);
    }

    [RelayCommand]
    private void TogglePskReveal() => PskRevealed = !PskRevealed;

    [RelayCommand]
    private void Pair() => PairRequested?.Invoke();

    [RelayCommand]
    private void Check() => CheckRequested?.Invoke();

    /// <summary>The fingerprint of the key currently in the form, for comparing with the Mac.</summary>
    public string FingerprintText => PairingPayload.FingerprintOfPsk(Psk) ?? "ключ не задан";

    [RelayCommand]
    private void Save()
    {
        if (!Validate()) return;
        SaveRequested?.Invoke();
    }

    [RelayCommand]
    private void Revert() => Load(_saved, new AppSettings
    {
        Theme = Theme?.Value ?? ThemePreference.System,
        StartOnLaunch = StartOnLaunch,
        StartMinimised = StartMinimised,
    });

    private bool Validate()
    {
        var config = Build();
        if (!config.TryGetKey(out _, out var keyError))
        {
            ValidationError = $"Общий ключ: {keyError}. Нажмите «Сгенерировать» и вставьте тот же ключ на Mac.";
            return false;
        }

        try
        {
            ReceiverConfig.ParseEndpoint(config.Listen, 47702);
        }
        catch (Exception)
        {
            ValidationError = $"Не удаётся разобрать адрес «{config.Listen}». Пример: 0.0.0.0:47702";
            return false;
        }

        if (config.Relay is not null)
        {
            try
            {
                ReceiverConfig.ParseEndpoint(config.Relay, 47702);
            }
            catch (Exception)
            {
                ValidationError = $"Не удаётся разобрать адрес релея «{config.Relay}».";
                return false;
            }
        }

        if (config.Gamepad)
        {
            try
            {
                ReceiverConfig.ParseEndpoint(config.UsbIpListen, 3240);
            }
            catch (Exception)
            {
                ValidationError = $"Не удаётся разобрать адрес USB/IP «{config.UsbIpListen}». Пример: 127.0.0.1:3240";
                return false;
            }
        }

        ValidationError = null;
        return true;
    }

    // Any edit marks the form dirty; the derived labels follow their sources.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading) return;

        switch (e.PropertyName)
        {
            case nameof(Gain): OnPropertyChanged(nameof(GainText)); break;
            case nameof(JitterMs): OnPropertyChanged(nameof(JitterText)); break;
            case nameof(MaxJitterMs): OnPropertyChanged(nameof(MaxJitterText)); break;
            case nameof(LatencyMs): OnPropertyChanged(nameof(LatencyText)); break;
            case nameof(Psk): OnPropertyChanged(nameof(FingerprintText)); break;
            // Bookkeeping and the preferences that apply immediately are not "unsaved edits".
            case nameof(IsDirty) or nameof(ValidationError) or nameof(PskRevealed) or nameof(DeviceNotice)
                or nameof(Theme) or nameof(StartOnLaunch) or nameof(StartMinimised):
                return;
        }

        IsDirty = true;
    }
}

public sealed record ThemeOption(ThemePreference Value, string Title);
