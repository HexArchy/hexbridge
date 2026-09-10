using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HexBridge.App.Features;
using HexBridge.DualSense;
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
        new DualSenseFeature());

    private readonly IReadOnlyList<IFeatureUiModule> _modules;
    private readonly DispatcherTimer _timer;
    private readonly string _configPath;
    private readonly AppSettings _ui;

    private ReceiverConfig _config;
    private int _ticksToSample;
    private bool _stoppedByUser;

    public SettingsViewModel Settings { get; } = new();
    public LogViewModel Log { get; } = new();

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
    [ObservableProperty] private string _pauseLabel = "Пауза";
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

        _modules = FeatureUiCatalog.For(_receiver.Features);
        foreach (var page in _modules.SelectMany(module => module.CreatePages())) Pages.Add(page);
        Pages.Add(new FeaturePage("Настройки", Settings));
        Pages.Add(new FeaturePage("Журнал", Log));

        SelectedTab = Math.Clamp(_ui.LastTab, 0, Pages.Count - 1);
        Settings.Load(_config, _ui);
        Settings.SaveRequested += OnSaveRequested;
        Settings.PropertyChanged += OnSettingsPropertyChanged;

        _receiver.Log += Log.Enqueue;

        _timer = new DispatcherTimer(Tick, DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();

        Log.Add(LogLevel.Info, $"hexbridge: конфиг {_configPath}");
        if (!_config.TryGetKey(out _, out var keyError))
        {
            Log.Add(LogLevel.Warning, $"hexbridge: {keyError} — откройте «Настройки» и задайте общий ключ");
        }
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
    }

    // MARK: - Commands

    [RelayCommand]
    private async Task StartAsync()
    {
        _stoppedByUser = false;
        await RunGuarded(() => _receiver.StartAsync(_config));
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        _stoppedByUser = true;
        foreach (var module in _modules) module.Reset();
        await RunGuarded(() => _receiver.StopAsync());
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
        Log.Drain();

        IsRunning = snapshot.IsRunning;

        if (--_ticksToSample <= 0)
        {
            _ticksToSample = 10;
            foreach (var module in _modules) module.Sample(snapshot);
        }

        Headline = snapshot.Status switch
        {
            ReceiverStatus.Live => "Связь есть",
            ReceiverStatus.Muted => "Микрофон выключен",
            ReceiverStatus.SenderLost => "Отправитель молчит",
            ReceiverStatus.WaitingForSender => "Ждём отправителя",
            ReceiverStatus.Failed => "Ошибка",
            _ => _stoppedByUser ? "На паузе" : "Приём остановлен",
        };
        IsGood = snapshot.Status is ReceiverStatus.Live;
        IsWaiting = snapshot.Status is ReceiverStatus.WaitingForSender or ReceiverStatus.Muted or ReceiverStatus.SenderLost;
        IsBad = snapshot.Status is ReceiverStatus.Failed;

        PauseLabel = snapshot.IsRunning ? "Пауза" : "Возобновить";
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
            ReceiverStatus.Muted => "HexBridge — микрофон выключен на Mac",
            ReceiverStatus.SenderLost => "HexBridge — отправитель молчит",
            ReceiverStatus.WaitingForSender => "HexBridge — ждём отправителя",
            ReceiverStatus.Failed => $"HexBridge — ошибка: {snapshot.Detail}",
            _ => _stoppedByUser ? "HexBridge — на паузе" : "HexBridge — приём остановлен",
        };
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        await _receiver.DisposeAsync();
    }
}
