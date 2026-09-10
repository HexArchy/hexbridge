using CommunityToolkit.Mvvm.ComponentModel;
using HexBridge.Microphone;

using HexBridge.Localization;

namespace HexBridge.App.ViewModels;

/// <summary>Everything the Status page shows, refreshed from a snapshot ten times a second.</summary>
public sealed partial class StatusViewModel : ObservableObject
{
    /// <summary>Bottom of the meter scale. Speech peaks live between -30 and -6 dBFS.</summary>
    public const double MeterFloorDb = -60;

    [ObservableProperty] private string _headline = Strings.Status_Headline_StoppedReceiving;
    [ObservableProperty] private string _subline = Strings.Status_Sub_StoppedReceiving;

    /// <summary>
    /// This machine is the one holding the microphone. The page is the same page either
    /// way — one meter, one connection card — but three of its labels are about a direction
    /// and would be backwards without this.
    /// </summary>
    [ObservableProperty] private bool _isGiving;

    [ObservableProperty] private string _endpointLabel = Strings.Status_Label_Endpoint_Receiving;
    [ObservableProperty] private string _peerLabel = Strings.Status_Label_Peer_Receiving;

    // Three mutually exclusive flags rather than a brush: the view picks colours through
    // style classes, so the card follows the light/dark theme without any work here.
    [ObservableProperty] private bool _isGood;
    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private bool _isBad;

    [ObservableProperty] private double _level;      // 0..1, mapped from dBFS
    [ObservableProperty] private double _peakMark;   // 0..1, short-lived hold marker
    [ObservableProperty] private string _peakText = Strings.Common_Empty;

    /// <summary>
    /// The microphone is muted — ours, or the far end's. The meter keeps moving, because
    /// the audio is still being read, but it drops the colour scale: «горячо» and «тихо»
    /// are not questions worth answering while nobody is hearing any of it.
    /// </summary>
    [ObservableProperty] private bool _isMuted;

    // Packets, latency, uptime and buffer depth live on the «Качество» page:
    // this one answers whether the sound is arriving, that one how well.
    // The session id, the listen address and the relay left the window
    // altogether — the first is a protocol number nobody can act on, and the
    // other two are settings, shown where they are set.
    [ObservableProperty] private string _senderText = Strings.Status_Peer_NotConnected;
    [ObservableProperty] private string _peerText = Strings.Common_Empty;
    [ObservableProperty] private string _outputText = Strings.Common_Empty;

    [ObservableProperty] private string? _gameHint;
    [ObservableProperty] private bool _hasGameHint;

    /// <summary>
    /// The hint with the device name already in it. Built here rather than by a StringFormat
    /// in the view: the sentence around the name is a translated string, and a format string
    /// baked into XAML is one the language cannot reach.
    /// </summary>
    [ObservableProperty] private string _gameHintText = "";

    // Peak hold decays instead of snapping back, so a transient stays readable.
    private double _holdValue;
    private DateTime _holdSetAt;

