using CommunityToolkit.Mvvm.ComponentModel;

namespace HexBridge.App.ViewModels;

/// <summary>
/// The five states every feature is described by, from DESIGN.md §7.0. Both features use
/// the same automaton on purpose: the user learns the rules once, and «Ждёт Windows» and
/// «Ждёт контроллер» mean the same kind of thing.
/// </summary>
public enum UiStatus
{
    /// <summary>Switched off deliberately. Not a problem, and never painted as one.</summary>
    Off,

    /// <summary>Coming up. Ten seconds at most, then it is an error.</summary>
    Starting,

    /// <summary>Our side is ready and the other side is not.</summary>
    Waiting,

    /// <summary>Working.</summary>
    Live,

    /// <summary>Stopped, and the user has to do something about it.</summary>
    Error,
}

/// <summary>
/// What every page has in common: one state, one sentence about it, and one line of
/// detail underneath. §7.0's «rule of one line» is enforced here rather than in each
/// view — there is exactly one place to put the headline, so there can only be one.
/// </summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty] private UiStatus _status = UiStatus.Off;

    /// <summary>The sentence that answers «is it working».</summary>
    [ObservableProperty] private string _headline = "";

    /// <summary>One line of why, or of what to do next.</summary>
    [ObservableProperty] private string _subline = "";

    /// <summary>Two or three words for the side navigation.</summary>
    [ObservableProperty] private string _navState = "";

    // The status card and the dots pick their colours through style classes rather than
    // through a bound brush, which is what keeps both themes correct with no work here.
    public bool IsOk => Status is UiStatus.Live;
    public bool IsWarn => Status is UiStatus.Waiting;
    public bool IsBad => Status is UiStatus.Error;
    public bool IsOff => Status is UiStatus.Off;

    /// <summary>Drives the one looped animation in the app (§5.4 transition 9).</summary>
    public bool IsPending => Status is UiStatus.Waiting or UiStatus.Starting;

    protected void Describe(UiStatus status, string headline, string subline, string navState)
    {
        Status = status;
        Headline = headline;
        Subline = subline;
        NavState = navState;
    }

    partial void OnStatusChanged(UiStatus value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsOk));
        OnPropertyChanged(nameof(IsWarn));
        OnPropertyChanged(nameof(IsBad));
        OnPropertyChanged(nameof(IsOff));
        OnPropertyChanged(nameof(IsPending));
    }

    /// <summary>«2 ч 05 м», «3 м 12 с», «41 с» — never a bare number of seconds past a minute.</summary>
    protected static string Duration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours} ч {span.Minutes:00} м"
        : span.TotalMinutes >= 1
            ? $"{span.Minutes} м {span.Seconds:00} с"
            : $"{span.Seconds} с";
}
