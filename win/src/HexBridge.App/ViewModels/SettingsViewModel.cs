using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HexBridge.Microphone;

using HexBridge.Localization;

namespace HexBridge.App.ViewModels;

public sealed record OutputMode(string Value, string Title);

/// <summary>Where the giving role reads audio from. Same shape as <see cref="OutputMode"/>.</summary>
public sealed record InputMode(string Value, string Title);

public sealed record DeviceOption(string? Selector, string Title, string? Paired, bool Recommended)
{
    /// <summary>
    /// «пара для игр: CABLE Output» — the sentence around the name, not just the name. It is
    /// built here rather than by a StringFormat in the view, because the sentence is a
    /// translated string and XAML has nowhere to look one up.
    /// </summary>
    public string PairedText => Paired is null ? "" : Loc.F(Strings.Settings_Paired_Format, Paired);
}

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

    // The four option lists are rebuilt rather than translated in place: a combo box binds
    // to the item, not to a string inside it, so a language change has to hand it new items
    // and put the selection back on the one that means the same thing.
    public IReadOnlyList<InputMode> InputModes { get; private set; } = BuildInputModes();

    /// <summary>
    /// Opus at 32 kbit/s is what the Mac has always sent and what the receiver is tuned
    /// for; the two either side of it are for a link that is worse or better than usual.
    /// </summary>
    public IReadOnlyList<BitrateOption> Bitrates { get; private set; } = BuildBitrates();

    public IReadOnlyList<OutputMode> OutputModes { get; private set; } = BuildOutputModes();

    public IReadOnlyList<ThemeOption> Themes { get; private set; } = BuildThemes();

    /// <summary>«System / English / Русский». Each language names itself, in itself.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; private set; } = BuildLanguages();

    private static InputMode[] BuildInputModes() =>
    [
        new("wasapi", Strings.Settings_Input_Wasapi),
        new("tone", Strings.Settings_Input_Tone),
        new("null", Strings.Settings_Input_Null),
    ];

    private static BitrateOption[] BuildBitrates() =>
    [
        new(16000, Strings.Settings_Bitrate_16),
        new(24000, Strings.Settings_Bitrate_24),
        new(32000, Strings.Settings_Bitrate_32),
        new(48000, Strings.Settings_Bitrate_48),
        new(64000, Strings.Settings_Bitrate_64),
    ];

    private static OutputMode[] BuildOutputModes() =>
    [
        new("wasapi", Strings.Settings_Output_Wasapi),
        new("null", Strings.Settings_Output_Null),
        new("wav:hexbridge.wav", Strings.Settings_Output_Wav),
    ];

    private static ThemeOption[] BuildThemes() =>
    [
        new(ThemePreference.System, Strings.Settings_Theme_System),
        new(ThemePreference.Light, Strings.Settings_Theme_Light),
        new(ThemePreference.Dark, Strings.Settings_Theme_Dark),
    ];

    private static LanguageOption[] BuildLanguages() =>
    [
        new(AppLanguage.System, Strings.Settings_Language_System),
        new(AppLanguage.English, Strings.Settings_Language_English),
        new(AppLanguage.Russian, Strings.Settings_Language_Russian),
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

    /// <summary>
    /// Applied on the spot like the theme, and for the same reason: a language menu that
    /// needs «Сохранить» before it does anything is a language menu nobody trusts.
    /// </summary>
    [ObservableProperty] private LanguageOption? _language;
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
    public string GainText => Gain <= 0.001 ? Strings.Settings_Gain_Silence : Loc.Db(20 * Math.Log10(Gain));

    public string InputGainText =>
        InputGain <= 0.001 ? Strings.Settings_Gain_Silence : Loc.Db(20 * Math.Log10(InputGain));

    public string JitterText => Loc.Ms(JitterMs);
    public string MaxJitterText => Loc.Ms(MaxJitterMs);
    public string LatencyText => Loc.Ms(LatencyMs);

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
            ?? new BitrateOption(config.Bitrate, Loc.Kbits(config.Bitrate));

        RefreshDevices();
        Device = Devices.FirstOrDefault(d => d.Selector == config.Device) ?? Devices[0];
        InputDevice = InputDevices.FirstOrDefault(d => d.Selector == config.InputDevice) ?? InputDevices[0];

        Theme = Themes.First(t => t.Value == ui.Theme);
        Language = Languages.First(l => l.Value == ui.Language);
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
        Devices.Add(new DeviceOption(null, Strings.Settings_Device_Auto, null, true));
        InputDevices.Clear();
        InputDevices.Add(new DeviceOption(null, Strings.Settings_Device_DefaultMic, null, true));

        if (!OperatingSystem.IsWindows())
        {
            DeviceNotice = Strings.Settings_Notice_WindowsOnly;
            InputNotice = DeviceNotice;
            return;
        }

        try
        {
            AddWindowsDevices();
            DeviceNotice = Devices.Count > 1 ? null : Strings.Settings_Notice_NoCable;
            InputNotice = InputDevices.Count > 1 ? null : Strings.Settings_Notice_NoMicrophone;
        }
        catch (Exception ex)
        {
            DeviceNotice = Loc.F(Strings.Settings_Notice_ListFailed, ex.Message);
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
    public string FingerprintText => PairingPayload.FingerprintOfPsk(Psk) ?? Strings.Settings_Fingerprint_None;

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
        Language = Language?.Value ?? AppLanguage.System,
        StartOnLaunch = StartOnLaunch,
        StartMinimised = StartMinimised,
        AutoUpdate = AutoUpdate,
    });

    /// <summary>
    /// The language changed. The computed labels only need a notification; the four option
    /// lists have to be rebuilt, because a combo box holds the item and not the string inside
    /// it — and the selection is put back by value, so nothing the user chose moves.
    /// </summary>
    public void Retranslate()
    {
        var loading = _loading;
        _loading = true;
        try
        {
            var input = Input?.Value;
            var bitrate = Bitrate?.Value;
            var output = Output?.Value;
            var theme = Theme?.Value;
            var language = Language?.Value;

            InputModes = BuildInputModes();
            Bitrates = BuildBitrates();
            OutputModes = BuildOutputModes();
            Themes = BuildThemes();
            Languages = BuildLanguages();

            OnPropertyChanged(nameof(InputModes));
            OnPropertyChanged(nameof(Bitrates));
            OnPropertyChanged(nameof(OutputModes));
            OnPropertyChanged(nameof(Themes));
            OnPropertyChanged(nameof(Languages));

            Input = InputModes.FirstOrDefault(m => m.Value == input) ?? InputModes[0];
            Bitrate = Bitrates.FirstOrDefault(b => b.Value == bitrate)
                ?? new BitrateOption(bitrate ?? 32000, Loc.Kbits(bitrate ?? 32000));
            Output = OutputModes.FirstOrDefault(m => m.Value == output) ?? OutputModes[0];
            Theme = Themes.FirstOrDefault(t => t.Value == theme) ?? Themes[0];
            Language = Languages.FirstOrDefault(l => l.Value == language) ?? Languages[0];

            RefreshDevices();
            var device = Device?.Selector;
            var inputDevice = InputDevice?.Selector;
            Device = Devices.FirstOrDefault(d => d.Selector == device) ?? Devices[0];
            InputDevice = InputDevices.FirstOrDefault(d => d.Selector == inputDevice) ?? InputDevices[0];

            OnPropertyChanged(nameof(RoleTitle));
            OnPropertyChanged(nameof(RoleSummary));
            OnPropertyChanged(nameof(GainText));
            OnPropertyChanged(nameof(InputGainText));
            OnPropertyChanged(nameof(JitterText));
            OnPropertyChanged(nameof(MaxJitterText));
            OnPropertyChanged(nameof(LatencyText));
            OnPropertyChanged(nameof(FingerprintText));

            // A message the user is looking at has to change with the rest of the page, and
            // the only honest way to re-word it is to work it out again.
            if (ValidationError is not null) Validate();
        }
        finally
        {
            _loading = loading;
        }
    }

    private bool Validate()
    {
        var config = Build();
        if (!config.TryGetKey(out _, out var keyError))
        {
            ValidationError = Loc.F(Strings.Settings_Error_Key, keyError);
            return false;
        }

        if (config.Role == BridgeRole.Sender)
        {
            // The address of the other machine is the one thing this side cannot work out on
            // its own, so an empty one is a failure worth naming rather than a default.
            if (string.IsNullOrWhiteSpace(config.Target) && string.IsNullOrWhiteSpace(config.Relay))
            {
                ValidationError = Strings.Settings_Error_NoTarget;
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
                ValidationError = Loc.F(Strings.Settings_Error_Listen, config.Listen);
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
                ValidationError = Loc.F(Strings.Settings_Error_Relay, config.Relay);
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
                ValidationError = Loc.F(Strings.Settings_Error_UsbIp, config.UsbIpListen);
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
                or nameof(Theme) or nameof(Language)
                or nameof(StartOnLaunch) or nameof(StartMinimised) or nameof(AutoUpdate):
                return;
        }

        IsDirty = true;
    }
}

public sealed record ThemeOption(ThemePreference Value, string Title);

public sealed record LanguageOption(AppLanguage Value, string Title);
