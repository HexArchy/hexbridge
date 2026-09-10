using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HexBridge.App.Features;
using HexBridge.Clipboard;
using HexBridge.Devices;
using HexBridge.Microphone;

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
    private readonly ReceiverService _receiver = new(
        new MicrophoneFeature(),
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
    [ObservableProperty] private string _trayTooltip = "HexBridge — приём остановлен";
    /// <summary>The header's only button: it says what pressing it will do.</summary>
    [ObservableProperty] private string _pauseLabel = "Запустить";
    [ObservableProperty] private string _configPathText = "";

    // The window header shows the transport, not any one feature.
    [ObservableProperty] private string _headline = "Приём остановлен";
    [ObservableProperty] private bool _isGood;
    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private bool _isBad;

    public MainViewModel(string? configPath = null)
    {
        _configPath = configPath ?? ReceiverConfig.DefaultPath;
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

        Pairing = new PairingViewModel(() => _config, CommitPairingAsync, Log.Add, _discovery);

        _modules = FeatureUiCatalog.For(_receiver.Features);
        foreach (var page in _modules.SelectMany(module => module.CreatePages())) Pages.Add(page);
        Pages.Add(new FeaturePage("Настройки", Settings));
        Pages.Add(new FeaturePage("Журнал", Log));

        SelectedTab = Math.Clamp(_ui.LastTab, 0, Pages.Count - 1);
        Settings.Updates = Updates;
        Settings.Load(_config, _ui);
        Settings.SaveRequested += OnSaveRequested;
        Settings.PairRequested += Pairing.Open;
        Settings.CheckRequested += Pairing.OpenChecks;
        Settings.PropertyChanged += OnSettingsPropertyChanged;

        _receiver.Log += Log.Enqueue;

        _timer = new DispatcherTimer(Tick, DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();

        Log.Add(LogLevel.Info, $"hexbridge: конфиг {_configPath}");

        // §9: an install that has never been paired opens the wizard instead of a screen
        // full of empty telemetry. Once a key exists it never appears on its own again —
        // it stays one button away in the settings as «Связать заново».
        if (!_config.TryGetKey(out _, out var keyError))
        {
            Log.Add(LogLevel.Warning, $"hexbridge: {keyError} — запускаем мастер связывания");
            Pairing.Open();
        }
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
        // The listen address stays a wildcard: the payload carries the address the Mac
        // should dial, which is not the same thing as the interface we bind.
        if (string.IsNullOrWhiteSpace(next.Listen)) next.Listen = $"0.0.0.0:{payload.Port}";

        try
        {
            next.Save(_configPath);
        }
        catch (Exception ex)
        {
            Log.Add(LogLevel.Error, $"hexbridge: не удалось сохранить конфиг: {ex.Message}");
            return;
        }

        _config = next;
        Settings.Load(_config, _ui);
        Log.Add(LogLevel.Info, $"hexbridge: связано, отпечаток ключа {payload.Fingerprint}");

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
        if (!_receiver.IsRunning) return;
        _discovery.Publish(_config, Pairing.MachineName);
    }

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        if (_receiver.IsRunning) await StopAsync();
        else await StartAsync();
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
            Log.Add(LogLevel.Error, $"hexbridge: {ex.Message}");
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
            Log.Add(LogLevel.Error, $"hexbridge: не удалось сохранить конфиг: {ex.Message}");
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
        Log.Add(LogLevel.Info, "hexbridge: настройки сохранены");

        // Applying settings means rebinding the socket and the device, so only restart
        // something that was actually running.
        if (_receiver.IsRunning) await RestartAsync();
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.AutoUpdate))
        {
            // Applied on the spot rather than on «Сохранить»: a switch that says «не
            // проверять» must stop the next check, not the one after the user remembers to
            // press save.
            _ui.AutoUpdate = Settings.AutoUpdate;
            _ui.Save();
            return;
        }

        if (e.PropertyName != nameof(SettingsViewModel.Theme) || Settings.Theme is null) return;

        _ui.Theme = Settings.Theme.Value;
        _ui.Save();
        ApplyTheme?.Invoke(_ui.Theme);
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

        Headline = snapshot.Status switch
        {
            ReceiverStatus.Live => "Связь есть",
            ReceiverStatus.Muted => "Микрофон заглушен",
            ReceiverStatus.SenderLost => "Mac замолчал",
            ReceiverStatus.WaitingForSender => "Ждём Mac",
            ReceiverStatus.Failed => "Ошибка",
            _ => _stoppedByUser ? "На паузе" : "Приём остановлен",
        };
        IsGood = snapshot.Status is ReceiverStatus.Live;
        IsWaiting = snapshot.Status is ReceiverStatus.WaitingForSender or ReceiverStatus.Muted or ReceiverStatus.SenderLost;
        IsBad = snapshot.Status is ReceiverStatus.Failed;

        PauseLabel = snapshot.IsRunning ? "Пауза" : "Запустить";
        Tray = snapshot.Status switch
        {
            ReceiverStatus.Live => TrayState.Live,
            ReceiverStatus.Muted or ReceiverStatus.SenderLost or ReceiverStatus.WaitingForSender => TrayState.Warn,
            ReceiverStatus.Failed => TrayState.Error,
            _ => TrayState.Idle,
        };
        TrayTooltip = snapshot.Status switch
        {
            ReceiverStatus.Live => $"HexBridge — звук идёт, {snapshot.PacketsPerSecond:F0} пак/с",
            ReceiverStatus.Muted => "HexBridge — микрофон заглушен на Mac",
            ReceiverStatus.SenderLost => "HexBridge — Mac замолчал",
            ReceiverStatus.WaitingForSender => "HexBridge — ждём Mac",
            ReceiverStatus.Failed => "HexBridge — ошибка, откройте окно",
            _ => _stoppedByUser ? "HexBridge — на паузе" : "HexBridge — приём остановлен",
        };
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        _discovery.Dispose();
        await Pairing.DisposeAsync();
        await _receiver.DisposeAsync();
    }
}
