using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HexBridge.Files;

using HexBridge.Localization;

namespace HexBridge.App.ViewModels;

/// <summary>One file that has landed on this machine, as the list shows it.</summary>
public sealed record ArrivedRow(string Name, string SizeText, string TimeText);

/// <summary>
/// The files page. One thing to do — put a file in — and one thing to read: what came out
/// the other end. The drop target is the page; the button is there for the people who do
/// not drag.
/// </summary>
public sealed partial class FilesViewModel : ObservableObject
{
    private FilesFeature? _feature;

    /// <summary>What the arrivals list was last built from, so a tick that changed nothing rebuilds nothing.</summary>
    private (int Count, DateTime Newest) _shown;

    [ObservableProperty] private string _headline = Strings.Files_Headline_Off;
    [ObservableProperty] private string _subline = Strings.Files_Sub_Off;

    [ObservableProperty] private bool _isGood;
    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private bool _isBad;

    /// <summary>The drop target is only real while the feature is running.</summary>
    [ObservableProperty] private bool _isOn;

    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private string _sentText = "0";
    [ObservableProperty] private string _receivedText = "0";

    [ObservableProperty] private bool _isTransferring;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _transferText = "";

    /// <summary>
    /// Why the last file did not go. Cleared by the next attempt: a refusal that stays on
    /// screen after the thing it refused has been dealt with is a refusal nobody trusts.
    /// </summary>
    [ObservableProperty] private string? _error;

    public ObservableCollection<ArrivedRow> Arrived { get; } = [];

    public bool HasArrived => Arrived.Count > 0;

    /// <summary>
    /// The ceiling, said in the round number rather than the exact one. A file's limit is
    /// what the format's own <c>u32</c> can count — one byte short of four gigabytes — and
    /// «up to 4 GiB less one byte» is a sentence written for the format, not for a person.
    /// </summary>
    public string DropHint => Loc.F(Strings.Files_Drop_Hint, Loc.F(Strings.Unit_Gibibytes, 4));

    /// <summary>The feature this page drives. Set once, at startup, by the catalog.</summary>
    public void Attach(FilesFeature feature) => _feature = feature;

    /// <summary>
    /// Sends one file. Reading it is somebody else's thread: a 60 MB file off a slow disk
    /// would otherwise freeze the window for as long as it took.
    /// </summary>
    public void Send(string path)
    {
        var feature = _feature;
        if (feature is null) return;

        Error = null;
        Task.Run(() =>
        {
            var error = feature.Send(path);
            Dispatcher.UIThread.Post(() => Error = error);
        });
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Folder.Length == 0) return;

        try
        {
            Process.Start(new ProcessStartInfo(Folder) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No shell, or it refused. The folder is written out on the page next to the
            // button for exactly this, so there is nothing to report.
        }
    }

    public void Apply(FilesState? state)
    {
        if (state is null)
        {
            Headline = Strings.Files_Headline_Off;
            Subline = Strings.Files_Sub_Off;
            IsGood = IsWaiting = IsBad = IsOn = IsTransferring = false;
            SentText = ReceivedText = Loc.Count(0);
            Rebuild([]);
            return;
        }

        Headline = state.Headline;
        Subline = state.Detail ?? "";
        IsGood = state.Status is FeatureStatus.Live;
        IsWaiting = state.Status is FeatureStatus.Waiting or FeatureStatus.Warning;
        IsBad = state.Status is FeatureStatus.Failed;
        IsOn = state.Status is not (FeatureStatus.Disabled or FeatureStatus.Stopped);

        Folder = state.Folder;
        SentText = Loc.Count(state.Sent);
        ReceivedText = Loc.Count(state.Received);

        IsTransferring = state.TransferDescription is not null;
        Progress = state.Progress;
        TransferText = state.TransferDescription is null
            ? ""
            : Loc.F(
                state.TransferDirection == BulkDirection.Outgoing
                    ? Strings.Files_Transfer_Out
                    : Strings.Files_Transfer_In,
                state.TransferDescription);

        Rebuild(state.Arrived);
    }

    private void Rebuild(IReadOnlyList<ArrivedFile> arrived)
    {
        var stamp = (arrived.Count, arrived.Count == 0 ? default : arrived[0].At);
        if (stamp == _shown) return;
        _shown = stamp;

        Arrived.Clear();
        foreach (var file in arrived)
        {
            Arrived.Add(new ArrivedRow(
                file.Name,
                Loc.Size(file.Bytes),
                file.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)));
        }
        OnPropertyChanged(nameof(HasArrived));
    }
}
