using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using HexBridge.Localization;

namespace HexBridge.App.ViewModels;

/// <summary>
/// The first question a fresh install asks, and the one switch in the settings that can
/// answer it again later: what does this computer do.
///
/// <para>
/// It is asked before pairing, not after, because the answer decides who generates the key.
/// The machine that listens is the one that knows its own address, so it is the one that
/// makes the code; the machine that dials reads a code off the other one's screen. Asking
/// in the other order would mean showing somebody a QR code and then taking it away.
/// </para>
///
/// <para>
/// The two answers are named for what the machine does with a microphone, never for what it
/// does with packets. «Отправитель» is true and useless: nobody stands in front of two
/// computers wondering which one sends datagrams.
/// </para>
/// </summary>
public sealed partial class RoleViewModel : ObservableObject
{
    private readonly Func<BridgeRole, Task> _commit;

    /// <summary>True the first time round, when there is nothing to go back to.</summary>
    [ObservableProperty] private bool _isFirstRun;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private BridgeRole _selected = BridgeRole.Receiver;

    /// <summary>The role that is actually in force, as opposed to the one under the cursor.</summary>
    [ObservableProperty] private BridgeRole _current = BridgeRole.Receiver;

    public RoleViewModel(Func<BridgeRole, Task> commit) => _commit = commit;

    public bool ReceiverPicked => Selected == BridgeRole.Receiver;
    public bool SenderPicked => Selected == BridgeRole.Sender;

    /// <summary>True when confirming would actually change something.</summary>
    public bool WouldChange => Selected != Current;

    public string Title => IsFirstRun ? Strings.Role_Title_FirstRun : Strings.Role_Title_Change;

    public string Footnote => Selected == BridgeRole.Sender
        ? Strings.Role_Footnote_Sender
        : Strings.Role_Footnote_Receiver;

    /// <summary>What pressing the primary button will do, in the words of the choice made.</summary>
    public string ConfirmLabel => IsFirstRun
        ? Strings.Role_Confirm_Continue
        : WouldChange ? Strings.Role_Confirm_Switch : Strings.Role_Confirm_Keep;

    public string ReceiverTitle => RoleWording.Title(BridgeRole.Receiver);
    public string ReceiverSummary => RoleWording.Summary(BridgeRole.Receiver);
    public string SenderTitle => RoleWording.Title(BridgeRole.Sender);
    public string SenderSummary => RoleWording.Summary(BridgeRole.Sender);

    // The one honest sentence about what the giving role cannot do — Role_Sender_Caveat —
    // is bound straight from the two views that show it. It used to live here as a constant
    // because XAML had no other way to reach a shared string; the localizer is that way now.

    /// <summary>Opens the question. <paramref name="firstRun"/> hides the way out of it.</summary>
    public void Open(BridgeRole current, bool firstRun)
    {
        Current = current;
        Selected = current;
        IsFirstRun = firstRun;
        IsOpen = true;
    }

    [RelayCommand]
    private void PickReceiver() => Selected = BridgeRole.Receiver;

    [RelayCommand]
    private void PickSender() => Selected = BridgeRole.Sender;

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        var chosen = Selected;
        IsOpen = false;
        Current = chosen;
        await _commit(chosen);
    }

    /// <summary>Only reachable when a role is already in force; a first run has no «отмена».</summary>
    [RelayCommand]
    private void Cancel()
    {
        Selected = Current;
        IsOpen = false;
    }

    partial void OnSelectedChanged(BridgeRole value)
    {
        _ = value;
        OnPropertyChanged(nameof(ReceiverPicked));
        OnPropertyChanged(nameof(SenderPicked));
        OnPropertyChanged(nameof(WouldChange));
        OnPropertyChanged(nameof(Footnote));
        OnPropertyChanged(nameof(ConfirmLabel));
    }

    partial void OnCurrentChanged(BridgeRole value)
    {
        _ = value;
        OnPropertyChanged(nameof(WouldChange));
        OnPropertyChanged(nameof(ConfirmLabel));
    }

    partial void OnIsFirstRunChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ConfirmLabel));
    }

    /// <summary>The language changed; every string here is computed, so this is the whole of it.</summary>
    public void Retranslate()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Footnote));
        OnPropertyChanged(nameof(ConfirmLabel));
        OnPropertyChanged(nameof(ReceiverTitle));
        OnPropertyChanged(nameof(ReceiverSummary));
        OnPropertyChanged(nameof(SenderTitle));
        OnPropertyChanged(nameof(SenderSummary));
    }
}
