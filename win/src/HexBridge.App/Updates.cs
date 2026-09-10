using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velopack;
using Velopack.Sources;

namespace HexBridge.App;

/// <summary>
/// The update button, and the daily check behind it.
///
/// <para>
/// Velopack rather than a downloader of our own, and for a reason that is not about
/// Windows at all: <c>vpk</c> builds the installer and the release channel <b>from
/// macOS</b> (<c>vpk [win] pack -r win-x64</c>), which is the machine this project is
/// developed and released on. Anything else would have meant a Windows runner in the
/// release workflow for the one step that cannot be skipped.
/// </para>
///
/// <para>
/// Three rules, and they are the whole design (see <see cref="UpdatePolicy"/>):
/// </para>
/// <list type="number">
///   <item>a check happens at most once a day, and never in the first two minutes;</item>
///   <item>nothing is downloaded or installed until the user presses something — this
///         class has no path from «check» to «apply» that does not pass through a
///         command;</item>
///   <item>the whole thing is off with one switch in the settings, and off means off.</item>
/// </list>
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly Func<bool> _isEnabled;
    private readonly Action<DateTime> _rememberCheck;
    private readonly Action<LogLevel, string> _log;
    private readonly UpdateManager? _manager;

    private UpdateInfo? _pending;

    [ObservableProperty] private UpdateStage _stage = UpdateStage.Idle;
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _availableVersion = "";

    /// <summary>
    /// False on a build that was not installed by Velopack — a portable unzip, a
    /// `dotnet run`, the CI test host. There is nothing to update in place there, and
    /// offering a button that cannot work is worse than offering none.
    /// </summary>
    public bool IsSupported => _manager?.IsInstalled == true;

    /// <summary>The version running now, for the settings line.</summary>
    public string CurrentVersion { get; }

    public UpdateViewModel(
        Func<bool> isEnabled,
        Action<DateTime> rememberCheck,
        Action<LogLevel, string> log,
        UpdateManager? manager = null)
    {
        _isEnabled = isEnabled;
        _rememberCheck = rememberCheck;
        _log = log;

        try
        {
            _manager = manager ?? new UpdateManager(new GithubSource(UpdatePolicy.Repository, null, false));
        }
        catch (Exception ex)
        {
            // A malformed install directory, or none. Not a reason to fail a launch.
            _log(LogLevel.Warning, $"hexbridge: обновления недоступны — {ex.Message}");
            _manager = null;
        }

        CurrentVersion = _manager?.CurrentVersion?.ToString() ?? "не установлено через инсталлятор";
    }

    /// <summary>
    /// Runs a check if one is due. Called from the shell's clock; returns without touching
    /// the network on every tick but one a day, and on none at all when the setting is off.
    /// </summary>
    public async Task TickAsync(DateTime? lastCheckUtc, DateTime nowUtc, TimeSpan uptime)
    {
        if (!IsSupported || Stage is UpdateStage.Checking or UpdateStage.Downloading) return;
        if (uptime < UpdatePolicy.StartupDelay) return;
        if (!UpdatePolicy.ShouldCheck(_isEnabled(), lastCheckUtc, nowUtc)) return;

        await CheckAsync();
    }

    /// <summary>«Проверить сейчас». Ignores the schedule; still obeys the off switch.</summary>
    [RelayCommand]
    public async Task CheckAsync()
    {
        if (_manager is null || !IsSupported) return;
        if (!_isEnabled())
        {
            Stage = UpdateStage.Idle;
            Headline = "Проверка обновлений выключена";
            Detail = "Включите её в настройках, если захотите узнавать о новых версиях.";
            return;
        }

        Stage = UpdateStage.Checking;
        Headline = "Проверяю обновления…";
        Detail = "";

        try
        {
            var found = await _manager.CheckForUpdatesAsync();
            _rememberCheck(DateTime.UtcNow);

            if (found is null)
            {
                _pending = null;
                Stage = UpdateStage.Idle;
                Headline = "Установлена последняя версия";
                Detail = $"Сейчас {CurrentVersion}.";
                return;
            }

            _pending = found;
            AvailableVersion = found.TargetFullRelease.Version.ToString();
            Stage = UpdateStage.Available;
            Headline = $"Есть версия {AvailableVersion}";
            // Nothing has been downloaded at this point, and the wording says so: the user
            // is being asked, not informed of a decision already taken.
            Detail = "Скачать и установить? Приём звука прервётся на несколько секунд при перезапуске.";
        }
        catch (Exception ex)
        {
            Stage = UpdateStage.Failed;
            Headline = "Не удалось проверить обновления";
            Detail = ex.Message;
            _log(LogLevel.Warning, $"hexbridge: проверка обновлений не удалась — {ex.Message}");
        }
    }

    /// <summary>«Скачать». The first thing that touches the disk, and only on a press.</summary>
    [RelayCommand]
    public async Task DownloadAsync()
    {
        if (_manager is null || _pending is null) return;

        Stage = UpdateStage.Downloading;
        Headline = $"Скачиваю {AvailableVersion}…";
        Detail = "";

        try
        {
            await _manager.DownloadUpdatesAsync(_pending, progress =>
            {
                Detail = $"{progress} %";
            });

            Stage = UpdateStage.Ready;
            Headline = $"Версия {AvailableVersion} готова";
            Detail = "Установится при следующем запуске — или нажмите «Перезапустить».";
        }
        catch (Exception ex)
        {
            Stage = UpdateStage.Failed;
            Headline = "Не удалось скачать обновление";
            Detail = ex.Message;
            _log(LogLevel.Error, $"hexbridge: загрузка обновления не удалась — {ex.Message}");
        }
    }

    /// <summary>«Перезапустить». Applies what was downloaded and comes back up.</summary>
    [RelayCommand]
    public void Apply()
    {
        if (_manager is null || _pending is null || Stage != UpdateStage.Ready) return;

        _log(LogLevel.Info, $"hexbridge: ставлю {AvailableVersion} и перезапускаюсь");
        _manager.ApplyUpdatesAndRestart(_pending);
    }

    /// <summary>«Скачать» is offered only after a check found something.</summary>
    public bool CanDownload => Stage == UpdateStage.Available;

    /// <summary>«Перезапустить» is offered only once the bits are on disk.</summary>
    public bool CanApply => Stage == UpdateStage.Ready;

    public bool IsBusy => Stage is UpdateStage.Checking or UpdateStage.Downloading;

    public bool HasMessage => Headline.Length > 0;

    partial void OnStageChanged(UpdateStage value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(IsBusy));
    }

    partial void OnHeadlineChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasMessage));
    }

    /// <summary>What the settings page says when nothing is happening.</summary>
    public string Summary => IsSupported
        ? $"Версия {CurrentVersion}. Проверка раз в сутки, установка — только по вашей команде."
        : "Эта сборка распакована из архива, а не установлена. Обновляется заменой файлов вручную.";
}