    public void Apply(ReceiverSnapshot s, MicrophoneState? m)
    {
        IsGiving = s.Role == BridgeRole.Sender;
        EndpointLabel = IsGiving ? Strings.Status_Label_Endpoint_Sharing : Strings.Status_Label_Endpoint_Receiving;
        PeerLabel = IsGiving ? Strings.Status_Label_Peer_Sharing : Strings.Status_Label_Peer_Receiving;

        (Headline, Subline) = Describe(s, m);
        IsGood = s.Status is ReceiverStatus.Live;
        IsWaiting = s.Status is ReceiverStatus.WaitingForSender or ReceiverStatus.Muted or ReceiverStatus.SenderLost;
        IsBad = s.Status is ReceiverStatus.Failed;

        IsMuted = s.Status is ReceiverStatus.Muted;

        var peakHold = m?.PeakHold ?? 0;
        var db = MicrophoneState.ToDbfs(peakHold);
        var normalised = peakHold > 0 ? Math.Clamp((db - MeterFloorDb) / -MeterFloorDb, 0, 1) : 0;
        Level = normalised;
        PeakText = s.Status is ReceiverStatus.Live or ReceiverStatus.Muted && peakHold > 0
            ? Loc.Dbfs1(db)
            : Strings.Common_Empty;

        var now = DateTime.UtcNow;
        if (normalised >= _holdValue)
        {
            _holdValue = normalised;
            _holdSetAt = now;
        }
        else if (now - _holdSetAt > TimeSpan.FromSeconds(1))
        {
            _holdValue = Math.Max(normalised, _holdValue - 0.02);
        }
        PeakMark = _holdValue;

        // A machine names itself in its first HELLO, so «имя неизвестно» is only ever true of
        // one that has been heard from. One that has not is a different fact and a different
        // thing to do about it.
        SenderText = s.SenderName is { Length: > 0 } name ? name
            : s.PeerAddress is null ? Strings.Status_Peer_NotConnected
            : s.LastPacketAt is null ? Strings.Status_Peer_Silent
            : Strings.Status_Peer_Unnamed;
        PeerText = s.PeerAddress ?? Strings.Common_Empty;
        OutputText = string.IsNullOrEmpty(m?.OutputDescription) ? Strings.Common_Empty : m.OutputDescription;

        // The hint is about picking a microphone in a game, which is a thing to do on the
        // machine the games are on. Here that is the machine that receives.
        GameHint = m?.PairedCaptureName;
        HasGameHint = !IsGiving && m?.PairedCaptureName is not null;
        GameHintText = GameHint is null ? "" : Loc.F(Strings.Status_GameHint, GameHint);
    }

    /// <summary>
    /// The same six states in the words of whichever end this is. Every line names the
    /// other machine by what it does rather than by what it sends — «отправитель» is a fact
    /// about datagrams, and nobody is standing here thinking about datagrams.
    /// </summary>
    private static (string, string) Describe(ReceiverSnapshot s, MicrophoneState? m)
    {
        var giving = s.Role == BridgeRole.Sender;
        var endpoint = m?.DeviceName ?? m?.OutputDescription
            ?? (giving ? Strings.Status_Endpoint_Microphone : Strings.Status_Endpoint_Output);

        return s.Status switch
        {
            ReceiverStatus.Live => (Strings.Status_Headline_Live, giving
                ? Loc.F(Strings.Common_Arrow, endpoint, Who(s))
                : Loc.F(Strings.Common_Arrow, Who(s), endpoint)),
            ReceiverStatus.Muted => (Strings.Status_Headline_Muted, giving
                ? Strings.Status_Sub_Muted_Sharing
                : Loc.F(Strings.Status_Sub_Muted_Receiving, Who(s))),
            ReceiverStatus.SenderLost => (Strings.Status_Headline_Lost,
                s.LastPacketAt is { } at
                    ? Loc.F(Strings.Status_Sub_Lost_For, Loc.Duration(DateTime.UtcNow - at))
                    : Strings.Status_Sub_Lost),
            ReceiverStatus.WaitingForSender => (Strings.Status_Headline_Waiting, giving
                ? Loc.F(Strings.Status_Sub_Waiting_Sharing, s.PeerAddress ?? Strings.Status_Peer_Fallback)
                : Loc.F(Strings.Status_Sub_Waiting_Receiving, s.Listen)),
            ReceiverStatus.Failed => (Strings.Status_Headline_Failed, s.Detail ?? Strings.Status_Sub_Failed),
            _ => giving
                ? (Strings.Status_Headline_StoppedSharing, Strings.Status_Sub_StoppedSharing)
                : (Strings.Status_Headline_StoppedReceiving, Strings.Status_Sub_StoppedReceiving),
        };
    }

    /// <summary>
    /// The machine at the other end, by the name it gave in its HELLO. Each end names the
    /// other by what it is rather than by what the protocol calls it.
    /// </summary>
    private static string Who(ReceiverSnapshot s) =>
        string.IsNullOrEmpty(s.SenderName) ? s.PeerAddress ?? Strings.Status_Peer_Fallback : s.SenderName;
}
