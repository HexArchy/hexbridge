using CommunityToolkit.Mvvm.ComponentModel;
using HexBridge.Clipboard;

namespace HexBridge.App.ViewModels;

/// <summary>
/// The clipboard page. Three things in the order they matter: whether it is on at all, what
/// leaves this machine when it is, and what crossed last.
/// </summary>
public sealed partial class ClipboardViewModel : ObservableObject
{
    [ObservableProperty] private string _headline = "Общий буфер выключен";
    [ObservableProperty] private string _subline = "Включите его в настройках";

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
            Headline = "Общий буфер выключен";
            Subline = "Включите его в настройках";
            IsGood = IsWaiting = IsBad = IsOn = IsTransferring = false;
            LastText = LastDirectionText = LastWhenText = "—";
            SentText = ReceivedText = "0";
            return;
        }

        Headline = state.Headline;
        Subline = state.Detail ?? "";
        IsGood = state.Status is FeatureStatus.Live;
        IsWaiting = state.Status is FeatureStatus.Waiting or FeatureStatus.Warning;
        IsBad = state.Status is FeatureStatus.Failed;
        IsOn = state.Status is not (FeatureStatus.Disabled or FeatureStatus.Stopped);

        SentText = state.Sent.ToString("N0");
        ReceivedText = state.Received.ToString("N0");

        LastText = state.LastDescription ?? "—";
        LastDirectionText = state.LastDirection switch
        {
            BulkDirection.Outgoing => "отсюда на вторую машину",
            BulkDirection.Incoming => "со второй машины сюда",
            _ => "—",
        };
        LastWhenText = state.LastAt is { } at ? When(DateTime.UtcNow - at) : "—";

        IsTransferring = state.TransferDescription is not null;
        Progress = state.Progress;
        TransferText = state.TransferDescription is null
            ? ""
            : state.TransferDirection == BulkDirection.Outgoing
                ? $"Отправляем: {state.TransferDescription}"
                : $"Принимаем: {state.TransferDescription}";
    }

    private static string When(TimeSpan ago) => ago < TimeSpan.FromSeconds(10) ? "только что"
        : ago < TimeSpan.FromMinutes(1) ? $"{(int)ago.TotalSeconds} с назад"
        : ago < TimeSpan.FromHours(1) ? $"{(int)ago.TotalMinutes} мин назад"
        : $"{(int)ago.TotalHours} ч назад";
}
