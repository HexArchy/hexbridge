using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HexBridge.Microphone;

namespace HexBridge.App.ViewModels;

public sealed record OutputMode(string Value, string Title);

/// <summary>Where the giving role reads audio from. Same shape as <see cref="OutputMode"/>.</summary>
public sealed record InputMode(string Value, string Title);

public sealed record DeviceOption(string? Selector, string Title, string? Paired, bool Recommended);

/// <summary>One Opus bitrate, with what it costs said in words rather than in bits.</summary>
public sealed record BitrateOption(int Value, string Title);

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

    /// <summary>
    /// «Сменить роль». The question is asked in the same modal a fresh install sees, so
    /// there is one screen that explains the choice rather than two that half do.
    /// </summary>
    public event Action? RoleChangeRequested;

    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>
    /// The update card, owned by the shell and shown here. Handed over rather than created
    /// because a check is a thing the whole app does once a day, not a thing a settings
    /// page does whenever it is opened.
    /// </summary>
    public UpdateViewModel? Updates { get; set; }

    public ObservableCollection<DeviceOption> Devices { get; } = [];

    /// <summary>Capture endpoints, for the giving role. Empty in the other one.</summary>
    public ObservableCollection<DeviceOption> InputDevices { get; } = [];

    public IReadOnlyList<InputMode> InputModes { get; } =
    [
        new("wasapi", "Микрофон этого компьютера"),
        new("tone", "Тон 440 Гц — проверка тракта"),
        new("null", "Тишина — только диагностика"),
    ];

    /// <summary>
    /// Opus at 32 kbit/s is what the Mac has always sent and what the receiver is tuned
    /// for; the two either side of it are for a link that is worse or better than usual.
    /// </summary>
    public IReadOnlyList<BitrateOption> Bitrates { get; } =
    [
        new(16000, "16 кбит/с — узкий канал"),
        new(24000, "24 кбит/с"),
        new(32000, "32 кбит/с — как на Mac"),
        new(48000, "48 кбит/с"),
        new(64000, "64 кбит/с — запас по качеству"),
    ];

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

    /// <summary>Read-only here: it is changed through the modal that explains it.</summary>
    [ObservableProperty] private BridgeRole _role = BridgeRole.Receiver;

    [ObservableProperty] private string _target = "";
    [ObservableProperty] private InputMode? _input;
    [ObservableProperty] private DeviceOption? _inputDevice;
    [ObservableProperty] private double _inputGain = 1.0;
    [ObservableProperty] private BitrateOption? _bitrate;
    [ObservableProperty] private bool _startMuted;
    [ObservableProperty] private string? _inputNotice;

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

    /// <summary>
    /// «Проверять обновления». Applies immediately like the other preferences below the
    /// line — an off switch that needs «Сохранить» to take effect is not an off switch.
    /// </summary>
    [ObservableProperty] private bool _autoUpdate = true;

    public bool IsGiving => Role == BridgeRole.Sender;
    public bool IsTaking => Role == BridgeRole.Receiver;

    public string RoleTitle => RoleWording.Title(Role);
    public string RoleSummary => RoleWording.Summary(Role);

    /// <summary>Gain shown the way the user thinks about it, rather than as a multiplier.</summary>
    public string GainText => Gain <= 0.001 ? "тишина" : $"{20 * Math.Log10(Gain):+0.0;-0.0;0.0} дБ";

    public string InputGainText => InputGain <= 0.001 ? "тишина" : $"{20 * Math.Log10(InputGain):+0.0;-0.0;0.0} дБ";

    public string JitterText => $"{JitterMs} мс";
    public string MaxJitterText => $"{MaxJitterMs} мс";
    public string LatencyText => $"{LatencyMs} мс";

    public void Load(ReceiverConfig config, AppSettings ui)
    {
        _loading = true;
        _saved = config.Clone();

        Role = config.Role;
        Listen = config.Listen;
        Target = config.Target;
        InputGain = config.InputGain;
        StartMuted = config.StartMuted;
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

        Input = InputModes.FirstOrDefault(m => m.Value == config.Input) ?? InputModes[0];
        Bitrate = Bitrates.FirstOrDefault(b => b.Value == config.Bitrate)
            ?? new BitrateOption(config.Bitrate, $"{config.Bitrate / 1000} кбит/с");

        RefreshDevices();
        Device = Devices.FirstOrDefault(d => d.Selector == config.Device) ?? Devices[0];
        InputDevice = InputDevices.FirstOrDefault(d => d.Selector == config.InputDevice) ?? InputDevices[0];

        Theme = Themes.First(t => t.Value == ui.Theme);
        StartOnLaunch = ui.StartOnLaunch;
        StartMinimised = ui.StartMinimised;
        AutoUpdate = ui.AutoUpdate;
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
        config.Target = Target.Trim();
        config.Input = Input?.Value ?? "wasapi";
        config.InputDevice = InputDevice?.Selector;
        config.InputGain = (float)InputGain;
        config.Bitrate = Bitrate?.Value ?? 32000;
        config.StartMuted = StartMuted;
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
        InputDevices.Clear();
        InputDevices.Add(new DeviceOption(null, "Микрофон по умолчанию", null, true));

        if (!OperatingSystem.IsWindows())
        {
            DeviceNotice = "Список устройств доступен только на Windows.";
            InputNotice = DeviceNotice;
            return;
        }

        try
        {
            AddWindowsDevices();
            DeviceNotice = Devices.Count > 1
                ? null
                : "Виртуальный кабель не найден. Установите Steam или VB-Audio Virtual Cable.";
            InputNotice = InputDevices.Count > 1
                ? null
                : "Микрофонов не найдено. Подключите микрофон или гарнитуру.";
        }
        catch (Exception ex)
        {
            DeviceNotice = $"Не удалось прочитать список устройств: {ex.Message}";
            InputNotice = DeviceNotice;
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

        // No preference list on this side: which microphone is the right one is a question
        // about the room, and guessing at it is how somebody broadcasts the wrong one.
        var preferred = DeviceCatalog.PickCapture(null)?.ID;
        foreach (var device in DeviceCatalog.CaptureDevices())
        {
            InputDevices.Add(new DeviceOption(
                device.FriendlyName, device.FriendlyName, null, device.ID == preferred));
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
    private void ChangeRole() => RoleChangeRequested?.Invoke();

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
        AutoUpdate = AutoUpdate,
    });

    private bool Validate()
    {
        var config = Build();
        if (!config.TryGetKey(out _, out var keyError))
        {
            ValidationError = $"Общий ключ: {keyError}. Нажмите «Сгенерировать» и вставьте тот же ключ на Mac.";
            return false;
        }

        if (config.Role == BridgeRole.Sender)
        {
            // The address of the other machine is the one thing this side cannot work out on
            // its own, so an empty one is a failure worth naming rather than a default.
            if (string.IsNullOrWhiteSpace(config.Target) && string.IsNullOrWhiteSpace(config.Relay))
            {
                ValidationError =
                    "Не задан адрес второго компьютера. Свяжите машины кнопкой «Связать заново» "
                    + "или впишите адрес вида 192.168.1.10:47702.";
                return false;
            }

            try
            {
                config.ResolvePeer();
            }
            catch (Exception ex)
            {
                ValidationError = ex.Message;
                return false;
            }
        }
        else
        {
            try
            {
                ReceiverConfig.ParseEndpoint(config.Listen, 47702);
            }
            catch (Exception)
            {
                ValidationError = $"Не удаётся разобрать адрес «{config.Listen}». Пример: 0.0.0.0:47702";
                return false;
            }
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

        if (config.Gamepad && config.Role == BridgeRole.Receiver)
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
            case nameof(InputGain): OnPropertyChanged(nameof(InputGainText)); break;
            case nameof(Role):
                OnPropertyChanged(nameof(IsGiving));
                OnPropertyChanged(nameof(IsTaking));
                OnPropertyChanged(nameof(RoleTitle));
                OnPropertyChanged(nameof(RoleSummary));
                return;
            case nameof(JitterMs): OnPropertyChanged(nameof(JitterText)); break;
            case nameof(MaxJitterMs): OnPropertyChanged(nameof(MaxJitterText)); break;
            case nameof(LatencyMs): OnPropertyChanged(nameof(LatencyText)); break;
            case nameof(Psk): OnPropertyChanged(nameof(FingerprintText)); break;
            // Bookkeeping and the preferences that apply immediately are not "unsaved edits".
            case nameof(IsDirty) or nameof(ValidationError) or nameof(PskRevealed)
                or nameof(DeviceNotice) or nameof(InputNotice)
                or nameof(Theme) or nameof(StartOnLaunch) or nameof(StartMinimised) or nameof(AutoUpdate):
                return;
        }

        IsDirty = true;
    }
}

public sealed record ThemeOption(ThemePreference Value, string Title);
