using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HexBridge.App.Features;
using HexBridge.Clipboard;
using HexBridge.Devices;
using HexBridge.Microphone;

using HexBridge.Localization;

namespace HexBridge.App.ViewModels;

public enum TrayState { Idle, Live, Warn, Error }

/// <summary>
/// Owns the receiver and the polling clock. Every visible number comes from a snapshot read
/// on this timer, so a 50 packet/s stream costs the UI ten updates a second, not fifty.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    // The composition root: the one place in the app that names a feature. Everything
    // below works off IFeature and IFeatureUiModule, so a third feature adds a class and
    // one entry here and in FeatureUiCatalog, and changes nothing else.
    // Both halves of the microphone are registered; the role decides which one starts.
    // They share an id, so the pages below are built once and serve either.
    private readonly ReceiverService _receiver = new(
        new MicrophoneFeature(),
        new MicrophoneCaptureFeature(),
        new DevicesFeature(),
        new ClipboardFeature());

    private readonly IReadOnlyList<IFeatureUiModule> _modules;
    private readonly DispatcherTimer _timer;
    private readonly string _configPath;
    private readonly AppSettings _ui;

    /// <summary>
    /// The Bonjour advertisement, alive for as long as the receiver is (PROTOCOL.md,
    /// «Автопоиск хоста»). It is the shell's and not the wizard's, because the reason it
    /// exists outlasts pairing by months: the tag is not tied to an address, so a Mac that
    /// was paired last year finds this PC again after the router hands it a new lease —
    /// but only if the advertisement is still on the wire to be found.
    /// </summary>
    private readonly DiscoveryPublisher _discovery;

    private readonly DateTime _startedAt = DateTime.UtcNow;

    private ReceiverConfig _config;
    private int _ticksToSample;
    private bool _stoppedByUser;

    public SettingsViewModel Settings { get; } = new();
    public LogViewModel Log { get; } = new();

    /// <summary>
    /// «Что делает этот компьютер». Owned by the shell because the answer decides which
    /// features start, which wizard opens and whether this machine advertises itself — and
    /// because it is asked before there is a page to put it on.
    /// </summary>
    public RoleViewModel Role { get; }

    /// <summary>
    /// Updates (docs/UPDATES.md). Driven from the same clock as everything else, and it
    /// does nothing at all on all but one tick a day — and nothing ever, when the switch in
    /// the settings is off.
    /// </summary>
    public UpdateViewModel Updates { get; }

    /// <summary>
    /// The pairing wizard (§9). It lives on the shell rather than on a page because it is
    /// shown over whatever the user was looking at, and because §9.2 makes it the very
    /// first thing an unconfigured install does.
    /// </summary>
    public PairingViewModel Pairing { get; }

    /// <summary>Every tab, in order: the features first, then the app's own two pages.</summary>
    public ObservableCollection<FeaturePage> Pages { get; } = [];

    /// <summary>Set by the App shell; the view model must not reach for windows itself.</summary>
    public Action? RequestShowWindow { get; set; }
    public Action? RequestExit { get; set; }
    public Func<string, Task>? CopyToClipboard { get; set; }
    public Action<ThemePreference>? ApplyTheme { get; set; }

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private TrayState _tray = TrayState.Idle;
    [ObservableProperty] private string _trayTooltip = Strings.Tray_Tip_StoppedReceiving;
    /// <summary>The header's only button: it says what pressing it will do.</summary>
    [ObservableProperty] private string _pauseLabel = Strings.App_Button_Start;
    [ObservableProperty] private string _configPathText = "";

    /// <summary>True while this machine is the one holding the microphone.</summary>
    [ObservableProperty] private bool _isGiving;

    /// <summary>The line under the title, which says which half of the pair this is.</summary>
    [ObservableProperty] private string _tagline = Strings.App_Tagline_Receiving;

    /// <summary>Says what pressing it will do, like every other button in the header.</summary>
    [ObservableProperty] private string _muteLabel = Strings.App_Button_Mute;

    // The window header shows the transport, not any one feature.
    [ObservableProperty] private string _headline = Strings.App_Headline_StoppedReceiving;
    [ObservableProperty] private bool _isGood;
    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private bool _isBad;

    public MainViewModel(string? configPath = null)
    {
        _configPath = configPath ?? ReceiverConfig.ResolvedPath();
        ConfigPathText = _configPath;
        _ui = AppSettings.Load();
        _config = ReceiverConfig.Load(_configPath);
        _discovery = new DiscoveryPublisher(Log.Add);
        Updates = new UpdateViewModel(
            isEnabled: () => _ui.AutoUpdate,
            rememberCheck: when =>
            {
                _ui.LastUpdateCheckUtc = when;
                _ui.Save();
            },
            log: Log.Add);

        Role = new RoleViewModel(CommitRoleAsync);
        Pairing = new PairingViewModel(() => _config, CommitPairingAsync, Log.Add, _discovery);

        _modules = FeatureUiCatalog.For(_receiver.Features);
        foreach (var page in _modules.SelectMany(module => module.CreatePages())) Pages.Add(page);
        Pages.Add(new FeaturePage("Tab_Settings", Settings));
        Pages.Add(new FeaturePage("Tab_Log", Log));

        SelectedTab = Math.Clamp(_ui.LastTab, 0, Pages.Count - 1);
        Settings.Updates = Updates;
        Settings.Load(_config, _ui);
        Settings.SaveRequested += OnSaveRequested;
        Settings.PairRequested += Pairing.Open;
        Settings.RoleChangeRequested += () => Role.Open(_config.Role, firstRun: false);
        Settings.CheckRequested += Pairing.OpenChecks;
        Settings.PropertyChanged += OnSettingsPropertyChanged;

        _receiver.Log += Log.Enqueue;

        _timer = new DispatcherTimer(Tick, DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();

        Localizer.Instance.LanguageChanged += OnLanguageChanged;

        Log.Add(LogLevel.Info, Loc.F(Strings.Log_Config, _configPath));
        ApplyRole();

        // A fresh install is asked what it is before it is asked to pair, because the answer
        // decides who makes the key. An install that predates roles is not asked at all: it
        // has a role — the one it has been doing — and a question with an obvious answer is
        // a question not worth putting in somebody's way.
        if (!_ui.RoleChosen && !_config.TryGetKey(out _, out _))
        {
            Role.Open(_config.Role, firstRun: true);
            return;
        }

        _ui.RoleChosen = true;

        // §9: an install that has never been paired opens the wizard instead of a screen
        // full of empty telemetry. Once a key exists it never appears on its own again —
        // it stays one button away in the settings as «Связать заново».
        if (!_config.TryGetKey(out _, out var keyError))
        {
            Log.Add(LogLevel.Warning, Loc.F(Strings.Log_NoKeyOpeningWizard, keyError));
            Pairing.Open();
        }
    }

    /// <summary>
    /// The role was answered. Writing it to the config is what makes it real; everything
    /// else — which features start, which wizard opens, whether we advertise — follows from
    /// the file.
    /// </summary>
    private async Task CommitRoleAsync(BridgeRole role)
    {
        _ui.RoleChosen = true;
        _ui.Save();

        if (_config.Role != role)
        {
            var next = _config.Clone();
            next.Role = role;
            try
            {
                next.Save(_configPath);
            }
            catch (Exception ex)
            {
                Log.Add(LogLevel.Error, Loc.F(Strings.Log_ConfigSaveFailed, ex.Message));
                return;
            }

            _config = next;
            Settings.Load(_config, _ui);
            Log.Add(LogLevel.Info, Loc.F(Strings.Log_RoleSet, RoleWording.Title(role).ToLowerInvariant()));

            // The old advertisement described a machine that listened. Leaving it up would
            // point the other side at a port nothing is on any more.
            _discovery.Stop();
            ApplyRole();
            if (_receiver.IsRunning) await RestartAsync();
        }
        else
        {
            ApplyRole();
        }

        // Changing role does not change the key, but an install that has never had one is
        // exactly where this question came from.
        if (!_config.TryGetKey(out _, out _)) Pairing.Open();
    }

    /// <summary>Pushes the role into everything that renders differently because of it.</summary>
    private void ApplyRole()
    {
        IsGiving = _config.Role == BridgeRole.Sender;
        Role.Current = _config.Role;
        Pairing.Role = _config.Role;
        Tagline = IsGiving ? Strings.App_Tagline_Sharing : Strings.App_Tagline_Receiving;
    }

    /// <summary>
    /// §9.5: the key and the address the wizard generated become the config, and the
    /// receiver is restarted onto them. Writing the file is what makes the pairing real —
    /// everything before this point is a code on a screen.
    /// </summary>
    private async Task CommitPairingAsync(PairingPayload payload)
    {
        var next = _config.Clone();
        next.Psk = payload.Psk;

        if (next.Role == BridgeRole.Sender)
        {
            // Here the payload came off the other machine's screen, and its whole point is
            // the address: that is the one thing this side could not have worked out alone.
            next.Target = $"{payload.Host}:{payload.Port}";
        }
        else
        {
            // The listen address stays a wildcard: the payload carries the address the other
            // machine should dial, which is not the same thing as the interface we bind.
            if (string.IsNullOrWhiteSpace(next.Listen)) next.Listen = $"0.0.0.0:{payload.Port}";
        }

        try
        {
            next.Save(_configPath);
        }
        catch (Exception ex)
        {
            Log.Add(LogLevel.Error, Loc.F(Strings.Log_ConfigSaveFailed, ex.Message));
            return;
        }

        _config = next;
        Settings.Load(_config, _ui);
        Log.Add(LogLevel.Info, Loc.F(Strings.Log_Paired, payload.Fingerprint));

        // The receiver has to come up on the new key before the checks can say anything
        // truthful about packets arriving.
        await RestartAsync();
    }

    partial void OnSelectedTabChanged(int value)
    {
        if (_ui.LastTab == value) return;
        _ui.LastTab = value;
        _ui.Save();
    }

    public bool StartOnLaunch => _ui.StartOnLaunch;
    public bool StartMinimised => _ui.StartMinimised;
    public ThemePreference Theme => _ui.Theme;

    public void AttachClipboard(Func<string, Task> copy)
    {
        CopyToClipboard = copy;
        Log.CopyToClipboard = copy;
        Settings.CopyToClipboard = copy;
        Pairing.CopyToClipboard = copy;
    }

    // MARK: - Commands

    [RelayCommand]
    private async Task StartAsync()
    {
        _stoppedByUser = false;
        await RunGuarded(() => _receiver.StartAsync(_config));
        Advertise();
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        _stoppedByUser = true;
        foreach (var module in _modules) module.Reset();
        await RunGuarded(() => _receiver.StopAsync());
        // A PC that is not listening must not be findable: a Mac that dialled it would get
        // silence and no explanation, which is worse than an empty list.
        if (!Pairing.IsOpen) _discovery.Stop();
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        _stoppedByUser = false;
        foreach (var module in _modules) module.Reset();
        await RunGuarded(async () =>
        {
            await _receiver.StopAsync();
            await _receiver.StartAsync(_config);
        });
        Advertise();
    }

    /// <summary>
    /// Puts the current key's tag and the current port on the wire. Idempotent, so calling
    /// it after every transition costs nothing when nothing moved — and republishes the
    /// moment the key changes, which is what makes a re-pairing stop the old Mac from
    /// finding this PC.
    /// </summary>
    private void Advertise()
    {
        // Only the machine that listens has an address worth publishing. One that dials
        // would be advertising an ephemeral port nothing answers on.
        if (!_receiver.IsRunning || _config.Role != BridgeRole.Receiver) return;
        _discovery.Publish(_config, Pairing.MachineName);
    }

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        if (_receiver.IsRunning) await StopAsync();
        else await StartAsync();
    }

    /// <summary>
    /// Mute, live: no restart and no reconnect. The flag rides the keepalive, so the far
    /// end says «микрофон заглушен» rather than watching the sound stop for no reason.
    /// </summary>
    [RelayCommand]
    private void ToggleMute()
    {
        _receiver.Muted = !_receiver.Muted;
        Refresh();
    }

    [RelayCommand]
    private void ShowWindow() => RequestShowWindow?.Invoke();

    [RelayCommand]
    private void Exit() => RequestExit?.Invoke();

    /// <summary>
    /// Runs a pipeline transition off the UI thread — claiming a WASAPI endpoint and
    /// resolving a relay name both block for long enough to drop frames on the window.
    /// </summary>
    private async Task RunGuarded(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await Task.Run(action);
        }
        catch (Exception ex)
        {
            Log.Add(LogLevel.Error, Loc.F(Strings.Log_Prefix, ex.Message));
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    // MARK: - Settings

    private async void OnSaveRequested()
    {
        var next = Settings.Build();
        try
        {
            next.Save(_configPath);
        }
        catch (Exception ex)
        {
            Log.Add(LogLevel.Error, Loc.F(Strings.Log_ConfigSaveFailed, ex.Message));
            return;
        }

        _ui.StartOnLaunch = Settings.StartOnLaunch;
        _ui.StartMinimised = Settings.StartMinimised;
        _ui.AutoUpdate = Settings.AutoUpdate;

        if (Autostart.IsEnabled != Settings.Autostart)
        {
            Settings.AutostartError = Autostart.TrySet(Settings.Autostart, out var autostartError)
                ? null
                : autostartError;
        }
        _ui.Save();

        _config = next;
        Settings.MarkSaved(next);
        Log.Add(LogLevel.Info, Strings.Log_SettingsSaved);

        // Applying settings means rebinding the socket and the device, so only restart
        // something that was actually running.
        if (_receiver.IsRunning) await RestartAsync();
    }

    /// <summary>
    /// True while a language change is being pushed through the app.
    ///
    /// <para>
    /// Retranslating the settings page hands its combo boxes new item lists, and a combo box
    /// handed a new list writes its selection straight back through the binding before the
    /// new selection has been put in — so the page reports «the language changed» in the
    /// middle of changing the language. Without this flag that is an infinite loop, and it
    /// is not a hypothetical one: it hung the window the first time this was tried.
    /// </para>
    /// </summary>
    private bool _switchingLanguage;

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Whatever the page says about itself while it is being retranslated is an artefact
        // of the retranslation, not a decision the user made.
        if (_switchingLanguage) return;

        if (e.PropertyName == nameof(SettingsViewModel.AutoUpdate))
        {
            // Applied on the spot rather than on «Сохранить»: a switch that says «не
            // проверять» must stop the next check, not the one after the user remembers to
            // press save.
            _ui.AutoUpdate = Settings.AutoUpdate;
            _ui.Save();
            return;
        }

        if (e.PropertyName == nameof(SettingsViewModel.Language))
        {
            // Applied at once, like the theme. Language.Apply moves the whole process — the
            // string table, the number formats and every thread started after it — and the
            // localizer's event brings the window along; nothing is restarted.
            if (Settings.Language is null || _ui.Language == Settings.Language.Value) return;

            _ui.Language = Settings.Language.Value;
            _ui.Save();

            _switchingLanguage = true;
            try
            {
                HexBridge.Localization.Language.Apply(_ui.Language);
            }
            finally
            {
                _switchingLanguage = false;
            }
            return;
        }

        if (e.PropertyName != nameof(SettingsViewModel.Theme) || Settings.Theme is null) return;

        _ui.Theme = Settings.Theme.Value;
        _ui.Save();
        ApplyTheme?.Invoke(_ui.Theme);
    }

    // MARK: - Language

    /// <summary>
    /// Set by the App shell: the control theme keeps its own table of strings for things like
    /// the text box context menu, and reaching into a theme is not a view model's business.
    /// </summary>
    public Action? ApplySemiLocale { get; set; }

    /// <summary>
    /// The language moved. Almost nothing has to happen here: every number and every state
    /// line on screen is rebuilt from a snapshot on the next tick, a tenth of a second away.
    /// What is left is the text that is <em>not</em> derived from a snapshot — the tab strip,
    /// the option lists in the settings, the wizard's own wording — and this is that list.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var page in Pages) page.Retranslate();

        // The tagline is worked out from the role rather than from a snapshot, so the tick
        // below would not touch it.
        ApplyRole();
        Settings.Retranslate();
        Role.Retranslate();
        Pairing.Retranslate();
        Updates.Retranslate();
        ApplySemiLocale?.Invoke();
        Refresh();
    }

    // MARK: - Polling

    private void Refresh()
    {
        var snapshot = _receiver.Snapshot;

        foreach (var module in _modules) module.Apply(snapshot);
        Pairing.Apply(snapshot);
        Log.Drain();

        IsRunning = snapshot.IsRunning;

        if (--_ticksToSample <= 0)
        {
            _ticksToSample = 10;
            foreach (var module in _modules) module.Sample(snapshot);
            // Once a second, and `TickAsync` returns immediately on all but one of them a
            // day. Fire-and-forget: an update check must never make the UI wait.
            _ = Updates.TickAsync(_ui.LastUpdateCheckUtc, DateTime.UtcNow, DateTime.UtcNow - _startedAt);
        }

        MuteLabel = _receiver.Muted ? Strings.App_Button_Unmute : Strings.App_Button_Mute;

        // The header names no machine on purpose. Either end may be nameless until its first
        // HELLO, and «Ждём » with nothing after it is worse than a sentence that is always
        // true. The name has a home on the status page, where there is room for it.
        Headline = snapshot.Status switch
        {
            ReceiverStatus.Live => Strings.App_Headline_Live,
            ReceiverStatus.Muted => Strings.App_Headline_Muted,
            ReceiverStatus.SenderLost => Strings.App_Headline_Lost,
            ReceiverStatus.WaitingForSender => Strings.App_Headline_Waiting,
            ReceiverStatus.Failed => Strings.App_Headline_Failed,
            _ => _stoppedByUser ? Strings.App_Headline_Paused
                : IsGiving ? Strings.App_Headline_StoppedSharing
                : Strings.App_Headline_StoppedReceiving,
        };
        IsGood = snapshot.Status is ReceiverStatus.Live;
        IsWaiting = snapshot.Status is ReceiverStatus.WaitingForSender or ReceiverStatus.Muted or ReceiverStatus.SenderLost;
        IsBad = snapshot.Status is ReceiverStatus.Failed;

        PauseLabel = snapshot.IsRunning ? Strings.App_Button_Pause : Strings.App_Button_Start;
        Tray = snapshot.Status switch
        {
            ReceiverStatus.Live => TrayState.Live,
            ReceiverStatus.Muted or ReceiverStatus.SenderLost or ReceiverStatus.WaitingForSender => TrayState.Warn,
            ReceiverStatus.Failed => TrayState.Error,
            _ => TrayState.Idle,
        };
        TrayTooltip = snapshot.Status switch
        {
            ReceiverStatus.Live => Loc.F(Strings.Tray_Tip_Live,
                snapshot.PacketsPerSecond.ToString("F0", System.Globalization.CultureInfo.CurrentCulture)),
            ReceiverStatus.Muted => IsGiving ? Strings.Tray_Tip_MutedSharing : Strings.Tray_Tip_MutedReceiving,
            ReceiverStatus.SenderLost => Strings.Tray_Tip_Lost,
            ReceiverStatus.WaitingForSender => Strings.Tray_Tip_Waiting,
            ReceiverStatus.Failed => Strings.Tray_Tip_Failed,
            _ => _stoppedByUser
                ? Strings.Tray_Tip_Paused
                : IsGiving ? Strings.Tray_Tip_StoppedSharing : Strings.Tray_Tip_StoppedReceiving,
        };
    }

    public async ValueTask DisposeAsync()
    {
        Localizer.Instance.LanguageChanged -= OnLanguageChanged;
        _timer.Stop();
        _discovery.Dispose();
        await Pairing.DisposeAsync();
        await _receiver.DisposeAsync();
    }
}
