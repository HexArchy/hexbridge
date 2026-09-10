using CommunityToolkit.Mvvm.ComponentModel;
using HexBridge.Clipboard;

using HexBridge.Localization;

namespace HexBridge.App.ViewModels;

/// <summary>
/// The clipboard page. Three things in the order they matter: whether it is on at all, what
/// leaves this machine when it is, and what crossed last.
/// </summary>
public sealed partial class ClipboardViewModel : ObservableObject
{
    [ObservableProperty] private string _headline = Strings.Clipboard_Headline_Off;
    [ObservableProperty] private string _subline = Strings.Clipboard_Sub_Off;

    [ObservableProperty] private bool _isGood;
    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private bool _isBad;

    /// <summary>The privacy notice is shown only while the feature is actually on.</summary>
    [ObservableProperty] private bool _isOn;

    [ObservableProperty] private string _lastText = "—";
    [ObservableProperty] private string _lastDirectionText = "—";
    [ObservableProperty] private string _lastWhenText = "—";
    [ObservableProperty] private string _sentText = "0";
    [ObservableProperty] private string _receivedText = "0";

    // In-flight object. A progress bar is the one thing a 300 KB screenshot needs and a
    // line of text does not, so it appears only while something is actually moving.
    [ObservableProperty] private bool _isTransferring;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _transferText = "";

    public void Apply(ClipboardState? state)
    {
        if (state is null)
        {
            Headline = Strings.Clipboard_Headline_Off;
            Subline = Strings.Clipboard_Sub_Off;
            IsGood = IsWaiting = IsBad = IsOn = IsTransferring = false;
            LastText = LastDirectionText = LastWhenText = Strings.Common_Empty;
            SentText = ReceivedText = Loc.Count(0);
            return;
        }

        Headline = state.Headline;
        Subline = state.Detail ?? "";
        IsGood = state.Status is FeatureStatus.Live;
        IsWaiting = state.Status is FeatureStatus.Waiting or FeatureStatus.Warning;
        IsBad = state.Status is FeatureStatus.Failed;
        IsOn = state.Status is not (FeatureStatus.Disabled or FeatureStatus.Stopped);

        SentText = Loc.Count(state.Sent);
        ReceivedText = Loc.Count(state.Received);

        LastText = state.LastDescription ?? Strings.Common_Empty;
        LastDirectionText = state.LastDirection switch
        {
            BulkDirection.Outgoing => Strings.Clipboard_Direction_Out,
            BulkDirection.Incoming => Strings.Clipboard_Direction_In,
            _ => Strings.Common_Empty,
        };
        LastWhenText = state.LastAt is { } at ? When(DateTime.UtcNow - at) : Strings.Common_Empty;

        IsTransferring = state.TransferDescription is not null;
        Progress = state.Progress;
        TransferText = state.TransferDescription is null
            ? ""
            : Loc.F(
                state.TransferDirection == BulkDirection.Outgoing
                    ? Strings.Clipboard_Transfer_Out
                    : Strings.Clipboard_Transfer_In,
                state.TransferDescription);
    }

    private static string When(TimeSpan ago) => ago < TimeSpan.FromSeconds(10) ? Strings.Clipboard_When_JustNow
        : ago < TimeSpan.FromMinutes(1) ? Loc.F(Strings.Clipboard_When_Seconds, (int)ago.TotalSeconds)
        : ago < TimeSpan.FromHours(1) ? Loc.F(Strings.Clipboard_When_Minutes, (int)ago.TotalMinutes)
        : Loc.F(Strings.Clipboard_When_Hours, (int)ago.TotalHours);
}
