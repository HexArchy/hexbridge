using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velopack;
using Velopack.Sources;

using HexBridge.Localization;

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
            _log(LogLevel.Warning, Loc.F(Strings.Log_UpdatesUnavailable, ex.Message));
            _manager = null;
        }

        CurrentVersion = _manager?.CurrentVersion?.ToString() ?? Strings.Update_Version_Unknown;
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
            Headline = Strings.Update_Off_Headline;
            Detail = Strings.Update_Off_Detail;
            return;
        }

        Stage = UpdateStage.Checking;
        Headline = Strings.Update_Checking;
        Detail = "";

        try
        {
            var found = await _manager.CheckForUpdatesAsync();
            _rememberCheck(DateTime.UtcNow);

            if (found is null)
            {
                _pending = null;
                Stage = UpdateStage.Idle;
                Headline = Strings.Update_Current;
                Detail = Loc.F(Strings.Update_Current_Detail, CurrentVersion);
                return;
            }

            _pending = found;
            AvailableVersion = found.TargetFullRelease.Version.ToString();
            Stage = UpdateStage.Available;
            Headline = Loc.F(Strings.Update_Available, AvailableVersion);
            // Nothing has been downloaded at this point, and the wording says so: the user
            // is being asked, not informed of a decision already taken.
            Detail = Strings.Update_Available_Detail;
        }
        catch (Exception ex)
        {
            Stage = UpdateStage.Failed;
            Headline = Strings.Update_CheckFailed;
            Detail = ex.Message;
            _log(LogLevel.Warning, Loc.F(Strings.Log_UpdateCheckFailed, ex.Message));
        }
    }

    /// <summary>«Скачать». The first thing that touches the disk, and only on a press.</summary>
    [RelayCommand]
    public async Task DownloadAsync()
    {
        if (_manager is null || _pending is null) return;

        Stage = UpdateStage.Downloading;
        Headline = Loc.F(Strings.Update_Downloading, AvailableVersion);
        Detail = "";

        try
        {
            await _manager.DownloadUpdatesAsync(_pending, progress =>
            {
                Detail = Loc.F(Strings.Unit_Percent, progress);
            });

            Stage = UpdateStage.Ready;
            Headline = Loc.F(Strings.Update_Ready, AvailableVersion);
            Detail = Strings.Update_Ready_Detail;
        }
        catch (Exception ex)
        {
            Stage = UpdateStage.Failed;
            Headline = Strings.Update_DownloadFailed;
            Detail = ex.Message;
            _log(LogLevel.Error, Loc.F(Strings.Log_UpdateDownloadFailed, ex.Message));
        }
    }

    /// <summary>«Перезапустить». Applies what was downloaded and comes back up.</summary>
    [RelayCommand]
    public void Apply()
    {
        if (_manager is null || _pending is null || Stage != UpdateStage.Ready) return;

        _log(LogLevel.Info, Loc.F(Strings.Log_UpdateApplying, AvailableVersion));
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
        ? Loc.F(Strings.Update_Summary, CurrentVersion)
        : Strings.Update_Summary_Portable;

    /// <summary>
    /// The language changed. <see cref="Summary"/> is computed, so it only needs telling;
    /// a headline that is on screen is left alone on purpose — it describes something that
    /// happened at a moment, and rewriting history is worse than a mixed-language line that
    /// the next check replaces anyway.
    /// </summary>
    public void Retranslate() => OnPropertyChanged(nameof(Summary));
}
